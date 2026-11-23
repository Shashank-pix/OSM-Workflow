using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSMWorkflow
{
    [Serializable]
    public sealed class OSMParamValue
    {
        public string key;
        public float number;
        public Vector4 vector;
        public UnityEngine.Object reference;
    }

    /// <summary>Serializable node record. The view is never the source of truth; this is.</summary>
    [Serializable]
    public sealed class OSMNodeData
    {
        public string id;
        public string typeId;
        public Vector2 position;
        public List<OSMParamValue> values = new List<OSMParamValue>();

        public string ShortId { get { return id.Length >= 6 ? id.Substring(0, 6) : id; } }

        OSMParamValue Find(string key)
        {
            for (int i = 0; i < values.Count; i++) if (values[i].key == key) return values[i];
            return null;
        }

        OSMParamValue FindOrAdd(string key)
        {
            var v = Find(key);
            if (v == null) { v = new OSMParamValue { key = key }; values.Add(v); }
            return v;
        }

        public float GetFloat(string key, float def = 0f) { var v = Find(key); return v == null ? def : v.number; }
        public bool GetBool(string key, bool def = false) { return GetFloat(key, def ? 1f : 0f) > 0.5f; }
        public int GetInt(string key, int def = 0) { return Mathf.RoundToInt(GetFloat(key, def)); }
        public Vector4 GetVector(string key, Vector4 def) { var v = Find(key); return v == null ? def : v.vector; }
        public Color GetColor(string key, Color def)
        {
            var v = Find(key);
            return v == null ? def : new Color(v.vector.x, v.vector.y, v.vector.z, v.vector.w);
        }
        public T GetObject<T>(string key) where T : UnityEngine.Object { var v = Find(key); return v == null ? null : v.reference as T; }

        public void SetFloat(string key, float value) { FindOrAdd(key).number = value; }
        public void SetBool(string key, bool value) { SetFloat(key, value ? 1f : 0f); }
        public void SetVector(string key, Vector4 value) { FindOrAdd(key).vector = value; }
        public void SetColor(string key, Color c) { FindOrAdd(key).vector = new Vector4(c.r, c.g, c.b, c.a); }
        public void SetObject(string key, UnityEngine.Object o) { FindOrAdd(key).reference = o; }

        public void InitDefault(OSMParamDef p)
        {
            switch (p.Kind)
            {
                case OSMParamKind.Number:
                case OSMParamKind.Slider:
                case OSMParamKind.Toggle:
                case OSMParamKind.Choice:
                    SetFloat(p.Key, p.Default); break;
                case OSMParamKind.Color:
                case OSMParamKind.ColorHDR:
                case OSMParamKind.Vector2:
                case OSMParamKind.Vector3:
                    SetVector(p.Key, p.DefaultVector); break;
                case OSMParamKind.Texture:
                    FindOrAdd(p.Key); break;
            }
        }
    }

    [Serializable]
    public sealed class OSMConnection
    {
        public string fromNode;
        public string fromPort;
        public string toNode;
        public string toPort;
    }

    public enum OSMResolutionPolicy { Fixed = 0, MatchLargestInput = 1, MatchFirstInput = 2 }
    public enum OSMBitDepth { Bits8 = 0, Bits16 = 1 }
    public enum OSMCompressionChoice { Uncompressed = 0, CompressedHQ = 1, Compressed = 2 }

    [Serializable]
    public sealed class OSMBakeSettings
    {
        public string folder = "Assets/OSMTextures";
        public string fileName = "";
        public OSMResolutionPolicy policy = OSMResolutionPolicy.Fixed;
        public int size = 1024;
        public OSMBitDepth bitDepth = OSMBitDepth.Bits8;
        public bool mipmaps = true;
        public OSMCompressionChoice compression = OSMCompressionChoice.CompressedHQ;
        public FilterMode filter = FilterMode.Bilinear;
        public TextureWrapMode wrap = TextureWrapMode.Repeat;
        public bool addInputNodeAfterBake = true;
    }

    /// <summary>
    /// The OSM graph asset. Editor-only on purpose: it never ships in a player build.
    /// The generated shader and material are plain Unity assets that work without this tool.
    /// </summary>
    public sealed class OSMGraphAsset : ScriptableObject
    {
        public const int CurrentSchema = 1;

        public int schemaVersion = CurrentSchema;
        public string graphGuid;
        public List<OSMNodeData> nodes = new List<OSMNodeData>();
        public List<OSMConnection> connections = new List<OSMConnection>();

        public Shader generatedShader;
        public Material generatedMaterial;
        public OSMBakeSettings bakeSettings = new OSMBakeSettings();

        public OSMNodeData OutputNode
        {
            get { foreach (var n in nodes) if (n.typeId == OSMNodeIds.Output) return n; return null; }
        }

        public string ShortGuid { get { return string.IsNullOrEmpty(graphGuid) ? "00000000" : graphGuid.Substring(0, 8); } }

        // ---------------------------------------------------------------- queries

        public OSMNodeData FindNode(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < nodes.Count; i++) if (nodes[i].id == id) return nodes[i];
            return null;
        }

        public OSMConnection GetConnectionInto(string nodeId, string portKey)
        {
            for (int i = 0; i < connections.Count; i++)
            {
                var c = connections[i];
                if (c.toNode == nodeId && c.toPort == portKey) return c;
            }
            return null;
        }

        /// <summary>True when node <paramref name="a"/> depends (transitively) on node <paramref name="b"/>, or a == b.</summary>
        public bool DependsOn(string a, string b)
        {
            if (a == b) return true;
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(a);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!seen.Add(cur)) continue;
                for (int i = 0; i < connections.Count; i++)
                {
                    var c = connections[i];
                    if (c.toNode != cur) continue;
                    if (c.fromNode == b) return true;
                    stack.Push(c.fromNode);
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- mutation

        public string GenerateNodeId()
        {
            string id;
            do { id = Guid.NewGuid().ToString("N"); }
            while (nodes.Exists(n => n.ShortId == id.Substring(0, 6)));
            return id;
        }

        public OSMNodeData AddNode(string typeId, Vector2 position, string forcedId = null)
        {
            OSMNodeDef def;
            if (!OSMNodeRegistry.TryGet(typeId, out def)) return null;
            if (def.Unique && nodes.Exists(n => n.typeId == typeId)) return null;
            var d = new OSMNodeData { id = forcedId ?? GenerateNodeId(), typeId = typeId, position = position };
            foreach (var p in def.Params) d.InitDefault(p);
            nodes.Add(d);
            return d;
        }

        public void RemoveNode(string id)
        {
            nodes.RemoveAll(n => n.id == id);
            connections.RemoveAll(c => c.fromNode == id || c.toNode == id);
        }

        /// <summary>Connects two ports, replacing any existing connection into the same input.</summary>
        public bool Connect(string fromNode, string fromPort, string toNode, string toPort)
        {
            if (fromNode == toNode || DependsOn(fromNode, toNode)) return false;
            connections.RemoveAll(c => c.toNode == toNode && c.toPort == toPort);
            connections.Add(new OSMConnection { fromNode = fromNode, fromPort = fromPort, toNode = toNode, toPort = toPort });
            return true;
        }

        public void Disconnect(string fromNode, string fromPort, string toNode, string toPort)
        {
            connections.RemoveAll(c => c.fromNode == fromNode && c.fromPort == fromPort && c.toNode == toNode && c.toPort == toPort);
        }

        /// <summary>Drops connections whose endpoints no longer exist or whose types are no longer compatible.</summary>
        public int PruneInvalidConnections()
        {
            return connections.RemoveAll(c => !IsConnectionValid(c));
        }

        public bool IsConnectionValid(OSMConnection c)
        {
            var from = FindNode(c.fromNode);
            var to = FindNode(c.toNode);
            if (from == null || to == null) return false;
            OSMNodeDef fd, td;
            if (!OSMNodeRegistry.TryGet(from.typeId, out fd) || !OSMNodeRegistry.TryGet(to.typeId, out td)) return false;
            OSMPortDef? o = null, i = null;
            foreach (var p in fd.Outputs(from)) if (p.Key == c.fromPort) o = p;
            foreach (var p in td.Inputs(to)) if (p.Key == c.toPort) i = p;
            if (!o.HasValue || !i.HasValue) return false;
            return OSMPortTypes.CanConnect(o.Value.Type, i.Value.Type);
        }

        // ---------------------------------------------------------------- factories

        public static OSMGraphAsset CreateEmpty()
        {
            var g = CreateInstance<OSMGraphAsset>();
            g.graphGuid = Guid.NewGuid().ToString("N");
            g.AddNode(OSMNodeIds.Output, new Vector2(700, 0));
            return g;
        }

        /// <summary>Workflow A: separate AO / Roughness / Metallic maps packed live into OSM.</summary>
        public static OSMGraphAsset CreateSeparateMapsTemplate()
        {
            var g = CreateEmpty();
            var o = g.OutputNode;
            var albedo = g.AddNode(OSMNodeIds.Albedo, new Vector2(-120, -330));
            var normal = g.AddNode(OSMNodeIds.NormalMap, new Vector2(-120, -120));
            var conv = g.AddNode(OSMNodeIds.NormalConverter, new Vector2(180, -120));
            var ao = g.AddNode(OSMNodeIds.AOMap, new Vector2(-120, 130));
            var rough = g.AddNode(OSMNodeIds.RoughnessMap, new Vector2(-120, 300));
            var r2s = g.AddNode(OSMNodeIds.RoughToSmooth, new Vector2(180, 300));
            var metal = g.AddNode(OSMNodeIds.MetallicMap, new Vector2(-120, 470));
            var comb = g.AddNode(OSMNodeIds.Combiner, new Vector2(430, 260));
            g.Connect(albedo.id, "color", o.id, "baseColor");
            g.Connect(normal.id, "normal", conv.id, "normal");
            g.Connect(conv.id, "normal", o.id, "normal");
            g.Connect(ao.id, "ao", comb.id, "ao");
            g.Connect(rough.id, "roughness", r2s.id, "roughness");
            g.Connect(r2s.id, "smoothness", comb.id, "smoothness");
            g.Connect(metal.id, "metallic", comb.id, "metallic");
            g.Connect(comb.id, "osm", o.id, "osm");
            return g;
        }

        /// <summary>Workflow B: an already packed OSM texture.</summary>
        public static OSMGraphAsset CreatePackedTemplate()
        {
            var g = CreateEmpty();
            var o = g.OutputNode;
            var albedo = g.AddNode(OSMNodeIds.Albedo, new Vector2(300, -330));
            var normal = g.AddNode(OSMNodeIds.NormalMap, new Vector2(100, -120));
            var conv = g.AddNode(OSMNodeIds.NormalConverter, new Vector2(400, -120));
            var osm = g.AddNode(OSMNodeIds.OSMTexture, new Vector2(300, 130));
            g.Connect(albedo.id, "color", o.id, "baseColor");
            g.Connect(normal.id, "normal", conv.id, "normal");
            g.Connect(conv.id, "normal", o.id, "normal");
            g.Connect(osm.id, "osm", o.id, "osm");
            return g;
        }
    }

    /// <summary>Stable node type identifiers. These are persisted in assets: never rename them.</summary>
    public static class OSMNodeIds
    {
        public const string Output = "osm.output";
        public const string Albedo = "tex.albedo";
        public const string NormalMap = "tex.normal";
        public const string AOMap = "tex.ao";
        public const string RoughnessMap = "tex.roughness";
        public const string MetallicMap = "tex.metallic";
        public const string HeightMap = "tex.height";
        public const string EmissionMap = "tex.emission";
        public const string OSMTexture = "tex.osm";
        public const string RoughToSmooth = "proc.r2s";
        public const string NormalConverter = "proc.normalconv";
        public const string Combiner = "pack.osm";
        public const string Split = "util.split";
        public const string Combine = "util.combine";
        public const string OneMinus = "util.oneminus";
        public const string Multiply = "util.multiply";
        public const string Add = "util.add";
        public const string Lerp = "util.lerp";
        public const string Constant = "util.constant";
    }
}
