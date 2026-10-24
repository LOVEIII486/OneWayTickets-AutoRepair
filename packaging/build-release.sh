#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-0.3.5}"
BEPINEX_VERSION="5.4.23.5"
PKG_NAME="OneWayTickets_AutoRepair_v${VERSION}_BepInEx${BEPINEX_VERSION}_win64"

GAME_ROOT="${GAME_ROOT:-${OWT_GAME_ROOT:-}}"
GAME_ROOT="${GAME_ROOT%/}"
GAME_ROOT="${GAME_ROOT%\\}"
DIST="${DIST_DIR:-$REPO_ROOT/dist}"
CACHE="${CACHE_DIR:-$REPO_ROOT/_cache}"
BEPINEX_ZIP="${BEPINEX_ZIP:-$CACHE/BepInEx_win_x64_${BEPINEX_VERSION}.zip}"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip"
EXTRACTED="$CACHE/bepinex-${BEPINEX_VERSION}"
STAGE="$DIST/staging"

if [ -z "$GAME_ROOT" ]; then
    echo "GAME_ROOT is not set." >&2
    echo "Example: GAME_ROOT='C:/Program Files (x86)/Steam/steamapps/common/再再启程 One Way Tickets' bash packaging/build-release.sh" >&2
    exit 1
fi

if [ ! -f "$GAME_ROOT/再再启程_Data/Managed/World.dll" ]; then
    echo "Game not found at: $GAME_ROOT" >&2
    exit 1
fi

if [ ! -f "$BEPINEX_ZIP" ]; then
    mkdir -p "$CACHE"
    echo "==> Downloading BepInEx ${BEPINEX_VERSION}"
    curl -L --fail --retry 3 -o "$BEPINEX_ZIP" "$BEPINEX_URL"
fi

if [ ! -d "$EXTRACTED/BepInEx/core" ]; then
    mkdir -p "$EXTRACTED"
    echo "==> Extracting BepInEx"
    unzip -q "$BEPINEX_ZIP" -d "$EXTRACTED"
fi

echo "==> Build plugin"
( cd "$REPO_ROOT" && dotnet build -c Release -v minimal -p:GameRoot="${GAME_ROOT}/" -p:DeployToGame=false )

PLUGIN_DLL="$REPO_ROOT/bin/Release/netstandard2.1/OWT.AutoRepair.dll"
if [ ! -f "$PLUGIN_DLL" ]; then
    echo "Plugin DLL not found after build: $PLUGIN_DLL" >&2
    exit 1
fi

echo "==> Assemble staging"
rm -rf "$STAGE"
mkdir -p "$STAGE/BepInEx/core" "$STAGE/BepInEx/plugins" "$STAGE/BepInEx/config" "$STAGE/LICENSES"
cp -a "$EXTRACTED/BepInEx/core/." "$STAGE/BepInEx/core/"
cp -a "$EXTRACTED/winhttp.dll" "$EXTRACTED/doorstop_config.ini" "$EXTRACTED/.doorstop_version" "$EXTRACTED/changelog.txt" "$STAGE/"
cp -a "$PLUGIN_DLL" "$STAGE/BepInEx/plugins/"
cp -a "$REPO_ROOT/packaging/config/owt.autorepair.cfg" "$STAGE/BepInEx/config/"
cp -a "$REPO_ROOT/packaging/LICENSES/." "$STAGE/LICENSES/"
cp -a "$REPO_ROOT/packaging/安装说明.txt" "$REPO_ROOT/packaging/THIRD-PARTY-NOTICES.txt" "$STAGE/"

echo "==> Add UTF-8 BOM to the Chinese guide"
( cd "$STAGE" && python -I - "安装说明.txt" <<'PY'
import sys
p = sys.argv[1]
data = open(p, 'rb').read()
if not data.startswith(b'\xef\xbb\xbf'):
    open(p, 'wb').write(b'\xef\xbb\xbf' + data)
PY
)

echo "==> Create zip"
( cd "$DIST" && python -I - "staging" "${PKG_NAME}.zip" <<'PY'
import os
import sys
import zipfile

stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(stage):
        dirs.sort()
        files.sort()
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, stage)
            z.write(full, rel)
print('written', out)
PY
)

echo "==> Write checksum"
( cd "$DIST" && sha256sum "${PKG_NAME}.zip" > SHA256SUMS.txt && cat SHA256SUMS.txt )

echo "==> Package: $DIST/${PKG_NAME}.zip"
