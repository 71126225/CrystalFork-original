using System;
using System.Collections.Generic;
using System.Threading;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Linq;
using Shared;

public sealed class AgentStatus
{
    public ushort Level { get; init; }
    public MirClass? Class { get; init; }
    public int GroupCount { get; init; }
    public string MapFile { get; init; } = string.Empty;
    public string MapName { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public string Action { get; init; } = string.Empty;
    public DateTime CycleStart { get; init; }
}

public interface IAgentLogger
{
    void RegisterAgent(string agent);
    void RemoveAgent(string agent);
    void UpdateStatus(string agent, AgentStatus status);
}

static class AgentStatusFormatter
{
    public static string Format(string name, AgentStatus status)
    {
        var map = Path.GetFileNameWithoutExtension(status.MapFile);
        var cls = status.Class?.ToString() ?? string.Empty;
        if (cls.Length > 2) cls = cls.Substring(0, 2);
        return $"{name} - Lv. {status.Level} ({cls})[{status.GroupCount}] - {map} ({status.MapName}) ({status.X},{status.Y}) - {status.Action}";
    }
}

public sealed class ConsoleAgentLogger : IAgentLogger
{
    public void RegisterAgent(string agent)
    {
        // no-op
    }

    public void RemoveAgent(string agent)
    {
        // no-op
    }

    public void UpdateStatus(string agent, AgentStatus status)
    {
        Console.Error.WriteLine(AgentStatusFormatter.Format(agent, status));
    }
}

public sealed class NullAgentLogger : IAgentLogger
{
    public void RegisterAgent(string agent)
    {
        // no-op
    }

    public void RemoveAgent(string agent)
    {
        // no-op
    }

    public void UpdateStatus(string agent, AgentStatus status)
    {
        // no-op
    }
}

public sealed class SummaryAgentLogger : IAgentLogger, IDisposable
{
    private readonly Dictionary<string, AgentStatus> _status = new();
    private readonly List<string> _order = new();
    private readonly HashSet<string> _registered = new();
    private readonly object _lockObj = new();
    private readonly Timer _timer;
    private readonly CpuMonitor _cpu = new();
    private readonly bool _debug;
    private int _lastLineCount;
    private string? _focusedAgent;

    public SummaryAgentLogger(bool debugEnabled = false)
    {
        _debug = debugEnabled;
        _timer = new Timer(_ =>
        {
            lock (_lockObj)
            {
                Render();
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // CPU monitor initialized above
    }

    public void Dispose() => _timer.Dispose();

    public void RegisterAgent(string agent)
    {
        lock (_lockObj)
        {
            if (_registered.Add(agent))
            {
                _status[agent] = new AgentStatus();
                _order.Add(agent);
                Render();
            }
        }
    }

    public void RemoveAgent(string agent)
    {
        lock (_lockObj)
        {
            if (_registered.Remove(agent) && _status.Remove(agent))
            {
                _order.Remove(agent);
                Render();
            }
        }
    }

    public void UpdateStatus(string agent, AgentStatus status)
    {
        lock (_lockObj)
        {
            if (!_registered.Contains(agent))
                return;

            _status[agent] = status;
        }
    }

    public void FocusAgent(string agent)
    {
        lock (_lockObj)
        {
            _focusedAgent = agent;
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput())
                {
                    AutoFlush = true
                });
                Console.Clear();
            }
            catch { }
        }
    }

    public void EndFocus()
    {
        lock (_lockObj)
        {
            _focusedAgent = null;
            try
            {
                Console.SetOut(TextWriter.Null);
                Console.Clear();
                Render();
            }
            catch { }
        }
    }

    public bool ShouldLog(string agent)
    {
        lock (_lockObj)
        {
            return _focusedAgent == null || _focusedAgent == agent;
        }
    }

    public void SortByCycleTime()
    {
        if (!_debug)
            return;
        lock (_lockObj)
        {
            _order.Sort((a, b) =>
            {
                _status.TryGetValue(a, out var sa);
                _status.TryGetValue(b, out var sb);
                var ta = sa != null ? (DateTime.UtcNow - sa.CycleStart).TotalMilliseconds : 0;
                var tb = sb != null ? (DateTime.UtcNow - sb.CycleStart).TotalMilliseconds : 0;
                return ta.CompareTo(tb);
            });
            Render();
        }
    }

    private void Render()
    {
        if (_focusedAgent != null) return;
        Console.CursorVisible = false;
        int colWidth = Math.Max(20, Console.WindowWidth / 4);
        var lines = new List<string>();
        string currentLine = string.Empty;
        for (int i = 0; i < _order.Count; i++)
        {
            var agent = _order[i];
            _status.TryGetValue(agent, out var status);
            int cycle = (int)(DateTime.UtcNow - status.CycleStart).TotalMilliseconds;
            string name = _debug ? $"{agent}({cycle})" : agent;
            string cell = AgentStatusFormatter.Format(name, status);
            if (cell.Length > colWidth)
                cell = cell.Substring(0, colWidth);
            cell = cell.PadRight(colWidth);
            currentLine += cell;
            if (i % 4 == 3 || i == _order.Count - 1)
            {
                if (currentLine.Length > Console.WindowWidth)
                    currentLine = currentLine.Substring(0, Console.WindowWidth);
                lines.Add(currentLine.PadRight(Console.WindowWidth));
                currentLine = string.Empty;
            }
        }
        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                Console.SetCursorPosition(0, i);
                Console.Error.Write(lines[i]);
            }

            for (int i = lines.Count; i < _lastLineCount; i++)
            {
                Console.SetCursorPosition(0, i);
                Console.Error.Write(new string(' ', Console.WindowWidth));
            }
        }
        catch { }


        var cpuUsage = _cpu.GetCpuUsage();
        Console.Title = $"Agents: {_order.Count} CPU: {cpuUsage:0.0}%";

        _lastLineCount = lines.Count;
    }

    // CPU usage handled by CpuMonitor
}
