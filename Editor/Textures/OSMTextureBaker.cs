using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace OSMWorkflow
{
    /// <summary>Small in-memory helper shaders (channel view and difference view).</summary>
    public static class OSMUtilityShaders
    {
        static Shader _channel, _diff;
        static Material _channelMat, _diffMat;

        static readonly string ChannelSrc = @"Shader 'Hidden/OSM Workflow/ChannelView'
{
    Properties { _MainTex('t', 2D) = 'white' {} _Mode('m', Float) = 0 }
    SubShader { Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include 'UnityCG.cginc'
            sampler2D _MainTex; float _Mode;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_Mode < 0.5) return float4(c.rgb, 1);
                if (_Mode < 1.5) return float4(c.rrr, 1);
                if (_Mode < 2.5) return float4(c.ggg, 1);
                if (_Mode < 3.5) return float4(c.bbb, 1);
                return float4(c.aaa, 1);
            }
        ENDCG }
    }
}".Replace('\'', '"');

        static readonly string DiffSrc = @"Shader 'Hidden/OSM Workflow/Diff'
{
    Properties { _A('a', 2D) = 'white' {} _B('b', 2D) = 'white' {} _Tol('tol', Float) = 0.01 _Mode('m', Float) = 0 }
    SubShader { Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include 'UnityCG.cginc'
            sampler2D _A; sampler2D _B; float _Tol; float _Mode;
            float4 frag(v2f_img i) : SV_Target
            {
                float3 a = tex2D(_A, i.uv).rgb; float3 b = tex2D(_B, i.uv).rgb;
                float3 d = abs(a - b);
                float m = max(d.r, max(d.g, d.b));
                if (_Mode > 0.5) return float4(m, m, m, 1);
                if (m <= _Tol) return float4(m.xxx * 4.0, 1);
                float k = saturate((m - _Tol) * 8.0);
                return float4(1, 1.0 - k, 0, 1);
            }
        ENDCG }
    }
}".Replace('\'', '"');

        static Shader Make(string src)
        {
            var s = ShaderUtil.CreateShaderAsset(src, false);
            if (s != null) s.hideFlags = HideFlags.HideAndDontSave;
            return s;
        }

        static Material Mat(ref Shader sh, ref Material m, string src)
        {
            if (sh == null) sh = Make(src);
            if (m == null && sh != null) m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return m;
        }

        /// <summary>Renders one channel (0 = RGB, 1..4 = R,G,B,A) of a texture into a linear RenderTexture.</summary>
        public static void BlitChannel(Texture src, RenderTexture dst, int mode)
        {
            var m = Mat(ref _channel, ref _channelMat, ChannelSrc);
            if (m == null || src == null) return;
            m.SetFloat("_Mode", mode);
            var prev = RenderTexture.active;
            Graphics.Blit(src, dst, m);
            RenderTexture.active = prev;
        }

        public static void BlitDiff(Texture a, Texture b, RenderTexture dst, float tolerance, bool raw)
        {
            var m = Mat(ref _diff, ref _diffMat, DiffSrc);
            if (m == null) return;
            m.SetTexture("_A", a);
            m.SetTexture("_B", b);
            m.SetFloat("_Tol", tolerance);
            m.SetFloat("_Mode", raw ? 1f : 0f);
            var prev = RenderTexture.active;
            Graphics.Blit(a, dst, m);
            RenderTexture.active = prev;
        }
    }

    public sealed class OSMBakePlan
    {
        public int Width, Height;
        public string Note;
        public readonly List<KeyValuePair<string, Vector2Int>> Inputs = new List<KeyValuePair<string, Vector2Int>>();
    }

    public static class OSMShaderMessages
    {
        /// <summary>Reads compiler messages through reflection so the tool tolerates API differences between Unity versions.</summary>
        public static string Collect(Shader shader)
        {
            if (shader == null) return "Shader could not be created.";
            var sb = new System.Text.StringBuilder();
            try
            {
                var mi = typeof(ShaderUtil).GetMethod("GetShaderMessages", new[] { typeof(Shader) });
                var arr = mi != null ? mi.Invoke(null, new object[] { shader }) as Array : null;
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var t = item.GetType();
                        object sev = t.GetField("severity") != null ? t.GetField("severity").GetValue(item) : null;
                        string msg = t.GetField("message") != null ? (string)t.GetField("message").GetValue(item) : item.ToString();
                        object line = t.GetField("line") != null ? t.GetField("line").GetValue(item) : null;
                        if (sev != null && sev.ToString().IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        sb.Append(msg);
                        if (line != null) sb.Append(" (line ").Append(line).Append(')');
                        sb.Append('\n');
                    }
                }
            }
            catch (Exception) { /* fall through to generic text */ }
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "The shader has compile errors. Select the generated shader asset and read its inspector for details.";
        }
    }

    public static class OSMTextureBaker
    {
        // ------------------------------------------------------------------ planning

        public static OSMBakePlan Plan(OSMCompileResult compile, OSMBakeSettings s)
        {
            var plan = new OSMBakePlan();
            foreach (var t in compile.Textures)
                if (t.Texture != null) plan.Inputs.Add(new KeyValuePair<string, Vector2Int>(t.Title, new Vector2Int(t.Texture.width, t.Texture.height)));

            int w = Mathf.Max(4, s.size), h = w;
            switch (s.policy)
            {
                case OSMResolutionPolicy.MatchLargestInput:
                    if (plan.Inputs.Count > 0)
                    {
                        var best = plan.Inputs[0].Value;
                        foreach (var i in plan.Inputs) if (i.Value.x * i.Value.y > best.x * best.y) best = i.Value;
                        w = best.x; h = best.y; plan.Note = "Matched to the largest input (" + w + "x" + h + ").";
                    }
                    else plan.Note = "No texture inputs: using the fixed size.";
                    break;
                case OSMResolutionPolicy.MatchFirstInput:
                    if (plan.Inputs.Count > 0) { w = plan.Inputs[0].Value.x; h = plan.Inputs[0].Value.y; plan.Note = "Matched to '" + plan.Inputs[0].Key + "'."; }
                    else plan.Note = "No texture inputs: using the fixed size.";
                    break;
                default:
                    plan.Note = "Fixed output size.";
                    break;
            }
            plan.Width = Mathf.Clamp(w, 1, 16384);
            plan.Height = Mathf.Clamp(h, 1, 16384);
            return plan;
        }

        // ------------------------------------------------------------------ GPU evaluation

        /// <summary>Evaluates a compiled bake shader into a temporary RenderTexture. The caller releases it.</summary>
        public static RenderTexture Evaluate(OSMCompileResult compile, int w, int h, bool sixteenBit, out string error)
        {
            error = null;
            if (!compile.Success) { error = "The graph has errors."; return null; }
            var shader = ShaderUtil.CreateShaderAsset(compile.ShaderSource, false);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
            {
                error = OSMShaderMessages.Collect(shader);
                if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
                return null;
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            compile.ApplyTo(mat);
            var fmt = sixteenBit ? GraphicsFormat.R16G16B16A16_UNorm : GraphicsFormat.R8G8B8A8_UNorm;
            var rt = new RenderTexture(new RenderTextureDescriptor(w, h, fmt, 0)) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            rt.Create();
            var previous = RenderTexture.active;
            Graphics.Blit(Texture2D.whiteTexture, rt, mat);
            RenderTexture.active = previous;
            UnityEngine.Object.DestroyImmediate(mat);
            UnityEngine.Object.DestroyImmediate(shader);
            return rt;
        }

        /// <summary>Evaluates to an in-memory linear Texture2D (8-bit). Used by comparison and self-tests. Caller destroys it.</summary>
        public static Texture2D EvaluateToTexture(OSMCompileResult compile, int w, int h, bool mipmaps, out string error)
        {
            var rt = Evaluate(compile, w, h, false, out error);
            if (rt == null) return null;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipmaps, true) { hideFlags = HideFlags.HideAndDontSave };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(mipmaps, false);
            RenderTexture.active = prev;
            UnityEngine.Object.DestroyImmediate(rt);
            return tex;
        }

        // ------------------------------------------------------------------ saving

        public static string AssetPath(OSMBakeSettings s)
        {
            string folder = (s.folder ?? "Assets").Replace('\\', '/').TrimEnd('/');
            string name = string.IsNullOrWhiteSpace(s.fileName) ? "Packed_OSM" : s.fileName.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return folder + "/" + name + ".png";
        }

        /// <summary>
        /// Bakes the compiled sub-graph to a PNG inside the project, configures the importer as linear data
        /// and stores OSM metadata. Source textures are never touched.
        /// </summary>
        public static Texture2D BakeToAsset(OSMGraphAsset graph, OSMCompileResult compile, OSMBakeSettings s, OSMBakePlan plan, out string message)
        {
            try
            {
                EditorUtility.DisplayProgressBar("OSM Workflow", "Baking the OSM texture on the GPU...", 0.4f);
                return BakeToAssetInner(graph, compile, s, plan, out message);
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static Texture2D BakeToAssetInner(OSMGraphAsset graph, OSMCompileResult compile, OSMBakeSettings s, OSMBakePlan plan, out string message)
        {
            string path = AssetPath(s);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) { message = "The output folder must be inside the project's Assets folder."; return null; }

            bool sixteen = s.bitDepth == OSMBitDepth.Bits16;
            string error;
            var rt = Evaluate(compile, plan.Width, plan.Height, sixteen, out error);
            if (rt == null) { message = "Bake failed: " + error; return null; }

            byte[] png;
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                if (!sixteen)
                {
                    var tex = new Texture2D(plan.Width, plan.Height, TextureFormat.RGBA32, false, true);
                    tex.ReadPixels(new Rect(0, 0, plan.Width, plan.Height), 0, 0, false);
                    png = tex.EncodeToPNG();
                    UnityEngine.Object.DestroyImmediate(tex);
                }
                else
                {
                    var tex = new Texture2D(plan.Width, plan.Height, TextureFormat.RGBAFloat, false, true);
                    tex.ReadPixels(new Rect(0, 0, plan.Width, plan.Height), 0, 0, false);
                    Color[] px = tex.GetPixels();
                    var u16 = new ushort[px.Length * 4];
                    for (int i = 0; i < px.Length; i++)
                    {
                        u16[i * 4 + 0] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(px[i].r) * 65535f);
                        u16[i * 4 + 1] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(px[i].g) * 65535f);
                        u16[i * 4 + 2] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(px[i].b) * 65535f);
                        u16[i * 4 + 3] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(px[i].a) * 65535f);
                    }
                    var bytes = new byte[u16.Length * 2];
                    Buffer.BlockCopy(u16, 0, bytes, 0, bytes.Length);
                    png = ImageConversion.EncodeArrayToPNG(bytes, GraphicsFormat.R16G16B16A16_UNorm, (uint)plan.Width, (uint)plan.Height);
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }
            finally
            {
                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(rt);
            }
            if (png == null || png.Length == 0) { message = "PNG encoding failed."; return null; }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) { message = "Saved '" + path + "' but could not configure its importer."; return null; }
            imp.textureType = TextureImporterType.Default;
            imp.sRGBTexture = false;                                  // packed data is linear
            imp.alphaSource = TextureImporterAlphaSource.None;
            imp.alphaIsTransparency = false;
            imp.mipmapEnabled = s.mipmaps;
            imp.filterMode = s.filter;
            imp.wrapMode = s.wrap;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.isReadable = false;
            imp.maxTextureSize = Mathf.Min(16384, Mathf.NextPowerOfTwo(Mathf.Max(plan.Width, plan.Height)));   // never downscale silently
            switch (sixteen ? OSMCompressionChoice.Uncompressed : s.compression)
            {
                case OSMCompressionChoice.Uncompressed: imp.textureCompression = TextureImporterCompression.Uncompressed; break;
                case OSMCompressionChoice.Compressed: imp.textureCompression = TextureImporterCompression.Compressed; break;
                default: imp.textureCompression = TextureImporterCompression.CompressedHQ; break;
            }
            OSMTextureTools.WriteMetadata(imp, new OSMTextureMetadata
            {
                width = plan.Width, height = plan.Height, bitDepth = sixteen ? 16 : 8,
                sourceGraphGuid = graph != null ? graph.graphGuid : null,
                bakedUtc = DateTime.UtcNow.ToString("o")
            });
            imp.SaveAndReimport();

            var result = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            message = "Baked " + plan.Width + "x" + plan.Height + " (" + (sixteen ? 16 : 8) + "-bit) to " + path + ".";
            if (sixteen && result != null)
            {
                uint bpp = GraphicsFormatUtility.GetBlockSize(result.graphicsFormat);
                message += bpp >= 8 ? " 16-bit precision was preserved by the importer." : " WARNING: Unity imported it as 8-bit; 16-bit precision is NOT preserved on this setup.";
            }
            if (compile.Textures.Count > 0 && plan.Inputs.Count > 0)
            {
                int resampled = 0;
                foreach (var i in plan.Inputs) if (i.Value.x != plan.Width || i.Value.y != plan.Height) resampled++;
                if (resampled > 0) message += " " + resampled + " input(s) were resampled to the output size.";
            }
            return result;
        }
    }
}
