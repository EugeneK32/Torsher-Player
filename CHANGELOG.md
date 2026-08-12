# Changelog

## 4.2.1

- Rebranded the application and all technical identifiers to **Torsher Player**: solution/project, namespaces, executable/app bundle, release artifacts, local data/log paths, native resolver identifiers, CI and documentation.
- Native library override environment variable is now `TORSHER_PLAYER_NATIVE_PATH`.

- Made the playback seek bar a first-class focus target. Up from the bottom OSD controls focuses the timeline, a short Left/Right tap seeks by 10 seconds, holding either direction previews a smoothly accelerating seek, and the real absolute seek is committed once on release.
- Added SDL gamepad button-release events so hold-to-seek has the same deterministic behavior on keyboard and controllers.
- Confined keyboard and gamepad directional navigation to the currently open surface. Opening Settings, Audio, Subtitles or Episodes now transfers focus into that panel instead of leaving focus behind on the OSD.
- Switched controller-first button highlighting to the actual `:focus` state, fixed stale focus after opening panels, and removed the duplicate native focus decoration around the seek bar.
- Reworked Settings into aligned columns with deterministic row/column navigation and a wider panel.
- Fixed seek-thumb/tracker geometry so it scales with the UI instead of clipping at larger scale values.
- Added a compact OSD volume editor and separated the audio-track and volume actions with distinct headphone/speaker icons.
- Expanded the persisted theme set to 13 themes, including three distinct light palettes: Light, Paper and Ivory.
- Kept all OSD action buttons in one left-aligned toolbar group.
- Removed on-screen keyboard/gamepad instruction text from the OSD, side panels and torrent picker.
- Replaced the static “opening video” startup message with an indeterminate loading state and staged live status while the source is connecting/being analysed.
- Increased mpv HTTP network timeout from the default 60 seconds to 180 seconds for HTTP/HTTPS sources, including the local torrent streaming bridge.
- Added up to two automatic startup retries for HTTP/torrent sources before showing a terminal playback error.
- Fixed the Windows 7-Zip resolver when only one common installation path exists.
- Updated `global.json` to use the .NET 10 SDK line with `latestFeature`, while the application continues to target `net8.0`.
- Removed the nullable logger warning during `MainWindow` construction.

## 4.2.0

- Added a cross-platform per-user single-instance guard to prevent duplicate player windows.
- Reworked the playback OSD into a classic icon toolbar using `Material.Icons.Avalonia`; no hand-authored SVG icons are used.
- Reworked keyboard/gamepad control around visible directional focus: arrows/D-pad/left stick move focus, Enter/A activates, Esc/B goes back.
- Added strong `:focus-visible` styling so the active control is obvious on TV/desktop.
- Added built-in `.torrent` launch support with MonoTorrent streaming, multi-video file/episode picker and in-player episode switching.
- Added per-torrent-file media keys so resume/progress checkpoints remain independent between episodes.
- Added five persisted UI themes: Midnight, OLED, Ocean, Amber, Forest and Light.
- Kept OSD inactivity hiding, mouse/touch support and optional convenience shortcuts.

## 4.1.0

- Prepared the Avalonia codebase for a public cross-platform GitHub repository.
- Replaced Windows-only XInput polling with SDL3 gamepad input and a no-gamepad fallback.
- Added one assembly-wide native resolver for libmpv and SDL3 across Windows (`.dll`), Linux (`.so`) and macOS (`.dylib`).
- Added reproducible Windows native dependency manifests with pinned URLs and SHA-256 verification; downloaded binaries are no longer committed to Git.
- Added Windows x64/arm64 build tooling plus Linux/macOS self-contained publish tooling.
- Added GitHub Actions CI compilation for all six release RIDs on Windows/Linux/macOS runners.
- Added tag-driven release automation for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`, including packaged archives and `SHA256SUMS.txt`.
- Added `global.json`, repository verification, Dependabot, issue templates, contributing/security/release documentation and bilingual README files.
- Made sleep/display inhibition best-effort on Windows, macOS and Linux.
- Declared GPL-2.0-or-later project licensing for the public libmpv-based distribution model.

## 4.0.1

- Updated Avalonia 12 window decoration usage to `WindowDecorations.Full/None`.
- Fixed native libmpv deployment: the runtime DLL is now resolved explicitly from `Endpne.LibMPV.Windows`.
- Normal Visual Studio / `dotnet build` copies the x64 libmpv runtime next to `TorsherPlayer.exe`.
- `build.cmd`/publish passes the resolved native directory to MSBuild so portable and single-file publishing include the runtime payload correctly.
- Prevented `libmpv.lib` (the linker import library) from being treated as the runtime library.

## 4.0.0

- Migrated the full custom player interface from WPF/OpenTK to **Avalonia 12.1**.
- Kept the proven C#/.NET 8 + in-process libmpv architecture; this is not an mpv launcher and does not use mpv's OSC.
- Replaced `GLWpfControl` with Avalonia `OpenGlControlBase`; libmpv renders into Avalonia's current OpenGL framebuffer.
- Preserved the dedicated serialized libmpv command thread and separate event thread so playback commands never block the UI/render path.
- Preserved the real-machine validated `MPV_RENDER_PARAM_FLIP_Y=1` orientation.
- Fixed OSD auto-hide with a monotonic user-activity state machine. Playback state updates no longer keep the controls alive.
- Pointer jitter below 4 logical pixels no longer resets OSD idle time.
- Side menus and active seek dragging intentionally pin the OSD; otherwise it hides after 4 seconds.
- Cursor hides together with the OSD and returns immediately on user activity.
- Preserved dynamic TV UI scale `0.9–2.5×` with persisted settings.
- Preserved audio/subtitle selection, subtitle off, subtitle/audio delay, subtitle scale, speed, screenshot and drag/drop external tracks.
- Preserved XInput gamepad, keyboard/mouse controls, Lampa callback/resume protocol and local checkpoint fallback.
- Removed WPF/OpenTK dependencies and WPF-specific native interop.
- Simplified publishing: `Endpne.LibMPV.Windows` is consumed as a normal NuGet native asset; no manual native-DLL injection property is used.

## 3.0.1

- Fixed WPF resource/Thickness compile issues and native libmpv packaging during the previous implementation.

## 3.0.0

- First stable C#/.NET 8 + in-process libmpv Render API implementation. Replaced the abandoned Go/Win32 experiments.
