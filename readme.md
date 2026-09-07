# AAML

AAML (Avalonia Alternative Mod Launcher) is a replacement for the default game launchers from **XCOM 2** and **XCOM Chimera Squad**. It is a ground-up rewrite of the Alternative Mod Launcher (AML) using .NET 10 and Avalonia.

[![CI](https://github.com/JakeRoxs/xcom2-aaml/actions/workflows/ci.yml/badge.svg)](https://github.com/JakeRoxs/xcom2-aaml/actions/workflows/ci.yml)
[![Release](https://github.com/JakeRoxs/xcom2-aaml/actions/workflows/release.yml/badge.svg)](https://github.com/JakeRoxs/xcom2-aaml/actions/workflows/release.yml)

## Features

- Replaces the official game launcher
- Steam Workshop support — browse, subscribe, manage mods directly in the launcher
- Mod categories, profiles, and launch argument presets
- Configuration editor — change all of a mod's configs from within the launcher
- Configuration saving — persist changes to disk and into your settings file for backup
- Extensive search and filter options
- Editable mod descriptions
- Compatibility checks — duplicate IDs, class and screenlistener conflicts
- Cleans old `ModOverride` entries from `XComEngine.ini`
- Optional cleanup of unnecessary files to reduce memory footprint
- Profile loader auto-creates groups when the profile contains them

## Supported Games

| Game | Windows | Linux |
|------|---------|-------|
| XCOM 2 (War of the Chosen) | Yes | Yes |
| XCOM Chimera Squad | Yes | Experimental |

## Platforms

| Platform | Format | Status |
|----------|--------|--------|
| Windows (x64) | Single-file self-contained ZIP | Stable |
| Linux (x64) | Tar archive | Stable |
| Linux (x64) | AppImage | Stable |
| Flatpak | `io.github.jakeroxs.xcom2_aaml` | Sandbox qualification in progress |

Linux releases include both a tar archive and an AppImage. The AppImage preserves application data in standard XDG directories across upgrades; deleting the AppImage does not delete settings, profiles, logs, or game data.

## Installation

Download the latest release from [GitHub Releases](https://github.com/JakeRoxs/xcom2-aaml/releases).

### Windows

1. Download `AAML-{version}-win-x64.zip`
2. Extract anywhere
3. Run `AAML.exe`

No .NET runtime installation is required — the package is self-contained.

### Linux (tar)

```bash
tar -xzf AAML-{version}-linux-x64.tar.gz
cd AAML\ linux-x64
./AAML
```

### Linux (AppImage)

```bash
chmod +x AAML-{version}-linux-x86_64.AppImage
./AAML-{version}-linux-x86_64.AppImage
```

If FUSE is unavailable, use `--appimage-extract-and-run`.

### Flatpak

```bash
flatpak install flathub io.github.jakeroxs.xcom2_aaml
flatpak run io.github.jakeroxs.xcom2_aaml
```

For Flatpak testing with the Proton wrapper launch option:

```bash
flatpak run --command=aaml-proton-launch-option io.github.jakeroxs.xcom2_aaml
```

AAML delegates Steam startup through the host when the launcher itself is sandboxed.

## Configuration

On first launch, AAML will prompt you to select your game installation directory. You can change this at any time in the Dashboard settings page.

Application data (settings, profiles, logs) is stored in platform-standard locations:

| Platform | Location |
|----------|----------|
| Windows | `%LOCALAPPDATA%\AAML\` |
| Linux | `$XDG_CONFIG_HOME/aaml/`, `$XDG_DATA_HOME/aaml/`, `$XDG_STATE_HOME/aaml/` |

## CLI

AAML exposes a command-line interface for scripting and automation:

```bash
# List available UI actions
AAML --list-ui-actions

# Run a specific action
AAML --ui-action <name> [--option value] [--flag]
```

## Contributing

Community contributions are welcome. File issues and pull requests on [GitHub](https://github.com/JakeRoxs/xcom2-aaml).

Ways to contribute:

- Reviewing and testing pull requests
- Resolving open [issues](https://github.com/JakeRoxs/xcom2-aaml/issues)
- Suggesting improvements and filing bug reports
- Creating [wiki content](https://github.com/JakeRoxs/xcom2-aaml/wiki) (tutorials, troubleshooting guides, etc.)

### Building

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/JakeRoxs/xcom2-aaml.git
cd xcom2-aaml
dotnet build
dotnet test
```

Publish for Windows:

```bash
dotnet publish src/AAML.Avalonia/AAML.Avalonia.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Publish for Linux:

```bash
dotnet publish src/AAML.Avalonia/AAML.Avalonia.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

## License

Released under [GPLv3](LICENSE).

## Bug Reports

File issues on [GitHub](https://github.com/JakeRoxs/xcom2-aaml/issues). See the [FAQ](https://github.com/JakeRoxs/xcom2-aaml/wiki/FAQ#how-and-where-do-i-report-a-bugissue) for troubleshooting tips before reporting.

## Credit

- AAML is a continuation of the [Alternative Mod Launcher (AML)](https://github.com/jakeroxs/xcom2-aml) project.
- The logo is based on the original XCOM 2 logo by Firaxis, created by [Puma_The_Great](https://steamcommunity.com/id/Sexiest_Man_Alive/).
- XCOM 2 and War of the Chosen icons and GUI graphics are property of Firaxis.
