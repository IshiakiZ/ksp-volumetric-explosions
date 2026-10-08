// The smoke of Volumetric Explosions as a volume: the same shader as tools/shaderpack/volume.glsl, for the
// Unity editor, so that it can be built for Direct3D (Windows), which needs compiled bytecode that cannot
// be made by hand. The walk along the ray is generated from the GLSL by tools/unityshaders/port.py: edit
// the GLSL and run that again rather than editing here.
//
// NOT YET BUILT OR RUN: it was written without a Unity editor to hand.
Shader "VolumetricExplosions/Volume"
{
    Properties
    {
        _Volume ("Volume", 3D) = "" {}
        _Amount ("Amount", 3D) = "" {}
        _Detail ("Detail", 3D) = "" {}
        _Around ("Around", 3D) = "" {}
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
            #pragma target 3.5
            #include "UnityCG.cginc"

            float4 _VolParams, _VolCamera, _VolStep, _VolGrid, _VolSize, _VolOffset, _VolDetail, _VolPeak, _VolSun, _VolSunDir, _VolSunLocal, _VolAmb, _VolGlow;
            float4 _VolLampA, _VolLampB, _VolLampC, _VolLamps, _VolTint, _VolHot1, _VolHot2, _VolHot3, _VolHot4;
            sampler3D _Volume, _Amount, _Around, _Detail;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(vertex.xyz, 1.0));
                o.pos = mul(UNITY_MATRIX_VP, world);
                // The far faces of the box are what is drawn, and they must not be cut off by the far end of the camera's range.
                #if defined(UNITY_REVERSED_Z)
                o.pos.z = max(o.pos.z, o.pos.w * 0.00001);
                #else
                o.pos.z = min(o.pos.z, o.pos.w * 0.99999);
                #endif
                o.world = world.xyz;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag(v2f i, UNITY_VPOS_TYPE pixel : VPOS) : SV_Target
            {
                float3 eye = _WorldSpaceCameraPos;
                float3 rd = normalize(i.world - eye);
                // The same ray in the box's own coordinates (-0.5 to 0.5 each way). The distance along it stays in metres.
                float3 ro = mul(unity_WorldToObject, float4(eye, 1.0)).xyz;
                float3 rdo = mul((float3x3)unity_WorldToObject, rd);
                float3 size = max(abs(rdo), float3(1e-6, 1e-6, 1e-6));
                float3 inv = float3(rdo.x < 0.0 ? -1.0 : 1.0, rdo.y < 0.0 ? -1.0 : 1.0, rdo.z < 0.0 ? -1.0 : 1.0) / size;
                float3 ta = (float3(-0.5, -0.5, -0.5) - ro) * inv, tb = (float3(0.5, 0.5, 0.5) - ro) * inv;
                float3 lo = min(ta, tb), hi = max(ta, tb);
                float t0 = max(max(lo.x, lo.y), lo.z), t1 = min(min(hi.x, hi.y), hi.z);
                // Only the stretch of the ray that this camera draws.
                float forward = max(-mul((float3x3)UNITY_MATRIX_V, rd).z, 1e-4);
                t0 = max(t0, _ProjectionParams.y / forward);
                t1 = min(t1, _ProjectionParams.z / forward);
                if (_VolCamera.x > 0.5)
                {
                    float depth = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen));
                    t1 = min(t1, LinearEyeDepth(depth) / forward);
                }
                clip(t1 - t0);

                // Steps are counted in fine steps from the camera. In smoke they are as long as a pixel or two is wide
                // out there, a power of two fine steps; through clear air near smoke, about a cell of the grid; where
                // there is nothing near at all, twice that. Every pixel starts its own fixed part of a step in.
                float finest = _VolStep.x;
                float perMetre = _VolCamera.y * _VolGrid.y / finest;
                float fineHere = exp2(floor(log2(max(t0 * perMetre, 1.0)) + 0.25));
                float coarse = max(_VolGrid.x, fineHere);
                float grain = frac(52.9829189 * frac(dot(pixel.xy, float2(0.06711056, 0.00583715))) + _VolPeak.z);
                float shift = grain * 8.0;
                float kFirst = t0 / finest - shift, kLast = t1 / finest - shift;
                float k = ceil(kFirst / coarse) * coarse;
                float stride = coarse;
                bool fine = false, ended = false, deep = false;
                float left = _VolParams.y, clear = 0.0, last = -1.0e9;

                // Thin smoke between the camera and the sun shines.
                float toward = max(dot(rd, _VolSunDir.xyz), 0.0);
                float shine = 1.0 + _VolSun.w * toward * toward * toward * toward;
                float3 colour = float3(0.0, 0.0, 0.0);
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
                    float3 at = p + 0.5;
                    float4 tone = tex3Dlod(_Around, float4(at, 0.0));
                    if (tone.g <= 0.0 && !fine)
                    {
                        k = (floor(k / (2.0 * coarse)) + 1.0) * 2.0 * coarse;
                        continue;
                    }
                    float4 amount = tex3Dlod(_Amount, float4(at, 0.0));
                    float smoke = amount.r * 0.99611 + amount.g * 0.0038911, flame = amount.b * 0.99611 + amount.a * 0.0038911;
                    bool some = smoke + flame > 0.00001;
                    if (some && !fine)
                    {
                        float fineNow = max(fineHere, exp2(floor(log2(max(t * perMetre, 1.0)) + 0.25))) * (deep ? 4.0 : 1.0);
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
                        left -= 1.0;
                        float4 air = tex3Dlod(_Volume, float4(at, 0.0));
                        float3 here = p * _VolSize.xyz + _VolOffset.xyz;

                        // Billows. The pattern is lumps upon lumps (cellular noise), read twice: once as it is, and once much
                        // larger and turned another way. No lump of the one lines up with a lump of the other, so although each
                        // repeats every few metres, the two together never show the same shape twice. How finely they are read
                        // depends on nothing but how far away the place is: the smallest lumps drawn are a pixel or two across.
                        // (Were it to follow the length of the steps, which changes in jumps, parts of a cloud would go blurred
                        // and sharp again as they moved.) From so far off that even the biggest lumps are a pixel or two across
                        // they are left out: read that coarsely, the pattern is no longer lumps but the lattice it repeats on.
                        float level = log2(t * _VolCamera.y * _VolGrid.z);
                        float3 q = here * _VolDetail.w - _VolDetail.xyz;
                        float3 turned = float3(dot(q, float3(0.80, -0.48, 0.36)), dot(q, float3(0.60, 0.64, -0.48)), dot(q, float3(0.0, 0.60, 0.80))) * 0.37 + float3(0.31, 0.67, 0.13);
                        float4 fine_ = tex3Dlod(_Detail, float4(q, max(level, 0.0)));
                        float4 broad = tex3Dlod(_Detail, float4(turned, max(level - 1.43, 0.0)));
                        float seenFine = 1.0 - smoothstep(2.6, 4.0, level), seenBroad = 1.0 - smoothstep(2.6, 4.0, level - 1.43);
                        float lump = 0.45 + 0.65 * (seenFine * (fine_.r - 0.45) + seenBroad * (broad.r - 0.45));
                        float3 slopeBroad = (broad.gba - 0.5) * (2.0 * 10.11 * 0.37 * seenBroad);
                        float3 slope = (fine_.gba - 0.5) * (2.0 * 10.11 * seenFine)
                                   + float3(dot(slopeBroad, float3(0.80, 0.60, 0.0)), dot(slopeBroad, float3(-0.48, 0.64, 0.60)), dot(slopeBroad, float3(0.36, -0.48, 0.80)));

                        // The body of the smoke is left whole: thick, and solid to look at. It is only towards its edge, where
                        // there is less smoke at a place than round about it, that the billows are cut in: deepest between the
                        // lumps of the pattern, hardly at all where a lump stands. So the smoke ends in a surface of rounded
                        // bulges with creases between them, as real smoke does, rather than fading away or being full of holes.
                        float fill = 1.0 - exp(-smoke * _VolParams.x * _VolPeak.w);
                        float about = 1.0 - exp(-tone.r * tone.r * _VolParams.x * _VolPeak.w);
                        float eaten = _VolParams.z * clamp(1.25 - 1.6 * lump, 0.0, 1.0);
                        float kept = max(fill - eaten * max(about, 0.6 * fill), 0.0) / (1.0 - 0.6 * eaten);
                        float sigma = smoke * _VolParams.x * (fill > 1e-4 ? min(kept / fill, 1.6) : 0.0);
                        if (_VolGrid.w > 0.5 && (_VolGrid.w < 1.5 || _VolGrid.w > 2.5)) sigma = smoke * _VolParams.x;      // (testing: the smoke as it is on the grid, not cut into)
                        float thin = exp(-sigma * _VolPeak.w);

                        // Flame: licked into tongues by the same pattern, flickering, and coloured by how hot it is.
                        float heat = air.a * 1.2 * (0.72 + 0.56 * lump);
                        float burning = flame * _VolParams.w * clamp(2.5 * lump - 0.45, 0.0, 1.8);
                        float3 hot = heat < 0.25 ? _VolHot1.rgb * (heat * 4.0) : heat < 0.5 ? lerp(_VolHot1.rgb, _VolHot2.rgb, heat * 4.0 - 1.0) : heat < 0.75 ? lerp(_VolHot2.rgb, _VolHot3.rgb, heat * 4.0 - 2.0) : lerp(_VolHot3.rgb, _VolHot4.rgb, min(heat * 4.0 - 3.0, 1.0));

                        // Smoke: its own colour, in the light that reaches it. Thinned out, dark smoke looks paler. And each puff
                        // has a lit side and a shaded one: where the smoke gets thicker towards the sun, this place is darker.
                        float3 own = tone.b * tone.b * lerp(float3(1.0, 1.0, 1.0), _VolTint.rgb, tone.a);
                        own += float3(0.05, 0.048, 0.045) * thin * step(own.r, 0.3);
                        float relief = clamp(1.0 - _VolSunLocal.w * 0.055 * dot(slope, _VolSunLocal.xyz), 0.45, 1.6);
                        // The sun's light, after the smoke that lies towards the sun (a part of it gets much further than the rest: light
                        // finds its way round inside a cloud), the light of the open sky, and that of the fires near by.
                        float before = (air.r * 0.99611 + air.g * 0.0038911) * 16.0;
                        float sun = 0.6 * exp(-before) + 0.4 * exp(-0.25 * before);
                        float sky = exp(-air.b * 8.0);
                        float3 toA = here - _VolLampA.xyz, toB = here - _VolLampB.xyz, toC = here - _VolLampC.xyz;
                        float firelight = min(_VolLamps.x * _VolLampA.w / (dot(toA, toA) + _VolLampA.w + 1e-4) + _VolLamps.y * _VolLampB.w / (dot(toB, toB) + _VolLampB.w + 1e-4) + _VolLamps.z * _VolLampC.w / (dot(toC, toC) + _VolLampC.w + 1e-4), 1.0);
                        // Beside a fire the eye is taken up by the fire: the daylight on the smoke there counts for little, and it shows by the firelight.
                        float dazzle = 1.0 - 0.85 * min(firelight * 1.6, 1.0);
                        float3 light = (_VolSun.rgb * (sun * shine * relief) + _VolAmb.rgb * (0.3 + 0.7 * sky)) * dazzle + _VolGlow.rgb * (firelight * 4.0);
                        float3 lit = sqrt(own * light);
                        if (_VolGrid.w > 0.5 && _VolGrid.w < 2.5) { lit = float3(0.45, 0.45, 0.45); burning = 0.0; }                      // (testing: no light and no flame)

                        float both = sigma + burning;
                        float a = 1.0 - exp(-both * min(stride * finest, t1 - t));        // the last step stops at whatever solid thing is behind
                        colour += through * a * (lit * sigma + hot * (burning * 1.8)) / max(both, 1e-5);
                        through *= 1.0 - a;
                    }
                    else if (fine)
                    {
                        clear += stride;
                    }
                    k += stride;
                    if (fine)
                    {
                        // Once most of what can be seen along this ray has been passed (or enough has been spent on it), the steps are four times as long.
                        if (!deep && (left <= 0.0 || through < 0.3) && fmod(k, 4.0 * stride) < 0.5) { deep = true; stride *= 4.0; }
                        // And out of the smoke, as long as the grid's cells again.
                        if (clear >= coarse && fmod(k, coarse) < 0.5) { fine = false; stride = coarse; }
                    }
                }
                return float4(colour, 1.0 - through);
            }
            ENDCG
        }
    }
}
