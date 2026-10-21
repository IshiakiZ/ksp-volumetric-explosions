// VolumetricExplosions/Carry: made from tools/shaderpack/carry.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Carry"
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
            ZWrite Off
            ZTest Always
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

            float4 _CarryAt;     // xy: where the slot begins in what is drawn into, pixels; zw: the slot's size, pixels
            float4 _CarryQ;     // xy: which pixel of each two by two was walked this frame; z: 1 to carry over, 0 to take the walked pixels' alone (no frame before to be trusted), 2 to copy the frame before as it is; w: what the depths kept with the smoke were multiplied by (_VolCamera.w)
            float4 _CarryCam;     // xyz: the camera now (the scene's place)
            float4 _CarryRayX;     // rows taking a pixel's place on the picture (-1 to 1 each way, 1, 0) to the way of its ray (not of one length)
            float4 _CarryRayY;
            float4 _CarryRayZ;
            float4 _CarryWasX;     // rows of what took a place in the scene to last frame's picture: across, up, and w
            float4 _CarryWasY;
            float4 _CarryWasW;
            float4 _CarryBoxX;     // rows taking a place in the box now to where that place of the box was a frame ago (the scene's places)
            float4 _CarryBoxY;
            float4 _CarryBoxZ;
            Texture2D _CarryQuarter; SamplerState sampler_CarryQuarter;     // this frame's walk: light and how much it hides
            Texture2D _CarryQuarterDepth; SamplerState sampler_CarryQuarterDepth;     // r: how far along the ray what it hides lies, times what it hides (times _CarryQ.w); g: how widely
            Texture2D _CarryWas; SamplerState sampler_CarryWas;     // last frame's, as this wrote it
            Texture2D _CarryWasDepth; SamplerState sampler_CarryWasDepth;

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                // (one sheet over all of what is drawn into: its corners are 0 and 1 each way)
                OUT.pos = float4(in_POSITION0.xy * 2.0 - 1.0, 0.0, 1.0);
                return OUT;
            }

            // A patch of smoke walked at one pixel in each two by two this frame, the four in turn (volume.glsl with _VolQuarter, into a
            // picture half as wide and tall), and the other three carried over from the frame before (Layers: research/rendering/notes/
            // smoke-temporal.md). Drawn twice with the same numbers: into the patch's slot of the atlas, which the sheet lays on the screen
            // as before, and into the patch's own picture of what it was, which the next frame carries over from.
            //
            // A pixel walked this frame keeps what it found. One not walked: its smoke is taken to lie as far along its ray as the walked
            // pixels round it found theirs (by how much each hides; where none of them hides anything, as far as this pixel's own did last
            // frame), that place is carried back by how the patch's box has moved since, and seen by last frame's camera; what was there
            // last frame is fetched and held between the least and the most of the walked pixels round about. Smoke that has changed,
            // or come out from behind something, does not linger longer than the four frames it takes every pixel to be walked again.
            // (In the lab, inside a booster's cloud, with the camera flying at up to 150 m/s and turning 90 degrees a second, and the smoke
            // going on between grids: within 0.2 of 255 of the patch walked whole, for 60-66 % less of the walk.)

            struct both
            {
                float4 result : SV_Target0;
                float4 result1 : SV_Target1;
            };

            both Both(float4 a, float4 b) { both o; o.result = a; o.result1 = b; return o; }

            both frag(v2f IN)
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float4 result1 = float4(0.0, 0.0, 0.0, 0.0);
                int2 px = ((int2)(FragCoord(IN.pos).xy - _CarryAt.xy));
                int2 size = ((int2)(_CarryAt.zw + 0.5));
                if (_CarryQ.z > 1.5) { result = TexelFetch(_CarryWas, px, 0); result1 = TexelFetch(_CarryWasDepth, px, 0); return Both(result, result1); }
                int2 q = ((int2)(_CarryQ.xy + 0.5));
                if ((px.x & 1) == q.x && (px.y & 1) == q.y) { result = TexelFetch(_CarryQuarter, px >> 1, 0); result1 = TexelFetch(_CarryQuarterDepth, px >> 1, 0); return Both(result, result1); }
                // (the walked pixels round this one: in a three by three, one, two or four of them)
                float4 lo = ((float4)(1e9)), hi = ((float4)(-1e9)), loD = ((float4)(1e9)), hiD = ((float4)(-1e9));
                float depthSum = 0.0, hideSum = 0.0;
                { [loop] for (int j = -1; j <= 1; j++)
                    { [loop] for (int i = -1; i <= 1; i++)
                    {
                        int2 at = px + int2(i, j);
                        if ((at.x & 1) != q.x || (at.y & 1) != q.y || any(LessThan(at, ((int2)(0)))) || any(GreaterThanEqual(at, size))) continue;
                        float4 c = TexelFetch(_CarryQuarter, at >> 1, 0), d = TexelFetch(_CarryQuarterDepth, at >> 1, 0);
                        lo = min(lo, c); hi = max(hi, c); loD = min(loD, d); hiD = max(hiD, d);
                        depthSum += d.r;
                        hideSum += c.a;
                    } } }
                float4 result_ = 0.5 * (lo + hi), resultD = 0.5 * (loD + hiD);
                { [loop] for (int once = _CarryQ.z > 0.5 ? 0 : 1; once < 1; once++)
                {
                    float scale = max(_CarryQ.w, 1e-6);
                    float4 own = TexelFetch(_CarryWas, px, 0);
                    float far = hideSum > 1e-3 ? depthSum / (hideSum * scale) : (own.a > 1e-3 ? TexelFetch(_CarryWasDepth, px, 0).r / (own.a * scale) : 0.0);
                    if (far <= 0.0) break;
                    // (this pixel's ray, from its place on the picture. Where the game draws into its pictures upside down (Direct3D), their
                    // first row is their bottom one all the same, as here, and a place on one is read at the same numbers.)
                    float2 ndc = (((float2)(px)) + 0.5) / _CarryAt.zw * 2.0 - 1.0;
                    float4 n = float4(ndc, 1.0, 0.0);
                    float3 ray = normalize(float3(dot(_CarryRayX, n), dot(_CarryRayY, n), dot(_CarryRayZ, n)));
                    float4 here = float4(_CarryCam.xyz + ray * far, 1.0);
                    float4 was = float4(dot(_CarryBoxX, here), dot(_CarryBoxY, here), dot(_CarryBoxZ, here), 1.0);
                    float w = dot(_CarryWasW, was);
                    if (w <= 1e-4) break;
                    float2 uv = float2(dot(_CarryWasX, was), dot(_CarryWasY, was)) / w * 0.5 + 0.5;
                    if (any(LessThan(uv, ((float2)(0.0)))) || any(GreaterThan(uv, ((float2)(1.0))))) break;
                    float4 back = SampleLod(_CarryWas, uv, 0.0), backD = SampleLod(_CarryWasDepth, uv, 0.0);
                    result_ = clamp(back, lo, hi);
                    // (its depths kept with what it hides now)
                    resultD = backD * (back.a > 1e-4 ? result_.a / back.a : 0.0);
                } }
                result = result_;
                result1 = resultD;
                return Both(result, result1);
            }
            ENDCG
        }
    }
}
