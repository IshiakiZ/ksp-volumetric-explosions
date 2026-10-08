#!/usr/bin/env python3
"""Write the Unity (ShaderLab) version of the smoke shader from tools/shaderpack/volume.glsl.

    python3 tools/unityshaders/port.py

The walk along the ray is the long part of the shader and is the same on every graphics system, so it is
kept in one place (the GLSL, which is what the Mac and Linux versions of the game run) and turned into HLSL
here by rule; the short set-up at the top differs between the two and is written out in full below.
"""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
HEAD = '''// The smoke of Volumetric Explosions as a volume: the same shader as tools/shaderpack/volume.glsl, for the
// Unity editor, so that it can be built for Direct3D (Windows), which needs compiled bytecode that cannot
// be made by hand. The walk along the ray is generated from the GLSL by tools/unityshaders/port.py: edit
// the GLSL and run that again rather than editing here.
//
// NOT YET BUILT OR RUN: it was written without a Unity editor to hand.
Shader "VolumetricExplosions/Volume"
{
    Properties
    {
        _Volume ("Volume", 3D) = "" {}
        _Amount ("Amount", 3D) = "" {}
        _Detail ("Detail", 3D) = "" {}
        _Around ("Around", 3D) = "" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Front

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            float4 _VolParams, _VolCamera, _VolStep, _VolGrid, _VolSize, _VolOffset, _VolDetail, _VolPeak, _VolSun, _VolSunDir, _VolSunLocal, _VolAmb, _VolGlow;
            float4 _VolLampA, _VolLampB, _VolLampC, _VolLamps, _VolTint, _VolHot1, _VolHot2, _VolHot3, _VolHot4;
            sampler3D _Volume, _Amount, _Around, _Detail;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(vertex.xyz, 1.0));
                o.pos = mul(UNITY_MATRIX_VP, world);
                // The far faces of the box are what is drawn, and they must not be cut off by the far end of the camera's range.
                #if defined(UNITY_REVERSED_Z)
                o.pos.z = max(o.pos.z, o.pos.w * 0.00001);
                #else
                o.pos.z = min(o.pos.z, o.pos.w * 0.99999);
                #endif
                o.world = world.xyz;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag(v2f i, UNITY_VPOS_TYPE pixel : VPOS) : SV_Target
            {
                float3 eye = _WorldSpaceCameraPos;
                float3 rd = normalize(i.world - eye);
                // The same ray in the box's own coordinates (-0.5 to 0.5 each way). The distance along it stays in metres.
                float3 ro = mul(unity_WorldToObject, float4(eye, 1.0)).xyz;
                float3 rdo = mul((float3x3)unity_WorldToObject, rd);
                float3 size = max(abs(rdo), float3(1e-6, 1e-6, 1e-6));
                float3 inv = float3(rdo.x < 0.0 ? -1.0 : 1.0, rdo.y < 0.0 ? -1.0 : 1.0, rdo.z < 0.0 ? -1.0 : 1.0) / size;
                float3 ta = (float3(-0.5, -0.5, -0.5) - ro) * inv, tb = (float3(0.5, 0.5, 0.5) - ro) * inv;
                float3 lo = min(ta, tb), hi = max(ta, tb);
                float t0 = max(max(lo.x, lo.y), lo.z), t1 = min(min(hi.x, hi.y), hi.z);
                // Only the stretch of the ray that this camera draws.
                float forward = max(-mul((float3x3)UNITY_MATRIX_V, rd).z, 1e-4);
                t0 = max(t0, _ProjectionParams.y / forward);
                t1 = min(t1, _ProjectionParams.z / forward);
                if (_VolCamera.x > 0.5)
                {
                    float depth = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen));
                    t1 = min(t1, LinearEyeDepth(depth) / forward);
                }
                clip(t1 - t0);

'''
TAIL = '''
            }
            ENDCG
        }
    }
}
'''


def lookups(line):
    """textureLod(name, place, level) -> tex3Dlod(name, float4(place, level)), minding the brackets inside."""
    while "textureLod(" in line:
        start = line.index("textureLod(")
        depth, end = 0, None
        for n in range(start + len("textureLod("), len(line)):
            if line[n] == "(": depth += 1
            elif line[n] == ")":
                if depth == 0: end = n; break
                depth -= 1
        inner = line[start + len("textureLod("):end]
        depth, commas = 0, []
        for n, c in enumerate(inner):
            if c == "(": depth += 1
            elif c == ")": depth -= 1
            elif c == "," and depth == 0: commas.append(n)
        name, place, level = inner[:commas[0]], inner[commas[0] + 1:commas[-1]].strip(), inner[commas[-1] + 1:].strip()
        line = line[:start] + "tex3Dlod(%s, float4(%s, %s))" % (name, place, level) + line[end + 1:]
    return line


def main():
    glsl = open(os.path.join(HERE, "..", "shaderpack", "volume.glsl")).read()
    fragment = glsl.split("#ifdef FRAGMENT")[1]
    walk = fragment[fragment.index("    // Steps are counted in fine steps from the camera."):fragment.rindex("}")]
    walk = "\n".join(lookups(line) for line in walk.rstrip().split("\n"))
    walk = walk.replace("gl_FragCoord.xy", "pixel.xy").replace("SV_Target0 = vec4(colour, 1.0 - through);", "return float4(colour, 1.0 - through);")
    walk = re.sub(r"\bvec([234])\b", r"float\1", walk)
    for was, now in (("mix(", "lerp("), ("fract(", "frac("), ("mod(", "fmod("), ("inversesqrt(", "rsqrt(")):
        walk = re.sub(r"\b" + re.escape(was), now, walk)
    walk = re.sub(r"float3\((-?[0-9.]+)\)", r"float3(\1, \1, \1)", walk)
    walk = walk.replace("for (int i = 0; i < 288; i++)", "[loop] for (int i = 0; i < 288; i++)")
    assert "textureLod" not in walk and "vec3" not in walk and "gl_" not in walk
    walk = "\n".join(("            " + line if line.strip() else line) for line in walk.split("\n"))
    out = os.path.join(HERE, "Assets", "VolumetricExplosions", "Volume.shader")
    open(out, "w").write(HEAD + walk + TAIL)
    print("wrote", os.path.relpath(out))


if __name__ == "__main__":
    main()
