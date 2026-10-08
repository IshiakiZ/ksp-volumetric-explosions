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
    if (far < 0.0003) discard;
    // (red, green and blue are bent a very little differently, as by glass)
    float r = texture(_VolScene, uv + moved * (1.0 + _ShockPulse.y)).r;
    float g = texture(_VolScene, uv + moved).g;
    float b = texture(_VolScene, uv + moved * (1.0 - _ShockPulse.y)).b;
    float shown = smoothstep(0.0003, 0.002, far);
    SV_Target0 = vec4(vec3(r, g, b) * (1.0 + shade) * shown, shown);
}
#endif
