using Shared;
using System;
using System.Drawing;
using System.Threading.Tasks;
using PlayerAgents.Map;

public sealed class ArcherAI : BaseAI
{
    private DateTime _nextRangeAttackTime = DateTime.MinValue;
    private Point? _cachedShootSpot;
    private DateTime _nextShootSpotCheck = DateTime.MinValue;

    public ArcherAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.10;
    protected override double MpPotionWeightFraction => 0.50;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinMC, Stat.MaxMC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };

    private bool HasBowEquipped()
    {
        var eq = Client.Equipment;
        if (eq == null || eq.Count <= (int)EquipmentSlot.Weapon) return false;
        var weapon = eq[(int)EquipmentSlot.Weapon];
        return weapon != null && weapon.Info != null &&
               weapon.Info.Type == ItemType.Weapon &&
               weapon.Info.RequiredClass.HasFlag(RequiredClass.Archer);
    }


    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (!HasBowEquipped())
        {
            await base.AttackMonsterAsync(monster, current);
            return;
        }

        const int attackRange = 7;
        const int retreatRange = 3;
        int dist = Functions.MaxDistance(current, monster.Location);
        var map = Client.CurrentMap;

        if (map != null && dist <= retreatRange)
        {
            var safe = GetSafestPoint(map, monster, attackRange);
            if (safe != current)
            {
                var path = await MovementHelper.FindPathAsync(Client, map, current, safe, monster.Id, 0);
                if (path.Count > 0)
                {
                    await MovementHelper.MoveAlongPathAsync(Client, path, safe);
                    RecordAttackTime();
                    return;
                }
            }
        }

        if (map != null && dist <= attackRange && CanShoot(map, current, monster.Location))
        {
            var dir = Functions.DirectionFromPoint(current, monster.Location);
            await Client.RangeAttackAsync(dir, monster.Location, monster.Id);
            RecordAttackTime();
            _nextRangeAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
        }
        else if (map != null)
        {
            // Out of range or line of sight blocked; reposition
            await MoveToTargetAsync(map, current, monster);
        }
        else
        {
            await base.AttackMonsterAsync(monster, current);
        }
    }

    protected override async Task<bool> MoveToTargetAsync(MapData map, Point current, TrackedObject target, int radius = 1)
    {
        if (!HasBowEquipped())
            return await base.MoveToTargetAsync(map, current, target, radius);

        const int attackRange = 7;
        const int retreatRange = 3;

        int dist = Functions.MaxDistance(current, target.Location);
        bool canShoot = dist <= attackRange && CanShoot(map, current, target.Location);

        if (!canShoot)
        {
            var spot = GetNearestShootSpot(map, target, current, attackRange, retreatRange);
            if (spot == current && dist > attackRange)
            {
                var path = await MovementHelper.FindPathAsync(Client, map, current, target.Location, target.Id, attackRange);
                if (path.Count > 0)
                    return await MovementHelper.MoveAlongPathAsync(Client, path, target.Location);
            }
            else if (spot != current)
            {
                var path = await MovementHelper.FindPathAsync(Client, map, current, spot, target.Id, 0);
                if (path.Count > 0)
                    return await MovementHelper.MoveAlongPathAsync(Client, path, spot);
            }

            return true;
        }

        if (DateTime.UtcNow >= _nextRangeAttackTime)
        {
            var dir = Functions.DirectionFromPoint(current, target.Location);
            await Client.RangeAttackAsync(dir, target.Location, target.Id);
            RecordAttackTime();
            _nextRangeAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
        }

        if (dist <= retreatRange)
        {
            var safe = GetSafestPoint(map, target, attackRange);
            if (safe != current)
            {
                var path = await MovementHelper.FindPathAsync(Client, map, current, safe, target.Id, 0);
                if (path.Count > 0)
                    return await MovementHelper.MoveAlongPathAsync(Client, path, safe);
            }
        }

        return true;
    }

    private Point GetSafestPoint(MapData map, TrackedObject target, int range)
    {
        var obstacles = MovementHelper.BuildObstacles(Client, target.Id, 0);
        Point best = Client.CurrentLocation;
        int bestScore = int.MinValue;

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                var p = new Point(target.Location.X + dx, target.Location.Y + dy);
                int dist = Functions.MaxDistance(p, target.Location);
                if (dist < range - 1 || dist > range) continue;
                if (!map.IsWalkable(p.X, p.Y)) continue;
                if (obstacles.Contains(p)) continue;
                if (!CanShoot(map, p, target.Location)) continue;

                int min = int.MaxValue;
                foreach (var obj in Client.TrackedObjects.Values)
                {
                    if (obj.Type != ObjectType.Monster || obj.Dead || obj.Id == target.Id) continue;
                    int d = Functions.MaxDistance(p, obj.Location);
                    if (d < min) min = d;
                }
                int score = min - dist; // prefer farther from others, near range boundary
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
        }

        return best;
    }

    private Point GetNearestShootSpot(MapData map, TrackedObject target, Point current, int attackRange, int safeDist)
    {
        if (DateTime.UtcNow < _nextShootSpotCheck && _cachedShootSpot.HasValue)
        {
            var spot = _cachedShootSpot.Value;
            int d = Functions.MaxDistance(spot, target.Location);
            if (d >= safeDist && d <= attackRange && map.IsWalkable(spot.X, spot.Y) && CanShoot(map, spot, target.Location))
                return spot;
        }

        var obstacles = MovementHelper.BuildObstacles(Client, target.Id, 0);
        var dirs = new[] { new Point(0,-1), new Point(1,0), new Point(0,1), new Point(-1,0), new Point(1,-1), new Point(1,1), new Point(-1,1), new Point(-1,-1) };
        var queue = new Queue<Point>();
        var visited = new HashSet<Point> { current };
        queue.Enqueue(current);

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            int dist = Functions.MaxDistance(p, target.Location);
            if (p != current && dist >= safeDist && dist <= attackRange && CanShoot(map, p, target.Location))
            {
                _cachedShootSpot = p;
                _nextShootSpotCheck = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                return p;
            }

            foreach (var d in dirs)
            {
                var n = new Point(p.X + d.X, p.Y + d.Y);
                if (!map.IsWalkable(n.X, n.Y)) continue;
                if (obstacles.Contains(n)) continue;
                if (Functions.MaxDistance(n, current) > attackRange) continue;
                if (visited.Add(n))
                    queue.Enqueue(n);
            }
        }

        _cachedShootSpot = current;
        _nextShootSpotCheck = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        return current;
    }

    private static bool CanShoot(MapData map, Point from, Point to)
    {
        Point location = from;

        while (location != to)
        {
            MirDirection dir = Functions.DirectionFromPoint(location, to);
            location = Functions.PointMove(location, dir, 1);

            if (location.X < 0 || location.Y < 0 ||
                location.X >= map.Width || location.Y >= map.Height)
                return false;

            if (!map.IsWalkable(location.X, location.Y))
                return false;
        }

        return true;
    }
}
