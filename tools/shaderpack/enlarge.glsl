#ifdef VERTEX
#version 150
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _VolDrawn;      // x: 1 if this cloud's small picture was drawn for the camera drawing now (see Air.ForCamera)
in vec3 in_POSITION0;
void main()
{
    // (a camera none of this cloud lies in the range of: nothing drawn at all)
    if (_VolDrawn.x < 0.5) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }
    vec4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
    vec4 clip = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
    // (the far faces of the smoke's box, kept inside the camera's range: exactly as the smoke itself is drawn)
    clip.z = min(clip.z, clip.w * 0.99999);
    gl_Position = clip;
}
#endif
#ifdef FRAGMENT
#version 150
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
uniform vec4 _ZBufferParams;
uniform vec4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene, z: 1 if the small picture was drawn for this camera
uniform sampler2D _VolHalf;  // the smoke, drawn small: its colour times how much it hides, and how much it hides
uniform sampler2D _CameraDepthTexture;
out vec4 SV_Target0;

// How far ahead the ray of one of the small picture's pixels stopped.
float stopped(ivec2 cell, ivec2 most)
{
    vec4 four = vec4(texelFetch(_CameraDepthTexture, min(2 * cell, most), 0).x, texelFetch(_CameraDepthTexture, min(2 * cell + ivec2(1, 0), most), 0).x,
                     texelFetch(_CameraDepthTexture, min(2 * cell + ivec2(0, 1), most), 0).x, texelFetch(_CameraDepthTexture, min(2 * cell + ivec2(1, 1), most), 0).x);
    four = 1.0 / (_ZBufferParams.z * four + _ZBufferParams.w);
    return ((cell.x + cell.y) & 1) == 0 ? min(min(four.x, four.y), min(four.z, four.w)) : max(max(four.x, four.y), max(four.z, four.w));
}

void main()
{
    if (_VolCamera.z < 0.5) discard;
    ivec2 pixel = ivec2(gl_FragCoord.xy), last = textureSize(_VolHalf, 0) - 1;
    // The four small pixels round this one, and how much each counts for by nearness alone.
    vec2 at = (vec2(pixel) + 0.5) * 0.5 - 0.5;
    ivec2 base = ivec2(floor(at));
    vec2 f = at - vec2(base);
    ivec2 c00 = clamp(base, ivec2(0), last), c10 = clamp(base + ivec2(1, 0), ivec2(0), last), c01 = clamp(base + ivec2(0, 1), ivec2(0), last), c11 = clamp(base + ivec2(1, 1), ivec2(0), last);
    vec4 share = vec4((1.0 - f.x) * (1.0 - f.y), f.x * (1.0 - f.y), (1.0 - f.x) * f.y, f.x * f.y);
    // (Most of the box is clear air: where none of the four has anything in it there is nothing to put on the
    // screen, however they are weighed, and the twenty-one looks at the depth picture below are spared.)
    vec4 s00 = texelFetch(_VolHalf, c00, 0), s10 = texelFetch(_VolHalf, c10, 0), s01 = texelFetch(_VolHalf, c01, 0), s11 = texelFetch(_VolHalf, c11, 0);
    if (s00 == vec4(0.0) && s10 == vec4(0.0) && s01 == vec4(0.0) && s11 == vec4(0.0)) discard;
    if (_VolCamera.x > 0.5)
    {
        ivec2 most = textureSize(_CameraDepthTexture, 0) - 1;
        float here = 1.0 / (_ZBufferParams.z * texelFetch(_CameraDepthTexture, min(pixel, most), 0).x + _ZBufferParams.w);
        vec4 apart = vec4(stopped(c00, most), stopped(c10, most), stopped(c01, most), stopped(c11, most)) - here;
        // How fast this pixel's own surface goes away from the camera, pixel to pixel: to either side and up and
        // down, the smaller change of each pair (at the edge of something, the other is the jump to what is behind it).
        vec4 beside = vec4(texelFetch(_CameraDepthTexture, clamp(pixel - ivec2(1, 0), ivec2(0), most), 0).x, texelFetch(_CameraDepthTexture, clamp(pixel + ivec2(1, 0), ivec2(0), most), 0).x,
                          texelFetch(_CameraDepthTexture, clamp(pixel - ivec2(0, 1), ivec2(0), most), 0).x, texelFetch(_CameraDepthTexture, clamp(pixel + ivec2(0, 1), ivec2(0), most), 0).x);
        beside = abs(1.0 / (_ZBufferParams.z * beside + _ZBufferParams.w) - here);
        float slopes = min(beside.x, beside.y) + min(beside.z, beside.w);
        // (the small pixels stopped at things up to two pixels and a bit from this one)
        float slack = 0.01 * here + 0.01 + 3.0 * slopes;
        share /= apart * apart + slack * slack;
        share /= share.x + share.y + share.z + share.w;
    }
    vec4 smoke = s00 * share.x + s10 * share.y + s01 * share.z + s11 * share.w;
    if (smoke.a <= 0.0005 && smoke.r + smoke.g + smoke.b <= 0.0005) discard;
    SV_Target0 = smoke;
}
#endif
