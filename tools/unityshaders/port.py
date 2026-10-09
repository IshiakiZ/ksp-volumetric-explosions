#!/usr/bin/env python3
"""Write the Unity (ShaderLab) versions of this project's shaders from their GLSL.

    python3 tools/unityshaders/port.py

The seven shaders (the explosions' smoke, the enlarging of it, the burn mark and the shock front, Keystone's
lens, and Natural Light's shafts of sunlight and its blacking out of pixels that are not numbers) are kept
in one place: tools/shaderpack/*.glsl, which is what the Mac and Linux versions of
the game run, packed into bundles by hand. Direct3D (Windows) wants compiled bytecode that only the Unity
editor can make, from HLSL. So the GLSL is turned into HLSL here by rule, and written out as .shader files
for the Unity project in this folder to build (see README.md). Edit the GLSL and run this again; do not
edit the .shader files.

The rules are those the seven need and no more: this is not a general translator. Where Direct3D differs in
a way no rule can see (which way up a picture lies), the lines concerned are replaced one by one, below,
and each such replacement must find its line exactly once, so that a changed GLSL stops this instead of
quietly leaving something out.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PACK = os.path.join(HERE, "..", "shaderpack")
sys.path.insert(0, PACK)
import make_bundle  # noqa: E402  (for each shader's name, textures and how it is blended)

# Where each goes in the Unity project.
FOLDER = {"volume": "VolumetricExplosions", "enlarge": "VolumetricExplosions", "mark": "VolumetricExplosions", "shock": "VolumetricExplosions", "lens": "Keystone",
          "shafts": "NaturalLight", "clean": "NaturalLight"}

# What Unity's own headers already declare.
GIVEN = {"_WorldSpaceCameraPos", "_ProjectionParams", "_ZBufferParams", "_ScreenParams", "_Time"}

# Names that are something else in HLSL: a variable of ours that has one of them gets an underscore.
RESERVED = {"all", "any", "clip", "lit", "sample", "point", "line", "triangle", "linear", "half", "fixed", "string", "matrix", "vector", "pass", "technique",
            "texture", "sampler", "dst", "mad", "rcp", "noise", "frac", "lerp", "mul", "saturate", "fmod", "ddx", "ddy", "register", "shared", "volatile",
            "static", "export", "inline", "interface", "class", "namespace", "precise", "double", "uint", "dword", "auto", "case", "default", "switch",
            "typedef", "enum", "long", "short", "signed", "unsigned", "new", "delete", "this", "template", "typename", "union", "using", "virtual", "operator",
            "private", "protected", "public", "try", "catch", "throw", "goto", "char", "friend", "explicit", "mutable", "sizeof", "abort", "trunc_", "IN", "OUT", "result"}

# The camera's clip space ends at a different place on each system, and Direct3D counts depth backwards in this game.
HELD = """#if defined(UNITY_REVERSED_Z)
    clip.z = max(clip.z, clip.w * 0.00001);
#else
    clip.z = min(clip.z, clip.w * 0.99999);
#endif"""

# Lines that are replaced outright (in the GLSL, before anything else is done to it): (which shaders, which stage, the line, what it becomes).
# What they are about: on Direct3D a picture's first row is its top one, and the game draws into its pictures upside down to make up for
# it (_ProjectionParams.x is -1 while it does). So a place on the screen worked out from where a corner was put has to be turned over to
# match, and which way depends on what is being drawn into.
SPECIAL = [
    (("volume", "enlarge", "mark"), "vertex", "    clip.z = min(clip.z, clip.w * 0.99999);", HELD),
    (("volume", "mark"), "fragment", "vec2 uv = vs_TEXCOORD1.xy / vs_TEXCOORD1.w * 0.5 + 0.5;",
     "vec2 uv = vec2(vs_TEXCOORD1.x / vs_TEXCOORD1.w, vs_TEXCOORD1.y / vs_TEXCOORD1.w * _ProjectionParams.x) * 0.5 + 0.5;"),
    (("shock",), "vertex", "    vs_TEXCOORD0 = in_POSITION0.xy;", "    vs_TEXCOORD0 = vec2(in_POSITION0.x, in_POSITION0.y * _ProjectionParams.x);"),
    (("lens",), "vertex", "    vs_TEXCOORD0 = in_POSITION0.xy * 0.5 + 0.5;",
     """    // (the first of the two goes is drawn into a picture of the lens's own, which lies as every picture does on this system;
    // the second into whatever the camera is drawing into, which may lie either way: see SPECIAL in tools/unityshaders/port.py)
    float way = 1.0;
#if UNITY_UV_STARTS_AT_TOP
    way = _LensStage.x < 1.5 ? -1.0 : _ProjectionParams.x;
#endif
    vs_TEXCOORD0 = vec2(in_POSITION0.x * 0.5 + 0.5, in_POSITION0.y * way * 0.5 + 0.5);"""),
    (("lens",), "fragment", "        turn = further * turn;", "        turn = mul(further, turn);"),
    (("shafts",), "vertex", "    vs_TEXCOORD0 = in_POSITION0.xy * 0.5 + 0.5;",
     """    // (the first two goes are drawn into pictures of the shafts' own, which lie as every picture does on this system; the
    // third into whatever the camera is drawing into, which may lie either way: as for the lens, above)
    float way = 1.0;
#if UNITY_UV_STARTS_AT_TOP
    way = _ShaftStage.x < 2.5 ? -1.0 : _ProjectionParams.x;
#endif
    vs_TEXCOORD0 = vec2(in_POSITION0.x * 0.5 + 0.5, in_POSITION0.y * way * 0.5 + 0.5);"""),
    (("enlarge",), "fragment", "    if (s00 == vec4(0.0) && s10 == vec4(0.0) && s01 == vec4(0.0) && s11 == vec4(0.0)) discard;",
     "    if (AllZero(s00) && AllZero(s10) && AllZero(s01) && AllZero(s11)) discard;"),
]

COMMON = """            #include "UnityCG.cginc"

            // (the GLSL has each of the game's matrices as its four columns; HLSL has them as rows)
            #define hlslcc_mtx4x4unity_ObjectToWorld transpose(unity_ObjectToWorld)
            #define hlslcc_mtx4x4unity_WorldToObject transpose(unity_WorldToObject)
            #define hlslcc_mtx4x4unity_MatrixVP transpose(UNITY_MATRIX_VP)
            #define hlslcc_mtx4x4unity_MatrixV transpose(UNITY_MATRIX_V)
            #define SampleLod(S, uv, lod) S.SampleLevel(sampler##S, uv, lod)
            #define SampleFlat(S, uv) S.SampleLevel(sampler##S, uv, 0.0)
            #define Mod(x, y) ((x) - (y) * floor((x) / (y)))
            #define GreaterThan(a, b) ((a) > (b))
            float4 TexelFetch(Texture2D t, int2 p, int lod) { return t.Load(int3(p, lod)); }
            float4 TexelFetch(Texture3D t, int3 p, int lod) { return t.Load(int4(p, lod)); }
            int2 TextureSize(Texture2D t, int lod) { uint w, h; t.GetDimensions(w, h); return int2(w, h); }
            bool AllZero(float4 v) { return !any(v); }
"""

# One pixel of the camera's depth picture, by its row and column. Where this is being drawn straight to the screen on Direct3D, the
# screen's rows run the other way from the depth picture's.
DEPTH = """            float4 DepthFetch(int2 p, int lod)
            {
            #if UNITY_UV_STARTS_AT_TOP
                if (_ProjectionParams.x > 0.0) { uint w, h; _CameraDepthTexture.GetDimensions(w, h); p.y = (int)h - 1 - p.y; }
            #endif
                return _CameraDepthTexture.Load(int3(p, lod));
            }
"""

BLEND = {0.0: "Zero", 1.0: "One", 5.0: "SrcAlpha", 10.0: "OneMinusSrcAlpha"}
CULL = {0.0: "Off", 1.0: "Front", 2.0: "Back"}
ZTEST = {8.0: "Always", 4.0: "LEqual"}


def bracket(text, start):
    """Where the bracket opened at text[start] is closed."""
    depth = 0
    for n in range(start, len(text)):
        if text[n] == "(": depth += 1
        elif text[n] == ")":
            depth -= 1
            if depth == 0: return n
    raise SystemExit("an unclosed bracket in: " + text[start:start + 60])


def arguments(inner):
    """The arguments between a pair of brackets, split at the commas that are not inside others."""
    out, depth, last = [], 0, 0
    for n, c in enumerate(inner):
        if c in "([": depth += 1
        elif c in ")]": depth -= 1
        elif c == "," and depth == 0:
            out.append(inner[last:n]); last = n + 1
    out.append(inner[last:])
    return [a.strip() for a in out]


def makers(text):
    """float3(x) with one thing in it is not allowed in HLSL: it becomes a cast. And a matrix made from numbers is made row by row there, column by column in GLSL."""
    pattern = re.compile(r"(?<![\w.@])((?:float|int|bool)[234](?:x[234])?)\(")
    while True:
        found = pattern.search(text)
        if not found: break
        kind, open_at = found.group(1), found.end() - 1
        close = bracket(text, open_at)
        inside = arguments(text[open_at + 1:close])
        if len(inside) == 1: new = "((%s)(%s))" % (kind, inside[0])
        elif "x" in kind: new = "transpose(%s@(%s))" % (kind, ", ".join(inside))
        else: new = "%s@(%s)" % (kind, ", ".join(inside))
        text = text[:found.start(1)] + new + text[close + 1:]
    return text.replace("@(", "(")


def uncommented(text):
    return re.sub(r"//[^\n]*", "", text)


def convert(text, stage):
    """A stretch of GLSL (functions, or the inside of main) as HLSL."""
    text = re.sub(r"\bivec([234])\b", r"int\1", text)
    text = re.sub(r"\bbvec([234])\b", r"bool\1", text)
    text = re.sub(r"\bvec([234])\b", r"float\1", text)
    text = re.sub(r"\bmat([234])\b", r"float\1x\1", text)
    text = text.replace("texelFetch(_CameraDepthTexture, ", "DepthFetch(")
    for was, now in (("textureLod(", "SampleLod("), ("texture(", "SampleFlat("), ("texelFetch(", "TexelFetch("), ("textureSize(", "TextureSize("), ("mix(", "lerp("), ("fract(", "frac("),
                     ("inversesqrt(", "rsqrt("), ("mod(", "Mod("), ("greaterThan(", "GreaterThan("), ("dFdx(", "ddx("), ("dFdy(", "ddy("), ("floatBitsToInt(", "asint(")):
        text = re.sub(r"(?<![\w.])" + re.escape(was), now, text)
    text = makers(text)
    text = re.sub(r"\bgl_FragCoord\b", "IN.pos", text)
    text = re.sub(r"\bgl_Position\b", "OUT.pos", text)
    text = re.sub(r"\bSV_Target0\b", "result", text)
    text = re.sub(r"\bvs_TEXCOORD(\d)\b", ("OUT" if stage == "vertex" else "IN") + r".vs_TEXCOORD\1", text)
    text = re.sub(r"(\n\s*)for \(", r"\1[loop] for (", text)
    for left in ("textureLod", "texelFetch", "textureSize", "gl_", "vec2", "vec3", "vec4", "mat2", "mat3", "mat4"):
        if re.search(r"(?<![\w.])" + re.escape(left) + r"\b", uncommented(text)): raise SystemExit("still GLSL after conversion: " + left)
    return text


def section(glsl, which):
    """The text of one stage of a GLSL file."""
    text = glsl.split("#ifdef " + which.upper())[1]
    return text[:text.index("\n#endif")]


def stage_of(text):
    """One stage taken apart: what it reads, what it is handed and hands on, and its functions."""
    uniforms, textures, matrices, ins, outs, statics, body = [], [], [], [], [], [], []
    depth = 0
    for line in text.split("\n"):
        bare = line.split("//")[0].strip()
        if depth == 0:
            if bare.startswith("#version") or bare.startswith("#extension"): continue
            found = re.match(r"uniform vec4 hlslcc_mtx4x4(\w+)\[4\];", bare)
            if found: matrices.append(found.group(1)); continue
            found = re.match(r"uniform sampler([23])D (\w+);", bare)
            if found: textures.append((found.group(2), int(found.group(1)), line.split("//", 1)[1].strip() if "//" in line else "")); continue
            found = re.match(r"uniform (\w+) (\w+);", bare)
            if found: uniforms.append((found.group(2), found.group(1), line.split("//", 1)[1].strip() if "//" in line else "")); continue
            found = re.match(r"(in|out) (\w+) (\w+);", bare)
            if found: (ins if found.group(1) == "in" else outs).append((found.group(3), found.group(2))); continue
            found = re.match(r"(float|vec[234]|int|bool) (\w+);", bare)
            if found: statics.append((found.group(2), found.group(1))); continue
        depth += bare.count("{") - bare.count("}")
        body.append(line)
    body = "\n".join(body)
    start = body.index("void main()")
    opened = body.index("{", start)
    level, closed = 0, None
    for n in range(opened, len(body)):
        if body[n] == "{": level += 1
        elif body[n] == "}":
            level -= 1
            if level == 0: closed = n; break
    return {"uniforms": uniforms, "textures": textures, "matrices": matrices, "ins": ins, "outs": outs, "statics": statics,
            "helpers": body[:start].strip("\n"), "main": body[opened + 1:closed].strip("\n")}


def kind(glsl_type):
    return {"vec2": "float2", "vec3": "float3", "vec4": "float4", "float": "float", "int": "int", "bool": "bool", "mat4": "float4x4"}[glsl_type]


def renamed(text, names):
    """Our own names that clash, given an underscore: in the code, not in what is said about it."""
    out = []
    for line in text.split("\n"):
        code, mark, note = line.partition("//")
        for name in names: code = re.sub(r"(?<![\w.])" + name + r"\b(?!\s*\()", name + "_", code)
        out.append(code + mark + note)
    return "\n".join(out)


def indented(text, by):
    return "\n".join((by + line if line.strip() and not line.lstrip().startswith("#") else line.lstrip() if line.lstrip().startswith("#") else line) for line in text.split("\n"))


def port(which):
    spec = make_bundle.SHADERS[which]
    glsl = open(os.path.join(PACK, spec["code"])).read()
    texts = {"vertex": section(glsl, "vertex"), "fragment": section(glsl, "fragment")}
    for shaders, stage, was, now in SPECIAL:
        if which not in shaders: continue
        if texts[stage].count(was) != 1: raise SystemExit("%s.glsl (%s): expected exactly one line reading: %s" % (which, stage, was.strip()))
        texts[stage] = texts[stage].replace(was, now)
    vertex, fragment = stage_of(texts["vertex"]), stage_of(texts["fragment"])

    # Our own names that mean something else in HLSL.
    code = uncommented(vertex["helpers"] + vertex["main"] + fragment["helpers"] + fragment["main"])
    used = set(re.findall(r"(?<![\w.])([A-Za-z_]\w*)\b(?!\s*\()", code))
    clash = sorted(n for n in used if n in RESERVED and n not in ("static",))
    # (those the replacements above put in on purpose are HLSL already)
    clash = [n for n in clash if n not in ("mul",)]
    for part in (vertex, fragment):
        for key in ("helpers", "main"): part[key] = renamed(part[key], clash)

    seen, declared = set(), []
    for stage in (vertex, fragment):
        for name, glsl_type, note in stage["uniforms"]:
            if name in GIVEN or name in seen: continue
            seen.add(name); declared.append("            %s %s;%s" % (kind(glsl_type), name, "     // " + note if note else ""))
    textures = []
    for stage in (vertex, fragment):
        for name, dims, note in stage["textures"]:
            if name in seen: continue
            seen.add(name); textures.append("            Texture%dD %s; SamplerState sampler%s;%s" % (dims, name, name, "     // " + note if note else ""))
    statics = ["            static %s %s;" % (kind(t), n) for n, t in fragment["statics"] + vertex["statics"]]
    passed = ["                float4 pos : SV_POSITION;"] + ["                %s %s : TEXCOORD%s;" % (kind(t), n, n[-1]) for n, t in vertex["outs"]]
    properties = ["        %s (\"%s\", %s) = \"\" {}" % (name, name[1:], "3D" if dims == 3 else "2D") for name, dims in spec["textures"][:-spec.get("given", 1)]]
    source, dest = spec.get("blend", (1.0, 10.0))
    depth_read = any(name == "_CameraDepthTexture" for name, _, _ in fragment["textures"])

    out = []
    out.append("// %s: made from tools/shaderpack/%s by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again." % (spec["name"], spec["code"]))
    out.append("Shader \"%s\"\n{\n    Properties\n    {\n%s\n    }\n    SubShader\n    {" % (spec["name"], "\n".join(properties)))
    out.append("        Tags { \"Queue\" = \"Transparent\" \"RenderType\" = \"Transparent\" \"IgnoreProjector\" = \"True\" }\n        Pass\n        {")
    out.append("            Blend %s %s\n            ZWrite Off\n            ZTest %s\n            Cull %s\n" % (BLEND[source], BLEND[dest], ZTEST[spec.get("ztest", 8.0)], CULL[spec.get("cull", 1.0)]))
    out.append("            CGPROGRAM\n            #pragma vertex vert\n            #pragma fragment frag\n            #pragma target 4.5\n            #pragma only_renderers d3d11 glcore metal vulkan")
    out.append(COMMON)
    out.append("\n".join(declared))
    out.append("\n".join(textures))
    if statics: out.append("\n".join(statics))
    if depth_read: out.append("\n" + DEPTH)
    out.append("\n            struct v2f\n            {\n%s\n            };\n" % "\n".join(passed))
    if vertex["helpers"].strip(): out.append(indented(convert(vertex["helpers"], "vertex"), "            ") + "\n")
    out.append("            v2f vert(float3 in_POSITION0 : POSITION)\n            {\n                v2f OUT = (v2f)0;")
    out.append(indented(re.sub(r"\breturn;", "return OUT;", convert(vertex["main"], "vertex")), "            "))
    out.append("                return OUT;\n            }\n")
    if fragment["helpers"].strip(): out.append(indented(convert(fragment["helpers"], "fragment"), "            ") + "\n")
    out.append("            float4 frag(v2f IN) : SV_Target\n            {\n                float4 result = float4(0.0, 0.0, 0.0, 0.0);")
    out.append(indented(re.sub(r"\breturn;", "return result;", convert(fragment["main"], "fragment")), "            "))
    out.append("                return result;\n            }\n            ENDCG\n        }\n    }\n}")
    folder = os.path.join(HERE, "Assets", FOLDER[which])
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, spec["name"].split("/")[1] + ".shader")
    open(path, "w").write("\n".join(out) + "\n")
    print("wrote %s (%d lines)%s" % (os.path.relpath(path, os.path.join(HERE, "..", "..")), "\n".join(out).count("\n") + 1, "; renamed: " + ", ".join(clash) if clash else ""))


if __name__ == "__main__":
    asked = sys.argv[1:]
    for name in asked or ["volume", "enlarge", "mark", "shock", "lens", "shafts", "clean"]:
        # (a repository of one mod has that mod's shaders and not the others': those are passed over)
        if not asked and not os.path.exists(os.path.join(PACK, make_bundle.SHADERS[name]["code"])):
            print("left out: %s (tools/shaderpack/%s is not here)" % (name, make_bundle.SHADERS[name]["code"]))
            continue
        port(name)
