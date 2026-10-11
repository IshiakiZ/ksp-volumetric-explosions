// VolumetricExplosions/Enlarge: made from tools/shaderpack/enlarge.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Enlarge"
{
    Properties
    {
        _VolHalf ("VolHalf", 2D) = "" {}
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

            float4 _VolDrawn;     // x: 1 if this cloud's small picture was drawn for the camera drawing now (see Air.ForCamera)
            float4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene, z: 1 if the small picture was drawn for this camera
            Texture2D _VolHalf; SamplerState sampler_VolHalf;     // the smoke, drawn small: its colour times how much it hides, and how much it hides
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                // (a camera none of this cloud lies in the range of: nothing drawn at all)
                if (_VolDrawn.x < 0.5) { OUT.pos = float4(2.0, 2.0, 2.0, 1.0); return OUT; }
                float4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
                float4 clip_ = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
                // (the far faces of the smoke's box, kept inside the camera's range: exactly as the smoke itself is drawn)
#if defined(UNITY_REVERSED_Z)
                clip_.z = max(clip_.z, clip_.w * 0.00001);
#else
                clip_.z = min(clip_.z, clip_.w * 0.99999);
#endif
                OUT.pos = clip_;
                return OUT;
            }

            // Smoke is soft, and walking a ray through it for every pixel of the screen is most of what it costs. So it
            // is drawn first into a picture of its own with half as many pixels each way (see volume.glsl and
            // Air.Halves), and this puts that picture on the screen, on the same box, enlarged.
            //
            // Enlarged plainly it would smear across the outline of anything standing in the smoke: the small picture's
            // pixels beside a rocket stopped at the rocket or went on past it, and a pixel of the screen on the rocket's
            // edge would get some of each. So each pixel of the small picture stopped at the nearest or the farthest of
            // the four things behind it, turn and turn about; here the same is worked out again for the four small pixels
            // round each pixel of the screen, and those that stopped where this pixel's own surface is count for most.
            // Where they all stopped at much the same distance this is the ordinary smooth enlargement.
            //
            // "Much the same" has to allow for a surface that slopes away from the camera: on level ground seen from low
            // down, the distance changes by several parts in a hundred from one pixel to the next, more the further off.
            // Judged against a fixed allowance, each pixel there took all its smoke from the one small pixel that
            // happened to match best, and the enlargement was no enlargement at all: little squares, two pixels wide.

            // How far ahead the ray of one of the small picture's pixels stopped.
            float stopped(int2 cell, int2 most)
            {
                float4 four = float4(TexelFetch(_CameraDepthTexture, min(2 * cell, most), 0).x, TexelFetch(_CameraDepthTexture, min(2 * cell + int2(1, 0), most), 0).x, TexelFetch(_CameraDepthTexture, min(2 * cell + int2(0, 1), most), 0).x, TexelFetch(_CameraDepthTexture, min(2 * cell + int2(1, 1), most), 0).x);
                four = 1.0 / (_ZBufferParams.z * four + _ZBufferParams.w);
                return ((cell.x + cell.y) & 1) == 0 ? min(min(four.x, four.y), min(four.z, four.w)) : max(max(four.x, four.y), max(four.z, four.w));
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                if (_VolCamera.z < 0.5) discard;
                int2 pixel = ((int2)(FragCoord(IN.pos).xy)), last = TextureSize(_VolHalf, 0) - 1;
                // The four small pixels round this one, and how much each counts for by nearness alone.
                float2 at = (((float2)(pixel)) + 0.5) * 0.5 - 0.5;
                int2 base = ((int2)(floor(at)));
                float2 f = at - ((float2)(base));
                int2 c00 = clamp(base, ((int2)(0)), last), c10 = clamp(base + int2(1, 0), ((int2)(0)), last), c01 = clamp(base + int2(0, 1), ((int2)(0)), last), c11 = clamp(base + int2(1, 1), ((int2)(0)), last);
                float4 share = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
                // (Most of the box is clear air: where none of the four has anything in it there is nothing to put on the
                // screen, however they are weighed, and the twenty-one looks at the depth picture below are spared.)
                float4 s00 = TexelFetch(_VolHalf, c00, 0), s10 = TexelFetch(_VolHalf, c10, 0), s01 = TexelFetch(_VolHalf, c01, 0), s11 = TexelFetch(_VolHalf, c11, 0);
                if (AllZero(s00) && AllZero(s10) && AllZero(s01) && AllZero(s11)) discard;
                if (_VolCamera.x > 0.5)
                {
                    int2 most = TextureSize(_CameraDepthTexture, 0) - 1;
                    float here = 1.0 / (_ZBufferParams.z * TexelFetch(_CameraDepthTexture, min(pixel, most), 0).x + _ZBufferParams.w);
                    float4 apart = float4(stopped(c00, most), stopped(c10, most), stopped(c01, most), stopped(c11, most)) - here;
                    // How fast this pixel's own surface goes away from the camera, pixel to pixel: to either side and up and
                    // down, the smaller change of each pair (at the edge of something, the other is the jump to what is behind it).
                    float4 beside = float4(TexelFetch(_CameraDepthTexture, clamp(pixel - int2(1, 0), ((int2)(0)), most), 0).x, TexelFetch(_CameraDepthTexture, clamp(pixel + int2(1, 0), ((int2)(0)), most), 0).x, TexelFetch(_CameraDepthTexture, clamp(pixel - int2(0, 1), ((int2)(0)), most), 0).x, TexelFetch(_CameraDepthTexture, clamp(pixel + int2(0, 1), ((int2)(0)), most), 0).x);
                    beside = abs(1.0 / (_ZBufferParams.z * beside + _ZBufferParams.w) - here);
                    float slopes = min(beside.x, beside.y) + min(beside.z, beside.w);
                    // (the small pixels stopped at things up to two pixels and a bit from this one)
                    float slack = 0.01 * here + 0.01 + 3.0 * slopes;
                    share /= apart * apart + slack * slack;
                    share /= share.x + share.y + share.z + share.w;
                }
                float4 smoke = s00 * share.x + s10 * share.y + s01 * share.z + s11 * share.w;
                if (smoke.a <= 0.0005 && smoke.r + smoke.g + smoke.b <= 0.0005) discard;
                result = smoke;
                return result;
            }
            ENDCG
        }
    }
}
