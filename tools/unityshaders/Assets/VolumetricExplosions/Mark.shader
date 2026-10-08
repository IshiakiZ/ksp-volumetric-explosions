// A burn mark thrown onto whatever is there: the same shader as tools/shaderpack/mark.glsl, for the Unity
// editor, so that it can be built for Direct3D (Windows). Keep the two in step by hand; this one is short.
//
// NOT YET BUILT OR RUN: it was written without a Unity editor to hand.
Shader "VolumetricExplosions/Mark"
{
    Properties
    {
        _MarkTex ("Mark", 2D) = "black" {}
        _MarkEmbers ("Embers", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
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

            float4 _VolCamera, _MarkTint, _MarkGlow, _MarkSize;
            sampler2D _MarkTex, _MarkEmbers;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float4 screen : TEXCOORD1;
                float3 up : TEXCOORD2;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(vertex.xyz, 1.0));
                o.pos = mul(UNITY_MATRIX_VP, world);
                #if defined(UNITY_REVERSED_Z)
                o.pos.z = max(o.pos.z, o.pos.w * 0.00001);
                #else
                o.pos.z = min(o.pos.z, o.pos.w * 0.99999);
                #endif
                o.world = world.xyz;
                o.screen = ComputeScreenPos(o.pos);
                o.up = normalize(float3(unity_ObjectToWorld[0][1], unity_ObjectToWorld[1][1], unity_ObjectToWorld[2][1]));
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 eye = _WorldSpaceCameraPos;
                float3 rd = normalize(i.world - eye);
                float forward = max(-mul((float3x3)UNITY_MATRIX_V, rd).z, 1e-4);
                float depth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen)));
                float3 place = eye + rd * (depth / forward);
                float3 inBox = mul(unity_WorldToObject, float4(place, 1.0)).xyz;
                // Which way the surface there faces, from how the place changes from one pixel to the next. A face
                // turned well away from the mark is left alone: thrown onto it from above, the mark would be drawn out into streaks.
                float3 facing = normalize(cross(ddx(place), ddy(place)));
                float flat_ = smoothstep(0.3, 0.6, abs(dot(facing, i.up)));
                float level = max(log2(depth * _VolCamera.y / _MarkSize.x), 0.0) + 0.5 * (1.0 - flat_);
                float4 mark = tex2Dlod(_MarkTex, float4(inBox.xz + 0.5, 0.0, level));
                float4 hot = tex2Dlod(_MarkEmbers, float4(inBox.xz + 0.5, 0.0, level));
                float inside = step(abs(inBox.x), 0.5) * step(abs(inBox.z), 0.5) * (1.0 - smoothstep(0.35, 0.5, abs(inBox.y)));
                float shown = _VolCamera.x * step(depth, _ProjectionParams.z * 0.999) * inside * flat_;
                float a = mark.a * _MarkTint.a * shown;
                float3 glow = hot.rgb * hot.a * _MarkGlow.rgb * shown;
                clip(a + glow.r + glow.g + glow.b - 0.0005);
                return float4(mark.rgb * _MarkTint.rgb * a + glow, a);
            }
            ENDCG
        }
    }
}
