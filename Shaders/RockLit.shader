// Simple PBR lit shader for rock assets — URP (Unity 6 / URP 17).
// Standard UV-based sampling with three texture inputs:
//   COL  – Albedo (RGB) with color correction (tint, hue shift, saturation, lightness, contrast)
//   NOR  – Normal map (tangent-space, mark as "Normal Map" in import settings)
//   MSO  – Channel-packed: R = Metallic, G = Smoothness, B = Ambient Occlusion
//
// References:
//   URP ShaderLibrary    – https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/shading-model.html
//   SurfaceData/InputData – https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/use-built-in-shader-methods-lighting.html
//   Shadows               – https://docs.unity3d.com/6000.1/Documentation/Manual/urp/use-built-in-shader-methods-shadows.html

Shader "Custom/Rock Lit"
{
    Properties
    {
        [Header(Textures)]
        _COL ("COL (Albedo)",  2D) = "white" {}
        [Normal] _NOR ("NOR (Normal)",  2D) = "bump"  {}
        _MSO ("MSO (Metallic Smoothness AO)", 2D) = "white" {}

        [Header(Color Correction)]
        _Tint           ("Tint",       Color)             = (1, 1, 1, 1)
        _HueShift       ("Hue Shift",  Range(-0.5, 0.5))  = 0.0
        _Saturation     ("Saturation", Range(0, 2))        = 1.0
        _Lightness      ("Lightness",  Range(0, 2))        = 1.0
        _Contrast       ("Contrast",   Range(0, 2))        = 1.0

        [Header(Surface)]
        _NormalIntensity     ("Normal Intensity",     Range(0, 2)) = 1.0
        _SmoothnessIntensity ("Smoothness Intensity", Range(0, 2)) = 1.0
        _AOStrength          ("AO Strength",          Range(0, 1)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }
        LOD 200

        // ─────────────────────────────────────────────
        // Shared declarations (injected into every pass)
        // ─────────────────────────────────────────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // ── Textures & samplers ──
        TEXTURE2D(_COL);  SAMPLER(sampler_COL);
        TEXTURE2D(_NOR);  SAMPLER(sampler_NOR);
        TEXTURE2D(_MSO);  SAMPLER(sampler_MSO);

        // SRP-Batcher compatible per-material CBUFFER
        CBUFFER_START(UnityPerMaterial)
            float4 _COL_ST;
            float4 _NOR_ST;
            float4 _MSO_ST;
            half4  _Tint;
            half   _HueShift;
            half   _Saturation;
            half   _Lightness;
            half   _Contrast;
            half   _NormalIntensity;
            half   _SmoothnessIntensity;
            half   _AOStrength;
        CBUFFER_END

        // ── RGB ↔ HSL color correction ──
        half3 RGBtoHSL(half3 c)
        {
            half cMax = max(c.r, max(c.g, c.b));
            half cMin = min(c.r, min(c.g, c.b));
            half delta = cMax - cMin;

            half l = (cMax + cMin) * 0.5h;
            half s = 0.0h;
            half h = 0.0h;

            if (delta > 0.0001h)
            {
                s = (l < 0.5h) ? delta / (cMax + cMin) : delta / (2.0h - cMax - cMin);

                if (cMax == c.r)
                    h = (c.g - c.b) / delta + (c.g < c.b ? 6.0h : 0.0h);
                else if (cMax == c.g)
                    h = (c.b - c.r) / delta + 2.0h;
                else
                    h = (c.r - c.g) / delta + 4.0h;

                h /= 6.0h;
            }

            return half3(h, s, l);
        }

        half _HueToRGB(half p, half q, half t)
        {
            if (t < 0.0h) t += 1.0h;
            if (t > 1.0h) t -= 1.0h;
            if (t < 1.0h / 6.0h) return p + (q - p) * 6.0h * t;
            if (t < 0.5h) return q;
            if (t < 2.0h / 3.0h) return p + (q - p) * (2.0h / 3.0h - t) * 6.0h;
            return p;
        }

        half3 HSLtoRGB(half3 hsl)
        {
            half h = hsl.x;
            half s = hsl.y;
            half l = hsl.z;

            if (s < 0.0001h)
                return (half3)l;

            half q = (l < 0.5h) ? l * (1.0h + s) : l + s - l * s;
            half p = 2.0h * l - q;

            return half3(
                _HueToRGB(p, q, h + 1.0h / 3.0h),
                _HueToRGB(p, q, h),
                _HueToRGB(p, q, h - 1.0h / 3.0h)
            );
        }

        half3 AdjustHSLC(half3 rgb, half hueShift, half satMul, half lightMul, half contrast)
        {
            half3 hsl = RGBtoHSL(rgb);
            hsl.x = frac(hsl.x + hueShift);
            hsl.y = saturate(hsl.y * satMul);
            hsl.z = saturate(hsl.z * lightMul);
            half3 col = HSLtoRGB(hsl);
            col = saturate(lerp(0.5h, col, contrast));
            return col;
        }
        ENDHLSL

        // ═══════════════════════════════════════════════
        //  PASS 1 – Forward Lit
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   ForwardLitVert
            #pragma fragment ForwardLitFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half4  tangentWS  : TEXCOORD2;
                float2 uv         : TEXCOORD3;
                half   fogFactor  : TEXCOORD4;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 5);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ForwardLitVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs   nrmInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                o.positionCS = posInputs.positionCS;
                o.positionWS = posInputs.positionWS;
                o.normalWS   = nrmInputs.normalWS;
                o.tangentWS  = half4(nrmInputs.tangentWS, input.tangentOS.w);
                o.uv         = TRANSFORM_TEX(input.uv, _COL);
                o.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);

                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, o.lightmapUV);
                OUTPUT_SH(o.normalWS, o.vertexSH);

                return o;
            }

            half4 ForwardLitFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // ── Sample textures ──
                half4 colTex = SAMPLE_TEXTURE2D(_COL, sampler_COL, input.uv);
                half4 msoTex = SAMPLE_TEXTURE2D(_MSO, sampler_MSO, input.uv);
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NOR, sampler_NOR, input.uv), _NormalIntensity);

                // ── Color correction ──
                half3 albedo = AdjustHSLC(colTex.rgb * _Tint.rgb,
                    _HueShift, _Saturation, _Lightness, _Contrast);

                // ── MSO unpacking ──
                half metallic   = msoTex.r;
                half smoothness = msoTex.g * _SmoothnessIntensity;
                half occlusion  = lerp(1.0h, msoTex.b, _AOStrength);

                // ── Transform normal from tangent to world space ──
                half3 normalWS = normalize(input.normalWS);
                half3 tangentWS = normalize(input.tangentWS.xyz);
                half3 bitangentWS = cross(normalWS, tangentWS) * input.tangentWS.w;
                half3x3 TBN = half3x3(tangentWS, bitangentWS, normalWS);
                half3 normal = normalize(mul(normalTS, TBN));

                // ── Build InputData ──
                InputData inputData = (InputData)0;
                inputData.positionWS              = input.positionWS;
                inputData.positionCS              = input.positionCS;
                inputData.normalWS                = normal;
                inputData.viewDirectionWS         = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.fogCoord                = input.fogFactor;
                inputData.bakedGI                 = SAMPLE_GI(input.lightmapUV, input.vertexSH, normal);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask              = SAMPLE_SHADOWMASK(input.lightmapUV);

                #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif

                // ── Build SurfaceData ──
                SurfaceData surfaceData    = (SurfaceData)0;
                surfaceData.albedo         = albedo;
                surfaceData.metallic       = metallic;
                surfaceData.smoothness     = smoothness;
                surfaceData.occlusion      = occlusion;
                surfaceData.normalTS       = half3(0, 0, 1);
                surfaceData.alpha          = 1.0h;

                // ── Lighting ──
                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb   = MixFog(color.rgb, input.fogFactor);

                return color;
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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 GetShadowPositionHClip(float3 positionWS, float3 normalWS)
            {
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 posCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));

                #if UNITY_REVERSED_Z
                    posCS.z = min(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    posCS.z = max(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return posCS;
            }

            Varyings ShadowVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 nrmWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = GetShadowPositionHClip(posWS, nrmWS);
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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

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
        //  PASS 4 – Depth Normals  (SSAO, depth-normals prepass)
        // ═══════════════════════════════════════════════
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS   : TEXCOORD0;
                half4  tangentWS  : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexNormalInputs nrmInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS   = nrmInputs.normalWS;
                o.tangentWS  = half4(nrmInputs.tangentWS, input.tangentOS.w);
                o.uv         = TRANSFORM_TEX(input.uv, _COL);
                return o;
            }

            half4 DepthNormalsFrag(Varyings input) : SV_Target
            {
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NOR, sampler_NOR, input.uv), _NormalIntensity);

                half3 normalWS = normalize(input.normalWS);
                half3 tangentWS = normalize(input.tangentWS.xyz);
                half3 bitangentWS = cross(normalWS, tangentWS) * input.tangentWS.w;
                half3x3 TBN = half3x3(tangentWS, bitangentWS, normalWS);
                half3 normal = normalize(mul(normalTS, TBN));

                return half4(normal, 0.0h);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
