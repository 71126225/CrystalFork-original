using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public sealed class MonsterEntry
{
    public string Name { get; set; } = string.Empty;
    public int Damage { get; set; }
    public int RepulseAt { get; set; }
    public List<string> SeenOnMaps { get; set; } = new();
}

public sealed class MonsterMemoryBank : MemoryBankBase<MonsterEntry>
{
    private readonly Dictionary<string, MonsterEntry> _lookup = new();

    public MonsterMemoryBank(string path) : base(path, "Global\\MonsterMemoryBankMutex")
    {
        foreach (var e in _entries)
            _lookup[e.Name] = e;
    }

    protected override void OnLoaded()
    {
        _lookup.Clear();
        foreach (var e in _entries)
            _lookup[e.Name] = e;
    }

    public void AddSeenOnMap(string monsterName, string mapFile)
    {
        bool added = false;
        lock (_lock)
        {
            ReloadIfUpdated();
            var normalizedMap = Path.GetFileNameWithoutExtension(mapFile);
            if (!_lookup.TryGetValue(monsterName, out var entry))
            {
                entry = new MonsterEntry { Name = monsterName };
                _entries.Add(entry);
                _lookup[monsterName] = entry;
                added = true;
            }
            if (!entry.SeenOnMaps.Contains(normalizedMap))
            {
                entry.SeenOnMaps.Add(normalizedMap);
                added = true;
            }
        }
        if (added)
            Save();
    }

    public void RecordDamage(string monsterName, int damage)
    {
        bool changed = false;
        lock (_lock)
        {
            ReloadIfUpdated();
            if (!_lookup.TryGetValue(monsterName, out var entry))
            {
                entry = new MonsterEntry { Name = monsterName, Damage = damage };
                _entries.Add(entry);
                _lookup[monsterName] = entry;
                changed = true;
            }
            else if (damage > entry.Damage)
            {
                entry.Damage = damage;
                changed = true;
            }
        }
        if (changed)
            Save();
    }

    public int GetDamage(string monsterName)
    {
        lock (_lock)
        {
            ReloadIfUpdated();
            return _lookup.TryGetValue(monsterName, out var entry) ? entry.Damage : 0;
        }
    }

    public void RecordRepulseAt(string monsterName, int level)
    {
        bool changed = false;
        lock (_lock)
        {
            ReloadIfUpdated();
            if (!_lookup.TryGetValue(monsterName, out var entry))
            {
                entry = new MonsterEntry { Name = monsterName, RepulseAt = level };
                _entries.Add(entry);
                _lookup[monsterName] = entry;
                changed = true;
            }
            else if (entry.RepulseAt == 0 || level < entry.RepulseAt)
            {
                entry.RepulseAt = level;
                changed = true;
            }
        }
        if (changed)
            Save();
    }

    public int GetRepulseAt(string monsterName)
    {
        lock (_lock)
        {
            ReloadIfUpdated();
            return _lookup.TryGetValue(monsterName, out var entry) ? entry.RepulseAt : 0;
        }
    }
}
