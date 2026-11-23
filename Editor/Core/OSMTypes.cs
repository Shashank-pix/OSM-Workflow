using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>Data types that can flow through a port.</summary>
    public enum OSMPortType
    {
        Float = 0,
        Vector3 = 1,
        Vector4 = 2,
        /// <summary>Packed data: R = AO, G = Smoothness, B = Metallic, A = reserved (1).</summary>
        OSM = 3
    }

    public static class OSMPortTypes
    {
        public static string Hlsl(OSMPortType t)
        {
            switch (t)
            {
                case OSMPortType.Float: return "float";
                case OSMPortType.Vector3: return "float3";
                default: return "float4";
            }
        }

        public static string Label(OSMPortType t)
        {
            switch (t)
            {
                case OSMPortType.Float: return "Float";
                case OSMPortType.Vector3: return "Vector3 / Colour";
                case OSMPortType.Vector4: return "Vector4 / RGBA";
                default: return "OSM (R=AO, G=Smoothness, B=Metallic)";
            }
        }

        public static Color PortColor(OSMPortType t)
        {
            switch (t)
            {
                case OSMPortType.Float: return new Color(0.60f, 0.85f, 1.00f);
                case OSMPortType.Vector3: return new Color(1.00f, 0.85f, 0.45f);
                case OSMPortType.Vector4: return new Color(0.95f, 0.55f, 0.85f);
                default: return new Color(0.55f, 1.00f, 0.60f);
            }
        }

        /// <summary>
        /// Connection rules. Float widens to vectors, vectors convert between each other,
        /// OSM may be read as a vector, but nothing except an OSM port can feed an OSM port
        /// so the channel convention can never be lost by accident.
        /// </summary>
        public static bool CanConnect(OSMPortType from, OSMPortType to)
        {
            if (from == to) return true;
            switch (to)
            {
                case OSMPortType.Float: return false;
                case OSMPortType.Vector3: return true;
                case OSMPortType.Vector4: return true;
                default: return false;
            }
        }

        public static string Coerce(string expr, OSMPortType from, OSMPortType to)
        {
            if (from == to) return expr;
            if (from == OSMPortType.Float) return "((" + Hlsl(to) + ")(" + expr + "))";
            if (to == OSMPortType.Vector3) return "(" + expr + ").xyz";
            if (to == OSMPortType.Vector4) return "float4(" + expr + ", 1.0)";
            return expr;
        }

        public static string F(float f)
        {
            return f.ToString("0.########", CultureInfo.InvariantCulture);
        }
    }

    public readonly struct OSMPortDef
    {
        public readonly string Key;
        public readonly string Label;
        public readonly OSMPortType Type;
        public OSMPortDef(string key, string label, OSMPortType type) { Key = key; Label = label; Type = type; }
    }

    public enum OSMParamKind { Number, Slider, Toggle, Choice, Color, ColorHDR, Vector2, Vector3, Texture }

    /// <summary>Declarative description of a node setting. The node and inspector UI are generated from these.</summary>
    public sealed class OSMParamDef
    {
        public string Key;
        public string Label;
        public string Tooltip;
        public OSMParamKind Kind;
        public float Default;
        public float Min, Max = 1f;
        public Vector4 DefaultVector;
        public string[] Options;
        public string DefaultTexture = "white";
        public bool AffectsPorts;
        public Func<OSMNodeData, bool> VisibleWhen;

        public OSMParamDef When(Func<OSMNodeData, bool> f) { VisibleWhen = f; return this; }

        public static OSMParamDef Number(string key, string label, float def = 0f, string tip = null)
        { return new OSMParamDef { Key = key, Label = label, Kind = OSMParamKind.Number, Default = def, Tooltip = tip }; }

        public static OSMParamDef Slider(string key, string label, float min, float max, float def, string tip = null)
        { return new OSMParamDef { Key = key, Label = label, Kind = OSMParamKind.Slider, Min = min, Max = max, Default = def, Tooltip = tip }; }

        public static OSMParamDef Toggle(string key, string label, bool def, string tip = null)
        { return new OSMParamDef { Key = key, Label = label, Kind = OSMParamKind.Toggle, Default = def ? 1f : 0f, Tooltip = tip }; }

        public static OSMParamDef Choice(string key, string label, string[] options, int def = 0, string tip = null, bool affectsPorts = false)
        { return new OSMParamDef { Key = key, Label = label, Kind = OSMParamKind.Choice, Options = options, Default = def, Tooltip = tip, AffectsPorts = affectsPorts }; }

        public static OSMParamDef Tint(string key, string label, Color def, bool hdr = false, string tip = null)
        { return new OSMParamDef { Key = key, Label = label, Kind = hdr ? OSMParamKind.ColorHDR : OSMParamKind.Color, DefaultVector = new Vector4(def.r, def.g, def.b, def.a), Tooltip = tip }; }

        public static OSMParamDef Vec2(string key, string label, Vector2 def, string tip = null)
        { return new OSMParamDef { Key = key, Label = label, Kind = OSMParamKind.Vector2, DefaultVector = new Vector4(def.x, def.y, 0, 0), Tooltip = tip }; }

        public static OSMParamDef Vec3(string key, string label, Vector3 def, string tip = null)
        { return new OSMParamDef { Key = key, Label = label, Kind = OSMParamKind.Vector3, DefaultVector = new Vector4(def.x, def.y, def.z, 0), Tooltip = tip }; }

        public static OSMParamDef Tex(string key, string label, string defaultTexture = "white", string tip = null)
        { return new OSMParamDef { Key = key, Label = label, Kind = OSMParamKind.Texture, DefaultTexture = defaultTexture, Tooltip = tip }; }
    }

    public enum OSMSeverity { Info = 0, Warning = 1, Error = 2 }

    public sealed class OSMDiagnostic
    {
        public OSMSeverity Severity;
        public string NodeId;
        public string Message;
        public string FixLabel;
        public Action Fix;
    }

    public enum OSMBindingKind { Float, Vector, Color, Texture }

    /// <summary>A material property produced by the compiler together with the value to assign to it.</summary>
    public sealed class OSMBinding
    {
        public string Name;
        public string Label;
        public OSMBindingKind Kind;
        public float Float;
        public Vector4 Vector;
        public Texture Texture;
        public bool Hdr;
        public string DefaultTexture = "white";
        public string NodeId;

        public void Apply(Material m)
        {
            if (m == null || !m.HasProperty(Name)) return;
            switch (Kind)
            {
                case OSMBindingKind.Float: m.SetFloat(Name, Float); break;
                case OSMBindingKind.Vector: m.SetVector(Name, Vector); break;
                case OSMBindingKind.Color: m.SetColor(Name, new Color(Vector.x, Vector.y, Vector.z, Vector.w)); break;
                case OSMBindingKind.Texture: m.SetTexture(Name, Texture); break;
            }
        }
    }

    public sealed class OSMTextureUse
    {
        public string NodeId;
        public string Title;
        public Texture Texture;
    }

    public sealed class OSMCompileOptions
    {
        public bool IncludeDebug = true;
        public string ShaderName = "Hidden/OSM Workflow/Preview";
        /// <summary>When set, the OSM input of the output node samples this texture instead of evaluating its source nodes.</summary>
        public Texture OsmOverride;
    }

    public sealed class OSMCompileResult
    {
        public string ShaderSource;
        public string Hash;
        public readonly List<OSMBinding> Bindings = new List<OSMBinding>();
        public readonly List<OSMDiagnostic> Diagnostics = new List<OSMDiagnostic>();
        public readonly List<OSMTextureUse> Textures = new List<OSMTextureUse>();

        public bool HasErrors
        {
            get { foreach (var d in Diagnostics) if (d.Severity == OSMSeverity.Error) return true; return false; }
        }
        public bool Success { get { return !HasErrors && !string.IsNullOrEmpty(ShaderSource); } }

        public void ApplyTo(Material m) { foreach (var b in Bindings) b.Apply(m); }
    }

    public static class OSMEvents
    {
        /// <summary>Raised when any texture asset was imported or re-imported. Listeners should debounce.</summary>
        public static event Action TexturesReimported;
        internal static void RaiseTexturesReimported() { if (TexturesReimported != null) TexturesReimported(); }
    }
}
