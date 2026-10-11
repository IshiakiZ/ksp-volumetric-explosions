// VolumetricExplosions/Shock: made from tools/shaderpack/shock.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Shock"
{
    Properties
    {
        _VolScene ("VolScene", 2D) = "" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
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

            float4 _ShockSheet;     // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
            float4 _ShockEye;     // xyz: where the camera is
            float4 _ShockRight;     // xyz: the camera's right, as long as half the picture is wide at one metre off; w: that length
            float4 _ShockUp;     // the same, upward
            float4 _ShockAhead;     // xyz: the way the camera looks, w: the picture's height over its width
            float4 _ShockDepth;     // xy: turn a depth read from the picture of depths into metres ahead, z: 1 if there is such a picture, w: what it reads where nothing was drawn
            float4 _ShockC0;     // xyz: the middle of a front, w: its radius in metres (0: there is none)
            float4 _ShockC1;
            float4 _ShockC2;
            float4 _ShockC3;
            float4 _ShockP0;     // x: how far it pushes the picture at most, in heights of the screen, y: how wide the front is, in metres, z: the least it may look, in heights of the screen, w: how much lighter and darker it shows
            float4 _ShockP1;
            float4 _ShockP2;
            float4 _ShockP3;
            float4 _ShockG0;     // xyz: which way is up where the front is, w: how far its ring along the ground pushes the picture, in heights of the screen (0: it is not on the ground)
            float4 _ShockG1;
            float4 _ShockG2;
            float4 _ShockG3;
            float4 _ShockPulse;     // x: how far the whole picture is read from further out at its corners just now, in halves of the screen (less than nothing: the picture swells), y: how far the colours part
            Texture2D _VolScene; SamplerState sampler_VolScene;     // the picture so far
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;
            static float shade;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 vs_TEXCOORD0 : TEXCOORD0;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                // One triangle that covers the whole screen, given in the screen's own coordinates (-1 to 1 each way).
                OUT.pos = float4(in_POSITION0.xy * _ShockSheet.xy, _ShockSheet.z, _ShockSheet.w);
                OUT.vs_TEXCOORD0 = float2(in_POSITION0.x, in_POSITION0.y * _ProjectionParams.x);
                return OUT;
            }

            // The shock front of a blast: a thin shell of squeezed air racing outward, which bends the light that
            // passes through it. Nothing is drawn of the shell itself. The picture as it stands (everything drawn so
            // far, smoke and fire included) is drawn again with each pixel read from a little to one side: where the
            // line of sight just grazes the shell, which from outside is a ring round the blast that sweeps across the
            // view, and where the shell meets the ground, a ring racing along it. Across the front the picture is drawn
            // together from both sides and let go again, smoothly, so that there is no line to be seen anywhere: only what
            // lies behind, rippling as the front goes over it. Whatever stands in front of the shell is left alone.
            //
            // And when a front reaches the camera itself the whole picture swells, most at its corners, and bounces back.


            // Across a front, from one width inside it (-1) to one width outside (1): nothing right at it, the picture
            // drawn towards it from both sides, and nothing again a width away. (4.68: so that the most is 1.)
            float ripple(float d)
            {
                return d * (1.0 - pow(abs(d), 0.8)) * 4.68;
            }

            // Where on the screen a place is, given from the eye.
            float2 screenOf(float3 v)
            {
                return float2(dot(v, _ShockRight.xyz) / (_ShockRight.w * _ShockRight.w), dot(v, _ShockUp.xyz) / (_ShockUp.w * _ShockUp.w)) / max(dot(v, _ShockAhead.xyz), 1e-3) * 0.5 + 0.5;
            }

            float2 front(float4 c, float4 p, float4 g, float3 rd, float scene)
            {
                float2 moved = ((float2)(0.0));
                if (c.w <= 0.0) return moved;
                float span = 2.0 * _ShockUp.w;                 // metres to a height of the screen, a metre off
                float3 toC = c.xyz - _ShockEye.xyz;
                float along = dot(toC, rd);
                float3 off = toC - rd * along;                   // from where the line of sight passes nearest the middle of the ball, to the middle
                float b = length(off);
                if (along > 0.0 && b > 1e-3)
                {
                    // The shell seen edge-on. Inside its outline the front can be no wider than the ball; the push is less there by as much, so that it runs smoothly through.
                    float wide = max(p.y, p.z * along * span);
                    float inner = min(wide, 0.8 * c.w);
                    float d = b - c.w;
                    float part = d > 0.0 ? 1.0 : inner / wide;
                    d /= d > 0.0 ? wide : inner;
                    if (abs(d) < 1.0)
                    {
                        // (pushed further than a part of its own width, the picture would fold over on itself)
                        float most = min(p.x, 0.2 * wide / (along * span)) * part * ripple(d);
                        // Not what stands in front of the shell, nor (for a moment, as the front passes the camera) what is beside the camera.
                        most *= smoothstep(along - wide, along + wide, scene) * smoothstep(0.0, wide, along);
                        float2 out2 = -float2(dot(off, _ShockRight.xyz) / _ShockRight.w, dot(off, _ShockUp.xyz) / _ShockUp.w);
                        float len = length(out2);
                        if (len > 1e-4)
                        {
                            moved += out2 / len * float2(_ShockAhead.w, 1.0) * most;
                            shade += p.w * most / max(p.x, 1e-5);
                        }
                    }
                }
                if (g.w > 0.0)
                {
                    // The ring along the ground, where the shell meets it: the ground itself seems to give under it.
                    float down = -dot(rd, g.xyz), high = -dot(toC, g.xyz);
                    if (down > 1e-3 && high > 0.0)
                    {
                        float far = high / down;               // how far off the line of sight meets the ground
                        float3 h = rd * far - toC;               // from the middle of the ring to there
                        float r = length(h);
                        float wide = max(p.y, p.z * far * span);
                        float inner = min(wide, 0.8 * c.w);
                        float d = r - c.w;
                        float part = d > 0.0 ? 1.0 : inner / wide;
                        d /= d > 0.0 ? wide : inner;
                        if (abs(d) < 1.0 && r > 1e-3)
                        {
                            float metres = min(g.w * far * span, 0.2 * wide) * part * ripple(d);
                            // Only where what is seen there really is the ground (or as good as), if that can be told.
                            if (_ShockDepth.z > 0.5) metres *= 1.0 - smoothstep(0.04 * far + 0.5, 0.12 * far + 2.0, abs(scene - far));
                            moved += screenOf(rd * far + h / r * metres) - screenOf(rd * far);
                            shade += p.w * metres / max(g.w * far * span, 1e-5);
                        }
                    }
                }
                return moved;
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float2 at = IN.vs_TEXCOORD0;
                float2 uv = at * 0.5 + 0.5;
                float3 rd = normalize(_ShockAhead.xyz + _ShockRight.xyz * at.x + _ShockUp.xyz * at.y);
                float scene = 1.0e9;
                if (_ShockDepth.z > 0.5)
                {
                    float depth = SampleFlat(_CameraDepthTexture, uv).x;
                    if (abs(depth - _ShockDepth.w) > 1e-5) scene = 1.0 / (_ShockDepth.x * depth + _ShockDepth.y) / max(dot(rd, _ShockAhead.xyz), 1e-3);
                }
                shade = 0.0;
                float2 moved = front(_ShockC0, _ShockP0, _ShockG0, rd, scene) + front(_ShockC1, _ShockP1, _ShockG1, rd, scene)
                           + front(_ShockC2, _ShockP2, _ShockG2, rd, scene) + front(_ShockC3, _ShockP3, _ShockG3, rd, scene);
                moved += at * (0.5 * (0.3 + 0.35 * dot(at, at)) * _ShockPulse.x);
                float far = length(moved * float2(1.0 / _ShockAhead.w, 1.0));      // in heights of the screen
                if (far < 0.0003) discard;
                // (red, green and blue are bent a very little differently, as by glass)
                float r = SampleFlat(_VolScene, uv + moved * (1.0 + _ShockPulse.y)).r;
                float g = SampleFlat(_VolScene, uv + moved).g;
                float b = SampleFlat(_VolScene, uv + moved * (1.0 - _ShockPulse.y)).b;
                float shown = smoothstep(0.0003, 0.002, far);
                result = float4(float3(r, g, b) * (1.0 + shade) * shown, shown);
                return result;
            }
            ENDCG
        }
    }
}
