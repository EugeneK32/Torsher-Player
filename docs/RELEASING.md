# Releasing

GitHub Actions owns release packaging. A tag matching `v*` starts `.github/workflows/release.yml`.

## Release targets

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Windows artifacts include the pinned native payload fetched from `native/native-dependencies.json`. Linux/macOS artifacts are self-contained for .NET/Avalonia but currently expect libmpv to be available from the system loader; SDL3 is optional and gamepad support gracefully disables itself if SDL3 is absent.

## Steps

1. Ensure CI is green on the commit to release and the worktree is clean.
2. Run `python3 scripts/verify-source.py` and a Release build for at least the primary development RID.
3. Update `CHANGELOG.md` and confirm the tag version is the version you intend to publish.
4. Review `native/native-dependencies.json` and `THIRD_PARTY_NOTICES.md` if a native dependency changed.
5. Create and push an annotated tag, for example:

   ```bash
   git tag -a v4.2.1 -m "Torsher Player 4.2.1"
   git push origin v4.2.1
   ```

6. The release workflow publishes all RIDs, creates archives, generates `SHA256SUMS.txt`, and creates a GitHub Release. Download at least one produced archive and smoke-test it before announcing the release.

## macOS signing

The current workflow packages the publish output as `TorsherPlayer.app` inside the macOS archive, but the app is unsigned and unnotarized. Signing/notarization should be added only after the repository has the required Apple Developer credentials configured as GitHub Actions secrets. Do not add private certificates or credentials to the repository.

## License / native payload review

The project declares GPL-2.0-or-later because the default mpv/libmpv configuration is GPL-licensed. Before changing the Windows mpv build or redistributing a different binary bundle, re-check that build's license/source obligations and update the third-party notices.
