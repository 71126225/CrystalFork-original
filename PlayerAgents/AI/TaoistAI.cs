using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using PlayerAgents.Map;

public sealed class TaoistAI : BaseAI
{
    public TaoistAI(GameClient client) : base(client) { }

    private DateTime _nextSpellTime = DateTime.MinValue;
    private readonly Dictionary<uint, DateTime> _poisonedTargets = new();

    private void RecordSpellTime()
    {
        _nextSpellTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
    }

    protected override double HpPotionWeightFraction => 0.20;
    protected override double MpPotionWeightFraction => 0.40;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinDC, Stat.MaxDC, Stat.MinSC, Stat.MaxSC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };

    protected override int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        if (!IsOffensiveSlot(slot))
            return base.GetItemScore(item, slot);

        if (item.Info == null) return 0;

        int score = 0;
        score += item.Info.Stats[Stat.MinDC] + item.AddedStats[Stat.MinDC];
        score += item.Info.Stats[Stat.MaxDC] + item.AddedStats[Stat.MaxDC];
        score += (item.Info.Stats[Stat.MinSC] + item.AddedStats[Stat.MinSC]) * 10;
        score += (item.Info.Stats[Stat.MaxSC] + item.AddedStats[Stat.MaxSC]) * 10;
        return score;
    }

    private bool HasAmulet(int shape = -1)
    {
        var eq = Client.Equipment;
        if (eq == null || eq.Count <= (int)EquipmentSlot.Amulet) return false;
        var item = eq[(int)EquipmentSlot.Amulet];
        if (item?.Info == null || item.Info.Type != ItemType.Amulet) return false;
        if (shape >= 0)
        {
            if (shape == 1)
                return item.Info.Shape == 1 || item.Info.Shape == 2;
            return item.Info.Shape == shape;
        }
        return true;
    }

    private ClientMagic? GetMagic(Spell spell) => Client.Magics.FirstOrDefault(m => m.Spell == spell);

    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (monster.Dead) return;

        var map = Client.CurrentMap;
        var heal = GetMagic(Spell.Healing);
        if (heal != null && DateTime.UtcNow >= _nextSpellTime)
        {
            int maxHP = Client.GetMaxHP();
            if (Client.HP <= maxHP * 4 / 5)
            {
                await Client.CastMagicAsync(Spell.Healing, MirDirection.Up, current, Client.ObjectId);
                RecordSpellTime();
                return;
            }
        }

        var poison = GetMagic(Spell.Poisoning);
        var soulFire = GetMagic(Spell.SoulFireBall);
        int attackRange = soulFire?.Range > 0 ? soulFire.Range : 7;
        const int retreatRange = 3;
        int dist = Functions.MaxDistance(current, monster.Location);
        bool highDamage = Client.MonsterMemory.GetDamage(monster.Name) > Client.GetMaxHP() / 5;

        if (poison != null && HasAmulet(1) && DateTime.UtcNow >= _nextSpellTime)
        {
            if (!_poisonedTargets.TryGetValue(monster.Id, out var until) || DateTime.UtcNow >= until)
            {
                if (dist <= attackRange)
                {
                    var dir = Functions.DirectionFromPoint(current, monster.Location);
                    await Client.CastMagicAsync(Spell.Poisoning, dir, monster.Location, monster.Id);
                    RecordSpellTime();
                    _poisonedTargets[monster.Id] = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                    return;
                }
            }
        }

        if (soulFire != null && HasAmulet())
        {
            bool requiresLine = CanFlySpells.List.Contains(Spell.SoulFireBall);
            bool canCast = map != null && dist <= attackRange && (!requiresLine || CanCast(map, current, monster.Location));

            if (highDamage)
            {
                if (dist <= retreatRange && map != null)
                {
                    var safe = GetRetreatPoint(map, current, monster, attackRange, retreatRange, requiresLine);
                    if (safe != current)
                    {
                        var path = await FindBufferedPathAsync(map, current, safe, 0);
                        if (path.Count > 0)
                        {
                            await MovementHelper.MoveAlongPathAsync(Client, path, safe);
                            RecordAttackTime();
                            return;
                        }
                    }
                }

                if (canCast && DateTime.UtcNow >= _nextSpellTime)
                {
                    var dir = Functions.DirectionFromPoint(current, monster.Location);
                    await Client.CastMagicAsync(Spell.SoulFireBall, dir, monster.Location, monster.Id);
                    RecordSpellTime();
                    return;
                }

                if (!canCast && map != null)
                {
                    await MoveToTargetAsync(map, current, monster, attackRange - 1);
                    return;
                }
            }
            else
            {
                if (canCast && DateTime.UtcNow >= _nextSpellTime)
                {
                    var dir = Functions.DirectionFromPoint(current, monster.Location);
                    await Client.CastMagicAsync(Spell.SoulFireBall, dir, monster.Location, monster.Id);
                    RecordSpellTime();
                    return;
                }

                if (dist <= 1)
                {
                    await base.AttackMonsterAsync(monster, current);
                }
                else if (!canCast && map != null)
                {
                    await MoveToTargetAsync(map, current, monster);
                }
                return;
            }
        }

        await base.AttackMonsterAsync(monster, current);
    }

    protected override async Task<bool> MoveToTargetAsync(MapData map, Point current, TrackedObject target, int radius = 1)
    {
        var soulFire = GetMagic(Spell.SoulFireBall);
        if (soulFire == null || !HasAmulet())
            return await base.MoveToTargetAsync(map, current, target, radius);

        int attackRange = soulFire.Range > 0 ? soulFire.Range : 7;
        const int retreatRange = 3;
        int dist = Functions.MaxDistance(current, target.Location);
        bool highDamage = Client.MonsterMemory.GetDamage(target.Name) > Client.GetMaxHP() / 5;
        bool requiresLine = CanFlySpells.List.Contains(Spell.SoulFireBall);
        bool canCast = dist <= attackRange && (!requiresLine || CanCast(map, current, target.Location));

        if (highDamage)
        {
            if (!canCast)
            {
                var path = await FindBufferedPathAsync(map, current, target.Location, 3);
                return path.Count > 0 && await MovementHelper.MoveAlongPathAsync(Client, path, path[^1]);
            }

            if (dist <= retreatRange)
            {
                var safe = GetRetreatPoint(map, current, target, attackRange, retreatRange, requiresLine);
                if (safe != current)
                {
                    var path = await FindBufferedPathAsync(map, current, safe, 0);
                    if (path.Count > 0)
                        return await MovementHelper.MoveAlongPathAsync(Client, path, safe);
                }
            }

            return true;
        }

        return await base.MoveToTargetAsync(map, current, target, radius);
    }

    protected override IReadOnlyList<DesiredItem> DesiredItems
    {
        get
        {
            var list = new List<DesiredItem>(base.DesiredItems);

            bool hasSoulFire = Client.Magics.Any(m => m.Spell == Spell.SoulFireBall);
            bool hasPoison = Client.Magics.Any(m => m.Spell == Spell.Poisoning);

            if (hasSoulFire)
                list.Add(new DesiredItem(ItemType.Amulet, shape: 0, count: 50));

            if (hasPoison)
            {
                list.Add(new DesiredItem(ItemType.Amulet, shape: 1, count: 50));
                list.Add(new DesiredItem(ItemType.Amulet, shape: 2, count: 50));
            }

            return list;
        }
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
        return origin;
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
                var pt = new Point(obj.Location.X + d.X, obj.Location.Y + d.Y);
                if (map.IsWalkable(pt.X, pt.Y))
                    obstacles.Add(pt);
            }
        }
        return obstacles;
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

    private async Task<List<Point>> FindBufferedPathAsync(MapData map, Point start, Point dest, int radius)
    {
        var obstacles = BuildObstacles(map);
        var path = await PathFinder.FindPathAsync(map, start, dest, obstacles, radius);
        if (path.Count == 0)
            path = await MovementHelper.FindPathAsync(Client, map, start, dest, 0, radius);
        return path;
    }
}
