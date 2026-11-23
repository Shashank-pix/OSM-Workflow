using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>
    /// A node implementation. Completely independent of the graph view: it only declares ports and
    /// parameters and emits shader code through <see cref="OSMEmitContext"/>.
    /// Add a new node by deriving from this class; the registry discovers it automatically.
    /// </summary>
    public abstract class OSMNodeDef
    {
        static readonly OSMParamDef[] NoParams = new OSMParamDef[0];
        static readonly OSMPortDef[] NoPorts = new OSMPortDef[0];

        public abstract string TypeId { get; }
        public abstract string Title { get; }
        public abstract string Category { get; }
        public virtual string Help { get { return string.Empty; } }
        public virtual bool Deletable { get { return true; } }
        public virtual bool Unique { get { return false; } }

        public virtual IReadOnlyList<OSMParamDef> Params { get { return NoParams; } }
        public virtual IReadOnlyList<OSMPortDef> Inputs(OSMNodeData data) { return NoPorts; }
        public virtual IReadOnlyList<OSMPortDef> Outputs(OSMNodeData data) { return NoPorts; }

        public abstract void Emit(OSMEmitContext c, OSMNodeData d);

        public Color TitleColor
        {
            get
            {
                switch (Category)
                {
                    case "Input": return new Color(0.16f, 0.36f, 0.55f);
                    case "Processing": return new Color(0.50f, 0.32f, 0.12f);
                    case "Packing": return new Color(0.20f, 0.50f, 0.25f);
                    case "Utility": return new Color(0.36f, 0.36f, 0.42f);
                    default: return new Color(0.55f, 0.18f, 0.22f);
                }
            }
        }

        public bool TryGetInput(OSMNodeData d, string key, out OSMPortDef port)
        {
            foreach (var p in Inputs(d)) if (p.Key == key) { port = p; return true; }
            port = default(OSMPortDef);
            return false;
        }

        public bool TryGetOutput(OSMNodeData d, string key, out OSMPortDef port)
        {
            foreach (var p in Outputs(d)) if (p.Key == key) { port = p; return true; }
            port = default(OSMPortDef);
            return false;
        }
    }

    public static class OSMNodeRegistry
    {
        public static readonly string[] CategoryOrder = { "Input", "Processing", "Packing", "Utility", "Output" };

        static Dictionary<string, OSMNodeDef> _map;
        static List<OSMNodeDef> _all;

        static void Init()
        {
            if (_map != null) return;
            _map = new Dictionary<string, OSMNodeDef>();
            _all = new List<OSMNodeDef>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<OSMNodeDef>())
            {
                if (t.IsAbstract || t.GetConstructor(Type.EmptyTypes) == null) continue;
                var def = (OSMNodeDef)Activator.CreateInstance(t);
                if (_map.ContainsKey(def.TypeId))
                {
                    Debug.LogWarning("[OSM Workflow] Duplicate node type id '" + def.TypeId + "' ignored (" + t.Name + ").");
                    continue;
                }
                _map[def.TypeId] = def;
                _all.Add(def);
            }
            _all.Sort((a, b) =>
            {
                int ca = Array.IndexOf(CategoryOrder, a.Category);
                int cb = Array.IndexOf(CategoryOrder, b.Category);
                return ca != cb ? ca.CompareTo(cb) : string.CompareOrdinal(a.Title, b.Title);
            });
        }

        public static IReadOnlyList<OSMNodeDef> All { get { Init(); return _all; } }

        public static bool TryGet(string typeId, out OSMNodeDef def)
        {
            Init();
            if (typeId == null) { def = null; return false; }
            return _map.TryGetValue(typeId, out def);
        }

        public static OSMNodeDef Get(string typeId)
        {
            OSMNodeDef d;
            return TryGet(typeId, out d) ? d : null;
        }
    }
}
