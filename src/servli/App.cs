using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Servli;

public sealed class App
{
    public async Task RunAsync(string[] raw)
    {
        var args = raw.Where(x => x != "--debug").ToArray();
        if (args.Length == 0) { Help(); return; }
        string cmd = args[0].ToLowerInvariant();
        if (cmd != "_host" && cmd != "update-self") await SelfUpdate.CheckAsync();
        switch (cmd)
        {
            case "help": case "--help": case "-h": Help(); break;
            case "providers": foreach (var p in Providers.All) Console.WriteLine($"{p.Id,-16} {p.DisplayName}"); break;
            case "versions": await VersionsAsync(Arg(args, 1)); break;
            case "create": await CreateAsync(args); break;
            case "list": List(); break;
            case "info": Info(MetaStore.Load(Arg(args, 1))); break;
            case "start": await ProcessHost.StartAsync(MetaStore.Load(Arg(args, 1))); break;
            case "stop": await ProcessHost.StopAsync(MetaStore.Load(Arg(args, 1))); break;
            case "restart": { var meta = MetaStore.Load(Arg(args, 1)); await ProcessHost.StopAsync(meta); await ProcessHost.StartAsync(MetaStore.Load(meta.Name)); break; }
            case "status": { var meta = MetaStore.Load(Arg(args, 1)); Console.WriteLine($"{meta.Name}: {(ProcessHost.IsRunning(meta) ? $"RUNNING (PID {meta.ProcessId})" : "STOPPED")}"); break; }
            case "console": await ConsoleAsync(MetaStore.Load(Arg(args, 1))); break;
            case "send": { var meta = MetaStore.Load(Arg(args, 1)); await ProcessHost.SendAsync(meta, string.Join(' ', args.Skip(2))); Console.WriteLine("Command sent."); break; }
            case "logs": await LogsAsync(MetaStore.Load(Arg(args, 1)), args.Contains("--follow")); break;
            case "eula": Eula(MetaStore.Load(Arg(args, 1))); break;
            case "properties": Properties(args); break;
            case "memory": Memory(args); break;
            case "backup": { var meta = MetaStore.Load(Arg(args, 1)); Console.WriteLine("Backup created: " + Path.GetFileName(await Backup.CreateAsync(meta))); break; }
            case "backups": Backups(MetaStore.Load(Arg(args, 1))); break;
            case "restore": { var meta = MetaStore.Load(Arg(args, 1)); Backup.Restore(meta, Arg(args, 2), args.Contains("--yes")); Console.WriteLine("Restored."); break; }
            case "delete-backup": DeleteBackup(args); break;
            case "update": await UpdateAsync(args); break;
            case "addon": await AddonAsync(args); break;
            case "plugin": case "mod": await PackageAsync(args, cmd); break;
            case "delete": Delete(args); break;
            case "java": await JavaAsync(args); break;
            case "doctor": await DoctorAsync(args.Length > 1 ? args[1] : null); break;
            case "update-self": await SelfUpdate.UpdateAsync(); break;
            case "_host": await ProcessHost.HostAsync(MetaStore.Load(Arg(args, 1))); break;
            default: throw new ServliException($"Unknown command '{cmd}'. Run 'servli help'.");
        }
    }
    private static string Arg(string[] args, int index) => args.Length > index ? args[index] : throw new ServliException($"Missing argument. Run 'servli help'.");
    private static void Help() => Console.WriteLine("""
        servli — Minecraft server manager by JAVBED

        servli providers                 List server implementations
        servli versions <provider>       List Minecraft or software versions
        servli create <name> <provider> <version|latest> [--geyser] [--floodgate]
        servli list | info <name> | doctor [name]
        servli start|stop|restart|status|console <name> | send <name> <command...>
        servli logs <name> [--follow] | eula <name>
        servli properties <name> [key] [value] | memory <name> <size>
        servli backup <name> | backups <name>
        servli restore <name> <backup> --yes | delete-backup <name> <backup>
        servli update <name>|--all | update-self
        servli addon <name> geyser|floodgate|remove <addon>
        servli plugin|mod search <name> <query>
        servli plugin|mod install <name> <local-file|Modrinth-project>
        servli java list|install <8|17|21|25>|path <version>
        servli delete <name> --yes

        Add --debug to any command for stack traces.
        """);
    private static async Task VersionsAsync(string provider)
    {
        var p = Providers.Get(provider);
        var versions = await p.GetVersionsAsync();
        Console.WriteLine($"{p.DisplayName}: {versions.Count} versions");
        foreach (var version in versions) Console.WriteLine(version);
    }
    private static async Task CreateAsync(string[] args)
    {
        var meta = await Installer.CreateAsync(Arg(args, 1), Arg(args, 2), Arg(args, 3), args.Contains("--geyser"), args.Contains("--floodgate"));
        Console.WriteLine($"Server created: {meta.Name}\nSoftware: {Providers.Get(meta.Provider).DisplayName} {meta.SoftwareVersion}\nMinecraft: {meta.MinecraftVersion}\nJava: {(meta.JavaVersion == 0 ? "not required" : meta.JavaVersion)}\nMemory: {meta.Memory}");
        if (meta.JavaVersion != 0 && meta.Provider != "powernukkitx") Console.WriteLine($"\nEULA has not been accepted.\nRead https://aka.ms/MinecraftEULA, then run:\nservli eula {meta.Name}");
        if (meta.Geyser) Console.WriteLine("Java: TCP 25565   Bedrock: UDP 19132 (configure Geyser after first launch)");
    }
    private static void List()
    {
        int width = Console.IsOutputRedirected ? 80 : Console.WindowWidth;
        int nameWidth = width < 70 ? 12 : 18, softwareWidth = width < 70 ? 12 : 16, minecraftWidth = width < 70 ? 10 : 12, statusWidth = width < 70 ? 8 : 10;
        Console.WriteLine($"{"NAME".PadRight(nameWidth)} {"SOFTWARE".PadRight(softwareWidth)} {"MINECRAFT".PadRight(minecraftWidth)} {"STATUS".PadRight(statusWidth)} MEMORY");
        Console.WriteLine(new string('-', Math.Min(width, nameWidth + softwareWidth + minecraftWidth + statusWidth + 10)));
        foreach (string name in MetaStore.Names())
        {
            try { var m = MetaStore.Load(name); Console.WriteLine($"{Clip(name,nameWidth).PadRight(nameWidth)} {Clip(m.Provider,softwareWidth).PadRight(softwareWidth)} {Clip(m.MinecraftVersion,minecraftWidth).PadRight(minecraftWidth)} {(ProcessHost.IsRunning(m) ? "RUNNING" : "STOPPED").PadRight(statusWidth)} {(m.JavaVersion == 0 ? "-" : m.Memory)}"); }
            catch { Console.WriteLine($"{Clip(name,nameWidth).PadRight(nameWidth)} CORRUPT"); }
        }
    }
    private static string Clip(string value, int width) => value.Length <= width ? value : value[..(width - 1)] + "…";
    private static void Info(ServerMeta m)
    {
        Console.WriteLine($"Name: {m.Name}\nSoftware: {Providers.Get(m.Provider).DisplayName} {m.SoftwareVersion}\nMinecraft: {m.MinecraftVersion}\nStatus: {(ProcessHost.IsRunning(m) ? "RUNNING" : "STOPPED")}\nJava: {(m.JavaVersion == 0 ? "not required" : m.JavaVersion)}\nMemory: {m.Memory}\nCreated: {m.CreatedUtc:u}\nData: {Paths.Server(m.Name)}\nGeyser: {m.Geyser}\nFloodgate: {m.Floodgate}");
    }
    private static async Task ConsoleAsync(ServerMeta m)
    {
        if (!ProcessHost.IsRunning(m)) throw new ServliException($"{m.Name} is not running.");
        Console.WriteLine($"Attached to {m.Name}. Enter server commands; Ctrl+C to detach.");
        using var cancellation = new CancellationTokenSource();
        var tail = Task.Run(async () =>
        {
            string? file = Directory.EnumerateFiles(Paths.Logs(m.Name), "console-*.log").OrderByDescending(x => x).FirstOrDefault();
            if (file is null) return;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(0, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            while (!cancellation.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync();
                if (line is null) await Task.Delay(200, cancellation.Token);
                else Console.WriteLine(line);
            }
        }, cancellation.Token);
        while (Console.ReadLine() is { } line)
        {
            if (line is "exit" or "detach") break;
            if (line.Length > 0) await ProcessHost.SendAsync(m, line);
        }
        cancellation.Cancel();
        try { await tail; } catch (OperationCanceledException) { }
    }
    private static async Task LogsAsync(ServerMeta m, bool follow)
    {
        var file = Directory.Exists(Paths.Logs(m.Name)) ? Directory.EnumerateFiles(Paths.Logs(m.Name), "console-*.log").OrderByDescending(x => x).FirstOrDefault() : null;
        if (file is null) { Console.WriteLine("No logs yet."); return; }
        using var initialStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var initialReader = new StreamReader(initialStream);
        var lines = (await initialReader.ReadToEndAsync()).Split('\n'); foreach (var line in lines.TakeLast(100)) if (line.Length > 0) Console.WriteLine(line.TrimEnd('\r'));
        if (!follow) return;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(0, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        while (true) { string? line = await reader.ReadLineAsync(); if (line is null) await Task.Delay(250); else Console.WriteLine(line); }
    }
    private static void Eula(ServerMeta m)
    {
        if (m.Provider is "bds" or "pocketmine" or "powernukkitx") throw new ServliException("This provider does not use the Java Edition eula.txt file. Review its upstream terms before starting.");
        Console.WriteLine("Minecraft EULA: https://aka.ms/MinecraftEULA");
        Console.Write("Have you read and agreed to it? Type 'yes': ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() != "yes") { Console.WriteLine("EULA remains unaccepted."); return; }
        PropertiesFile.Set(Path.Combine(Paths.Content(m.Name), "eula.txt"), "eula", "true");
        Console.WriteLine("EULA accepted.");
    }
    private static void Properties(string[] args)
    {
        var m = MetaStore.Load(Arg(args, 1));
        string file = Path.Combine(Paths.Content(m.Name), "server.properties");
        if (args.Length == 2) { if (File.Exists(file)) Console.Write(File.ReadAllText(file)); else Console.WriteLine("No server.properties yet."); return; }
        string key = args[2];
        if (args.Length == 3) { Console.WriteLine(PropertiesFile.Get(file, key) ?? "(unset)"); return; }
        PropertiesFile.Set(file, key, string.Join(' ', args.Skip(3)));
        Console.WriteLine($"{key}={PropertiesFile.Get(file, key)}");
    }
    private static void Memory(string[] args)
    {
        var m = MetaStore.Load(Arg(args, 1));
        if (m.JavaVersion == 0) throw new ServliException("Memory setting applies to Java servers.");
        string value = Arg(args, 2).ToUpperInvariant();
        if (!Regex.IsMatch(value, "^[1-9][0-9]{0,3}[MG]$")) throw new ServliException("Use a JVM memory value such as 2G or 4096M.");
        m.Memory = value; MetaStore.Save(m); Console.WriteLine($"{m.Name} memory: {value}");
    }
    private static void Backups(ServerMeta m)
    {
        if (!Directory.Exists(Paths.Backups(m.Name))) return;
        foreach (var file in Directory.EnumerateFiles(Paths.Backups(m.Name), "*.zip").OrderByDescending(x => x)) Console.WriteLine($"{Path.GetFileName(file),-43} {Net.FormatSize(new FileInfo(file).Length)}");
    }
    private static void DeleteBackup(string[] args)
    {
        var m = MetaStore.Load(Arg(args, 1)); string name = Arg(args, 2);
        if (Path.GetFileName(name) != name) throw new ServliException("Invalid backup name.");
        string file = Path.Combine(Paths.Backups(m.Name), name);
        if (!File.Exists(file)) throw new ServliException("Backup not found.");
        File.Delete(file); Console.WriteLine($"Deleted backup {name}.");
    }
    private static async Task UpdateAsync(string[] args)
    {
        if (Arg(args, 1) == "--all")
        {
            bool failed = false;
            foreach (string name in MetaStore.Names())
                try { await Installer.UpdateAsync(MetaStore.Load(name)); }
                catch (Exception ex) { Console.Error.WriteLine($"ERROR: {name}: {ex.Message}"); failed = true; }
            if (failed) Environment.ExitCode = 1;
        }
        else
        {
            int mcIndex = Array.IndexOf(args, "--minecraft");
            await Installer.UpdateAsync(MetaStore.Load(args[1]), mcIndex >= 0 ? Arg(args, mcIndex + 1) : null);
        }
    }
    private static async Task AddonAsync(string[] args)
    {
        var m = MetaStore.Load(Arg(args, 1));
        if (ProcessHost.IsRunning(m)) throw new ServliException("Stop the server before changing addons.");
        if (Arg(args, 2) == "remove") Addon.Remove(m, Arg(args, 3));
        else await Addon.InstallAsync(m, args[2]);
        Console.WriteLine("Addon configuration changed. Review plugin configuration after first launch.");
    }
    private static async Task PackageAsync(string[] args, string command)
    {
        string action = Arg(args, 1);
        var m = MetaStore.Load(Arg(args, 2));
        if (action == "search") { await Modrinth.SearchAsync(m, string.Join(' ', args.Skip(3))); return; }
        if (action != "install") throw new ServliException("Usage: servli plugin|mod search|install <server> <query|file|project>.");
        string target = Arg(args, 3), source = Path.GetFullPath(target);
        if (!File.Exists(source)) { await Modrinth.InstallAsync(m, target); return; }
        string folder = command == "mod" ? "mods" : "plugins";
        if (command == "mod" && !Providers.IsMod(m.Provider) || command == "plugin" && !Providers.IsPlugin(m.Provider)) throw new ServliException($"{m.Provider} does not use {folder}.");
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension != ".jar" && !(m.Provider == "pocketmine" && extension == ".phar")) throw new ServliException("Package must be a .jar (PocketMine: .phar).");
        string destDir = Path.Combine(Paths.Content(m.Name), folder); Directory.CreateDirectory(destDir);
        string dest = Path.Combine(destDir, Path.GetFileName(source));
        if (File.Exists(dest)) throw new ServliException("Package already exists in server directory.");
        await using var from = File.OpenRead(source); await using var to = File.Create(dest); await from.CopyToAsync(to);
        Console.WriteLine($"Installed {Path.GetFileName(source)}. Restart server to load it.");
    }
    private static void Delete(string[] args)
    {
        var m = MetaStore.Load(Arg(args, 1));
        if (ProcessHost.IsRunning(m)) throw new ServliException("Stop the server before deleting.");
        if (!args.Contains("--yes")) throw new ServliException("Deletion removes server and backups. Run again with --yes to confirm.");
        Directory.Delete(Paths.Server(m.Name), true); Console.WriteLine($"Deleted {m.Name}.");
    }
    private static async Task JavaAsync(string[] args)
    {
        switch (Arg(args, 1))
        {
            case "list": foreach (int major in new[] { 8, 17, 21, 25 }) Console.WriteLine($"Java {major,-2} {JavaRuntime.Find(major) ?? "not installed"}"); break;
            case "install": case "path": { int major = int.Parse(Arg(args, 2)); Console.WriteLine(await JavaRuntime.EnsureAsync(major)); break; }
            default: throw new ServliException("Usage: servli java list|install <version>|path <version>");
        }
    }
    private static async Task DoctorAsync(string? name)
    {
        try { Directory.CreateDirectory(Paths.Root); string temp = Path.Combine(Paths.Root, ".write-test"); File.WriteAllText(temp, "ok"); File.Delete(temp); Console.WriteLine("✓ Data directory writable"); }
        catch (Exception e) { Console.WriteLine($"✗ Data directory: {e.Message}"); }
        var drive = new DriveInfo(Path.GetPathRoot(Paths.Root)!);
        Console.WriteLine(drive.AvailableFreeSpace > 512L * 1024 * 1024 ? $"✓ Free disk: {Net.FormatSize(drive.AvailableFreeSpace)}" : "⚠ Low disk space (<512 MB)");
        foreach (int major in new[] { 8, 17, 21, 25 }) if (JavaRuntime.Find(major) is not null) Console.WriteLine($"✓ Java {major}");
        try { using var response = await Net.Client.GetAsync("https://fill.papermc.io/v3/projects/paper", HttpCompletionOption.ResponseHeadersRead); Console.WriteLine(response.IsSuccessStatusCode ? "✓ Paper metadata API reachable" : $"⚠ Paper metadata API returned {(int)response.StatusCode}"); }
        catch (Exception ex) { Console.WriteLine($"⚠ Paper metadata API unavailable: {ex.Message}"); }
        if (name is null)
        {
            if (Directory.Exists(Paths.Servers))
                foreach (var folder in Directory.EnumerateDirectories(Paths.Servers))
                    if (File.Exists(Path.Combine(folder, "servli.json")))
                        try { MetaStore.Load(Path.GetFileName(folder)); }
                        catch (Exception ex) { Console.WriteLine($"✗ {Path.GetFileName(folder)} metadata: {ex.Message}"); }
            return;
        }
        var m = MetaStore.Load(name); string content = Paths.Content(name);
        Console.WriteLine(File.Exists(Path.Combine(content, m.Executable)) || m.Executable == "@libraries" ? "✓ Server executable" : "✗ Server executable missing — run update or recreate from backup");
        if (m.JavaVersion > 0) Console.WriteLine(JavaRuntime.Find(m.JavaVersion) is not null ? $"✓ Java {m.JavaVersion}" : $"✗ Java {m.JavaVersion} unavailable — run servli java install {m.JavaVersion}");
        if (m.Provider is not ("bds" or "pocketmine" or "powernukkitx")) Console.WriteLine(PropertiesFile.Get(Path.Combine(content, "eula.txt"), "eula") == "true" ? "✓ EULA" : $"⚠ EULA not accepted — run servli eula {name}");
        Console.WriteLine(File.Exists(Path.Combine(content, "server.properties")) ? "✓ server.properties" : "⚠ server.properties missing");
        if (m.ProcessId is not null && !ProcessHost.IsRunning(m)) Console.WriteLine("⚠ Stale process information — server is not running");
        if (m.Provider is "forge" or "neoforge" && !Directory.Exists(Path.Combine(content, "libraries"))) Console.WriteLine("✗ Mod loader libraries missing — reinstall or restore");
        if (m.Provider == "pocketmine" && !File.Exists(Path.Combine(Paths.Runtimes, "pocketmine-php", OperatingSystem.IsWindows() ? "php.exe" : "php"))) Console.WriteLine("✗ PocketMine PHP runtime missing — create a new PocketMine server to reinstall it");
        if (m.Geyser && !File.Exists(Path.Combine(content, "plugins", "Geyser-Spigot.jar"))) Console.WriteLine("✗ Geyser plugin missing — run servli addon " + name + " geyser");
        if (m.Floodgate && !File.Exists(Path.Combine(content, "plugins", "floodgate-spigot.jar"))) Console.WriteLine("✗ Floodgate plugin missing — run servli addon " + name + " floodgate");
        if (m.Floodgate)
        {
            string config = Path.Combine(content, "plugins", "Geyser-Spigot", "config.yml");
            if (File.Exists(config) && !File.ReadAllText(config).Contains("auth-type: floodgate")) Console.WriteLine("⚠ Geyser auth-type is not floodgate — set it in plugins/Geyser-Spigot/config.yml");
        }
        bool bedrock = m.Provider is "bds" or "powernukkitx" or "pocketmine";
        int port = int.TryParse(PropertiesFile.Get(Path.Combine(content, "server.properties"), "server-port"), out int parsed) ? parsed : bedrock ? 19132 : 25565;
        if (ProcessHost.IsRunning(m)) Console.WriteLine($"✓ Server running on configured port {port}");
        else try { using var socket = new Socket(AddressFamily.InterNetwork, bedrock ? SocketType.Dgram : SocketType.Stream, bedrock ? ProtocolType.Udp : ProtocolType.Tcp); socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port)); Console.WriteLine($"✓ Port {port} available"); }
        catch (SocketException) { Console.WriteLine($"⚠ Port {port} in use — check other servers or server.properties"); }
    }
}
