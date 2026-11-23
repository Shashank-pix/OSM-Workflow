using System.Collections.Generic;
using UnityEngine;

namespace OSMWorkflow
{
    // =====================================================================================
    //  Shared helpers
    // =====================================================================================

    static class NodeCommon
    {
        public static readonly string[] ChannelNames = { "R", "G", "B", "A" };
        public static readonly string[] ChannelSwizzle = { ".r", ".g", ".b", ".a" };
        public static readonly string[] TypeNames = { "Float", "Vector3", "Vector4" };

        public static OSMParamDef Tiling() { return OSMParamDef.Vec2("tiling", "Tiling", Vector2.one, "UV scale. Exposed as a material property."); }
        public static OSMParamDef Offset() { return OSMParamDef.Vec2("offset", "Offset", Vector2.zero, "UV offset. Exposed as a material property."); }

        public static OSMPortType TypeOf(OSMNodeData d)
        {
            switch (d.GetInt("type", 0))
            {
                case 1: return OSMPortType.Vector3;
                case 2: return OSMPortType.Vector4;
                default: return OSMPortType.Float;
            }
        }

        /// <summary>A float property splatted to the requested type.</summary>
        public static string Splat(OSMPortType t, string scalarExpr)
        {
            return OSMPortTypes.Coerce(scalarExpr, OSMPortType.Float, t);
        }
    }

    // =====================================================================================
    //  Output
    // =====================================================================================

    public sealed class OSMLitOutputNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Toggle("twoSided", "Two Sided", false, "Render both faces (Cull Off)."),
            OSMParamDef.Toggle("alphaClip", "Alpha Clipping", false, "Discard pixels whose Alpha is below the threshold. Also applies to shadows and depth."),
            OSMParamDef.Slider("cutoff", "Alpha Threshold", 0f, 1f, 0.5f, "Pixels with Alpha below this value are discarded. Exposed as a material property.").When(d => d.GetBool("alphaClip")),
            OSMParamDef.Tint("fbColor", "Base Color (fallback)", Color.white, false, "Used when Base Color is not connected."),
            OSMParamDef.Slider("fbAO", "AO (fallback)", 0f, 1f, 1f, "Used when OSM is not connected. 1 = no occlusion."),
            OSMParamDef.Slider("fbSmooth", "Smoothness (fallback)", 0f, 1f, 0.5f, "Used when OSM is not connected."),
            OSMParamDef.Slider("fbMetal", "Metallic (fallback)", 0f, 1f, 0f, "Used when OSM is not connected. 0 = dielectric."),
            OSMParamDef.Tint("fbEmission", "Emission (fallback)", Color.black, true, "Used when Emission is not connected."),
            OSMParamDef.Slider("parallax", "Height Scale", 0f, 0.1f, 0.02f, "Strength of the parallax effect when Height is connected. Exposed as a material property.")
        };
        static readonly OSMPortDef[] In =
        {
            new OSMPortDef("baseColor", "Base Color", OSMPortType.Vector3),
            new OSMPortDef("normal", "Normal (tangent)", OSMPortType.Vector3),
            new OSMPortDef("osm", "OSM", OSMPortType.OSM),
            new OSMPortDef("emission", "Emission", OSMPortType.Vector3),
            new OSMPortDef("height", "Height (parallax)", OSMPortType.Float),
            new OSMPortDef("alpha", "Alpha (for clipping)", OSMPortType.Float)
        };

        public override string TypeId { get { return OSMNodeIds.Output; } }
        public override string Title { get { return "OSM Lit"; } }
        public override string Category { get { return "Output"; } }
        public override bool Deletable { get { return false; } }
        public override bool Unique { get { return true; } }
        public override string Help { get { return "Terminal node. Generates a URP Lit-style opaque shader that reads the OSM channels explicitly (R = AO, G = Smoothness, B = Metallic)."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d) { return In; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var s = c.Surface;
            s.TwoSided = d.GetBool("twoSided");
            s.AlphaClip = d.GetBool("alphaClip");
            if (s.AlphaClip)
            {
                s.CutoffProp = c.PropFloat(d, "cutoff", d.GetFloat("cutoff", 0.5f));
                if (!c.IsConnected(d, "alpha")) c.Info(d, "Alpha Clipping is on but nothing is connected to Alpha (for example Base Color Texture > Alpha), so nothing is clipped.");
            }

            string bc = c.Input(d, "baseColor");
            if (bc == null)
                bc = c.PropColor(d, "fbColor", d.GetColor("fbColor", Color.white), false) + ".rgb";
            s.BaseColor = bc;

            s.Normal = c.InputOr(d, "normal", "float3(0.0, 0.0, 1.0)");

            if (c.OsmOverride != null)
            {
                c.PropTextureNamed("_OSM_Override", "OSM (packed override)", c.OsmOverride, "white", null);
                s.Osm = c.NewVar(OSMPortType.Vector4, "OSM_SAMPLE(_OSM_Override, uv)", "s");
            }
            else
            {
                string osm = c.Input(d, "osm");
                if (osm == null)
                {
                    string ao = c.PropFloat(d, "fbAO", d.GetFloat("fbAO", 1f));
                    string sm = c.PropFloat(d, "fbSmooth", d.GetFloat("fbSmooth", 0.5f));
                    string me = c.PropFloat(d, "fbMetal", d.GetFloat("fbMetal", 0f));
                    osm = c.NewVar(OSMPortType.Vector4, "float4(" + ao + ", " + sm + ", " + me + ", 1.0)", "f");
                    c.Info(d, "OSM input is not connected. Using the fallback AO / Smoothness / Metallic values.");
                }
                s.Osm = osm;
            }

            string em = c.Input(d, "emission");
            if (em == null)
                em = c.PropColor(d, "fbEmission", d.GetColor("fbEmission", Color.black), true) + ".rgb";
            s.Emission = em;
        }
    }

    // =====================================================================================
    //  Texture inputs
    // =====================================================================================

    public sealed class OSMAlbedoNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Tex("texture", "Texture", "white"),
            OSMParamDef.Tint("tint", "Tint", Color.white, false, "Multiplied with the texture. With no texture this is the colour."),
            NodeCommon.Tiling(), NodeCommon.Offset()
        };
        static readonly OSMPortDef[] Out =
        {
            new OSMPortDef("color", "Color", OSMPortType.Vector3),
            new OSMPortDef("alpha", "Alpha", OSMPortType.Float)
        };

        public override string TypeId { get { return OSMNodeIds.Albedo; } }
        public override string Title { get { return "Base Color Texture"; } }
        public override string Category { get { return "Input"; } }
        public override string Help { get { return "Albedo / base colour map. Sampled as colour data (sRGB)."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var tex = d.GetObject<Texture>("texture");
            string tint = c.PropColor(d, "tint", d.GetColor("tint", Color.white), false);
            if (tex == null)
            {
                c.Output(d, "color", OSMPortType.Vector3, tint + ".rgb");
                c.Output(d, "alpha", OSMPortType.Float, tint + ".a");
                c.Info(d, "No texture assigned. Outputs the tint colour.");
                return;
            }
            OSMTextureTools.CheckColorData(c, d, tex);
            string s = c.SampleTexture(d, "texture", "white");
            c.Output(d, "color", OSMPortType.Vector3, s + ".rgb * " + tint + ".rgb");
            c.Output(d, "alpha", OSMPortType.Float, s + ".a * " + tint + ".a");
        }
    }

    public sealed class OSMNormalMapNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Tex("texture", "Texture", "bump"),
            OSMParamDef.Slider("strength", "Strength", 0f, 4f, 1f, "Scales the X/Y components. Exposed as a material property."),
            OSMParamDef.Choice("encoding", "Encoding", new[] { "Auto (import settings)", "Unity Normal Map", "Raw RGB" }, 0,
                "Auto decodes with Unity's normal-map path when the texture is imported as a Normal Map, otherwise as raw RGB (x = r*2-1 ...)."),
            NodeCommon.Tiling(), NodeCommon.Offset()
        };
        static readonly OSMPortDef[] Out = { new OSMPortDef("normal", "Normal", OSMPortType.Vector3) };

        public override string TypeId { get { return OSMNodeIds.NormalMap; } }
        public override string Title { get { return "Normal Map"; } }
        public override string Category { get { return "Input"; } }
        public override string Help { get { return "Outputs a decoded, signed tangent-space normal vector. Use Normal Convention Converter to flip X/Y."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var tex = d.GetObject<Texture>("texture");
            if (tex == null)
            {
                c.Output(d, "normal", OSMPortType.Vector3, "float3(0.0, 0.0, 1.0)");
                c.Info(d, "No texture assigned. Outputs a flat normal.");
                return;
            }
            int enc = d.GetInt("encoding", 0);
            OSMTextureTools.CheckNormalImport(c, d, tex, enc);
            bool unity = enc == 1 || (enc == 0 && OSMTextureTools.IsNormalMapImport(tex));
            string s = c.SampleTexture(d, "texture", "bump");
            string k = c.PropFloat(d, "strength", d.GetFloat("strength", 1f));
            c.Output(d, "normal", OSMPortType.Vector3, (unity ? "OSM_UnpackNormal(" : "OSM_RawNormal(") + s + ", " + k + ")");
        }
    }

    public abstract class OSMScalarMapNode : OSMNodeDef
    {
        OSMParamDef[] _params;
        OSMPortDef[] _out;

        protected abstract string OutKey { get; }
        protected abstract string OutLabel { get; }
        protected abstract float FallbackValue { get; }
        protected abstract string What { get; }

        public override string Category { get { return "Input"; } }

        public override IReadOnlyList<OSMParamDef> Params
        {
            get
            {
                if (_params == null)
                    _params = new[]
                    {
                        OSMParamDef.Tex("texture", "Texture", "white"),
                        OSMParamDef.Choice("channel", "Channel", NodeCommon.ChannelNames, 0, "Which channel of the texture holds the data."),
                        OSMParamDef.Slider("fallback", "Value (no texture)", 0f, 1f, FallbackValue, "Used when no texture is assigned."),
                        NodeCommon.Tiling(), NodeCommon.Offset()
                    };
                return _params;
            }
        }

        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d)
        {
            if (_out == null) _out = new[] { new OSMPortDef(OutKey, OutLabel, OSMPortType.Float) };
            return _out;
        }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var tex = d.GetObject<Texture>("texture");
            if (tex == null)
            {
                c.Output(d, OutKey, OSMPortType.Float, c.PropFloat(d, "fallback", d.GetFloat("fallback", FallbackValue)));
                c.Info(d, "No texture assigned. Outputs the constant value " + OSMPortTypes.F(d.GetFloat("fallback", FallbackValue)) + ".");
                return;
            }
            OSMTextureTools.CheckLinearData(c, d, tex, What);
            string s = c.SampleTexture(d, "texture", "white");
            int ch = Mathf.Clamp(d.GetInt("channel", 0), 0, 3);
            c.Output(d, OutKey, OSMPortType.Float, s + NodeCommon.ChannelSwizzle[ch]);
        }
    }

    public sealed class OSMAOMapNode : OSMScalarMapNode
    {
        public override string TypeId { get { return OSMNodeIds.AOMap; } }
        public override string Title { get { return "AO Map"; } }
        public override string Help { get { return "Ambient occlusion map (linear scalar). 1 = no occlusion."; } }
        protected override string OutKey { get { return "ao"; } }
        protected override string OutLabel { get { return "AO"; } }
        protected override float FallbackValue { get { return 1f; } }
        protected override string What { get { return "AO"; } }
    }

    public sealed class OSMRoughnessMapNode : OSMScalarMapNode
    {
        public override string TypeId { get { return OSMNodeIds.RoughnessMap; } }
        public override string Title { get { return "Roughness Map"; } }
        public override string Help { get { return "Roughness map (linear scalar). Connect through 'Roughness to Smoothness' before the OSM Combiner."; } }
        protected override string OutKey { get { return "roughness"; } }
        protected override string OutLabel { get { return "Roughness"; } }
        protected override float FallbackValue { get { return 0.5f; } }
        protected override string What { get { return "Roughness"; } }
    }

    public sealed class OSMHeightMapNode : OSMScalarMapNode
    {
        public override string TypeId { get { return OSMNodeIds.HeightMap; } }
        public override string Title { get { return "Height Map"; } }
        public override string Help { get { return "Height / displacement map (linear scalar, 0.5 = no shift). Connect it to the Height input of OSM Lit to enable parallax. Nodes that feed Height are sampled with the original UVs."; } }
        protected override string OutKey { get { return "height"; } }
        protected override string OutLabel { get { return "Height"; } }
        protected override float FallbackValue { get { return 0.5f; } }
        protected override string What { get { return "Height"; } }
    }

    public sealed class OSMMetallicMapNode : OSMScalarMapNode
    {
        public override string TypeId { get { return OSMNodeIds.MetallicMap; } }
        public override string Title { get { return "Metallic Map"; } }
        public override string Help { get { return "Metallic map (linear scalar). 0 = dielectric, 1 = metal."; } }
        protected override string OutKey { get { return "metallic"; } }
        protected override string OutLabel { get { return "Metallic"; } }
        protected override float FallbackValue { get { return 0f; } }
        protected override string What { get { return "Metallic"; } }
    }

    public sealed class OSMEmissionMapNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Tex("texture", "Texture", "white"),
            OSMParamDef.Tint("color", "Color (HDR)", Color.white, true, "Emission colour. With no texture this is the emitted colour."),
            OSMParamDef.Slider("intensity", "Intensity", 0f, 10f, 1f),
            NodeCommon.Tiling(), NodeCommon.Offset()
        };
        static readonly OSMPortDef[] Out = { new OSMPortDef("emission", "Emission", OSMPortType.Vector3) };

        public override string TypeId { get { return OSMNodeIds.EmissionMap; } }
        public override string Title { get { return "Emission Map"; } }
        public override string Category { get { return "Input"; } }
        public override string Help { get { return "Emission colour map multiplied by an HDR colour and intensity."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var tex = d.GetObject<Texture>("texture");
            string col = c.PropColor(d, "color", d.GetColor("color", Color.white), true);
            string k = c.PropFloat(d, "intensity", d.GetFloat("intensity", 1f));
            if (tex == null)
            {
                c.Output(d, "emission", OSMPortType.Vector3, col + ".rgb * " + k);
                c.Info(d, "No texture assigned. Outputs the emission colour.");
                return;
            }
            OSMTextureTools.CheckColorData(c, d, tex);
            string s = c.SampleTexture(d, "texture", "white");
            c.Output(d, "emission", OSMPortType.Vector3, s + ".rgb * " + col + ".rgb * " + k);
        }
    }

    public sealed class OSMTextureInputNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Tex("texture", "OSM Texture", "white", "A packed texture: R = AO, G = Smoothness, B = Metallic."),
            NodeCommon.Tiling(), NodeCommon.Offset()
        };
        static readonly OSMPortDef[] Out =
        {
            new OSMPortDef("osm", "OSM", OSMPortType.OSM),
            new OSMPortDef("ao", "AO (R)", OSMPortType.Float),
            new OSMPortDef("smoothness", "Smoothness (G)", OSMPortType.Float),
            new OSMPortDef("metallic", "Metallic (B)", OSMPortType.Float)
        };

        public override string TypeId { get { return OSMNodeIds.OSMTexture; } }
        public override string Title { get { return "OSM Texture Input"; } }
        public override string Category { get { return "Input"; } }
        public override string Help { get { return "A pre-packed OSM texture with the fixed mapping R = AO, G = Smoothness, B = Metallic. Recognised through metadata, never by file name."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var tex = d.GetObject<Texture>("texture");
            if (tex == null)
            {
                c.Warn(d, "No OSM texture assigned. Using AO 1, Smoothness 0.5, Metallic 0.");
                c.Output(d, "osm", OSMPortType.OSM, "float4(1.0, 0.5, 0.0, 1.0)");
                c.Output(d, "ao", OSMPortType.Float, "1.0");
                c.Output(d, "smoothness", OSMPortType.Float, "0.5");
                c.Output(d, "metallic", OSMPortType.Float, "0.0");
                return;
            }
            OSMTextureTools.CheckOSMTexture(c, d, tex);
            string s = c.SampleTexture(d, "texture", "white");
            c.Output(d, "osm", OSMPortType.OSM, "float4(" + s + ".rgb, 1.0)");
            c.Output(d, "ao", OSMPortType.Float, s + ".r");
            c.Output(d, "smoothness", OSMPortType.Float, s + ".g");
            c.Output(d, "metallic", OSMPortType.Float, s + ".b");
        }
    }

    // =====================================================================================
    //  Processing and packing
    // =====================================================================================

    public sealed class OSMRoughToSmoothNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Toggle("invert", "Invert (roughness → smoothness)", true,
                "Smoothness = 1 - Roughness. Turn off to bypass when the input already contains smoothness."),
            OSMParamDef.Slider("fallback", "Roughness (unconnected)", 0f, 1f, 0.5f, "Used when no input is connected.")
        };
        static readonly OSMPortDef[] In = { new OSMPortDef("roughness", "Roughness", OSMPortType.Float) };
        static readonly OSMPortDef[] Out = { new OSMPortDef("smoothness", "Smoothness", OSMPortType.Float) };

        public override string TypeId { get { return OSMNodeIds.RoughToSmooth; } }
        public override string Title { get { return "Roughness to Smoothness"; } }
        public override string Category { get { return "Processing"; } }
        public override string Help { get { return "Computes S = 1 - R in linear space. Never applied twice: the tool warns when the input looks already converted."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d) { return In; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            bool invert = d.GetBool("invert", true);
            string input = c.Input(d, "roughness");
            var src = c.SourceNode(d, "roughness");
            if (input == null)
            {
                input = c.PropFloat(d, "fallback", d.GetFloat("fallback", 0.5f));
                c.Info(d, "No roughness input connected. Using the fallback value.");
            }
            else if (src != null)
            {
                if (invert && (src.typeId == OSMNodeIds.OSMTexture || src.typeId == OSMNodeIds.RoughToSmooth))
                    c.Warn(d, "The input already looks like smoothness. Inverting it again would double-convert. Turn 'Invert' off to bypass.");
                if (!invert && src.typeId == OSMNodeIds.RoughnessMap)
                    c.Info(d, "Invert is off: the roughness map passes through unchanged and will be treated as smoothness.");
            }
            // abs(inv - x): inv = 1 gives 1 - x, inv = 0 gives x. A property, so toggling never recompiles the shader.
            string inv = c.PropFloat(d, "inv", invert ? 1f : 0f);
            c.Output(d, "smoothness", OSMPortType.Float, "saturate(abs(" + inv + " - " + input + "))");
        }
    }

    public sealed class OSMNormalConverterNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Toggle("flipX", "Flip X (Red)", false, "x' = -x"),
            OSMParamDef.Toggle("flipY", "Flip Y (Green)", false, "y' = -y. This is the usual DirectX/OpenGL difference. Tick both for 'Flip both'.")
        };
        static readonly OSMPortDef[] In = { new OSMPortDef("normal", "Normal", OSMPortType.Vector3) };
        static readonly OSMPortDef[] Out = { new OSMPortDef("normal", "Normal", OSMPortType.Vector3) };

        public override string TypeId { get { return OSMNodeIds.NormalConverter; } }
        public override string Title { get { return "Normal Convention Converter"; } }
        public override string Category { get { return "Processing"; } }
        public override string Help { get { return "Negates the X and/or Y component of the already-decoded tangent-space normal (Z is preserved). Never flips based on the source application."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d) { return In; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            bool fx = d.GetBool("flipX"), fy = d.GetBool("flipY");
            string n = c.Input(d, "normal");
            if (n == null)
            {
                c.Warn(d, "No normal input connected. Using a flat normal.");
                n = "float3(0.0, 0.0, 1.0)";
            }
            var src = c.SourceNode(d, "normal");
            if (src != null && src.typeId == OSMNodeIds.NormalConverter)
            {
                if (fx && src.GetBool("flipX")) c.Warn(d, "X is flipped here and in the previous converter: the two flips cancel out.");
                if (fy && src.GetBool("flipY")) c.Warn(d, "Y is flipped here and in the previous converter: the two flips cancel out.");
            }
            string sx = c.PropFloat(d, "signX", fx ? -1f : 1f);
            string sy = c.PropFloat(d, "signY", fy ? -1f : 1f);
            c.Output(d, "normal", OSMPortType.Vector3, "float3((" + n + ").x * " + sx + ", (" + n + ").y * " + sy + ", (" + n + ").z)");
        }
    }

    public sealed class OSMCombinerNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Slider("fbAO", "AO (unconnected)", 0f, 1f, 1f, "Default 1 = no occlusion."),
            OSMParamDef.Slider("fbSmooth", "Smoothness (unconnected)", 0f, 1f, 0.5f),
            OSMParamDef.Slider("fbMetal", "Metallic (unconnected)", 0f, 1f, 0f, "Default 0 = dielectric.")
        };
        static readonly OSMPortDef[] In =
        {
            new OSMPortDef("ao", "AO → R", OSMPortType.Float),
            new OSMPortDef("smoothness", "Smoothness → G", OSMPortType.Float),
            new OSMPortDef("metallic", "Metallic → B", OSMPortType.Float)
        };
        static readonly OSMPortDef[] Out = { new OSMPortDef("osm", "OSM", OSMPortType.OSM) };

        public override string TypeId { get { return OSMNodeIds.Combiner; } }
        public override string Title { get { return "OSM Combiner"; } }
        public override string Category { get { return "Packing"; } }
        public override string Help { get { return "Packs AO, Smoothness and Metallic into R, G, B (A = 1). The output is a live value; use 'Bake OSM…' to save it as a texture asset."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d) { return In; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }

        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            string ao = c.Input(d, "ao") ?? c.PropFloat(d, "fbAO", d.GetFloat("fbAO", 1f));
            string sm = c.Input(d, "smoothness") ?? c.PropFloat(d, "fbSmooth", d.GetFloat("fbSmooth", 0.5f));
            string me = c.Input(d, "metallic") ?? c.PropFloat(d, "fbMetal", d.GetFloat("fbMetal", 0f));

            var smSrc = c.SourceNode(d, "smoothness");
            if (smSrc != null && smSrc.typeId == OSMNodeIds.RoughnessMap)
                c.Warn(d, "A Roughness Map is connected straight into Smoothness. Insert a 'Roughness to Smoothness' node, otherwise the material will look inverted.");

            c.Output(d, "osm", OSMPortType.OSM, "float4(saturate(" + ao + "), saturate(" + sm + "), saturate(" + me + "), 1.0)");
        }
    }

    // =====================================================================================
    //  Utility nodes
    // =====================================================================================

    public sealed class OSMSplitNode : OSMNodeDef
    {
        static readonly OSMPortDef[] In = { new OSMPortDef("in", "Input", OSMPortType.Vector4) };
        static readonly OSMPortDef[] Out =
        {
            new OSMPortDef("r", "R", OSMPortType.Float), new OSMPortDef("g", "G", OSMPortType.Float),
            new OSMPortDef("b", "B", OSMPortType.Float), new OSMPortDef("a", "A", OSMPortType.Float)
        };
        public override string TypeId { get { return OSMNodeIds.Split; } }
        public override string Title { get { return "Split Channels"; } }
        public override string Category { get { return "Utility"; } }
        public override string Help { get { return "Exposes R, G, B and A of a vector (or an OSM value) as separate scalars."; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d) { return In; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }
        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            string i = c.InputOr(d, "in", "float4(0.0, 0.0, 0.0, 1.0)");
            c.Output(d, "r", OSMPortType.Float, "(" + i + ").r");
            c.Output(d, "g", OSMPortType.Float, "(" + i + ").g");
            c.Output(d, "b", OSMPortType.Float, "(" + i + ").b");
            c.Output(d, "a", OSMPortType.Float, "(" + i + ").a");
        }
    }

    public sealed class OSMCombineChannelsNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Number("dr", "R (unconnected)", 0f), OSMParamDef.Number("dg", "G (unconnected)", 0f),
            OSMParamDef.Number("db", "B (unconnected)", 0f), OSMParamDef.Number("da", "A (unconnected)", 1f)
        };
        static readonly OSMPortDef[] In =
        {
            new OSMPortDef("r", "R", OSMPortType.Float), new OSMPortDef("g", "G", OSMPortType.Float),
            new OSMPortDef("b", "B", OSMPortType.Float), new OSMPortDef("a", "A", OSMPortType.Float)
        };
        static readonly OSMPortDef[] Out =
        {
            new OSMPortDef("rgba", "RGBA", OSMPortType.Vector4), new OSMPortDef("rgb", "RGB", OSMPortType.Vector3)
        };
        public override string TypeId { get { return OSMNodeIds.Combine; } }
        public override string Title { get { return "Combine Channels"; } }
        public override string Category { get { return "Utility"; } }
        public override string Help { get { return "Packs four scalars into a vector. For the OSM convention use the OSM Combiner instead."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d) { return In; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return Out; }
        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            string r = c.Input(d, "r") ?? c.PropFloat(d, "dr", d.GetFloat("dr", 0f));
            string g = c.Input(d, "g") ?? c.PropFloat(d, "dg", d.GetFloat("dg", 0f));
            string b = c.Input(d, "b") ?? c.PropFloat(d, "db", d.GetFloat("db", 0f));
            string a = c.Input(d, "a") ?? c.PropFloat(d, "da", d.GetFloat("da", 1f));
            c.Output(d, "rgba", OSMPortType.Vector4, "float4(" + r + ", " + g + ", " + b + ", " + a + ")");
            c.Output(d, "rgb", OSMPortType.Vector3, "float3(" + r + ", " + g + ", " + b + ")");
        }
    }

    public sealed class OSMOneMinusNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P = { OSMParamDef.Choice("type", "Type", NodeCommon.TypeNames, 0, null, true) };
        public override string TypeId { get { return OSMNodeIds.OneMinus; } }
        public override string Title { get { return "One Minus"; } }
        public override string Category { get { return "Utility"; } }
        public override string Help { get { return "Outputs 1 - input."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d) { return new[] { new OSMPortDef("in", "In", NodeCommon.TypeOf(d)) }; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return new[] { new OSMPortDef("out", "Out", NodeCommon.TypeOf(d)) }; }
        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var t = NodeCommon.TypeOf(d);
            string i = c.Input(d, "in");
            if (i == null) { i = NodeCommon.Splat(t, "0.0"); c.Info(d, "No input connected. Using 0."); }
            c.Output(d, "out", t, "1.0 - " + i);
        }
    }

    public sealed class OSMMultiplyNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Choice("type", "Type", NodeCommon.TypeNames, 0, null, true),
            OSMParamDef.Number("fbA", "A (unconnected)", 1f), OSMParamDef.Number("fbB", "B (unconnected)", 1f)
        };
        public override string TypeId { get { return OSMNodeIds.Multiply; } }
        public override string Title { get { return "Multiply"; } }
        public override string Category { get { return "Utility"; } }
        public override string Help { get { return "A × B."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d)
        { var t = NodeCommon.TypeOf(d); return new[] { new OSMPortDef("a", "A", t), new OSMPortDef("b", "B", t) }; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return new[] { new OSMPortDef("out", "Out", NodeCommon.TypeOf(d)) }; }
        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var t = NodeCommon.TypeOf(d);
            string a = c.Input(d, "a") ?? NodeCommon.Splat(t, c.PropFloat(d, "fbA", d.GetFloat("fbA", 1f)));
            string b = c.Input(d, "b") ?? NodeCommon.Splat(t, c.PropFloat(d, "fbB", d.GetFloat("fbB", 1f)));
            c.Output(d, "out", t, a + " * " + b);
        }
    }

    public sealed class OSMAddNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Choice("type", "Type", NodeCommon.TypeNames, 0, null, true),
            OSMParamDef.Number("fbA", "A (unconnected)", 0f), OSMParamDef.Number("fbB", "B (unconnected)", 0f)
        };
        public override string TypeId { get { return OSMNodeIds.Add; } }
        public override string Title { get { return "Add"; } }
        public override string Category { get { return "Utility"; } }
        public override string Help { get { return "A + B."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d)
        { var t = NodeCommon.TypeOf(d); return new[] { new OSMPortDef("a", "A", t), new OSMPortDef("b", "B", t) }; }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return new[] { new OSMPortDef("out", "Out", NodeCommon.TypeOf(d)) }; }
        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var t = NodeCommon.TypeOf(d);
            string a = c.Input(d, "a") ?? NodeCommon.Splat(t, c.PropFloat(d, "fbA", d.GetFloat("fbA", 0f)));
            string b = c.Input(d, "b") ?? NodeCommon.Splat(t, c.PropFloat(d, "fbB", d.GetFloat("fbB", 0f)));
            c.Output(d, "out", t, a + " + " + b);
        }
    }

    public sealed class OSMLerpNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Choice("type", "Type", NodeCommon.TypeNames, 0, null, true),
            OSMParamDef.Number("fbA", "A (unconnected)", 0f), OSMParamDef.Number("fbB", "B (unconnected)", 1f),
            OSMParamDef.Slider("fbT", "T (unconnected)", 0f, 1f, 0.5f)
        };
        public override string TypeId { get { return OSMNodeIds.Lerp; } }
        public override string Title { get { return "Lerp"; } }
        public override string Category { get { return "Utility"; } }
        public override string Help { get { return "Blends from A to B by T."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Inputs(OSMNodeData d)
        {
            var t = NodeCommon.TypeOf(d);
            return new[] { new OSMPortDef("a", "A", t), new OSMPortDef("b", "B", t), new OSMPortDef("t", "T", OSMPortType.Float) };
        }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return new[] { new OSMPortDef("out", "Out", NodeCommon.TypeOf(d)) }; }
        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            var t = NodeCommon.TypeOf(d);
            string a = c.Input(d, "a") ?? NodeCommon.Splat(t, c.PropFloat(d, "fbA", d.GetFloat("fbA", 0f)));
            string b = c.Input(d, "b") ?? NodeCommon.Splat(t, c.PropFloat(d, "fbB", d.GetFloat("fbB", 1f)));
            string k = c.Input(d, "t") ?? c.PropFloat(d, "fbT", d.GetFloat("fbT", 0.5f));
            c.Output(d, "out", t, "lerp(" + a + ", " + b + ", " + k + ")");
        }
    }

    public sealed class OSMConstantNode : OSMNodeDef
    {
        static readonly OSMParamDef[] P =
        {
            OSMParamDef.Choice("type", "Type", new[] { "Float", "Vector3", "Color" }, 0, null, true),
            OSMParamDef.Number("value", "Value", 0f).When(d => d.GetInt("type", 0) == 0),
            OSMParamDef.Vec3("vector", "Vector", Vector3.zero).When(d => d.GetInt("type", 0) == 1),
            OSMParamDef.Tint("color", "Color", Color.white).When(d => d.GetInt("type", 0) == 2)
        };
        static OSMPortType TypeOf(OSMNodeData d)
        {
            switch (d.GetInt("type", 0))
            {
                case 1: return OSMPortType.Vector3;
                case 2: return OSMPortType.Vector4;
                default: return OSMPortType.Float;
            }
        }
        public override string TypeId { get { return OSMNodeIds.Constant; } }
        public override string Title { get { return "Constant"; } }
        public override string Category { get { return "Utility"; } }
        public override string Help { get { return "A fixed scalar, vector or colour. Exposed as a material property, so editing it does not recompile."; } }
        public override IReadOnlyList<OSMParamDef> Params { get { return P; } }
        public override IReadOnlyList<OSMPortDef> Outputs(OSMNodeData d) { return new[] { new OSMPortDef("out", "Out", TypeOf(d)) }; }
        public override void Emit(OSMEmitContext c, OSMNodeData d)
        {
            switch (d.GetInt("type", 0))
            {
                case 1:
                    c.Output(d, "out", OSMPortType.Vector3, c.PropVector(d, "vector", d.GetVector("vector", Vector4.zero)) + ".xyz");
                    break;
                case 2:
                    c.Output(d, "out", OSMPortType.Vector4, c.PropColor(d, "color", d.GetColor("color", Color.white), false));
                    break;
                default:
                    c.Output(d, "out", OSMPortType.Float, c.PropFloat(d, "value", d.GetFloat("value", 0f)));
                    break;
            }
        }
    }
}
