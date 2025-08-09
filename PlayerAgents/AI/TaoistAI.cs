using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PlayerAgents.Map;

public sealed class TaoistAI : BaseAI
{
    public TaoistAI(GameClient client) : base(client) { }

    private DateTime _nextSpellTime = DateTime.MinValue;
    private readonly Dictionary<uint, DateTime> _redPoisoned = new();
    private readonly Dictionary<uint, DateTime> _greenPoisoned = new();
    private readonly Dictionary<uint, DateTime> _lastSoulShield = new();

    private uint? _skeletonId;
    private bool _skeletonResting;
    private bool _inventoryRefreshInProgress;
    private const string SkeletonName = "BoneFamiliar";

    private void RecordSpellTime()
    {
        RecordAttackTime();
        _nextSpellTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
    }

    protected override double HpPotionWeightFraction => 0.20;
    protected override double MpPotionWeightFraction => 0.40;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinDC, Stat.MaxDC, Stat.MinSC, Stat.MaxSC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };

    protected override IEnumerable<Spell> GetAttackSpells()
    {
        yield return Spell.SummonSkeleton;
        yield return Spell.Poisoning;
        yield return Spell.SoulFireBall;
        yield return Spell.Healing;
        yield return Spell.SoulShield;
    }

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
            return item.Info.Shape == shape;
        return true;
    }

    private async Task<bool> EnsureAmuletAsync(int shape)
    {
        if (HasAmulet(shape)) return true;
        var inventory = Client.Inventory;
        if (inventory == null) return false;
        foreach (var item in inventory)
        {
            if (item?.Info == null || item.Info.Type != ItemType.Amulet) continue;
            if (shape >= 0 && item.Info.Shape != shape) continue;

            await Client.EquipItemAsync(item, EquipmentSlot.Amulet);
            return true;
        }
        return false;
    }

    private TrackedObject? GetSkeleton()
    {
        if (_skeletonId.HasValue && Client.TrackedObjects.TryGetValue(_skeletonId.Value, out var obj))
        {
            if (!obj.Dead && !obj.Hidden && obj.Tamed &&
                obj.Name.StartsWith(SkeletonName, StringComparison.OrdinalIgnoreCase) &&
                obj.Name.EndsWith($"({Client.PlayerName})", StringComparison.OrdinalIgnoreCase))
                return obj;

            _skeletonId = null;
        }

        foreach (var o in Client.TrackedObjects.Values)
        {
            if (o.Type != ObjectType.Monster || !o.Tamed || o.Dead || o.Hidden) continue;
            if (!o.Name.StartsWith(SkeletonName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!o.Name.EndsWith($"({Client.PlayerName})", StringComparison.OrdinalIgnoreCase)) continue;

            _skeletonId = o.Id;
            return o;
        }

        return null;
    }

    private bool HasNearbySkeleton(Point loc)
    {
        foreach (var o in Client.TrackedObjects.Values)
        {
            if (o.Dead || o.Hidden) continue;
            if (o.Id == _skeletonId) continue;
            if (o.Type != ObjectType.Monster || !o.Tamed) continue;
            if (!o.Name.StartsWith(SkeletonName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Functions.MaxDistance(o.Location, loc) <= 1)
                return true;
        }
        return false;
    }

    private async Task RestSkeletonAsync()
    {
        var skeleton = GetSkeleton();
        if (skeleton == null) return;
        if (string.IsNullOrEmpty(Client.CurrentMapFile)) return;
        var map = Client.CurrentMap;
        if (map == null) return;
        var mapName = Path.GetFileNameWithoutExtension(Client.CurrentMapFile);

        var current = Client.CurrentLocation;
        var zones = Client.SafezoneMemory.GetAll()
            .Where(z => z.Map.Equals(mapName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(z => DistanceToZone(current, z));

        foreach (var zone in zones)
        {
            var candidates = new List<Point>();
            for (int x = zone.X - zone.Size; x <= zone.X + zone.Size; x++)
            {
                for (int y = zone.Y - zone.Size; y <= zone.Y + zone.Size; y++)
                {
                    var loc = new Point(x, y);
                    if (!map.IsWalkable(loc.X, loc.Y)) continue;
                    if (HasNearbySkeleton(loc)) continue;
                    candidates.Add(loc);
                }
            }

            foreach (var loc in candidates.OrderBy(p => Functions.MaxDistance(current, p)))
            {
                bool reached = await Client.MoveWithinRangeAsync(loc, 0, 0, NpcInteractionType.General, WalkDelay, null);
                if (!reached) continue;
                await Client.ChangePetModeAsync(PetMode.None);
                _skeletonResting = true;
                return;
            }
        }
    }

    private static int DistanceToZone(Point current, SafezoneEntry zone)
    {
        int dx = Math.Abs(current.X - zone.X) - zone.Size;
        if (dx < 0) dx = 0;
        int dy = Math.Abs(current.Y - zone.Y) - zone.Size;
        if (dy < 0) dy = 0;
        return Math.Max(dx, dy);
    }

    private async Task EnsureSkeletonAsync(Point current)
    {
        var magic = GetMagic(Spell.SummonSkeleton);
        if (magic == null) return;

        var skeleton = GetSkeleton();
        if (skeleton != null && Functions.MaxDistance(current, skeleton.Location) <= 10) return;

        if (DateTime.UtcNow < _nextSpellTime) return;
        if (!await EnsureAmuletAsync(0)) return;

        await Client.CastMagicAsync(Spell.SummonSkeleton, MirDirection.Up, current, 0);
        RecordSpellTime();

        await Client.ChangePetModeAsync(PetMode.Both);
        _skeletonResting = false;
    }

    private async Task<bool> SupportGroupAsync(Point current)
    {
        if (!Client.IsGrouped) return false;

        var heal = GetMagic(Spell.Healing);
        var soulShield = GetMagic(Spell.SoulShield);

        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (!Client.IsGroupMember(obj.Id) || obj.Id == Client.ObjectId || obj.Dead || obj.Hidden) continue;
            if (obj.HealthPercent.HasValue && heal != null && DateTime.UtcNow >= _nextSpellTime)
            {
                if (obj.HealthPercent.Value <= 60)
                {
                    var dir = Functions.DirectionFromPoint(current, obj.Location);
                    await Client.CastMagicAsync(Spell.Healing, dir, obj.Location, obj.Id);
                    RecordSpellTime();
                    return true;
                }
            }

            if (soulShield != null && DateTime.UtcNow >= _nextSpellTime)
            {
                if (!_lastSoulShield.TryGetValue(obj.Id, out var last) || DateTime.UtcNow - last > TimeSpan.FromMinutes(2))
                {
                    var dir = Functions.DirectionFromPoint(current, obj.Location);
                    await Client.CastMagicAsync(Spell.SoulShield, dir, obj.Location, obj.Id);
                    RecordSpellTime();
                    _lastSoulShield[obj.Id] = DateTime.UtcNow;
                    return true;
                }
            }
        }

        return false;
    }

    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (monster.Dead || monster.Hidden) return;

        if (await SupportGroupAsync(current))
            return;

        await EnsureSkeletonAsync(current);

        var soulShield = GetMagic(Spell.SoulShield);
        if (soulShield != null && !Client.HasBuff(BuffType.SoulShield) && DateTime.UtcNow >= _nextSpellTime)
        {
            if (await EnsureAmuletAsync(0))
            {
                await Client.CastMagicAsync(Spell.SoulShield, MirDirection.Up, current, Client.ObjectId);
                RecordSpellTime();
                return;
            }
        }

        var map = Client.CurrentMap;
        int maxHP = Client.GetMaxHP();
        var heal = GetMagic(Spell.Healing);
        if (heal != null && DateTime.UtcNow >= _nextSpellTime)
        {
            if (Client.HP <= maxHP * 4 / 5)
            {
                await Client.CastMagicAsync(Spell.Healing, MirDirection.Up, current, Client.ObjectId);
                RecordSpellTime();
                return;
            }
        }

        if (monster.AI == 3)
        {
            await base.AttackMonsterAsync(monster, current);
            return;
        }

        var poison = GetMagic(Spell.Poisoning);
        var soulFire = GetMagic(Spell.SoulFireBall);
        int attackRange = soulFire?.Range > 0 ? soulFire.Range : 7;
        int dist = Functions.MaxDistance(current, monster.Location);
        int avgAC = (Client.GetStatTotal(Stat.MinAC) + Client.GetStatTotal(Stat.MaxAC)) / 2;
        bool highDamage = Client.MonsterMemory.GetDamage(monster.Name) - avgAC > maxHP / 5;
        bool redPoisoned = monster.Poison.HasFlag(PoisonType.Red);
        bool greenPoisoned = monster.Poison.HasFlag(PoisonType.Green);
        if (greenPoisoned)
            _greenPoisoned.Remove(monster.Id);
        if (redPoisoned)
            _redPoisoned.Remove(monster.Id);

        if (poison != null && DateTime.UtcNow >= _nextSpellTime)
        {

            if (!greenPoisoned &&
                (!_greenPoisoned.TryGetValue(monster.Id, out var greenUntil) || DateTime.UtcNow >= greenUntil) &&
                await EnsureAmuletAsync(1))
            {
                if (dist <= attackRange)
                {
                    var dir = Functions.DirectionFromPoint(current, monster.Location);
                    await Client.CastMagicAsync(Spell.Poisoning, dir, monster.Location, monster.Id);
                    RecordSpellTime();
                    _greenPoisoned[monster.Id] = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                    return;
                }
            }

            if (!redPoisoned &&
                (!_redPoisoned.TryGetValue(monster.Id, out var redUntil) || DateTime.UtcNow >= redUntil) &&
                await EnsureAmuletAsync(2))
            {
                if (dist <= attackRange)
                {
                    var dir = Functions.DirectionFromPoint(current, monster.Location);
                    await Client.CastMagicAsync(Spell.Poisoning, dir, monster.Location, monster.Id);
                    RecordSpellTime();
                    _redPoisoned[monster.Id] = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                    return;
                }
            }
        }

        bool castedSoulFire = false;
        if (soulFire != null && await EnsureAmuletAsync(0))
        {
            bool requiresLine = CanFlySpells.List.Contains(Spell.SoulFireBall);
            bool canCast = map != null && dist <= attackRange && (!requiresLine || CanCast(map, current, monster.Location));

            if (canCast && DateTime.UtcNow >= _nextSpellTime)
            {
                var dir = Functions.DirectionFromPoint(current, monster.Location);
                await Client.CastMagicAsync(Spell.SoulFireBall, dir, monster.Location, monster.Id);
                RecordSpellTime();
                castedSoulFire = true;
            }
        }

        if (dist <= 1 && (!highDamage || !castedSoulFire))
        {
            await base.AttackMonsterAsync(monster, current);
        }
    }

    protected override async Task<bool> MoveToTargetAsync(MapData map, Point current, TrackedObject target, int radius = 1)
    {
        if (target.Type != ObjectType.Monster)
            return await base.MoveToTargetAsync(map, current, target, radius);
        if (target.Dead || target.Hidden)
            return false;
        if (target.AI == 3)
            return await base.MoveToTargetAsync(map, current, target, radius);
        await EnsureSkeletonAsync(current);
        var soulFire = GetMagic(Spell.SoulFireBall);
        if (soulFire == null || !HasAmulet())
            return await base.MoveToTargetAsync(map, current, target, radius);

        int attackRange = soulFire.Range > 0 ? soulFire.Range : 7;
        const int retreatRange = 3;
        int dist = Functions.MaxDistance(current, target.Location);
        int avgAC = (Client.GetStatTotal(Stat.MinAC) + Client.GetStatTotal(Stat.MaxAC)) / 2;
        bool highDamage = Client.MonsterMemory.GetDamage(target.Name) - avgAC > Client.GetMaxHP() / 5;
        bool requiresLine = CanFlySpells.List.Contains(Spell.SoulFireBall);
        bool inRange = dist <= attackRange;

        if (requiresLine && inRange && !CanCast(map, current, target.Location))
        {
            var path = await FindBufferedPathAsync(map, current, target.Location, 3);
            if (path.Count > 0)
                return await MovementHelper.MoveAlongPathAsync(Client, path, path[^1]);
            IgnoreMonster(target.Id, TimeSpan.FromSeconds(20));
            return true;
        }

        bool canCast = inRange && (!requiresLine || CanCast(map, current, target.Location));

        if (highDamage)
        {
            if (!inRange)
            {
                var path = await FindBufferedPathAsync(map, current, target.Location, 3);
                if (path.Count > 0)
                    return await MovementHelper.MoveAlongPathAsync(Client, path, path[^1]);
                return false;
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
                return false;
            }

            return false;
        }

        if (dist > 1)
        {
            var path = await FindBufferedPathAsync(map, current, target.Location, 1);
            return path.Count > 0 && await MovementHelper.MoveAlongPathAsync(Client, path, path[^1]);
        }

        return false;
    }

    protected override async Task BeforeNpcInteractionAsync(Point location, uint npcId, NpcEntry? entry, NpcInteractionType interactionType)
    {
        if (npcId == 0 && interactionType == NpcInteractionType.General)
            _inventoryRefreshInProgress = true;

        await RestSkeletonAsync();
    }

    protected override async Task AfterNpcInteractionAsync(Point location, uint npcId, NpcEntry? entry, NpcInteractionType interactionType)
    {
        if (_skeletonResting && (!_inventoryRefreshInProgress || npcId == 0))
        {
            await Client.ChangePetModeAsync(PetMode.Both);
            _skeletonResting = false;
        }

        if (npcId == 0 && interactionType == NpcInteractionType.General)
            _inventoryRefreshInProgress = false;
    }

    protected override IReadOnlyList<DesiredItem> DesiredItems
    {
        get
        {
            var list = new List<DesiredItem>(base.DesiredItems);

            bool hasSoulFire = Client.Magics.Any(m => m.Spell == Spell.SoulFireBall);
            bool hasPoison = Client.Magics.Any(m => m.Spell == Spell.Poisoning);
            bool hasSkeleton = Client.Magics.Any(m => m.Spell == Spell.SummonSkeleton);
            bool hasSoulShield = Client.Magics.Any(m => m.Spell == Spell.SoulShield);

            if (hasSoulFire || hasSkeleton || hasSoulShield)
                list.Add(new DesiredItem(ItemType.Amulet, shape: 0, count: 500));

            if (hasPoison)
                list.Add(new DesiredItem(ItemType.Amulet, shape: 1, count: 500));

            if (hasPoison)
                list.Add(new DesiredItem(ItemType.Amulet, shape: 2, count: 500));

            return list;
        }
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
                    if (obj.Type != ObjectType.Monster || obj.Dead || obj.Tamed || obj.Id == target.Id) continue;
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

}
