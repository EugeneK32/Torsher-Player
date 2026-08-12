#!/usr/bin/env python3
"""Create a release archive from a dotnet publish directory using stdlib only."""
from __future__ import annotations

import argparse
import plistlib
import shutil
import tarfile
import tempfile
import zipfile
from pathlib import Path


def copy_tree_contents(source: Path, destination: Path) -> None:
    for child in source.iterdir():
        target = destination / child.name
        if child.is_dir():
            shutil.copytree(child, target, symlinks=True)
        else:
            shutil.copy2(child, target, follow_symlinks=False)


def add_repository_files(repo_root: Path, destination: Path) -> None:
    for filename in ("README.md", "README.ru.md", "LICENSE", "THIRD_PARTY_NOTICES.md"):
        source = repo_root / filename
        if source.is_file():
            shutil.copy2(source, destination / filename)

    licenses = repo_root / "LICENSES"
    if licenses.is_dir():
        shutil.copytree(licenses, destination / "LICENSES")


def build_macos_bundle(publish: Path, staging: Path, version: str) -> None:
    app = staging / "TorsherPlayer.app"
    macos = app / "Contents" / "MacOS"
    resources = app / "Contents" / "Resources"
    macos.mkdir(parents=True)
    resources.mkdir(parents=True)
    copy_tree_contents(publish, macos)

    executable = macos / "TorsherPlayer"
    if executable.exists():
        executable.chmod(executable.stat().st_mode | 0o111)

    bundle_version = version.split("-", 1)[0].split("+", 1)[0]
    info = {
        "CFBundleDevelopmentRegion": "en",
        "CFBundleExecutable": "TorsherPlayer",
        "CFBundleIdentifier": "org.torsherplayer.TorsherPlayer",
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleName": "TorsherPlayer",
        "CFBundleDisplayName": "Torsher Player",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": bundle_version,
        "CFBundleVersion": bundle_version,
        "NSHighResolutionCapable": True,
    }
    with (app / "Contents" / "Info.plist").open("wb") as stream:
        plistlib.dump(info, stream, sort_keys=True)


def zip_dir(source: Path, output: Path) -> None:
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(source.rglob("*")):
            if path.is_dir():
                continue
            archive.write(path, path.relative_to(source.parent))


def tar_dir(source: Path, output: Path) -> None:
    with tarfile.open(output, "w:gz", compresslevel=9) as archive:
        archive.add(source, arcname=source.name, recursive=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()

    if not args.input.is_dir():
        raise SystemExit(f"publish directory not found: {args.input}")

    name = f"TorsherPlayer-{args.version}-{args.rid}"
    args.output_dir.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="torsherplayer-package-") as tmp:
        staging = Path(tmp) / name
        staging.mkdir()
        if args.rid.startswith("osx-"):
            build_macos_bundle(args.input, staging, args.version)
        else:
            copy_tree_contents(args.input, staging)
        add_repository_files(args.repo_root, staging)

        if args.rid.startswith("win-"):
            output = args.output_dir / f"{name}.zip"
            zip_dir(staging, output)
        else:
            output = args.output_dir / f"{name}.tar.gz"
            tar_dir(staging, output)

    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
