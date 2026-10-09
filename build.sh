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
  rm -f "$OUT/PluginData/"* "$OUT/settings.cfg"
  cp "$SRC"/Textures/*.png "$SRC"/Textures/*.bin "$SRC"/Shaders/*.bundle "$OUT/PluginData/"
  native "$OUT/PluginData"
  others "$OUT/PluginData"
  # (the settings are in PluginData, which the game does not read: a .cfg anywhere else is one of the game's configs, and changing it costs a slow start)
  cp "$SRC/settings.cfg" "$OUT/PluginData/settings.cfg"
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
    # (everything of the mod's is put in afresh but the player's own settings, which are kept: in PluginData, or beside the library where they used to be, from where the mod moves them itself)
    find "$FX/PluginData" -maxdepth 1 -type f ! -name settings.cfg -delete
    for f in "$OUT"/PluginData/*; do
      if [ "$(basename "$f")" = settings.cfg ] && { [ -f "$FX/PluginData/settings.cfg" ] || [ -f "$FX/settings.cfg" ]; }; then continue; fi
      cp "$f" "$FX/PluginData/"
    done
    # (the game here has no use for the other systems' libraries; they are for the copy that is given out)
    case "$(uname -s)" in Darwin) rm -f "$FX/PluginData/vfxgrid.dll" "$FX/PluginData/vfxgrid.so" ;; Linux) rm -f "$FX/PluginData/vfxgrid.dll" "$FX/PluginData/vfxgrid.dylib" ;; esac
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
