using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace Servli;

public interface IServerProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<string>> GetVersionsAsync();
    Task<Resolution> ResolveAsync(string requested);
}

public static class Providers
{
    private static readonly Dictionary<string, IServerProvider> Items = new IServerProvider[]
    {
        new VanillaProvider(), new PaperProvider(), new PurpurProvider(), new FabricProvider(), new QuiltProvider(),
        new MavenInstallerProvider("forge"), new MavenInstallerProvider("neoforge"), new GitHubProvider("powernukkitx"),
        new GitHubProvider("pocketmine"), new BdsProvider()
    }.ToDictionary(x => x.Id);
    public static IEnumerable<IServerProvider> All => Items.Values;
    public static IServerProvider Get(string id) => Items.TryGetValue(id.ToLowerInvariant(), out var provider) ? provider : throw new ServliException($"Unknown provider '{id}'. Run 'servli providers'.");
    public static bool IsJava(string id) => id != "pocketmine" && id != "bds";
    public static bool IsPlugin(string id) => id is "paper" or "purpur" or "powernukkitx" or "pocketmine";
    public static bool IsMod(string id) => id is "fabric" or "quilt" or "forge" or "neoforge";
}

public sealed class VanillaProvider : IServerProvider
{
    const string Manifest = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
    public string Id => "vanilla";
    public string DisplayName => "Mojang Vanilla";
    public async Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        using var doc = await Net.JsonAsync(Manifest);
        return doc.RootElement.GetProperty("versions").EnumerateArray().Where(x => Net.Text(x, "type") == "release").Select(x => Net.Text(x, "id")).ToArray();
    }
    public async Task<Resolution> ResolveAsync(string requested)
    {
        using var doc = await Net.JsonAsync(Manifest);
        var root = doc.RootElement;
        var version = requested == "latest" ? root.GetProperty("latest").GetProperty("release").GetString()! : requested;
        var entry = root.GetProperty("versions").EnumerateArray().FirstOrDefault(x => Net.Text(x, "id") == version && Net.Text(x, "type") == "release");
        if (entry.ValueKind == JsonValueKind.Undefined) throw new ServliException($"Mojang has no release '{version}'.");
        using var detail = await Net.JsonAsync(Net.Text(entry, "url"));
        var server = detail.RootElement.GetProperty("downloads").GetProperty("server");
        int java = detail.RootElement.TryGetProperty("javaVersion", out var j) && j.TryGetProperty("majorVersion", out var major) ? major.GetInt32() : Versions.RequiredJava(version);
        return new(version, version, new(new(Net.Text(server, "url")), "server.jar", Net.Text(server, "sha1"), "sha1"), java);
    }
}

public sealed class PaperProvider : IServerProvider
{
    public string Id => "paper";
    public string DisplayName => "Paper";
    public async Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        using var doc = await Net.JsonAsync("https://fill.papermc.io/v3/projects/paper");
        return doc.RootElement.GetProperty("versions").EnumerateObject().SelectMany(x => x.Value.EnumerateArray().Select(v => v.GetString()!)).Where(x => !x.Contains('-')).ToArray();
    }
    public async Task<Resolution> ResolveAsync(string requested)
    {
        var versions = await GetVersionsAsync();
        string version = requested == "latest" ? versions.First() : requested;
        if (!versions.Contains(version)) throw new ServliException($"Paper has no Minecraft version {version}.");
        using var doc = await Net.JsonAsync($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds");
        var builds = doc.RootElement.EnumerateArray().ToArray();
        var build = builds.FirstOrDefault(x => Net.Text(x, "channel") == "STABLE");
        if (build.ValueKind == JsonValueKind.Undefined) build = builds.FirstOrDefault();
        if (build.ValueKind == JsonValueKind.Undefined) throw new ServliException($"Paper has no build for Minecraft {version}.");
        var download = build.GetProperty("downloads").GetProperty("server:default");
        return new(version, Net.Text(build, "id"), new(new(Net.Text(download, "url")), "server.jar", Net.Text(download.GetProperty("checksums"), "sha256"), "sha256"), Versions.RequiredJava(version));
    }
}

public sealed class PurpurProvider : IServerProvider
{
    public string Id => "purpur";
    public string DisplayName => "Purpur";
    public async Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        using var doc = await Net.JsonAsync("https://api.purpurmc.org/v2/purpur");
        return doc.RootElement.GetProperty("versions").EnumerateArray().Select(x => x.GetString()!).Reverse().ToArray();
    }
    public async Task<Resolution> ResolveAsync(string requested)
    {
        var versions = await GetVersionsAsync();
        string version = requested == "latest" ? versions.First() : requested;
        if (!versions.Contains(version)) throw new ServliException($"Purpur has no Minecraft version {version}.");
        using var doc = await Net.JsonAsync($"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(version)}/latest");
        var root = doc.RootElement;
        string build = Net.Text(root, "build");
        string md5 = root.TryGetProperty("md5", out var hash) ? hash.ToString() : "";
        return new(version, build, new(new($"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(version)}/{build}/download"), "server.jar", md5, "md5"), Versions.RequiredJava(version));
    }
}

public sealed class FabricProvider : IServerProvider
{
    public string Id => "fabric";
    public string DisplayName => "Fabric";
    public async Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        using var doc = await Net.JsonAsync("https://meta.fabricmc.net/v2/versions/game");
        return doc.RootElement.EnumerateArray().Where(x => x.GetProperty("stable").GetBoolean()).Select(x => Net.Text(x, "version")).ToArray();
    }
    public async Task<Resolution> ResolveAsync(string requested)
    {
        var versions = await GetVersionsAsync();
        string version = requested == "latest" ? versions.First() : requested;
        if (!versions.Contains(version)) throw new ServliException($"Fabric has no stable Minecraft version {version}.");
        using var loaders = await Net.JsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(version)}");
        var loader = loaders.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("loader").GetProperty("stable").GetBoolean());
        if (loader.ValueKind == JsonValueKind.Undefined) throw new ServliException($"Fabric has no stable loader for {version}.");
        string loaderVersion = Net.Text(loader.GetProperty("loader"), "version");
        using var installers = await Net.JsonAsync("https://meta.fabricmc.net/v2/versions/installer");
        string installerVersion = Net.Text(installers.RootElement.EnumerateArray().First(x => x.GetProperty("stable").GetBoolean()), "version");
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(version)}/{loaderVersion}/{installerVersion}/server/jar";
        return new(version, loaderVersion, new(new(url), "server.jar"), Versions.RequiredJava(version));
    }
}

public sealed class QuiltProvider : IServerProvider
{
    public string Id => "quilt";
    public string DisplayName => "Quilt";
    public async Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        using var doc = await Net.JsonAsync("https://meta.quiltmc.org/v3/versions/game");
        return doc.RootElement.EnumerateArray().Where(x => x.TryGetProperty("stable", out var stable) && stable.GetBoolean()).Select(x => Net.Text(x, "version")).ToArray();
    }
    public async Task<Resolution> ResolveAsync(string requested)
    {
        var versions = await GetVersionsAsync();
        string version = requested == "latest" ? versions.First() : requested;
        if (!versions.Contains(version)) throw new ServliException($"Quilt has no stable Minecraft version {version}.");
        using var loaders = await Net.JsonAsync($"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(version)}");
        var loader = loaders.RootElement.EnumerateArray()
            .Where(x => Version.TryParse(Net.Text(x.GetProperty("loader"), "version"), out _))
            .OrderByDescending(x => Version.Parse(Net.Text(x.GetProperty("loader"), "version")))
            .FirstOrDefault();
        if (loader.ValueKind == JsonValueKind.Undefined) throw new ServliException($"Quilt has no loader for {version}.");
        string loaderVersion = Net.Text(loader.GetProperty("loader"), "version");
        using var installers = await Net.JsonAsync("https://meta.quiltmc.org/v3/versions/installer");
        var installer = installers.RootElement.EnumerateArray().First();
        string installerUrl = Net.Text(installer, "url");
        string checksum = (await Net.Client.GetStringAsync(installerUrl + ".sha256")).Trim().Split(' ')[0];
        return new(version, loaderVersion, new(new(installerUrl), "quilt-installer.jar", checksum, "sha256"), Versions.RequiredJava(version), "quilt-installer");
    }
}

public sealed class MavenInstallerProvider(string id) : IServerProvider
{
    public string Id => id;
    public string DisplayName => id == "forge" ? "Forge" : "NeoForge";
    private string Base => id == "forge" ? "https://maven.minecraftforge.net/net/minecraftforge/forge" : "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private async Task<string[]> BuildsAsync()
    {
        string xml = await Net.Client.GetStringAsync(Base + "/maven-metadata.xml");
        return XDocument.Parse(xml).Descendants("version").Select(x => x.Value).Reverse().ToArray();
    }
    public async Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        var builds = await BuildsAsync();
        return builds.Select(Minecraft).Where(x => x is not null).Distinct().Select(x => x!).OrderByDescending(x => Version.TryParse(x, out var parsed) ? parsed : new Version(0, 0)).ToArray();
    }
    private string? Minecraft(string build)
    {
        if (id == "forge") return build.Split('-')[0];
        var match = Regex.Match(build, @"^(\d+)\.(\d+)");
        if (!match.Success) return null;
        int minor = int.Parse(match.Groups[1].Value), patch = int.Parse(match.Groups[2].Value);
        return minor >= 26 ? $"{minor}.{patch}" : $"1.{minor}.{patch}";
    }
    public async Task<Resolution> ResolveAsync(string requested)
    {
        var builds = await BuildsAsync();
        string mcRequested = requested == "latest" ? (await GetVersionsAsync()).First() : requested;
        string? build = builds.FirstOrDefault(x => Minecraft(x) == mcRequested);
        if (build is null) throw new ServliException($"{DisplayName} has no installer for Minecraft {requested}.");
        string mc = Minecraft(build)!;
        string file = id == "forge" ? $"forge-{build}-installer.jar" : $"neoforge-{build}-installer.jar";
        string url = $"{Base}/{build}/{file}";
        string sha1 = (await Net.Client.GetStringAsync(url + ".sha1")).Trim().Split(' ')[0];
        return new(mc, build, new(new(url), file, sha1, "sha1"), Versions.RequiredJava(mc), "maven-installer");
    }
}

public sealed class GitHubProvider(string id) : IServerProvider
{
    public string Id => id;
    public string DisplayName => id == "pocketmine" ? "PocketMine-MP" : "PowerNukkitX";
    private string Repo => id == "pocketmine" ? "pmmp/PocketMine-MP" : "PowerNukkitX/PowerNukkitX";
    public async Task<IReadOnlyList<string>> GetVersionsAsync()
    {
        using var doc = await Net.JsonAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
        return doc.RootElement.EnumerateArray().Where(x => !x.GetProperty("draft").GetBoolean() && !x.GetProperty("prerelease").GetBoolean()).Select(x => BedrockVersion(x)).Where(x => x.Length > 0).Distinct().ToArray();
    }
    public async Task<Resolution> ResolveAsync(string requested)
    {
        JsonDocument doc;
        JsonElement root;
        if (requested == "latest")
        {
            doc = await Net.JsonAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            root = doc.RootElement;
        }
        else
        {
            doc = await Net.JsonAsync($"https://api.github.com/repos/{Repo}/releases?per_page=100");
            root = doc.RootElement.EnumerateArray().FirstOrDefault(x => !x.GetProperty("draft").GetBoolean() && !x.GetProperty("prerelease").GetBoolean() && (Net.Text(x, "tag_name") == requested || BedrockVersion(x) == requested));
            if (root.ValueKind == JsonValueKind.Undefined) throw new ServliException($"{DisplayName} has no release for Bedrock {requested}.");
        }
        using (doc)
        {
        string version = Net.Text(root, "tag_name");
        string filename = id == "pocketmine" ? "PocketMine-MP.phar" : "powernukkitx.jar";
        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(x => Net.Text(x, "name") == filename);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new ServliException($"{DisplayName} release {version} has no {filename} asset.");
        string digest = Net.Text(asset, "digest");
        string mc = BedrockVersion(root);
        if (mc.Length == 0) throw new ServliException($"{DisplayName} release {version} does not state a supported Bedrock version.");
        return new(mc, version, new(new(Net.Text(asset, "browser_download_url")), filename, digest.StartsWith("sha256:") ? digest[7..] : null, "sha256"), id == "pocketmine" ? 0 : 21, id == "pocketmine" ? "phar" : "jar");
        }
    }
    private static string BedrockVersion(JsonElement release)
    {
        string body = Net.Text(release, "body");
        var match = Regex.Match(body, @"(?:Minecraft Version[^\r\n]{0,12}?\|\s*|Bedrock Edition\s*)(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "";
    }
}

public sealed class BdsProvider : IServerProvider
{
    public string Id => "bds";
    public string DisplayName => "Mojang Bedrock Dedicated Server";
    public async Task<IReadOnlyList<string>> GetVersionsAsync() => [(await ResolveAsync("latest")).MinecraftVersion];
    public async Task<Resolution> ResolveAsync(string requested)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new ServliException("Mojang BDS currently publishes Windows/Linux x64 binaries only.");
        using var doc = await Net.JsonAsync("https://net-secondary.web.minecraft-services.net/api/v1.0/download/links");
        string type = OperatingSystem.IsWindows() ? "serverBedrockWindows" : "serverBedrockLinux";
        var entry = doc.RootElement.GetProperty("result").GetProperty("links").EnumerateArray().First(x => Net.Text(x, "downloadType") == type);
        string url = Net.Text(entry, "downloadUrl");
        var match = Regex.Match(url, @"bedrock-server-([\d.]+)\.zip$");
        if (!match.Success) throw new ServliException("Mojang BDS download URL did not contain a version.");
        string version = match.Groups[1].Value;
        if (requested != "latest" && requested != version) throw new ServliException($"Mojang currently offers BDS {version}; requested {requested} is unavailable from the official download service.");
        return new(version, version, new(new(url), "bedrock-server.zip"), 0, "zip");
    }
}
