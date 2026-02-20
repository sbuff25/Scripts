// Lean mobile grass/ground-cover shader for URP (Unity 6 / URP 17).
// Designed for GPU instanced foliage — minimal fragment cost for tile-based mobile GPUs.
//
// Features:
//   - Single texture sample (albedo RGB + alpha in A channel)
//   - Alpha clip (NOT alpha blend — preserves early-Z on tile-based architectures)
//   - Half precision throughout
//   - Half-Lambert diffuse with two-sided lighting (no specular, no PBR)
//   - Vertex-based wind animation using vertex color R as flex mask (0=root, 1=tip)
//   - Light probe ambient via SampleSH
//   - Single directional light only (no additional lights)
//   - GPU instancing via DrawMeshInstanced
//   - SRP Batcher compatible
//
// References:
//   URP ShaderLibrary – https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/shading-model.html
//   Shadows           – https://docs.unity3d.com/6000.1/Documentation/Manual/urp/use-built-in-shader-methods-shadows.html

Shader "PDW/Foliage Grass"
{
    Properties
    {
        [Header(Texture)]
        _BaseMap ("Albedo (RGB) Alpha (A)", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        [Header(Wind)]
        _WindSpeed ("Speed", Float) = 1.0
        _WindStrength ("Strength", Float) = 0.1
        _WindFreq ("Frequency", Float) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "AlphaTest"
        }

        // ─────────────────────────────────────────────
        // Shared declarations (injected into every pass)
        // ─────────────────────────────────────────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        // SRP-Batcher compatible per-material CBUFFER (must be identical in all passes)
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _Cutoff;
            half   _WindSpeed;
            half   _WindStrength;
            half   _WindFreq;
            float4 _LODCrossfade; // (fadeInStart, fadeInEnd, fadeOutStart, fadeOutEnd)
        CBUFFER_END

        // Per-instance thinning fade (set by FoliageRenderer via MaterialPropertyBlock)
        // See: https://docs.unity3d.com/ScriptReference/MaterialPropertyBlock.SetFloatArray.html
        UNITY_INSTANCING_BUFFER_START(FoliageInstanceProps)
            UNITY_DEFINE_INSTANCED_PROP(float, _ThinFade)
        UNITY_INSTANCING_BUFFER_END(FoliageInstanceProps)

        // Interleaved gradient noise — screen-space dither for LOD crossfade
        float _IGNoise(float2 pixelCoord)
        {
            return frac(52.9829189 * frac(dot(pixelCoord, float2(0.06711056, 0.00583715))));
        }

        // LOD crossfade dither — smoothly blends between adjacent LOD levels.
        void ApplyLODCrossfade(float3 posWS, float4 posCS)
        {
            float2 diff = float2(_WorldSpaceCameraPos.x - posWS.x, _WorldSpaceCameraPos.z - posWS.z);
            float dist = length(diff);
            float dither = _IGNoise(floor(posCS.xy));

            float fiW = _LODCrossfade.y - _LODCrossfade.x;
            if (fiW > 0.001)
            {
                float t = saturate((dist - _LODCrossfade.x) / fiW);
                clip(t + dither - 1.0);
            }

            float foW = _LODCrossfade.w - _LODCrossfade.z;
            if (foW > 0.001)
            {
                float t = saturate((dist - _LODCrossfade.z) / foW);
                clip((1.0 - t) - dither);
            }
        }

        // Thinning fade dither — smoothly fades out instances culled by distance thinning.
        // Uses the same IGN dither as LOD crossfade for visual consistency.
        void ApplyThinningFade(float4 posCS)
        {
            #ifdef UNITY_INSTANCING_ENABLED
            float fade = UNITY_ACCESS_INSTANCED_PROP(FoliageInstanceProps, _ThinFade);
            if (fade > 0.001 && fade < 0.999)
                clip(fade - _IGNoise(floor(posCS.xy)));
            #endif
        }

        // Wind displacement — shared across all passes so shadows match geometry
        float3 ApplyWind(float3 posWS, half flexMask)
        {
            half wind = sin(_Time.y * _WindSpeed + posWS.x * _WindFreq) * _WindStrength * flexMask;
            posWS.x += wind;
            posWS.z += wind * 0.5h;
            return posWS;
        }
        ENDHLSL

        // ═══════════════════════════════════════════════
        //  PASS 1 – Forward Lit
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma instancing_options assumeuniformscaling
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR; // R = flex mask (0 at root, 1 at tip)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                posWS = ApplyWind(posWS, input.color.r);

                output.positionCS = TransformWorldToHClip(posWS);
                output.positionWS = posWS;
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                output.normalWS   = (half3)TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                clip(tex.a - _Cutoff);
                ApplyLODCrossfade(input.positionWS, input.positionCS);
                ApplyThinningFade(input.positionCS);

                // Main directional light with shadows
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                // Half-Lambert with abs(NdotL) for two-sided lighting
                half NdotL = abs(dot(input.normalWS, mainLight.direction));
                NdotL = NdotL * 0.5h + 0.5h;
                half3 diffuse = tex.rgb * mainLight.color * NdotL * mainLight.shadowAttenuation;

                // Ambient from light probes
                half3 ambient = SampleSH(input.normalWS) * tex.rgb;

                return half4(diffuse + ambient, 1.0h);
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
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma instancing_options assumeuniformscaling
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posWS   = TransformObjectToWorld(input.positionOS.xyz);
                posWS = ApplyWind(posWS, input.color.r);
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

                output.positionCS = posCS;
                output.positionWS = posWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                ApplyLODCrossfade(input.positionWS, input.positionCS);
                ApplyThinningFade(input.positionCS);
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
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #pragma instancing_options assumeuniformscaling
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                posWS = ApplyWind(posWS, input.color.r);
                output.positionCS = TransformWorldToHClip(posWS);
                output.positionWS = posWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                ApplyLODCrossfade(input.positionWS, input.positionCS);
                ApplyThinningFade(input.positionCS);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
