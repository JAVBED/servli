# Provider integrations

Each provider implements `IServerProvider` with version discovery and an exact resolution from requested version to `Resolution` (Minecraft version, software build, artifact URL/hash, runtime, install kind). `Installer` handles the install kinds. A new provider should return a concrete version and fail on unsupported requests. Downloads must use HTTPS and a project-supported endpoint.

| Provider | Metadata | Install artifact | Integrity |
| --- | --- | --- | --- |
| Vanilla | [Mojang launcher manifest](https://launchermeta.mojang.com/mc/game/version_manifest_v2.json) | Mojang server JAR | SHA-1 from version manifest |
| Paper | [Paper downloads service](https://docs.papermc.io/misc/downloads-service) | Stable build JAR | SHA-256 |
| Purpur | [Purpur API](https://api.purpurmc.org/v2/purpur) | Latest build for exact Minecraft version | MD5 |
| Fabric | [Fabric Meta](https://meta.fabricmc.net/) | Server bootstrap JAR | No checksum in server bootstrap response |
| Quilt | [Quilt Meta](https://meta.quiltmc.org/) and [installer guide](https://quiltmc.org/en/install/server/) | Installer JAR | Maven SHA-256 sidecar |
| Forge | [Forge Maven](https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml) | Installer JAR | Maven SHA-1 sidecar |
| NeoForge | [NeoForge Maven](https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml) | Installer JAR | Maven SHA-1 sidecar |
| BDS | [Mojang download links](https://net-secondary.web.minecraft-services.net/api/v1.0/download/links) | Current OS archive | HTTP Content-MD5 when supplied |
| PocketMine-MP | [GitHub releases](https://github.com/pmmp/PocketMine-MP/releases), [PHP binaries](https://github.com/pmmp/PHP-Binaries/releases) | PHAR and PHP runtime | GitHub SHA-256 digest when supplied |
| PowerNukkitX | [GitHub releases](https://github.com/PowerNukkitX/PowerNukkitX/releases) | Release JAR | GitHub SHA-256 digest when supplied |

The Maven providers run their supported `--installServer` command and launch with the generated `libraries/.../win_args.txt` or `unix_args.txt`. Quilt runs its CLI installer with an explicit destination and loader version. BDS extracts only regular ZIP entries inside the server directory and preserves worlds/config during an explicit version update. Archive paths are checked before extraction. PocketMine's PHP binary is installed privately and kept separate from Java runtimes.

Metadata responses are cached for six hours; downloads use retries and resume partial files when the server accepts byte ranges. Checksums are checked before files replace an installation. `SERVLI_LIVE_TESTS=1 dotnet test` exercises live provider resolution; release CI runs offline unit tests and standalone smoke commands. Full server boot testing requires large third-party downloads and remains a manual validation step for new upstream releases and architectures.

When an upstream API changes, inspect its current documentation and example response first. Do not construct artifact URLs from version strings unless the project's metadata explicitly documents that layout.
