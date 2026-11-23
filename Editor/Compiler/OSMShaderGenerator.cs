using System.Text;

namespace OSMWorkflow
{
    /// <summary>
    /// Produces ShaderLab/HLSL source from a compiled context. Two outputs share the same node code:
    /// the URP Lit-style surface shader and a full-screen evaluation shader used for baking.
    /// Templates use ' instead of " for readability; apostrophes are converted when the template is expanded.
    /// </summary>
    public static class OSMShaderGenerator
    {
        // ------------------------------------------------------------------------------------------------
        //  URP Lit-style opaque shader (explicit OSM channel sampling)
        // ------------------------------------------------------------------------------------------------

        public static string GenerateLit(OSMEmitContext ctx, OSMCompileOptions o)
        {
            var props = new StringBuilder();
            var cbuf = new StringBuilder();
            var texs = new StringBuilder();

            foreach (var b in ctx.Bindings)
            {
                props.Append("        ").Append(PropertyLine(b)).Append('\n');
                switch (b.Kind)
                {
                    case OSMBindingKind.Texture:
                        texs.Append("        TEXTURE2D(").Append(b.Name).Append(");\n");
                        texs.Append("        SAMPLER(sampler").Append(b.Name).Append(");\n");
                        break;
                    case OSMBindingKind.Float:
                        cbuf.Append("        float ").Append(b.Name).Append(";\n");
                        break;
                    default:
                        cbuf.Append("        float4 ").Append(b.Name).Append(";\n");
                        break;
                }
            }
            if (o.IncludeDebug)
            {
                props.Append("        _OSM_Debug('OSM Debug View', Float) = 0\n");
                cbuf.Append("        float _OSM_Debug;\n");
            }
            if (cbuf.Length == 0) cbuf.Append("        float _OSM_Pad;\n");

            var s = ctx.Surface;
            string src = LitTemplate
                .Replace("%%NAME%%", o.ShaderName)
                .Replace("%%PROPERTIES%%", props.ToString())
                .Replace("%%CBUFFER%%", cbuf.ToString())
                .Replace("%%TEXTURES%%", texs.ToString())
                .Replace("%%CULL%%", s.TwoSided ? "Off" : "Back")
                .Replace("%%FACEPARAM%%", s.TwoSided ? ", FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC" : "")
                .Replace("%%FACEFLIP%%", s.TwoSided ? "normalWS *= IS_FRONT_VFACE(facing, 1.0, -1.0);" : "")
                .Replace("%%ALPHAFUNC%%", BuildAlphaFunc(s))
                .Replace("%%CUTOFF%%", s.CutoffProp ?? "0.5")
                .Replace("%%QUEUE%%", s.AlphaClip ? "AlphaTest" : "Geometry")
                .Replace("%%RENDERTYPE%%", s.AlphaClip ? "TransparentCutout" : "Opaque")
                .Replace("%%PARALLAXSETUP%%", s.UsesParallax
                    ? "float3 osm_viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);\n                float3 osm_viewDirTS = float3(dot(osm_viewDirWS, normalize(input.tangentWS.xyz)), dot(osm_viewDirWS, normalize(osm_bitangentWS)), dot(osm_viewDirWS, normalize(input.normalWS.xyz)));"
                    : "")
                .Replace("%%DEBUGDEFINE%%", o.IncludeDebug ? "#define OSM_DEBUG 1" : "")
                .Replace("%%GRAPH%%", ctx.Code)
                .Replace("%%BASECOLOR%%", s.BaseColor)
                .Replace("%%NORMAL%%", s.Normal)
                .Replace("%%OSM%%", s.Osm)
                .Replace("%%EMISSION%%", s.Emission);
            // The preview does not need shadow/depth passes: leaving them out makes the shader parse and compile faster.
            if (o.IncludeDebug)
                src = System.Text.RegularExpressions.Regex.Replace(src, @"//EXTRA_BEGIN.*?//EXTRA_END\n?", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            src = src.Replace("//EXTRA_BEGIN\n", "").Replace("//EXTRA_END\n", "");
            return src.Replace('\'', '"');
        }

        static string BuildAlphaFunc(OSMSurface s)
        {
            if (!s.AlphaClip) return "";
            return "#define OSM_ALPHACLIP 1\n        float OSM_EvalAlpha(float2 uv)\n        {\n" + (s.AlphaCode ?? "") + "            return " + (s.AlphaExpr ?? "1.0") + ";\n        }\n";
        }

        // ------------------------------------------------------------------------------------------------
        //  Full-screen evaluation shader used for baking and comparison
        // ------------------------------------------------------------------------------------------------

        public static string GenerateBake(OSMEmitContext ctx, string shaderName, string finalExpr)
        {
            var props = new StringBuilder();
            var decls = new StringBuilder();
            foreach (var b in ctx.Bindings)
            {
                props.Append("        ").Append(PropertyLine(b)).Append('\n');
                switch (b.Kind)
                {
                    case OSMBindingKind.Texture: decls.Append("        sampler2D ").Append(b.Name).Append(";\n"); break;
                    case OSMBindingKind.Float: decls.Append("        float ").Append(b.Name).Append(";\n"); break;
                    default: decls.Append("        float4 ").Append(b.Name).Append(";\n"); break;
                }
            }
            string src = BakeTemplate
                .Replace("%%NAME%%", shaderName)
                .Replace("%%PROPERTIES%%", props.ToString())
                .Replace("%%DECLS%%", decls.ToString())
                .Replace("%%GRAPH%%", ctx.Code)
                .Replace("%%FINAL%%", finalExpr);
            return src.Replace('\'', '"');
        }

        // ------------------------------------------------------------------------------------------------

        static string PropertyLine(OSMBinding b)
        {
            string label = (b.Label ?? b.Name).Replace('"', '\'').Replace('\'', ' ');
            switch (b.Kind)
            {
                case OSMBindingKind.Texture:
                    return "[NoScaleOffset] " + b.Name + "('" + label + "', 2D) = '" + b.DefaultTexture + "' {}";
                case OSMBindingKind.Float:
                    return b.Name + "('" + label + "', Float) = " + OSMPortTypes.F(b.Float);
                case OSMBindingKind.Vector:
                    return b.Name + "('" + label + "', Vector) = (" + OSMPortTypes.F(b.Vector.x) + "," + OSMPortTypes.F(b.Vector.y) + ","
                           + OSMPortTypes.F(b.Vector.z) + "," + OSMPortTypes.F(b.Vector.w) + ")";
                default:
                    return (b.Hdr ? "[HDR] " : "") + b.Name + "('" + label + "', Color) = (" + OSMPortTypes.F(b.Vector.x) + "," + OSMPortTypes.F(b.Vector.y) + ","
                           + OSMPortTypes.F(b.Vector.z) + "," + OSMPortTypes.F(b.Vector.w) + ")";
            }
        }

        // ================================================================================================
        //  Templates
        // ================================================================================================

        const string LitTemplate = @"// Generated by OSM Workflow. Do not edit: changes are overwritten when the graph is compiled.
// OSM convention: R = Ambient Occlusion, G = Smoothness, B = Metallic, A = reserved (1).
Shader '%%NAME%%'
{
    Properties
    {
%%PROPERTIES%%
    }

    SubShader
    {
        Tags { 'RenderType' = '%%RENDERTYPE%%' 'RenderPipeline' = 'UniversalPipeline' 'UniversalMaterialType' = 'Lit' 'Queue' = '%%QUEUE%%' }
        LOD 300

        HLSLINCLUDE
        #include 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl'

        CBUFFER_START(UnityPerMaterial)
%%CBUFFER%%
        CBUFFER_END

%%TEXTURES%%
        #define OSM_SAMPLE(t, uv) SAMPLE_TEXTURE2D(t, sampler##t, uv)
        #define OSM_UnpackNormal(c, s) UnpackNormalScale(c, s)
        float3 OSM_RawNormal(float4 c, float s) { float3 n = c.xyz * 2.0 - 1.0; n.xy *= s; return normalize(n); }
        float2 OSM_ParallaxOffset(float h, float amplitude, float3 viewDirTS)
        {
            h = h * amplitude - amplitude * 0.5;
            float3 v = normalize(viewDirTS);
            v.z += 0.42;
            return h * (v.xy / v.z);
        }
%%ALPHAFUNC%%
        ENDHLSL

        Pass
        {
            Name 'OSMForward'
            Tags { 'LightMode' = 'UniversalForwardOnly' }
            Cull %%CULL%%
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #include_with_pragmas 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl'
            #include_with_pragmas 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl'

            #include 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl'
            #include 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl'

            %%DEBUGDEFINE%%

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 texcoord   : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float4 tangentWS  : TEXCOORD3;
                half4  fogFactorAndVertexLight : TEXCOORD4;
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD5;
            #endif
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
            #if defined(USE_APV_PROBE_OCCLUSION)
                float4 probeOcclusion : TEXCOORD9;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                half fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
            #else
                half3 vertexLight = half3(0.0, 0.0, 0.0);
            #endif

                output.uv = input.texcoord;
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.tangentWS = float4(normalInput.tangentWS.xyz, input.tangentOS.w * GetOddNegativeScale());
                output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);

                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
            #if defined(OUTPUT_SH4)
                float4 probeOccVS = float4(1.0, 1.0, 1.0, 1.0);
                OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, probeOccVS);
            #if defined(USE_APV_PROBE_OCCLUSION)
                output.probeOcclusion = probeOccVS;
            #endif
            #else
                OUTPUT_SH(output.normalWS.xyz, output.vertexSH);
            #endif

            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(vertexInput);
            #endif
                return output;
            }

            half4 Frag(Varyings input %%FACEPARAM%%) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.uv;

                float osm_sgn = input.tangentWS.w;
                float3 osm_bitangentWS = osm_sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
                half3x3 tbn = half3x3(input.tangentWS.xyz, osm_bitangentWS, input.normalWS.xyz);
                %%PARALLAXSETUP%%

                // ---------------- generated graph ----------------
%%GRAPH%%
                // -------------------------------------------------
            #if defined(OSM_ALPHACLIP)
                clip(OSM_EvalAlpha(uv) - %%CUTOFF%%);
            #endif
                float3 osm_base = %%BASECOLOR%%;
                float3 osm_normalTS = %%NORMAL%%;
                float4 osm_packed = %%OSM%%;
                float3 osm_emission = %%EMISSION%%;
                float osm_ao = saturate(osm_packed.r);
                float osm_smoothness = saturate(osm_packed.g);
                float osm_metallic = saturate(osm_packed.b);

            #if defined(OSM_DEBUG)
                if (_OSM_Debug > 0.5)
                {
                    float3 dbg = osm_base;
                    if (_OSM_Debug < 1.5) dbg = osm_ao.xxx;
                    else if (_OSM_Debug < 2.5) dbg = osm_smoothness.xxx;
                    else if (_OSM_Debug < 3.5) dbg = osm_metallic.xxx;
                    else if (_OSM_Debug < 4.5) dbg = normalize(osm_normalTS) * 0.5 + 0.5;
            #if !defined(UNITY_COLORSPACE_GAMMA)
                    dbg = SRGBToLinear(saturate(dbg));
            #endif
                    return half4(dbg, 1.0);
                }
            #endif

                float3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(osm_normalTS, tbn));
                %%FACEFLIP%%

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = osm_base;
                surfaceData.specular = half3(0.0, 0.0, 0.0);
                surfaceData.metallic = osm_metallic;
                surfaceData.smoothness = osm_smoothness;
                surfaceData.normalTS = osm_normalTS;
                surfaceData.emission = osm_emission;
                surfaceData.occlusion = osm_ao;
                surfaceData.alpha = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = input.shadowCoord;
            #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
            #endif
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
                inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            #if defined(USE_APV_PROBE_OCCLUSION)
                inputData.probeOcclusion = input.probeOcclusion;
            #endif
            #if !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
            #if defined(USE_APV_PROBE_OCCLUSION)
                float4 probeOccFS = input.probeOcclusion;
            #else
                float4 probeOccFS = float4(1.0, 1.0, 1.0, 1.0);
            #endif
                inputData.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(inputData.positionWS), inputData.normalWS,
                                              inputData.viewDirectionWS, inputData.positionCS.xy, probeOccFS, inputData.shadowMask);
            #else
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
            #endif

            #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
            #endif

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1.0;
                return color;
            }
            ENDHLSL
        }

//EXTRA_BEGIN
        Pass
        {
            Name 'ShadowCaster'
            Tags { 'LightMode' = 'ShadowCaster' }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull %%CULL%%

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl'

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                output.positionCS = positionCS;
                output.uv = input.texcoord;
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
            #if defined(OSM_ALPHACLIP)
                clip(OSM_EvalAlpha(input.uv) - %%CUTOFF%%);
            #endif
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name 'DepthOnly'
            Tags { 'LightMode' = 'DepthOnly' }
            ZWrite On
            ColorMask R
            Cull %%CULL%%

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 texcoord   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.texcoord;
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
            #if defined(OSM_ALPHACLIP)
                clip(OSM_EvalAlpha(input.uv) - %%CUTOFF%%);
            #endif
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name 'DepthNormals'
            Tags { 'LightMode' = 'DepthNormals' }
            ZWrite On
            Cull %%CULL%%

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _WRITE_RENDERING_LAYERS
            #include_with_pragmas 'Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl'

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 texcoord   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD0;
            };

            Varyings NormalsVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.texcoord;
                return output;
            }

            void NormalsFrag(Varyings input, out half4 outNormalWS : SV_Target0
            #if defined(_WRITE_RENDERING_LAYERS)
                , out float4 outRenderingLayers : SV_Target1
            #endif
            )
            {
            #if defined(OSM_ALPHACLIP)
                clip(OSM_EvalAlpha(input.uv) - %%CUTOFF%%);
            #endif
                float3 n = NormalizeNormalPerPixel(input.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct = PackNormalOctQuadEncode(n);
                float2 remapped = saturate(oct * 0.5 + 0.5);
                outNormalWS = half4(PackFloat2To888(remapped), 0.0);
            #else
                outNormalWS = half4(n, 0.0);
            #endif
            #if defined(_WRITE_RENDERING_LAYERS)
                outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 0);
            #endif
            }
            ENDHLSL
        }
//EXTRA_END
    }

    Fallback 'Hidden/Universal Render Pipeline/FallbackError'
}
";

        const string BakeTemplate = @"// Generated by OSM Workflow (evaluation shader used for baking and comparison).
Shader '%%NAME%%'
{
    Properties
    {
%%PROPERTIES%%
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include 'UnityCG.cginc'

%%DECLS%%
            #define OSM_SAMPLE(t, uv) tex2D(t, uv)
            float3 OSM_UnpackNormal(float4 c, float s)
            {
                float3 n = UnpackNormal(c);
                n.xy *= s;
                n.z = sqrt(saturate(1.0 - dot(n.xy, n.xy)));
                return n;
            }
            float3 OSM_RawNormal(float4 c, float s) { float3 n = c.xyz * 2.0 - 1.0; n.xy *= s; return normalize(n); }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
%%GRAPH%%
                return %%FINAL%%;
            }
            ENDCG
        }
    }
}
";
    }
}
