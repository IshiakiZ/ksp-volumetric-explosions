#!/usr/bin/env python3
"""Write the C description of the explosions mod's particle from its C# one.

    python3 tools/native/genp.py src/VolumetricExplosions/Site.cs out/p.h

The grid maker in C (src/VolumetricExplosions/Native/vfxgrid.c) reads the particles straight out of the
game's memory, so the two descriptions must agree field for field. This takes the C one from the C# one
every time the mod is built, and gives it a number made from the fields' kinds and names in order; the
mod works out the same number from its own particle when it starts (see Native.cs) and does not use a
library whose number differs (one left over from another build).
"""
import re
import sys

KINDS = {"float": "float", "byte": "uint8_t", "uint": "uint32_t", "int": "int32_t"}


def fields(source):
    body = re.search(r"public struct P\s*\{(.*?)\n        \}", source, re.S)
    if not body: raise SystemExit("no 'public struct P' found")
    out = []
    for line in body.group(1).split("\n"):
        line = line.split("//")[0].strip()
        if not line: continue
        m = re.match(r"public (\w+) ([\w\s,]+);$", line)
        if not m or m.group(1) not in KINDS: raise SystemExit("a line of struct P that this does not understand: " + line)
        for name in m.group(2).split(","): out.append((m.group(1), name.strip()))
    return out


def signature(found):
    """FNV-1a, 32 bits, over "kind name;" for each field in order (the same sum as Native.Signature)."""
    h = 0x811C9DC5
    for kind, name in found:
        for byte in (kind + " " + name + ";").encode("ascii"):
            h = ((h ^ byte) * 0x01000193) & 0xFFFFFFFF
    return h


if __name__ == "__main__":
    if len(sys.argv) != 3: raise SystemExit(__doc__)
    found = fields(open(sys.argv[1]).read())
    lines = ["// Made by tools/native/genp.py from Site.cs: do not edit.", "#pragma once", "#include <stdint.h>", "typedef struct P {"]
    lines += ["    %s %s;" % (KINDS[kind], name) for kind, name in found]
    lines += ["} P;", "#define P_SIGNATURE 0x%08Xu" % signature(found), ""]
    open(sys.argv[2], "w").write("\n".join(lines))
