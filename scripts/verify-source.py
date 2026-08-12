#!/usr/bin/env python3
"""Fast repository checks that do not require the .NET SDK."""
from __future__ import annotations

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ERRORS: list[str] = []


def fail(message: str) -> None:
    ERRORS.append(message)


def parse_xml(relative: str) -> None:
    path = ROOT / relative
    try:
        ET.parse(path)
    except Exception as exc:
        fail(f"invalid XML {relative}: {exc}")


def parse_json(relative: str):
    path = ROOT / relative
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        fail(f"invalid JSON {relative}: {exc}")
        return None


for file in (
    "src/TorsherPlayer/App.axaml",
    "src/TorsherPlayer/MainWindow.axaml",
    "src/TorsherPlayer/TorsherPlayer.csproj",
    "src/TorsherPlayer/app.manifest",
):
    parse_xml(file)

manifest = parse_json("native/native-dependencies.json")
global_json = parse_json("global.json")
if global_json:
    sdk = global_json.get("sdk", {})
    if not str(sdk.get("version", "")).startswith("10.0."):
        fail("global.json must select the .NET 10 SDK line")
    if sdk.get("rollForward") != "latestFeature":
        fail("global.json rollForward must be latestFeature")

if manifest:
    for rid in ("win-x64", "win-arm64"):
        spec = manifest.get("windows", {}).get(rid)
        if not spec:
            fail(f"native manifest has no {rid}")
            continue
        for dep in ("mpv", "sdl3"):
            item = spec.get(dep, {})
            sha = item.get("sha256", "")
            if not re.fullmatch(r"[0-9a-fA-F]{64}", sha):
                fail(f"{rid}/{dep} SHA-256 is not 64 hex chars")
            if not str(item.get("url", "")).startswith("https://"):
                fail(f"{rid}/{dep} URL is not HTTPS")

source_files = list((ROOT / "src").rglob("*.cs")) + list((ROOT / "src").rglob("*.csproj"))
source_text = "\n".join(p.read_text(encoding="utf-8") for p in source_files)

# Keep the public product identity clean after the rename. The external Lampa
# integration is still intentionally named Lampa; only the former player brand
# and its technical identifiers are forbidden.
legacy_brand_tokens = (
    "Lampa" + "Player",
    "lampa" + "player",
    "LAMPA" + "_PLAYER",
)
for path in ROOT.rglob("*"):
    if not path.is_file() or ".git" in path.parts or "LICENSES" in path.parts:
        continue
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        continue
    for token in legacy_brand_tokens:
        if token in text:
            fail(f"legacy product brand remains in {path.relative_to(ROOT)}")

for forbidden in (
    "XInputGamepadService",
    "Endpne.LibMPV.Windows",
    "input-ipc-server",
    "--wid",
    "MessageBox.Show",
    "SystemDecorations =",
):
    if forbidden in source_text:
        fail(f"legacy/forbidden source token remains: {forbidden}")

resolver = (ROOT / "src/TorsherPlayer/Infrastructure/NativeDependencyResolver.cs").read_text(encoding="utf-8")
for required in ("torsherplayer_mpv", "torsherplayer_sdl3", "libmpv.so", "libmpv.dylib", "SDL3.dll"):
    if required not in resolver:
        fail(f"native resolver is missing {required}")

# Every AXAML click handler must exist in the code-behind.
axaml = (ROOT / "src/TorsherPlayer/MainWindow.axaml").read_text(encoding="utf-8")
code = (ROOT / "src/TorsherPlayer/MainWindow.axaml.cs").read_text(encoding="utf-8")
handlers = sorted(set(re.findall(r'\bClick="([A-Za-z_][A-Za-z0-9_]*)"', axaml)))
for handler in handlers:
    if not re.search(rf"\b{re.escape(handler)}\s*\(", code):
        fail(f"AXAML handler not found in code-behind: {handler}")

# Duplicate x:Name values make generated fields ambiguous and often fail XAML compilation.
names = re.findall(r'\bx:Name="([A-Za-z_][A-Za-z0-9_]*)"', axaml)
for name in sorted(set(names)):
    if names.count(name) > 1:
        fail(f"duplicate x:Name in MainWindow.axaml: {name}")

# The TV interaction contract is focus-first: visible XYFocus plus one icon library.
for required in (
    'XYFocus.NavigationModes="Enabled"',
    'Material.Icons.Avalonia',
    'SourcePickerPanel',
    'EpisodesButton',
    'x:Name="Progress"',
    'Focusable="True"',
    'x:Name="StatusProgress"',
):
    if required not in axaml:
        fail(f"MainWindow UI contract missing: {required}")

# Arrow keys must enter the focus/navigation router. The router may intentionally
# scrub the seek Slider when that Slider itself owns focus; what we forbid is a
# hidden global playback shortcut wired directly in Window_KeyDown.
arrow_router = re.search(
    r'if \(e\.Key is Key\.Left or Key\.Right or Key\.Up or Key\.Down\)(.*?)e\.Handled = true;\s*return;',
    code,
    re.S,
)
if arrow_router is None or "HandleDirectionalInput" not in arrow_router.group(1):
    fail("arrow keys are not routed through focus navigation")


for forbidden_hint in (
    "Стрелки — выбор",
    "Enter / A",
    "Tab тоже работает",
    "FocusedActionText",
):
    if forbidden_hint in axaml:
        fail(f"obsolete on-screen input hint remains: {forbidden_hint}")


# Input is discoverable and symmetric across keyboard/gamepad.
for required in (
    "FocusManager?.TryMoveFocus",
    "ActivateFocused();",
    "e.Key == Key.Enter",
    "GamepadAction.A",
    "GamepadAction.B",
):
    if required not in code:
        fail(f"focus/input contract missing: {required}")

# Torrent and theme features must remain wired into the public player.
for required in ("TorrentSession.OpenAsync", "SelectTorrentMediaAsync", "ThemeCatalog.Apply"):
    if required not in code:
        fail(f"4.2 feature contract missing: {required}")

theme_code = (ROOT / "src/TorsherPlayer/Infrastructure/ThemeCatalog.cs").read_text(encoding="utf-8")
for theme in ("Midnight", "OLED", "Ocean", "Amber", "Forest", "Light"):
    if f'"{theme}"' not in theme_code:
        fail(f"theme missing from ThemeCatalog: {theme}")

# Dynamic resources referenced by the main view should have defaults in App.axaml.
app_axaml = (ROOT / "src/TorsherPlayer/App.axaml").read_text(encoding="utf-8")
resources = set(re.findall(r"\{DynamicResource\s+([^}]+)\}", axaml))
for resource in sorted(resources):
    if resource not in app_axaml:
        fail(f"DynamicResource has no default in App.axaml: {resource}")

# Basic project/brand contract checks.
if not (ROOT / "TorsherPlayer.sln").is_file():
    fail("TorsherPlayer.sln is missing")
csproj = (ROOT / "src/TorsherPlayer/TorsherPlayer.csproj").read_text(encoding="utf-8")
for required in (
    "<AssemblyName>TorsherPlayer</AssemblyName>",
    "<RootNamespace>TorsherPlayer</RootNamespace>",
    "<Product>Torsher Player</Product>",
):
    if required not in csproj:
        fail(f"product identity missing from project: {required}")
for required in (
    "net8.0",
    "Avalonia\" Version=\"12.1.0",
    "Material.Icons.Avalonia\" Version=\"3.0.2",
    "MonoTorrent\" Version=\"3.0.2",
    "win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64",
):
    if required not in csproj:
        fail(f"project contract missing: {required}")

# Single-instance protection must happen before Avalonia creates a window.
program = (ROOT / "src/TorsherPlayer/Program.cs").read_text(encoding="utf-8")
if "SingleInstanceGuard.TryAcquire" not in program:
    fail("Program.cs does not acquire the single-instance guard")

# Rough C# brace balance (strings/comments stripped) catches accidental structural edits
# without requiring Roslyn/.NET in lightweight CI/source validation environments.
def csharp_brace_balance(text: str) -> tuple[int, bool]:
    text = re.sub(r'@"(?:""|[^"])*"', '""', text, flags=re.S)
    text = re.sub(r'\$?"(?:\\.|[^"\\])*"', '""', text, flags=re.S)
    text = re.sub(r"'(?:\\.|[^'\\])'", "''", text)
    text = re.sub(r'//.*', '', text)
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    balance = 0
    negative = False
    for ch in text:
        if ch == '{': balance += 1
        elif ch == '}':
            balance -= 1
            if balance < 0: negative = True
    return balance, negative

for path in (ROOT / "src").rglob("*.cs"):
    balance, negative = csharp_brace_balance(path.read_text(encoding="utf-8"))
    if balance != 0 or negative:
        fail(f"unbalanced C# braces: {path.relative_to(ROOT)} (balance={balance})")

if ERRORS:
    print("Repository verification FAILED:", file=sys.stderr)
    for error in ERRORS:
        print(f"  - {error}", file=sys.stderr)
    raise SystemExit(1)

print(f"Repository verification OK: {len(handlers)} AXAML click handlers, {len(resources)} dynamic resources.")
