// Slope-based 3-layer tri-planar PBR terrain shader for URP (Unity 6 / URP 17).
// Projects textures along world X, Y, Z axes and blends by surface normal.
// Three material layers (Grass, Dirt, Rock) blend automatically based on surface slope.
//
// Slope model:
//   slope = 1 - worldNormal.y  (0 = flat, 1 = vertical wall)
//   Grass  — appears on flat surfaces (slope < GrassSlopeThreshold)
//   Rock   — appears on steep surfaces (slope > RockSlopeThreshold)
//   Dirt   — fills the transition between grass and rock
//
// Per-layer texture inputs (COL/N/MSO):
//   COL  – Albedo (RGB)
//   N    – Normal map (tangent-space, mark as "Normal Map" in import settings)
//   MSO  – Channel-packed: R = Metallic, G = Smoothness, B = Ambient Occlusion
//
// References:
//   URP ShaderLibrary   – https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/shading-model.html
//   SurfaceData/InputData – https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/use-built-in-shader-methods-lighting.html
//   Shadows              – https://docs.unity3d.com/6000.1/Documentation/Manual/urp/use-built-in-shader-methods-shadows.html
//   Tri-planar normals   – "Whiteout" method (GPU Gems / Ben Golus)

Shader "Custom/TriPlanar Lit"
{
    Properties
    {
        [Header(Grass Layer   Textures)]
        [NoScaleOffset] _GrassCOL ("COL (Albedo)",  2D) = "white" {}
        [NoScaleOffset][Normal] _GrassN ("N (Normal)",  2D) = "bump"  {}
        [NoScaleOffset] _GrassMSO ("MSO",              2D) = "white" {}
        [Header(Grass Layer   Settings)]
        _GrassTint     ("Tint",     Color)          = (1, 1, 1, 1)
        _GrassScale    ("Scale",    Range(0.01, 5)) = 1.0
        _GrassRotation   ("Rotation",   Range(0, 360))    = 0.0
        _GrassHueShift   ("Hue Shift",  Range(-0.5, 0.5)) = 0.0
        _GrassSaturation ("Saturation", Range(0, 2))       = 1.0
        _GrassLightness  ("Lightness",  Range(0, 2))       = 1.0
        _GrassContrast   ("Contrast",   Range(0, 2))       = 1.0
        [Tooltip(Slope value 0 to 1 above which grass fades out. 0 is flat and 1 is vertical.)]
        _GrassSlopeThreshold ("Slope Threshold", Range(0, 1)) = 0.3

        [Header(Dirt Layer   Textures)]
        [NoScaleOffset] _DirtCOL ("COL (Albedo)",  2D) = "white" {}
        [NoScaleOffset][Normal] _DirtN ("N (Normal)",  2D) = "bump"  {}
        [NoScaleOffset] _DirtMSO ("MSO",              2D) = "white" {}
        [Header(Dirt Layer   Settings)]
        _DirtTint     ("Tint",     Color)          = (1, 1, 1, 1)
        _DirtScale    ("Scale",    Range(0.01, 5)) = 1.0
        _DirtRotation   ("Rotation",   Range(0, 360))    = 0.0
        _DirtHueShift   ("Hue Shift",  Range(-0.5, 0.5)) = 0.0
        _DirtSaturation ("Saturation", Range(0, 2))       = 1.0
        _DirtLightness  ("Lightness",  Range(0, 2))       = 1.0
        _DirtContrast   ("Contrast",   Range(0, 2))       = 1.0

        [Header(Rock Layer   Textures)]
        [NoScaleOffset] _RockCOL ("COL (Albedo)",  2D) = "white" {}
        [NoScaleOffset][Normal] _RockN ("N (Normal)",  2D) = "bump"  {}
        [NoScaleOffset] _RockMSO ("MSO",              2D) = "white" {}
        [Header(Rock Layer   Settings)]
        _RockTint     ("Tint",     Color)          = (1, 1, 1, 1)
        _RockScale    ("Scale",    Range(0.01, 5)) = 1.0
        _RockRotation   ("Rotation",   Range(0, 360))    = 0.0
        _RockHueShift   ("Hue Shift",  Range(-0.5, 0.5)) = 0.0
        _RockSaturation ("Saturation", Range(0, 2))       = 1.0
        _RockLightness  ("Lightness",  Range(0, 2))       = 1.0
        _RockContrast   ("Contrast",   Range(0, 2))       = 1.0
        [Tooltip(Slope value 0 to 1 above which rock fades in. Should be greater than grass threshold.)]
        _RockSlopeThreshold  ("Slope Threshold",  Range(0, 1)) = 0.6

        [Header(Slope Blending)]
        [Tooltip(Width of the blend region between layers. Larger values give softer transitions.)]
        _SlopeBlendWidth     ("Blend Width",       Range(0.01, 0.5)) = 0.1
        [Tooltip(Contrast applied to the slope value before blending. Values above 1 push surfaces toward fully flat or fully steep.)]
        _SlopeContrast       ("Slope Contrast",    Range(0.1, 5))    = 1.0

        [Header(Global Adjustments)]
        _NormalIntensity     ("Normal Intensity",     Range(0, 2)) = 1.0
        _SmoothnessIntensity ("Smoothness Intensity", Range(0, 1)) = 1.0
        _AOStrength          ("AO Strength",          Range(0, 1)) = 1.0

        [Header(Triplanar)]
        _TriSharpness ("Blend Sharpness", Range(1, 20)) = 4.0

        [Header(Tiling Break Up)]
        [Tooltip(Rotation angle in degrees for the second sample. 0 disables break up entirely.)]
        _BreakupAngle    ("Rotation Angle",    Range(0, 180))     = 0.0
        [Tooltip(World space frequency of the blend noise. Smaller values produce larger patches.)]
        _BreakupScale    ("Patch Scale",       Range(0.001, 0.5)) = 0.05
        [Tooltip(Edge sharpness between rotated patches. 0 is a soft gradient and 1 is a hard cut.)]
        _BreakupSharpness("Edge Sharpness",    Range(0, 1))       = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }
        LOD 300

        // ─────────────────────────────────────────────
        // Shared declarations (injected into every pass)
        // ─────────────────────────────────────────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // ── Textures & samplers ──
        // Grass
        TEXTURE2D(_GrassCOL);  SAMPLER(sampler_GrassCOL);
        TEXTURE2D(_GrassN);    SAMPLER(sampler_GrassN);
        TEXTURE2D(_GrassMSO);  SAMPLER(sampler_GrassMSO);
        // Dirt
        TEXTURE2D(_DirtCOL);   SAMPLER(sampler_DirtCOL);
        TEXTURE2D(_DirtN);     SAMPLER(sampler_DirtN);
        TEXTURE2D(_DirtMSO);   SAMPLER(sampler_DirtMSO);
        // Rock
        TEXTURE2D(_RockCOL);   SAMPLER(sampler_RockCOL);
        TEXTURE2D(_RockN);     SAMPLER(sampler_RockN);
        TEXTURE2D(_RockMSO);   SAMPLER(sampler_RockMSO);

        // SRP-Batcher compatible per-material CBUFFER
        CBUFFER_START(UnityPerMaterial)
            half4  _GrassTint;
            float  _GrassScale;
            half   _GrassRotation;
            half   _GrassHueShift;
            half   _GrassSaturation;
            half   _GrassLightness;
            half   _GrassContrast;
            half4  _DirtTint;
            float  _DirtScale;
            half   _DirtRotation;
            half   _DirtHueShift;
            half   _DirtSaturation;
            half   _DirtLightness;
            half   _DirtContrast;
            half4  _RockTint;
            float  _RockScale;
            half   _RockRotation;
            half   _RockHueShift;
            half   _RockSaturation;
            half   _RockLightness;
            half   _RockContrast;
            half   _NormalIntensity;
            half   _SmoothnessIntensity;
            half   _AOStrength;
            half   _TriSharpness;
            half   _BreakupAngle;
            float  _BreakupScale;
            half   _BreakupSharpness;
            half   _GrassSlopeThreshold;
            half   _RockSlopeThreshold;
            half   _SlopeBlendWidth;
            half   _SlopeContrast;
        CBUFFER_END

        // ── RGB to HSL ──
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

        // ── Apply HSL + contrast adjustment to an RGB color ──
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

        // ── Tri-planar blend weights ──
        half3 TriplanarWeights(half3 worldNormal, half sharpness)
        {
            half3 w = pow(abs(worldNormal), sharpness);
            return w / (w.x + w.y + w.z + 0.0001h);
        }

        // ── Slope-based layer weights ──
        // slope: 0 = flat, 1 = vertical wall
        // Slope contrast remaps the slope curve around 0.5 midpoint before thresholding.
        void SlopeWeights(half slope, out half grassW, out half dirtW, out half rockW)
        {
            // Apply slope contrast — pushes values toward 0 or 1
            slope = saturate(lerp(0.5h, slope, _SlopeContrast));

            half bw = max(_SlopeBlendWidth, 0.001h);
            grassW = 1.0h - smoothstep(_GrassSlopeThreshold - bw, _GrassSlopeThreshold + bw, slope);
            rockW  = smoothstep(_RockSlopeThreshold - bw, _RockSlopeThreshold + bw, slope);
            dirtW  = saturate(1.0h - grassW - rockW);

            // Normalize to guarantee weights sum to 1
            half total = grassW + dirtW + rockW + 0.0001h;
            grassW /= total;
            dirtW  /= total;
            rockW  /= total;
        }

        // ── 2D UV rotation ──
        float2 RotateUV(float2 uv, float angleRad)
        {
            float s, c;
            sincos(angleRad, s, c);
            return float2(uv.x * c - uv.y * s,
                          uv.x * s + uv.y * c);
        }

        // ── Procedural gradient noise (used for break-up blend mask) ──
        float2 _NoiseHash(float2 p)
        {
            p = float2(dot(p, float2(127.1, 311.7)),
                        dot(p, float2(269.5, 183.3)));
            return -1.0 + 2.0 * frac(sin(p) * 43758.5453123);
        }

        float GradientNoise(float2 p)
        {
            float2 i = floor(p);
            float2 f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);

            float a = dot(_NoiseHash(i + float2(0.0, 0.0)), f - float2(0.0, 0.0));
            float b = dot(_NoiseHash(i + float2(1.0, 0.0)), f - float2(1.0, 0.0));
            float c = dot(_NoiseHash(i + float2(0.0, 1.0)), f - float2(0.0, 1.0));
            float d = dot(_NoiseHash(i + float2(1.0, 1.0)), f - float2(1.0, 1.0));

            return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
        }

        // ── Break-up blend mask ──
        // Returns 0..1 — how much of the rotated sample to use.
        half BreakupMask(float2 worldUV, float2 seed)
        {
            float n = GradientNoise(worldUV * _BreakupScale + seed);
            half  t = (half)n * 0.5h + 0.5h;                       // remap to [0,1]
            half  w = max(0.5h * (1.0h - _BreakupSharpness), 0.01h);
            return smoothstep(0.5h - w, 0.5h + w, t);
        }

        // ── Tri-planar sample one layer (COL + MSO + N) with optional break-up ──
        // Samples at base orientation, then blends with a rotated second sample
        // using a noise-driven patch mask when _BreakupAngle > 0.
        void SampleLayer(
            TEXTURE2D_PARAM(texCOL, sampCOL),
            TEXTURE2D_PARAM(texN,   sampN),
            TEXTURE2D_PARAM(texMSO, sampMSO),
            float3 worldPos, half3 worldNrm, half3 triW, float scale, half rotationDeg, half nrmIntensity,
            out half4 albedo, out half4 mso, out half3 nrmX, out half3 nrmY, out half3 nrmZ)
        {
            // Base UVs (with per-layer rotation)
            float rotRad = radians(rotationDeg);
            float2 rawX = worldPos.zy;
            float2 rawY = worldPos.xz;
            float2 rawZ = worldPos.xy;

            float2 uvX = RotateUV(rawX, rotRad) * scale;
            float2 uvY = RotateUV(rawY, rotRad) * scale;
            float2 uvZ = RotateUV(rawZ, rotRad) * scale;

            // Primary samples
            half4 colX = SAMPLE_TEXTURE2D(texCOL, sampCOL, uvX);
            half4 colY = SAMPLE_TEXTURE2D(texCOL, sampCOL, uvY);
            half4 colZ = SAMPLE_TEXTURE2D(texCOL, sampCOL, uvZ);

            half4 msoX = SAMPLE_TEXTURE2D(texMSO, sampMSO, uvX);
            half4 msoY = SAMPLE_TEXTURE2D(texMSO, sampMSO, uvY);
            half4 msoZ = SAMPLE_TEXTURE2D(texMSO, sampMSO, uvZ);

            nrmX = UnpackNormalScale(SAMPLE_TEXTURE2D(texN, sampN, uvX), nrmIntensity);
            nrmY = UnpackNormalScale(SAMPLE_TEXTURE2D(texN, sampN, uvY), nrmIntensity);
            nrmZ = UnpackNormalScale(SAMPLE_TEXTURE2D(texN, sampN, uvZ), nrmIntensity);

            // Break-up: blend with rotated second sample
            float breakRad = radians(_BreakupAngle);
            [branch] if (breakRad > 0.001)
            {
                float2 buvX = RotateUV(rawX, rotRad + breakRad) * scale;
                float2 buvY = RotateUV(rawY, rotRad + breakRad) * scale;
                float2 buvZ = RotateUV(rawZ, rotRad + breakRad) * scale;

                // Per-axis blend masks (unique seeds prevent lock-step)
                half mX = BreakupMask(rawX, float2( 7.0, 13.0));
                half mY = BreakupMask(rawY, float2(31.0, 59.0));
                half mZ = BreakupMask(rawZ, float2(73.0, 97.0));

                colX = lerp(colX, SAMPLE_TEXTURE2D(texCOL, sampCOL, buvX), mX);
                colY = lerp(colY, SAMPLE_TEXTURE2D(texCOL, sampCOL, buvY), mY);
                colZ = lerp(colZ, SAMPLE_TEXTURE2D(texCOL, sampCOL, buvZ), mZ);

                msoX = lerp(msoX, SAMPLE_TEXTURE2D(texMSO, sampMSO, buvX), mX);
                msoY = lerp(msoY, SAMPLE_TEXTURE2D(texMSO, sampMSO, buvY), mY);
                msoZ = lerp(msoZ, SAMPLE_TEXTURE2D(texMSO, sampMSO, buvZ), mZ);

                nrmX = lerp(nrmX, UnpackNormalScale(SAMPLE_TEXTURE2D(texN, sampN, buvX), nrmIntensity), mX);
                nrmY = lerp(nrmY, UnpackNormalScale(SAMPLE_TEXTURE2D(texN, sampN, buvY), nrmIntensity), mY);
                nrmZ = lerp(nrmZ, UnpackNormalScale(SAMPLE_TEXTURE2D(texN, sampN, buvZ), nrmIntensity), mZ);
            }

            // Tri-planar blend
            albedo = colX * triW.x + colY * triW.y + colZ * triW.z;
            mso    = msoX * triW.x + msoY * triW.y + msoZ * triW.z;
        }

        // ── Whiteout tri-planar normal blend ──
        half3 BlendTriplanarNormals(half3 nrmX, half3 nrmY, half3 nrmZ, half3 worldNrm, half3 triW)
        {
            nrmX = half3(nrmX.xy + worldNrm.zy, abs(nrmX.z) * worldNrm.x);
            nrmY = half3(nrmY.xy + worldNrm.xz, abs(nrmY.z) * worldNrm.y);
            nrmZ = half3(nrmZ.xy + worldNrm.xy, abs(nrmZ.z) * worldNrm.z);

            return normalize(
                nrmX.zyx * triW.x +
                nrmY.xzy * triW.y +
                nrmZ.xyz * triW.z
            );
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
                float2 lightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half   fogFactor  : TEXCOORD2;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 3);
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
                VertexNormalInputs   nrmInputs = GetVertexNormalInputs(input.normalOS);

                o.positionCS = posInputs.positionCS;
                o.positionWS = posInputs.positionWS;
                o.normalWS   = nrmInputs.normalWS;
                o.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);

                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, o.lightmapUV);
                OUTPUT_SH(o.normalWS, o.vertexSH);

                return o;
            }

            half4 ForwardLitFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 worldPos = input.positionWS;
                half3  worldNrm = normalize(input.normalWS);
                half3  triW     = TriplanarWeights(worldNrm, _TriSharpness);

                // ── Slope weights ──
                half slope = 1.0h - saturate(worldNrm.y);
                half grassW, dirtW, rockW;
                SlopeWeights(slope, grassW, dirtW, rockW);

                // ── Sample each layer ──
                half4 grassAlbedo, grassMSO;
                half3 grassNX, grassNY, grassNZ;
                SampleLayer(
                    TEXTURE2D_ARGS(_GrassCOL, sampler_GrassCOL),
                    TEXTURE2D_ARGS(_GrassN,   sampler_GrassN),
                    TEXTURE2D_ARGS(_GrassMSO, sampler_GrassMSO),
                    worldPos, worldNrm, triW, _GrassScale, _GrassRotation, _NormalIntensity,
                    grassAlbedo, grassMSO, grassNX, grassNY, grassNZ);

                half4 dirtAlbedo, dirtMSO;
                half3 dirtNX, dirtNY, dirtNZ;
                SampleLayer(
                    TEXTURE2D_ARGS(_DirtCOL, sampler_DirtCOL),
                    TEXTURE2D_ARGS(_DirtN,   sampler_DirtN),
                    TEXTURE2D_ARGS(_DirtMSO, sampler_DirtMSO),
                    worldPos, worldNrm, triW, _DirtScale, _DirtRotation, _NormalIntensity,
                    dirtAlbedo, dirtMSO, dirtNX, dirtNY, dirtNZ);

                half4 rockAlbedo, rockMSO;
                half3 rockNX, rockNY, rockNZ;
                SampleLayer(
                    TEXTURE2D_ARGS(_RockCOL, sampler_RockCOL),
                    TEXTURE2D_ARGS(_RockN,   sampler_RockN),
                    TEXTURE2D_ARGS(_RockMSO, sampler_RockMSO),
                    worldPos, worldNrm, triW, _RockScale, _RockRotation, _NormalIntensity,
                    rockAlbedo, rockMSO, rockNX, rockNY, rockNZ);

                // ── Per-layer HSL + contrast adjustment (applied before slope blend) ──
                half3 gCol = AdjustHSLC(grassAlbedo.rgb * _GrassTint.rgb,
                    _GrassHueShift, _GrassSaturation, _GrassLightness, _GrassContrast);
                half3 dCol = AdjustHSLC(dirtAlbedo.rgb * _DirtTint.rgb,
                    _DirtHueShift, _DirtSaturation, _DirtLightness, _DirtContrast);
                half3 rCol = AdjustHSLC(rockAlbedo.rgb * _RockTint.rgb,
                    _RockHueShift, _RockSaturation, _RockLightness, _RockContrast);

                // ── Blend layers by slope ──
                half4 albedo = half4(gCol * grassW + dCol * dirtW + rCol * rockW, 1.0h);

                half4 mso = grassMSO * grassW + dirtMSO * dirtW + rockMSO * rockW;

                half metallic   = mso.r;
                half smoothness = mso.g * _SmoothnessIntensity;
                half occlusion  = lerp(1.0h, mso.b, _AOStrength);

                // ── Blend normals per tri-planar axis, then whiteout blend ──
                half3 blendNX = grassNX * grassW + dirtNX * dirtW + rockNX * rockW;
                half3 blendNY = grassNY * grassW + dirtNY * dirtW + rockNY * rockW;
                half3 blendNZ = grassNZ * grassW + dirtNZ * dirtW + rockNZ * rockW;

                half3 normal = BlendTriplanarNormals(blendNX, blendNY, blendNZ, worldNrm, triW);

                // ── Build InputData ──
                // See: https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.0/manual/use-built-in-shader-methods-lighting.html
                InputData inputData = (InputData)0;
                inputData.positionWS              = worldPos;
                inputData.positionCS              = input.positionCS;
                inputData.normalWS                = normal;
                inputData.viewDirectionWS         = GetWorldSpaceNormalizeViewDir(worldPos);
                inputData.fogCoord                = input.fogFactor;
                inputData.bakedGI                 = SAMPLE_GI(input.lightmapUV, input.vertexSH, normal);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask              = SAMPLE_SHADOWMASK(input.lightmapUV);

                #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = TransformWorldToShadowCoord(worldPos);
                #endif

                // ── Build SurfaceData ──
                SurfaceData surfaceData    = (SurfaceData)0;
                surfaceData.albedo         = albedo.rgb;
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            half4 DepthNormalsFrag(Varyings input) : SV_Target
            {
                float3 worldPos = input.positionWS;
                half3  worldNrm = normalize(input.normalWS);
                half3  triW     = TriplanarWeights(worldNrm, _TriSharpness);

                // Slope weights
                half slope = 1.0h - saturate(worldNrm.y);
                half grassW, dirtW, rockW;
                SlopeWeights(slope, grassW, dirtW, rockW);

                // Sample normals for each layer
                half4 unusedAlbedo, unusedMSO;
                half3 grassNX, grassNY, grassNZ;
                SampleLayer(
                    TEXTURE2D_ARGS(_GrassCOL, sampler_GrassCOL),
                    TEXTURE2D_ARGS(_GrassN,   sampler_GrassN),
                    TEXTURE2D_ARGS(_GrassMSO, sampler_GrassMSO),
                    worldPos, worldNrm, triW, _GrassScale, _GrassRotation, _NormalIntensity,
                    unusedAlbedo, unusedMSO, grassNX, grassNY, grassNZ);

                half3 dirtNX, dirtNY, dirtNZ;
                SampleLayer(
                    TEXTURE2D_ARGS(_DirtCOL, sampler_DirtCOL),
                    TEXTURE2D_ARGS(_DirtN,   sampler_DirtN),
                    TEXTURE2D_ARGS(_DirtMSO, sampler_DirtMSO),
                    worldPos, worldNrm, triW, _DirtScale, _DirtRotation, _NormalIntensity,
                    unusedAlbedo, unusedMSO, dirtNX, dirtNY, dirtNZ);

                half3 rockNX, rockNY, rockNZ;
                SampleLayer(
                    TEXTURE2D_ARGS(_RockCOL, sampler_RockCOL),
                    TEXTURE2D_ARGS(_RockN,   sampler_RockN),
                    TEXTURE2D_ARGS(_RockMSO, sampler_RockMSO),
                    worldPos, worldNrm, triW, _RockScale, _RockRotation, _NormalIntensity,
                    unusedAlbedo, unusedMSO, rockNX, rockNY, rockNZ);

                // Blend normals by slope, then whiteout tri-planar blend
                half3 blendNX = grassNX * grassW + dirtNX * dirtW + rockNX * rockW;
                half3 blendNY = grassNY * grassW + dirtNY * dirtW + rockNY * rockW;
                half3 blendNZ = grassNZ * grassW + dirtNZ * dirtW + rockNZ * rockW;

                half3 normal = BlendTriplanarNormals(blendNX, blendNY, blendNZ, worldNrm, triW);

                return half4(normal, 0.0h);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
