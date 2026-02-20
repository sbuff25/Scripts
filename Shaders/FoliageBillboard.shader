// Y-axis billboard impostor shader for URP (Unity 6 / URP 17).
// Used as the final LOD level for trees/bushes — drastically reduces far-field triangle count.
//
// The vertex shader rotates each instance's mesh to face the camera around the Y axis.
// Position and uniform scale are read from the object-to-world matrix; rotation is discarded
// and replaced with the billboard orientation.
//
// Features:
//   - Single texture sample (albedo RGB + alpha in A channel)
//   - Optional normal map for per-texel lighting variation (toggle via _NORMALMAP keyword)
//   - Optional packed extra map: R=Subsurface G=Gloss B=AO (toggle via _EXTRAMAP keyword)
//   - Alpha clip
//   - Half precision throughout
//   - Half-Lambert diffuse with two-sided lighting
//   - LOD crossfade dithering (screen-space IGN, complementary clip)
//   - Light probe ambient via SampleSH
//   - Single directional light only (no additional lights)
//   - GPU instancing via DrawMeshInstanced
//   - SRP Batcher compatible
//   - 4 passes: ForwardLit, ShadowCaster, DepthOnly, DepthNormals
//
// Mesh setup:
//   Use a simple quad (4 verts, 2 tris) centered at the base (pivot at bottom center).
//   Example verts: (-0.5, 0, 0), (0.5, 0, 0), (0.5, 1, 0), (-0.5, 1, 0)
//   The shader handles Y-axis billboard rotation — mesh orientation doesn't matter.
//
// References:
//   URP ShaderLibrary – https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/shading-model.html

Shader "PDW/Foliage Billboard"
{
    Properties
    {
        [Header(Texture)]
        _BaseMap ("Albedo (RGB) Alpha (A)", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        [Header(Normal)]
        [Toggle(_NORMALMAP)] _UseNormalMap ("Enable Normal Map", Float) = 0.0
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0, 2)) = 1.0

        [Header(Extra Packed Texture)]
        [Toggle(_EXTRAMAP)] _UseExtraMap ("Enable Extra Map", Float) = 0.0
        _ExtraTex ("Extra (R=Subsurface G=Gloss B=AO)", 2D) = "white" {}
        _TranslucencyStrength ("Translucency Strength", Range(0, 2)) = 0.5
        _TranslucencyColor ("Translucency Color", Color) = (0.5, 0.8, 0.3, 1)

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

        TEXTURE2D(_BaseMap);   SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap);   SAMPLER(sampler_BumpMap);
        TEXTURE2D(_ExtraTex);  SAMPLER(sampler_ExtraTex);

        // SRP-Batcher compatible per-material CBUFFER (must be identical in all passes)
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BumpMap_ST;
            float4 _ExtraTex_ST;
            half4  _BaseColor;
            half   _Cutoff;
            half   _UseNormalMap;
            half   _BumpScale;
            half   _UseExtraMap;
            half   _TranslucencyStrength;
            half4  _TranslucencyColor;
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
        // Outgoing LOD fades out (complementary dither) while incoming LOD fades in.
        // Called after alpha clip in every pass (ForwardLit, ShadowCaster, DepthOnly)
        // so shadows and depth match the visible geometry.
        void ApplyLODCrossfade(float3 posWS, float4 posCS)
        {
            float2 diff = float2(_WorldSpaceCameraPos.x - posWS.x, _WorldSpaceCameraPos.z - posWS.z);
            float dist = length(diff);
            float dither = _IGNoise(floor(posCS.xy));

            // Near edge: fade in from previous LOD
            float fiW = _LODCrossfade.y - _LODCrossfade.x;
            if (fiW > 0.001)
            {
                float t = saturate((dist - _LODCrossfade.x) / fiW);
                clip(t + dither - 1.0);
            }

            // Far edge: fade out to next LOD
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

        // Billboard vertex transformation — rotates any mesh to face camera around Y axis.
        // Rebuilds object-to-world with billboard orientation, preserving position + scale.
        // Uses TransformObjectToWorld() for instancing/batching compatibility.
        // Works with any mesh layout (XY quads, XZ quads, cross-billboards, etc.).
        float3 BillboardWorldPos(float3 localPos)
        {
            // Instance world position — transform the origin through the standard API
            // so it works correctly with GPU instancing, SRP Batcher, and DrawMeshInstanced.
            float3 instancePos = TransformObjectToWorld(float3(0.0, 0.0, 0.0));

            // Uniform scale — length of the transformed X unit vector
            float scale = length(TransformObjectToWorld(float3(1.0, 0.0, 0.0)) - instancePos);

            // Camera direction in XZ plane
            float3 toCam = _WorldSpaceCameraPos.xyz - instancePos;
            toCam.y = 0.0;
            float dist = max(length(toCam), 0.001);
            toCam /= dist;

            // Billboard basis vectors (Y-axis aligned)
            float3 right   = normalize(cross(float3(0.0, 1.0, 0.0), toCam));
            float3 up      = float3(0.0, 1.0, 0.0);
            float3 forward = toCam;

            // Transform local position using billboard basis (all 3 axes)
            return instancePos
                + (right * localPos.x + up * localPos.y + forward * localPos.z) * scale;
        }

        // Extended version that also outputs the billboard tangent-space basis
        // for normal mapping. T = right, B = up, N = camera-facing direction.
        float3 BillboardWorldPosWithBasis(float3 localPos, out float3 tangentWS, out float3 normalWS)
        {
            float3 instancePos = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
            float scale = length(TransformObjectToWorld(float3(1.0, 0.0, 0.0)) - instancePos);

            float3 toCam = _WorldSpaceCameraPos.xyz - instancePos;
            toCam.y = 0.0;
            float dist = max(length(toCam), 0.001);
            toCam /= dist;

            float3 right = normalize(cross(float3(0.0, 1.0, 0.0), toCam));

            // Output billboard basis for TBN
            tangentWS = right;
            normalWS  = toCam;

            return instancePos
                + (right * localPos.x + float3(0.0, 1.0, 0.0) * localPos.y + toCam * localPos.z) * scale;
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
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _EXTRAMAP
            #pragma instancing_options assumeuniformscaling
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                #if defined(_NORMALMAP)
                half3  tangentWS  : TEXCOORD2;
                half3  normalWS   : TEXCOORD3;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                #if defined(_NORMALMAP)
                float3 tangent, normal;
                float3 posWS = BillboardWorldPosWithBasis(input.positionOS.xyz, tangent, normal);
                output.tangentWS = (half3)tangent;
                output.normalWS  = (half3)normal;
                #else
                float3 posWS = BillboardWorldPos(input.positionOS.xyz);
                #endif

                output.positionCS = TransformWorldToHClip(posWS);
                output.positionWS = posWS;
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                clip(tex.a - _Cutoff);
                ApplyLODCrossfade(input.positionWS, input.positionCS);
                ApplyThinningFade(input.positionCS);

                // Resolve normal — from normal map or fixed up vector
                half3 shadingNormal;
                #if defined(_NORMALMAP)
                {
                    half3 normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                    // Billboard TBN: T = right, B = up(0,1,0), N = toward camera
                    half3 T = normalize(input.tangentWS);
                    half3 N = normalize(input.normalWS);
                    half3 B = half3(0.0h, 1.0h, 0.0h);
                    shadingNormal = normalize(normalTS.x * T + normalTS.y * B + normalTS.z * N);
                }
                #else
                {
                    // Billboard face normal — toward camera in XZ plane
                    float3 toCam = _WorldSpaceCameraPos.xyz - input.positionWS;
                    toCam.y = 0.0;
                    shadingNormal = (half3)normalize(toCam);
                }
                #endif

                // Main directional light with shadows
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                // Half-Lambert with abs for two-sided lighting
                half NdotL = abs(dot(shadingNormal, mainLight.direction));
                NdotL = NdotL * 0.5h + 0.5h;
                half3 diffuse = tex.rgb * mainLight.color * NdotL * mainLight.shadowAttenuation;

                // Ambient from light probes
                half3 ambient = SampleSH(shadingNormal) * tex.rgb;

                #if defined(_EXTRAMAP)
                {
                    half3 extra = SAMPLE_TEXTURE2D(_ExtraTex, sampler_ExtraTex, input.uv).rgb;
                    half subsurface = extra.r;
                    half gloss      = extra.g;
                    half ao         = extra.b;

                    // Subsurface scattering — backlit glow (matches FoliageTree.shader)
                    half3 viewDirWS = (half3)normalize(GetCameraPositionWS() - input.positionWS);
                    half sss = saturate(dot(viewDirWS, -mainLight.direction)) * subsurface * _TranslucencyStrength;
                    half3 translucency = sss * _TranslucencyColor.rgb * tex.rgb * mainLight.color;

                    // Specular — Blinn-Phong modulated by gloss
                    half3 halfDir = normalize(mainLight.direction + viewDirWS);
                    half specPow = gloss * 128.0h + 2.0h;
                    half spec = pow(max(dot(shadingNormal, halfDir), 0.0h), specPow) * gloss;
                    half3 specular = spec * mainLight.color * mainLight.shadowAttenuation;

                    // AO darkens ambient
                    ambient *= ao;

                    return half4(diffuse + translucency + specular + ambient, 1.0h);
                }
                #endif

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
                float2 uv         : TEXCOORD0;
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

                float3 posWS = BillboardWorldPos(input.positionOS.xyz);
                float3 normalWS = float3(0, 0, 1); // billboard face normal (approximate)

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

                float3 posWS = BillboardWorldPos(input.positionOS.xyz);
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

        // ═══════════════════════════════════════════════
        //  PASS 4 – Depth Normals (SSAO, screen-space effects)
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma instancing_options assumeuniformscaling
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 posWS = BillboardWorldPos(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(posWS);
                output.positionWS = posWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthNormalsFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                ApplyLODCrossfade(input.positionWS, input.positionCS);
                ApplyThinningFade(input.positionCS);

                // Billboard face normal — toward camera in XZ plane
                float3 toCam = _WorldSpaceCameraPos.xyz - input.positionWS;
                toCam.y = 0.0;
                half3 normalWS = (half3)normalize(toCam);

                return half4(normalWS, 0.0h);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
