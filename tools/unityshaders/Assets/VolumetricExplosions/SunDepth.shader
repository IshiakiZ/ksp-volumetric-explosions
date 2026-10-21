// VolumetricExplosions/SunDepth: made from tools/shaderpack/sundepth.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/SunDepth"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma only_renderers d3d11 glcore metal vulkan
            #include "UnityCG.cginc"

            // (the GLSL has each of the game's matrices as its four columns; HLSL has them as rows)
            #define hlslcc_mtx4x4unity_ObjectToWorld transpose(unity_ObjectToWorld)
            #define hlslcc_mtx4x4unity_WorldToObject transpose(unity_WorldToObject)
            #define hlslcc_mtx4x4unity_MatrixVP transpose(UNITY_MATRIX_VP)
            #define hlslcc_mtx4x4unity_MatrixV transpose(UNITY_MATRIX_V)
            #define SampleLod(S, uv, lod) S.SampleLevel(sampler##S, uv, lod)
            #define SampleFlat(S, uv) S.SampleLevel(sampler##S, uv, 0.0)
            #define SampleGradient(S, uv, dx, dy) S.SampleGrad(sampler##S, uv, dx, dy)
            #define Mod(x, y) ((x) - (y) * floor((x) / (y)))
            #define GreaterThan(a, b) ((a) > (b))
            #define GreaterThanEqual(a, b) ((a) >= (b))
            #define LessThan(a, b) ((a) < (b))
            #define LessThanEqual(a, b) ((a) <= (b))
            float4 TexelFetch(Texture2D t, int2 p, int lod) { return t.Load(int3(p, lod)); }
            float4 TexelFetch(Texture3D t, int3 p, int lod) { return t.Load(int4(p, lod)); }
            int2 TextureSize(Texture2D t, int lod) { uint w, h; t.GetDimensions(w, h); return int2(w, h); }
            bool AllZero(float4 v) { return !any(v); }
            // (one of a vector's numbers, chosen by a variable, read and given a new value: see chosen in tools/unityshaders/port.py)
            float Pick(float2 v, int n) { return n <= 0 ? v.x : v.y; }
            float Pick(float3 v, int n) { return n <= 0 ? v.x : n == 1 ? v.y : v.z; }
            float Pick(float4 v, int n) { return n <= 0 ? v.x : n == 1 ? v.y : n == 2 ? v.z : v.w; }
            float2 Put(float2 v, int n, float x) { return float2(n == 0 ? x : v.x, n == 1 ? x : v.y); }
            float3 Put(float3 v, int n, float x) { return float3(n == 0 ? x : v.x, n == 1 ? x : v.y, n == 2 ? x : v.z); }
            float4 Put(float4 v, int n, float x) { return float4(n == 0 ? x : v.x, n == 1 ? x : v.y, n == 2 ? x : v.z, n == 3 ? x : v.w); }
            // gl_FragCoord: which pixel is being drawn, its rows counted from the bottom, as every picture's are read here. Direct3D counts
            // the rows of what it draws into from the top, and the game draws into its pictures upside down to make up for it (saying so with
            // _ProjectionParams.x at -1): so where it is drawing straight to the screen, the row is turned over, or each picture read by pixel
            // (the camera's depth, the smoke drawn small) would be read upside down.
            float4 FragCoord(float4 pos)
            {
            #if UNITY_UV_STARTS_AT_TOP
                if (_ProjectionParams.x > 0.0) pos.y = _ScreenParams.y - pos.y;
            #endif
                return pos;
            }

            float4 _SunMapFrom;     // xyz: a point on the plane the distances are measured from, on the sun's side of the smoke
            float4 _SunMapAlong;     // xyz: the way the sun's light goes (away from the sun)


            struct v2f
            {
                float4 pos : SV_POSITION;
                float vs_TEXCOORD0 : TEXCOORD0;
            };

            // Volumetric Explosions: what stands between the smoke and the sun, drawn as the sun sees it (see SunShadow.cs). Every
            // solid thing about the patch of smoke (the craft, the tower, the buildings, the ground) is drawn into a small picture
            // taken from the sun's side, and what is kept at each dot of it is how far along the sun's light the nearest of them
            // is, from a plane on the sun's side: so the smoke's shader can tell, at any place in the smoke, whether something
            // stands between it and the sun.

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                float4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
                OUT.pos = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
                OUT.vs_TEXCOORD0 = dot(world.xyz - _SunMapFrom.xyz, _SunMapAlong.xyz);
                return OUT;
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                result = float4(IN.vs_TEXCOORD0, 0.0, 0.0, 1.0);
                return result;
            }
            ENDCG
        }
    }
}
