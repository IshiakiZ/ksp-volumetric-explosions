// VolumetricExplosions/Layers: made from tools/shaderpack/layers.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Layers"
{
    Properties
    {
        _VolAtlas ("VolAtlas", 2D) = "" {}
        _VolAtlasDepth ("VolAtlasDepth", 2D) = "" {}
        _VolFull ("VolFull", 2D) = "" {}
        _VolFullDepth ("VolFullDepth", 2D) = "" {}
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

            float4 _VolSheet;     // the part of the screen any patch's smoke can be on, as the camera's projection gives it (-1 to 1): x0, y0, x1, y1 (x1 not beyond x0: nothing for this camera)
            float4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene, w: what the depths kept with the smoke were multiplied by (for this camera)
            float4 _VolLayers;     // x: how many patches were drawn into the atlas for this camera, y: 1 if the ships' patches were drawn into the full-sized picture, z: slots to a row of the atlas, w: 1 if a slot is half the screen's size each way (0: the screen's size)
            float4 _VolSlots;     // xy: the size of a slot, in pixels; z: (testing) 1 lays the patches over one another in the order they were drawn, as the game used to (no sorting by depth); 2 shows each patch in a colour of its own; 3 the same laid in the old order; 4 the nearest two patches' depths and spreads as colours; 5 sorted and laid over one another as until 2026-10-10
            Texture2D _VolAtlas; SamplerState sampler_VolAtlas;     // the patches' smoke, each in its slot: its colour times how much it hides, and how much it hides
            Texture2D _VolAtlasDepth; SamplerState sampler_VolAtlasDepth;     // r: how far along the ray what it hides lies, on the whole, g: how widely that is spread along it, each times how much it hides (and times _VolCamera.w)
            Texture2D _VolFull; SamplerState sampler_VolFull;     // the same, at the screen's full size, for the ships' patches
            Texture2D _VolFullDepth; SamplerState sampler_VolFullDepth;
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                // (a sheet over that part of the screen: its corners are 0 and 1 each way)
                if (_VolSheet.z <= _VolSheet.x) { OUT.pos = float4(2.0, 2.0, 2.0, 1.0); return OUT; }
                float2 corner = lerp(_VolSheet.xy, _VolSheet.zw, in_POSITION0.xy);
                OUT.pos = float4(corner.x, corner.y * _ProjectionParams.x, 0.0, 1.0);
                return OUT;
            }

            // Every patch of air's smoke on the screen at once, each part of it in front of or behind the others as it really lies.
            //
            // Each patch is walked by itself (see volume.glsl), into a picture of its own: the smoke patches that stay where they
            // are each into a slot of one big picture (the atlas), at half the screen's size each way; those that go along with
            // ships (their engines' flames and young smoke) all into one picture at the screen's full size. With the colour and how
            // much it hides, each walk keeps how far along the ray, on the whole, what it hides lies (the depth of each step
            // weighed by how much that step added to what is hidden) and how widely that is spread.
            //
            // Patches overlap. A crash leaves the cloud of the blast, the dust of the slide, the smoke of a leak, of burning
            // pieces, of a tank that went up later, each in a patch of its own, and their boxes lie in and through one another.
            // Each patch used to be put on the screen whole, one over another in the order the game sorts see-through things in
            // (by how far the middle of each box is from the camera, and the ships' patches after all the rest): so the pale smoke
            // of a small patch half inside the black cloud of a big one, its middle nearer the camera than the big box's, was
            // drawn over that cloud even where it lay behind it.
            //
            // Here, for each pixel, each patch's smoke is dimmed by every other patch's as much as that other hides in front of it,
            // going by where along this pixel's ray each one's smoke lies (see the end). Smoke wholly behind another is hidden by
            // it as much as the other hides; smoke mixed through the same stretch of air as another shows as far as each lets the
            // other through. So there is no order to get wrong, and no edge where one changes.

            // How far ahead the ray of one of a half-sized slot's pixels stopped (see volume.glsl: the nearest or the farthest of the
            // four things behind it, turn and turn about).
            float stopped(int2 cell, int2 most)
            {
                float4 four = float4(TexelFetch(_CameraDepthTexture, min(2 * cell, most), 0).x, TexelFetch(_CameraDepthTexture, min(2 * cell + int2(1, 0), most), 0).x, TexelFetch(_CameraDepthTexture, min(2 * cell + int2(0, 1), most), 0).x, TexelFetch(_CameraDepthTexture, min(2 * cell + int2(1, 1), most), 0).x);
                four = 1.0 / (_ZBufferParams.z * four + _ZBufferParams.w);
                return ((cell.x + cell.y) & 1) == 0 ? min(min(four.x, four.y), min(four.z, four.w)) : max(max(four.x, four.y), max(four.z, four.w));
            }

            // (testing: a colour for each patch)
            float3 hue(int n)
            {
                float h = frac(float(n) * 0.618034 + 0.1) * 6.0;
                return clamp(float3(abs(h - 3.0) - 1.0, 2.0 - abs(h - 2.0), 2.0 - abs(h - 4.0)), 0.0, 1.0);
            }

            // How thick (in the sense of how much it hides) smoke that hides so much is, over how much it hides: 1 for the faintest.
            float thickness(float hidden)
            {
                float a = min(hidden, 0.999);
                return a > 1e-4 ? -log(1.0 - a) / a : 1.0;
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                int2 pixel = ((int2)(FragCoord(IN.pos).xy));
                int inAtlas = int(_VolLayers.x + 0.5), perRow = max(int(_VolLayers.z + 0.5), 1);
                bool small = _VolLayers.w > 0.5;
                int2 slot = ((int2)(_VolSlots.xy + 0.5)), last = slot - 1;
                float scale = max(_VolCamera.w, 1e-6);
                int test = int(_VolSlots.z + 0.5);

                float4 colours[16];
                float depths[16], spreads[16];
                int kinds[16];
                int n = 0;

                // The four pixels of a half-sized slot round this one, and how much each counts for by nearness alone. (A slot the
                // screen's size: this pixel's own.)
                float2 at = small ? (((float2)(pixel)) + 0.5) * 0.5 - 0.5 : ((float2)(pixel));
                int2 base = ((int2)(floor(at)));
                float2 f = small ? at - ((float2)(base)) : ((float2)(0.0));
                int2 c00 = clamp(base, ((int2)(0)), last), c10 = clamp(base + int2(1, 0), ((int2)(0)), last), c01 = clamp(base + int2(0, 1), ((int2)(0)), last), c11 = clamp(base + int2(1, 1), ((int2)(0)), last);
                float4 share = float4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
                bool weighed = !small || _VolCamera.x < 0.5;
                [loop] for (int i = 0; i < 15; i++)
                {
                    if (i >= inAtlas) break;
                    int2 origin = int2(i - (i / perRow) * perRow, i / perRow) * slot;
                    float4 s00 = TexelFetch(_VolAtlas, origin + c00, 0), s10 = TexelFetch(_VolAtlas, origin + c10, 0), s01 = TexelFetch(_VolAtlas, origin + c01, 0), s11 = TexelFetch(_VolAtlas, origin + c11, 0);
                    // (most of the screen has none of this patch's smoke on it, and is passed over at once)
                    if (AllZero(s00) && AllZero(s10) && AllZero(s01) && AllZero(s11)) continue;
                    if (!weighed)
                    {
                        // Enlarged plainly, a half-sized picture would smear over the outline of anything standing in the smoke: the
                        // slot's pixels beside a rocket stopped at the rocket or went on past it. So those that stopped where this
                        // pixel's own surface is count for most (allowing for a surface that slopes away from the camera: see
                        // enlarge.glsl, which did this for one patch at a time). The same for every patch: worked out once.
                        weighed = true;
                        int2 most = TextureSize(_CameraDepthTexture, 0) - 1;
                        float here = 1.0 / (_ZBufferParams.z * TexelFetch(_CameraDepthTexture, min(pixel, most), 0).x + _ZBufferParams.w);
                        float4 apart = float4(stopped(c00, most), stopped(c10, most), stopped(c01, most), stopped(c11, most)) - here;
                        float4 beside = float4(TexelFetch(_CameraDepthTexture, clamp(pixel - int2(1, 0), ((int2)(0)), most), 0).x, TexelFetch(_CameraDepthTexture, clamp(pixel + int2(1, 0), ((int2)(0)), most), 0).x, TexelFetch(_CameraDepthTexture, clamp(pixel - int2(0, 1), ((int2)(0)), most), 0).x, TexelFetch(_CameraDepthTexture, clamp(pixel + int2(0, 1), ((int2)(0)), most), 0).x);
                        beside = abs(1.0 / (_ZBufferParams.z * beside + _ZBufferParams.w) - here);
                        float slopes = min(beside.x, beside.y) + min(beside.z, beside.w);
                        float slack = 0.01 * here + 0.01 + 3.0 * slopes;
                        share /= apart * apart + slack * slack;
                        share /= share.x + share.y + share.z + share.w;
                    }
                    float4 smoke = s00 * share.x + s10 * share.y + s01 * share.z + s11 * share.w;
                    if (smoke.a <= 0.0005 && smoke.r + smoke.g + smoke.b <= 0.0005) continue;
                    float2 deep = TexelFetch(_VolAtlasDepth, origin + c00, 0).rg * share.x + TexelFetch(_VolAtlasDepth, origin + c10, 0).rg * share.y
                              + TexelFetch(_VolAtlasDepth, origin + c01, 0).rg * share.z + TexelFetch(_VolAtlasDepth, origin + c11, 0).rg * share.w;
                    colours[n] = smoke;
                    depths[n] = deep.x / (max(smoke.a, 1e-4) * scale);
                    spreads[n] = deep.y / (max(smoke.a, 1e-4) * scale);
                    kinds[n] = i;
                    n++;
                }
                if (_VolLayers.y > 0.5)
                {
                    float4 smoke = TexelFetch(_VolFull, pixel, 0);
                    if (smoke.a > 0.0005 || smoke.r + smoke.g + smoke.b > 0.0005)
                    {
                        float2 deep = TexelFetch(_VolFullDepth, pixel, 0).rg;
                        colours[n] = smoke;
                        depths[n] = deep.x / (max(smoke.a, 1e-4) * scale);
                        spreads[n] = deep.y / (max(smoke.a, 1e-4) * scale);
                        kinds[n] = 15;
                        n++;
                    }
                }
                if (n == 0) discard;
                if (test == 2 || test == 3)
                    [loop] for (int i = 0; i < n; i++) colours[i].rgb = colours[i].a * (kinds[i] == 15 ? ((float3)(1.0)) : hue(kinds[i]));

                // Nearest first (for the test views: the picture itself needs no order). (Insertion: there are seldom more than two or three here.)
                if (test != 1 && test != 3)
                    [loop] for (int i = 1; i < n; i++)
                    {
                        float4 c = colours[i];
                        float d = depths[i], w = spreads[i];
                        int k = kinds[i];
                        int j = i - 1;
                        while (j >= 0 && depths[j] > d)
                        {
                            colours[j + 1] = colours[j]; depths[j + 1] = depths[j]; spreads[j + 1] = spreads[j]; kinds[j + 1] = kinds[j];
                            j--;
                        }
                        colours[j + 1] = c; depths[j + 1] = d; spreads[j + 1] = w; kinds[j + 1] = k;
                    }
                else
                    // (testing: the order they were drawn in, which is the farthest box first, as the game sorts them, and the ships'
                    // patches last of all; reversed here, to be laid nearest first like the rest)
                    [loop] for (int i = 0; i < n / 2; i++)
                    {
                        float4 c = colours[i]; float d = depths[i], w = spreads[i]; int k = kinds[i];
                        colours[i] = colours[n - 1 - i]; depths[i] = depths[n - 1 - i]; spreads[i] = spreads[n - 1 - i]; kinds[i] = kinds[n - 1 - i];
                        colours[n - 1 - i] = c; depths[n - 1 - i] = d; spreads[n - 1 - i] = w; kinds[n - 1 - i] = k;
                    }

                // (testing: how far behind the nearest patch's smoke the next lies, red, 0 to 10 metres; how widely the nearest is spread along
                // the ray, green, and the next, blue, 0 to 5 metres; how much the nearest hides, as the picture's own alpha)
                if (test == 4)
                {
                    result = n > 1 ? float4(clamp((depths[1] - depths[0]) * 0.1, 0.0, 1.0), clamp(spreads[0] * 0.2, 0.0, 1.0), clamp(spreads[1] * 0.2, 0.0, 1.0), 1.0)
                                       : float4(0.0, clamp(spreads[0] * 0.2, 0.0, 1.0), 0.0, 1.0);
                    return result;
                }

                if (test == 1 || test == 3)
                {
                    // (testing: each laid over the next in the order they were drawn, as the game used to)
                    float4 laid = colours[n - 1];
                    [loop] for (int i = n - 2; i >= 0; i--) laid = colours[i] + laid * (1.0 - colours[i].a);
                    result = float4(max(laid.rgb, ((float3)(0.0))), clamp(laid.a, 0.0, 1.0));
                    return result;
                }
                if (test == 5)
                {
                    // (testing: as it was until 2026-10-10: from the farthest to the nearest, each over what is behind it, or mixed with it as
                    // two smokes in one stretch of air where their depths are closer than their spreads together)
                    float4 result_ = colours[n - 1];
                    float behind = depths[n - 1], behindSpread = spreads[n - 1];
                    [loop] for (int i = n - 2; i >= 0; i--)
                    {
                        float4 c = colours[i];
                        float d = depths[i];
                        float4 over = c + result_ * (1.0 - c.a);
                        float soft = max(spreads[i] + behindSpread, 0.6 + 0.006 * d);
                        float apart = smoothstep(0.0, soft, behind - d);
                        if (apart < 1.0)
                        {
                            float ta = thickness(c.a) * c.a, tb = thickness(result_.a) * result_.a, both = ta + tb;
                            float3 mixed = both > 1e-4 ? (c.rgb * thickness(c.a) + result_.rgb * thickness(result_.a)) * (over.a / both) : c.rgb + result_.rgb;
                            over.rgb = lerp(mixed, over.rgb, apart);
                        }
                        result_ = over;
                        behind = d;
                        behindSpread = spreads[i];
                    }
                    result = float4(max(result_.rgb, ((float3)(0.0))), clamp(result_.a, 0.0, 1.0));
                    return result;
                }

                // How much of each patch's smoke shows through the others. As a walk goes along the ray, what it has hidden so far is just
                // how much its smoke hides of anything at that depth: so how far it has hidden at each depth, which the walk kept as its
                // smoke's depth on the whole and how widely that is spread (taken as a bell curve about the one, as wide as the other),
                // says how much of the smoke of any other patch at any depth it hides. The light of each patch is dimmed by every other
                // as much as the other hides in front of it: wholly by one that lies wholly in front, not at all by one wholly behind,
                // and by half as much as it hides where the two are mixed through the same stretch of air. (The patches are separate
                // smokes, so what they let through together is what each lets through, in any order: exact, but for taking each one's
                // smoke as a bell curve along the ray.) There is no edge anywhere where an order changes, and nothing to put in order:
                // pale smoke a little inside a thick black cloud is dimmed by as much of the cloud as lies in front of it. (Until
                // 2026-10-10 they were put in order by depth and laid one over another, mixed only where their depths were close: pale
                // smoke a little in front of the cloud's depth on the whole was laid over it, more or less whole, in spots.)
                float3 light = ((float3)(0.0));
                float through = 1.0;
                [loop] for (int i = 0; i < n; i++)
                {
                    through *= 1.0 - colours[i].a;
                    float seen = 1.0;
                    [loop] for (int j = 0; j < n; j++)
                    {
                        if (j == i) continue;
                        // (never sharper than a few tenths of a metre, more further off, where a pixel is wider and depths are kept less finely)
                        float least = 0.3 + 0.002 * (depths[i] + depths[j]);
                        float wide = sqrt(spreads[i] * spreads[i] + spreads[j] * spreads[j] + least * least);
                        float ahead = clamp(1.702 * (depths[i] - depths[j]) / wide, -30.0, 30.0);
                        seen *= 1.0 - colours[j].a / (1.0 + exp(-ahead));      // (the share of the other's smoke in front of this one's, a bell curve's sum)
                    }
                    light += colours[i].rgb * seen;
                }
                result = float4(max(light, ((float3)(0.0))), clamp(1.0 - through, 0.0, 1.0));
                return result;
            }
            ENDCG
        }
    }
}
