# Shared by the build scripts that build Volumetric Explosions (sourced, not run; after common.sh).
# The explosions mod makes its grids in C where it can (see src/VolumetricExplosions/Native.cs): a small library,
# built here for the kind of processor the game itself is built for. Without a C compiler the mod is built
# without it, and makes its grids in C# as it always could.
native() {  # native <folder to put it in> [file name without ending]
  local out="$1" name="${2:-vfxgrid}" made
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
      # Windows (Git Bash): Microsoft's own C compiler (see msvc, below), where Visual Studio or its Build Tools have it
      python3 tools/native/genp.py src/VolumetricExplosions/Site.cs "$TMP/p.h"
      msvc src/VolumetricExplosions/Native/vfxgrid.c "$out/$name.dll" -I"$(cygpath -m "$TMP")" && return 0 || made=$?
      [ "$made" = 2 ] || return 1
      echo "note: no C compiler found (Visual Studio's or its Build Tools' \"Desktop development with C++\"); Volumetric Explosions is built without its library"
      return 0 ;;
  esac
  command -v cc >/dev/null 2>&1 || { echo "note: no C compiler found (on a Mac: xcode-select --install); Volumetric Explosions is built without its library"; return 0; }
  python3 tools/native/genp.py src/VolumetricExplosions/Site.cs "$TMP/p.h"
  case "$(uname -s)" in
    Darwin) cc -O3 -arch x86_64 -mmacosx-version-min=10.13 -dynamiclib -fvisibility=hidden -fno-math-errno -I "$TMP" -o "$out/$name.dylib" src/VolumetricExplosions/Native/vfxgrid.c ;;
    Linux)  cc -O3 -shared -fPIC -fvisibility=hidden -fno-math-errno -I "$TMP" -o "$out/$name.so" src/VolumetricExplosions/Native/vfxgrid.c -lm ;;
    *)      echo "note: Volumetric Explosions' library is not built on this kind of system" ;;
  esac
}

# A library for Windows made on Windows by Microsoft's C compiler, from Visual Studio or its Build Tools: found with Visual Studio's
# own locator and given the paths of the Windows SDK here, so that nothing has to be set up first (no Developer Command Prompt).
# The C runtime goes into the library itself (-MT), so that it needs nothing installed beside the game. Fails with 2 where there
# is no such compiler, with 1 where the source does not compile.
msvc() {  # msvc <C source> <library to make> [more of cl's options]
  local source="$1" dll="$2" where="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe" vs tools kits sdk work w
  shift 2
  [ -x "$where" ] || return 2
  vs="$("$where" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
  [ -n "$vs" ] || return 2
  vs="$(cygpath -u "$vs")"
  tools="$vs/VC/Tools/MSVC/$(tr -d '\r\n ' < "$vs/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt" 2>/dev/null)"
  [ -x "$tools/bin/Hostx64/x64/cl.exe" ] || return 2
  kits="/c/Program Files (x86)/Windows Kits/10"
  sdk="$(ls -1 "$kits/Include" 2>/dev/null | sort -V | while IFS= read -r v; do [ -f "$kits/Include/$v/ucrt/stdio.h" ] && [ -f "$kits/Lib/$v/um/x64/kernel32.lib" ] && echo "$v"; done | tail -1)"
  [ -n "$sdk" ] || return 2
  work="$(mktemp -d)"; w="$(cygpath -m "$work")"
  # (cl's options begin with a slash as easily as with a dash, and Git Bash would take "/O2" for a path: so dashes, and its paths as Windows' own)
  if ! PATH="$tools/bin/Hostx64/x64:$PATH" MSYS2_ARG_CONV_EXCL='*' cl.exe -nologo -O2 -std:c17 -MT -LD "$@" \
         -I"$(cygpath -m "$tools/include")" -I"$(cygpath -m "$kits/Include/$sdk/ucrt")" -Fo"$w/" -Fe"$(cygpath -m "$dll")" "$(cygpath -m "$source")" \
         -link -nologo -LIBPATH:"$(cygpath -m "$tools/lib/x64")" -LIBPATH:"$(cygpath -m "$kits/Lib/$sdk/ucrt/x64")" -LIBPATH:"$(cygpath -m "$kits/Lib/$sdk/um/x64")" \
         -IMPLIB:"$w/made.lib" kernel32.lib > "$work/log"; then
    tr -d '\r' < "$work/log" >&2; rm -rf "$work"; return 1
  fi
  tr -d '\r' < "$work/log" | grep -i 'warning' || true
  rm -rf "$work"
}

# The same library for the other systems the game runs on, where there is a compiler here that can make them:
# Zig (which carries everything it needs for all three; looked for on the path and in ~/.ksp-ai-bridge/zig), or
# MinGW for Windows alone. Neither is needed for anything else, and a copy of the mod without these files
# works the same there, on its own code.
# NOTE: the Windows library made so (Zig 0.17.0) was run in the game on Windows on 2026-10-10 and made the same grids
# as the C# (NativeCheck); the Linux one has not been run. So it is made only when asked for (OTHERS=1) and is
# not in the download: a library that goes wrong takes the whole game down with it, which the mod's own code
# cannot. Whether the Windows one goes in now is for the Mac to say.
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
