using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Servli;

public static class Archive
{
    public static void ExtractZip(string file, string directory)
    {
        Directory.CreateDirectory(directory);
        using var zip = ZipFile.OpenRead(file);
        foreach (var entry in zip.Entries)
        {
            string dest = SafePath(directory, entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
    }
    public static void ExtractTarGz(string file, string directory)
    {
        Directory.CreateDirectory(directory);
        using var source = File.OpenRead(file);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            string dest = SafePath(directory, entry.Name);
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(dest); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var output = File.Create(dest);
            entry.DataStream?.CopyTo(output);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dest, (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }
    public static string SafePath(string root, string entry)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ServliException($"Archive contains unsafe path: {entry}");
        return path;
    }
}

public static class JavaRuntime
{
    public static string? Find(int major)
    {
        string local = Path.Combine(Paths.Runtimes, $"java-{major}", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(local)) return local;
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home) candidates.Add(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"));
        candidates.Add("java");
        foreach (string candidate in candidates)
            if (GetMajor(candidate) == major) return candidate;
        return null;
    }
    public static int? GetMajor(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return null;
            string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var match = System.Text.RegularExpressions.Regex.Match(output, "version \\\"(\\d+)(?:\\.(\\d+))?");
            if (!match.Success) return null;
            int value = int.Parse(match.Groups[1].Value);
            return value == 1 ? int.Parse(match.Groups[2].Value) : value;
        }
        catch { return null; }
    }
    public static async Task<string> EnsureAsync(int major)
    {
        if (major is not (8 or 17 or 21 or 25)) throw new ServliException($"Automatic Java {major} installation is unavailable. Install a compatible Java runtime manually.");
        if (Find(major) is string found) return found;
        string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "mac" : throw new ServliException("Unsupported OS for Java runtime.");
        string arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "aarch64", _ => throw new ServliException("Unsupported architecture for Java runtime.") };
        using var doc = await Net.JsonAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={arch}&image_type=jre&os={os}&vendor=eclipse", false);
        var asset = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (asset.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new ServliException($"Eclipse Temurin has no Java {major} JRE for {os}/{arch}.");
        var package = asset.GetProperty("binary").GetProperty("package");
        string archive = Path.Combine(Paths.Cache, Net.Text(package, "name"));
        if (!File.Exists(archive)) await Net.DownloadAsync(new(new(Net.Text(package, "link")), Path.GetFileName(archive), Net.Text(package, "checksum"), "sha256"), archive);
        else Net.Verify(archive, Net.Text(package, "checksum"), "sha256");
        string temp = Path.Combine(Paths.Runtimes, $"java-{major}.installing");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        if (archive.EndsWith(".zip")) Archive.ExtractZip(archive, temp); else Archive.ExtractTarGz(archive, temp);
        string fileName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        string executable = Directory.EnumerateFiles(temp, fileName, SearchOption.AllDirectories).FirstOrDefault(x => Path.GetFileName(Path.GetDirectoryName(x)) == "bin") ?? throw new ServliException("Downloaded JRE archive has no Java executable.");
        string target = Path.Combine(Paths.Runtimes, $"java-{major}");
        Directory.CreateDirectory(target);
        string nested = Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!;
        foreach (var item in Directory.EnumerateFileSystemEntries(nested))
        {
            string dest = Path.Combine(target, Path.GetFileName(item));
            if (Directory.Exists(item)) { if (Directory.Exists(dest)) Directory.Delete(dest, true); Directory.Move(item, dest); }
            else File.Move(item, dest, true);
        }
        Directory.Delete(temp, true);
        string result = Path.Combine(target, "bin", fileName);
        if (GetMajor(result) != major) throw new ServliException($"Downloaded Java runtime did not report version {major}.");
        return result;
    }
}
