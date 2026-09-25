using System.Text.Json;

namespace Servli;

public static class Modrinth
{
    private static string Loader(ServerMeta meta) => meta.Provider is "paper" or "purpur" ? "paper" : meta.Provider;
    public static async Task SearchAsync(ServerMeta meta, string query)
    {
        if (meta.Provider is not ("paper" or "purpur" or "fabric" or "quilt" or "forge" or "neoforge")) throw new ServliException("Modrinth search is supported for Paper, Purpur and mod loaders.");
        string type = meta.Provider is "paper" or "purpur" ? "plugin" : "mod";
        string facets = JsonSerializer.Serialize(new[] { new[] { $"project_type:{type}" }, new[] { $"categories:{Loader(meta)}" }, new[] { $"versions:{meta.MinecraftVersion}" } });
        using var doc = await Net.JsonAsync($"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&limit=20", false);
        foreach (var hit in doc.RootElement.GetProperty("hits").EnumerateArray()) Console.WriteLine($"{Net.Text(hit, "slug"),-28} {Net.Text(hit, "title")}");
    }
    public static Task InstallAsync(ServerMeta meta, string project) => InstallAsync(meta, project, new HashSet<string>(), null);
    private static async Task InstallAsync(ServerMeta meta, string project, HashSet<string> visited, string? pinnedVersion)
    {
        if (!visited.Add(pinnedVersion ?? project)) return;
        if (meta.Provider is not ("paper" or "purpur" or "fabric" or "quilt" or "forge" or "neoforge")) throw new ServliException("Modrinth installs are supported for Paper, Purpur and mod loaders.");
        string loader = Loader(meta);
        string url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(project)}/version?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { meta.MinecraftVersion }))}&include_changelog=false";
        using var doc = await Net.JsonAsync(pinnedVersion is null ? url : $"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(pinnedVersion)}", false);
        JsonElement version;
        if (pinnedVersion is null)
        {
            var versions = doc.RootElement.EnumerateArray().ToArray();
            version = versions.FirstOrDefault(x => Net.Text(x, "version_type") == "release");
            if (version.ValueKind == JsonValueKind.Undefined) version = versions.FirstOrDefault();
        }
        else
        {
            version = doc.RootElement;
            if (!version.GetProperty("game_versions").EnumerateArray().Any(x => x.GetString() == meta.MinecraftVersion) || !version.GetProperty("loaders").EnumerateArray().Any(x => x.GetString() == loader)) throw new ServliException($"Pinned dependency {pinnedVersion} is incompatible with {loader} {meta.MinecraftVersion}.");
        }
        if (version.ValueKind == JsonValueKind.Undefined) throw new ServliException($"Modrinth project {project} has no {loader} build for Minecraft {meta.MinecraftVersion}.");
        foreach (var dependency in version.GetProperty("dependencies").EnumerateArray().Where(x => Net.Text(x, "dependency_type") == "required"))
        {
            string dep = Net.Text(dependency, "project_id");
            if (dep.Length == 0) throw new ServliException($"{project} has a required dependency that cannot be resolved automatically.");
            string pinned = Net.Text(dependency, "version_id");
            await InstallAsync(meta, dep, visited, pinned.Length == 0 ? null : pinned);
        }
        var file = version.GetProperty("files").EnumerateArray().FirstOrDefault(x => x.GetProperty("primary").GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined) file = version.GetProperty("files").EnumerateArray().FirstOrDefault();
        if (file.ValueKind == JsonValueKind.Undefined) throw new ServliException($"Modrinth version {Net.Text(version, "version_number")} has no file.");
        string filename = Net.Text(file, "filename");
        if (!filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new ServliException($"Modrinth file {filename} is not a JAR.");
        string folder = meta.Provider is "paper" or "purpur" ? "plugins" : "mods";
        string destination = Path.Combine(Paths.Content(meta.Name), folder, Path.GetFileName(filename));
        if (File.Exists(destination)) { Console.WriteLine($"Already installed: {filename}"); return; }
        string hash = Net.Text(file.GetProperty("hashes"), "sha512");
        await Net.DownloadAsync(new(new(Net.Text(file, "url")), filename, hash, "sha512"), destination);
        Console.WriteLine($"Installed {filename} ({Net.Text(version, "version_number")}).");
    }
}
