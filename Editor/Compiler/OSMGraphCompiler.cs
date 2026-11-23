using System.Collections.Generic;
using UnityEngine;

namespace OSMWorkflow
{
    public static class OSMGraphValidator
    {
        /// <summary>Structural checks that do not need code generation.</summary>
        public static List<OSMDiagnostic> Validate(OSMGraphAsset g)
        {
            var list = new List<OSMDiagnostic>();

            int outputs = 0;
            foreach (var n in g.nodes)
            {
                OSMNodeDef def;
                if (!OSMNodeRegistry.TryGet(n.typeId, out def))
                {
                    list.Add(Err(n.id, "Unknown node type '" + n.typeId + "'. The graph was probably created with a newer version of the tool."));
                    continue;
                }
                if (n.typeId == OSMNodeIds.Output) outputs++;
            }
            if (outputs == 0) list.Add(Err(null, "The graph has no OSM Lit output node."));
            if (outputs > 1) list.Add(Err(null, "The graph has more than one OSM Lit output node."));

            foreach (var c in g.connections)
            {
                var from = g.FindNode(c.fromNode);
                var to = g.FindNode(c.toNode);
                if (from == null || to == null)
                {
                    list.Add(Err(to != null ? to.id : null, "A connection refers to a node that no longer exists."));
                    continue;
                }
                if (!g.IsConnectionValid(c))
                    list.Add(Err(to.id, "Connection " + c.fromPort + " → " + c.toPort + " is invalid (missing port or incompatible types)."));
            }

            // Cycle detection (the editor prevents cycles, but assets can be edited by hand or merged).
            var state = new Dictionary<string, int>();
            foreach (var n in g.nodes) DetectCycle(g, n.id, state, list);

            return list;
        }

        static bool DetectCycle(OSMGraphAsset g, string id, Dictionary<string, int> state, List<OSMDiagnostic> list)
        {
            int s;
            if (state.TryGetValue(id, out s)) { if (s == 1) { list.Add(Err(id, "Cycle detected: this node depends on itself.")); return true; } return false; }
            state[id] = 1;
            bool found = false;
            foreach (var c in g.connections)
                if (c.toNode == id && g.FindNode(c.fromNode) != null)
                    found |= DetectCycle(g, c.fromNode, state, list);
            state[id] = 2;
            return found;
        }

        static OSMDiagnostic Err(string nodeId, string msg)
        { return new OSMDiagnostic { Severity = OSMSeverity.Error, NodeId = nodeId, Message = msg }; }
    }

    public static class OSMGraphCompiler
    {
        // ====================================================================== Lit shader

        public static OSMCompileResult CompileLit(OSMGraphAsset g, OSMCompileOptions o)
        {
            var res = new OSMCompileResult();
            if (g == null) { res.Diagnostics.Add(new OSMDiagnostic { Severity = OSMSeverity.Error, Message = "No graph." }); return res; }

            res.Diagnostics.AddRange(OSMGraphValidator.Validate(g));
            if (res.HasErrors) return res;

            var output = g.OutputNode;
            var order = TopoOrder(g, output.id, o.OsmOverride != null ? "osm" : null);
            var ctx = new OSMEmitContext(g, o.OsmOverride);
            EmitWithParallax(ctx, g, output, order);
            BuildAlphaFunction(ctx, g, output);

            ReportUnused(g, order, res.Diagnostics);
            res.Diagnostics.AddRange(ctx.Diagnostics);
            res.Bindings.AddRange(ctx.Bindings);
            res.Textures.AddRange(ctx.Textures);
            if (res.HasErrors) return res;

            res.ShaderSource = OSMShaderGenerator.GenerateLit(ctx, o);
            res.Hash = Hash128.Compute(res.ShaderSource).ToString();
            return res;
        }

        // ====================================================================== bake / evaluate

        /// <summary>
        /// Compiles the sub-graph that ends at one output port into a full-screen "evaluation" shader.
        /// The result is rendered on the GPU to bake textures, so baking uses exactly the same node code as the material.
        /// </summary>
        public static OSMCompileResult CompileBake(OSMGraphAsset g, string nodeId, string portKey, bool signedVectorToUnit)
        {
            var res = new OSMCompileResult();
            res.Diagnostics.AddRange(OSMGraphValidator.Validate(g));
            var root = g.FindNode(nodeId);
            if (root == null) { res.Diagnostics.Add(new OSMDiagnostic { Severity = OSMSeverity.Error, Message = "Bake source node not found." }); return res; }
            if (res.HasErrors) return res;

            var order = TopoOrder(g, nodeId, null);
            var ctx = new OSMEmitContext(g, null);
            EmitAll(ctx, order);
            res.Diagnostics.AddRange(ctx.Diagnostics);
            res.Bindings.AddRange(ctx.Bindings);
            res.Textures.AddRange(ctx.Textures);

            string expr; OSMPortType type;
            if (!ctx.TryGetOutput(nodeId, portKey, out expr, out type))
            {
                res.Diagnostics.Add(new OSMDiagnostic { Severity = OSMSeverity.Error, NodeId = nodeId, Message = "The selected node has no output '" + portKey + "'." });
                return res;
            }
            if (res.HasErrors) return res;

            string final;
            switch (type)
            {
                case OSMPortType.Float: final = "float4(" + expr + ", " + expr + ", " + expr + ", 1.0)"; break;
                case OSMPortType.Vector3: final = signedVectorToUnit ? "float4(" + expr + " * 0.5 + 0.5, 1.0)" : "float4(" + expr + ", 1.0)"; break;
                default: final = expr; break;
            }
            res.ShaderSource = OSMShaderGenerator.GenerateBake(ctx, "Hidden/OSM Workflow/Bake_" + g.ShortGuid, final);
            res.Hash = Hash128.Compute(res.ShaderSource).ToString();
            return res;
        }

        // ====================================================================== helpers

        /// <summary>
        /// Parallax needs the height before any other texture is sampled. Nodes feeding the Height input are emitted
        /// first with the original UVs, the UV is shifted, then every remaining node samples with the shifted UV.
        /// </summary>
        static void EmitWithParallax(OSMEmitContext ctx, OSMGraphAsset g, OSMNodeData output, List<OSMNodeData> order)
        {
            var conn = g.GetConnectionInto(output.id, "height");
            var src = conn != null ? g.FindNode(conn.fromNode) : null;
            if (src == null) { EmitAll(ctx, order); return; }

            var first = TopoOrder(g, src.id, null);
            var firstIds = new HashSet<string>();
            foreach (var n in first) firstIds.Add(n.id);
            EmitAll(ctx, first);

            string h; OSMPortType t;
            if (ctx.TryGetOutput(src.id, conn.fromPort, out h, out t))
            {
                string scale = ctx.PropFloat(output, "parallax", output.GetFloat("parallax", 0.02f));
                string hv = OSMPortTypes.Coerce(h, t, OSMPortType.Float);
                ctx.Comment("Parallax: shift the UV by the height before sampling the other maps");
                ctx.Raw("uv += OSM_ParallaxOffset(" + hv + ", " + scale + ", osm_viewDirTS);");
                ctx.Surface.UsesParallax = true;
            }
            var rest = new List<OSMNodeData>();
            foreach (var n in order) if (!firstIds.Contains(n.id)) rest.Add(n);
            EmitAll(ctx, rest);
        }

        /// <summary>
        /// Alpha clipping must also run in the shadow and depth passes, which do not run the main graph.
        /// The nodes upstream of Alpha are therefore emitted a second time into a standalone function.
        /// </summary>
        static void BuildAlphaFunction(OSMEmitContext ctx, OSMGraphAsset g, OSMNodeData output)
        {
            if (!ctx.Surface.AlphaClip) return;
            var conn = g.GetConnectionInto(output.id, "alpha");
            var src = conn != null ? g.FindNode(conn.fromNode) : null;
            if (src == null) { ctx.Surface.AlphaCode = ""; ctx.Surface.AlphaExpr = "1.0"; return; }

            var actx = new OSMEmitContext(g, null);
            EmitAll(actx, TopoOrder(g, src.id, null));
            string a; OSMPortType t;
            if (!actx.TryGetOutput(src.id, conn.fromPort, out a, out t)) { ctx.Surface.AlphaCode = ""; ctx.Surface.AlphaExpr = "1.0"; return; }
            ctx.AdoptBindings(actx);
            ctx.Surface.AlphaCode = actx.Code;
            ctx.Surface.AlphaExpr = OSMPortTypes.Coerce(a, t, OSMPortType.Float);
        }

        static void EmitAll(OSMEmitContext ctx, List<OSMNodeData> order)
        {
            foreach (var n in order)
            {
                var def = OSMNodeRegistry.Get(n.typeId);
                if (def == null) continue;
                ctx.Comment(def.Title + " (" + n.ShortId + ")");
                def.Emit(ctx, n);
            }
        }

        /// <summary>Dependencies first. Only nodes that contribute to the root are visited (dead branches are pruned).</summary>
        public static List<OSMNodeData> TopoOrder(OSMGraphAsset g, string rootId, string ignoredRootPort)
        {
            var order = new List<OSMNodeData>();
            var seen = new HashSet<string>();
            Visit(g, rootId, rootId, ignoredRootPort, seen, order);
            return order;
        }

        static void Visit(OSMGraphAsset g, string id, string rootId, string ignoredRootPort, HashSet<string> seen, List<OSMNodeData> order)
        {
            if (!seen.Add(id)) return;
            foreach (var c in g.connections)
            {
                if (c.toNode != id) continue;
                if (id == rootId && ignoredRootPort != null && c.toPort == ignoredRootPort) continue;
                if (g.FindNode(c.fromNode) == null) continue;
                Visit(g, c.fromNode, rootId, ignoredRootPort, seen, order);
            }
            var n = g.FindNode(id);
            if (n != null) order.Add(n);
        }

        static void ReportUnused(OSMGraphAsset g, List<OSMNodeData> used, List<OSMDiagnostic> diags)
        {
            var set = new HashSet<string>();
            foreach (var n in used) set.Add(n.id);
            foreach (var n in g.nodes)
            {
                if (set.Contains(n.id)) continue;
                var def = OSMNodeRegistry.Get(n.typeId);
                diags.Add(new OSMDiagnostic
                {
                    Severity = OSMSeverity.Info, NodeId = n.id,
                    Message = (def != null ? def.Title : n.typeId) + " is not connected to the output and is ignored."
                });
            }
        }
    }
}
