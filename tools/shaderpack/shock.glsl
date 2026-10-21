#ifdef VERTEX
#version 150
uniform vec4 _ShockSheet;    // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
in vec3 in_POSITION0;
out vec2 vs_TEXCOORD0;
void main()
{
    // One triangle that covers the whole screen, given in the screen's own coordinates (-1 to 1 each way).
    gl_Position = vec4(in_POSITION0.xy * _ShockSheet.xy, _ShockSheet.z, _ShockSheet.w);
    vs_TEXCOORD0 = in_POSITION0.xy;
}
#endif
#ifdef FRAGMENT
#version 150
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
uniform vec4 _ShockEye;      // xyz: where the camera is
uniform vec4 _ShockRight;    // xyz: the camera's right, as long as half the picture is wide at one metre off; w: that length
uniform vec4 _ShockUp;       // the same, upward
uniform vec4 _ShockAhead;    // xyz: the way the camera looks, w: the picture's height over its width
uniform vec4 _ShockDepth;    // xy: turn a depth read from the picture of depths into metres ahead, z: 1 if there is such a picture, w: what it reads where nothing was drawn
uniform vec4 _ShockC0;       // xyz: the middle of a front, w: its radius in metres (0: there is none)
uniform vec4 _ShockC1;
uniform vec4 _ShockC2;
uniform vec4 _ShockC3;
uniform vec4 _ShockP0;       // x: how far it pushes the picture at most, in heights of the screen, y: how wide the front is, in metres, z: the least it may look, in heights of the screen, w: how much lighter and darker it shows
uniform vec4 _ShockP1;
uniform vec4 _ShockP2;
uniform vec4 _ShockP3;
uniform vec4 _ShockG0;       // xyz: which way is up where the front is, w: how far its ring along the ground pushes the picture, in heights of the screen (0: it is not on the ground)
uniform vec4 _ShockG1;
uniform vec4 _ShockG2;
uniform vec4 _ShockG3;
uniform vec4 _ShockPulse;    // x: how far the whole picture is read from further out at its corners just now, in halves of the screen (less than nothing: the picture swells), y: how far the colours part
uniform vec4 _ShockW0;       // x: the blast's size, the cube root of its charge in kilograms of TNT, in metres (0: it leaves no cloud), y: how far the air at its middle is above its dew point (kelvin, as at 15 degrees), z: how much less a metre higher, w: its fireball's radius
uniform vec4 _ShockW1;
uniform vec4 _ShockW2;
uniform vec4 _ShockW3;
uniform vec4 _ShockSun;      // xyz: towards the sun
uniform vec4 _ShockSunLight; // rgb: the sun's light (amounts of light: what the screen shows is their square root), w: how much more a thin cloud shows towards the sun
uniform vec4 _ShockSky;      // rgb: the sky's light (the same)
uniform vec4 _ShockFire;     // rgb: the fire's light, as it lights the smoke a fireball's radius from it (the same)
uniform sampler2D _VolScene; // the picture so far
uniform sampler2D _CameraDepthTexture;
in vec2 vs_TEXCOORD0;
out vec4 SV_Target0;

float shade;

// Across a front, from one width inside it (-1) to one width outside (1): nothing right at it, the picture
// drawn towards it from both sides, and nothing again a width away. (4.68: so that the most is 1.)
float ripple(float d)
{
    return d * (1.0 - pow(abs(d), 0.8)) * 4.68;
}

// Where on the screen a place is, given from the eye.
vec2 screenOf(vec3 v)
{
    return vec2(dot(v, _ShockRight.xyz) / (_ShockRight.w * _ShockRight.w), dot(v, _ShockUp.xyz) / (_ShockUp.w * _ShockUp.w)) / max(dot(v, _ShockAhead.xyz), 1e-3) * 0.5 + 0.5;
}

vec2 front(vec4 c, vec4 p, vec4 g, vec3 rd, float scene)
{
    vec2 moved = vec2(0.0);
    if (c.w <= 0.0) return moved;
    float span = 2.0 * _ShockUp.w;                 // metres to a height of the screen, a metre off
    vec3 toC = c.xyz - _ShockEye.xyz;
    float along = dot(toC, rd);
    vec3 off = toC - rd * along;                   // from where the line of sight passes nearest the middle of the ball, to the middle
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
            vec2 out2 = -vec2(dot(off, _ShockRight.xyz) / _ShockRight.w, dot(off, _ShockUp.xyz) / _ShockUp.w);
            float len = length(out2);
            if (len > 1e-4)
            {
                moved += out2 / len * vec2(_ShockAhead.w, 1.0) * most;
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
            vec3 h = rd * far - toC;               // from the middle of the ring to there
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
float hash(vec3 p)
{
    p = fract(p * 0.3183099 + 0.1);
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float lumps(vec3 x)
{
    vec3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i), hash(i + vec3(1.0, 0.0, 0.0)), f.x), mix(hash(i + vec3(0.0, 1.0, 0.0)), hash(i + vec3(1.0, 1.0, 0.0)), f.x), f.y),
               mix(mix(hash(i + vec3(0.0, 0.0, 1.0)), hash(i + vec3(1.0, 0.0, 1.0)), f.x), mix(hash(i + vec3(0.0, 1.0, 1.0)), hash(i + vec3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
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
void vapour(vec4 c, vec4 v, vec4 g, vec3 rd, float scene, inout vec4 got)
{
    if (v.x <= 0.0 || c.w <= 0.0) return;
    float size = v.x;
    float outer = c.w - 0.95 * size;
    if (outer <= 0.0) return;
    vec3 toC = c.xyz - _ShockEye.xyz;
    float along = dot(toC, rd);
    float b2 = max(dot(toC, toC) - along * along, 0.0);
    if (b2 >= outer * outer) return;
    float half_ = sqrt(outer * outer - b2);
    float t0 = max(along - half_, 0.0), t1 = min(along + half_, scene);
    if (t1 <= t0) return;
    // What lies beyond the fire and its smoke is hidden by them: the far side of the shell, where the line of sight passes
    // through them. They are not in the picture of depths: taken as a ball about the fireball, wider and flatter along the
    // ground (and above it, for a blast on the ground), soft at its edge.
    vec3 offC = toC - rd * along + g.xyz * (g.w > 0.0 ? 0.5 * v.w : 0.0);
    float upOff = dot(offC, g.xyz);
    float smoke = 1.0 - smoothstep(0.5, 1.15, length(vec2(length(offC - g.xyz * upOff) / (1.35 * v.w), upOff / (1.2 * v.w))));
    // (the hollow inside the shell is passed over: the near side, and the far side)
    float inner = c.w - 4.0 * size;
    float hole = inner > 0.0 && b2 < inner * inner ? sqrt(inner * inner - b2) : -1.0;
    float toward = max(dot(rd, _ShockSun.xyz), 0.0);
    float shine = 1.0 + _ShockSunLight.w * toward * toward * toward * toward;
    for (int side = 0; side < 2; side++)
    {
        float a = side == 0 ? t0 : max(t0, along + hole);
        float b = side == 0 ? (hole > 0.0 ? min(t1, along - hole) : t1) : t1;
        if (side == 1 && hole <= 0.0) break;
        if (b <= a) continue;
        float dt = (b - a) / 6.0;
        for (int i = 0; i < 6; i++)
        {
            vec3 p = rd * (a + (float(i) + 0.5) * dt) - toC;
            float r = length(p);
            float behind = (c.w - r) / size;
            float k = (behind - 0.95) / 3.05;
            if (k <= 0.0 || k >= 1.0) continue;
            float z = max(r / size, 1.0);
            float shape = k < 0.3 ? sin(k * 5.236) : cos((k - 0.3) * 2.244);
            float cool = 28.5 / z * shape;
            float wet = (cool - max(v.y - v.z * dot(p, g.xyz), 0.0)) * smoothstep(3.0, 4.5, z);
            if (wet <= 0.0) continue;
            vec3 n = p / max(r, 1e-3);
            // (the air is not as damp everywhere: lumps of it a few tens of metres across, damper and drier, held where they are
            // as the front sweeps through them, which it shows up as it goes)
            vec3 air = (p + c.xyz * 0.37) / size;
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
            vec3 lit = sqrt(_ShockSunLight.rgb * (facing * shine) + _ShockSky.rgb + _ShockFire.rgb * (4.0 * fire));
            got.rgb += (1.0 - got.a) * hides * lit;
            got.a += (1.0 - got.a) * hides;
        }
    }
}

void main()
{
    vec2 at = vs_TEXCOORD0;
    vec2 uv = at * 0.5 + 0.5;
    vec3 rd = normalize(_ShockAhead.xyz + _ShockRight.xyz * at.x + _ShockUp.xyz * at.y);
    float scene = 1.0e9;
    if (_ShockDepth.z > 0.5)
    {
        float depth = texture(_CameraDepthTexture, uv).x;
        if (abs(depth - _ShockDepth.w) > 1e-5) scene = 1.0 / (_ShockDepth.x * depth + _ShockDepth.y) / max(dot(rd, _ShockAhead.xyz), 1e-3);
    }
    shade = 0.0;
    vec2 moved = front(_ShockC0, _ShockP0, _ShockG0, rd, scene) + front(_ShockC1, _ShockP1, _ShockG1, rd, scene)
               + front(_ShockC2, _ShockP2, _ShockG2, rd, scene) + front(_ShockC3, _ShockP3, _ShockG3, rd, scene);
    moved += at * (0.5 * (0.3 + 0.35 * dot(at, at)) * _ShockPulse.x);
    float far = length(moved * vec2(1.0 / _ShockAhead.w, 1.0));      // in heights of the screen
    vec4 cloud = vec4(0.0);
    vapour(_ShockC0, _ShockW0, _ShockG0, rd, scene, cloud);
    vapour(_ShockC1, _ShockW1, _ShockG1, rd, scene, cloud);
    vapour(_ShockC2, _ShockW2, _ShockG2, rd, scene, cloud);
    vapour(_ShockC3, _ShockW3, _ShockG3, rd, scene, cloud);
    if (far < 0.0003 && cloud.a < 0.002) discard;
    // (red, green and blue are bent a very little differently, as by glass)
    float r = texture(_VolScene, uv + moved * (1.0 + _ShockPulse.y)).r;
    float g = texture(_VolScene, uv + moved).g;
    float b = texture(_VolScene, uv + moved * (1.0 - _ShockPulse.y)).b;
    float shown = smoothstep(0.0003, 0.002, far);
    // (the cloud over what lies behind it, bent or not)
    SV_Target0 = vec4(cloud.rgb + vec3(r, g, b) * (1.0 + shade) * shown * (1.0 - cloud.a), cloud.a + shown * (1.0 - cloud.a));
}
#endif
