# Third-party notices

Torsher Player is built on third-party open-source software. This file is informational; the corresponding upstream licenses govern those components.

## Avalonia

- Components: Avalonia UI / Avalonia Desktop / Fluent theme
- Version used by this repository: 12.1.0
- Upstream: AvaloniaUI/Avalonia
- License: MIT (see the upstream project for the authoritative license text)

### Inter font

- Component: Inter font distributed through `Avalonia.Fonts.Inter`
- Upstream: rsms/inter
- Font license: SIL Open Font License 1.1 (OFL-1.1)


## Material.Icons / Material.Icons.Avalonia

- Components: Material icon metadata and Avalonia control
- Version used by this repository: 3.0.2
- Upstream: SKProCH/Material.Icons
- License: MIT
- Icon data is sourced by the upstream project from Material Design Icons. Torsher Player consumes the packaged icon set and does not vendor hand-authored SVG artwork.

## MonoTorrent

- Component: BitTorrent client/streaming library
- Version used by this repository: 3.0.2
- Upstream: alanmcgovern/monotorrent
- License: MIT

## mpv / libmpv

- Component: libmpv media engine
- Upstream: mpv-player/mpv
- The normal mpv build is distributed under GPL version 2 or later; mpv can also be built in a restricted LGPL configuration when GPL components are disabled.
- Torsher Player's project license is therefore declared as GPL-2.0-or-later for the public distribution model used here.

Windows CI uses a pinned third-party Windows build from `shinchiro/mpv-winbuild-cmake`. Exact archive URLs, versions and SHA-256 hashes live in `native/native-dependencies.json`. Those archives may contain additional codec/runtime components; their upstream notices and source information remain applicable.

## SDL3

- Component: SDL3 gamepad input
- Upstream: libsdl-org/SDL
- Windows version pinned by this repository: 3.4.10
- License: zlib
- Exact Windows archive URLs and SHA-256 hashes live in `native/native-dependencies.json`.

## No vendored native binaries in Git

Downloaded `.dll`, `.so` and `.dylib` files are intentionally ignored. The repository stores dependency metadata and checksums, not generated/downloaded native payloads.
