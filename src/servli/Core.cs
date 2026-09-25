using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Servli;

public sealed class ServliException(string message) : Exception(message);

public static class Paths
{
    public static string Root => Environment.GetEnvironmentVariable("SERVLI_HOME") is { Length: > 0 } value ? Path.GetFullPath(value) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".servli");
    public static string Servers => Path.Combine(Root, "servers");
    public static string Server(string name) => Path.Combine(Servers, name);
    public static string Content(string name) => Path.Combine(Server(name), "server");
    public static string Metadata(string name) => Path.Combine(Server(name), "servli.json");
    public static string Logs(string name) => Path.Combine(Server(name), "logs");
    public static string Backups(string name) => Path.Combine(Server(name), "backups");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Runtimes => Path.Combine(Root, "runtimes");
    public static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,47}$")) throw new ServliException("Server names must be 1–48 letters, digits, hyphens, or underscores and start with a letter or digit.");
    }
}

public sealed class ServerMeta
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public int JavaVersion { get; set; }
    public string Memory { get; set; } = "2G";
    public string Executable { get; set; } = "server.jar";
    public List<string> JvmArgs { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Geyser { get; set; }
    public bool Floodgate { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedUtc { get; set; }
}

public static class MetaStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static ServerMeta Load(string name)
    {
        Paths.ValidateName(name);
        if (!File.Exists(Paths.Metadata(name))) throw new ServliException($"Server '{name}' does not exist.");
        try { return JsonSerializer.Deserialize<ServerMeta>(File.ReadAllText(Paths.Metadata(name)), Json) ?? throw new Exception("Empty metadata"); }
        catch (Exception e) { throw new ServliException($"Cannot read metadata for '{name}': {e.Message}"); }
    }
    public static void Save(ServerMeta meta)
    {
        Paths.ValidateName(meta.Name);
        Directory.CreateDirectory(Paths.Server(meta.Name));
        string target = Paths.Metadata(meta.Name), temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(meta, Json));
        File.Move(temp, target, true);
    }
    public static IEnumerable<string> Names() => Directory.Exists(Paths.Servers) ? Directory.EnumerateDirectories(Paths.Servers).Select(Path.GetFileName).Where(x => x is not null && File.Exists(Paths.Metadata(x))).Select(x => x!) : [];
}

public sealed record Artifact(Uri Url, string FileName, string? Hash = null, string? HashAlgorithm = null);
public sealed record Resolution(string MinecraftVersion, string SoftwareVersion, Artifact Artifact, int JavaVersion, string Kind = "jar");

public static class Net
{
    public static readonly HttpClient Client = Create();
    private static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("servli/1.0 (+https://github.com/JAVBED/servli)");
        return client;
    }
    public static async Task<JsonDocument> JsonAsync(string url, bool cache = true)
    {
        Directory.CreateDirectory(Paths.Cache);
        var file = Path.Combine(Paths.Cache, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".json");
        if (cache && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(6)) return JsonDocument.Parse(await File.ReadAllTextAsync(file));
        try
        {
            using var response = await Client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            using var result = JsonDocument.Parse(body);
            if (cache) await File.WriteAllTextAsync(file, body);
            return JsonDocument.Parse(body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            if (cache && File.Exists(file)) return JsonDocument.Parse(await File.ReadAllTextAsync(file));
            throw new ServliException($"Could not contact {new Uri(url).Host}: {e.Message}");
        }
    }
    public static async Task DownloadAsync(Artifact artifact, string destination)
    {
        if (artifact.Url.Scheme != Uri.UriSchemeHttps) throw new ServliException("Download URL must use HTTPS.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                using var request = new HttpRequestMessage(HttpMethod.Get, artifact.Url);
                if (artifact.Url.Host.EndsWith("minecraft.net", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                    request.Headers.Referrer = new Uri("https://www.minecraft.net/en-us/download/server/bedrock");
                }
                if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (!append) existing = 0;
                long? total = response.Content.Headers.ContentLength is long size ? size + existing : null;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[128 * 1024]; long current = existing;
                var clock = Stopwatch.StartNew(); long lastDraw = 0;
                while (true)
                {
                    int n = await source.ReadAsync(buffer);
                    if (n == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, n)); current += n;
                    if (!Console.IsOutputRedirected && clock.ElapsedMilliseconds - lastDraw > 150)
                    {
                        lastDraw = clock.ElapsedMilliseconds;
                        int width = Math.Clamp(Console.WindowWidth - 34, 8, 48);
                        int filled = total is > 0 ? (int)Math.Clamp(current * width / total.Value, 0, width) : 0;
                        Console.Write($"\r{artifact.FileName} [{new string('█', filled)}{new string(' ', width - filled)}] {FormatSize(current)} {FormatSize((long)(current / Math.Max(clock.Elapsed.TotalSeconds, .1)))}/s  ");
                    }
                }
                if (!Console.IsOutputRedirected) Console.WriteLine();
                await target.FlushAsync(); target.Close();
                if (artifact.Hash is not null) Verify(partial, artifact.Hash, artifact.HashAlgorithm);
                else if (response.Content.Headers.ContentMD5 is byte[] md5) Verify(partial, Convert.ToHexString(md5), "md5");
                File.Move(partial, destination, true);
                return;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Checksum mismatch") || ex is HttpRequestException { StatusCode: HttpStatusCode.RequestedRangeNotSatisfiable })
                    if (File.Exists(partial)) File.Delete(partial);
                if (attempt == 3) throw new ServliException($"Could not download {artifact.Url}: {ex.Message}");
                await Task.Delay(attempt * 1000);
            }
        }
        throw new ServliException($"Could not download {artifact.FileName} after three attempts.");
    }
    public static void Verify(string file, string? expected, string? algorithm)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;
        using var stream = File.OpenRead(file);
        byte[] actual = algorithm?.ToLowerInvariant() switch
        {
            "sha1" => SHA1.HashData(stream),
            "sha256" => SHA256.HashData(stream),
            "sha512" => SHA512.HashData(stream),
            "md5" => MD5.HashData(stream),
            _ => throw new ServliException($"Unsupported hash algorithm '{algorithm}'.")
        };
        if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new ServliException($"Checksum mismatch for {Path.GetFileName(file)}.");
    }
    public static string FormatSize(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
    public static string Text(JsonElement el, string key) => el.TryGetProperty(key, out var value) ? value.ToString() : "";
}

public static class Versions
{
    public static int RequiredJava(string mc)
    {
        var parts = mc.Split('.', '-');
        if (parts[0] == "26") return 25;
        if (parts.Length < 2 || !int.TryParse(parts[1], out int minor)) return 21;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;
        return minor > 20 || (minor == 20 && patch >= 5) ? 21 : minor >= 18 ? 17 : 8;
    }
}
