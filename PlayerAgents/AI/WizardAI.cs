using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using PlayerAgents.Map;

public sealed class WizardAI : BaseAI
{
    private DateTime _nextSpellTime = DateTime.MinValue;
    private Point? _cachedCastSpot;
    private DateTime _nextCastSpotCheck = DateTime.MinValue;

    protected override bool ShouldIgnoreDistantTarget(TrackedObject target, int distance)
    {
        if (target.AI == 3)
            return distance > 20;

        var magic = GetBestMagic();
        bool longRange = magic != null &&
                         magic.Range > 0 &&
                         !CanFlySpells.List.Contains(magic.Spell);
        return distance > 20 && !longRange;
    }

    public WizardAI(GameClient client) : base(client) { }

    protected override double HpPotionWeightFraction => 0.10;
    protected override double MpPotionWeightFraction => 0.50;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinMC, Stat.MaxMC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };

    private ClientMagic? GetBestMagic()
    {
        ClientMagic? best = null;
        int mp = Client.MP;
        foreach (var magic in Client.Magics)
        {
            if (Array.IndexOf(Globals.RangedSpells, magic.Spell) < 0) continue;
            int cost = magic.BaseCost + magic.LevelCost * magic.Level;
            if (cost > mp) continue;
            if (best == null || cost > best.BaseCost + best.LevelCost * best.Level)
                best = magic;
        }
        return best;
    }

    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (monster.Dead || monster.Hidden) return;

        var map = Client.CurrentMap;

        if (monster.AI == 3)
        {
            await base.AttackMonsterAsync(monster, current);
            return;
        }

        var magic = GetBestMagic();
        if (map == null || magic == null)
        {
            await base.AttackMonsterAsync(monster, current);
            return;
        }

        int attackRange = magic.Range > 0 ? magic.Range : 7;
        bool requiresLine = CanFlySpells.List.Contains(magic.Spell);

        if (Functions.MaxDistance(current, monster.Location) <= attackRange &&
            (!requiresLine || CanCast(map, current, monster.Location)) &&
            DateTime.UtcNow >= _nextSpellTime)
        {
            var dir = Functions.DirectionFromPoint(current, monster.Location);
            await Client.CastMagicAsync(magic.Spell, dir, monster.Location, monster.Id);
            RecordAttackTime();
            _nextSpellTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
        }
        else
        {
            await base.AttackMonsterAsync(monster, current);
        }
    }

    protected override async Task<bool> MoveToTargetAsync(MapData map, Point current, TrackedObject target, int radius = 1)
    {
        if (target.Type != ObjectType.Monster)
            return await base.MoveToTargetAsync(map, current, target, radius);
        if (target.Dead || target.Hidden)
            return true;
        if (target.AI == 3)
            return await base.MoveToTargetAsync(map, current, target, radius);

        var magic = GetBestMagic();
        if (magic == null)
            return await base.MoveToTargetAsync(map, current, target, radius);

        int attackRange = magic.Range > 0 ? magic.Range : 7;
        const int retreatRange = 3;
        int dist = Functions.MaxDistance(current, target.Location);
        bool requiresLine = CanFlySpells.List.Contains(magic.Spell);
        bool canCast = dist <= attackRange && (!requiresLine || CanCast(map, current, target.Location));

        if (!canCast)
        {
            var spot = requiresLine ? GetNearestCastSpot(map, target, current, attackRange, retreatRange)
                                     : GetSafestPoint(map, current, target, attackRange);
            if (spot == current)
            {
                if (dist > attackRange)
                {
                    var path = await FindBufferedPathAsync(map, current, target.Location, 3);
                    if (path.Count > 0)
                    {
                        bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, path[^1]);
                        if (!moved)
                        {
                            _cachedCastSpot = null;
                            _nextCastSpotCheck = DateTime.MinValue;
                        }
                        return moved;
                    }
                }
                else
                {
                    var safe = GetRetreatPoint(map, current, target, attackRange, retreatRange, requiresLine);
                    if (safe != current)
                    {
                        var path = await FindBufferedPathAsync(map, current, safe, 0);
                        if (path.Count > 0)
                        {
                            bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, safe);
                            if (!moved)
                            {
                                _cachedCastSpot = null;
                                _nextCastSpotCheck = DateTime.MinValue;
                            }
                            return moved;
                        }
                    }
                }
            }
            else
            {
                var path = await FindBufferedPathAsync(map, current, spot, 0);
                if (path.Count > 0)
                {
                    bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, spot);
                    if (!moved)
                    {
                        _cachedCastSpot = null;
                        _nextCastSpotCheck = DateTime.MinValue;
                    }
                    return moved;
                }
            }
            return true;
        }

        if (dist <= retreatRange)
        {
            var safe = GetRetreatPoint(map, current, target, attackRange, retreatRange, requiresLine);
            if (safe != current)
            {
                var path = await FindBufferedPathAsync(map, current, safe, 0);
                if (path.Count > 0)
                {
                    bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, safe);
                    if (!moved)
                    {
                        _cachedCastSpot = null;
                        _nextCastSpotCheck = DateTime.MinValue;
                    }
                    return moved;
                }
            }
            return true;
        }

        return false;
    }

    private Point GetSafestPoint(MapData map, Point origin, TrackedObject target, int range)
    {
        var obstacles = BuildObstacles(map);
        Point best = origin;
        int bestScore = int.MinValue;

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                var p = new Point(origin.X + dx, origin.Y + dy);
                int dist = Functions.MaxDistance(p, origin);
                if (dist < range - 1 || dist > range) continue;
                if (!map.IsWalkable(p.X, p.Y)) continue;
                if (obstacles.Contains(p)) continue;
                if (!CanCast(map, p, target.Location)) continue;

                int min = 6;
                foreach (var obj in Client.TrackedObjects.Values)
                {
                    if (obj.Type != ObjectType.Monster || obj.Dead || obj.Id == target.Id) continue;
                    int d = Functions.MaxDistance(p, obj.Location);
                    if (d <= 6 && d < min) min = d;
                }
                int score = min - dist;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
        }
        return best;
    }

    private Point GetRetreatPoint(MapData map, Point origin, TrackedObject target, int attackRange, int retreatRange, bool requiresLine)
    {
        var obstacles = BuildObstacles(map);
        var dir = Functions.DirectionFromPoint(target.Location, origin);
        var p = origin;
        for (int i = 1; i <= attackRange; i++)
        {
            p = Functions.PointMove(origin, dir, i);
            if (!map.IsWalkable(p.X, p.Y)) break;
            if (obstacles.Contains(p)) break;
            if (Functions.MaxDistance(p, target.Location) < retreatRange) continue;
            if (!requiresLine || CanCast(map, p, target.Location))
                return p;
        }
        return GetSafestPoint(map, origin, target, attackRange);
    }

    private Point GetNearestCastSpot(MapData map, TrackedObject target, Point current, int attackRange, int safeDist)
    {
        if (DateTime.UtcNow < _nextCastSpotCheck && _cachedCastSpot.HasValue)
        {
            var spot = _cachedCastSpot.Value;
            int d = Functions.MaxDistance(spot, target.Location);
            var obs = BuildObstacles(map);
            if (d >= safeDist && d <= attackRange &&
                map.IsWalkable(spot.X, spot.Y) &&
                !obs.Contains(spot) &&
                CanCast(map, spot, target.Location))
                return spot;
        }

        var obstacles = BuildObstacles(map);
        var dirs = new[] { new Point(0,-1), new Point(1,0), new Point(0,1), new Point(-1,0), new Point(1,-1), new Point(1,1), new Point(-1,1), new Point(-1,-1) };
        var queue = new Queue<Point>();
        var visited = new HashSet<Point> { current };
        queue.Enqueue(current);

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            int dist = Functions.MaxDistance(p, target.Location);
            if (p != current && dist >= safeDist && dist <= attackRange && CanCast(map, p, target.Location))
            {
                _cachedCastSpot = p;
                _nextCastSpotCheck = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                return p;
            }

            foreach (var d in dirs)
            {
                var n = new Point(p.X + d.X, p.Y + d.Y);
                if (!map.IsWalkable(n.X, n.Y)) continue;
                if (obstacles.Contains(n)) continue;
                if (Functions.MaxDistance(n, current) > attackRange * 3) continue;
                if (visited.Add(n))
                    queue.Enqueue(n);
            }
        }

        _cachedCastSpot = current;
        _nextCastSpotCheck = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        return current;
    }

    private static bool CanCast(MapData map, Point from, Point to)
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

    private HashSet<Point> BuildObstacles(MapData map)
    {
        var obstacles = MovementHelper.BuildObstacles(Client);
        var dirs = new[] { new Point(0,-1), new Point(1,0), new Point(0,1), new Point(-1,0), new Point(1,-1), new Point(1,1), new Point(-1,1), new Point(-1,-1) };
        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type != ObjectType.Monster || obj.Dead) continue;
            obstacles.Add(obj.Location);
            foreach (var d in dirs)
            {
                var p = new Point(obj.Location.X + d.X, obj.Location.Y + d.Y);
                if (map.IsWalkable(p.X, p.Y))
                    obstacles.Add(p);
            }
        }
        return obstacles;
    }

    private async Task<List<Point>> FindBufferedPathAsync(MapData map, Point start, Point dest, int radius)
    {
        var obstacles = BuildObstacles(map);
        var path = await PathFinder.FindPathAsync(map, start, dest, obstacles, radius);
        if (path.Count == 0)
            path = await MovementHelper.FindPathAsync(Client, map, start, dest, 0, radius);
        return path;
    }
}

