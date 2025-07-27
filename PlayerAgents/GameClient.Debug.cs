using System;
using System.Threading.Tasks;
using C = ClientPackets;

public sealed partial class GameClient
{
    private string? _debugRecipient;
    private bool _debugActive;

    internal void Log(string message)
    {
        Console.WriteLine(message);
        if (_debugActive && !string.IsNullOrEmpty(_debugRecipient))
            FireAndForget(SendWhisperAsync(_debugRecipient, message));
    }

    private async Task SendWhisperAsync(string target, string message)
    {
        if (_stream == null) return;
        await SendAsync(new C.Chat { Message = $"/{target} {message}" });
    }

    private void HandleDebugCommand(string text)
    {
        var parts = text.Split(new[] {"=>"}, 2, StringSplitOptions.None);
        if (parts.Length != 2) return;
        string sender = parts[0];
        string msg = parts[1].Trim();

        if (msg.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            StartDebug(sender);
        }
        else if (msg.Equals("enddebug", StringComparison.OrdinalIgnoreCase))
        {
            StopDebug(sender);
        }
        else if (msg.Equals("inventory", StringComparison.OrdinalIgnoreCase))
        {
            FireAndForget(SendInventoryAsync(sender));
        }
    }

    private void StartDebug(string sender)
    {
        if (_debugActive) return;
        _debugRecipient = sender;
        _debugActive = true;
    }

    private void StopDebug(string sender)
    {
        if (!_debugActive || _debugRecipient != sender) return;
        _debugActive = false;
        _debugRecipient = null;
    }

    private async Task SendInventoryAsync(string target)
    {
        if (_inventory == null) return;

        var groups = _inventory
            .Where(i => i != null && i.Info != null)
            .GroupBy(i => i!.Info!.FriendlyName)
            .Select(g => new { Name = g.Key, Count = g.Sum(i => (int)i!.Count) })
            .OrderBy(g => g.Name);

        foreach (var g in groups)
        {
            await SendWhisperAsync(target, $"{g.Name} x{g.Count}");
            await Task.Delay(500);
        }
    }
}
