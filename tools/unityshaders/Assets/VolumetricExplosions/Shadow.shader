// VolumetricExplosions/Shadow: made from tools/shaderpack/shadow.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Shadow"
{
    Properties
    {
        _Volume ("Volume", 3D) = "" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend DstColor Zero
            ZWrite Off
            ZTest Always
            Cull Front

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
            #define Mod(x, y) ((x) - (y) * floor((x) / (y)))
            #define GreaterThan(a, b) ((a) > (b))
            #define GreaterThanEqual(a, b) ((a) >= (b))
            #define LessThan(a, b) ((a) < (b))
            #define LessThanEqual(a, b) ((a) <= (b))
            float4 TexelFetch(Texture2D t, int2 p, int lod) { return t.Load(int3(p, lod)); }
            float4 TexelFetch(Texture3D t, int3 p, int lod) { return t.Load(int4(p, lod)); }
            int2 TextureSize(Texture2D t, int lod) { uint w, h; t.GetDimensions(w, h); return int2(w, h); }
            bool AllZero(float4 v) { return !any(v); }
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

            float4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene
            float4 _ShadowGridX;     // the rows of the turn from the scene's axes into the cloud's grid box (-0.5 to 0.5 each way)
            float4 _ShadowGridY;
            float4 _ShadowGridZ;
            float4 _ShadowSun;     // xyz: towards the sun, in the grid box's own axes (and its sizes); w: how dark a shadow may be (0: none)
            float4 _ShadowSunWorld;     // xyz: towards the sun, in the scene's axes
            Texture3D _Volume; SamplerState sampler_Volume;
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 vs_TEXCOORD0 : TEXCOORD0;
                float4 vs_TEXCOORD1 : TEXCOORD1;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                float4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
                float4 clip_ = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
                // (the far faces of the box are what is drawn; they are kept inside the camera's range, as for the smoke)
#if defined(UNITY_REVERSED_Z)
                clip_.z = max(clip_.z, clip_.w * 0.00001);
#else
                clip_.z = min(clip_.z, clip_.w * 0.99999);
#endif
                OUT.pos = clip_;
                OUT.vs_TEXCOORD0 = world.xyz;
                OUT.vs_TEXCOORD1 = clip_;
                return OUT;
            }

            // The shadow a cloud of smoke throws on whatever is under it. The box this is drawn with is the stretch the shadow can
            // fall in (the cloud's box drawn out away from the sun to the ground). For each pixel of it the place the game has drawn
            // there is worked out from its depth picture and followed towards the sun into the cloud's grid, where how much smoke
            // lies between each cell and the sun is kept already (the smoke's own light is worked out from it): what is drawn there
            // is darkened by as much of the sunlight as that smoke takes away. So a cloud's shadow lies on the ground, the pad, the
            // buildings and the ships as they are, and goes soft and thin where the smoke does.

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float3 eye = _WorldSpaceCameraPos;
                float3 rd = normalize(IN.vs_TEXCOORD0 - eye);
                float forward = -(hlslcc_mtx4x4unity_MatrixV[0].z * rd.x + hlslcc_mtx4x4unity_MatrixV[1].z * rd.y + hlslcc_mtx4x4unity_MatrixV[2].z * rd.z);
                forward = max(forward, 1e-4);
                float2 uv = float2(IN.vs_TEXCOORD1.x / IN.vs_TEXCOORD1.w, IN.vs_TEXCOORD1.y / IN.vs_TEXCOORD1.w * _ProjectionParams.x) * 0.5 + 0.5;
                float depth = 1.0 / (_ZBufferParams.z * SampleFlat(_CameraDepthTexture, uv).x + _ZBufferParams.w);
                if (_VolCamera.x < 0.5 || depth > _ProjectionParams.z * 0.999) discard;
                float3 place = eye + rd * (depth / forward);
                // Which way the surface there faces (from how the place changes from one pixel to the next, turned to face the
                // camera): a face turned from the sun is in its own shadow already, and has no sunlight for the smoke to take.
                float3 facing = cross(ddx(place), ddy(place));
                float size = length(facing);
                facing = size > 1e-8 ? facing / size : -rd;
                if (dot(facing, rd) > 0.0) facing = -facing;
                float lit_ = clamp(dot(facing, _ShadowSunWorld.xyz) * 4.0, 0.0, 1.0);
                if (lit_ <= 0.0) discard;
                // Into the grid's box, on the way to the sun.
                float3 p = float3(dot(_ShadowGridX.xyz, place) + _ShadowGridX.w, dot(_ShadowGridY.xyz, place) + _ShadowGridY.w, dot(_ShadowGridZ.xyz, place) + _ShadowGridZ.w);
                float3 d = _ShadowSun.xyz;
                float3 safe = float3(abs(d.x) < 1e-6 ? 1e-6 : d.x, abs(d.y) < 1e-6 ? 1e-6 : d.y, abs(d.z) < 1e-6 ? 1e-6 : d.z);
                float3 ta = (((float3)(-0.5)) - p) / safe, tb = (((float3)(0.5)) - p) / safe;
                float3 lo = min(ta, tb), hi = max(ta, tb);
                float t0 = max(max(lo.x, lo.y), max(lo.z, 0.0)), t1 = min(min(hi.x, hi.y), hi.z);
                if (t1 <= t0) discard;
                float3 e = clamp(p + d * (t0 + 1e-3 * (t1 - t0)), ((float3)(-0.5)), ((float3)(0.5)));
                float4 air = SampleLod(_Volume, e + 0.5, 0.0);
                float before = (air.r * 0.99611 + air.g * 0.0038911) * 16.0;
                // (as the smoke's own light reckons it: a part of the sunlight finds its way round inside a cloud)
                float through = 0.8 * exp(-before) + 0.2 * exp(-0.25 * before);
                float dark = _ShadowSun.w * lit_ * (1.0 - through);
                if (!(dark > 0.004)) discard;
                result = float4(((float3)(1.0 - min(dark, 0.95))), 1.0);
                return result;
            }
            ENDCG
        }
    }
}
