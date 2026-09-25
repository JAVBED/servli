# servli

`servli` is JAVBED's cross-platform command-line manager for Minecraft servers. It creates isolated installations, obtains runtimes and server software from upstream projects, runs servers behind a local console host, and manages properties, backups, addons, mods, and updates. It has no GUI and does not ship Minecraft server binaries.

## Install

Download the archive for your OS and CPU from [GitHub Releases](https://github.com/JAVBED/servli/releases). Extract `servli` (`servli.exe` on Windows), put it on your `PATH`, and run `servli help`. Release executables are self-contained; .NET is not required on the target machine. The first server creation downloads its upstream software and any needed private Java or PocketMine PHP runtime.

Supported release targets: Windows x64/ARM64, Linux x64/ARM64, macOS Intel/Apple Silicon. Mojang BDS itself only supplies Windows and Linux x64 packages. Some upstream runtimes may not exist for every OS/CPU combination; servli reports that explicitly.

## Providers

| Provider | ID | Runtime | Notes |
| --- | --- | --- | --- |
| Mojang Vanilla | `vanilla` | Java | Official launcher manifest and SHA-1 |
| Paper | `paper` | Java | Paper downloads API and SHA-256 |
| Purpur | `purpur` | Java | Purpur API and MD5 |
| Fabric | `fabric` | Java | Fabric Meta server bootstrap |
| Quilt | `quilt` | Java | Quilt Meta and CLI installer |
| Forge | `forge` | Java | Official Maven installer |
| NeoForge | `neoforge` | Java | Official Maven installer |
| Mojang BDS | `bds` | Native | Official current Windows/Linux x64 archive |
| PocketMine-MP | `pocketmine` | PHP | Official GitHub PHAR and private PMMP PHP binary |
| PowerNukkitX | `powernukkitx` | Java | Official GitHub release JAR |

`versions` shows Minecraft versions for Java providers, current BDS version for BDS, and supported Bedrock versions parsed from release notes for PocketMine-MP/PowerNukkitX. `latest` is resolved to a concrete version in `servli.json`. BDS's official feed only exposes the current package; requests for an older BDS version fail instead of substituting a different version.

## Everyday use

```text
servli providers
servli versions paper
servli create survival paper 1.21.8
servli list
servli info survival
servli eula survival
servli start survival
servli console survival
servli logs survival --follow
servli status survival
servli stop survival
```

`servli eula` requires you to read Mojang's EULA and type `yes`; creation leaves `eula=false`. The EULA command applies to Java Edition servers. Review the relevant upstream terms for other providers. `console` shows new log lines and sends commands through a local named pipe to the server host. Type `exit` or `detach` to leave the console without stopping the server. `stop` sends the normal server command, retries during startup, then force terminates after 30 seconds if necessary.

## Java and memory

```text
servli java list
servli java install 21
servli java path 21
servli memory survival 4G
```

Java 8, 17, 21, and 25 are supported. servli uses a matching system Java or downloads a checksum-verified Eclipse Temurin JRE under its data directory. It does not modify `JAVA_HOME`. Java server memory defaults to `2G`. Advanced JVM arguments can be edited in the `jvmArgs` array in the server's `servli.json`; use one argument per array item. Use care when changing metadata while a server is running.

## Bedrock and crossplay

```text
servli create bedrock bds latest
servli create pocket pocketmine latest
servli create nukkit powernukkitx latest
servli create crossplay paper 1.21.8 --geyser --floodgate
servli addon survival geyser
servli addon survival floodgate
servli addon survival remove geyser
```

BDS, PocketMine-MP, and PowerNukkitX are different server products with different runtime and plugin ecosystems. The Bedrock default is UDP port 19132. PocketMine uses a private PHP binary, not Java. PowerNukkitX uses Java 21. BDS updates can change the Minecraft version, so require `servli update bedrock --minecraft latest` explicitly.

Geyser/Floodgate are Paper/Purpur plugins, not BDS conversions. servli downloads and verifies their upstream builds. When Geyser is installed on a Paper version older than 26.2, servli also installs compatible ViaVersion from Modrinth. The Java default is TCP 25565; Geyser's Bedrock default is UDP 19132. With Floodgate, servli changes Geyser's generated `auth-type` from `online` to `floodgate` when the plugin first finishes loading and requests a Geyser reload. It preserves any custom non-default auth setting. Review `plugins/Geyser-Spigot/config.yml` and the console log, and restart if Geyser reports that a setting needs it. Floodgate's private key remains in its own plugin directory; do not share it.

## Configuration, packages, backups

```text
servli properties survival
servli properties survival motd "JAVBED Server"
servli properties survival max-players 20
servli plugin search survival viaversion
servli plugin install survival viaversion
servli plugin install survival ./my-plugin.jar
servli mod search modded sodium
servli mod install modded ./my-mod.jar
servli backup survival
servli backups survival
servli restore survival backup-20260925-123045-abcdef.zip --yes
```

Property edits retain comments and unknown keys. Modrinth installs select files matching the server loader and Minecraft version, verify SHA-512, and resolve required project dependencies. Local JAR installation works for Paper/Purpur, Fabric/Quilt/Forge/NeoForge, and PowerNukkitX. PocketMine accepts local PHAR plugins. Search and remote install through Modrinth are limited to Paper/Purpur and Java mod loaders. Restart after installing packages.

Backups are timestamped ZIP files of server data, excluding transient logs and lock/partial files. A running Java Edition server receives `save-off`, `save-all flush`, and `save-on`; the backup aborts if save completion is not confirmed. If Windows locks an open world file despite a successful flush, servli stops the server, archives it, and restarts it, reporting that interruption. Stop BDS, PocketMine, and PowerNukkitX before backup. Restore overwrites current server data and requires a stopped server plus `--yes`. Keep an off-machine copy of important backups.

## Updates and diagnostics

```text
servli update survival
servli update --all
servli update bedrock --minecraft latest
servli update-self
servli doctor
servli doctor survival
```

Provider updates retain the configured Minecraft version by default. servli creates a backup first and restores server files if a replacement or installer fails. BDS requires an explicit version change. `update --all` reports individual errors and continues. `update-self` applies only to official single-file releases; source builds cannot overwrite themselves. A standalone release checks the latest GitHub release at most once every 24 hours and prompts before updating.

`doctor` checks writable storage, free disk, installed runtimes, server executable, EULA, properties, stale PID state, addon files, and the configured port. The data directory defaults to `~/.servli` on all three OS families. Set `SERVLI_HOME` to an absolute path to change it. Each server has `servli.json`, `server/`, `backups/`, and `logs/` under `servers/<name>/`. Metadata does not contain secrets.

Use `--debug` with any command for a stack trace. Normal errors are single-line messages. Network endpoints and project releases can change; run `servli doctor` and check the upstream project's status when discovery fails.

## Build and test

Requires the .NET 10 SDK:

```text
dotnet restore
dotnet build
dotnet test
dotnet publish src/servli/servli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Set `SERVLI_LIVE_TESTS=1` to run the optional live metadata tests. `.github/workflows/release.yml` builds and smoke-tests six self-contained archives on matching runners, publishes a GitHub Release for `v*` tags, and supports manual runs.

## Project status

The Windows x64 validation run covered real create/start/stop flows for Paper, Fabric, Quilt, Forge, NeoForge, BDS, PocketMine-MP, and PowerNukkitX; Vanilla and Purpur downloads and creation were verified. Geyser, Floodgate, and ViaVersion installation were verified and a Paper crossplay server was started. The CI workflow is configured to build and smoke-test the other release architectures, but has not run until this repository is pushed to GitHub. Their full server flows have not been run locally. BDS has no official historical version catalog. Modrinth search/install and provider update flows have automated logic but have not been exercised across every project and upstream release. Confirm compatibility before production use.

## Upstream projects and disclaimer

servli is MIT-licensed original code. Upstream server distributions, plugins, mods, and runtimes retain their own licenses and terms. This repository does not bundle them. Thanks to [Mojang](https://www.minecraft.net/), [PaperMC](https://papermc.io/), [Purpur](https://purpurmc.org/), [Fabric](https://fabricmc.net/), [Quilt](https://quiltmc.org/), [Minecraft Forge](https://minecraftforge.net/), [NeoForged](https://neoforged.net/), [PocketMine-MP](https://pmmp.io/), [PowerNukkitX](https://powernukkitx.org/), [GeyserMC](https://geysermc.org/), [Modrinth](https://modrinth.com/), and [Eclipse Adoptium](https://adoptium.net/).

servli is an independent JAVBED project and is not affiliated with, endorsed by, or sponsored by Mojang Studios or Microsoft. Minecraft is a trademark of Microsoft Corporation.
