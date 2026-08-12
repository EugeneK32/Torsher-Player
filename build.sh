#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RID=linux-x64 ;;
    Linux-aarch64|Linux-arm64) RID=linux-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Darwin-arm64) RID=osx-arm64 ;;
    *) echo "Unsupported host. Pass a RID explicitly." >&2; exit 2 ;;
  esac
fi

dotnet restore "$ROOT/src/TorsherPlayer/TorsherPlayer.csproj" -r "$RID"
dotnet publish "$ROOT/src/TorsherPlayer/TorsherPlayer.csproj" \
  -c Release -r "$RID" --no-restore --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -p:DebugType=None -p:DebugSymbols=false \
  -o "$ROOT/dist/$RID-portable"

echo "Published: $ROOT/dist/$RID-portable"
