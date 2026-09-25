using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Servli;

public static class SelfUpdate
{
    private const string ReleaseApi = "https://api.github.com/repos/JAVBED/servli/releases/latest";
    private static Version CurrentVersion => typeof(SelfUpdate).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static async Task CheckAsync()
    {
        if (!IsStandalone() || Console.IsInputRedirected || Console.IsOutputRedirected) return;
        string stamp = Path.Combine(Paths.Cache, "self-update-check.txt");
        if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromHours(24)) return;
        Directory.CreateDirectory(Paths.Cache);
        File.WriteAllText(stamp, DateTimeOffset.UtcNow.ToString("O"));
        try
        {
            var release = await LatestAsync();
            if (release.version is null || !Version.TryParse(release.version.TrimStart('v'), out var latest) || latest <= CurrentVersion) return;
            Console.WriteLine($"\nservli update available\nCurrent: v{CurrentVersion.ToString(3)}   Latest: {release.version}\n[1] Update now  [2] Continue");
            if (Console.ReadLine()?.Trim() == "1") await UpdateAsync();
        }
        catch { /* Update notification must not block server management. */ }
    }
    public static async Task UpdateAsync()
    {
        if (!IsStandalone()) throw new ServliException("Source/framework-dependent builds cannot update themselves. Install a release archive instead.");
        var (version, artifact) = await LatestAsync();
        if (version is null || artifact is null) throw new ServliException("No compatible official release archive was found.");
        string temp = Path.Combine(Paths.Cache, artifact.FileName);
        await Net.DownloadAsync(artifact, temp);
        string staging = Path.Combine(Paths.Cache, "self-update-stage");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        if (temp.EndsWith(".zip")) Archive.ExtractZip(temp, staging); else Archive.ExtractTarGz(temp, staging);
        string executable = Directory.EnumerateFiles(staging, OperatingSystem.IsWindows() ? "servli.exe" : "servli", SearchOption.AllDirectories).FirstOrDefault() ?? throw new ServliException("Release archive has no servli executable.");
        string current = Environment.ProcessPath!;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(Paths.Cache, "replace-servli.ps1");
            File.WriteAllText(script, "Start-Sleep -Seconds 2\nCopy-Item -LiteralPath '" + executable.Replace("'", "''") + "' -Destination '" + current.Replace("'", "''") + "' -Force\n");
            Process.Start(new ProcessStartInfo("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) { UseShellExecute = false, CreateNoWindow = true });
        }
        else
        {
            string script = Path.Combine(Paths.Cache, "replace-servli.sh");
            string target = current.Replace("'", "'\\''");
            File.WriteAllText(script, "#!/bin/sh\nsleep 2\ncp '" + executable.Replace("'", "'\\''") + "' '" + target + ".new'\nchmod +x '" + target + ".new'\nmv -f '" + target + ".new' '" + target + "'\n");
            Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false, CreateNoWindow = true });
        }
        Console.WriteLine($"Staged servli {version}. Replacement will finish after this command exits.");
    }
    #pragma warning disable IL3000
    private static bool IsStandalone() => string.IsNullOrEmpty(typeof(SelfUpdate).Assembly.Location);
    #pragma warning restore IL3000
    private static async Task<(string? version, Artifact? artifact)> LatestAsync()
    {
        using var doc = await Net.JsonAsync(ReleaseApi, false);
        string version = Net.Text(doc.RootElement, "tag_name");
        string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : OperatingSystem.IsMacOS() ? "intel" : "x64";
        string name = $"servli-{os}-{arch}" + (OperatingSystem.IsWindows() ? ".zip" : ".tar.gz");
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(x => Net.Text(x, "name") == name);
        if (asset.ValueKind == JsonValueKind.Undefined) return (version, null);
        string digest = Net.Text(asset, "digest");
        return (version, new(new(Net.Text(asset, "browser_download_url")), name, digest.StartsWith("sha256:") ? digest[7..] : null, "sha256"));
    }
}
