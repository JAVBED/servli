using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Servli;

public static class PhpRuntime
{
    public static async Task<string> EnsureAsync()
    {
        string target = Path.Combine(Paths.Runtimes, "pocketmine-php");
        string executable = Path.Combine(target, OperatingSystem.IsWindows() ? "php.exe" : "php");
        if (File.Exists(executable)) return executable;
        using var doc = await Net.JsonAsync("https://api.github.com/repos/pmmp/PHP-Binaries/releases/latest", false);
        string os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "MacOS" : throw new ServliException("PocketMine PHP runtime is unsupported on this OS.");
        string arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? "x86_64" : "x64", Architecture.Arm64 => "arm64", _ => throw new ServliException("PocketMine PHP runtime is unsupported on this architecture.") };
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(x => Net.Text(x, "name").Contains($"-{os}-{arch}-", StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined) throw new ServliException($"PocketMine has no PHP binary for {os}/{arch}.");
        string digest = Net.Text(asset, "digest"), archive = Path.Combine(Paths.Cache, Net.Text(asset, "name"));
        await Net.DownloadAsync(new(new(Net.Text(asset, "browser_download_url")), Path.GetFileName(archive), digest.StartsWith("sha256:") ? digest[7..] : null, "sha256"), archive);
        string temp = target + ".installing";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        if (archive.EndsWith(".zip")) Archive.ExtractZip(archive, temp); else Archive.ExtractTarGz(archive, temp);
        string? found = Directory.EnumerateFiles(temp, Path.GetFileName(executable), SearchOption.AllDirectories).FirstOrDefault();
        if (found is null) throw new ServliException("PocketMine PHP archive did not include php executable.");
        Directory.CreateDirectory(target);
        string sourceRoot = Path.GetDirectoryName(found)!;
        foreach (var item in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            string dest = Path.Combine(target, Path.GetFileName(item));
            if (Directory.Exists(item)) { if (Directory.Exists(dest)) Directory.Delete(dest, true); Directory.Move(item, dest); }
            else File.Move(item, dest, true);
        }
        Directory.Delete(temp, true);
        return executable;
    }
}

public static class Installer
{
    public static async Task<ServerMeta> CreateAsync(string name, string providerId, string requested, bool geyser, bool floodgate)
    {
        Paths.ValidateName(name);
        if (Directory.Exists(Paths.Server(name))) throw new ServliException($"Server '{name}' already exists.");
        if ((geyser || floodgate) && providerId != "paper") throw new ServliException("Geyser/Floodgate creation is currently supported on Paper only.");
        if (floodgate) geyser = true;
        var provider = Providers.Get(providerId);
        var resolution = await provider.ResolveAsync(requested);
        if (resolution.JavaVersion > 0) await JavaRuntime.EnsureAsync(resolution.JavaVersion);
        if (providerId == "pocketmine") await PhpRuntime.EnsureAsync();
        string server = Paths.Content(name);
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(Paths.Logs(name));
        Directory.CreateDirectory(Paths.Backups(name));
        try
        {
            string destination = Path.Combine(server, resolution.Artifact.FileName);
            await Net.DownloadAsync(resolution.Artifact, destination);
            string executable = resolution.Artifact.FileName;
            if (resolution.Kind == "zip")
            {
                Archive.ExtractZip(destination, server);
                File.Delete(destination);
                executable = OperatingSystem.IsWindows() ? "bedrock_server.exe" : "bedrock_server";
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(server, executable), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            else if (resolution.Kind is "quilt-installer" or "maven-installer")
            {
                var args = resolution.Kind == "quilt-installer" ? new[] { "-jar", resolution.Artifact.FileName, "install", "server", resolution.MinecraftVersion, resolution.SoftwareVersion, "--install-dir=.", "--download-server" } : new[] { "-jar", resolution.Artifact.FileName, "--installServer" };
                await RunInstallerAsync(JavaRuntime.Find(resolution.JavaVersion)!, server, args);
                executable = resolution.Kind == "quilt-installer" ? "quilt-server-launch.jar" : "@libraries";
                if (resolution.Kind == "quilt-installer" && !File.Exists(Path.Combine(server, executable))) throw new ServliException("Quilt installer did not create its server launcher.");
            }
            var meta = new ServerMeta { Name = name, Provider = provider.Id, MinecraftVersion = resolution.MinecraftVersion, SoftwareVersion = resolution.SoftwareVersion, JavaVersion = resolution.JavaVersion, Executable = executable, Geyser = geyser, Floodgate = floodgate };
            if (providerId != "pocketmine" && !File.Exists(Path.Combine(server, "server.properties"))) File.WriteAllText(Path.Combine(server, "server.properties"), providerId is "bds" or "powernukkitx" ? "server-name=JAVBED Server\nserver-port=19132\nmax-players=10\n" : "motd=JAVBED Server\nserver-port=25565\nmax-players=20\n");
            if (providerId is not ("bds" or "pocketmine" or "powernukkitx")) File.WriteAllText(Path.Combine(server, "eula.txt"), "# Read https://aka.ms/MinecraftEULA before accepting.\neula=false\n");
            if (geyser) await Addon.InstallAsync(meta, "geyser");
            if (floodgate) await Addon.InstallAsync(meta, "floodgate");
            MetaStore.Save(meta);
            return meta;
        }
        catch
        {
            if (Directory.Exists(Paths.Server(name))) Directory.Delete(Paths.Server(name), true);
            throw;
        }
    }
    public static async Task RunInstallerAsync(string command, string directory, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(command) { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new ServliException("Could not start provider installer.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new ServliException($"Provider installer failed (exit {process.ExitCode}): {((await stderr) + await stdout)[^Math.Min(1000, (await stderr).Length + (await stdout).Length)..]}");
    }
    public static async Task UpdateAsync(ServerMeta meta, string? requestedMinecraft = null)
    {
        if (ProcessHost.IsRunning(meta)) throw new ServliException("Stop the server before updating.");
        if (meta.Provider == "bds" && requestedMinecraft is null) throw new ServliException("BDS updates change the Minecraft version. Run 'servli update <name> --minecraft latest' to request that explicitly.");
        var resolution = await Providers.Get(meta.Provider).ResolveAsync(requestedMinecraft ?? meta.MinecraftVersion);
        if (requestedMinecraft is null && resolution.MinecraftVersion != meta.MinecraftVersion) throw new ServliException("Update resolution changed the configured Minecraft version.");
        if (resolution.SoftwareVersion == meta.SoftwareVersion) { Console.WriteLine($"{meta.Name} is up to date ({meta.SoftwareVersion})."); return; }
        Console.WriteLine($"{meta.Name}: {meta.SoftwareVersion} → {resolution.SoftwareVersion} ({meta.MinecraftVersion})");
        string backup = await Backup.CreateAsync(meta);
        string server = Paths.Content(meta.Name);
        string temp = Path.Combine(Paths.Server(meta.Name), "update.download");
        await Net.DownloadAsync(resolution.Artifact, temp);
        try
        {
            if (resolution.Kind is "jar" or "phar")
            {
                string dest = Path.Combine(server, meta.Executable);
                File.Move(temp, dest, true);
            }
            else if (resolution.Kind is "quilt-installer" or "maven-installer")
            {
                File.Move(temp, Path.Combine(server, resolution.Artifact.FileName), true);
                var args = resolution.Kind == "quilt-installer" ? new[] { "-jar", resolution.Artifact.FileName, "install", "server", resolution.MinecraftVersion, resolution.SoftwareVersion, "--install-dir=.", "--download-server" } : new[] { "-jar", resolution.Artifact.FileName, "--installServer" };
                await RunInstallerAsync(await JavaRuntime.EnsureAsync(resolution.JavaVersion), server, args);
            }
            else if (resolution.Kind == "zip")
            {
                string stage = Path.Combine(Paths.Server(meta.Name), "update-stage");
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Archive.ExtractZip(temp, stage);
                foreach (string source in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(stage, source);
                    if (relative is "server.properties" or "allowlist.json" or "permissions.json" || relative.StartsWith("worlds" + Path.DirectorySeparatorChar) || relative.StartsWith("behavior_packs" + Path.DirectorySeparatorChar) || relative.StartsWith("resource_packs" + Path.DirectorySeparatorChar)) continue;
                    string dest = Path.Combine(server, relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(source, dest, true);
                }
                Directory.Delete(stage, true);
                File.Delete(temp);
            }
            meta.SoftwareVersion = resolution.SoftwareVersion;
            meta.MinecraftVersion = resolution.MinecraftVersion;
            meta.JavaVersion = resolution.JavaVersion;
            MetaStore.Save(meta);
        }
        catch
        {
            Backup.Restore(meta, Path.GetFileName(backup), true);
            throw;
        }
    }
}

public static class PropertiesFile
{
    public static string? Get(string file, string key) => File.Exists(file) ? File.ReadAllLines(file).Select(x => x.Trim()).Where(x => !x.StartsWith('#') && !x.StartsWith('!')).Select(x => x.Split('=', 2)).Where(x => x.Length == 2 && x[0].Trim() == key).Select(x => x[1]).LastOrDefault() : null;
    public static void Set(string file, string key, string value)
    {
        if (!Regex.IsMatch(key, "^[A-Za-z0-9_.-]+$")) throw new ServliException("Invalid property key.");
        if (value.Contains('\n') || value.Contains('\r')) throw new ServliException("Property value cannot contain newlines.");
        if (key is "max-players" or "server-port" or "server-portv6" or "view-distance" && (!int.TryParse(value, out int number) || number < 0 || number > 65535)) throw new ServliException($"{key} must be a nonnegative number up to 65535.");
        if (key is "online-mode" or "pvp" or "white-list" && value is not ("true" or "false")) throw new ServliException($"{key} must be true or false.");
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        int index = lines.FindLastIndex(x => !x.TrimStart().StartsWith('#') && x.Split('=', 2)[0].Trim() == key);
        if (index < 0) lines.Add($"{key}={value}"); else lines[index] = $"{key}={value}";
        File.WriteAllLines(file, lines);
    }
}

public static class Backup
{
    public static string Name(DateTimeOffset time) => $"backup-{time.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.zip";
    public static async Task<string> CreateAsync(ServerMeta meta)
    {
        bool running = ProcessHost.IsRunning(meta), saveOff = false;
        if (running)
        {
            if (meta.Provider is "bds" or "pocketmine" or "powernukkitx") throw new ServliException("Consistent live backup is not supported for this provider. Stop the server first.");
            try
            {
                await ProcessHost.SendAsync(meta, "save-off"); saveOff = true;
                await ProcessHost.SendAsync(meta, "save-all flush");
            }
            catch
            {
                if (saveOff) await ProcessHost.SendAsync(meta, "save-on");
                throw;
            }
        }
        Directory.CreateDirectory(Paths.Backups(meta.Name));
        string file = Path.Combine(Paths.Backups(meta.Name), Name(DateTimeOffset.UtcNow));
        bool retryStopped = false;
        try
        {
            try { WriteArchive(meta, file); }
            catch (IOException) when (running) { retryStopped = true; if (File.Exists(file)) File.Delete(file); }
        }
        finally
        {
            if (saveOff && ProcessHost.IsRunning(meta)) await ProcessHost.SendAsync(meta, "save-on");
        }
        if (retryStopped)
        {
            Console.WriteLine("Live world files are locked; stopping server for a consistent backup, then restarting it.");
            await ProcessHost.StopAsync(meta);
            try { WriteArchive(meta, file); }
            finally { await ProcessHost.StartAsync(MetaStore.Load(meta.Name)); }
        }
        return file;
    }
    private static void WriteArchive(ServerMeta meta, string file)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var source in Directory.EnumerateFiles(Paths.Content(meta.Name), "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Paths.Content(meta.Name), source);
            if (relative.StartsWith("logs" + Path.DirectorySeparatorChar) || relative.EndsWith(".part") || relative.EndsWith(".lock")) continue;
            zip.CreateEntryFromFile(source, relative, CompressionLevel.Optimal);
        }
    }
    public static void Restore(ServerMeta meta, string backup, bool yes)
    {
        if (ProcessHost.IsRunning(meta)) throw new ServliException("Stop the server before restoring.");
        string file = Path.Combine(Paths.Backups(meta.Name), backup);
        if (Path.GetFileName(backup) != backup || !File.Exists(file)) throw new ServliException("Backup not found.");
        if (!yes) throw new ServliException("Restore overwrites current server data. Run again with --yes to confirm.");
        string target = Paths.Content(meta.Name), old = target + ".before-restore";
        if (Directory.Exists(old)) Directory.Delete(old, true);
        Directory.Move(target, old);
        try { Archive.ExtractZip(file, target); Directory.Delete(old, true); }
        catch { if (Directory.Exists(target)) Directory.Delete(target, true); Directory.Move(old, target); throw; }
    }
}

public static class Addon
{
    public static async Task InstallAsync(ServerMeta meta, string addon)
    {
        if (meta.Provider != "paper" && meta.Provider != "purpur") throw new ServliException("Geyser/Floodgate requires Paper or Purpur.");
        if (addon is not ("geyser" or "floodgate")) throw new ServliException("Unknown addon.");
        if (addon == "floodgate" && !meta.Geyser) await InstallAsync(meta, "geyser");
        using var doc = await Net.JsonAsync($"https://download.geysermc.org/v2/projects/{addon}/versions/latest/builds/latest", false);
        var root = doc.RootElement;
        var artifact = root.GetProperty("downloads").GetProperty("spigot");
        string url = $"https://download.geysermc.org/v2/projects/{addon}/versions/latest/builds/latest/downloads/spigot";
        string folder = Path.Combine(Paths.Content(meta.Name), "plugins");
        await Net.DownloadAsync(new(new(url), Net.Text(artifact, "name"), Net.Text(artifact, "sha256"), "sha256"), Path.Combine(folder, Net.Text(artifact, "name")));
        if (addon == "geyser" && Version.TryParse(meta.MinecraftVersion, out var minecraft) && minecraft < new Version(26, 2))
            await Modrinth.InstallAsync(meta, "viaversion");
        if (addon == "geyser") meta.Geyser = true; else meta.Floodgate = true;
        if (File.Exists(Paths.Metadata(meta.Name))) MetaStore.Save(meta);
    }
    public static void Remove(ServerMeta meta, string addon)
    {
        if (addon is not ("geyser" or "floodgate")) throw new ServliException("Unknown addon.");
        if (addon == "geyser" && meta.Floodgate) throw new ServliException("Remove Floodgate before removing Geyser.");
        string file = Path.Combine(Paths.Content(meta.Name), "plugins", addon == "geyser" ? "Geyser-Spigot.jar" : "floodgate-spigot.jar");
        if (File.Exists(file)) File.Delete(file);
        if (addon == "floodgate")
        {
            string config = Path.Combine(Paths.Content(meta.Name), "plugins", "Geyser-Spigot", "config.yml");
            if (File.Exists(config)) File.WriteAllText(config, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(config), @"(?m)^(\s*auth-type:\s*)floodgate(\s*)$", "${1}online${2}"));
        }
        if (addon == "geyser") meta.Geyser = false; else meta.Floodgate = false;
        MetaStore.Save(meta);
    }
}
