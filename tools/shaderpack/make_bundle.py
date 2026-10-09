#!/usr/bin/env python3
"""Pack one of this project's shaders into a Unity asset bundle, without the Unity editor.

    python3 tools/shaderpack/make_bundle.py <KSP folder> <output file> [volume|enlarge|mark|shock|lens|shafts|clean]

Volumetric Explosions': "volume" (the default) is the smoke, "enlarge" what puts the smoke on the screen
when it has been drawn at half size, "mark" the burn mark thrown onto the ground, "shock" the shock front
that bends the picture behind it. Keystone's: "lens", what a lens, a shutter and a film do to a picture.
Natural Light's: "shafts", shafts of sunlight; "clean", which blacks out the pixels that are not numbers.

A KSP mod's own shader normally has to be compiled in the Unity editor. On OpenGL (the Mac and Linux
versions of the game) a compiled shader is only its GLSL text plus a list of what it reads, so this
takes one small shader out of a bundle that ships with the game, as a pattern, replaces the text and
the list with ours, and writes a new bundle. Direct3D (the Windows version) needs compiled bytecode,
which this cannot make: there the mod falls back to drawing its particles as soft sprites.
"""
import copy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import glprogram
import typetree
from unityfile import Asset, Bundle, Reader, Writer, lz4_store

OPENGL_CORE = 15

# What each shader's fragment stage reads: (name, columns, is a matrix); and its textures: (name, dimensions).
# The last texture of each is the game's own depth picture, which is not a property of the material
# ("given": how many of the last are the game's own, where it is more than that one).
VERTEX = [("unity_ObjectToWorld", 4, True), ("unity_MatrixVP", 4, True)]
SHADERS = {
    "volume": {
        "name": "VolumetricExplosions/Volume", "path": "assets/volumetricexplosions/volume.shader", "id": 7307199000000000001, "program": 1987650001,
        "bundle": "volumetricexplosions", "file": "CAB-766f6c756d65747269636578706c6f73", "code": "volume.glsl",
        "fragment": [("_WorldSpaceCameraPos", 3, False), ("_ProjectionParams", 4, False), ("_ZBufferParams", 4, False), ("_VolParams", 4, False), ("_VolStep", 4, False), ("_VolGrid", 4, False),
                     ("_VolSize", 4, False), ("_VolOffset", 4, False), ("_VolDetail", 4, False), ("_VolFlow", 4, False), ("_VolCamera", 4, False), ("_VolPeak", 4, False), ("_VolThin", 4, False), ("_VolSun", 4, False),
                     ("_VolSunDir", 4, False), ("_VolSunLocal", 4, False), ("_VolAmb", 4, False), ("_VolGlow", 4, False), ("_VolLampA", 4, False), ("_VolLampB", 4, False), ("_VolLampC", 4, False), ("_VolLamps", 4, False),
                     ("_VolTint", 4, False), ("_VolHot1", 4, False), ("_VolHot2", 4, False), ("_VolHot3", 4, False), ("_VolHot4", 4, False),
                     ("_VolCutA", 4, False), ("_VolCutB", 4, False), ("_VolCutC", 4, False), ("_VolCutD", 4, False), ("_VolCutE", 4, False), ("_VolCutF", 4, False), ("unity_WorldToObject", 4, True), ("unity_MatrixV", 4, True)],
        "textures": [("_Volume", 3), ("_Amount", 3), ("_Detail", 3), ("_Around", 3), ("_Flow", 3), ("_RestA", 3), ("_RestB", 3), ("_RestC", 3), ("_RestD", 3), ("_RestE", 3), ("_Turns", 3), ("_Clear", 3), ("_CameraDepthTexture", 2)],
    },
    "mark": {
        "name": "VolumetricExplosions/Mark", "path": "assets/volumetricexplosions/mark.shader", "id": 7307199000000000002, "program": 1987650002,
        "bundle": "volumetricexplosionsmark", "file": "CAB-766f6c756d65747269636d61726b0000", "code": "mark.glsl",
        "fragment": [("_WorldSpaceCameraPos", 3, False), ("_ProjectionParams", 4, False), ("_ZBufferParams", 4, False), ("_VolCamera", 4, False), ("_MarkTint", 4, False), ("_MarkGlow", 4, False),
                     ("_MarkSize", 4, False), ("unity_WorldToObject", 4, True), ("unity_MatrixV", 4, True)],
        "textures": [("_MarkTex", 2), ("_MarkEmbers", 2), ("_CameraDepthTexture", 2)],
    },
    "enlarge": {
        "name": "VolumetricExplosions/Enlarge", "path": "assets/volumetricexplosions/enlarge.shader", "id": 7307199000000000004, "program": 1987650004,
        "bundle": "volumetricexplosionsenlarge", "file": "CAB-766f6c756d6574726963656e6c617267", "code": "enlarge.glsl",
        "fragment": [("_ZBufferParams", 4, False), ("_VolCamera", 4, False)],
        "textures": [("_VolHalf", 2), ("_CameraDepthTexture", 2)],
        "vertex": VERTEX + [("_VolDrawn", 4, False)],
    },
    "shock": {
        "name": "VolumetricExplosions/Shock", "path": "assets/volumetricexplosions/shock.shader", "id": 7307199000000000003, "program": 1987650003,
        "bundle": "volumetricexplosionsshock", "file": "CAB-766f6c756d6574726963730068636b00", "code": "shock.glsl",
        "fragment": [("_ShockEye", 4, False), ("_ShockRight", 4, False), ("_ShockUp", 4, False), ("_ShockAhead", 4, False), ("_ShockDepth", 4, False),
                     ("_ShockC0", 4, False), ("_ShockC1", 4, False), ("_ShockC2", 4, False), ("_ShockC3", 4, False),
                     ("_ShockP0", 4, False), ("_ShockP1", 4, False), ("_ShockP2", 4, False), ("_ShockP3", 4, False),
                     ("_ShockG0", 4, False), ("_ShockG1", 4, False), ("_ShockG2", 4, False), ("_ShockG3", 4, False), ("_ShockPulse", 4, False)],
        "textures": [("_VolScene", 2), ("_CameraDepthTexture", 2)],
        "vertex": [("_ShockSheet", 4, False)],
        "cull": 0.0, "ztest": 8.0,       # a sheet over the whole screen; the shader works out for itself what stands in front of a front
    },
    "lens": {
        "name": "Keystone/Lens", "path": "assets/keystone/lens.shader", "id": 7307199000000000005, "program": 1987650005,
        "bundle": "keystonelens", "file": "CAB-6b657973746f6e656c656e7300000000", "code": "lens.glsl",
        "fragment": [("_ZBufferParams", 4, False), ("_LensStage", 4, False), ("_LensSize", 4, False), ("_LensFocus", 4, False), ("_LensMove", 4, False), ("_LensMarks", 4, False), ("_LensFrame", 4, False)],
        "textures": [("_LensScene", 2), ("_LensBlur", 2), ("_CameraMotionVectorsTexture", 2), ("_CameraDepthTexture", 2)],
        "vertex": [("_LensSheet", 4, False)],
        "cull": 0.0, "ztest": 8.0,
        "blend": (1.0, 0.0),             # what it draws takes the place of what was there
        "given": 2,                      # its last two textures are the game's own (how far each pixel has moved, and how far off it is)
    },
    "shafts": {
        "name": "NaturalLight/Shafts", "path": "assets/naturallight/shafts.shader", "id": 7307199000000000006, "program": 1987650006,
        "bundle": "nlshafts", "file": "CAB-6e6c7368616674730000000000000000", "code": "shafts.glsl",
        "fragment": [("_ZBufferParams", 4, False), ("_ShaftStage", 4, False), ("_ShaftSun", 4, False), ("_ShaftLook", 4, False), ("_ShaftTint", 4, False)],
        "textures": [("_ShaftRays", 2), ("_ShaftScene", 2), ("_CameraDepthTexture", 2)],
        "vertex": [("_ShaftSheet", 4, False)],
        "cull": 0.0, "ztest": 8.0,
        "blend": (1.0, 1.0),             # what it draws is added to what was there (its own pictures are wiped first)
        "given": 2,                      # (the picture so far and how far off each pixel is: both the game's own, handed over as they are)
    },
    "clean": {
        "name": "NaturalLight/Clean", "path": "assets/naturallight/clean.shader", "id": 7307199000000000007, "program": 1987650007,
        "bundle": "nlclean", "file": "CAB-6e6c636c65616e000000000000000000", "code": "clean.glsl",
        "fragment": [("_CleanMark", 4, False)],
        "textures": [("_CleanScene", 2)],
        "vertex": [("_CleanSheet", 4, False)],
        "cull": 0.0, "ztest": 8.0,
        "blend": (1.0, 0.0),             # what it draws takes the place of what was there (and it draws only where it has to)
        "given": 1,                      # (the half-size picture is handed to it as it is made, not kept by the material)
    },
}


def find(folder, name):
    for where, _, files in os.walk(folder):
        if name in files:
            return os.path.join(where, name)
    raise SystemExit("the game's %s was not found under %s" % (name, folder))


def pattern(ksp, bundle_name, class_id, pick):
    """An object of the given class from one of the game's own bundles, with the description of its layout."""
    bundle = Bundle(open(find(os.path.join(ksp, "GameData"), bundle_name), "rb").read())
    for name, data in bundle.files.items():
        if bundle.node_flags[name] != 4: continue
        asset = Asset(data)
        if asset.version != 21: continue
        for obj in asset.objects:
            if obj["class"] != class_id: continue
            kind = asset.types[obj["type"]]
            root = typetree.parse_tree(kind["tree"], kind["tree_counts"], asset.version)
            value = typetree.read(root, Reader(asset.bytes_of(obj)))
            if pick(value): return value, root, kind["raw"]
    raise SystemExit("no pattern of class %d in %s" % (class_id, bundle_name))


def layout(params):
    """Positions for a list of parameters, sixteen bytes to a vector and sixty-four to a matrix."""
    at, out = 0, []
    for name, cols, matrix in params:
        out.append((name, cols, matrix, at))
        at += 64 if matrix else 16
    return out, at


def build(ksp, out_path, which):
    spec = SHADERS[which]
    NAME, PATH, SHADER_ID, FRAGMENT, TEXTURES = spec["name"], spec["path"], spec["id"], spec["fragment"], spec["textures"]
    VERTEX = spec.get("vertex", globals()["VERTEX"])
    shader, shader_tree, shader_type = pattern(ksp, "makinghistory_scene", 48, lambda v: v["m_ParsedForm"]["m_Name"] == "UnlitAlpha")
    index, index_tree, index_type = pattern(ksp, "serenity.kspexpansion", 142, lambda v: True)
    code = open(os.path.join(HERE, spec["code"]), "rb").read()

    names = ["$Globals"] + [p[0] for p in FRAGMENT + VERTEX] + [t[0] for t in TEXTURES]
    number = {name: n for n, name in enumerate(names)}
    fragment, fragment_size = layout(FRAGMENT)
    vertex, vertex_size = layout(VERTEX)

    # ---- the readable description of the shader
    form = shader["m_ParsedForm"]
    form["m_Name"] = NAME
    texture_prop = copy.deepcopy(form["m_PropInfo"]["m_Props"][0])
    props = []
    for name, dims in TEXTURES[:-spec.get("given", 1)]:
        prop = copy.deepcopy(texture_prop)
        prop["m_Name"], prop["m_Description"] = name, name[1:]
        prop["m_DefTexture"] = {"m_DefaultName": "", "m_TexDim": dims}
        props.append(prop)
    form["m_PropInfo"]["m_Props"] = props
    one = form["m_SubShaders"][0]["m_Passes"][0]
    one["m_NameIndices"] = [{"first": name, "second": number[name]} for name in sorted(names)]
    state = one["m_State"]
    source, dest = spec.get("blend", (1.0, 10.0))                        # (as a rule: colour already multiplied by opacity)
    for key, value in (("srcBlend", source), ("destBlend", dest), ("srcBlendAlpha", source), ("destBlendAlpha", dest)):
        state["rtBlend0"][key]["val"] = value
    state["zWrite"]["val"] = 0.0
    state["zTest"]["val"] = spec.get("ztest", 8.0)       # 8, always: the shader itself stops at whatever is in front
    state["culling"]["val"] = spec.get("cull", 1.0)      # 1: draw the far faces of the box, so it still works with the camera inside
    state["gpuProgramID"] = spec["program"]

    def groups_for_form():
        out = []
        for params, size in ((fragment, fragment_size), (vertex, vertex_size)):
            out.append({"m_NameIndex": number["$Globals"],
                        "m_MatrixParams": [{"m_NameIndex": number[n], "m_Index": at, "m_ArraySize": 0, "m_Type": 0, "m_RowCount": 4} for n, c, m, at in params if m],
                        "m_VectorParams": [{"m_NameIndex": number[n], "m_Index": at, "m_ArraySize": 0, "m_Type": 0, "m_Dim": c} for n, c, m, at in params if not m],
                        "m_StructParams": [], "m_Size": size})
        return out

    gl_vertex = [s for s in one["progVertex"]["m_SubPrograms"] if s["m_GpuProgramType"] == glprogram.GLCORE][0]
    gl_fragment = [s for s in one["progFragment"]["m_SubPrograms"] if s["m_GpuProgramType"] == glprogram.GLCORE][0]
    gl_vertex["m_BlobIndex"] = 0
    gl_vertex["m_Channels"] = {"m_Channels": [], "m_SourceMap": 1}                 # positions only
    gl_vertex["m_TextureParams"] = [{"m_NameIndex": number[n], "m_Index": i, "m_SamplerIndex": i, "m_MultiSampled": False, "m_Dim": d} for i, (n, d) in enumerate(TEXTURES)]
    gl_vertex["m_ConstantBuffers"] = groups_for_form()
    gl_vertex["m_ConstantBufferBindings"] = []
    gl_fragment["m_BlobIndex"] = 1
    one["progVertex"]["m_SubPrograms"] = [gl_vertex]
    one["progFragment"]["m_SubPrograms"] = [gl_fragment]

    # ---- the program itself
    def group(params, size):
        return {"name": "$Globals", "size": size, "params": [{"name": n, "type": 0, "rows": 4 if m else 1, "cols": c, "matrix": 1 if m else 0, "array": 0, "index": at} for n, c, m, at in params]}

    first = glprogram.write_entry({"code": code, "source_map": 1, "groups": [{"name": "", "size": 0, "params": []}, group(fragment, fragment_size), group(vertex, vertex_size)],
                                   "bindings": [{"name": n, "type": 0, "index": i, "extra": i, "texture": d << 1} for i, (n, d) in enumerate(TEXTURES)]})
    second = glprogram.write_entry({})
    blob = glprogram.write_blob([first, second])
    packed = lz4_store(blob)
    shader["platforms"] = [OPENGL_CORE]
    shader["offsets"] = [[0]]
    shader["compressedLengths"] = [[len(packed)]]
    shader["decompressedLengths"] = [[len(blob)]]
    shader["compressedBlob"] = packed

    # ---- the bundle's own index
    index["m_Name"] = index["m_AssetBundleName"] = spec["bundle"]
    index["m_PreloadTable"] = [{"m_FileID": 0, "m_PathID": SHADER_ID}]
    index["m_Container"] = [{"first": PATH, "second": {"preloadIndex": 0, "preloadSize": 1, "asset": {"m_FileID": 0, "m_PathID": SHADER_ID}}}]

    def packed_object(root, value):
        w = Writer()
        typetree.write(root, value, w)
        return bytes(w.b)

    asset = write_asset([shader_type, index_type], [(SHADER_ID, 0, packed_object(shader_tree, shader)), (1, 1, packed_object(index_tree, index))])
    Asset(asset)                                   # reads back, or this stops here
    data = write_bundle(spec["file"], asset)
    check = Bundle(data)
    assert list(check.files.values())[0] == asset
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    open(out_path, "wb").write(data)
    print("wrote %s (%d bytes, %d of GLSL)" % (out_path, len(data), len(code)))


def write_asset(types, objects):
    meta = Writer()
    meta.cstr("2019.4.18f1"); meta.i32(19); meta.boolean(True)           # 19: the platform the game's own bundles name; with layouts included they load on any
    meta.i32(len(types))
    for raw in types: meta.raw(raw)
    meta.i32(len(objects))
    body = Writer()
    for path_id, kind, data in sorted(objects, key=lambda o: o[0]):      # the game looks objects up by number, in order
        body.align(8)
        meta.align()
        meta.i64(path_id); meta.u32(len(body.b)); meta.u32(len(data)); meta.i32(kind)
        body.raw(data)
    meta.i32(0); meta.i32(0); meta.i32(0)                                 # no scripts, no other files, no reference types
    meta.cstr("")
    start = (20 + len(meta.b) + 15) // 16 * 16
    out = Writer(big=True)
    out.u32(len(meta.b)); out.u32(start + len(body.b)); out.u32(21); out.u32(start)
    out.u8(0); out.raw(b"\0\0\0")
    out.raw(meta.b)
    while len(out.b) < start: out.b.append(0)
    out.raw(body.b)
    return bytes(out.b)


def write_bundle(name, asset):
    info = Writer(big=True)
    info.raw(b"\0" * 16)
    info.i32(1); info.u32(len(asset)); info.u32(len(asset)); info.u16(0x40)
    info.i32(1); info.i64(0); info.i64(len(asset)); info.u32(4); info.cstr(name)
    head = Writer(big=True)
    head.cstr("UnityFS"); head.u32(7); head.cstr("5.x.x"); head.cstr("2019.4.18f1")
    size_at = len(head.b)
    head.i64(0); head.u32(len(info.b)); head.u32(len(info.b)); head.u32(0x40)
    head.align(16)
    out = bytearray(head.b + info.b + asset)
    out[size_at:size_at + 8] = len(out).to_bytes(8, "big")
    return bytes(out)


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4) or (len(sys.argv) == 4 and sys.argv[3] not in SHADERS): raise SystemExit(__doc__)
    build(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) == 4 else "volume")
