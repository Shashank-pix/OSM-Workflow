using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace OSMWorkflow
{
    public sealed class OSMTestResult
    {
        public string Name;
        public bool Passed;
        public string Detail;
    }

    /// <summary>
    /// Runs the packing, normal-convention, orientation and shader-compile checks on the real GPU path.
    /// Use  Tools > OSM Workflow > Run Self-Test  after installing, and after changing Unity or URP versions.
    /// </summary>
    public static class OSMSelfTest
    {
        const float Tol = 2.5f / 255f;
        const string TempFolder = "Assets/__OSMSelfTest";

        [MenuItem("Tools/OSM Workflow/Run Self-Test")]
        public static void RunFromMenu()
        {
            var results = RunAll();
            int failed = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var r in results)
            {
                sb.AppendLine((r.Passed ? "PASS  " : "FAIL  ") + r.Name + (string.IsNullOrEmpty(r.Detail) ? "" : "  -  " + r.Detail));
                if (!r.Passed) failed++;
            }
            Debug.Log("[OSM Workflow] Self-test:\n" + sb);
            EditorUtility.DisplayDialog("OSM Workflow self-test", (failed == 0 ? "All " + results.Count + " checks passed." : failed + " of " + results.Count + " checks FAILED.") + "\n\nDetails are in the Console.", "OK");
        }

        public static List<OSMTestResult> RunAll()
        {
            var list = new List<OSMTestResult>();
            list.Add(Run("Lit shader compiles for the active pipeline", ShaderCompiles));
            list.Add(Run("Lit shader with Height (parallax) compiles", HeightCompiles));
            list.Add(Run("Lit shader with Alpha Clipping compiles", AlphaClipCompiles));
            list.Add(Run("OSM packing: Roughness 0.3 -> S 0.7, AO 0.5, Metal 1", PackingA));
            list.Add(Run("OSM packing: fallbacks AO 1, S 0.5, M 0", PackingFallback));
            list.Add(Run("Roughness-to-Smoothness bypass leaves value unchanged", PackingBypass));
            list.Add(Run("Normal converter: flip X, flip Y, both, none", NormalFlips));
            list.Add(Run("Bake keeps texture orientation (no vertical flip)", Orientation));
            list.Add(Run("Bake to PNG: linear import, metadata, round trip", RoundTrip));
            return list;
        }

        static OSMTestResult Run(string name, System.Func<string> body)
        {
            var r = new OSMTestResult { Name = name };
            try { string err = body(); r.Passed = err == null; r.Detail = err; }
            catch (System.Exception e) { r.Passed = false; r.Detail = e.GetType().Name + ": " + e.Message; }
            return r;
        }

        // ------------------------------------------------------------------ helpers

        static OSMGraphAsset NewGraph() { return OSMGraphAsset.CreateEmpty(); }

        static Color32[] EvalPixels(OSMGraphAsset g, string node, string port, bool signed, int w, int h, out string error)
        {
            var c = OSMGraphCompiler.CompileBake(g, node, port, signed);
            if (!c.Success) { error = "compile failed: " + FirstError(c); return null; }
            var tex = OSMTextureBaker.EvaluateToTexture(c, w, h, false, out error);
            if (tex == null) return null;
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        static string FirstError(OSMCompileResult c)
        {
            foreach (var d in c.Diagnostics) if (d.Severity == OSMSeverity.Error) return d.Message;
            return "unknown";
        }

        static string Near(string what, float got, float expect, float tol)
        {
            return Mathf.Abs(got - expect) <= tol ? null : what + ": expected " + expect.ToString("0.000") + " got " + got.ToString("0.000");
        }

        static OSMNodeData Const(OSMGraphAsset g, float v)
        {
            var n = g.AddNode(OSMNodeIds.Constant, Vector2.zero);
            n.SetFloat("type", 0); n.SetFloat("value", v);
            return n;
        }

        // ------------------------------------------------------------------ tests

        static string ShaderCompiles()
        {
            var g = OSMGraphAsset.CreateSeparateMapsTemplate();
            var r = OSMGraphCompiler.CompileLit(g, new OSMCompileOptions { IncludeDebug = true, ShaderName = "Hidden/OSM Workflow/SelfTest" });
            if (!r.Success) return "compile failed: " + FirstError(r);
            var sh = ShaderUtil.CreateShaderAsset(r.ShaderSource, false);
            try
            {
                if (sh == null) return "Unity could not create the shader.";
                if (ShaderUtil.ShaderHasError(sh)) return OSMShaderMessages.Collect(sh);
                return null;
            }
            finally { if (sh != null) Object.DestroyImmediate(sh); Object.DestroyImmediate(g); }
        }

        static string HeightCompiles()
        {
            var g = OSMGraphAsset.CreateSeparateMapsTemplate();
            var h = g.AddNode(OSMNodeIds.HeightMap, Vector2.zero);
            g.Connect(h.id, "height", g.OutputNode.id, "height");
            var r = OSMGraphCompiler.CompileLit(g, new OSMCompileOptions { IncludeDebug = true, ShaderName = "Hidden/OSM Workflow/SelfTestHeight" });
            if (!r.Success) return "compile failed: " + FirstError(r);
            if (r.ShaderSource.IndexOf("OSM_ParallaxOffset(", System.StringComparison.Ordinal) < 0 || r.ShaderSource.IndexOf("uv += OSM_ParallaxOffset", System.StringComparison.Ordinal) < 0)
                return "parallax code was not generated.";
            var sh = ShaderUtil.CreateShaderAsset(r.ShaderSource, false);
            try
            {
                if (sh == null) return "Unity could not create the shader.";
                if (ShaderUtil.ShaderHasError(sh)) return OSMShaderMessages.Collect(sh);
                return null;
            }
            finally { if (sh != null) Object.DestroyImmediate(sh); Object.DestroyImmediate(g); }
        }

        static string AlphaClipCompiles()
        {
            var g = OSMGraphAsset.CreateSeparateMapsTemplate();
            var o = g.OutputNode;
            o.SetBool("alphaClip", true);
            OSMNodeData albedo = null;
            foreach (var n in g.nodes) if (n.typeId == OSMNodeIds.Albedo) albedo = n;
            g.Connect(albedo.id, "alpha", o.id, "alpha");
            var r = OSMGraphCompiler.CompileLit(g, new OSMCompileOptions { IncludeDebug = true, ShaderName = "Hidden/OSM Workflow/SelfTestAlpha" });
            if (!r.Success) return "compile failed: " + FirstError(r);
            if (r.ShaderSource.IndexOf("OSM_EvalAlpha(", System.StringComparison.Ordinal) < 0) return "alpha function was not generated.";
            var sh = ShaderUtil.CreateShaderAsset(r.ShaderSource, false);
            try
            {
                if (sh == null) return "Unity could not create the shader.";
                if (ShaderUtil.ShaderHasError(sh)) return OSMShaderMessages.Collect(sh);
                return null;
            }
            finally { if (sh != null) Object.DestroyImmediate(sh); Object.DestroyImmediate(g); }
        }

        static string BuildPacking(float rough, float ao, float metal, bool invert, float expectS, float expectAO, float expectM)
        {
            var g = NewGraph();
            var cr = Const(g, rough); var ca = Const(g, ao); var cm = Const(g, metal);
            var r2s = g.AddNode(OSMNodeIds.RoughToSmooth, Vector2.zero);
            r2s.SetBool("invert", invert);
            var comb = g.AddNode(OSMNodeIds.Combiner, Vector2.zero);
            g.Connect(cr.id, "out", r2s.id, "roughness");
            g.Connect(r2s.id, "smoothness", comb.id, "smoothness");
            g.Connect(ca.id, "out", comb.id, "ao");
            g.Connect(cm.id, "out", comb.id, "metallic");
            string err;
            var px = EvalPixels(g, comb.id, "osm", false, 4, 4, out err);
            Object.DestroyImmediate(g);
            if (px == null) return err;
            var p = px[5];
            return Near("AO(R)", p.r / 255f, expectAO, Tol) ?? Near("Smoothness(G)", p.g / 255f, expectS, Tol) ?? Near("Metallic(B)", p.b / 255f, expectM, Tol);
        }

        static string PackingA() { return BuildPacking(0.3f, 0.5f, 1f, true, 0.7f, 0.5f, 1f); }
        static string PackingBypass() { return BuildPacking(0.3f, 1f, 0f, false, 0.3f, 1f, 0f); }

        static string PackingFallback()
        {
            var g = NewGraph();
            var comb = g.AddNode(OSMNodeIds.Combiner, Vector2.zero);
            string err;
            var px = EvalPixels(g, comb.id, "osm", false, 4, 4, out err);
            Object.DestroyImmediate(g);
            if (px == null) return err;
            var p = px[5];
            return Near("AO", p.r / 255f, 1f, Tol) ?? Near("Smoothness", p.g / 255f, 0.5f, Tol) ?? Near("Metallic", p.b / 255f, 0f, Tol);
        }

        static string NormalFlips()
        {
            var input = new Vector3(0.30f, -0.40f, 0.8660254f);
            bool[] fx = { false, true, false, true };
            bool[] fy = { false, false, true, true };
            for (int k = 0; k < 4; k++)
            {
                var g = NewGraph();
                var c = g.AddNode(OSMNodeIds.Constant, Vector2.zero);
                c.SetFloat("type", 1); c.SetVector("vector", new Vector4(input.x, input.y, input.z, 0));
                var conv = g.AddNode(OSMNodeIds.NormalConverter, Vector2.zero);
                conv.SetBool("flipX", fx[k]); conv.SetBool("flipY", fy[k]);
                g.Connect(c.id, "out", conv.id, "normal");
                string err;
                var px = EvalPixels(g, conv.id, "normal", true, 4, 4, out err);
                Object.DestroyImmediate(g);
                if (px == null) return err;
                var p = px[5];
                float ex = fx[k] ? -input.x : input.x, ey = fy[k] ? -input.y : input.y;
                string e = Near("X", p.r / 255f * 2f - 1f, ex, 0.02f) ?? Near("Y", p.g / 255f * 2f - 1f, ey, 0.02f) ?? Near("Z", p.b / 255f * 2f - 1f, input.z, 0.02f);
                if (e != null) return "case flipX=" + fx[k] + " flipY=" + fy[k] + " -> " + e;
            }
            return null;
        }

        static string Orientation()
        {
            // 2x2 point-filtered texture with four distinct colours.
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            src.SetPixels32(new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255), new Color32(255, 255, 0, 255) });
            src.Apply(false, false);
            var g = NewGraph();
            var n = g.AddNode(OSMNodeIds.Albedo, Vector2.zero);
            n.SetObject("texture", src);
            string err;
            var px = EvalPixels(g, n.id, "color", false, 2, 2, out err);
            Object.DestroyImmediate(g);
            var expected = src.GetPixels32();
            Object.DestroyImmediate(src);
            if (px == null) return err;
            for (int i = 0; i < 4; i++)
                if (Mathf.Abs(px[i].r - expected[i].r) > 2 || Mathf.Abs(px[i].g - expected[i].g) > 2 || Mathf.Abs(px[i].b - expected[i].b) > 2)
                    return "pixel " + i + " differs: texture is flipped or shifted during baking.";
            return null;
        }

        static string RoundTrip()
        {
            var g = NewGraph();
            var cr = Const(g, 0.3f); var ca = Const(g, 0.5f); var cm = Const(g, 1f);
            var r2s = g.AddNode(OSMNodeIds.RoughToSmooth, Vector2.zero);
            var comb = g.AddNode(OSMNodeIds.Combiner, Vector2.zero);
            g.Connect(cr.id, "out", r2s.id, "roughness");
            g.Connect(r2s.id, "smoothness", comb.id, "smoothness");
            g.Connect(ca.id, "out", comb.id, "ao");
            g.Connect(cm.id, "out", comb.id, "metallic");

            var compile = OSMGraphCompiler.CompileBake(g, comb.id, "osm", false);
            if (!compile.Success) return "compile failed";
            var s = new OSMBakeSettings { folder = TempFolder, fileName = "SelfTest_OSM", size = 8, mipmaps = false, compression = OSMCompressionChoice.Uncompressed };
            var plan = OSMTextureBaker.Plan(compile, s);
            string msg;
            try
            {
                var tex = OSMTextureBaker.BakeToAsset(g, compile, s, plan, out msg);
                if (tex == null) return msg;
                var imp = OSMTextureTools.GetImporter(tex);
                if (imp == null || imp.sRGBTexture) return "texture was not imported as linear data.";
                OSMTextureMetadata meta;
                if (!OSMTextureTools.TryReadMetadata(tex, out meta)) return "OSM metadata missing after import.";
                if (meta.convention != OSMTextureTools.Convention) return "wrong convention in metadata.";

                // Read back through the GPU exactly like the shader would sample it.
                var rt = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                OSMUtilityShaders.BlitChannel(tex, rt, 0);
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var rb = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
                rb.ReadPixels(new Rect(0, 0, 8, 8), 0, 0, false);
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                var p = rb.GetPixels32()[27]; Object.DestroyImmediate(rb);
                return Near("AO", p.r / 255f, 0.5f, Tol) ?? Near("Smoothness", p.g / 255f, 0.7f, Tol) ?? Near("Metallic", p.b / 255f, 1f, Tol);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                Object.DestroyImmediate(g);
            }
        }
    }
}
