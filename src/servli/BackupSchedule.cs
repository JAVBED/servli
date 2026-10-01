using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Servli;

public sealed class BackupSchedule
{
    public string Mode { get; set; } = "off";
    public int Keep { get; set; } = 10;
    public int MaxAgeDays { get; set; }

    public void Validate()
    {
        if (Mode is not ("off" or "30m" or "hourly" or "daily" or "on-stop") && !Regex.IsMatch(Mode, "^[1-9][0-9]{0,2}h$")) throw new ServliException("Schedule must be off, 30m, hourly, Nh, daily, or on-stop.");
        if (Keep < 1 || Keep > 1000 || MaxAgeDays < 0 || MaxAgeDays > 3650) throw new ServliException("Backup retention is out of range.");
    }

    [JsonIgnore]
    public TimeSpan? Interval => Mode switch
    {
        "30m" => TimeSpan.FromMinutes(30),
        "hourly" => TimeSpan.FromHours(1),
        "daily" => TimeSpan.FromDays(1),
        _ when Mode.EndsWith('h') && int.TryParse(Mode[..^1], out int hours) => TimeSpan.FromHours(hours),
        _ => null
    };

    private static string FilePath(string name) => Path.Combine(Paths.Server(name), "backup-schedule.json");

    public static BackupSchedule Load(string name)
    {
        Paths.ValidateName(name);
        try
        {
            var file = FilePath(name);
            var config = File.Exists(file) ? JsonSerializer.Deserialize<BackupSchedule>(File.ReadAllText(file), MetaStore.Json) : null;
            config ??= new BackupSchedule();
            config.Validate();
            return config;
        }
        catch (Exception error) when (error is JsonException or ServliException or IOException)
        {
            throw new ServliException("Invalid backup schedule for " + name + ": " + error.Message);
        }
    }

    public static void Save(string name, BackupSchedule config)
    {
        Paths.ValidateName(name);
        config.Validate();
        string file = FilePath(name), temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, MetaStore.Json));
        File.Move(temp, file, true);
    }

    public static void Prune(string name, BackupSchedule config)
    {
        var folder = Paths.Backups(name);
        if (!Directory.Exists(folder)) return;
        var files = Directory.EnumerateFiles(folder, "backup-*.zip").OrderByDescending(path => path).ToList();
        foreach (string file in files.Skip(1).Where((path, index) => index + 1 >= config.Keep || config.MaxAgeDays > 0 && File.GetCreationTimeUtc(path) < DateTime.UtcNow.AddDays(-config.MaxAgeDays)))
            File.Delete(file);
    }

    public static async Task RunAsync(ServerMeta meta, CancellationToken token)
    {
        string previousMode = "";
        DateTime nextBackup = DateTime.MaxValue;
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
            catch (OperationCanceledException) { break; }
            try
            {
                var config = Load(meta.Name);
                if (config.Mode != previousMode)
                {
                    previousMode = config.Mode;
                    nextBackup = config.Interval is TimeSpan wait ? DateTime.UtcNow.Add(wait) : DateTime.MaxValue;
                }
                if (config.Interval is not TimeSpan interval || DateTime.UtcNow < nextBackup) continue;
                nextBackup = DateTime.UtcNow.Add(interval);
                await Backup.CreateAsync(MetaStore.Load(meta.Name), allowRestart: false);
                Prune(meta.Name, config);
            }
            catch (Exception error) { Console.Error.WriteLine("Scheduled backup failed: " + error.Message); }
        }
    }
}
