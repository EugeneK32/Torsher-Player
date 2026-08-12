# Contributing

Thanks for helping improve Torsher Player.

## Before opening a pull request

1. Keep playback behavior and the TV-oriented UI changes focused and reviewable.
2. Run `python3 scripts/verify-source.py` (or `python scripts/verify-source.py` on Windows).
3. Run a Release build for your platform/RID.
4. If the change touches playback, rendering, input or resume/progress, run the relevant checks from `docs/TESTING.md`.
5. Do not commit downloaded native binaries from `native/<RID>`.

## Native dependencies

Windows native dependencies are pinned by URL and SHA-256 in `native/native-dependencies.json` and are downloaded by `scripts/fetch-native.ps1`. Linux and macOS development builds use system-provided libmpv and optionally SDL3.

If you update a pinned dependency, update its version, URL, checksum, `THIRD_PARTY_NOTICES.md`, and describe the reason in the pull request.

## Code style

The repository uses `.editorconfig`. Prefer small platform abstractions over OS checks spread through the UI/player core.

## Bug reports

For playback/rendering bugs, attach the relevant tail of the Torsher Player log and include OS, CPU architecture, GPU/driver, media source (local/TorrServer/HTTP), and whether hardware decoding was enabled.
