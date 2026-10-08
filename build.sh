#!/bin/bash
# Build Volumetric Explosions against the local KSP install.
#
#   ./build.sh            build into GameData/VolumetricExplosions here, then install it into KSP (also: ./build.sh fx)
#   ./build.sh check      compile only, install nothing
#   ./build.sh dist       build into GameData/ here without installing anything
#   ./build.sh shaders    pack the four shaders again from tools/shaderpack/*.glsl (after changing one)
#   ./build.sh package    dist, then a zip of it in dist/
#
# Needs the .NET SDK (`brew install dotnet`) and, for the mod's library, a C compiler. With OTHERS=1, and Zig
# (on the path or in ~/.ksp-ai-bridge/zig) or MinGW, it also builds the library for Windows and Linux, which
# nobody has run yet.
# Override the game location with KSP_DIR=/path/to/KSP.
set -euo pipefail

cd "$(dirname "$0")"
MODE="${1:-install}"
. tools/build/common.sh
. tools/build/native.sh
SRC=src/VolumetricExplosions
OUT=GameData/VolumetricExplosions

build() {
  mkdir -p "$OUT/PluginData"
  compile "$OUT/VolumetricExplosions.dll" "" "$SRC"
  rm -f "$OUT/PluginData/"*
  cp "$SRC"/Textures/*.png "$SRC"/Textures/*.bin "$SRC"/Shaders/*.bundle "$OUT/PluginData/"
  native "$OUT/PluginData"
  others "$OUT/PluginData"
  cp "$SRC/settings.cfg" "$OUT/settings.cfg"
}

case "$MODE" in
  check)
    compile "$TMP/VolumetricExplosions.dll" "" "$SRC"
    compile "$TMP/VolumetricExplosions.Dev.dll" "DEV" "$SRC"
    native "$TMP"
    echo "ok: compiles"
    ;;
  dist)
    build
    echo "ok: built into $OUT"
    ;;
  install|fx)
    build
    FX="$KSP_DIR/GameData/VolumetricExplosions"
    mkdir -p "$FX/PluginData"
    cp "$OUT/VolumetricExplosions.dll" "$FX/"
    rm -f "$FX/PluginData/"*
    cp "$OUT"/PluginData/* "$FX/PluginData/"
    # (the game here has no use for the other systems' libraries; they are for the copy that is given out)
    case "$(uname -s)" in Darwin) rm -f "$FX/PluginData/vfxgrid.dll" "$FX/PluginData/vfxgrid.so" ;; Linux) rm -f "$FX/PluginData/vfxgrid.dll" "$FX/PluginData/vfxgrid.dylib" ;; esac
    [ -f "$FX/settings.cfg" ] || cp "$OUT/settings.cfg" "$FX/settings.cfg"     # keep the player's own settings
    echo "ok: Volumetric Explosions installed to $FX (restart KSP to load it)"
    ;;
  shaders)
    for name in volume enlarge mark shock; do python3 tools/shaderpack/make_bundle.py "$KSP_DIR" "$SRC/Shaders/$name.bundle" "$name"; done
    ;;
  package)
    build
    VERSION="$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' "$SRC/Addon.cs")"
    STAGE="$TMP/stage/VolumetricExplosions"; mkdir -p "$STAGE/GameData" dist
    cp -R "$OUT" "$STAGE/GameData/"; sed -e "s/@VERSION@/$VERSION/g" "$SRC/INSTALL.txt" > "$STAGE/README.txt"
    cp LICENSE "$STAGE/GameData/VolumetricExplosions/LICENSE.txt"
    rm -f "dist/VolumetricExplosions-$VERSION.zip"
    (cd "$TMP/stage" && zip -r -q -X "$OLDPWD/dist/VolumetricExplosions-$VERSION.zip" VolumetricExplosions -x '*.DS_Store')
    echo "dist/VolumetricExplosions-$VERSION.zip"
    ;;
  *)
    echo "usage: ./build.sh [install|check|dist|shaders|package]" >&2; exit 2
    ;;
esac
