// VolumetricExplosions/Mark: made from tools/shaderpack/mark.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Mark"
{
    Properties
    {
        _MarkTex ("MarkTex", 2D) = "" {}
        _MarkEmbers ("MarkEmbers", 2D) = "" {}
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
            float4 TexelFetch(Texture2D t, int2 p, int lod) { return t.Load(int3(p, lod)); }
            float4 TexelFetch(Texture3D t, int3 p, int lod) { return t.Load(int4(p, lod)); }
            int2 TextureSize(Texture2D t, int lod) { uint w, h; t.GetDimensions(w, h); return int2(w, h); }
            bool AllZero(float4 v) { return !any(v); }

            float4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene, y: the angle one of its pixels covers
            float4 _MarkTint;     // rgb: the colour the mark's picture is multiplied by, a: how strongly the mark shows
            float4 _MarkGlow;     // rgb: how brightly the embers glow
            float4 _MarkSize;     // x: metres to a cell of the mark's picture
            Texture2D _MarkTex; SamplerState sampler_MarkTex;
            Texture2D _MarkEmbers; SamplerState sampler_MarkEmbers;
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;

            float4 DepthFetch(int2 p, int lod)
            {
            #if UNITY_UV_STARTS_AT_TOP
                if (_ProjectionParams.x > 0.0) { uint w, h; _CameraDepthTexture.GetDimensions(w, h); p.y = (int)h - 1 - p.y; }
            #endif
                return _CameraDepthTexture.Load(int3(p, lod));
            }


            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 vs_TEXCOORD0 : TEXCOORD0;
                float4 vs_TEXCOORD1 : TEXCOORD1;
                float3 vs_TEXCOORD2 : TEXCOORD2;
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
                OUT.vs_TEXCOORD2 = normalize(hlslcc_mtx4x4unity_ObjectToWorld[1].xyz);
                return OUT;
            }

            // A burn mark, thrown onto whatever is there. The mark is a flat box laid on the ground where the fire
            // was. For each pixel of the box the place the game has already drawn there is worked out from its depth
            // picture, and if that place lies inside the box it takes the mark's colour at the spot straight "above"
            // it. So the mark follows steps, ramps and roofs exactly, and cannot float over a slope or sink into one.

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float3 eye = _WorldSpaceCameraPos;
                float3 rd = normalize(IN.vs_TEXCOORD0 - eye);
                float forward = -(hlslcc_mtx4x4unity_MatrixV[0].z * rd.x + hlslcc_mtx4x4unity_MatrixV[1].z * rd.y + hlslcc_mtx4x4unity_MatrixV[2].z * rd.z);
                forward = max(forward, 1e-4);
                float2 uv = float2(IN.vs_TEXCOORD1.x / IN.vs_TEXCOORD1.w, IN.vs_TEXCOORD1.y / IN.vs_TEXCOORD1.w * _ProjectionParams.x) * 0.5 + 0.5;
                float depth = 1.0 / (_ZBufferParams.z * SampleFlat(_CameraDepthTexture, uv).x + _ZBufferParams.w);
                float3 place = eye + rd * (depth / forward);
                float3 inBox = hlslcc_mtx4x4unity_WorldToObject[0].xyz * place.x + hlslcc_mtx4x4unity_WorldToObject[1].xyz * place.y + hlslcc_mtx4x4unity_WorldToObject[2].xyz * place.z + hlslcc_mtx4x4unity_WorldToObject[3].xyz;
                // Which way the surface there faces, from how the place changes from one pixel to the next. A face
                // turned well away from the mark (a wall, the side of a ship standing in it) is left alone: thrown
                // onto it from above, the mark would be drawn out into streaks.
                float3 facing = normalize(cross(ddx(place), ddy(place)));
                float flat_ = smoothstep(0.3, 0.6, abs(dot(facing, IN.vs_TEXCOORD2)));
                // The picture is read as coarsely as it shows from here (worked out from the distance: the graphics card's own way goes wrong along the outline of whatever stands in front).
                float level = max(log2(depth * _VolCamera.y / _MarkSize.x), 0.0) + 0.5 * (1.0 - flat_);
                float4 mark = SampleLod(_MarkTex, inBox.xz + 0.5, level);
                float4 hot = SampleLod(_MarkEmbers, inBox.xz + 0.5, level);
                float3 embers = hot.rgb * hot.a;
                float inside = step(abs(inBox.x), 0.5) * step(abs(inBox.z), 0.5) * (1.0 - smoothstep(0.35, 0.5, abs(inBox.y)));
                float shown = _VolCamera.x * step(depth, _ProjectionParams.z * 0.999) * inside * flat_;
                float a = mark.a * _MarkTint.a * shown;
                float3 glow = embers * _MarkGlow.rgb * shown;
                if (a + glow.r + glow.g + glow.b <= 0.0005) discard;
                result = float4(mark.rgb * _MarkTint.rgb * a + glow, a);
                return result;
            }
            ENDCG
        }
    }
}
