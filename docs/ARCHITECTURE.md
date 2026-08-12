# Architecture

## Goals

Torsher Player keeps a single cross-platform UI/playback codebase and isolates platform-specific behavior at the edges. The UI must not depend on native window handles, external mpv processes or Windows-only input APIs.

## Process model

```text
TorsherPlayer
 ├─ per-user single-instance guard
 ├─ Avalonia UI / XYFocus input routing / theme resources
 ├─ Material.Icons.Avalonia toolbar icon set
 ├─ Avalonia OpenGlControlBase video surface
 ├─ libmpv client + Render API
 ├─ libmpv event thread
 ├─ serialized libmpv command thread
 ├─ SDL3 gamepad polling thread
 ├─ optional MonoTorrent streaming session
 ├─ progress/callback worker
 ├─ native dependency resolver
 └─ best-effort OS sleep inhibitor
```

No external `mpv.exe`, named pipes, JSON IPC, `--wid` embedding or mpv OSC are involved.


## Single-instance model

Before Avalonia starts, `SingleInstanceGuard` opens a per-user lock file with exclusive sharing and keeps that handle for the whole process lifetime. A second process for the same user exits before creating a window. The OS releases the handle after a crash, so a stale file alone does not block later launches.

## UI, icons and directional focus

The playback surface uses ordinary Avalonia `Button` controls rather than hidden controller-only commands. `XYFocus.NavigationModes=Enabled` provides spatial arrow-key navigation. SDL gamepad directions call the same `FocusManager.TryMoveFocus` path, South/A raises the focused button's `Click` event, and East/B closes the current panel or performs the normal back/exit action.

Focus state is part of the UI contract: focused buttons use the `:focus` state for a high-contrast background/border so programmatic keyboard/gamepad focus is always visible. Open panels own focus explicitly, and Settings uses deterministic row/column navigation instead of relying on spatial heuristics. Material toolbar art comes from `Material.Icons.Avalonia`; icons are not maintained as local SVG files.

Theme colors are application resources applied by `ThemeCatalog`. Theme choice is persisted in `settings.json`; changing a theme does not replace controls or affect player state.

## Torrent streaming

A `.torrent` launch creates one `TorrentSession` backed by MonoTorrent streaming mode. The torrent is inspected for common video extensions and the UI exposes those files as episodes/media items. A selected torrent file is exposed by MonoTorrent over a loopback HTTP stream, which becomes the normal `LaunchOptions.Url`; libmpv therefore uses exactly the same playback path as it does for an ordinary HTTP source.

Switching torrent files keeps the torrent engine alive, creates a new stream URL and performs a normal libmpv `loadfile`. Each torrent file derives a distinct `media-key`, keeping resume/checkpoints/callback progress separate across episodes.

## Native dependency boundary

`NativeDependencyResolver` is the only assembly-wide `DllImportResolver`. Both mpv and SDL P/Invokes use synthetic logical names (`torsherplayer_mpv` and `torsherplayer_sdl3`) so the resolver can choose the correct native file for the current OS/architecture.

Lookup order:

1. executable/base directory;
2. `native/<RID>` and `runtimes/<RID>/native` beside the app;
3. directories from `TORSHER_PLAYER_NATIVE_PATH`;
4. common Homebrew/Linux library directories;
5. .NET single-file native extraction search directories;
6. the operating system's normal native loader.

Supported names include Windows `.dll`, Linux `.so` and macOS `.dylib` variants. SDL3 failure is non-fatal and disables controller input. libmpv is required for playback and produces the normal in-player engine error if unavailable.

## Rendering

libmpv is configured with `vo=libmpv`. `mpv_render_context_create` is initialized against the OpenGL context owned by Avalonia's `OpenGlControlBase`.

Only the OpenGL control lifecycle touches Avalonia's GL interface. The render callback performs no network/media commands. `MPV_RENDER_PARAM_BLOCK_FOR_TARGET_TIME=0` prevents mpv from deliberately sleeping the UI render path while waiting for a presentation timestamp.

mpv render wake-ups are coalesced: at most one Avalonia frame request is pending. The Avalonia UI is composited above the GL control, so the OSD/menus remain completely owned by Torsher Player.

The framebuffer is rendered with `MPV_RENDER_PARAM_FLIP_Y=1`, matching the orientation validated on the original Windows/TV deployment.

## libmpv commands and events

Player actions are enqueued onto `libmpv-commands` and submitted with `mpv_command_async`. Normal controls are serialized using `MPV_EVENT_COMMAND_REPLY`, avoiding completion-order surprises between rapid pause/seek/track operations.

The independent event worker observes player state and updates an immutable `PlayerSnapshot`. The Avalonia UI reads snapshots and does not make synchronous libmpv property queries.

Slow ancillary operations such as screenshots/external track loading are unsequenced so they cannot hold ordinary pause/seek controls behind I/O. Timeline hold-seeking is a UI-side scrub preview: the preview position accelerates smoothly while Left/Right is held, and libmpv receives a single absolute seek only when the direction is released. This avoids flooding the decoder/network source with repeated seeks.

## Gamepad

Gamepad support uses SDL3 instead of XInput. SDL is initialized from the Avalonia/main thread, while polling runs on `sdl3-gamepad` and posts semantic `GamepadAction` values back to the UI dispatcher.

The mapping is positional (South/East/West/North), so Xbox/PlayStation/Nintendo-layout devices are handled by SDL rather than by Windows-specific controller APIs. Directional input supports a dead zone and controlled key-repeat behavior for TV menus.

If SDL3 cannot be loaded or initialized, `NullGamepadService` is used and playback continues with keyboard/mouse.

## OSD activity state machine

- only meaningful user input calls `RegisterActivity()`;
- monotonic `Stopwatch` time drives the idle deadline;
- pointer movement below 4 logical pixels is ignored as jitter;
- playback/cache events never reset idle time;
- open menus and active seek dragging pin the controls visible;
- otherwise OSD and cursor hide after 4 seconds;
- any new input restores them immediately.

## Progress and resume

HTTP callbacks and local checkpoints run independently of playback. Resume priority is explicit `--resume`, then local checkpoint, then zero. A checkpoint near the completion threshold is considered watched and is not resumed in credits.

Callback/network failure is logged and never blocks libmpv or the UI.

## Failure isolation

- media/network failure -> in-player error/retry panel;
- real cache underrun -> separate buffering badge;
- renderer failure -> render context is released while GL is current and the engine can be rebuilt;
- callback failure -> log only;
- SDL/gamepad failure -> keyboard/mouse continue;
- Lampa unavailable -> local checkpoint continues;
- premature TorrServer/network EOF -> not treated as completion unless position is genuinely near file end.

## Power management

Keeping the display awake is a best-effort platform edge:

- Windows: `SetThreadExecutionState`;
- macOS: `caffeinate` child process;
- Linux: `systemd-inhibit` child process when available.

Failure to acquire an inhibitor never prevents playback.

## Hardware decoding

Default is `hwdec=auto-copy`: hardware decoding remains available while avoiding direct native texture sharing between mpv and the Avalonia/OpenGL surface. This costs a copy but keeps the rendering path predictable across GPU/window-system combinations.
