// VolumetricExplosions/Sprite: made from tools/shaderpack/sprite.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Sprite"
{
    Properties
    {
        _MainTex ("MainTex", 2D) = "" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
            ZWrite Off
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
            #define SampleMipped(S, uv) S.Sample(sampler##S, uv)
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

            float4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene; w: what the smoke's depths are kept times (this camera's)
            float4 _VolFrontOn;     // x: 1 if Layers drew its picture of the smoke in front for the camera drawing this (0: nothing taken off); y: 1 to see it (below)
            float4 _SpriteKind;     // x: the kind (above); y: how soon it fades out in front of what is behind it (the game's _InvFade; 0: not at all); z: 1 to show whatever smoke is in front
            float4 _TintColor;     // the tint the additive kind multiplies by (and doubles)
            Texture2D _MainTex; SamplerState sampler_MainTex;
            Texture2D _VolFront; SamplerState sampler_VolFront;     // (Layers) the nearest patch's: how much it hides, where its smoke lies on the whole, how widely; and how much the rest hide
            Texture2D _VolFront1; SamplerState sampler_VolFront1;     // (Layers) the rest's: where their smoke lies on the whole, how widely
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 vs_TEXCOORD0 : TEXCOORD0;
                float4 vs_TEXCOORD1 : TEXCOORD1;
                float4 vs_TEXCOORD2 : TEXCOORD2;
            };

            // Volumetric Explosions: the smoke and flames left as sprites (puffs, glows, sparks, rings, shards, dust), drawn as the game's
            // own particle shaders draw them, less as much of the smoke drawn as volumes as lies in front of each (Layers' picture of the
            // smoke in front, layers.glsl). The first of the mod's shaders to read a particle's colour and uv as well as its place
            // (make_bundle's VERTEX_INPUTS). 2026-10-28, from the Windows PC.

            v2f vert(float3 in_POSITION0 : POSITION, float4 in_COLOR0 : COLOR, float2 in_TEXCOORD0 : TEXCOORD0)
            {
                v2f OUT = (v2f)0;
                float4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
                OUT.pos = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
                float4 seen = hlslcc_mtx4x4unity_MatrixV[0] * world.x + hlslcc_mtx4x4unity_MatrixV[1] * world.y + hlslcc_mtx4x4unity_MatrixV[2] * world.z + hlslcc_mtx4x4unity_MatrixV[3] * world.w;
                OUT.vs_TEXCOORD0 = in_TEXCOORD0;
                OUT.vs_TEXCOORD1 = in_COLOR0;
                OUT.vs_TEXCOORD2 = float4(world.xyz, -seen.z);
                return OUT;
            }

            // Each kind as the game's own shader draws it (Unity's "Legacy Shaders/Particles/...", their sources read for this; the fog
            // they add is left out, as the game's scenes have none), given here as light and how much it hides, for one way of laying
            // on: what is there times what this lets through, and this one's light added.
            //   0, "Alpha Blended Premultiply" (the puffs): the colour times the picture, times the colour's opacity again.
            //   1, "Additive" (glows, sparks, rings): twice the colour times the tint times the picture, added as much as its opacity
            //      (clamped to 1), hiding nothing.
            //   2, "Alpha Blended" (shards, dust): twice the colour times the tint times the picture, laid over as much as its opacity
            //      (clamped). (Its tint is taken in the game's vertex program, the additive's in its fragment one: the same sum.)
            // Each fades out where it comes near what is drawn behind it (the "soft particles" of the game's own): its opacity times
            // how far in front of that it is, times _SpriteKind.y (0: not softened).
            //
            // Then the smoke in front. Where along this pixel's ray the smoke drawn as volumes lies, and how widely, is in Layers'
            // picture of it: the nearest patch by itself, the rest as one (see layers.glsl's start of main). Of each, as much as lies
            // in front of this sprite's depth (a bell curve's sum, as between two patches) hides it: what shows of it is what both let
            // through. A sprite at the smoke's depth on the whole shows half as much through a thick cloud as one wholly in front of it,
            // and one wholly behind it none. (Until 2026-10-28 everything drawn as a sprite was laid over the smoke whole or under it
            // whole, by its queue: a fire's flash or a puff behind a cloud showed through it.) _SpriteKind.z 1 leaves this out: the
            // flashes, which SiteBlasts dims already by what lies along the line to each.

            // How much of smoke that lies at `d` on the whole, spread `w` along the ray, lies in front of `z` (all times the camera's scale).
            float before(float z, float d, float w, float least)
            {
                return 1.0 / (1.0 + exp(-clamp(1.702 * (z - d) / sqrt(w * w + least * least), -30.0, 30.0)));
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float4 colour = IN.vs_TEXCOORD1;
                int2 pixel = ((int2)(FragCoord(IN.pos).xy));
                // (the soft edge, where there is a depth picture to go by)
                if (_SpriteKind.y > 0.0 && _VolCamera.x > 0.5)
                {
                    int2 most = TextureSize(_CameraDepthTexture, 0) - 1;
                    float scene = 1.0 / (_ZBufferParams.z * TexelFetch(_CameraDepthTexture, min(pixel, most), 0).x + _ZBufferParams.w);
                    colour.a *= clamp(_SpriteKind.y * (scene - IN.vs_TEXCOORD2.w), 0.0, 1.0);
                }
                float4 picture = SampleMipped(_MainTex, IN.vs_TEXCOORD0);
                int kind = int(_SpriteKind.x + 0.5);
                float4 result_;
                if (kind == 0) result_ = colour * picture * colour.a;
                else
                {
                    float4 lit_ = 2.0 * colour * picture * _TintColor;
                    lit_.a = clamp(lit_.a, 0.0, 1.0);
                    result_ = float4(lit_.rgb * lit_.a, kind == 1 ? 0.0 : lit_.a);
                }

                float shows = 1.0;
                if (_VolFrontOn.x > 0.5 && _SpriteKind.z < 0.5)
                {
                    int2 most = TextureSize(_VolFront, 0) - 1;
                    int2 at = min(pixel / 2, most);
                    float4 nearest = TexelFetch(_VolFront, at, 0);
                    if (nearest.x > 0.0 || nearest.w > 0.0)
                    {
                        float2 rest = TexelFetch(_VolFront1, at, 0).xy;
                        float scale = max(_VolCamera.w, 1e-6);
                        float metres = length(IN.vs_TEXCOORD2.xyz - _WorldSpaceCameraPos);
                        float z = metres * scale;
                        // (never sharper than a few tenths of a metre, more further off: as between two patches, layers.glsl)
                        float least = (0.3 + 0.004 * metres) * scale;
                        shows = (1.0 - nearest.x * before(z, nearest.y, nearest.z, least)) * (1.0 - nearest.w * before(z, rest.x, rest.y, least));
                        result_ *= shows;
                    }
                }
                // (for testing, Layers.TestFront: each sprite as much of it as shows through the smoke in front, green all of it, red none)
                if (_VolFrontOn.y > 0.5) result_ = float4(1.0 - shows, shows, 0.0, 1.0) * clamp(4.0 * picture.a * colour.a, 0.0, 1.0);
                result = result_;
                return result;
            }
            ENDCG
        }
    }
}
