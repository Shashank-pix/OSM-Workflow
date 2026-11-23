using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>Bakes an OSM-typed output of the graph into a PNG texture asset with correct import settings and metadata.</summary>
    public sealed class OSMBakeWindow : EditorWindow
    {
        struct Source { public string NodeId, Port, Label; }

        [SerializeField] OSMGraphAsset _graph;
        Action<Texture2D> _onBaked;
        readonly List<Source> _sources = new List<Source>();
        int _sel;
        OSMCompileResult _compile;
        OSMBakePlan _plan;
        string _message = "";
        MessageType _messageType = MessageType.None;
        Vector2 _scroll;
        bool _dirty = true;

        public static void Open(OSMGraphAsset graph, Action<Texture2D> onBaked)
        {
            var w = GetWindow<OSMBakeWindow>(true, "Bake OSM Texture");
            w.minSize = new Vector2(420, 520);
            w._graph = graph;
            w._onBaked = onBaked;
            w.CollectSources();
            w._dirty = true;
            w.ShowUtility();
        }

        void CollectSources()
        {
            _sources.Clear();
            _sel = 0;
            if (_graph == null) return;
            var o = _graph.OutputNode;
            var feeding = o != null ? _graph.GetConnectionInto(o.id, "osm") : null;
            foreach (var n in _graph.nodes)
            {
                var def = OSMNodeRegistry.Get(n.typeId);
                if (def == null || n.typeId == OSMNodeIds.OSMTexture) continue;
                foreach (var p in def.Outputs(n))
                {
                    if (p.Type != OSMPortType.OSM) continue;
                    _sources.Add(new Source { NodeId = n.id, Port = p.Key, Label = def.Title + " (" + n.ShortId + ")" });
                    if (feeding != null && feeding.fromNode == n.id && feeding.fromPort == p.Key) _sel = _sources.Count - 1;
                }
            }
        }

        void Recompute()
        {
            _dirty = false;
            _compile = null; _plan = null;
            if (_graph == null || _sources.Count == 0) return;
            _sel = Mathf.Clamp(_sel, 0, _sources.Count - 1);
            var src = _sources[_sel];
            _compile = OSMGraphCompiler.CompileBake(_graph, src.NodeId, src.Port, false);
            if (_compile.Success) _plan = OSMTextureBaker.Plan(_compile, _graph.bakeSettings);
        }

        void OnGUI()
        {
            if (_graph == null) { EditorGUILayout.HelpBox("Open this window from the OSM graph editor.", MessageType.Info); return; }
            var s = _graph.bakeSettings;
            if (_dirty) Recompute();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            if (_sources.Count == 0)
            {
                EditorGUILayout.HelpBox("The graph has no node that produces an OSM value. Add an 'OSM Combiner' and connect your maps to it.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUI.BeginChangeCheck();
            var labels = new string[_sources.Count];
            for (int i = 0; i < labels.Length; i++) labels[i] = _sources[i].Label;
            int sel = EditorGUILayout.Popup("OSM source", _sel, labels);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            string name = EditorGUILayout.TextField("File name", string.IsNullOrEmpty(s.fileName) ? _graph.name + "_OSM" : s.fileName);
            EditorGUILayout.BeginHorizontal();
            string folder = EditorGUILayout.TextField("Folder", s.folder);
            if (GUILayout.Button("Choose…", GUILayout.Width(70)))
            {
                string abs = EditorUtility.OpenFolderPanel("Output folder (inside Assets)", Application.dataPath, "");
                if (!string.IsNullOrEmpty(abs))
                {
                    string proj = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/');
                    abs = abs.Replace('\\', '/');
                    if (abs.StartsWith(proj + "/Assets", StringComparison.OrdinalIgnoreCase) || abs.Equals(proj + "/Assets", StringComparison.OrdinalIgnoreCase))
                        folder = abs.Substring(proj.Length + 1);
                    else EditorUtility.DisplayDialog("Folder not in project", "Please choose a folder inside this project's Assets folder.", "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Resolution", EditorStyles.boldLabel);
            var policy = (OSMResolutionPolicy)EditorGUILayout.EnumPopup("Size policy", s.policy);
            int size = s.size;
            if (policy == OSMResolutionPolicy.Fixed)
            {
                int[] sizes = { 128, 256, 512, 1024, 2048, 4096, 8192 };
                var names = Array.ConvertAll(sizes, v => v + " x " + v);
                int idx = Mathf.Max(0, Array.IndexOf(sizes, s.size));
                size = sizes[EditorGUILayout.Popup("Size", idx, names)];
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quality", EditorStyles.boldLabel);
            var depth = (OSMBitDepth)EditorGUILayout.EnumPopup(new GUIContent("Bit depth", "16-bit is experimental: Unity may still import it as 8-bit. The result message reports what actually happened."), s.bitDepth);
            if (depth == OSMBitDepth.Bits16)
                EditorGUILayout.HelpBox("16-bit export is experimental. The texture is forced to uncompressed and the importer result is verified after baking.", MessageType.Info);
            bool mips = EditorGUILayout.Toggle("Generate mipmaps", s.mipmaps);
            var comp = (OSMCompressionChoice)EditorGUILayout.EnumPopup("Compression", s.compression);
            var filter = (FilterMode)EditorGUILayout.EnumPopup("Filter mode", s.filter);
            var wrap = (TextureWrapMode)EditorGUILayout.EnumPopup("Wrap mode", s.wrap);
            bool addNode = EditorGUILayout.Toggle("Add 'OSM Texture Input' node afterwards", s.addInputNodeAfterBake);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_graph, "Edit OSM Bake Settings");
                _sel = sel;
                s.fileName = name; s.folder = folder; s.policy = policy; s.size = size; s.bitDepth = depth;
                s.mipmaps = mips; s.compression = comp; s.filter = filter; s.wrap = wrap; s.addInputNodeAfterBake = addNode;
                EditorUtility.SetDirty(_graph);
                _dirty = true;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Plan", EditorStyles.boldLabel);
            if (_compile == null || !_compile.Success)
            {
                EditorGUILayout.HelpBox("The selected source has errors. Fix them in the graph editor.", MessageType.Error);
            }
            else if (_plan != null)
            {
                EditorGUILayout.LabelField("Output size", _plan.Width + " x " + _plan.Height);
                if (!string.IsNullOrEmpty(_plan.Note)) EditorGUILayout.LabelField(_plan.Note, EditorStyles.miniLabel);
                foreach (var i in _plan.Inputs)
                {
                    bool resampled = i.Value.x != _plan.Width || i.Value.y != _plan.Height;
                    EditorGUILayout.LabelField("  " + i.Key, i.Value.x + " x " + i.Value.y + (resampled ? "   (resampled)" : ""));
                }
                if (_plan.Inputs.Count == 0) EditorGUILayout.LabelField("  No texture inputs: the result uses constant values only.", EditorStyles.miniLabel);
                foreach (var d in _compile.Diagnostics)
                    if (d.Severity == OSMSeverity.Warning) EditorGUILayout.HelpBox(d.Message, MessageType.Warning);
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_compile == null || !_compile.Success || _plan == null))
            {
                if (GUILayout.Button("Bake", GUILayout.Height(30))) Bake();
            }
            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, _messageType);
            EditorGUILayout.EndScrollView();
        }

        void Bake()
        {
            var s = _graph.bakeSettings;
            if (string.IsNullOrWhiteSpace(s.fileName)) s.fileName = _graph.name + "_OSM";
            string path = OSMTextureBaker.AssetPath(s);
            if (File.Exists(Path.GetFullPath(path)) &&
                !EditorUtility.DisplayDialog("Overwrite texture?", "'" + path + "' already exists. Overwrite it? Materials using it will update.", "Overwrite", "Cancel"))
                return;

            string msg;
            var tex = OSMTextureBaker.BakeToAsset(_graph, _compile, s, _plan, out msg);
            _message = msg;
            _messageType = tex != null ? (msg.Contains("WARNING") ? MessageType.Warning : MessageType.Info) : MessageType.Error;
            if (tex != null)
            {
                EditorGUIUtility.PingObject(tex);
                if (_onBaked != null && s.addInputNodeAfterBake) _onBaked(tex);
            }
            Repaint();
        }
    }
}
