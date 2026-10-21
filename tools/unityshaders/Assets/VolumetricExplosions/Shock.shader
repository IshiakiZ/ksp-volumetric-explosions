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
            float4 _ShockW0;     // x: the blast's size, the cube root of its charge in kilograms of TNT, in metres (0: it leaves no cloud), y: how far the air at its middle is above its dew point (kelvin, as at 15 degrees), z: how much less a metre higher, w: its fireball's radius
            float4 _ShockW1;
            float4 _ShockW2;
            float4 _ShockW3;
            float4 _ShockSun;     // xyz: towards the sun
            float4 _ShockSunLight;     // rgb: the sun's light (amounts of light: what the screen shows is their square root), w: how much more a thin cloud shows towards the sun
            float4 _ShockSky;     // rgb: the sky's light (the same)
            float4 _ShockFire;     // rgb: the fire's light, as it lights the smoke a fireball's radius from it (the same)
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
            //
            // In damp air a big blast's front leaves a cloud behind it for a moment: the one thing of a front that is drawn
            // (see vapour).


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

            // A pattern of lumps, 0 to 1, smooth between whole numbers.
            float hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float lumps(float3 x)
            {
                float3 i = floor(x), f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(hash(i), hash(i + float3(1.0, 0.0, 0.0)), f.x), lerp(hash(i + float3(0.0, 1.0, 0.0)), hash(i + float3(1.0, 1.0, 0.0)), f.x), f.y),
                           lerp(lerp(hash(i + float3(0.0, 0.0, 1.0)), hash(i + float3(1.0, 0.0, 1.0)), f.x), lerp(hash(i + float3(0.0, 1.0, 1.0)), hash(i + float3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
            }

            // The cloud a big blast leaves behind its front in damp air, for a moment (a Wilson cloud). After the squeeze comes air
            // thinned below what it was, for about three times as long; air thinned that quickly cools, about 0.8 of a degree for each
            // kilopascal; and where that takes it below its dew point the water in it turns to cloud, and back again as the air comes
            // back. So a shell of white follows the front out, from one of the blast's sizes behind it to four, thickest where the air is
            // thinnest, a third of the way in; until the front has spread so far that it no longer cools the air enough, and the shell
            // goes, from the inside out. Close in the front heats the air more than its wake ever cools it: nothing forms there. (The
            // thinning: 35/Z kilopascals at most, Z the distance over the size; Kinney and Graham.) Higher up the air may be nearer its
            // dew point, or further from it, and the dome is whiter there or less. What lies behind the fire is hidden by it.
            // Walked through in a few steps on each side of the shell; added to `got`, which is the cloud before it (colour already
            // multiplied by how much it hides).
            void vapour(float4 c, float4 v, float4 g, float3 rd, float scene, inout float4 got)
            {
                if (v.x <= 0.0 || c.w <= 0.0) return;
                float size = v.x;
                float outer = c.w - 0.95 * size;
                if (outer <= 0.0) return;
                float3 toC = c.xyz - _ShockEye.xyz;
                float along = dot(toC, rd);
                float b2 = max(dot(toC, toC) - along * along, 0.0);
                if (b2 >= outer * outer) return;
                float half_ = sqrt(outer * outer - b2);
                float t0 = max(along - half_, 0.0), t1 = min(along + half_, scene);
                if (t1 <= t0) return;
                // What lies beyond the fire and its smoke is hidden by them: the far side of the shell, where the line of sight passes
                // through them. They are not in the picture of depths: taken as a ball about the fireball, wider and flatter along the
                // ground (and above it, for a blast on the ground), soft at its edge.
                float3 offC = toC - rd * along + g.xyz * (g.w > 0.0 ? 0.5 * v.w : 0.0);
                float upOff = dot(offC, g.xyz);
                float smoke = 1.0 - smoothstep(0.5, 1.15, length(float2(length(offC - g.xyz * upOff) / (1.35 * v.w), upOff / (1.2 * v.w))));
                // (the hollow inside the shell is passed over: the near side, and the far side)
                float inner = c.w - 4.0 * size;
                float hole = inner > 0.0 && b2 < inner * inner ? sqrt(inner * inner - b2) : -1.0;
                float toward = max(dot(rd, _ShockSun.xyz), 0.0);
                float shine = 1.0 + _ShockSunLight.w * toward * toward * toward * toward;
                { [loop] for (int side = 0; side < 2; side++)
                {
                    float a = side == 0 ? t0 : max(t0, along + hole);
                    float b = side == 0 ? (hole > 0.0 ? min(t1, along - hole) : t1) : t1;
                    if (side == 1 && hole <= 0.0) break;
                    if (b <= a) continue;
                    float dt = (b - a) / 6.0;
                    { [loop] for (int i = 0; i < 6; i++)
                    {
                        float3 p = rd * (a + (float(i) + 0.5) * dt) - toC;
                        float r = length(p);
                        float behind = (c.w - r) / size;
                        float k = (behind - 0.95) / 3.05;
                        if (k <= 0.0 || k >= 1.0) continue;
                        float z = max(r / size, 1.0);
                        float shape = k < 0.3 ? sin(k * 5.236) : cos((k - 0.3) * 2.244);
                        float cool = 28.5 / z * shape;
                        float wet = (cool - max(v.y - v.z * dot(p, g.xyz), 0.0)) * smoothstep(3.0, 4.5, z);
                        if (wet <= 0.0) continue;
                        float3 n = p / max(r, 1e-3);
                        // (the air is not as damp everywhere: lumps of it a few tens of metres across, damper and drier, held where they are
                        // as the front sweeps through them, which it shows up as it goes)
                        float3 air = (p + c.xyz * 0.37) / size;
                        float lumpy = 0.25 + 1.1 * lumps(air * 0.45) + 0.45 * lumps(air * 1.3 + 7.0);
                        // How thick: not by how much water the cooling could make (it could make a thundercloud's worth), but by how many
                        // drops it can start and how far they grow in the moment it lasts. Drops grow as the square root of the time, so
                        // their cross-section with the time, which goes with the blast's size: a micron or so in the tenth of a second a
                        // big blast's thinning lasts, a thousand million of them to a cubic metre in country air, hiding about one part in
                        // a hundred of the light for each metre. And more drops start the further below its dew point the air is taken,
                        // up to a limit. (A blast the size of the one in Beirut in 2020 makes an all but solid dome; a rocket's tanks, a
                        // thin bubble, brighter at its rim where the line of sight goes through more of it.)
                        float hides = 1.0 - exp(-0.0005 * size * (1.0 - exp(-0.5 * wet)) * lumpy * dt);
                        if (dot(p, rd) > 0.0) hides *= 1.0 - smoke;
                        // Lit by the sun more on its sunward side (the cloud is thin: the far side still has some), and by the sky; and by
                        // the fire inside it, far more where the fire is seen through it (small drops throw most of the light that meets
                        // them on ahead: the shell between the eye and the fire glows with it).
                        float facing = 0.62 + 0.38 * dot(n, _ShockSun.xyz);
                        float ahead = max(-dot(n, rd), 0.0);
                        float fire = v.w * v.w / (r * r + v.w * v.w) * (0.4 + 3.6 * ahead * ahead * ahead * ahead);
                        float3 lit_ = sqrt(_ShockSunLight.rgb * (facing * shine) + _ShockSky.rgb + _ShockFire.rgb * (4.0 * fire));
                        got.rgb += (1.0 - got.a) * hides * lit_;
                        got.a += (1.0 - got.a) * hides;
                    } }
                } }
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
                float4 cloud = ((float4)(0.0));
                vapour(_ShockC0, _ShockW0, _ShockG0, rd, scene, cloud);
                vapour(_ShockC1, _ShockW1, _ShockG1, rd, scene, cloud);
                vapour(_ShockC2, _ShockW2, _ShockG2, rd, scene, cloud);
                vapour(_ShockC3, _ShockW3, _ShockG3, rd, scene, cloud);
                if (far < 0.0003 && cloud.a < 0.002) discard;
                // (red, green and blue are bent a very little differently, as by glass)
                float r = SampleFlat(_VolScene, uv + moved * (1.0 + _ShockPulse.y)).r;
                float g = SampleFlat(_VolScene, uv + moved).g;
                float b = SampleFlat(_VolScene, uv + moved * (1.0 - _ShockPulse.y)).b;
                float shown = smoothstep(0.0003, 0.002, far);
                // (the cloud over what lies behind it, bent or not)
                result = float4(cloud.rgb + float3(r, g, b) * (1.0 + shade) * shown * (1.0 - cloud.a), cloud.a + shown * (1.0 - cloud.a));
                return result;
            }
            ENDCG
        }
    }
}
