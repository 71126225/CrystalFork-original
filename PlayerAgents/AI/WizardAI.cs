using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using PlayerAgents.Map;

public sealed class WizardAI : BaseAI
{
    private DateTime _nextSpellTime = DateTime.MinValue;
    private Point? _cachedCastSpot;
    private DateTime _nextCastSpotCheck = DateTime.MinValue;
    private bool _taming;
    private string? _tameMonster;
    private int _tamedCount;
    private int _tameLimit;
    private readonly HashSet<uint> _pendingTames = new();
    private bool _petsResting;

    protected override bool ShouldIgnoreDistantTarget(TrackedObject target, int distance)
    {
        if (target.AI == 3)
            return distance > 20;

        var magic = GetBestMagic(Globals.RangedSpells);
        bool longRange = magic != null &&
                         magic.Range > 0 &&
                         !CanFlySpells.List.Contains(magic.Spell);
        return distance > 20 && !longRange;
    }

    public WizardAI(GameClient client) : base(client)
    {
        client.MonsterColourChanged += OnMonsterColourChanged;
        client.MonsterNameChanged += OnMonsterNameChanged;
    }

    protected override double HpPotionWeightFraction => 0.10;
    protected override double MpPotionWeightFraction => 0.50;
    protected override Stat[] OffensiveStats { get; } = new[]
        { Stat.MinMC, Stat.MaxMC };
    protected override Stat[] DefensiveStats { get; } = new[]
        { Stat.MinMAC, Stat.MaxMAC };

    protected override IEnumerable<Spell> GetAttackSpells()
    {
        foreach (var s in Globals.RangedSpells)
            yield return s;
        yield return Spell.HellFire;
    }

    private bool StartTamingRun()
    {
        var entry = Client.MonsterMemory.GetStrongestTameable(Client.Level);
        if (entry == null) return false;
        var electric = Client.Magics.FirstOrDefault(m => m.Spell == Spell.ElectricShock);
        if (electric == null) return false;
        _tameLimit = electric.Level + (Globals.MaxPets - 3);
        if (_tameLimit <= 0) return false;
        _taming = true;
        _tameMonster = entry.Name;
        _tamedCount = 0;
        _pendingTames.Clear();
        return true;
    }

    private string? GetClosestTameMap()
    {
        if (_tameMonster == null) return null;
        string? best = null;
        int bestLen = int.MaxValue;
        foreach (var map in Client.MonsterMemory.GetMonsterMaps(_tameMonster))
        {
            var path = MovementHelper.FindTravelPath(Client, Path.Combine(MapManager.MapDirectory, map + ".map"));
            if (path == null) continue;
            if (path.Count < bestLen)
            {
                best = map;
                bestLen = path.Count;
            }
        }
        return best;
    }

    private async Task<bool> TravelToTameMapAsync()
    {
        var map = GetClosestTameMap();
        if (map == null) return false;
        SetBestMap(map);
        var dest = Path.Combine(MapManager.MapDirectory, map + ".map");
        return await TravelToMapAsync(dest);
    }

    protected override async Task<bool> OnBeginTravelToBestMapAsync()
    {
        if (_taming)
        {
            if (!await TravelToTameMapAsync())
            {
                _taming = false;
                _tameMonster = null;
            }
            return false;
        }

        bool hasTames = Client.TrackedObjects.Values.Any(o => o.Type == ObjectType.Monster && o.Tamed);
        if (!hasTames && Random.Next(20) == 0 && StartTamingRun())
        {
            if (await TravelToTameMapAsync())
                return false;
            _taming = false;
            _tameMonster = null;
        }
        return true;
    }

    protected override void OnTameCommand()
    {
        bool hasTames = Client.TrackedObjects.Values.Any(o => o.Type == ObjectType.Monster && o.Tamed);
        if (hasTames) return;

        if (!StartTamingRun()) return;

        _ = Task.Run(async () =>
        {
            if (!await TravelToTameMapAsync())
            {
                _taming = false;
                _tameMonster = null;
            }
        });
    }

    protected override string GetMonsterTargetAction(TrackedObject monster)
    {
        if (_taming && _tameMonster != null &&
            string.Equals(monster.Name, _tameMonster, StringComparison.OrdinalIgnoreCase))
            return "taming...";
        return base.GetMonsterTargetAction(monster);
    }


    protected override async Task AttackMonsterAsync(TrackedObject monster, Point current)
    {
        if (monster.Dead || monster.Hidden) return;

        if (_taming && _tameMonster != null && _tamedCount < _tameLimit &&
            string.Equals(monster.Name, _tameMonster, StringComparison.OrdinalIgnoreCase))
        {
            var eshock = Client.Magics.FirstOrDefault(m => m.Spell == Spell.ElectricShock);
            if (eshock != null)
            {
                await TryTameAsync(monster);
            }
            return;
        }

        var map = Client.CurrentMap;

        var electric = Client.Magics.FirstOrDefault(m => m.Spell == Spell.ElectricShock);
        if (electric != null && !_taming)
        {
            int repulseAt = Client.MonsterMemory.GetRepulseAt(monster.Name);
            if (repulseAt != 0 && Client.Level >= repulseAt && !Client.MonsterMemory.GetCanTame(monster.Name))
            {
                int attempts = Client.MonsterMemory.GetTameAttempts(monster.Name);

                bool canAttempt = true;
                if (attempts % 20 == 0 && attempts > 0)
                {
                    int chance = attempts / 20;
                    canAttempt = Random.Next(chance) == 0;
                }

                if (canAttempt)
                {
                    var name = monster.Name;
                    await TryTameAsync(monster);
                    Client.MonsterMemory.IncrementTameAttempts(name);
                    return;
                }
            }
        }

        if (monster.AI == 3)
        {
            await base.AttackMonsterAsync(monster, current);
            return;
        }

        if (map != null && DateTime.UtcNow >= _nextSpellTime)
        {
            var hellfire = Client.Magics.FirstOrDefault(m => m.Spell == Spell.HellFire);
            if (hellfire != null)
            {
                int availableLevel = 0;
                if (Client.Level >= hellfire.Level3) availableLevel = 3;
                else if (Client.Level >= hellfire.Level2) availableLevel = 2;
                else if (Client.Level >= hellfire.Level1) availableLevel = 1;

                int castLevel = Math.Min(hellfire.Level + 1, availableLevel);
                if (castLevel > 0)
                {
                    int cost = hellfire.BaseCost + hellfire.LevelCost * (castLevel - 1);
                    if (cost <= Client.MP)
                    {
                        const int hellfireRange = 4;
                        int dist = Functions.MaxDistance(current, monster.Location);
                        if (dist <= hellfireRange && CanCast(map, current, monster.Location))
                        {
                            var dir = Functions.DirectionFromPoint(current, monster.Location);
                            bool targetFound = false;
                            bool otherFound = false;
                            var p = current;
                            for (int i = 1; i <= hellfireRange; i++)
                            {
                                p = Functions.PointMove(p, dir, 1);
                                if (p.X < 0 || p.Y < 0 || p.X >= map.Width || p.Y >= map.Height) break;
                                if (!map.IsWalkable(p.X, p.Y)) break;

                                foreach (var obj in Client.TrackedObjects.Values)
                                {
                                    if (obj.Type != ObjectType.Monster || obj.Dead || obj.Hidden || obj.Tamed || obj.AI == 3) continue;
                                    if (obj.Location != p) continue;
                                    if (obj.Id == monster.Id) targetFound = true;
                                    else otherFound = true;
                                    break;
                                }

                                if (targetFound && otherFound)
                                    break;
                            }

                            if (targetFound && otherFound)
                            {
                                await Client.CastMagicAsync(Spell.HellFire, dir, monster.Location, monster.Id);
                                RecordAttackTime();
                                _nextSpellTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
                                return;
                            }
                        }
                    }
                }
            }
        }

        var magic = GetBestMagic(Globals.RangedSpells);
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

    private async Task TryTameAsync(TrackedObject monster)
    {
        _pendingTames.Add(monster.Id);
        Client.SetTameTarget(monster.Id);
        var loc = Client.CurrentLocation;
        var dir = Functions.DirectionFromPoint(loc, monster.Location);
        await Client.CastMagicAsync(Spell.ElectricShock, dir, monster.Location, monster.Id);
        RecordAttackTime();
        await Task.Delay(AttackDelay);
    }

    private void OnMonsterColourChanged(uint id, Color colour)
    {
        if (_pendingTames.Contains(id) && Client.TrackedObjects.TryGetValue(id, out var obj) && obj.Tamed)
        {
            _pendingTames.Remove(id);
            _tamedCount++;
            if (_tamedCount >= _tameLimit)
            {
                _taming = false;
                _tameMonster = null;
                _pendingTames.Clear();
            }
        }
    }

    private void OnMonsterNameChanged(uint id, string name)
    {
        if (_pendingTames.Contains(id) && GameClient.IsTamedName(name))
        {
            _pendingTames.Remove(id);
            _tamedCount++;
            if (_tamedCount >= _tameLimit)
            {
                _taming = false;
                _tameMonster = null;
                _pendingTames.Clear();
            }
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

        var magic = GetBestMagic(Globals.RangedSpells);
        if (magic == null)
            return await base.MoveToTargetAsync(map, current, target, radius);

        int attackRange = magic.Range > 0 ? magic.Range : 7;
        const int retreatRange = 3;
        int dist = Functions.MaxDistance(current, target.Location);
        bool requiresLine = CanFlySpells.List.Contains(magic.Spell);

        if (dist > attackRange)
        {
            var path = await FindBufferedPathAsync(map, current, target.Location, attackRange);
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
            return true;
        }

        bool canCast = !requiresLine || CanCast(map, current, target.Location);

        if (!canCast)
        {
            var spot = requiresLine ? GetNearestCastSpot(map, target, current, attackRange, retreatRange)
                                     : GetSafestPoint(map, current, target, attackRange);
            if (spot == current)
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
            if (dist <= 1)
            {
                var repulse = Client.Magics.FirstOrDefault(m => m.Spell == Spell.Repulsion);
                if (repulse != null && DateTime.UtcNow >= _nextSpellTime)
                {
                    var targetId = target.Id;
                    var pushDir = Functions.DirectionFromPoint(current, target.Location);
                    var castTime = DateTime.UtcNow;

                    await Client.CastMagicAsync(Spell.Repulsion, MirDirection.Up, current, Client.ObjectId);
                    RecordAttackTime();
                    _nextSpellTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        if (Client.WasObjectPushedSince(targetId, pushDir, castTime))
                        {
                            Client.MonsterMemory.RecordRepulseAt(target.Name, Client.Level);
                        }
                        Client.ClearPushedObjects();
                    });
                }
            }

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

    private bool HasNearbyPet(Point loc)
    {
        foreach (var o in Client.TrackedObjects.Values)
        {
            if (o.Dead || o.Hidden) continue;
            if (o.Type != ObjectType.Monster || !o.Tamed) continue;
            if (Functions.MaxDistance(o.Location, loc) <= 1)
                return true;
        }
        return false;
    }

    private static int DistanceToZone(Point current, SafezoneEntry zone)
    {
        int dx = Math.Abs(current.X - zone.X) - zone.Size;
        if (dx < 0) dx = 0;
        int dy = Math.Abs(current.Y - zone.Y) - zone.Size;
        if (dy < 0) dy = 0;
        return Math.Max(dx, dy);
    }

    private async Task RestPetsAsync()
    {
        if (!Client.TrackedObjects.Values.Any(o => o.Type == ObjectType.Monster && o.Tamed)) return;
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
                    if (HasNearbyPet(loc)) continue;
                    candidates.Add(loc);
                }
            }

            foreach (var loc in candidates.OrderBy(p => Functions.MaxDistance(current, p)))
            {
                bool reached = await Client.MoveWithinRangeAsync(loc, 0, 0, NpcInteractionType.General, WalkDelay, null);
                if (!reached) continue;
                await Client.ChangePetModeAsync(PetMode.None);
                _petsResting = true;
                return;
            }
        }
    }

    protected override async Task BeforeNpcInteractionAsync(Point location, uint npcId, NpcEntry? entry, NpcInteractionType interactionType)
    {
        await RestPetsAsync();
    }

    protected override async Task AfterNpcInteractionAsync(Point location, uint npcId, NpcEntry? entry, NpcInteractionType interactionType)
    {
        if (_petsResting)
        {
            await Client.ChangePetModeAsync(PetMode.Both);
            _petsResting = false;
        }
    }

}

