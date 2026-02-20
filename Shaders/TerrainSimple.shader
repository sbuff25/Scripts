// Lean mobile terrain shader for URP (Unity 6 / URP 17).
// 2-layer slope-based blending: flat (grass) + steep (rock).
// World-space XZ UVs — no tri-planar overhead.
//
// Fragment cost: 2 texture samples, half-Lambert diffuse, SH ambient.
// Designed for Unity Terrain component with a custom material.
//
// References:
//   URP ShaderLibrary – https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/shading-model.html
//   Shadows           – https://docs.unity3d.com/6000.1/Documentation/Manual/urp/use-built-in-shader-methods-shadows.html

Shader "PDW/Terrain Simple"
{
    Properties
    {
        [Header(Flat Layer)]
        _FlatTex   ("Albedo", 2D)               = "white" {}
        _FlatTint  ("Tint",   Color)             = (1, 1, 1, 1)
        _FlatScale ("Scale",  Range(0.01, 10))   = 1.0

        [Header(Steep Layer)]
        _SteepTex   ("Albedo", 2D)              = "white" {}
        _SteepTint  ("Tint",   Color)            = (1, 1, 1, 1)
        _SteepScale ("Scale",  Range(0.01, 10))  = 1.0

        [Header(Slope Blending)]
        [Tooltip(Slope value 0 to 1 where steep layer begins. 0 is flat and 1 is vertical.)]
        _SlopeThreshold  ("Threshold",  Range(0, 1))       = 0.4
        [Tooltip(Width of the blend region between layers.)]
        _SlopeBlendWidth ("Blend Width", Range(0.01, 0.5)) = 0.1
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry-100"
        }

        // ─────────────────────────────────────────────
        // Shared declarations
        // ─────────────────────────────────────────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_FlatTex);   SAMPLER(sampler_FlatTex);
        TEXTURE2D(_SteepTex);  SAMPLER(sampler_SteepTex);

        CBUFFER_START(UnityPerMaterial)
            half4  _FlatTint;
            float  _FlatScale;
            half4  _SteepTint;
            float  _SteepScale;
            half   _SlopeThreshold;
            half   _SlopeBlendWidth;
        CBUFFER_END
        ENDHLSL

        // ═══════════════════════════════════════════════
        //  PASS 1 – Forward Lit
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half   fogFactor  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(posWS);
                o.positionWS = posWS;
                o.normalWS   = (half3)TransformObjectToWorldNormal(input.normalOS);
                o.fogFactor  = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half3 normalWS = normalize(input.normalWS);

                // World-space XZ UVs
                float2 flatUV  = input.positionWS.xz * _FlatScale;
                float2 steepUV = input.positionWS.xz * _SteepScale;

                // 2 texture samples total
                half3 flatCol  = SAMPLE_TEXTURE2D(_FlatTex,  sampler_FlatTex,  flatUV).rgb  * _FlatTint.rgb;
                half3 steepCol = SAMPLE_TEXTURE2D(_SteepTex, sampler_SteepTex, steepUV).rgb * _SteepTint.rgb;

                // Slope blend: 0 = flat, 1 = vertical
                half slope = 1.0h - saturate(normalWS.y);
                half bw = max(_SlopeBlendWidth, 0.001h);
                half steepW = smoothstep(_SlopeThreshold - bw, _SlopeThreshold + bw, slope);
                half3 albedo = lerp(flatCol, steepCol, steepW);

                // Main directional light with shadows
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                // Half-Lambert diffuse
                half NdotL = dot(normalWS, mainLight.direction);
                NdotL = NdotL * 0.5h + 0.5h;
                half3 diffuse = albedo * mainLight.color * NdotL * mainLight.shadowAttenuation;

                // Ambient from light probes
                half3 ambient = SampleSH(normalWS) * albedo;

                half3 color = diffuse + ambient;
                color = MixFog(color, input.fogFactor);

                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        // ═══════════════════════════════════════════════
        //  PASS 2 – Shadow Caster
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                float3 posWS   = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - posWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 posCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                    posCS.z = min(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    posCS.z = max(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                o.positionCS = posCS;
                return o;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ═══════════════════════════════════════════════
        //  PASS 3 – Depth Only
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return o;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ═══════════════════════════════════════════════
        //  PASS 4 – Depth Normals
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS   = (half3)TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            half4 DepthNormalsFrag(Varyings input) : SV_Target
            {
                return half4(normalize(input.normalWS), 0.0h);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
