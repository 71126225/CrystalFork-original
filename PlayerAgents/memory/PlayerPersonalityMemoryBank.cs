using System;
using System.IO;
using System.Text.Json;
using System.Threading;

public sealed class PlayerPersonality
{
    public int MaxGroupCount { get; set; }
}

public sealed class PlayerPersonalityMemoryBank
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public PlayerPersonalityMemoryBank(string directory)
    {
        _directory = directory;
    }

    public PlayerPersonality Load(string playerName)
    {
        var file = Path.Combine(_directory, playerName + ".json");
        Directory.CreateDirectory(_directory);
        var mutexName = $"Global\\PlayerPersonalityMemoryBank_{playerName}";
        using var mutex = new Mutex(false, mutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Failed to acquire file mutex");
        }
        catch (AbandonedMutexException)
        {
        }

        try
        {
            if (File.Exists(file))
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var personality = JsonSerializer.Deserialize<PlayerPersonality>(fs, _options);
                if (personality != null)
                    return personality;
            }

            var newPersonality = new PlayerPersonality { MaxGroupCount = Random.Shared.Next(1, 12) };
            string json = JsonSerializer.Serialize(newPersonality, _options);
            File.WriteAllText(file, json);
            return newPersonality;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public void Save(string playerName, PlayerPersonality personality)
    {
        var file = Path.Combine(_directory, playerName + ".json");
        Directory.CreateDirectory(_directory);
        var mutexName = $"Global\\PlayerPersonalityMemoryBank_{playerName}";
        using var mutex = new Mutex(false, mutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Failed to acquire file mutex");
        }
        catch (AbandonedMutexException)
        {
        }

        try
        {
            string json = JsonSerializer.Serialize(personality, _options);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(file))
                File.Replace(tmp, file, null);
            else
                File.Move(tmp, file);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
