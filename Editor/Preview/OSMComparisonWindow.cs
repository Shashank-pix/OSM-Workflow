using UnityEditor;
using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>
    /// Renders the same material twice under identical conditions (same mesh, camera, light, exposure):
    /// "Original" evaluates the separate maps live in the shader, "Packed" samples a texture baked from
    /// the same graph, exactly as a saved OSM texture would be sampled. A difference view with tolerance is provided.
    /// </summary>
    public sealed class OSMComparisonWindow : EditorWindow
    {
        const int RtSize = 384;

        [SerializeField] OSMGraphAsset _graph;
        OSMPreviewSettings _s = new OSMPreviewSettings();
        OSMPreviewController _ctrlA, _ctrlB;
        RenderTexture _rtA, _rtB, _rtDiff;
        Texture2D _packed;
        string _status = "";
        string _stats = "";
        float _tolerance = 0.02f;
        bool _dirty = true, _showDiff = true;
        int _showMode;

        public static void Open(OSMGraphAsset graph)
        {
            var w = GetWindow<OSMComparisonWindow>("OSM Compare");
            w.minSize = new Vector2(760, 440);
            w._graph = graph;
            w.Refresh();
            w.Show();
        }

        void OnEnable()
        {
            _ctrlA = new OSMPreviewController();
            _ctrlB = new OSMPreviewController();
        }

        void OnDisable()
        {
            if (_ctrlA != null) _ctrlA.Dispose();
            if (_ctrlB != null) _ctrlB.Dispose();
            Release(ref _rtA); Release(ref _rtB); Release(ref _rtDiff);
            if (_packed != null) DestroyImmediate(_packed);
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt != null) { rt.Release(); DestroyImmediate(rt); rt = null; }
        }

        static RenderTexture NewRT()
        {
            var rt = new RenderTexture(RtSize, RtSize, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            return rt;
        }

        void Refresh()
        {
            _status = ""; _stats = "";
            if (_graph == null) { _status = "Open this window from the OSM graph editor."; return; }
            string nameKey = _graph.ShortGuid;

            var a = OSMGraphCompiler.CompileLit(_graph, new OSMCompileOptions { IncludeDebug = true, ShaderName = "Hidden/OSM Workflow/CmpA_" + nameKey });
            if (!a.Success) { _status = "The graph has errors. Fix them in the graph editor first."; return; }

            var o = _graph.OutputNode;
            var conn = _graph.GetConnectionInto(o.id, "osm");
            if (conn == null) { _status = "Nothing is connected to the OSM input of OSM Lit, so there is nothing to compare."; return; }
            var src = _graph.FindNode(conn.fromNode);
            if (src.typeId == OSMNodeIds.OSMTexture)
            {
                _status = "The graph already uses a packed OSM texture. Comparison needs a live OSM Combiner (separate maps) feeding the output.";
                return;
            }

            var bake = OSMGraphCompiler.CompileBake(_graph, conn.fromNode, conn.fromPort, false);
            if (!bake.Success) { _status = "The OSM source has errors."; return; }
            var plan = OSMTextureBaker.Plan(bake, _graph.bakeSettings);
            string err;
            var tex = OSMTextureBaker.EvaluateToTexture(bake, plan.Width, plan.Height, _graph.bakeSettings.mipmaps, out err);
            if (tex == null) { _status = "Could not build the packed texture: " + err; return; }
            tex.filterMode = _graph.bakeSettings.filter;
            tex.wrapMode = _graph.bakeSettings.wrap;
            if (_packed != null) DestroyImmediate(_packed);
            _packed = tex;

            var b = OSMGraphCompiler.CompileLit(_graph, new OSMCompileOptions { IncludeDebug = true, ShaderName = "Hidden/OSM Workflow/CmpB_" + nameKey, OsmOverride = _packed });
            string e1, e2;
            if (!_ctrlA.Apply(a, _s.viewMode, out e1) || !_ctrlB.Apply(b, _s.viewMode, out e2))
            {
                _status = "Shader compile failed in the comparison.";
                return;
            }
            _status = "Packed texture: " + plan.Width + "x" + plan.Height + " (8-bit, uncompressed, mipmaps " + (_graph.bakeSettings.mipmaps ? "on" : "off") + "). Texture compression is not simulated.";
            _dirty = true;
        }

        void RenderAll()
        {
            if (_ctrlA.Material == null || _ctrlB.Material == null) return;
            if (_rtA == null) { _rtA = NewRT(); _rtB = NewRT(); _rtDiff = NewRT(); }
            _ctrlA.Material.SetFloat("_OSM_Debug", _s.viewMode);
            _ctrlB.Material.SetFloat("_OSM_Debug", _s.viewMode);
            _ctrlA.RenderTo(_rtA, _ctrlA.Material, _s);
            _ctrlB.RenderTo(_rtB, _ctrlB.Material, _s);
            OSMUtilityShaders.BlitDiff(_rtA, _rtB, _rtDiff, _tolerance, false);
            _dirty = false;
        }

        void ComputeStats()
        {
            if (_rtA == null) return;
            var raw = RenderTexture.GetTemporary(RtSize, RtSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            OSMUtilityShaders.BlitDiff(_rtA, _rtB, raw, _tolerance, true);
            var prev = RenderTexture.active;
            RenderTexture.active = raw;
            var t = new Texture2D(RtSize, RtSize, TextureFormat.RGBA32, false, true);
            t.ReadPixels(new Rect(0, 0, RtSize, RtSize), 0, 0, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(raw);

            var px = t.GetPixels32();
            DestroyImmediate(t);
            int over = 0, counted = 0; float max = 0f; double sum = 0;
            foreach (var p in px)
            {
                // Background pixels are identical in both renders and count as zero difference; skip fully black diff of background by counting only non-background is unnecessary.
                float d = p.r / 255f; counted++; sum += d; if (d > max) max = d; if (d > _tolerance) over++;
            }
            _stats = "Max difference " + max.ToString("0.000") + "   mean " + (sum / counted).ToString("0.0000") + "   pixels above tolerance " + (100f * over / counted).ToString("0.00") + "%";
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60))) Refresh();
                int vm = EditorGUILayout.Popup(_s.viewMode, OSMViewModes.Names, EditorStyles.toolbarPopup, GUILayout.Width(110));
                if (vm != _s.viewMode) { _s.viewMode = vm; _dirty = true; }
                var mesh = (OSMPreviewMesh)EditorGUILayout.EnumPopup(_s.mesh, EditorStyles.toolbarPopup, GUILayout.Width(80));
                if (mesh != _s.mesh) { _s.mesh = mesh; _dirty = true; }
                GUILayout.Space(8);
                GUILayout.Label("Tolerance", EditorStyles.miniLabel);
                float tol = GUILayout.HorizontalSlider(_tolerance, 0f, 0.1f, GUILayout.Width(110));
                if (!Mathf.Approximately(tol, _tolerance)) { _tolerance = tol; _dirty = true; }
                GUILayout.Label(_tolerance.ToString("0.000"), EditorStyles.miniLabel, GUILayout.Width(40));
                _showDiff = GUILayout.Toggle(_showDiff, "Difference", EditorStyles.toolbarButton, GUILayout.Width(70));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Compute stats", EditorStyles.toolbarButton, GUILayout.Width(90))) { if (_dirty) RenderAll(); ComputeStats(); }
            }

            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, _ctrlB != null && _ctrlB.Material != null ? MessageType.Info : MessageType.Warning);

            var area = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var e = Event.current;
            if (area.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDrag) { _s.orbit += new Vector2(-e.delta.x, -e.delta.y) * 0.6f; _dirty = true; e.Use(); Repaint(); }
                if (e.type == EventType.ScrollWheel) { _s.zoom = Mathf.Clamp(_s.zoom - e.delta.y * 0.03f, 0.4f, 3f); _dirty = true; e.Use(); Repaint(); }
            }

            if (e.type == EventType.Repaint)
            {
                if (_dirty) RenderAll();
                int cols = _showDiff ? 3 : 2;
                float w = area.width / cols;
                string[] titles = { "Original (separate maps)", "Packed (baked OSM texture)", "Difference" };
                RenderTexture[] rts = { _rtA, _rtB, _rtDiff };
                for (int i = 0; i < cols; i++)
                {
                    var r = new Rect(area.x + i * w + 2, area.y + 18, w - 4, area.height - 22);
                    GUI.Label(new Rect(r.x, area.y, r.width, 18), titles[i], EditorStyles.boldLabel);
                    if (rts[i] != null)
                    {
                        float side = Mathf.Min(r.width, r.height);
                        GUI.DrawTexture(new Rect(r.x + (r.width - side) * 0.5f, r.y, side, side), rts[i], ScaleMode.ScaleToFit, false);
                    }
                }
            }

            if (!string.IsNullOrEmpty(_stats)) EditorGUILayout.LabelField(_stats, EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField("Small differences are expected from mipmaps, filtering and texture compression; the two renders are not guaranteed bit-identical.", EditorStyles.miniLabel);
        }
    }
}
