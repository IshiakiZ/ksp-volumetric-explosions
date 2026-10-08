#ifdef VERTEX
#version 150
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
in vec3 in_POSITION0;
out vec3 vs_TEXCOORD0;
out vec4 vs_TEXCOORD1;
out vec3 vs_TEXCOORD2;
void main()
{
    vec4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
    vec4 clip = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
    // (the far faces of the box are what is drawn; they are kept inside the camera's range, as for the smoke)
    clip.z = min(clip.z, clip.w * 0.99999);
    gl_Position = clip;
    vs_TEXCOORD0 = world.xyz;
    vs_TEXCOORD1 = clip;
    vs_TEXCOORD2 = normalize(hlslcc_mtx4x4unity_ObjectToWorld[1].xyz);
}
#endif
#ifdef FRAGMENT
#version 150
// A burn mark, thrown onto whatever is there. The mark is a flat box laid on the ground where the fire
// was. For each pixel of the box the place the game has already drawn there is worked out from its depth
// picture, and if that place lies inside the box it takes the mark's colour at the spot straight "above"
// it. So the mark follows steps, ramps and roofs exactly, and cannot float over a slope or sink into one.
uniform vec3 _WorldSpaceCameraPos;
uniform vec4 _ProjectionParams;
uniform vec4 _ZBufferParams;
uniform vec4 hlslcc_mtx4x4unity_WorldToObject[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixV[4];
uniform vec4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene, y: the angle one of its pixels covers
uniform vec4 _MarkTint;      // rgb: the colour the mark's picture is multiplied by, a: how strongly the mark shows
uniform vec4 _MarkGlow;      // rgb: how brightly the embers glow
uniform vec4 _MarkSize;      // x: metres to a cell of the mark's picture
uniform sampler2D _MarkTex;
uniform sampler2D _MarkEmbers;
uniform sampler2D _CameraDepthTexture;
in vec3 vs_TEXCOORD0;
in vec4 vs_TEXCOORD1;
in vec3 vs_TEXCOORD2;
out vec4 SV_Target0;
void main()
{
    vec3 eye = _WorldSpaceCameraPos;
    vec3 rd = normalize(vs_TEXCOORD0 - eye);
    float forward = -(hlslcc_mtx4x4unity_MatrixV[0].z * rd.x + hlslcc_mtx4x4unity_MatrixV[1].z * rd.y + hlslcc_mtx4x4unity_MatrixV[2].z * rd.z);
    forward = max(forward, 1e-4);
    vec2 uv = vs_TEXCOORD1.xy / vs_TEXCOORD1.w * 0.5 + 0.5;
    float depth = 1.0 / (_ZBufferParams.z * texture(_CameraDepthTexture, uv).x + _ZBufferParams.w);
    vec3 place = eye + rd * (depth / forward);
    vec3 inBox = hlslcc_mtx4x4unity_WorldToObject[0].xyz * place.x + hlslcc_mtx4x4unity_WorldToObject[1].xyz * place.y + hlslcc_mtx4x4unity_WorldToObject[2].xyz * place.z + hlslcc_mtx4x4unity_WorldToObject[3].xyz;
    // Which way the surface there faces, from how the place changes from one pixel to the next. A face
    // turned well away from the mark (a wall, the side of a ship standing in it) is left alone: thrown
    // onto it from above, the mark would be drawn out into streaks.
    vec3 facing = normalize(cross(dFdx(place), dFdy(place)));
    float flat_ = smoothstep(0.3, 0.6, abs(dot(facing, vs_TEXCOORD2)));
    // The picture is read as coarsely as it shows from here (worked out from the distance: the graphics card's own way goes wrong along the outline of whatever stands in front).
    float level = max(log2(depth * _VolCamera.y / _MarkSize.x), 0.0) + 0.5 * (1.0 - flat_);
    vec4 mark = textureLod(_MarkTex, inBox.xz + 0.5, level);
    vec4 hot = textureLod(_MarkEmbers, inBox.xz + 0.5, level);
    vec3 embers = hot.rgb * hot.a;
    float inside = step(abs(inBox.x), 0.5) * step(abs(inBox.z), 0.5) * (1.0 - smoothstep(0.35, 0.5, abs(inBox.y)));
    float shown = _VolCamera.x * step(depth, _ProjectionParams.z * 0.999) * inside * flat_;
    float a = mark.a * _MarkTint.a * shown;
    vec3 glow = embers * _MarkGlow.rgb * shown;
    if (a + glow.r + glow.g + glow.b <= 0.0005) discard;
    SV_Target0 = vec4(mark.rgb * _MarkTint.rgb * a + glow, a);
}
#endif
