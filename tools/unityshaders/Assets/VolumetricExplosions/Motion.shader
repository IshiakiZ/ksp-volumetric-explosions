// VolumetricExplosions/Motion: made from tools/shaderpack/motion.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Motion"
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

            float4 _VolCamera;     // w: what the depths kept with the smoke were multiplied by (for this camera)
            float4 _VolSlot;     // xy: where this patch's slot begins in the atlas, in pixels; z: 1 if a slot is half the screen's size each way
            float4 _MotGoes;     // xyz: how far the patch as a whole has gone since the last frame, m (the scene's axes: its own drift, and whatever the ground or the ship it is fixed to has moved in the scene); w: how long the frame is, s
            float4 _MotFlow;     // x: what turns a number read from _Flow, less a half, into m/s along the grid's axes; y: how much the smoke must hide before it is written here; z: the least that smoke which moves at all is said to have moved, in widths of the picture (see the end; nothing: as it has moved)
            float4 _MotAxisX;     // the grid's three axes, in the scene's (units)
            float4 _MotAxisY;
            float4 _MotAxisZ;
            float4 _MotNowX;     // three rows of what takes a place in the scene to where it is on the picture, without the smoothing's shake: across, up, and how far (the last)
            float4 _MotNowY;
            float4 _MotNowW;
            float4 _MotWasX;     // and the same for the frame before
            float4 _MotWasY;
            float4 _MotWasW;
            Texture2D _VolAtlas; SamplerState sampler_VolAtlas;     // the patches' smoke, each in its slot: a, how much it hides
            Texture2D _VolAtlasDepth; SamplerState sampler_VolAtlasDepth;     // r: how far along the ray what it hides lies, times how much it hides (and times _VolCamera.w)
            Texture3D _Flow; SamplerState sampler_Flow;     // how fast the smoke in each part of the grid was going when the grid was made

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 vs_TEXCOORD0 : TEXCOORD0;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                float4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
                float4 clip_ = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
                // (the far faces of the box, held inside the camera's range, as the walk itself draws them: volume.glsl)
                clip_.z = min(clip_.z, clip_.w * 0.99999);
                OUT.pos = clip_;
                OUT.vs_TEXCOORD0 = world.xyz;
                return OUT;
            }

            // Where a patch's smoke is going, written into the game's picture of how far each pixel has moved since the last frame.
            //
            // A game that smooths its picture between frames (temporal antialiasing: TUFX turns it on for the near camera) keeps a
            // picture of the frames before, and for each pixel takes most of its colour from where that pixel's surface WAS in it,
            // going by this picture of motion. The game draws that picture from its solid things alone. Smoke is laid over them and
            // has no place in it: so where smoke drifts across ground that stands still, the smoothing takes the smoke for a thing
            // standing still that changes colour, and mixes each frame into nineteen of the frames before it. Smoke that hangs is
            // sharpened by that; smoke that moves is smeared into a blur as long as it has gone in a third of a second, and jumps
            // about with the smoothing's own sub-pixel shake (the owner, 2026-10-13: "every time it's moving, is lower quality for
            // the entire area ... jittery and weird"; photographed: fire in a 7 m/s wind, a soft orange smear with the smoothing
            // on, tongues of flame with it off).
            //
            // So, wherever a patch's smoke hides a good part of what is behind it, this writes how far that smoke has moved across
            // the picture since the last frame: the place along this pixel's ray where the smoke lies (the walk kept it: see
            // layers.glsl), how fast the smoke there is going (the patch as a whole, and the air's own motion in it, which the grid
            // keeps for carrying the smoke on between grids), where that place was a frame ago, and where the camera of a frame
            // ago saw it. The smoothing then looks for the smoke where the smoke was.

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                int2 pixel = ((int2)(FragCoord(IN.pos).xy));
                int2 at = ((int2)(_VolSlot.xy + 0.5)) + (_VolSlot.z > 0.5 ? pixel / 2 : pixel);
                float4 smoke = TexelFetch(_VolAtlas, at, 0);
                // (a flame hides little and shines much: it counts by its light as well)
                if (max(smoke.a, min(max(max(smoke.r, smoke.g), smoke.b) * 0.3, 1.0)) < _MotFlow.y) discard;
                float hides = max(smoke.a, 1e-3);
                float far = TexelFetch(_VolAtlasDepth, at, 0).r / (hides * max(_VolCamera.w, 1e-6));
                float3 ray = normalize(IN.vs_TEXCOORD0 - _WorldSpaceCameraPos);
                float3 place = _WorldSpaceCameraPos + ray * far;
                float3 inBox = (hlslcc_mtx4x4unity_WorldToObject[0] * place.x + hlslcc_mtx4x4unity_WorldToObject[1] * place.y + hlslcc_mtx4x4unity_WorldToObject[2] * place.z + hlslcc_mtx4x4unity_WorldToObject[3]).xyz;
                float3 going = (SampleLod(_Flow, clamp(inBox + 0.5, ((float3)(0.0)), ((float3)(1.0))), 0.0).xyz - 0.5) * _MotFlow.x;
                float3 gone = _MotGoes.xyz + (_MotAxisX.xyz * going.x + _MotAxisY.xyz * going.y + _MotAxisZ.xyz * going.z) * _MotGoes.w;
                float4 here = float4(place, 1.0), was = float4(place - gone, 1.0);
                float2 now = float2(dot(_MotNowX, here), dot(_MotNowY, here)) / max(dot(_MotNowW, here), 1e-4);
                float2 before = float2(dot(_MotWasX, was), dot(_MotWasY, was)) / max(dot(_MotWasW, was), 1e-4);
                // (in widths and heights of the picture: half of what the two differ by, which runs from -1 to 1)
                float2 moved = clamp((now - before) * 0.5, ((float2)(-0.25)), ((float2)(0.25)));
                // Following the smoke is not enough. Smoke does not only move, it turns over and changes as it goes, and the
                // smoothing still mixes each frame into six before it along the way it was told: tongues of flame came out as soft
                // as before (photographed, turn and turn about, 2026-10-13). What the smoothing does with a pixel that has moved a
                // hundredth of the picture's width or more is to trust the frames before only as far as they agree with the pixels
                // round about now: so smoke that moves at all is said to have moved at least that far, the way it is going. Smoke
                // that hangs still is left to the smoothing, which sharpens it.
                float far_ = length(moved);
                float least = _MotFlow.z * smoothstep(0.00025, 0.0012, far_);
                if (far_ < least) moved = (far_ > 1e-6 ? moved / far_ : ((float2)(0.7071))) * least;
                result = float4(moved, 0.0, 1.0);
                return result;
            }
            ENDCG
        }
    }
}
