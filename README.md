# servli

`servli` is JAVBED's cross-platform command-line manager for Minecraft servers. Create a server, run it in the background, attach to its console, and manage its configuration, backups, packages, and updates from one CLI. Each server has its own directory. servli downloads server software from its upstream project when needed; the repository does not contain Minecraft binaries.

## Install

When a release is available, download the archive for your OS and CPU from [GitHub Releases](https://github.com/JAVBED/servli/releases). Extract `servli` (`servli.exe` on Windows) into a directory on your `PATH`, open a new terminal, and run `servli help`. On macOS/Linux, make the extracted file executable with `chmod +x servli`. Release executables are self-contained; .NET is not required on the target machine. Until the first release, [build from source](#build-and-test).

Supported release targets: Windows x64/ARM64, Linux x64/ARM64, macOS Intel/Apple Silicon. Mojang BDS itself only supplies Windows and Linux x64 packages. Some upstream runtimes may not exist for every OS/CPU combination; servli reports that explicitly.

## Quick start

```text
servli providers                   # See available server software
servli versions paper              # See available Minecraft versions
servli create survival paper latest
servli eula survival               # Read the linked EULA and type yes if you agree
servli start survival
servli status survival
servli console survival            # Enter server commands; type detach to leave
servli stop survival
```

`latest` resolves to a specific Minecraft version and build when the server is created. To pin a version, use a command such as `servli create survival paper 1.21.8`. Server creation may download Java privately; you normally do not need to install or configure it yourself. The EULA remains unaccepted until you explicitly accept it. Check the port and settings before exposing a server to the internet.

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

## Command reference

| Task | Command |
| --- | --- |
| Discover software and versions | `servli providers`, `servli versions <provider>` |
| Create and inspect | `servli create <name> <provider> <version\|latest>`, `servli list`, `servli info <name>` |
| Run and control | `servli start\|stop\|restart\|status\|console <name>` |
| Read logs | `servli logs <name> [--follow]` |
| Set Java EULA, properties, memory | `servli eula <name>`, `servli properties <name> [key] [value]`, `servli memory <name> <size>` |
| Back up and restore | `servli backup <name>`, `servli backups <name>`, `servli restore <name> <backup> --yes`, `servli delete-backup <name> <backup>` |
| Manage software and addons | `servli update <name>`, `servli update --all`, `servli addon <name> geyser\|floodgate\|remove <addon>` |
| Install packages | `servli plugin\|mod search <name> <query>`, `servli plugin\|mod install <name> <file\|project>` |
| Manage Java | `servli java list`, `servli java install <version>`, `servli java path <version>` |
| Diagnose and remove | `servli doctor [name]`, `servli delete <name> --yes` |
| Update servli | `servli update-self` |

Commands that overwrite data, such as restore and delete, require `--yes`. Run `servli help` for the built-in summary. Add `--debug` to a command to print a stack trace when diagnosing an error.

## Working with a server

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
servli backups survival --json
servli list --json
servli schedule survival hourly --keep 10 --max-age-days 30
servli schedule survival on-stop --keep 5
servli restore survival <backup-name-from-servli-backups> --yes
```

Property edits retain comments and unknown keys. Modrinth installs select files matching the server loader and Minecraft version, verify SHA-512, and resolve required project dependencies. Local JAR installation works for Paper/Purpur, Fabric/Quilt/Forge/NeoForge, and PowerNukkitX. PocketMine accepts local PHAR plugins. Search and remote install through Modrinth are limited to Paper/Purpur and Java mod loaders. Restart after installing packages.

Backups are timestamped ZIP files of server data, excluding transient logs and lock/partial files. A running Java Edition server receives `save-off`, `save-all flush`, and `save-on`; the backup aborts if save completion is not confirmed. If Windows locks an open world file despite a successful flush, servli stops the server, archives it, and restarts it, reporting that interruption. Stop BDS, PocketMine, and PowerNukkitX before backup. Restore overwrites current server data and requires a stopped server plus `--yes`. Keep an off-machine copy of important backups.

`list --json` and `backups <name> --json` provide structured data for JAVBED's dashboard. Backup schedules are saved per server and run in the SERVLI host while it is active, including when JAVBED is closed. Supported modes are `off`, `30m`, `hourly`, `<N>h`, `daily`, and `on-stop`. Scheduled live backups do not interrupt or restart a running server; failures appear in the host log. Retention keeps at least one backup. Restore creates an additional backup of current server data before replacing it.

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

Normal errors are single-line messages. If something fails, run `servli doctor <name>` for server checks or `servli doctor` for host checks. If version discovery fails, check network access and the upstream project's status. Use `--debug` for a stack trace. If a port is in use, inspect `server.properties` (or the Bedrock server's equivalent) and change its port with `servli properties <name> server-port <port>` where supported.

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

The Windows x64 validation run covered real create/start/stop flows for Paper, Fabric, Quilt, Forge, NeoForge, BDS, PocketMine-MP, and PowerNukkitX; Vanilla and Purpur downloads and creation were verified. Geyser, Floodgate, and ViaVersion installation were verified and a Paper crossplay server was started. The [CI workflow](https://github.com/JAVBED/servli/actions) is configured to build and smoke-test the other release architectures. Their full server flows have not been run locally. BDS has no official historical version catalog. Modrinth search/install and provider update flows have automated logic but have not been exercised across every project and upstream release. Confirm compatibility before production use.

## Upstream projects and disclaimer

servli is MIT-licensed original code. Upstream server distributions, plugins, mods, and runtimes retain their own licenses and terms. This repository does not bundle them. Thanks to [Mojang](https://www.minecraft.net/), [PaperMC](https://papermc.io/), [Purpur](https://purpurmc.org/), [Fabric](https://fabricmc.net/), [Quilt](https://quiltmc.org/), [Minecraft Forge](https://minecraftforge.net/), [NeoForged](https://neoforged.net/), [PocketMine-MP](https://pmmp.io/), [PowerNukkitX](https://powernukkitx.org/), [GeyserMC](https://geysermc.org/), [Modrinth](https://modrinth.com/), and [Eclipse Adoptium](https://adoptium.net/).

servli is an independent JAVBED project and is not affiliated with, endorsed by, or sponsored by Mojang Studios or Microsoft. Minecraft is a trademark of Microsoft Corporation.
