# Shared by the build scripts that build Volumetric Explosions (sourced, not run; after common.sh).
# The explosions mod makes its grids in C where it can (see src/VolumetricExplosions/Native.cs): a small library,
# built here for the kind of processor the game itself is built for. Without a C compiler the mod is built
# without it, and makes its grids in C# as it always could.
native() {  # native <folder to put it in> [file name without ending]
  local out="$1" name="${2:-vfxgrid}"
  command -v cc >/dev/null 2>&1 || { echo "note: no C compiler found (on a Mac: xcode-select --install); Volumetric Explosions is built without its library"; return 0; }
  python3 tools/native/genp.py src/VolumetricExplosions/Site.cs "$TMP/p.h"
  case "$(uname -s)" in
    Darwin) cc -O3 -arch x86_64 -mmacosx-version-min=10.13 -dynamiclib -fvisibility=hidden -fno-math-errno -I "$TMP" -o "$out/$name.dylib" src/VolumetricExplosions/Native/vfxgrid.c ;;
    Linux)  cc -O3 -shared -fPIC -fvisibility=hidden -fno-math-errno -I "$TMP" -o "$out/$name.so" src/VolumetricExplosions/Native/vfxgrid.c -lm ;;
    *)      echo "note: Volumetric Explosions' library is not built on this kind of system" ;;
  esac
}

# The same library for the other systems the game runs on, where there is a compiler here that can make them:
# Zig (which carries everything it needs for all three; looked for on the path and in ~/.ksp-ai-bridge/zig), or
# MinGW for Windows alone. Neither is needed for anything else, and a copy of the mod without these files
# works the same there, on its own code.
# NOTE: what this makes has not been run on Windows or Linux, so it is made only when asked for (OTHERS=1) and
# is not in the download: a library that goes wrong takes the whole game down with it, which the mod's own
# code cannot. It goes in once someone has run it there.
others() {  # others <folder to put them in>
  local out="$1" zig="" source=src/VolumetricExplosions/Native/vfxgrid.c
  [ "${OTHERS:-0}" = 1 ] || return 0
  [ -f "$TMP/p.h" ] || return 0
  if command -v zig >/dev/null 2>&1; then zig="zig"; elif [ -x "$HOME/.ksp-ai-bridge/zig/zig" ]; then zig="$HOME/.ksp-ai-bridge/zig/zig"; fi
  if [ -n "$zig" ]; then
    "$zig" cc -target x86_64-windows-gnu -O3 -shared -fno-math-errno -s -I "$TMP" -o "$out/vfxgrid.dll" "$source" && rm -f "$out/vfxgrid.lib" "$out/vfxgrid.pdb"
    [ "$(uname -s)" = Linux ] || "$zig" cc -target x86_64-linux-gnu.2.17 -O3 -shared -fPIC -fvisibility=hidden -fno-math-errno -s -I "$TMP" -o "$out/vfxgrid.so" "$source" -lm
  elif command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
    x86_64-w64-mingw32-gcc -O3 -shared -fno-math-errno -s -static-libgcc -I "$TMP" -o "$out/vfxgrid.dll" "$source"
  else
    echo "note: no compiler here that builds for Windows (zig or x86_64-w64-mingw32-gcc): Volumetric Explosions is built without its library for Windows, and makes its grids in C# there"
  fi
}
