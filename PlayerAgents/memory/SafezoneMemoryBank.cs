using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

public sealed class SafezoneEntry
{
    public string Map { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Size { get; set; }
}

public sealed class SafezoneMemoryBank : MemoryBankBase<SafezoneEntry>
{
    private readonly HashSet<string> _keys = new();

    public SafezoneMemoryBank(string path) : base(path, "Global\\SafezoneMemoryBankMutex")
    {
        foreach (var e in _entries)
            _keys.Add(Key(e.Map, e.X, e.Y));
    }

    protected override void OnLoaded()
    {
        _keys.Clear();
        foreach (var e in _entries)
            _keys.Add(Key(e.Map, e.X, e.Y));
    }

    private static string Key(string map, int x, int y) => $"{map}:{x}:{y}";

    public void AddSafezone(string mapFile, Point location, int size)
    {
        if (size <= 0) return;
        bool added = false;
        lock (_lock)
        {
            ReloadIfUpdated();
            var map = Path.GetFileNameWithoutExtension(mapFile);
            var key = Key(map, location.X, location.Y);
            if (!_keys.Contains(key))
            {
                _entries.Add(new SafezoneEntry
                {
                    Map = map,
                    X = location.X,
                    Y = location.Y,
                    Size = size
                });
                _keys.Add(key);
                added = true;
            }
        }

        if (added)
            Save();
    }

    public bool HasSafezone(string mapFile, Point location)
    {
        var map = Path.GetFileNameWithoutExtension(mapFile);
        var key = Key(map, location.X, location.Y);
        lock (_lock)
        {
            ReloadIfUpdated();
            return _keys.Contains(key);
        }
    }
}
