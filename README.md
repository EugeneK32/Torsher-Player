# Torsher Player

A TV-oriented external media player for **Lampa**, built with **.NET 8 + Avalonia + libmpv**. **Torsher Player** owns the UI, input and Lampa progress/resume protocol; libmpv is used in-process only as the media engine.

> **Project status:** public pre-release. Windows is the most battle-tested target. Linux and macOS builds are part of CI/release automation, but still need wider real-machine testing.

[Русская версия README](README.ru.md)

## Why it exists

Browser/Electron playback can fail on containers, codecs or audio/subtitle combinations that mpv handles correctly. Torsher Player is intended to keep Lampa's launcher/library workflow while providing a native, controller-first playback surface.

There is no external `mpv.exe`, JSON IPC, `--wid` embedding or stock mpv OSC. Video is rendered through the **libmpv Render API** into Avalonia's `OpenGlControlBase`, under Torsher Player's own OSD and menus.

## Features

- custom scalable TV UI (`0.9×`–`2.5×`, default `1.8×`);
- OSD/cursor auto-hide after 4 seconds of real inactivity;
- play/pause, seeking, volume/mute and fullscreen;
- audio track selection;
- subtitle selection/off, delay and scale;
- audio delay and playback speed;
- external subtitle/audio files and drag & drop;
- screenshots;
- buffering/error/retry UI inside the player (no Windows message boxes);
- resume/checkpoints and HTTP callback events for Lampa integration;
- SDL3 gamepad input plus keyboard/mouse;
- visible directional focus navigation for TV/controller use;
- built-in `.torrent` streaming with an episode/file picker;
- 13 persisted visual themes, including three light palettes;
- per-user single-instance protection;
- one codebase for Windows, Linux and macOS.

## Platform status

| Target | Build in CI | Runtime native dependencies |
|---|---:|---|
| Windows x64 | Yes | Pinned libmpv + SDL3 bundled into release archive |
| Windows arm64 | Yes | Pinned libmpv + SDL3 bundled into release archive |
| Linux x64 | Yes | system `libmpv.so`; SDL3 optional for gamepad |
| Linux arm64 | Yes | system `libmpv.so`; SDL3 optional for gamepad |
| macOS x64 | Yes | system `libmpv.dylib`; SDL3 optional for gamepad |
| macOS arm64 | Yes | system `libmpv.dylib`; SDL3 optional for gamepad |

macOS release archives contain a `TorsherPlayer.app` bundle, currently unsigned/unnotarized.

## Architecture

```text
TorsherPlayer
 ├─ Avalonia UI / OpenGL surface
 ├─ libmpv Render API
 ├─ serialized libmpv command worker
 ├─ libmpv event worker
 ├─ SDL3 gamepad worker
 ├─ progress/callback worker
 └─ platform-native dependency resolver
```

The single native resolver understands Windows `.dll`, Linux `.so` and macOS `.dylib` names. It first checks bundled/runtime directories, `TORSHER_PLAYER_NATIVE_PATH`, common Unix package-manager library directories, and finally the OS loader.
`TORSHER_PLAYER_NATIVE_PATH` may contain multiple directories separated by the platform path separator (`;` on Windows, `:` on Unix). It is useful for custom libmpv/SDL3 installations without copying them into the application directory.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Prerequisites

- .NET 10 SDK for source builds. The app still targets `net8.0`; `global.json` selects a modern .NET 10 SDK/compiler for Avalonia 12 source generation and CI.
- Windows native dependencies are downloaded reproducibly by `scripts/fetch-native.ps1`; 7-Zip is required to unpack the pinned mpv archive.
- Linux/macOS currently require a system libmpv runtime. SDL3 is optional; without it playback still works but controller support is disabled.

## Build

### Windows x64

```powershell
./scripts/fetch-native.ps1 -Rid win-x64
dotnet restore src/TorsherPlayer/TorsherPlayer.csproj -r win-x64
dotnet build src/TorsherPlayer/TorsherPlayer.csproj -c Release -r win-x64
```

Or create a self-contained portable build:

```bat
build.cmd
```

ARM64:

```powershell
./scripts/build.ps1 -Rid win-arm64 -Target portable
```

### Linux / macOS

```bash
./build.sh
```

You can also pass an explicit RID, e.g. `./build.sh linux-arm64`.

## Run

Direct media/HTTP stream:

```text
TorsherPlayer --url <media-url-or-path> [options]
```

Torrent file:

```text
TorsherPlayer --torrent <file.torrent> [--torrent-file-index N] [options]
```

A positional `.torrent` path is also accepted. When a torrent contains several video files, TorsherPlayer shows a controller/keyboard-navigable picker and keeps the same list available from the playback toolbar for episode switching.

Example:

```text
TorsherPlayer \
  --url "http://127.0.0.1:8090/stream/..." \
  --title "Example — S01E01" \
  --media-key "tmdb:123:s01:e01" \
  --resume 812.4 \
  --ui-scale 1.8
```

Important integration options:

- `--callback <url>` and optional `--callback-token <token>`;
- `--progress-file <path>`;
- repeatable `--sub-file`, `--audio-file`, `--http-header`;
- `--user-agent`, `--referrer`;
- `--completed-at 0..1`;
- `--progress-every 2..60`;
- `--windowed`.

See [docs/LAMPA_INTEGRATION.md](docs/LAMPA_INTEGRATION.md) for the callback contract.

## Controls

The primary interaction model is intentionally discoverable rather than shortcut-driven:

- **Keyboard:** arrow keys move the visible focus, `Enter` activates the focused control, `Esc` goes back. `Tab` also works. On the focused timeline, a Left/Right tap seeks ±10 seconds; holding a direction moves a smooth accelerating preview and commits one real seek when released.
- **Gamepad:** D-pad/left stick move focus, South/A activates, East/B goes back.
- **Mouse/touch:** regular buttons remain clickable; wheel changes volume, double click toggles fullscreen, and the seekbar supports pointer dragging.

Optional convenience controls remain available: `Space/P` play/pause, `M` mute, `F/F11` fullscreen, `Q` quit, `[`/`]` subtitle delay, controller Menu = settings, View = fullscreen and shoulders = ±60 seconds.

The active element receives a strong focus highlight. The OSD still auto-hides after four seconds of real inactivity and returns on the next input.

## Themes

The Settings panel includes 13 persisted themes: **Midnight, OLED, Ocean, Amber, Forest, Crimson, Violet, Nord, Rose, Cobalt, Light, Paper and Ivory**. `Light` is a clean cool palette, `Paper` uses warmer paper/graphite tones with a slate accent, and `Ivory` is a cream/amber palette. All toolbar artwork comes from one Material Design icon package and uses the same control foreground/focus system; Torsher Player does not ship hand-drawn SVG toolbar icons.

## Torrent playback

`--torrent` uses MonoTorrent in streaming mode. Multi-video torrents are filtered to common video extensions and exposed as an in-player series/file list. Each selected file gets its own derived media key, so local resume/checkpoints and Lampa progress callbacks do not collide between episodes.

Torrent payload is downloaded into Torsher Player's local application-data torrent directory. This is a streaming feature, not a content catalog: Torsher Player does not search for or supply torrents.

## Data and logs

Torsher Player uses the platform's local application-data directory through .NET and stores:

```text
TorsherPlayer/settings.json
TorsherPlayer/state/*.json
TorsherPlayer/logs/torsherplayer.log
```

The exact base directory is OS-specific.

## CI and releases

- `.github/workflows/ci.yml` runs repository verification and compiles all six release RIDs on pushes and pull requests.
- `.github/workflows/release.yml` is tag-driven (`v*`) and publishes six RIDs, packages them, generates SHA-256 checksums and creates a GitHub Release.
- Windows native archives are pinned by URL + SHA-256 in `native/native-dependencies.json` rather than committed to Git.

See [docs/RELEASING.md](docs/RELEASING.md).

## License

Torsher Player is licensed under **GNU GPL version 2 or, at your option, any later version (GPL-2.0-or-later)**. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
