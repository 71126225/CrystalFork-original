using ClientPackets;
using Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PlayerAgents.Map;

public class BaseAI
{
    protected readonly GameClient Client;
    protected static readonly Random Random = new();
    private TrackedObject? _currentTarget;
    private Point? _searchDestination;
    private Point? _lostTargetLocation;
    private DateTime _nextTargetSwitchTime = DateTime.MinValue;
    private List<Point>? _currentRoamPath;
    private List<Point>? _lostTargetPath;
    private DateTime _nextPathFindTime = DateTime.MinValue;
    private MirDirection? _lastRoamDirection;


    protected virtual TimeSpan TargetSwitchInterval => TimeSpan.FromSeconds(3);
    // Using HashSet for faster Contains checks
    protected static readonly HashSet<EquipmentSlot> OffensiveSlots = new()
    {
        EquipmentSlot.Weapon,
        EquipmentSlot.Necklace,
        EquipmentSlot.RingL,
        EquipmentSlot.RingR,
        EquipmentSlot.Stone
    };

    // Monsters with these AI values are ignored when selecting a target
    protected static readonly HashSet<byte> IgnoredAIs = new() { 6, 58, 57, 56, 64 };

    protected static bool IsOffensiveSlot(EquipmentSlot slot) => OffensiveSlots.Contains(slot);

    public BaseAI(GameClient client)
    {
        Client = client;
        Client.ItemScoreFunc = GetItemScore;
        Client.DesiredItemsProvider = () => DesiredItems;
        Client.MovementEntryRemoved += OnMovementEntryRemoved;
        Client.ExpRateSaved += OnExpRateSaved;
    }

    private void OnMovementEntryRemoved()
    {
        _travelPath = null;
        _currentRoamPath = null;
        _nextPathFindTime = DateTime.MinValue;
        _lastRoamDirection = null;
    }

    private void OnExpRateSaved(double rate)
    {
        if (rate <= 0)
        {
            _currentBestMap = null;
            _nextBestMapCheck = DateTime.UtcNow;
        }
    }

    protected virtual int WalkDelay => 600;
    protected virtual int AttackDelay => 1400;
    protected virtual TimeSpan RoamPathFindInterval => TimeSpan.FromSeconds(2);
    protected virtual TimeSpan FailedPathFindDelay => TimeSpan.FromSeconds(5);
    protected virtual TimeSpan TravelPathFindInterval => TimeSpan.FromSeconds(1);
    protected virtual TimeSpan FailedTravelPathFindDelay => TimeSpan.FromSeconds(1);
    protected virtual TimeSpan EquipCheckInterval => TimeSpan.FromSeconds(5);
    protected virtual IReadOnlyList<DesiredItem> DesiredItems => Array.Empty<DesiredItem>();
    private DateTime _nextEquipCheck = DateTime.UtcNow;
    private DateTime _nextAttackTime = DateTime.UtcNow;
    private DateTime _nextPotionTime = DateTime.MinValue;
    private DateTime _nextTownTeleportTime = DateTime.MinValue;
    private DateTime _nextBestMapCheck = DateTime.MinValue;
    private string? _currentBestMap;
    private DateTime _travelPauseUntil = DateTime.MinValue;
    private List<MapMovementEntry>? _travelPath;
    private int _travelIndex;
    private DateTime _stationarySince = DateTime.MinValue;
    private Point _lastStationaryLocation = Point.Empty;
    private DateTime _travelStuckSince = DateTime.MinValue;
    private DateTime _lastMoveOrAttackTime = DateTime.MinValue;
    
    private readonly Dictionary<(Point Location, string Name), DateTime> _itemRetryTimes = new();
    private readonly Dictionary<uint, DateTime> _monsterIgnoreTimes = new();
    private static readonly TimeSpan ItemRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnreachableItemRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DroppedItemRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MonsterIgnoreDelay = TimeSpan.FromSeconds(10);
    private bool _sentRevive;
    private bool _sellingItems;
    private bool _repairingItems;
    private bool _buyingItems;
    private bool _buyAttempted;
    private HashSet<ItemType> _pendingBuyTypes = new();

    protected virtual int GetItemScore(UserItem item, EquipmentSlot slot)
    {
        int score = 0;
        if (item.Info != null)
            score += item.Info.Stats.Count;
        if (item.AddedStats != null)
            score += item.AddedStats.Count;
        return score;
    }

    private Point GetRandomPoint(PlayerAgents.Map.MapData map, Random random, Point origin, int radius, MirDirection? preferred)
    {
        if (preferred == null)
            return MovementHelper.GetRandomPoint(Client, map, random, origin, radius);

        const int attempts = 20;
        var obstacles = MovementHelper.BuildObstacles(Client);

        for (int i = 0; i < attempts; i++)
        {
            var dir = Functions.ShiftDirection(preferred.Value, random.Next(-1, 2));
            int distance = random.Next(1, radius + 1);
            var candidate = Functions.PointMove(origin, dir, distance);

            if (map.IsWalkable(candidate.X, candidate.Y) && !obstacles.Contains(candidate))
                return candidate;
        }

        return MovementHelper.GetRandomPoint(Client, map, random, origin, radius);
    }

    private UserItem? GetBestItemForSlot(EquipmentSlot slot, IEnumerable<UserItem?> inventory, UserItem? current)
    {
        int bestScore = current != null ? GetItemScore(current, slot) : -1;
        UserItem? bestItem = current;
        foreach (var item in inventory)
        {
            if (item == null) continue;
            if (!Client.CanEquipItem(item, slot)) continue;
            int score = GetItemScore(item, slot);
            if (bestItem == null || score > bestScore)
            {
                bestItem = item;
                bestScore = score;
            }
        }
        return bestItem;
    }

    private async Task CheckEquipmentAsync()
    {
        var inventory = Client.Inventory;
        var equipment = Client.Equipment;
        if (inventory == null || equipment == null) return;

        // create a mutable copy so we can mark equipped items as used
        var available = inventory.ToList();

        for (int slot = 0; slot < equipment.Count; slot++)
        {
            var equipSlot = (EquipmentSlot)slot;
            if (equipSlot == EquipmentSlot.Torch) continue;
            UserItem? current = equipment[slot];
            UserItem? bestItem = GetBestItemForSlot(equipSlot, available, current);

            if (bestItem != null && bestItem != current)
            {
                await Client.EquipItemAsync(bestItem, equipSlot);
                int idx = available.IndexOf(bestItem);
                if (idx >= 0) available[idx] = null; // prevent using same item twice
                if (bestItem.Info != null)
                    Client.Log($"I have equipped {bestItem.Info.FriendlyName}");
            }
        }

        // handle torch based on time of day
        var torchSlot = EquipmentSlot.Torch;
        UserItem? currentTorch = equipment.Count > (int)torchSlot ? equipment[(int)torchSlot] : null;
        if (Client.TimeOfDay == LightSetting.Night)
        {
            UserItem? bestTorch = GetBestItemForSlot(torchSlot, available, currentTorch);
            if (bestTorch != null && bestTorch != currentTorch)
            {
                await Client.EquipItemAsync(bestTorch, torchSlot);
                int idx = available.IndexOf(bestTorch);
                if (idx >= 0) available[idx] = null;
                if (bestTorch.Info != null)
                    Client.Log($"I have equipped {bestTorch.Info.FriendlyName}");
            }
        }
        else if (currentTorch != null)
        {
            if (currentTorch.Info != null)
                Client.Log($"I have unequipped {currentTorch.Info.FriendlyName}");
            await Client.UnequipItemAsync(torchSlot);
        }
    }

    private async Task TryUsePotionsAsync()
    {
        if (DateTime.UtcNow < _nextPotionTime) return;

        int maxHP = Client.GetMaxHP();
        int maxMP = Client.GetMaxMP();

        if (Client.HP < maxHP)
        {
            var pot = Client.FindPotion(true);
            double hpPercent = (double)Client.HP / maxHP;
            if (pot != null)
            {
                int heal = Client.GetPotionRestoreAmount(pot, true);
                if (heal > 0 && (maxHP - Client.HP >= heal || hpPercent <= 0.10))
                {
                    await Client.UseItemAsync(pot);
                    string name = pot.Info?.FriendlyName ?? "HP potion";
                    Client.Log($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return;
                }
            }
            else if (DateTime.UtcNow >= _nextTownTeleportTime && hpPercent <= 0.10)
            {
                var teleport = Client.FindTownTeleport();
                if (teleport != null)
                {
                    await Client.UseItemAsync(teleport);
                    string name = teleport.Info?.FriendlyName ?? "town teleport";
                    Client.Log($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    _nextTownTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                    return;
                }
            }
        }

        if (Client.MP < maxMP)
        {
            var pot = Client.FindPotion(false);
            if (pot != null)
            {
                int heal = Client.GetPotionRestoreAmount(pot, false);
                double mpPercent = (double)Client.MP / maxMP;
                if (heal > 0 && (maxMP - Client.MP >= heal || mpPercent <= 0.10))
                {
                    await Client.UseItemAsync(pot);
                    string name = pot.Info?.FriendlyName ?? "MP potion";
                    Client.Log($"Used {name}");
                    _nextPotionTime = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
            }
        }
    }

    private TrackedObject? FindClosestTarget(Point current, out int bestDist)
    {
        TrackedObject? closestMonster = null;
        int monsterDist = int.MaxValue;
        TrackedObject? closestItem = null;
        int itemDist = int.MaxValue;

        foreach (var obj in Client.TrackedObjects.Values)
        {
            if (obj.Type == ObjectType.Monster)
            {
                if (_monsterIgnoreTimes.TryGetValue(obj.Id, out var ignore) && DateTime.UtcNow < ignore) continue;
                if (obj.Dead) continue;
                if (IgnoredAIs.Contains(obj.AI)) continue;
                // previously ignored monsters that were recently engaged with another player
                // now we attempt to attack them unless we cannot reach them
                int dist = Functions.MaxDistance(current, obj.Location);
                if (dist < monsterDist)
                {
                    monsterDist = dist;
                    closestMonster = obj;
                }
            }
            else if (obj.Type == ObjectType.Item)
            {
                if (_itemRetryTimes.TryGetValue((obj.Location, obj.Name), out var retry) && DateTime.UtcNow < retry)
                    continue;
                int dist = Functions.MaxDistance(current, obj.Location);
                if (dist < itemDist)
                {
                    itemDist = dist;
                    closestItem = obj;
                }
            }
        }

        // Prioritize adjacent monsters
        if (closestMonster != null && monsterDist <= 1)
        {
            bestDist = monsterDist;
            return closestMonster;
        }

        // choose nearest between remaining options
        if (closestMonster != null && (closestItem == null || monsterDist <= itemDist))
        {
            bestDist = monsterDist;
            return closestMonster;
        }

        if (closestItem != null)
        {
            bool isGold = string.Equals(closestItem.Name, "Gold", StringComparison.OrdinalIgnoreCase);
            if (isGold || (Client.HasFreeBagSpace() && Client.GetCurrentBagWeight() < Client.GetMaxBagWeight()))
            {
                bestDist = itemDist;
                return closestItem;
            }
        }

        bestDist = int.MaxValue;
        return null;
    }

    private async Task<List<Point>> FindPathAsync(PlayerAgents.Map.MapData map, Point start, Point dest, uint ignoreId = 0, int radius = 1)
    {
        return await MovementHelper.FindPathAsync(Client, map, start, dest, ignoreId, radius);
    }

    private async Task<bool> MoveAlongPathAsync(List<Point> path, Point destination)
    {
        bool moved = await MovementHelper.MoveAlongPathAsync(Client, path, destination);
        if (moved)
            _lastMoveOrAttackTime = DateTime.UtcNow;
        return moved;
    }

    private Task<bool> TravelToMapAsync(string destMapFile)
    {
        if (DateTime.UtcNow < _travelPauseUntil)
            return Task.FromResult(false);

        var path = MovementHelper.FindTravelPath(Client, destMapFile);
        if (path == null)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            _travelPauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            return Task.FromResult(false);
        }

        if (path.Count == 0)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            return Task.FromResult(true);
        }

        _travelPath = path;
        _travelIndex = 0;
        UpdateTravelDestination();
        return Task.FromResult(true);
    }

    private void UpdateTravelDestination()
    {
        if (_travelPath == null) return;
        if (_travelIndex >= _travelPath.Count)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            Client.UpdateAction("roaming...");
            return;
        }

        string current = Path.GetFileNameWithoutExtension(Client.CurrentMapFile);
        var step = _travelPath[_travelIndex];

        if (current == step.DestinationMap)
        {
            _travelIndex++;
            if (_travelIndex >= _travelPath.Count)
            {
                _travelPath = null;
                _searchDestination = null;
                _lastRoamDirection = null;
                Client.UpdateAction("roaming...");
                return;
            }
            step = _travelPath[_travelIndex];
        }
        else if (current != step.SourceMap)
        {
            _travelPath = null;
            _searchDestination = null;
            _lastRoamDirection = null;
            Client.UpdateAction("roaming...");
            return;
        }

        var dest = new Point(step.SourceX, step.SourceY);
        if (_searchDestination == null || _searchDestination.Value != dest)
        {
            _searchDestination = dest;
            _currentRoamPath = null;
            _nextPathFindTime = DateTime.MinValue;
            _lastRoamDirection = null;
        }
    }

    private async Task ProcessBestMapAsync()
    {
        if (Client.IgnoreNpcInteractions || DateTime.UtcNow < _travelPauseUntil)
            return;

        if (DateTime.UtcNow >= _nextBestMapCheck)
        {
            _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromHours(1);
            string? selected = Client.GetBestMapForLevel();
            if (selected == null || Random.Next(20) == 0)
            {
                var explore = Client.GetRandomExplorationMap();
                selected = string.IsNullOrEmpty(explore) ? _currentBestMap : explore;
            }

            if (selected != _currentBestMap)
            {
                _currentBestMap = selected;
                await SellRepairAndBuyAsync();
                // force path recalculation if destination changes or interval lapses
                _travelPath = null;
            }
        }

        if (_currentBestMap == null)
            return;

        var target = Path.Combine(MapManager.MapDirectory, _currentBestMap + ".map");
        if (!string.Equals(Client.CurrentMapFile, target, StringComparison.OrdinalIgnoreCase))
        {
            if (_travelPath == null)
            {
                Client.Log($"Travelling to best map {_currentBestMap}");
                if (!await TravelToMapAsync(target))
                {
                    _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                    return;
                }
                _currentRoamPath = null;
                _lostTargetLocation = null;
                _lostTargetPath = null;
                _currentTarget = null;
            }
        }
    }

    private static bool MatchesDesiredItem(UserItem item, DesiredItem desired)
    {
        if (item.Info == null) return false;
        if (item.Info.Type != desired.Type) return false;
        if (desired.Shape.HasValue && item.Info.Shape != desired.Shape.Value) return false;
        if (desired.HpPotion.HasValue)
        {
            bool healsHP = item.Info.Stats[Stat.HP] > 0 || item.Info.Stats[Stat.HPRatePercent] > 0;
            bool healsMP = item.Info.Stats[Stat.MP] > 0 || item.Info.Stats[Stat.MPRatePercent] > 0;
            if (desired.HpPotion.Value && !healsHP) return false;
            if (!desired.HpPotion.Value && !healsMP) return false;
        }

        return true;
    }

    private Dictionary<UserItem, ushort> GetItemKeepCounts(IEnumerable<UserItem> inventory)
    {
        var keep = new Dictionary<UserItem, ushort>();
        int maxWeight = Client.GetMaxBagWeight();

        // Keep any potential equipment upgrades
        var equipment = Client.Equipment;
        if (equipment != null)
        {
            // Create mutable list so each item can only fill a single slot
            var available = inventory.ToList();

            for (int slot = 0; slot < equipment.Count; slot++)
            {
                var equipSlot = (EquipmentSlot)slot;
                if (equipSlot == EquipmentSlot.Torch) continue;

                UserItem? current = equipment[slot];
                UserItem? bestItem = GetBestItemForSlot(equipSlot, available, current);

                if (bestItem != null && bestItem != current)
                {
                    keep[bestItem] = bestItem.Count;
                    int idx = available.IndexOf(bestItem);
                    if (idx >= 0) available[idx] = null; // don't reuse same item
                }
            }
        }

        // Keep desired items up to the configured quota
        foreach (var desired in DesiredItems)
        {
            var matching = inventory.Where(i => MatchesDesiredItem(i, desired)).ToList();

            if (desired.Count.HasValue)
            {
                int remaining = desired.Count.Value;
                foreach (var item in matching.OrderByDescending(i => i.Weight))
                {
                    if (remaining <= 0) break;
                    ushort already = keep.TryGetValue(item, out var val) ? val : (ushort)0;
                    int available = item.Count - already;
                    if (available <= 0) continue;
                    ushort amount = (ushort)Math.Min(available, remaining);
                    keep[item] = (ushort)(already + amount);
                    remaining -= amount;
                }
            }

            if (desired.WeightFraction > 0)
            {
                int requiredWeight = (int)Math.Ceiling(maxWeight * desired.WeightFraction);
                int current = matching.Sum(i =>
                {
                    ushort kept = keep.TryGetValue(i, out var val) ? val : (ushort)0;
                    return i.Info!.Weight * kept;
                });
                foreach (var item in matching.OrderByDescending(i => i.Weight))
                {
                    if (current >= requiredWeight) break;
                    ushort already = keep.TryGetValue(item, out var val) ? val : (ushort)0;
                    if (item.Info == null) continue;
                    int available = item.Count - already;
                    if (available <= 0) continue;
                    int weightPer = item.Info.Weight;
                    int needed = (requiredWeight - current + weightPer - 1) / weightPer;
                    int add = Math.Min(available, needed);
                    keep[item] = (ushort)(already + add);
                    current += add * weightPer;
                }
            }
        }

        return keep;
    }

    private enum NpcInteractionResult
    {
        Success,
        PathFailed,
        NpcNotFound
    }

    private async Task<NpcInteractionResult> InteractWithNpcAsync(Point location, uint npcId, NpcEntry? entry,
        NpcInteractionType interactionType, IReadOnlyList<(UserItem item, ushort count)>? sellItems = null)
    {
        bool reached = false;
        for (int attempt = 0; attempt < 2 && !reached; attempt++)
        {
            reached = await Client.MoveWithinRangeAsync(location, npcId, Globals.DataRange, interactionType, WalkDelay);
            if (!reached)
                await Task.Delay(1000);
        }
        if (!reached)
        {
            Client.Log($"Could not path to {entry?.Name ?? npcId.ToString()}");
            return NpcInteractionResult.PathFailed;
        }

        if (npcId == 0 && entry != null)
            npcId = await Client.ResolveNpcIdAsync(entry);

        if (npcId == 0)
        {
            Client.Log($"Could not find NPC to {interactionType.ToString().ToLower()}");
            if (entry != null)
                Client.RemoveNpc(entry);
            return NpcInteractionResult.NpcNotFound;
        }

        switch (interactionType)
        {
            case NpcInteractionType.General:
                if (entry != null)
                    Client.StartNpcInteraction(npcId, entry);
                break;
            case NpcInteractionType.Buying:
                await Client.BuyNeededItemsAtNpcAsync(npcId);
                break;
            case NpcInteractionType.Selling:
                if (sellItems != null)
                {
                    await Client.SellItemsToNpcAsync(npcId, sellItems);
                    Client.Log($"Finished selling to {entry?.Name ?? npcId.ToString()}");
                }
                break;
            case NpcInteractionType.Repairing:
                await Client.RepairItemsAtNpcAsync(npcId);
                Client.Log($"Finished repairing at {entry?.Name ?? npcId.ToString()}");
                break;
        }

        return NpcInteractionResult.Success;
    }

    private async Task HandleInventoryAsync(bool force = false)
    {
        if (_sellingItems) return;
        var inventory = Client.Inventory;
        if (inventory == null) return;

        bool full = !Client.HasFreeBagSpace();
        bool heavy = Client.GetCurrentBagWeight() >= Client.GetMaxBagWeight() * 0.9;
        if (!force && !full && !heavy) return;

        var items = inventory.Where(i => i != null && i.Info != null).ToList();
        var keepCounts = GetItemKeepCounts(items);
        var sellGroups = items
            .Select(i => {
                ushort keep = keepCounts.TryGetValue(i, out var k) ? k : (ushort)0;
                ushort sell = (ushort)Math.Max(i.Count - keep, 0);
                return (item: i, sell);
            })
            .Where(t => t.sell > 0)
            .GroupBy(t => t.item!.Info!.Type)
            .ToDictionary(g => g.Key, g => g.ToList());

        _sellingItems = true;
        Client.UpdateAction("selling items");
        Client.IgnoreNpcInteractions = true;
        while (sellGroups.Count > 0)
        {
            var types = sellGroups.Keys.ToList();
            if (!Client.TryFindNearestNpc(types, out var npcId, out var loc, out var entry, out var matchedTypes, includeUnknowns: false))
                break;

            int count = matchedTypes.Sum(t => sellGroups[t].Sum(x => x.sell));
            Client.Log($"Heading to {entry?.Name ?? "unknown npc"} at {loc.X},{loc.Y} to sell {count} items");

            var sellItems = matchedTypes.SelectMany(t => sellGroups[t]).Where(x => x.item != null).ToList();
            var result = await InteractWithNpcAsync(loc, npcId, entry, NpcInteractionType.Selling, sellItems);

            if (result == NpcInteractionResult.Success)
            {
                foreach (var t in matchedTypes)
                    sellGroups.Remove(t);
            }
            else
            {
                if (result == NpcInteractionResult.NpcNotFound)
                    continue;
                break;
            }
        }
        Client.IgnoreNpcInteractions = false;
        Client.ResumeNpcInteractions();
        _sellingItems = false;
        Client.UpdateAction("roaming...");
    }

    private async Task HandleEquipmentRepairsAsync(bool force = false)
    {
        if (_repairingItems) return;
        var equipment = Client.Equipment;
        if (equipment == null) return;

        var toRepair = equipment.Where(i => i != null && i.Info != null && i.CurrentDura < i.MaxDura).ToList();
        if (toRepair.Count == 0) return;

        bool urgent = toRepair.Any(i => i.MaxDura > 0 && i.CurrentDura <= i.MaxDura * 0.05);
        if (!force && !urgent) return;

        _repairingItems = true;
        Client.UpdateAction("repairing items...");
        Client.IgnoreNpcInteractions = true;

        var types = toRepair.Select(i => i!.Info!.Type).Distinct().ToList();
        while (types.Count > 0)
        {
            if (!Client.TryFindNearestRepairNpc(types, out var npcId, out var loc, out var entry, out var matched, includeUnknowns: false))
                break;

            if (entry != null)
            {
                var itemNames = toRepair.Where(i => i.Info != null && matched.Contains(i.Info.Type))
                    .Select(i => i.Info!.FriendlyName)
                    .ToList();
                if (itemNames.Count > 0)
                    Client.Log($"I am heading to {entry.Name} at {loc.X}, {loc.Y} to repair {string.Join(", ", itemNames)}");
            }

            var result = await InteractWithNpcAsync(loc, npcId, entry, NpcInteractionType.Repairing);

            if (result == NpcInteractionResult.Success)
            {
                foreach (var t in matched)
                    types.Remove(t);
            }
            else
            {
                break;
            }
        }

        Client.IgnoreNpcInteractions = false;
        Client.ResumeNpcInteractions();
        _repairingItems = false;
        Client.UpdateAction("roaming...");
    }

    private bool NeedMoreOfDesiredItem(DesiredItem desired)
    {
        var inventory = Client.Inventory;
        if (inventory == null) return false;
        var matching = inventory.Where(i => i != null && MatchesDesiredItem(i!, desired)).ToList();

        if (desired.Count.HasValue)
            return matching.Count < desired.Count.Value;

        if (desired.WeightFraction > 0)
        {
            int requiredWeight = (int)Math.Ceiling(Client.GetMaxBagWeight() * desired.WeightFraction);
            int currentWeight = matching.Sum(i => i!.Weight);
            return currentWeight < requiredWeight;
        }

        return false;
    }

    private HashSet<ItemType> GetNeededBuyTypes()
    {
        var needed = new HashSet<ItemType>();
        foreach (var d in DesiredItems)
            if (NeedMoreOfDesiredItem(d))
                needed.Add(d.Type);
        return needed;
    }

    private void UpdatePendingBuyTypes()
    {
        _pendingBuyTypes = GetNeededBuyTypes();
        _buyAttempted = false;
    }

    private void RefreshPendingBuyTypes()
    {
        foreach (var type in _pendingBuyTypes.ToList())
        {
            var desired = DesiredItems.FirstOrDefault(d => d.Type == type);
            if (desired == null) continue;
            if (!NeedMoreOfDesiredItem(desired))
                _pendingBuyTypes.Remove(type);
        }
    }

    private async Task HandleBuyingItemsAsync()
    {
        if (_buyingItems || _buyAttempted) return;

        if (_pendingBuyTypes.Count == 0) return;

        var neededTypes = _pendingBuyTypes.ToHashSet();

        _buyAttempted = true;
        _buyingItems = true;
        Client.UpdateAction("buying items");
        Client.IgnoreNpcInteractions = true;

        try
        {
            while (neededTypes.Count > 0)
            {
                if (!Client.TryFindNearestBuyNpc(neededTypes, out var npcId, out var loc, out var entry, out var matched, includeUnknowns: false))
                    break;

                if (entry != null)
                    Client.Log($"Heading to {entry.Name} at {loc.X}, {loc.Y} to buy items");

                var result = await InteractWithNpcAsync(loc, npcId, entry, NpcInteractionType.Buying);

                foreach (var t in matched)
                    _pendingBuyTypes.Remove(t);

                RefreshPendingBuyTypes();
                neededTypes = _pendingBuyTypes.ToHashSet();

                if (result != NpcInteractionResult.Success)
                    break;
            }
        }
        finally
        {
            Client.IgnoreNpcInteractions = false;
            Client.ResumeNpcInteractions();
            RefreshPendingBuyTypes();
            _buyingItems = false;
            Client.UpdateAction("roaming...");
            UpdateTravelDestination();
        }
    }

    private async Task SellRepairAndBuyAsync()
    {
        await HandleInventoryAsync(true);
        await HandleEquipmentRepairsAsync(true);
        UpdatePendingBuyTypes();
        await HandleBuyingItemsAsync();
    }

    public virtual async Task RunAsync()
    {
        Point current;
        _nextBestMapCheck = DateTime.UtcNow;
        _lastStationaryLocation = Client.CurrentLocation;
        _stationarySince = DateTime.UtcNow;
        _lastMoveOrAttackTime = DateTime.UtcNow;
        _buyAttempted = false;
        while (!Client.Disconnected)
        {
            if (await HandleReviveAsync())
                continue;

            if (await HandleHarvestingAsync())
                continue;

            if (Client.MovementSavePending)
            {
                await Task.Delay(WalkDelay);
                continue;
            }

            Client.ProcessMapExpRateInterval();
            await HandleBuyingItemsAsync();
            await ProcessBestMapAsync();
            UpdateTravelDestination();
            bool traveling = _travelPath != null && DateTime.UtcNow >= _travelPauseUntil;
            if (traveling)
            {
                _currentTarget = null;
                _lostTargetLocation = null;
                _lostTargetPath = null;
            }
            if (Client.IsProcessingNpc)
            {
                await Task.Delay(WalkDelay);
                continue;
            }

            if (DateTime.UtcNow >= _nextEquipCheck)
            {
                await CheckEquipmentAsync();
                _nextEquipCheck = DateTime.UtcNow + EquipCheckInterval;
            }

            await HandleEquipmentRepairsAsync();

            await TryUsePotionsAsync();

            await HandleInventoryAsync();

            if (Client.GetCurrentBagWeight() > Client.GetMaxBagWeight() && Client.LastPickedItem != null)
            {
                Client.Log("Overweight detected, dropping last picked item");
                var drop = Client.LastPickedItem;
                await Client.DropItemAsync(drop);
                if (drop?.Info != null)
                {
                    // item may spawn on any adjacent cell so ignore all nearby copies
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var loc = new Point(Client.CurrentLocation.X + dx, Client.CurrentLocation.Y + dy);
                            _itemRetryTimes[(loc, drop.Info.FriendlyName)] = DateTime.UtcNow + DroppedItemRetryDelay;
                        }
                    }
                }
            }

            foreach (var kv in _itemRetryTimes.ToList())
                if (DateTime.UtcNow >= kv.Value)
                    _itemRetryTimes.Remove(kv.Key);

            foreach (var kv in _monsterIgnoreTimes.ToList())
                if (DateTime.UtcNow >= kv.Value)
                    _monsterIgnoreTimes.Remove(kv.Key);

            if (!Client.IsProcessingNpc && Client.TryDequeueNpc(out var npcId, out var entry))
            {
                var npcLoc = new Point(entry.X, entry.Y);
                _ = await InteractWithNpcAsync(npcLoc, npcId, entry, NpcInteractionType.General);
                await Task.Delay(WalkDelay);
                continue;
            }

            var map = Client.CurrentMap;
            if (map == null || !Client.IsMapLoaded)
            {
                await Task.Delay(WalkDelay);
                continue;
            }

            current = Client.CurrentLocation;
            if (_currentTarget != null && !Client.TrackedObjects.ContainsKey(_currentTarget.Id))
            {
                _lostTargetLocation = _currentTarget.Location;
                _lostTargetPath = null;
                _currentRoamPath = null;
                _currentTarget = null;
                _nextPathFindTime = DateTime.MinValue;
            }
            if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster)
            {
                if (_currentTarget.Dead)
                    _nextTargetSwitchTime = DateTime.MinValue;
            }
            int distance = 0;
            TrackedObject? closest = traveling ? null : FindClosestTarget(current, out distance);

            if (!traveling && _currentTarget != null && _currentTarget.Type == ObjectType.Monster &&
                !_currentTarget.Dead &&
                Client.TrackedObjects.ContainsKey(_currentTarget.Id) &&
                closest != null && closest.Type == ObjectType.Monster &&
                closest.Id != _currentTarget.Id &&
                DateTime.UtcNow < _nextTargetSwitchTime)
            {
                closest = _currentTarget;
                distance = Functions.MaxDistance(current, _currentTarget.Location);
            }

            if (closest != null)
            {
                _currentRoamPath = null;
                _lostTargetLocation = null;
                _lostTargetPath = null;
                _nextPathFindTime = DateTime.MinValue;
                if (_currentTarget?.Id != closest.Id)
                {
                    Client.Log($"I have targeted {closest.Name} at {closest.Location.X}, {closest.Location.Y}");
                    _currentTarget = closest;
                    if (closest.Type == ObjectType.Monster)
                        _nextTargetSwitchTime = DateTime.UtcNow + TargetSwitchInterval;
                }

                if (closest.Type == ObjectType.Item)
                {
                    if (distance > 0)
                    {
                        var path = await FindPathAsync(map, current, closest.Location, closest.Id, 0);
                        bool moved = path.Count > 0 && await MoveAlongPathAsync(path, closest.Location);
                        if (!moved)
                        {
                            var delay = path.Count == 0 ? UnreachableItemRetryDelay : ItemRetryDelay;
                            _itemRetryTimes[(closest.Location, closest.Name)] = DateTime.UtcNow + delay;
                            _currentTarget = null;
                        }
                    }
                    else
                    {
                        if (Client.HasFreeBagSpace() && Client.GetCurrentBagWeight() < Client.GetMaxBagWeight())
                        {
                            await Client.PickUpAsync();
                        }
                        _itemRetryTimes[(closest.Location, closest.Name)] = DateTime.UtcNow + ItemRetryDelay;
                        _currentTarget = null;
                    }
                }
                else
                {
                    if (distance > 1)
                    {
                        var path = await FindPathAsync(map, current, closest.Location, closest.Id);
                        bool moved = path.Count > 0 && await MoveAlongPathAsync(path, closest.Location);
                        if (!moved)
                        {
                            // ignore unreachable targets
                            _monsterIgnoreTimes[closest.Id] = DateTime.UtcNow + MonsterIgnoreDelay;
                            _currentTarget = null;
                            _nextTargetSwitchTime = DateTime.MinValue;
                        }
                    }
                    else if (DateTime.UtcNow >= _nextAttackTime)
                    {
                        var dir = Functions.DirectionFromPoint(current, closest.Location);
                        await Client.AttackAsync(dir);
                        _lastMoveOrAttackTime = DateTime.UtcNow;
                        _stationarySince = DateTime.UtcNow; // reset turn timer when attacking
                        _nextAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
                    }
                }
            }
            else
            {
                _currentTarget = null;
                if (!traveling && _lostTargetLocation.HasValue)
                {
                    if (Functions.MaxDistance(current, _lostTargetLocation.Value) <= 0)
                    {
                        _lostTargetLocation = null;
                        _lostTargetPath = null;
                    }
                    else
                    {
                        if (_lostTargetPath == null || _lostTargetPath.Count <= 1)
                        {
                            if (DateTime.UtcNow >= _nextPathFindTime)
                            {
                                _lostTargetPath = await FindPathAsync(map, current, _lostTargetLocation.Value);
                                _nextPathFindTime = DateTime.UtcNow + RoamPathFindInterval;
                                if (_lostTargetPath.Count == 0)
                                {
                                    _lostTargetLocation = null;
                                    _lostTargetPath = null;
                                }
                            }
                        }

                        if (_lostTargetPath != null && _lostTargetPath.Count > 0)
                        {
                            bool moved = await MoveAlongPathAsync(_lostTargetPath, _lostTargetLocation.Value);
                            if (!moved)
                            {
                                _lostTargetPath = null;
                                _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                            }
                            else if (_lostTargetPath.Count <= 1)
                            {
                                _lostTargetPath = null;
                                _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                            }
                        }
                    }
                }

                if (!traveling && !_lostTargetLocation.HasValue)
                {
                    if (_searchDestination == null ||
                        Functions.MaxDistance(current, _searchDestination.Value) <= 1 ||
                        !map.IsWalkable(_searchDestination.Value.X, _searchDestination.Value.Y))
                    {
                        _searchDestination = GetRandomPoint(map, Random, current, 50, _lastRoamDirection);
                        _lastRoamDirection = Functions.DirectionFromPoint(current, _searchDestination.Value);
                        _currentRoamPath = null;
                        _nextPathFindTime = DateTime.MinValue;
                        Client.Log($"No targets nearby, searching at {_searchDestination.Value.X}, {_searchDestination.Value.Y}");
                    }

                    if (_currentRoamPath == null || _currentRoamPath.Count <= 1)
                    {
                        if (DateTime.UtcNow >= _nextPathFindTime)
                        {
                            _currentRoamPath = await FindPathAsync(map, current, _searchDestination.Value, 0, 0);
                            _nextPathFindTime = DateTime.UtcNow + RoamPathFindInterval;
                            if (_currentRoamPath.Count == 0)
                            {
                                _currentRoamPath = null;
                                _searchDestination = null;
                                _lastRoamDirection = null;
                                _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                                await Task.Delay(WalkDelay);
                                continue;
                            }
                        }
                    }

                    if (_currentRoamPath != null && _currentRoamPath.Count > 0)
                    {
                        bool moved = await MoveAlongPathAsync(_currentRoamPath, _searchDestination.Value);
                        if (!moved)
                        {
                            _currentRoamPath = null;
                            _searchDestination = null;
                            _lastRoamDirection = null;
                            _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                        }
                        else if (_currentRoamPath.Count <= 1)
                        {
                            _currentRoamPath = null;
                            _nextPathFindTime = DateTime.UtcNow + FailedPathFindDelay;
                        }
                    }
                }
                else if (traveling && _searchDestination.HasValue)
                {
                    if (_currentRoamPath == null || _currentRoamPath.Count < 1)
                    {
                        if (DateTime.UtcNow >= _nextPathFindTime)
                        {
                            _currentRoamPath = await FindPathAsync(map, current, _searchDestination.Value, 0, 0);
                            _nextPathFindTime = DateTime.UtcNow + TravelPathFindInterval;
                            if (_currentRoamPath.Count == 0)
                            {
                                _travelPath = null;
                                _searchDestination = null;
                                _currentRoamPath = null;
                                _lastRoamDirection = null;
                                _travelPauseUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                                _nextBestMapCheck = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                                traveling = false;
                            }
                        }
                    }

                    if (traveling && _currentRoamPath != null && _currentRoamPath.Count > 1)
                    {
                        bool moved = await MoveAlongPathAsync(_currentRoamPath, _searchDestination.Value);
                        if (!moved)
                        {
                            _currentRoamPath = null;
                            _nextPathFindTime = DateTime.UtcNow + FailedTravelPathFindDelay;
                        }
                        else if (_currentRoamPath.Count <= 1)
                        {
                            _currentRoamPath = null;
                            _nextPathFindTime = DateTime.UtcNow + TravelPathFindInterval;
                        }
                    }
                }
                if (traveling && _searchDestination.HasValue)
                {
                    if (Client.CurrentLocation == _searchDestination.Value)
                    {
                        if (_travelStuckSince == DateTime.MinValue)
                            _travelStuckSince = DateTime.UtcNow;
                        else if (DateTime.UtcNow - _travelStuckSince > TimeSpan.FromSeconds(5))
                        {
                            var dir = (MirDirection)Random.Next(8);
                            await Client.TurnAsync(dir);
                            _travelStuckSince = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        _travelStuckSince = DateTime.MinValue;
                    }
                }
            }

            if (_sellingItems)
            {
                Client.UpdateAction("selling items");
            }
            else if (_repairingItems)
            {
                Client.UpdateAction("repairing items...");
            }
            else if (_buyingItems)
            {
                Client.UpdateAction("buying items");
            }
            else if (traveling)
            {
                Client.UpdateAction("travelling...");
            }
            else if (_currentTarget != null && _currentTarget.Type == ObjectType.Monster)
            {
                Client.UpdateAction($"attacking {_currentTarget.Name}");
            }
            else
            {
                Client.UpdateAction("roaming...");
            }

            if (Client.CurrentLocation != _lastStationaryLocation)
            {
                _lastStationaryLocation = Client.CurrentLocation;
                _stationarySince = DateTime.UtcNow;
            }
            else if (_stationarySince != DateTime.MinValue &&
                     DateTime.UtcNow - _stationarySince > TimeSpan.FromSeconds(5))
            {
                var dir = (MirDirection)Random.Next(8);
                await Client.TurnAsync(dir);
                _stationarySince = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - _lastMoveOrAttackTime > TimeSpan.FromSeconds(60) &&
                DateTime.UtcNow >= _nextTownTeleportTime)
            {
                var teleport = Client.FindTownTeleport();
                if (teleport != null)
                {
                    await Client.UseItemAsync(teleport);
                    string name = teleport.Info?.FriendlyName ?? "town teleport";
                    Client.Log($"Used {name} due to inactivity");
                    _nextTownTeleportTime = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                    _lastMoveOrAttackTime = DateTime.UtcNow;
                }
            }

            await Task.Delay(WalkDelay);
        }
    }

    private async Task<bool> HandleReviveAsync()
    {
        if (!Client.Dead) return false;
        _currentTarget = null;
        Client.UpdateAction("reviving");
        if (!_sentRevive)
        {
            await Client.TownReviveAsync();
            _sentRevive = true;
        }
        await Task.Delay(WalkDelay);
        if (!Client.Dead) _sentRevive = false;
        return true;
    }

    private async Task<bool> HandleHarvestingAsync()
    {
        if (!Client.IsHarvesting) return false;
        Client.UpdateAction("harvesting");
        var current = Client.CurrentLocation;
        var target = FindClosestTarget(current, out int dist);
        if (target != null && target.Type == ObjectType.Monster && dist <= 1)
        {
            if (DateTime.UtcNow >= _nextAttackTime)
            {
                var dir = Functions.DirectionFromPoint(current, target.Location);
                await Client.AttackAsync(dir);
                _lastMoveOrAttackTime = DateTime.UtcNow;
                _stationarySince = DateTime.UtcNow; // reset turn timer when attacking during harvest
                _nextAttackTime = DateTime.UtcNow + TimeSpan.FromMilliseconds(AttackDelay);
            }
        }
        await Task.Delay(WalkDelay);
        return true;
    }
}
