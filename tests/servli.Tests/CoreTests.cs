using Servli;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Servli.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("1.16.5", 8)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.8", 21)]
    [InlineData("26.3", 25)]
    public void Java_requirement_follows_minecraft_release(string version, int expected) => Assert.Equal(expected, Versions.RequiredJava(version));

    [Theory]
    [InlineData("paper")]
    [InlineData("vanilla")]
    [InlineData("fabric")]
    [InlineData("quilt")]
    [InlineData("forge")]
    [InlineData("neoforge")]
    [InlineData("bds")]
    [InlineData("pocketmine")]
    [InlineData("powernukkitx")]
    public void All_public_providers_resolve(string id) => Assert.Equal(id, Providers.Get(id).Id);

    [Theory]
    [InlineData("../bad")]
    [InlineData("bad/name")]
    [InlineData("bad name")]
    [InlineData("")]
    public void Unsafe_server_names_are_rejected(string name) => Assert.Throws<ServliException>(() => Paths.ValidateName(name));

    [Fact]
    public void Metadata_serialization_roundtrips()
    {
        var original = new ServerMeta { Name = "survival", Provider = "paper", MinecraftVersion = "1.21.8", SoftwareVersion = "60", JavaVersion = 21, Geyser = true, JvmArgs = ["-Dexample=true"] };
        var copy = JsonSerializer.Deserialize<ServerMeta>(JsonSerializer.Serialize(original, MetaStore.Json), MetaStore.Json)!;
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.MinecraftVersion, copy.MinecraftVersion);
        Assert.True(copy.Geyser);
        Assert.Equal("-Dexample=true", copy.JvmArgs.Single());
    }

    [Fact]
    public void Property_edit_preserves_comments_and_unknown_keys()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "# hello\nunknown-key=value\nmotd=old\n");
            PropertiesFile.Set(file, "motd", "JAVBED Server");
            Assert.Equal("JAVBED Server", PropertiesFile.Get(file, "motd"));
            Assert.Contains("# hello", File.ReadAllText(file));
            Assert.Contains("unknown-key=value", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Property_edit_rejects_invalid_numeric_value()
    {
        string file = Path.GetTempFileName();
        try { Assert.Throws<ServliException>(() => PropertiesFile.Set(file, "max-players", "many")); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Backup_name_is_unique_and_timestamped()
    {
        var instant = new DateTimeOffset(2026, 9, 25, 12, 30, 45, TimeSpan.Zero);
        string first = Backup.Name(instant), second = Backup.Name(instant);
        Assert.StartsWith("backup-20260925-123045-", first);
        Assert.NotEqual(first, second);
        Assert.EndsWith(".zip", first);
    }

    [Fact]
    public void Checksum_verification_detects_corruption()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "correct");
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("correct")));
            Net.Verify(file, hash, "sha256");
            File.WriteAllText(file, "changed");
            Assert.Throws<ServliException>(() => Net.Verify(file, hash, "sha256"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Archive_rejects_path_traversal()
    {
        Assert.Throws<ServliException>(() => Archive.SafePath(Path.GetTempPath(), "../outside.txt"));
    }

    [Fact]
    public void Stale_pid_is_not_running()
    {
        var meta = new ServerMeta { ProcessId = int.MaxValue, ProcessStartedUtc = DateTimeOffset.UtcNow };
        Assert.False(ProcessHost.IsRunning(meta));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Paper_metadata_resolves_real_build_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("SERVLI_LIVE_TESTS") != "1") return;
        var result = await Providers.Get("paper").ResolveAsync("1.21.8");
        Assert.Equal("1.21.8", result.MinecraftVersion);
        Assert.True(result.Artifact.Url.IsAbsoluteUri);
        Assert.NotNull(result.Artifact.Hash);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Bedrock_metadata_resolves_official_archive_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("SERVLI_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var result = await Providers.Get("bds").ResolveAsync("latest");
        Assert.EndsWith(".zip", result.Artifact.Url.AbsolutePath);
        Assert.NotEqual("latest", result.MinecraftVersion);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("vanilla", "1.21.8")]
    [InlineData("purpur", "1.21.8")]
    [InlineData("fabric", "1.21.8")]
    [InlineData("quilt", "1.21.8")]
    [InlineData("forge", "1.21.8")]
    [InlineData("neoforge", "1.21.8")]
    public async Task Java_provider_metadata_resolves_exact_version_when_enabled(string provider, string version)
    {
        if (Environment.GetEnvironmentVariable("SERVLI_LIVE_TESTS") != "1") return;
        var result = await Providers.Get(provider).ResolveAsync(version);
        Assert.Equal(version, result.MinecraftVersion);
        Assert.Equal(21, result.JavaVersion);
        Assert.Equal("https", result.Artifact.Url.Scheme);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Provider_does_not_substitute_unavailable_minecraft_version_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("SERVLI_LIVE_TESTS") != "1") return;
        await Assert.ThrowsAsync<ServliException>(() => Providers.Get("paper").ResolveAsync("1.0.0"));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("pocketmine")]
    [InlineData("powernukkitx")]
    public async Task Bedrock_plugin_server_metadata_has_distinct_minecraft_and_software_versions_when_enabled(string provider)
    {
        if (Environment.GetEnvironmentVariable("SERVLI_LIVE_TESTS") != "1") return;
        var result = await Providers.Get(provider).ResolveAsync("latest");
        Assert.StartsWith("1.", result.MinecraftVersion);
        Assert.NotEqual(result.MinecraftVersion, result.SoftwareVersion);
    }
}
