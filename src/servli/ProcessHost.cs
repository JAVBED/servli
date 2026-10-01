using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Servli;

public static class ProcessHost
{
    private static string Pipe(ServerMeta meta) => "servli-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Paths.Root + "/" + meta.Name)))[..24].ToLowerInvariant();
    public static bool IsRunning(ServerMeta meta)
    {
        if (meta.ProcessId is null || meta.ProcessStartedUtc is null) return false;
        try
        {
            using var process = Process.GetProcessById(meta.ProcessId.Value);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - meta.ProcessStartedUtc.Value.UtcDateTime).TotalSeconds) < 2;
        }
        catch { return false; }
    }
    public static async Task StartAsync(ServerMeta meta)
    {
        if (IsRunning(meta)) throw new ServliException($"{meta.Name} is already running.");
        if (!File.Exists(Path.Combine(Paths.Content(meta.Name), meta.Executable)) && meta.Executable != "@libraries") throw new ServliException($"Server executable missing: {meta.Executable}");
        if (meta.Provider is not ("bds" or "pocketmine" or "powernukkitx") && PropertiesFile.Get(Path.Combine(Paths.Content(meta.Name), "eula.txt"), "eula") != "true") throw new ServliException($"EULA not accepted. Read https://aka.ms/MinecraftEULA then run 'servli eula {meta.Name}'.");
        if (meta.JavaVersion > 0) await JavaRuntime.EnsureAsync(meta.JavaVersion);
        string self = Environment.ProcessPath ?? throw new ServliException("Cannot locate servli executable.");
        var psi = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.Root };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "servli.dll"));
        psi.ArgumentList.Add("_host"); psi.ArgumentList.Add(meta.Name);
        using var host = Process.Start(psi) ?? throw new ServliException("Could not start server host.");
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            var current = MetaStore.Load(meta.Name);
            if (IsRunning(current))
            {
                await Task.Delay(1000);
                if (!IsRunning(MetaStore.Load(meta.Name))) throw new ServliException($"{meta.Name} exited during startup. Run 'servli logs {meta.Name}' to inspect the error.");
                Console.WriteLine($"Started {meta.Name} (PID {current.ProcessId})."); return;
            }
            if (host.HasExited) throw new ServliException($"Server host exited early. Check {Paths.Logs(meta.Name)}.");
        }
        throw new ServliException("Server did not report a running process within ten seconds. Check logs.");
    }
    public static async Task HostAsync(ServerMeta meta)
    {
        Directory.CreateDirectory(Paths.Logs(meta.Name));
        var psi = new ProcessStartInfo { WorkingDirectory = Paths.Content(meta.Name), UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (meta.JavaVersion > 0)
        {
            psi.FileName = JavaRuntime.Find(meta.JavaVersion) ?? throw new ServliException($"Java {meta.JavaVersion} missing.");
            psi.ArgumentList.Add($"-Xms{meta.Memory}"); psi.ArgumentList.Add($"-Xmx{meta.Memory}");
            foreach (var arg in meta.JvmArgs) psi.ArgumentList.Add(arg);
            if (meta.Executable == "@libraries")
            {
                string suffix = OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";
                string file = Directory.EnumerateFiles(Path.Combine(Paths.Content(meta.Name), "libraries"), suffix, SearchOption.AllDirectories).FirstOrDefault() ?? throw new ServliException("Forge/NeoForge launch arguments missing.");
                psi.ArgumentList.Add("@" + Path.GetRelativePath(Paths.Content(meta.Name), file));
            }
            else { psi.ArgumentList.Add("-jar"); psi.ArgumentList.Add(meta.Executable); }
            psi.ArgumentList.Add("nogui");
        }
        else if (meta.Provider == "pocketmine")
        {
            psi.FileName = await PhpRuntime.EnsureAsync();
            psi.ArgumentList.Add(meta.Executable);
            psi.ArgumentList.Add("--no-wizard");
        }
        else
        {
            psi.FileName = Path.Combine(Paths.Content(meta.Name), meta.Executable);
            if (OperatingSystem.IsLinux()) psi.Environment["LD_LIBRARY_PATH"] = Paths.Content(meta.Name);
        }
        using var process = Process.Start(psi) ?? throw new ServliException("Could not launch server process.");
        meta.ProcessId = process.Id;
        meta.ProcessStartedUtc = process.StartTime.ToUniversalTime();
        MetaStore.Save(meta);
        using var backupCancellation = new CancellationTokenSource();
        var scheduledBackups = BackupSchedule.RunAsync(meta, backupCancellation.Token);
        string log = Path.Combine(Paths.Logs(meta.Name), $"console-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        await using var writer = new StreamWriter(new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var gate = new SemaphoreSlim(1);
        TaskCompletionSource<bool>? saveCompletion = null;
        async Task PumpAsync(StreamReader reader, string prefix)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await gate.WaitAsync();
                try
                {
                    await writer.WriteLineAsync(prefix + line);
                    if (line.Contains("Saved the game", StringComparison.OrdinalIgnoreCase)) saveCompletion?.TrySetResult(true);
                    if (meta.Floodgate && line.Contains("[Geyser-Spigot] Done", StringComparison.Ordinal))
                    {
                        string config = Path.Combine(Paths.Content(meta.Name), "plugins", "Geyser-Spigot", "config.yml");
                        if (File.Exists(config))
                        {
                            string current = File.ReadAllText(config);
                            string updated = System.Text.RegularExpressions.Regex.Replace(current, @"(?m)^(\s*auth-type:\s*)online(\s*)$", "${1}floodgate${2}");
                            if (updated != current)
                            {
                                File.WriteAllText(config, updated);
                                await process.StandardInput.WriteLineAsync("geyser reload");
                                await process.StandardInput.FlushAsync();
                                await writer.WriteLineAsync("[servli] Set Geyser auth-type to floodgate and requested reload.");
                            }
                        }
                    }
                }
                finally { gate.Release(); }
            }
        }
        var stdout = PumpAsync(process.StandardOutput, "");
        var stderr = PumpAsync(process.StandardError, "[stderr] ");
        var listener = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                using var pipe = new NamedPipeServerStream(Pipe(meta), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await pipe.WaitForConnectionAsync(new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token); }
                catch (OperationCanceledException) { continue; }
                using var input = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                using var output = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? command = await input.ReadLineAsync();
                if (command is not null && !command.Contains('\n') && !process.HasExited)
                {
                    if (command == "save-all flush") saveCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    await process.StandardInput.WriteLineAsync(command);
                    await process.StandardInput.FlushAsync();
                    if (command == "save-all flush")
                    {
                        try { await saveCompletion!.Task.WaitAsync(TimeSpan.FromSeconds(30)); await output.WriteLineAsync("OK"); }
                        catch (TimeoutException) { await output.WriteLineAsync("ERR save-all did not confirm completion"); }
                        finally { saveCompletion = null; }
                    }
                    else await output.WriteLineAsync("OK");
                }
            }
        });
        await process.WaitForExitAsync();
        backupCancellation.Cancel();
        await Task.WhenAll(stdout, stderr);
        meta.ProcessId = null; meta.ProcessStartedUtc = null; MetaStore.Save(meta);
        await scheduledBackups;
        var stopSchedule = BackupSchedule.Load(meta.Name);
        if (stopSchedule.Mode == "on-stop")
            try { await Backup.CreateAsync(meta); BackupSchedule.Prune(meta.Name, stopSchedule); }
            catch (Exception error) { Console.Error.WriteLine("On-stop backup failed: " + error.Message); }
        try { await listener.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
    public static async Task SendAsync(ServerMeta meta, string command)
    {
        if (!IsRunning(meta)) throw new ServliException($"{meta.Name} is not running.");
        if (command.Contains('\n') || command.Contains('\r')) throw new ServliException("Console commands must be single-line.");
        using var pipe = new NamedPipeClientStream(".", Pipe(meta), PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(3000); }
        catch { throw new ServliException("Server console is not reachable. Check host logs."); }
        using var output = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var input = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await output.WriteLineAsync(command);
        string? acknowledgement;
        try { acknowledgement = await input.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { throw new ServliException("Server console did not acknowledge command within five seconds."); }
        if (acknowledgement != "OK") throw new ServliException("Server console did not acknowledge command.");
    }
    public static async Task StopAsync(ServerMeta meta)
    {
        if (!IsRunning(meta)) { Console.WriteLine($"{meta.Name} is stopped."); return; }
        await SendAsync(meta, "stop");
        for (int i = 0; i < 60; i++)
        {
            if (!IsRunning(MetaStore.Load(meta.Name))) { Console.WriteLine($"Stopped {meta.Name}."); return; }
            if (i == 20 || i == 40) await SendAsync(meta, "stop");
            await Task.Delay(500);
        }
        using var process = Process.GetProcessById(meta.ProcessId!.Value);
        process.Kill(true);
        Console.WriteLine($"{meta.Name} did not stop within 30 seconds; force terminated.");
    }
}
