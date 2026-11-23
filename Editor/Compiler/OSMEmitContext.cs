using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>Final surface values chosen by the output node.</summary>
    public sealed class OSMSurface
    {
        public string BaseColor = "float3(1.0, 1.0, 1.0)";
        public string Normal = "float3(0.0, 0.0, 1.0)";
        public string Osm = "float4(1.0, 0.5, 0.0, 1.0)";
        public string Emission = "float3(0.0, 0.0, 0.0)";
        public bool TwoSided;
        public bool UsesParallax;
        public bool AlphaClip;
        public string CutoffProp;
        public string AlphaCode;   // statements that compute alpha from a parameter named uv
        public string AlphaExpr;   // float expression valid after AlphaCode
    }

    /// <summary>
    /// Passed to every node while compiling. Nodes read their inputs, write outputs and declare
    /// material properties through this object; they never build strings for the whole shader.
    /// Values that can change without changing the shader structure (constants, tints, flips...)
    /// are exposed as material properties so editing them never triggers a shader recompile.
    /// </summary>
    public sealed class OSMEmitContext
    {
        struct Out { public string Expr; public OSMPortType Type; }

        public readonly OSMGraphAsset Graph;
        public readonly Texture OsmOverride;
        public readonly List<OSMBinding> Bindings = new List<OSMBinding>();
        public readonly List<OSMDiagnostic> Diagnostics = new List<OSMDiagnostic>();
        public readonly List<OSMTextureUse> Textures = new List<OSMTextureUse>();
        public readonly OSMSurface Surface = new OSMSurface();

        readonly StringBuilder _code = new StringBuilder();
        readonly Dictionary<string, Out> _outputs = new Dictionary<string, Out>();
        readonly HashSet<string> _declared = new HashSet<string>();
        int _counter;

        public OSMEmitContext(OSMGraphAsset graph, Texture osmOverride)
        {
            Graph = graph;
            OsmOverride = osmOverride;
        }

        public string Code { get { return _code.ToString(); } }

        // ------------------------------------------------------------------ code

        public string NewVar(OSMPortType type, string expr, string hint = "v")
        {
            string name = hint + (_counter++);
            _code.Append("    ").Append(OSMPortTypes.Hlsl(type)).Append(' ').Append(name)
                 .Append(" = ").Append(expr).Append(";\n");
            return name;
        }

        /// <summary>Takes over property declarations made by another context so both agree on one material property set.</summary>
        public void AdoptBindings(OSMEmitContext other)
        {
            foreach (var b in other.Bindings)
                if (_declared.Add(b.Name)) Bindings.Add(b);
            foreach (var t in other.Textures)
            {
                bool known = false;
                foreach (var k in Textures) if (k.Texture == t.Texture && k.NodeId == t.NodeId) known = true;
                if (!known) Textures.Add(t);
            }
        }

        public void Raw(string line) { _code.Append("    ").Append(line).Append('\n'); }

        public void Comment(string text) { _code.Append("    // ").Append(text.Replace('\n', ' ')).Append('\n'); }

        // ------------------------------------------------------------------ inputs / outputs

        /// <summary>Expression feeding an input port, converted to the port's type; null when unconnected.</summary>
        public string Input(OSMNodeData n, string portKey)
        {
            var conn = Graph.GetConnectionInto(n.id, portKey);
            if (conn == null) return null;
            Out src;
            if (!_outputs.TryGetValue(conn.fromNode + "|" + conn.fromPort, out src)) return null;
            OSMNodeDef def;
            OSMPortDef port;
            if (!OSMNodeRegistry.TryGet(n.typeId, out def) || !def.TryGetInput(n, portKey, out port)) return src.Expr;
            return OSMPortTypes.Coerce(src.Expr, src.Type, port.Type);
        }

        public string InputOr(OSMNodeData n, string portKey, string fallback)
        {
            var e = Input(n, portKey);
            return e ?? fallback;
        }

        public bool IsConnected(OSMNodeData n, string portKey) { return Graph.GetConnectionInto(n.id, portKey) != null; }

        /// <summary>The node connected to an input, or null. Used by validation heuristics.</summary>
        public OSMNodeData SourceNode(OSMNodeData n, string portKey)
        {
            var conn = Graph.GetConnectionInto(n.id, portKey);
            return conn == null ? null : Graph.FindNode(conn.fromNode);
        }

        public void Output(OSMNodeData n, string portKey, OSMPortType type, string expr)
        {
            string v = NewVar(type, expr, "o");
            _outputs[n.id + "|" + portKey] = new Out { Expr = v, Type = type };
        }

        public bool TryGetOutput(string nodeId, string portKey, out string expr, out OSMPortType type)
        {
            Out o;
            if (_outputs.TryGetValue(nodeId + "|" + portKey, out o)) { expr = o.Expr; type = o.Type; return true; }
            expr = null; type = OSMPortType.Float;
            return false;
        }

        // ------------------------------------------------------------------ properties

        public string PropName(OSMNodeData n, string key) { return "_n" + n.ShortId + "_" + key; }

        string LabelFor(OSMNodeData n, string key)
        {
            var def = OSMNodeRegistry.Get(n.typeId);
            return (def != null ? def.Title : n.typeId) + " " + n.ShortId + " " + key;
        }

        public string PropFloat(OSMNodeData n, string key, float value)
        {
            string name = PropName(n, key);
            if (_declared.Add(name))
                Bindings.Add(new OSMBinding { Name = name, Label = LabelFor(n, key), Kind = OSMBindingKind.Float, Float = value, NodeId = n.id });
            return name;
        }

        public string PropVector(OSMNodeData n, string key, Vector4 value)
        {
            string name = PropName(n, key);
            if (_declared.Add(name))
                Bindings.Add(new OSMBinding { Name = name, Label = LabelFor(n, key), Kind = OSMBindingKind.Vector, Vector = value, NodeId = n.id });
            return name;
        }

        /// <summary>Colour property (converted from gamma to linear by Unity on upload).</summary>
        public string PropColor(OSMNodeData n, string key, Color value, bool hdr)
        {
            string name = PropName(n, key);
            if (_declared.Add(name))
                Bindings.Add(new OSMBinding
                {
                    Name = name, Label = LabelFor(n, key), Kind = OSMBindingKind.Color, NodeId = n.id, Hdr = hdr,
                    Vector = new Vector4(value.r, value.g, value.b, value.a)
                });
            return name;
        }

        public string PropTexture(OSMNodeData n, string key, Texture tex, string defaultTexture)
        {
            return PropTextureNamed(PropName(n, key), LabelFor(n, key), tex, defaultTexture, n.id);
        }

        public string PropTextureNamed(string name, string label, Texture tex, string defaultTexture, string nodeId)
        {
            if (_declared.Add(name))
            {
                Bindings.Add(new OSMBinding
                {
                    Name = name, Label = label, Kind = OSMBindingKind.Texture, Texture = tex,
                    DefaultTexture = defaultTexture, NodeId = nodeId
                });
                if (tex != null)
                {
                    var def = nodeId != null ? Graph.FindNode(nodeId) : null;
                    var nd = def != null ? OSMNodeRegistry.Get(def.typeId) : null;
                    Textures.Add(new OSMTextureUse { NodeId = nodeId, Title = nd != null ? nd.Title : label, Texture = tex });
                }
            }
            return name;
        }

        /// <summary>
        /// Declares the node's texture and tiling/offset properties and emits the sample.
        /// Returns the name of a float4 variable holding the raw sample.
        /// </summary>
        public string SampleTexture(OSMNodeData n, string texKey, string defaultTexture)
        {
            var tex = n.GetObject<Texture>(texKey);
            string tp = PropTexture(n, texKey, tex, defaultTexture);
            Vector4 t = n.GetVector("tiling", new Vector4(1, 1, 0, 0));
            Vector4 o = n.GetVector("offset", Vector4.zero);
            string st = PropVector(n, "st", new Vector4(t.x, t.y, o.x, o.y));
            return NewVar(OSMPortType.Vector4, "OSM_SAMPLE(" + tp + ", uv * " + st + ".xy + " + st + ".zw)", "s");
        }

        // ------------------------------------------------------------------ diagnostics

        public void Info(OSMNodeData n, string msg) { Add(OSMSeverity.Info, n, msg, null, null); }
        public void Warn(OSMNodeData n, string msg, string fixLabel = null, Action fix = null) { Add(OSMSeverity.Warning, n, msg, fixLabel, fix); }
        public void Error(OSMNodeData n, string msg) { Add(OSMSeverity.Error, n, msg, null, null); }

        void Add(OSMSeverity s, OSMNodeData n, string msg, string fixLabel, Action fix)
        {
            Diagnostics.Add(new OSMDiagnostic { Severity = s, NodeId = n != null ? n.id : null, Message = msg, FixLabel = fixLabel, Fix = fix });
        }
    }
}
