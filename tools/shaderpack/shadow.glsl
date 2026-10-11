#ifdef VERTEX
#version 150
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
in vec3 in_POSITION0;
out vec3 vs_TEXCOORD0;
out vec4 vs_TEXCOORD1;
void main()
{
    vec4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
    vec4 clip = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
    // (the far faces of the box are what is drawn; they are kept inside the camera's range, as for the smoke)
    clip.z = min(clip.z, clip.w * 0.99999);
    gl_Position = clip;
    vs_TEXCOORD0 = world.xyz;
    vs_TEXCOORD1 = clip;
}
#endif
#ifdef FRAGMENT
#version 150
// The shadow a cloud of smoke throws on whatever is under it. The box this is drawn with is the stretch the shadow can
// fall in (the cloud's box drawn out away from the sun to the ground). For each pixel of it the place the game has drawn
// there is worked out from its depth picture and followed towards the sun into the cloud's grid, where how much smoke
// lies between each cell and the sun is kept already (the smoke's own light is worked out from it): what is drawn there
// is darkened by as much of the sunlight as that smoke takes away. So a cloud's shadow lies on the ground, the pad, the
// buildings and the ships as they are, and goes soft and thin where the smoke does.
uniform vec3 _WorldSpaceCameraPos;
uniform vec4 _ProjectionParams;
uniform vec4 _ZBufferParams;
uniform vec4 hlslcc_mtx4x4unity_MatrixV[4];
uniform vec4 _VolCamera;       // x: 1 if the camera drawing this has a depth picture of the scene
uniform vec4 _ShadowGridX;     // the rows of the turn from the scene's axes into the cloud's grid box (-0.5 to 0.5 each way)
uniform vec4 _ShadowGridY;
uniform vec4 _ShadowGridZ;
uniform vec4 _ShadowSun;       // xyz: towards the sun, in the grid box's own axes (and its sizes); w: how dark a shadow may be (0: none)
uniform vec4 _ShadowSunWorld;  // xyz: towards the sun, in the scene's axes
uniform sampler3D _Volume;
uniform sampler2D _CameraDepthTexture;
in vec3 vs_TEXCOORD0;
in vec4 vs_TEXCOORD1;
out vec4 SV_Target0;
void main()
{
    vec3 eye = _WorldSpaceCameraPos;
    vec3 rd = normalize(vs_TEXCOORD0 - eye);
    float forward = -(hlslcc_mtx4x4unity_MatrixV[0].z * rd.x + hlslcc_mtx4x4unity_MatrixV[1].z * rd.y + hlslcc_mtx4x4unity_MatrixV[2].z * rd.z);
    forward = max(forward, 1e-4);
    vec2 uv = vs_TEXCOORD1.xy / vs_TEXCOORD1.w * 0.5 + 0.5;
    float depth = 1.0 / (_ZBufferParams.z * texture(_CameraDepthTexture, uv).x + _ZBufferParams.w);
    if (_VolCamera.x < 0.5 || depth > _ProjectionParams.z * 0.999) discard;
    vec3 place = eye + rd * (depth / forward);
    // Which way the surface there faces (from how the place changes from one pixel to the next, turned to face the
    // camera): a face turned from the sun is in its own shadow already, and has no sunlight for the smoke to take.
    vec3 facing = cross(dFdx(place), dFdy(place));
    float size = length(facing);
    facing = size > 1e-8 ? facing / size : -rd;
    if (dot(facing, rd) > 0.0) facing = -facing;
    float lit = clamp(dot(facing, _ShadowSunWorld.xyz) * 4.0, 0.0, 1.0);
    if (lit <= 0.0) discard;
    // Into the grid's box, on the way to the sun.
    vec3 p = vec3(dot(_ShadowGridX.xyz, place) + _ShadowGridX.w, dot(_ShadowGridY.xyz, place) + _ShadowGridY.w, dot(_ShadowGridZ.xyz, place) + _ShadowGridZ.w);
    vec3 d = _ShadowSun.xyz;
    vec3 safe = vec3(abs(d.x) < 1e-6 ? 1e-6 : d.x, abs(d.y) < 1e-6 ? 1e-6 : d.y, abs(d.z) < 1e-6 ? 1e-6 : d.z);
    vec3 ta = (vec3(-0.5) - p) / safe, tb = (vec3(0.5) - p) / safe;
    vec3 lo = min(ta, tb), hi = max(ta, tb);
    float t0 = max(max(lo.x, lo.y), max(lo.z, 0.0)), t1 = min(min(hi.x, hi.y), hi.z);
    if (t1 <= t0) discard;
    vec3 e = clamp(p + d * (t0 + 1e-3 * (t1 - t0)), vec3(-0.5), vec3(0.5));
    vec4 air = textureLod(_Volume, e + 0.5, 0.0);
    float before = (air.r * 0.99611 + air.g * 0.0038911) * 16.0;
    // (as the smoke's own light reckons it: a part of the sunlight finds its way round inside a cloud)
    float through = 0.8 * exp(-before) + 0.2 * exp(-0.25 * before);
    float dark = _ShadowSun.w * lit * (1.0 - through);
    if (!(dark > 0.004)) discard;
    SV_Target0 = vec4(vec3(1.0 - min(dark, 0.95)), 1.0);
}
#endif
