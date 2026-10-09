// VolumetricExplosions/Volume: made from tools/shaderpack/volume.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "VolumetricExplosions/Volume"
{
    Properties
    {
        _Volume ("Volume", 3D) = "" {}
        _Amount ("Amount", 3D) = "" {}
        _Detail ("Detail", 3D) = "" {}
        _Around ("Around", 3D) = "" {}
        _Flow ("Flow", 3D) = "" {}
        _RestA ("RestA", 3D) = "" {}
        _RestB ("RestB", 3D) = "" {}
        _RestC ("RestC", 3D) = "" {}
        _RestD ("RestD", 3D) = "" {}
        _RestE ("RestE", 3D) = "" {}
        _Turns ("Turns", 3D) = "" {}
        _Clear ("Clear", 3D) = "" {}
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

            float4 _VolParams;     // x: how much a metre of the thickest smoke blocks, y: how many steps may be spent on smoke, z: how deep the billows are cut into the edge of the smoke (0 to 1), w: how much a metre of the thickest flame blocks
            float4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene, y: the angle one of its pixels covers (both set for each camera)
            float4 _VolStep;     // x: length of the finest step in metres
            float4 _VolGrid;     // x: fine steps to a step through clear air (a power of two, about a cell of the grid), y: how many of the screen's pixels wide a step may look, z: cells of the detail to a metre, times how many of the screen's pixels the smallest lump drawn should cover, w: (for testing) 1: the smoke on the grid alone, 2: with its puffs but unlit, 3: lit but without puffs
            float4 _VolSize;     // xyz: size of the box in metres
            float4 _VolOffset;     // xyz: the middle of the box, in metres from the site's origin
            float4 _VolDetail;     // xyz: how far the billows' pattern had been carried along with the smoke as a whole when the grid in use was made, in metres, w: repeats of the pattern per metre
            float4 _VolFlow;     // x: twice how far the fastest smoke on the grid has gone since the grid was made, in metres, yw: what turns a number read from the textures of where the smoke "was" into metres (times y, plus w), z: the farthest the smoke is carried on from where the grid has it, in metres
            float4 _VolPeak;     // x: 1 if this is drawn at half size, to be enlarged, z: (for testing) steps moved along by this part of a step, w: metres of the thickest smoke that count as thick
            float4 _VolThin;     // x: how much of what is behind it a ray's smoke may hide, at the very most, before any of it is drawn (nought: all of it is drawn), y: how long ago the grid in use was made, in seconds
            float4 _VolSun;     // rgb: sunlight, w: how much thin smoke in front of the sun shines
            float4 _VolSunDir;     // xyz: towards the sun
            float4 _VolSunLocal;     // xyz: the same, in the site's own axes (east, up, north), w: how strongly the billows shade each other
            float4 _VolAmb;     // rgb: light from the sky
            float4 _VolGlow;     // rgb: light from the fire
            float4 _VolLampA;     // xyz: where the strongest fire is, in metres from the site's origin, w: its size squared;
            float4 _VolLampB;     // the second
            float4 _VolLampC;     // and the third
            float4 _VolLamps;     // xyz: how bright each of the three is
            float4 _VolTint;     // rgb: the hue of the dust here
            float4 _VolHot1;     // rgb: the colour of flame at a quarter of full heat,
            float4 _VolHot2;     // at half,
            float4 _VolHot3;     // at three quarters,
            float4 _VolHot4;     // and at full heat
            Texture3D _Volume; SamplerState sampler_Volume;     // rg: how much smoke lies towards the sun (sixteen bits in two bytes), b: how much towards the open sky, a: how hot the flame is
            Texture3D _Amount; SamplerState sampler_Amount;     // rg: how much smoke (sixteen bits in two bytes), ba: how much flame
            Texture3D _Around; SamplerState sampler_Around;     // r: how much smoke there is round about, over a couple of metres (square root), g: more than nothing if there is any smoke or flame near, b: how light that smoke is (square root), a: how much of it is dust
            Texture3D _Flow; SamplerState sampler_Flow;     // rgb: which way the smoke is moving and how fast, as a share of the fastest (0.5: not at all), a: how much the pattern of billows is squeezed there (1: to a sixteenth)
            Texture3D _RestA; SamplerState sampler_RestA;     // how far the smoke at a place is from where it "was", by each of six reckonings (three for the big billows, then three for the small), east, up and north:
            Texture3D _RestB; SamplerState sampler_RestB;     // eighteen numbers of sixteen bits, four to a texture, one after another
            Texture3D _RestC; SamplerState sampler_RestC;
            Texture3D _RestD; SamplerState sampler_RestD;
            Texture3D _RestE; SamplerState sampler_RestE;     // (its third number is something else: how fast the smoke at a place is going through its round of turns, in rounds a second)
            Texture3D _Turns; SamplerState sampler_Turns;     // whose turn it is at a place: rg the point of its round the smoke there is at, as a point on a circle (0.5: the middle of it), for the three reckonings the big billows go by; ba the same for the small billows' three
            Texture3D _Clear; SamplerState sampler_Clear;     // r: how many cells of the coarser grid it is from this one to the nearest with any smoke or flame in it, over 255 (nought: there is some in this one). Read cell by cell, not between cells.
            Texture3D _Detail; SamplerState sampler_Detail;     // r: how much the smoke is gathered (lumps upon lumps), gba: the slope of that, each way
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
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                float4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
                float4 clip_ = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
                // The far faces of the box are what is drawn, and they must not be cut off by the far end of the camera's
                // range (the game's near camera stops at 400 m): the smoke nearer than that still has to be drawn there.
                // Its depth is not used for anything, so it is simply held inside the range.
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

            // Smoke and fire as a volume, the way Counter-Strike 2 draws its smoke grenades. The box this is drawn
            // on holds grids made by the mod from its particles: how much smoke and flame there is at each place,
            // how much light reaches it (from the sun, the open sky and the fire) and how hot the flame is, and (more
            // coarsely) how much smoke there is round about, what colour it is, which way it is moving and where it
            // has come from. For each pixel a ray is walked through the box from the camera, a step at a time. At each
            // step the edge of the smoke is cut into billows by cellular noise, which is what turns a smooth blob into
            // something like smoke; the smoke takes its colour from the light there and the flame from its heat; and
            // the walk ends at whatever solid thing the game has already drawn.
            //
            // The billows belong to the smoke. They are not a pattern standing in the air for the smoke to drift
            // through (it was once: every billow was then forever being cut afresh as the smoke moved on through the
            // pattern, and the whole cloud seethed). Each bit of smoke knows where it "was" a few seconds ago, and it is
            // that place the pattern is read at: so a billow rises with the smoke it is made of, is carried round by
            // the eddies that carry the smoke, and is drawn out as the smoke spreads. Left at that it would in the end
            // be drawn out of all recognition. So there are three such reckonings, begun afresh by turns, each counting
            // for nothing when it begins, for most half-way through its life and for nothing again at its end: what is
            // seen is always a blend of sets of billows none of them too old, and (the three being a third of a life
            // apart, and rising and falling as a bell curve does) one that changes at an even rate. How long a turn
            // lasts differs from place to place: under a second where the air is pulling the smoke about, so that no
            // set is kept until it is drawn out into streaks, and up to half a minute where the smoke is calm, where
            // the billows it has can be kept and it hardly changes at all. (The mod sets the pace, and tells the shader
            // whose turn it is at each place: see _Turns.) And there are two such threes, one for the big billows and
            // one, taking turns twice as fast, for the small: a small eddy is gone long before a big one.
            //
            // Most of the work is the walk itself, and most of that is reading the billows. Three things are left out
            // that cannot change the picture: clear air is crossed in leaps (see _Clear), a set of billows that counts
            // for nothing is not read, and the small billows are not read where the big ones have already cut the smoke
            // right away. (Checked against the walk without them, both in one program so that the same sums are rounded
            // the same way: over a dozen kinds of scene not one pixel differed.)
            //
            // And one thing that can, by less than can be seen: smoke too thin to see is passed over (see _VolThin).
            // A cloud is wrapped in haze thinner than anything that shows, and a ray used to be walked through all of
            // it in fine steps, billows and all, to add up to nothing. Now each ray keeps count of the most the smoke
            // it has met so far could have hidden, and until that comes to a trace that could be seen (two steps of an
            // eight-bit picture, as installed) the smoke is passed over at the pace of clear air. From there on
            // everything counts as before. So a ray that only grazes the haze costs next to nothing, a ray into the
            // body of a cloud loses at most the first two 255ths of what it would have hidden (in practice far less:
            // the count is of the most the smoke could hide, were the billows to gather all of it), and no ray loses
            // more. It is a small saving, not a large one: counted in a four-tonne fire, a twentieth of the walk's
            // steps. Most of the walk is through smoke that shows.
            //
            // The picture must not depend on where exactly the steps fall, or it shimmers whenever they move: when
            // the camera moves, or when the cloud outgrows its grid. So the steps come at fixed distances from the
            // camera that have nothing to do with the grid, and the noise is only ever read as coarsely as the steps
            // are long: detail finer than a step cannot be drawn, only guessed at differently each time.

            // The billows at one place. The pattern is lumps upon lumps (cellular noise), read twice: once as it is,
            // for the small billows, and once much larger and turned another way, for the big. No lump of the one lines up
            // with a lump of the other, so although each repeats every few metres, the two together never show the same
            // shape twice. How finely they are read depends on how far away the place is: the smallest lumps drawn are
            // a pixel or two across. (Were it to follow the length of the steps, which changes in jumps, parts of a
            // cloud would go blurred and sharp again as they moved; it does so only deep along a ray, where the steps
            // have had to be lengthened and nothing finer than them can be read truly.) From so far off that even the biggest lumps are
            // a pixel or two across they are left out: read that coarsely, the pattern is no longer lumps but the lattice
            // it repeats on. Each adds how much more (or less) than usual the smoke is gathered there, and which way that slopes,
            // times what its set counts for just now (seen: see the walk below, which leaves a set that counts for nothing unread).
            // (The bigger lumps count for twice the smaller: a lump no deeper than a small one would not show as big.)
            void bigBillows(float3 q, float level, float seen, inout float lump, inout float3 slope)
            {
                float3 turned = float3(dot(q, float3(0.80, -0.48, 0.36)), dot(q, float3(0.60, 0.64, -0.48)), dot(q, float3(0.0, 0.60, 0.80))) * 0.37 + float3(0.31, 0.67, 0.13);
                float4 broad = SampleLod(_Detail, turned, max(level - 1.43, 0.0));
                lump += 0.9 * seen * (broad.r - 0.45);
                float3 way = (broad.gba - 0.5) * (2.0 * 10.72 * 0.37 * 1.4 * seen);
                slope += float3(dot(way, float3(0.80, 0.60, 0.0)), dot(way, float3(-0.48, 0.64, 0.60)), dot(way, float3(0.36, -0.48, 0.80)));
            }

            void smallBillows(float3 q, float level, float seen, inout float lump, inout float3 slope)
            {
                float4 fine_ = SampleLod(_Detail, q, max(level, 0.0));
                lump += 0.45 * seen * (fine_.r - 0.45);
                slope += (fine_.gba - 0.5) * (2.0 * 10.72 * 0.7 * seen);
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float3 eye = _WorldSpaceCameraPos;
                float3 rd = normalize(IN.vs_TEXCOORD0 - eye);
                // The same ray in the box's own coordinates (-0.5 to 0.5 each way). The distance along it stays in metres.
                float3 ro = hlslcc_mtx4x4unity_WorldToObject[0].xyz * eye.x + hlslcc_mtx4x4unity_WorldToObject[1].xyz * eye.y + hlslcc_mtx4x4unity_WorldToObject[2].xyz * eye.z + hlslcc_mtx4x4unity_WorldToObject[3].xyz;
                float3 rdo = hlslcc_mtx4x4unity_WorldToObject[0].xyz * rd.x + hlslcc_mtx4x4unity_WorldToObject[1].xyz * rd.y + hlslcc_mtx4x4unity_WorldToObject[2].xyz * rd.z;
                float3 size = max(abs(rdo), ((float3)(1e-6)));
                float3 inv = float3(rdo.x < 0.0 ? -1.0 : 1.0, rdo.y < 0.0 ? -1.0 : 1.0, rdo.z < 0.0 ? -1.0 : 1.0) / size;
                float3 ta = (((float3)(-0.5)) - ro) * inv, tb = (((float3)(0.5)) - ro) * inv;
                float3 lo = min(ta, tb), hi = max(ta, tb);
                float t0 = max(max(lo.x, lo.y), lo.z), t1 = min(min(hi.x, hi.y), hi.z);

                // Only the stretch of the ray that this camera draws (the game uses one camera for near things and one for far).
                float forward = -(hlslcc_mtx4x4unity_MatrixV[0].z * rd.x + hlslcc_mtx4x4unity_MatrixV[1].z * rd.y + hlslcc_mtx4x4unity_MatrixV[2].z * rd.z);
                forward = max(forward, 1e-4);
                t0 = max(t0, _ProjectionParams.y / forward);
                t1 = min(t1, _ProjectionParams.z / forward);
                if (_VolCamera.x > 0.5)
                {
                    float ahead;
                    if (_VolPeak.x > 0.5)
                    {
                        // Drawn at half size, to be enlarged afterwards (see enlarge.glsl): this pixel stands for four of the
                        // screen's. It stops at the nearest of the four things behind them or at the farthest, turn and turn
                        // about like the squares of a chessboard: so whatever a pixel of the screen shows, near thing or far,
                        // one of the pixels round it here stopped at the same.
                        int2 cell = ((int2)(IN.pos.xy)), most = TextureSize(_CameraDepthTexture, 0) - 1;
                        float4 four = float4(DepthFetch(min(2 * cell, most), 0).x, DepthFetch(min(2 * cell + int2(1, 0), most), 0).x, DepthFetch(min(2 * cell + int2(0, 1), most), 0).x, DepthFetch(min(2 * cell + int2(1, 1), most), 0).x);
                        four = 1.0 / (_ZBufferParams.z * four + _ZBufferParams.w);
                        ahead = ((cell.x + cell.y) & 1) == 0 ? min(min(four.x, four.y), min(four.z, four.w)) : max(max(four.x, four.y), max(four.z, four.w));
                    }
                    else
                    {
                        float2 uv = float2(IN.vs_TEXCOORD1.x / IN.vs_TEXCOORD1.w, IN.vs_TEXCOORD1.y / IN.vs_TEXCOORD1.w * _ProjectionParams.x) * 0.5 + 0.5;
                        ahead = 1.0 / (_ZBufferParams.z * SampleFlat(_CameraDepthTexture, uv).x + _ZBufferParams.w);
                    }
                    t1 = min(t1, ahead / forward);
                }
                if (t1 <= t0) discard;

                // Steps are counted in fine steps from the camera. In smoke they are as long as a pixel or two is wide
                // out there, a power of two fine steps, but never longer than half a cell of the grid; through clear air
                // near smoke, about a cell; where there is nothing near at all, twice that. Every pixel starts its own
                // fixed part of a step in.
                // (Half a cell: from far off a pixel is metres wide, and steps that long, each pixel starting somewhere
                // else along its first, made a distant cloud a speckle of light and dark pixels with a ragged outline.
                // A cloud that far off covers few pixels, so walking them finely costs next to nothing.)
                float finest = _VolStep.x;
                float3 perSize = 1.0 / _VolSize.xyz;
                float perMetre = _VolCamera.y * _VolGrid.y / finest;
                // (never shorter than two fine steps, a cell of the billows' pattern: from close to, shorter ones only used up the steps there are)
                float fineMost = max(0.5 * _VolGrid.x, 2.0);
                // (fine steps to a cell of the grid, along whichever way across the grid this ray goes fastest: see the leaps below)
                float perCell = 1.0 / (max(max(size.x, size.y), size.z) * 64.0 * finest);
                float fineHere = clamp(exp2(floor(log2(max(t0 * perMetre, 1.0)) + 0.25)), 2.0, fineMost);
                float coarse = max(_VolGrid.x, fineHere);
                float grain = frac(52.9829189 * frac(dot(IN.pos.xy, float2(0.06711056, 0.00583715))) + _VolPeak.z);
                float shift = grain * 8.0;
                float kFirst = t0 / finest - shift, kLast = t1 / finest - shift;
                float k = ceil(kFirst / coarse) * coarse;
                float stride = coarse;
                bool fine = false, ended = false;
                float left = _VolParams.y, clear = 0.0, last = -1.0e9, longer = 1.0, coarser = 0.0;
                float passed = 0.0;       // the most that the smoke passed over so far could have hidden (see _VolThin)

                // Thin smoke between the camera and the sun shines.
                float toward = max(dot(rd, _VolSunDir.xyz), 0.0);
                float shine = 1.0 + _VolSun.w * toward * toward * toward * toward;
                float3 colour = ((float3)(0.0));
                float through = 1.0;
                [loop] for (int i = 0; i < 288; i++)
                {
                    if (through < 0.02) break;
                    if (k >= kLast)
                    {
                        // At the end of the ray, one more look right in front of whatever stopped it: smoke lying thin against a surface is not to be stepped over.
                        if (fine || ended) break;
                        ended = true;
                        k = floor((kLast - 0.01) / fineHere) * fineHere;
                        if (k < kFirst || k <= last) break;
                    }
                    last = k;
                    float t = (k + shift) * finest;
                    float3 p = ro + rdo * t;
                    if (!fine)
                    {
                        // Clear air is crossed in leaps. The mod has worked out, for every cell of the coarser grid, how many
                        // cells it is from there to the nearest that has any smoke or flame in it at all; so from a place in a
                        // cell that is, say, six cells from any, the ray can go on for the best part of five cells and find
                        // nothing, whichever way it is going, and need not look. (Less a cell and three quarters of the coarser
                        // grid: one for where in its own cell the place is, a quarter for the finer grid's being read between
                        // its cells, and the rest for the smoke's being carried on from where the grid has it, as below.)
                        // A leap ends on a step that would have been taken anyway (in clear air every second coarse step is,
                        // whatever else): so what is found after it, and so the picture, is the same as if every step up to
                        // there had been taken.
                        float cells = TexelFetch(_Clear, clamp(((int3)((p + 0.5) * 32.0)), ((int3)(0)), ((int3)(31))), 0).r * 255.0;
                        if (cells > 2.5)
                        {
                            float to = floor((k + (2.0 * cells - 3.5) * perCell) / (2.0 * coarse)) * 2.0 * coarse;
                            if (to > k) { k = to; continue; }
                        }
                    }
                    // The grid is a moment old (it takes a few frames to make), and the smoke has moved on since. So the grid
                    // is read not at this place but where the smoke now here was when the grid was made, going by how fast
                    // the smoke about here was moving then: between one grid and the next the whole cloud is carried
                    // smoothly on, instead of standing still and then jumping.
                    float4 going = SampleLod(_Flow, p + 0.5, 0.0);
                    float3 gone = (going.xyz - 0.5) * _VolFlow.x;
                    gone *= min(1.0, _VolFlow.z / max(length(gone), 1e-4));
                    float3 here = p * _VolSize.xyz + _VolOffset.xyz;
                    float3 was = here - gone - _VolDetail.xyz;
                    float3 at = p + 0.5 - gone * perSize;
                    float4 tone = SampleLod(_Around, at, 0.0);
                    if (tone.g <= 0.0 && !fine)
                    {
                        k = (floor(k / (2.0 * coarse)) + 1.0) * 2.0 * coarse;
                        continue;
                    }
                    float4 amount = SampleLod(_Amount, at, 0.0);
                    float smoke = amount.r * 0.99611 + amount.g * 0.0038911, flame = amount.b * 0.99611 + amount.a * 0.0038911;
                    bool some = smoke + flame > 0.00001;
                    if (some && passed < _VolThin.x)
                    {
                        // Smoke that may be too thin to see. The most this step could hide is all its smoke gathered as thick as
                        // the billows ever gather it (1.6 times) over the whole step. While that and all before it come to less
                        // than could be seen, it is passed over as clear air is; the first step that takes it past, and every
                        // one after, counts in full. (Flame is never passed over: a spark shows however small.)
                        float most = smoke * _VolParams.x * 1.6 * stride * finest;
                        if (flame <= 0.0 && passed + most < _VolThin.x) { passed += most; some = false; }
                        else passed = _VolThin.x;
                    }
                    if (some && !fine)
                    {
                        float fineNow = clamp(exp2(floor(log2(max(t * perMetre, 1.0)) + 0.25)), fineHere, fineMost) * longer;
                        if (fineNow < stride)
                        {
                            // Smoke. Its edge is somewhere since the last step, which was clear: back to just after that one, and on in fine steps.
                            k = max(k - stride + fineNow, ceil(kFirst / fineNow) * fineNow);
                            stride = fineNow;
                            fine = true;
                            clear = 0.0;
                            continue;
                        }
                    }
                    if (some)
                    {
                        clear = 0.0;

                        // Billows: read where this smoke "was", by each reckoning that counts for anything just now (see the top),
                        // and blended. (Where the pattern is squeezed, it is read that much more coarsely.)
                        // And where the steps have had to be made longer than they should be (see the end of the loop: deep along
                        // a ray, behind most of what there is to see), more coarsely by as much. Read finely there, each pixel
                        // caught the small lumps or missed them by chance: smoke seen through much other smoke from close by was
                        // speckled, and a thin wall of it far in showed as a row of dots. The change comes in over a few steps.
                        coarser += (log2(longer) - coarser) * 0.5;
                        float level = (coarser > 0.01 ? max(log2(t * _VolCamera.y * _VolGrid.z), 0.0) + coarser : log2(t * _VolCamera.y * _VolGrid.z)) + going.a * 4.0;
                        float lump = 0.45;
                        float3 slope = ((float3)(0.0)), testFrom = ((float3)(0.0));
                        // Reading the billows is most of the work of the whole walk: six looks into the pattern, and five into the
                        // grids that say where to look. So what each set counts for here is worked out first (nothing from so far
                        // off that its lumps are too small to see, see above the functions; nothing for a set just begun afresh),
                        // and a set that counts for nothing is not read: it would have been multiplied by nought.
                        float bigNear = 1.0 - smoothstep(2.6, 4.0, level - 1.43), smallNear = 1.0 - smoothstep(2.6, 4.0, level);
                        float3 bigSeen = ((float3)(0.0)), smallSeen = ((float3)(0.0));
                        // Whose turn it is differs from place to place (see the top). So what each set counts for is read from the
                        // grid, like where the smoke "was": the mean over the smoke that is there. (Each three so scaled that
                        // together they are as deep as one set of billows whatever their shares: the three have nothing to do
                        // with one another, so their depths add as squares do.)
                        float4 r5 = ((float4)(0.0));
                        if (bigNear > 0.0)
                        {
                            // (As a point on a circle, for each three: the three reckonings count for as much as it is from three
                            // places on the rim, a third of the way round apart. Turned on by as far as the smoke has gone round
                            // since the grid was made, so that what they count for changes from frame to frame, not from grid to grid.)
                            float4 turn = SampleLod(_Turns, at, 0.0) * 2.0 - 1.0;
                            r5 = SampleLod(_RestE, at, 0.0) * _VolFlow.y + _VolFlow.w;
                            float on = 6.2831853 * r5.b * _VolThin.y, c = cos(on), s = sin(on);
                            float2 big2 = float2(turn.r * c - turn.g * s, turn.r * s + turn.g * c);
                            float c2 = c * c - s * s, s2 = 2.0 * s * c;
                            float2 small2 = float2(turn.b * c2 - turn.a * s2, turn.b * s2 + turn.a * c2);
                            float3 share = max(0.5 - 0.5 * (big2.x * float3(1.0, -0.5, -0.5) - big2.y * float3(0.0, 0.8660254, -0.8660254)), 0.0);
                            // (One that has come to the end of its turn since the grid was made, and begun afresh, counts for nothing
                            // until the next grid comes: the grid still has where the smoke "was" by it as it was before. It is the
                            // one that was in the second half of its turn then and is in the first half now. Where the smoke is not
                            // all at the same point of its round, that goes for the part of it that is at this one: the rest counts on.)
                            float3 was2 = turn.r * float3(0.0, 0.8660254, -0.8660254) + turn.g * float3(1.0, -0.5, -0.5), now2 = big2.x * float3(0.0, 0.8660254, -0.8660254) + big2.y * float3(1.0, -0.5, -0.5);
                            share = lerp(share, ((float3)(0.5 * max(1.0 - length(turn.rg), 0.0))), step(was2, ((float3)(-1e-4))) * step(((float3)(0.0)), now2));
                            bigSeen = bigNear * share * rsqrt(max(dot(share, share), 1e-6));
                            share = max(0.5 - 0.5 * (small2.x * float3(1.0, -0.5, -0.5) - small2.y * float3(0.0, 0.8660254, -0.8660254)), 0.0);
                            was2 = turn.b * float3(0.0, 0.8660254, -0.8660254) + turn.a * float3(1.0, -0.5, -0.5); now2 = small2.x * float3(0.0, 0.8660254, -0.8660254) + small2.y * float3(1.0, -0.5, -0.5);
                            share = lerp(share, ((float3)(0.5 * max(1.0 - length(turn.ba), 0.0))), step(was2, ((float3)(-1e-4))) * step(((float3)(0.0)), now2));
                            smallSeen = smallNear * share * rsqrt(max(dot(share, share), 1e-6));
                        }
                        bool3 big = GreaterThan(bigSeen, ((float3)(0.0))), small = GreaterThan(smallSeen, ((float3)(0.0)));
                        // (Each set is read at its own place in the pattern as well, so that three sets begun at the same spot are still three different sets.)
                        float4 r1 = ((float4)(0.0)), r2 = ((float4)(0.0)), r3 = ((float4)(0.0)), r4 = ((float4)(0.0));
                        if (big.x || big.y) r1 = SampleLod(_RestA, at, 0.0) * _VolFlow.y + _VolFlow.w;
                        if (big.y || big.z) r2 = SampleLod(_RestB, at, 0.0) * _VolFlow.y + _VolFlow.w;
                        if (big.z || small.x) r3 = SampleLod(_RestC, at, 0.0) * _VolFlow.y + _VolFlow.w;
                        testFrom = r1.rgb;
                        if (big.x) bigBillows((was + r1.rgb) * _VolDetail.w, level, bigSeen.x, lump, slope);
                        if (big.y) bigBillows((was + float3(r1.a, r2.rg)) * _VolDetail.w + float3(0.37, 0.11, 0.71), level, bigSeen.y, lump, slope);
                        if (big.z) bigBillows((was + float3(r2.ba, r3.r)) * _VolDetail.w + float3(0.71, 0.59, 0.23), level, bigSeen.z, lump, slope);
                        float lumpBig = lump;

                        // The body of the smoke is left whole: thick, and solid to look at. It is only towards its edge, where
                        // there is less smoke at a place than round about it, that the billows are cut in: deepest between the
                        // lumps of the pattern, hardly at all where a lump stands. So the smoke ends in a surface of rounded
                        // bulges with creases between them, as real smoke does, rather than fading away or being full of holes.
                        float fill = 1.0 - exp(-smoke * _VolParams.x * _VolPeak.w);
                        float about = 1.0 - exp(-tone.r * tone.r * _VolParams.x * _VolPeak.w);
                        // Where there is little smoke round about (the thin skirts of a cloud, and above all a puff that has
                        // strayed off on its own) the billows count for far more: between the lumps the smoke is cut right
                        // through, and on them it is gathered thicker, so that a lone puff is not a soft ball, which is not a
                        // shape smoke takes, but a ragged scrap of it. Only the billows themselves do this: where they
                        // cannot be seen (from far off, or where the pattern is in a tangle) the smoke is left as it is.
                        float usual = _VolParams.z * 0.53, thinly = 1.0 - about, edge = max(about, 0.6 * fill);
                        // (Well inside a thick cloud nothing is cut right through. A pocket of thinner smoke there stays thinner
                        // smoke: cut clean away, it was a hole with a hard edge, for thick smoke is either there or not.)
                        edge = lerp(edge, min(edge, 1.25 * fill), smoothstep(0.45, 0.8, about));
                        // (It is the big billows that do it: cut through by the small ones too, thin smoke was peppered with holes.)
                        float eatenBig = _VolParams.z * clamp(1.25 - 1.6 * lumpBig, 0.0, 1.0);
                        float cut = (eatenBig - usual) * 4.5 * about * thinly * thinly;

                        // In the skirts of a cloud the big billows have very often cut the smoke away altogether before the small
                        // ones are asked: there is nothing left for them to shape. Whether that is so can be told for certain
                        // without reading them: at their most (every one of them a full lump) they add so much to the lump and
                        // no more, which leaves at least so much eaten away; if even then nothing is left, and no flame either
                        // (none here, or the lump too slight for any to show), this step comes to nothing whatever they are.
                        // (Told with a little to spare, so that a rounding error cannot make it a different answer from the full sum.)
                        float lumpMost = lumpBig + 0.2475 * (smallSeen.x + smallSeen.y + smallSeen.z);
                        float eatenLeast = _VolParams.z * clamp(1.25 - 1.6 * lumpMost, 0.0, 1.0);
                        if (_VolGrid.w < 0.5 && fill - eatenLeast * edge - cut < -1e-5 && (flame <= 0.0 || lumpMost < 0.1799))
                        {
                            // (what follows is all that such a step does: see where the flame is worked out, below)
                            left -= 1.0;
                            k += stride;
                            if (fine && left <= 0.0 && longer < 7.5 && Mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
                            continue;
                        }
                        if (small.y || small.z) r4 = SampleLod(_RestD, at, 0.0) * _VolFlow.y + _VolFlow.w;
                        if (small.x) smallBillows((was + r3.gba) * _VolDetail.w, level, smallSeen.x, lump, slope);
                        if (small.y) smallBillows((was + r4.rgb) * _VolDetail.w + float3(0.53, 0.29, 0.17), level, smallSeen.y, lump, slope);
                        if (small.z) smallBillows((was + float3(r4.a, r5.rg)) * _VolDetail.w + float3(0.19, 0.83, 0.61), level, smallSeen.z, lump, slope);

                        float eaten = _VolParams.z * clamp(1.25 - 1.6 * lump, 0.0, 1.0);
                        float kept = max(fill - eaten * edge - cut, 0.0) / (1.0 - 0.6 * eaten);
                        // (Just inside where it is cut away the smoke comes on gently: thick smoke that began at once at its full thickness had an edge like card.)
                        float whole = fill > 1e-4 ? min(kept / fill, 1.6) : 0.0;
                        float sigma = smoke * _VolParams.x * whole * min(1.25 * whole, 1.0);
                        if (_VolGrid.w > 0.5 && (_VolGrid.w < 1.5 || _VolGrid.w > 2.5)) sigma = smoke * _VolParams.x;      // (testing: the smoke as it is on the grid, not cut into)
                        float thin = exp(-sigma * _VolPeak.w);

                        // Flame: licked into tongues by the same pattern, flickering, and coloured by how hot it is.
                        float burning = flame * _VolParams.w * clamp(2.5 * lump - 0.45, 0.0, 1.8);
                        // (Flame is a glow, and can be stepped through twice as fast as smoke. A step where the billows have cut
                        // everything away counts in full, though there was nothing to light: there are only so many steps to a
                        // ray, and counted for less, as they once were, a ray that began in the thin skirts of a cloud spent them
                        // all there and never reached the body of it: from close by, windows clean through the smoke with hard edges.)
                        left -= burning > sigma ? 2.0 : 1.0;
                        if (_VolGrid.w < 0.5 && sigma + burning <= 0.0)
                        {
                            k += stride;
                            if (fine && left <= 0.0 && longer < 7.5 && Mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
                            continue;
                        }
                        float4 air = SampleLod(_Volume, at, 0.0);
                        float heat = air.a * 1.2 * (0.72 + 0.56 * lump);
                        float3 hot = heat < 0.25 ? _VolHot1.rgb * (heat * 4.0) : heat < 0.5 ? lerp(_VolHot1.rgb, _VolHot2.rgb, heat * 4.0 - 1.0) : heat < 0.75 ? lerp(_VolHot2.rgb, _VolHot3.rgb, heat * 4.0 - 2.0) : lerp(_VolHot3.rgb, _VolHot4.rgb, min(heat * 4.0 - 3.0, 1.0));

                        // Smoke: its own colour, in the light that reaches it. Thinned out, dark smoke looks paler. And each puff
                        // has a lit side and a shaded one: where the smoke gets thicker towards the sun, this place is darker.
                        float3 own = tone.b * tone.b * lerp(((float3)(1.0)), _VolTint.rgb, tone.a);
                        own += float3(0.05, 0.048, 0.045) * thin * step(own.r, 0.3);
                        float relief = clamp(1.0 - _VolSunLocal.w * 0.055 * dot(slope, _VolSunLocal.xyz), 0.45, 1.6);
                        // The sun's light, after the smoke that lies towards the sun (a part of it gets much further than the rest: light
                        // finds its way round inside a cloud. Much of it in pale smoke, which throws nearly all the light it meets on to
                        // the next bit; little in soot, which swallows it), the light of the open sky, and that of the fires near by.
                        float before = (air.r * 0.99611 + air.g * 0.0038911) * 16.0;
                        float strays = 0.12 + 0.5 * min(tone.b * tone.b * 1.4, 1.0);
                        // Down in the clefts between billows less light of any kind gets in.
                        float cleft = eaten * about;
                        float sun = ((1.0 - strays) * exp(-before) + strays * exp(-0.25 * before)) * (1.0 - 0.3 * cleft);
                        float sky = exp(-air.b * 8.0) * (1.0 - 0.55 * cleft);
                        float3 toA = here - _VolLampA.xyz, toB = here - _VolLampB.xyz, toC = here - _VolLampC.xyz;
                        float firelight = min(_VolLamps.x * _VolLampA.w / (dot(toA, toA) + _VolLampA.w + 1e-4) + _VolLamps.y * _VolLampB.w / (dot(toB, toB) + _VolLampB.w + 1e-4) + _VolLamps.z * _VolLampC.w / (dot(toC, toC) + _VolLampC.w + 1e-4), 1.0);
                        // Beside a fire the eye is taken up by the fire: the daylight on the smoke there counts for little, and it shows by the firelight.
                        float dazzle = 1.0 - 0.85 * min(firelight * 1.6, 1.0);
                        float3 light = (_VolSun.rgb * (sun * shine * relief) + _VolAmb.rgb * (0.3 + 0.7 * sky)) * dazzle + _VolGlow.rgb * (firelight * 4.0);
                        float3 lit_ = sqrt(own * light);
                        if (_VolGrid.w > 0.5 && _VolGrid.w < 2.5) { lit_ = ((float3)(0.45)); burning = 0.0; }                      // (testing: no light and no flame)
                        if (_VolGrid.w > 14.5) burning = 0.0;                      // (testing: 15 everything but the flame,
                        else if (_VolGrid.w > 13.5) lit_ = ((float3)(0.0));                // 14 the flame alone, on black smoke)
                        else if (_VolGrid.w > 9.5) { burning = 0.0; lit_ = _VolGrid.w < 10.5 ? ((float3)(sun * shine * relief)) : _VolGrid.w < 11.5 ? ((float3)(sky)) : _VolGrid.w < 12.5 ? own * 3.0 : ((float3)(firelight)); }      // (testing: 10 the sun's light alone, 11 the sky's, 12 the smoke's own colour, 13 the firelight)
                        else if (_VolGrid.w > 4.5) { burning = 0.0; lit_ = _VolGrid.w < 5.5 ? ((float3)(lump)) : _VolGrid.w < 6.5 ? frac((was + testFrom) * _VolDetail.w) : _VolGrid.w < 7.5 ? abs(testFrom) / 30.0 : _VolGrid.w < 8.5 ? abs(gone) * 2.0 : float3(going.a, 1.0 - going.a, 0.0); }      // (testing: 5 the billows, 6 where in their pattern, 7 how far from where it "was", 8 how far carried on, 9 how squeezed the pattern is: green not at all, red sixteen times)

                        float both = sigma + burning;
                        float a = 1.0 - exp(-both * min(stride * finest, t1 - t));        // the last step stops at whatever solid thing is behind
                        colour += through * a * (lit_ * sigma + hot * (burning * 1.8)) / max(both, 1e-5);
                        through *= 1.0 - a;
                    }
                    else if (fine)
                    {
                        clear += stride;
                    }
                    k += stride;
                    if (fine)
                    {
                        // Once most of what can be seen along this ray has been passed the steps are four times as long. And each time
                        // the steps allowed for are used up they are doubled, and half as many allowed again: so a ray is walked finely
                        // for as long as it can be, and more and more coarsely after. (All at once to four times as long, as it once
                        // was, thick smoke seen through thin from close by was speckled: from there the steps are short and soon used up.)
                        if (through < 0.3 && longer < 3.5) { if (Mod(k, stride * 4.0 / longer) < 0.5) { stride *= 4.0 / longer; longer = 4.0; } }
                        else if (left <= 0.0 && longer < 7.5 && Mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
                        // And out of the smoke, as long as the grid's cells again.
                        if (clear >= coarse && Mod(k, coarse) < 0.5) { fine = false; stride = coarse; }
                    }
                }
                // The walk stops when no more than a fiftieth of what is behind the smoke would still get through. That fiftieth
                // is not let through: smoke thick enough to have stopped the walk hides everything, and all smoke hides a
                // fiftieth more than the walk made it, so that there is no step where the one becomes the other. (Let through,
                // it was enough for a bright sky, or the line of the horizon, to show faintly through the thickest smoke.)
                float hides = 1.0 - through, all_ = min(hides / 0.98, 1.0);
                result = float4(colour * (all_ / max(hides, 1e-6)), all_);
                return result;
            }
            ENDCG
        }
    }
}
