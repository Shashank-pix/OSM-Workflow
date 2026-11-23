using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace OSMWorkflow
{
    public sealed class OSMGraphWindow : EditorWindow, IOSMGraphHost
    {
        [SerializeField] OSMGraphAsset _graph;
        [SerializeField] OSMPreviewSettings _ps = new OSMPreviewSettings();

        OSMGraphView _view;
        OSMPreviewController _preview;
        OSMCompileResult _result;
        readonly List<OSMDiagnostic> _diags = new List<OSMDiagnostic>();

        VisualElement _libList, _inspector, _diagList, _placeholder;
        IMGUIContainer _previewGui;
        Label _status, _toast;
        ObjectField _graphField;
        IVisualElementScheduledItem _compileJob;
        string _selectionKey = "";
        bool _autoCompile;
        bool _pending;
        const string AutoKey = "OSMWorkflow.AutoCompile";
        Button _compileButton;
        Texture _previewTex;
        Vector2 _previewSize;
        bool _previewDirty = true;
        string _diagSignature = "";
        VisualElement _overlay;
        Label _overlayLabel;
        IVisualElementScheduledItem _dotJob;
        int _dots;
        bool _awaitFirstRender;
        bool _building;
        int _compileToken;
        ToolbarButton _matButton;
        RenderTexture _channelRT;

        public OSMGraphAsset Graph { get { return _graph; } }

        // ===================================================================== lifecycle

        public static OSMGraphWindow Open(OSMGraphAsset graph)
        {
            var w = GetWindow<OSMGraphWindow>();
            w.minSize = new Vector2(1000, 600);
            w.Load(graph);
            w.Show();
            w.Focus();
            return w;
        }

        [MenuItem("Window/OSM Workflow/Graph Editor")]
        static void MenuOpen() { Open(Selection.activeObject as OSMGraphAsset); }

        void OnEnable()
        {
            _autoCompile = EditorPrefs.GetBool(AutoKey, false);
            _preview = new OSMPreviewController();
            Undo.undoRedoPerformed += OnUndoRedo;
            OSMEvents.TexturesReimported += OnTexturesChanged;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            OSMEvents.TexturesReimported -= OnTexturesChanged;
            if (_preview != null) { _preview.Dispose(); _preview = null; }
            if (_channelRT != null) { _channelRT.Release(); DestroyImmediate(_channelRT); _channelRT = null; }
        }

        public void CreateGUI()
        {
            BuildUI();
            if (_graph != null) Load(_graph);
            else RefreshPlaceholder();
        }

        public void Load(OSMGraphAsset g)
        {
            _graph = g;
            if (_view == null) return;               // CreateGUI will call us again
            _graphField.SetValueWithoutNotify(g);
            _view.Populate();
            RefreshPlaceholder();
            RefreshInspector();
            _diagSignature = "";
            _previewTex = null;
            CompileNow();
            _view.schedule.Execute(() => _view.FrameAll()).ExecuteLater(60);
        }

        void RefreshPlaceholder()
        {
            if (_placeholder != null) _placeholder.style.display = _graph == null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ===================================================================== UI construction

        void BuildUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;

            // ---- toolbar (file and output actions only; compile controls live in the Preview panel)
            var tb = new Toolbar();
            _graphField = new ObjectField { objectType = typeof(OSMGraphAsset), allowSceneObjects = false };
            _graphField.style.width = 200;
            _graphField.RegisterValueChangedCallback(e => Load(e.newValue as OSMGraphAsset));
            tb.Add(_graphField);
            tb.Add(new ToolbarButton(SaveGraph) { text = "Save", tooltip = "Save the graph asset (Ctrl+S)" });
            tb.Add(new ToolbarSpacer());
            _matButton = new ToolbarButton(GenerateOrUpdateMaterial) { text = "Generate Material" };
            tb.Add(_matButton);
            var matMenu = new ToolbarMenu { text = "", tooltip = "More material options" };
            matMenu.menu.AppendAction("Update linked material", act => UpdateMaterial(),
                act => HasLinkedMaterial() ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            matMenu.menu.AppendAction("Select linked material", act => { if (HasLinkedMaterial()) { Selection.activeObject = _graph.generatedMaterial; EditorGUIUtility.PingObject(_graph.generatedMaterial); } },
                act => HasLinkedMaterial() ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            matMenu.menu.AppendAction("Save as new material…", act => GenerateMaterial());
            tb.Add(matMenu);
            tb.Add(new ToolbarSpacer());
            tb.Add(new ToolbarButton(BakeOsm) { text = "Bake OSM…", tooltip = "Save a packed OSM texture from the graph" });
            tb.Add(new ToolbarButton(() => { if (_graph != null) OSMComparisonWindow.Open(_graph); }) { text = "Compare…", tooltip = "Original (separate maps) versus packed texture" });
            root.Add(tb);

            // ---- body
            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1;
            root.Add(body);

            body.Add(BuildLibrary());

            var split = new TwoPaneSplitView(1, 350, TwoPaneSplitViewOrientation.Horizontal);
            body.Add(split);
            split.style.flexGrow = 1;

            var graphContainer = new VisualElement();
            graphContainer.style.flexGrow = 1;
            _view = new OSMGraphView(this, this);
            graphContainer.Add(_view);

            _placeholder = new Label("Open an OSM Shader Graph: double-click one in the Project window, drop one in the field above,\nor create one with  Assets > Create > OSM Workflow.");
            _placeholder.style.position = Position.Absolute;
            _placeholder.style.left = 0; _placeholder.style.right = 0; _placeholder.style.top = 80;
            _placeholder.style.unityTextAlign = TextAnchor.UpperCenter;
            _placeholder.style.whiteSpace = WhiteSpace.Normal;
            _placeholder.pickingMode = PickingMode.Ignore;
            graphContainer.Add(_placeholder);

            _toast = new Label();
            _toast.style.position = Position.Absolute;
            _toast.style.left = 12; _toast.style.bottom = 12; _toast.style.maxWidth = 520;
            _toast.style.paddingLeft = 10; _toast.style.paddingRight = 10; _toast.style.paddingTop = 5; _toast.style.paddingBottom = 5;
            _toast.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.92f);
            _toast.style.whiteSpace = WhiteSpace.Normal;
            _toast.style.display = DisplayStyle.None;
            _toast.pickingMode = PickingMode.Ignore;
            graphContainer.Add(_toast);

            split.Add(graphContainer);
            split.Add(BuildRightPanel());

            root.schedule.Execute(Poll).Every(250);
            root.RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.ctrlKey || e.commandKey) && e.keyCode == KeyCode.S) { SaveGraph(); e.StopPropagation(); }
            });
        }

        VisualElement BuildLibrary()
        {
            var col = new VisualElement();
            col.style.width = 190;
            col.style.borderRightWidth = 1;
            col.style.borderRightColor = new Color(0, 0, 0, 0.5f);
            col.style.backgroundColor = new Color(0.2f, 0.2f, 0.21f);

            var head = new Label("Node Library");
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.style.marginLeft = 6; head.style.marginTop = 4;
            col.Add(head);

            var search = new ToolbarSearchField();
            search.style.marginLeft = 4; search.style.marginRight = 4; search.style.width = StyleKeyword.Auto;
            search.RegisterValueChangedCallback(e => FillLibrary(e.newValue));
            col.Add(search);

            var hint = new Label("Click to add at the centre of the view. You can also press Space or right-click in the graph.");
            hint.style.whiteSpace = WhiteSpace.Normal; hint.style.fontSize = 10; hint.style.marginLeft = 6; hint.style.marginRight = 6;
            hint.style.color = new Color(0.7f, 0.7f, 0.7f);
            col.Add(hint);

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            _libList = scroll;
            col.Add(scroll);
            FillLibrary("");
            return col;
        }

        void FillLibrary(string filter)
        {
            _libList.Clear();
            filter = (filter ?? "").Trim().ToLowerInvariant();
            foreach (var cat in OSMNodeRegistry.CategoryOrder)
            {
                var defs = OSMNodeRegistry.All.Where(d => d.Category == cat && !d.Unique &&
                    (filter.Length == 0 || d.Title.ToLowerInvariant().Contains(filter) || d.Help.ToLowerInvariant().Contains(filter))).ToList();
                if (defs.Count == 0) continue;
                var h = new Label(cat);
                h.style.unityFontStyleAndWeight = FontStyle.Bold; h.style.marginTop = 6; h.style.marginLeft = 6;
                h.style.color = new Color(0.75f, 0.85f, 1f);
                _libList.Add(h);
                foreach (var d in defs)
                {
                    var def = d;
                    var b = new Button(() => AddNodeAt(def.TypeId, _view.ViewCenterInGraph() + new Vector2(UnityEngine.Random.Range(-30, 30), UnityEngine.Random.Range(-30, 30))))
                    { text = def.Title, tooltip = def.Help };
                    b.style.unityTextAlign = TextAnchor.MiddleLeft;
                    b.style.marginTop = 1; b.style.marginBottom = 1; b.style.marginLeft = 10;
                    _libList.Add(b);
                }
            }
        }

        VisualElement BuildRightPanel()
        {
            var scroll = new ScrollView();
            scroll.style.minWidth = 280;
            scroll.style.backgroundColor = new Color(0.2f, 0.2f, 0.21f);

            scroll.Add(Header("Preview"));

            // compile controls: checkbox for automatic compilation, button only needed for manual mode
            var compileRow = new VisualElement();
            compileRow.style.flexDirection = FlexDirection.Row;
            compileRow.style.alignItems = Align.Center;
            compileRow.style.marginLeft = 6; compileRow.style.marginRight = 6; compileRow.style.marginTop = 2;
            var autoToggle = new Toggle("Auto compile") { value = _autoCompile, tooltip = "Compile automatically after every change. Turn off to compile manually with the Compile button." };
            autoToggle.style.marginLeft = 0;
            _compileButton = new Button(() => CompileNow()) { text = "Compile", tooltip = "Compile the graph and refresh the preview" };
            _compileButton.style.display = _autoCompile ? DisplayStyle.None : DisplayStyle.Flex;
            autoToggle.RegisterValueChangedCallback(e =>
            {
                _autoCompile = e.newValue;
                EditorPrefs.SetBool(AutoKey, _autoCompile);
                _compileButton.style.display = _autoCompile ? DisplayStyle.None : DisplayStyle.Flex;
                if (_autoCompile && _pending) CompileNow();
            });
            compileRow.Add(autoToggle);
            compileRow.Add(_compileButton);
            _status = new Label("Ready");
            _status.style.flexGrow = 1;
            _status.style.unityTextAlign = TextAnchor.MiddleRight;
            compileRow.Add(_status);
            scroll.Add(compileRow);

            var previewHost = new VisualElement();
            previewHost.style.height = 270;
            _previewGui = new IMGUIContainer(OnPreviewGUI);
            _previewGui.style.flexGrow = 1;
            previewHost.Add(_previewGui);

            // Loading overlay shown while a new shader is being built (the build blocks the editor for a moment).
            _overlay = new VisualElement();
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0; _overlay.style.right = 0; _overlay.style.top = 0; _overlay.style.bottom = 0;
            _overlay.style.backgroundColor = new Color(0.08f, 0.08f, 0.09f, 0.78f);
            _overlay.style.alignItems = Align.Center;
            _overlay.style.justifyContent = Justify.Center;
            _overlay.style.display = DisplayStyle.None;
            _overlay.pickingMode = PickingMode.Ignore;
            _overlayLabel = new Label("Compiling shader");
            _overlayLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _overlayLabel.style.fontSize = 13;
            var overlayHint = new Label("The first compile of a new shader can take a few seconds.");
            overlayHint.style.fontSize = 10;
            overlayHint.style.color = new Color(0.75f, 0.75f, 0.75f);
            var bar = new ProgressBar { lowValue = 0, highValue = 100, value = 100, title = "" };
            bar.style.width = 160; bar.style.marginTop = 6;
            _overlay.Add(_overlayLabel); _overlay.Add(bar); _overlay.Add(overlayHint);
            previewHost.Add(_overlay);
            scroll.Add(previewHost);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            var view = new DropdownField("View", new List<string>(OSMViewModes.Names), Mathf.Clamp(_ps.viewMode, 0, OSMViewModes.Names.Length - 1));
            view.tooltip = "Lit shows full lighting. The other modes show a single channel as the shader reads it.";
            view.style.flexGrow = 1;
            view.RegisterValueChangedCallback(e => { _ps.viewMode = view.index; if (_preview != null && _preview.Material != null) _preview.Material.SetFloat("_OSM_Debug", _ps.viewMode); PreviewChanged(); });
            var mesh = new DropdownField("Mesh", new List<string> { "Sphere", "Quad", "Cube" }, (int)_ps.mesh);
            mesh.style.flexGrow = 1;
            mesh.RegisterValueChangedCallback(e => { _ps.mesh = (OSMPreviewMesh)mesh.index; PreviewChanged(); });
            var labelW = 40;
            var vl = view.Q<Label>(null, "unity-base-field__label"); if (vl != null) vl.style.minWidth = labelW;
            var ml = mesh.Q<Label>(null, "unity-base-field__label"); if (ml != null) ml.style.minWidth = labelW;
            row.Add(view); row.Add(mesh);
            scroll.Add(row);

            var light = new Foldout { text = "Lighting and camera", value = false };
            light.style.marginLeft = 6;
            var exp = new Slider("Exposure", 0.2f, 3f) { value = _ps.exposure, showInputField = true };
            exp.RegisterValueChangedCallback(e => { _ps.exposure = e.newValue; PreviewChanged(); });
            light.Add(exp);
            var yaw = new Slider("Light yaw", -180f, 180f) { value = _ps.lightYaw, showInputField = true };
            yaw.RegisterValueChangedCallback(e => { _ps.lightYaw = e.newValue; PreviewChanged(); });
            light.Add(yaw);
            var pitch = new Slider("Light pitch", -90f, 90f) { value = _ps.lightPitch, showInputField = true };
            pitch.RegisterValueChangedCallback(e => { _ps.lightPitch = e.newValue; PreviewChanged(); });
            light.Add(pitch);
            light.Add(new Button(() => { _ps.orbit = new Vector2(-20, 15); _ps.zoom = 1f; PreviewChanged(); }) { text = "Reset camera" });
            scroll.Add(light);

            scroll.Add(Header("Inspector"));
            _inspector = new VisualElement();
            _inspector.style.marginLeft = 6; _inspector.style.marginRight = 6;
            scroll.Add(_inspector);

            scroll.Add(Header("Diagnostics"));
            _diagList = new VisualElement();
            _diagList.style.marginLeft = 6; _diagList.style.marginRight = 6;
            scroll.Add(_diagList);
            return scroll;
        }

        static Label Header(string text)
        {
            var l = new Label(text);
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.backgroundColor = new Color(0.14f, 0.14f, 0.15f);
            l.style.paddingLeft = 6; l.style.paddingTop = 3; l.style.paddingBottom = 3;
            l.style.marginTop = 6;
            return l;
        }

        // ===================================================================== preview IMGUI

        void ShowOverlay(string text)
        {
            if (_overlay == null) return;
            _overlayLabel.text = text;
            _overlay.style.display = DisplayStyle.Flex;
            if (_dotJob == null) _dotJob = _overlay.schedule.Execute(() => { _dots = (_dots + 1) % 4; _overlayLabel.text = text + new string('.', _dots); }).Every(250);
            else _dotJob.Resume();
        }

        void HideOverlay()
        {
            if (_overlay == null) return;
            _overlay.style.display = DisplayStyle.None;
            if (_dotJob != null) _dotJob.Pause();
        }

        void PreviewChanged()
        {
            _previewDirty = true;
            if (_previewGui != null) _previewGui.MarkDirtyRepaint();
        }

        void OnPreviewGUI()
        {
            var rect = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var e = Event.current;
            if (rect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDrag && e.button == 0) { _ps.orbit += new Vector2(-e.delta.x, -e.delta.y) * 0.6f; e.Use(); PreviewChanged(); }
                else if (e.type == EventType.ScrollWheel) { _ps.zoom = Mathf.Clamp(_ps.zoom - e.delta.y * 0.03f, 0.4f, 3f); e.Use(); PreviewChanged(); }
            }
            if (e.type != EventType.Repaint) return;

            if (_preview == null || _preview.Material == null)
            {
                GUI.Label(rect, _graph == null ? "No graph" : "No preview yet.\nFix the errors listed under Diagnostics.", new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true });
                return;
            }
            // Render only when something changed or the panel was resized; otherwise redraw the cached image.
            if (_previewDirty || _previewTex == null || _previewSize != rect.size)
            {
                _previewTex = _preview.Render(rect, _preview.Material, _ps);
                _previewSize = rect.size;
                _previewDirty = false;
            }
            if (_previewTex != null) GUI.DrawTexture(rect, _previewTex, ScaleMode.StretchToFill, false);
            if (_awaitFirstRender)
            {
                // The first draw with a new shader is where Unity compiles it; the overlay stays until that draw is done.
                _awaitFirstRender = false;
                rootVisualElement.schedule.Execute(HideOverlay).ExecuteLater(10);
            }
        }

        // ===================================================================== compile pipeline

        void RequestCompile()
        {
            if (rootVisualElement == null) return;
            if (!_autoCompile)
            {
                _pending = true;      // wait for the Compile button
                UpdateStatus();
                return;
            }
            if (_compileJob != null) _compileJob.Pause();
            _compileJob = rootVisualElement.schedule.Execute(CompileNow);
            _compileJob.ExecuteLater(120);
        }

        void CompileNow()
        {
            _pending = false;
            int token = ++_compileToken;
            if (_graph == null || _view == null) { _diags.Clear(); UpdateStatus(); return; }

            // Phase 1 (fast): turn the graph into shader source.
            var result = OSMGraphCompiler.CompileLit(_graph, new OSMCompileOptions
            {
                IncludeDebug = true,
                ShaderName = "Hidden/OSM Workflow/Preview_" + _graph.ShortGuid
            });

            // Phase 2 (can be slow): Unity builds the shader. Show the loading overlay first and let it paint.
            bool build = result.Success && _preview.NeedsShaderBuild(result);
            if (build && _overlay != null)
            {
                _building = true;
                ShowOverlay("Compiling shader");
                UpdateStatus();
                if (_previewGui != null) _previewGui.MarkDirtyRepaint();
                Repaint();
                rootVisualElement.schedule.Execute(() => { if (token == _compileToken) FinishCompile(result, true); }).ExecuteLater(60);
            }
            else FinishCompile(result, false);
        }

        void FinishCompile(OSMCompileResult result, bool built)
        {
            _building = false;
            _result = result;
            _diags.Clear();
            _diags.AddRange(result.Diagnostics);

            bool shaderOk = false;
            if (result.Success)
            {
                string err;
                shaderOk = _preview.Apply(result, _ps.viewMode, out err);
                if (!shaderOk)
                    _diags.Add(new OSMDiagnostic { Severity = OSMSeverity.Error, Message = "The generated preview shader failed to compile (the last working preview is kept):\n" + err });
            }

            if (built && shaderOk) _awaitFirstRender = true;     // overlay is hidden after the first draw
            else HideOverlay();

            ApplyBadges();
            RebuildDiagnostics();
            UpdateStatus();
            PreviewChanged();
        }

        void ApplyBadges()
        {
            var worst = new Dictionary<string, OSMDiagnostic>();
            foreach (var d in _diags)
            {
                if (d.NodeId == null) continue;
                OSMDiagnostic cur;
                if (!worst.TryGetValue(d.NodeId, out cur) || d.Severity > cur.Severity) worst[d.NodeId] = d;
            }
            foreach (var kv in _view.NodeViews)
            {
                OSMDiagnostic d;
                if (worst.TryGetValue(kv.Key, out d)) kv.Value.SetBadge(d.Severity, d.Message);
                else kv.Value.SetBadge(null, null);
            }
        }

        void RebuildDiagnostics()
        {
            var sig = new System.Text.StringBuilder();
            foreach (var d in _diags) sig.Append((int)d.Severity).Append('|').Append(d.NodeId).Append('|').Append(d.Message).Append(d.Fix != null ? "|f" : "").Append('\n');
            string signature = sig.ToString();
            if (signature == _diagSignature) return;     // nothing changed: keep the existing rows
            _diagSignature = signature;
            _diagList.Clear();
            if (_diags.Count == 0) { _diagList.Add(new Label("No issues.")); return; }
            foreach (var d in _diags.OrderByDescending(x => x.Severity))
            {
                var diag = d;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.FlexStart;
                row.style.marginBottom = 3;

                string tag = d.Severity == OSMSeverity.Error ? "ERROR" : (d.Severity == OSMSeverity.Warning ? "WARN" : "INFO");
                var tagLabel = new Label(tag);
                tagLabel.style.width = 44;
                tagLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                tagLabel.style.color = d.Severity == OSMSeverity.Error ? new Color(1f, 0.45f, 0.45f) : (d.Severity == OSMSeverity.Warning ? new Color(1f, 0.85f, 0.35f) : new Color(0.6f, 0.8f, 1f));
                row.Add(tagLabel);

                var msg = new Label(d.Message);
                msg.style.flexGrow = 1; msg.style.flexShrink = 1; msg.style.whiteSpace = WhiteSpace.Normal;
                row.Add(msg);

                if (d.NodeId != null && _graph.FindNode(d.NodeId) != null)
                    row.Add(new Button(() => _view.SelectNode(diag.NodeId, true)) { text = "Show" });
                if (d.Fix != null)
                    row.Add(new Button(() => { diag.Fix(); RequestCompile(); }) { text = diag.FixLabel ?? "Fix" });
                _diagList.Add(row);
            }
        }

        void UpdateStatus()
        {
            if (_status == null) return;
            int e = _diags.Count(d => d.Severity == OSMSeverity.Error);
            int w = _diags.Count(d => d.Severity == OSMSeverity.Warning);
            if (_graph == null) { _status.text = "No graph"; return; }
            if (_building) { _status.text = "Compiling shader..."; _status.style.color = new Color(0.6f, 0.8f, 1f); return; }
            if (_pending) { _status.text = "Changes not compiled - press Compile"; _status.style.color = new Color(1f, 0.7f, 0.3f); return; }
            if (e > 0) { _status.text = e + " error(s), " + w + " warning(s)"; _status.style.color = new Color(1f, 0.45f, 0.45f); }
            else if (w > 0) { _status.text = "Compiled with " + w + " warning(s)"; _status.style.color = new Color(1f, 0.85f, 0.35f); }
            else { _status.text = "Compiled"; _status.style.color = new Color(0.55f, 1f, 0.6f); }
        }

        // ===================================================================== selection / inspector

        void Poll()
        {
            if (_view == null) return;
            RefreshMaterialButton();
            if (_graph != null)
                titleContent = new GUIContent("OSM: " + _graph.name + (EditorUtility.IsDirty(_graph) ? "*" : ""));
            string key = string.Join(",", _view.SelectedNodes.Select(n => n.Data.id).OrderBy(s => s).ToArray());
            if (key != _selectionKey) { _selectionKey = key; RefreshInspector(); }
        }

        void RefreshInspector()
        {
            if (_inspector == null) return;
            _inspector.Clear();
            if (_graph == null) return;

            var sel = _view.SelectedNodes.ToList();
            if (sel.Count != 1)
            {
                _inspector.Add(new Label(sel.Count == 0
                    ? "Select a node to see its help, issues and texture channels.\n\nTip: drag from a port into empty space or press Space to add nodes. Only compatible ports light up while dragging a connection."
                    : sel.Count + " nodes selected."));
                ((Label)_inspector[0]).style.whiteSpace = WhiteSpace.Normal;
                return;
            }

            var nv = sel[0];
            var title = new Label(nv.Def.Title + "   [" + nv.Def.Category + "]");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _inspector.Add(title);
            var help = new Label(nv.Def.Help);
            help.style.whiteSpace = WhiteSpace.Normal; help.style.marginBottom = 4;
            _inspector.Add(help);

            foreach (var d in _diags.Where(x => x.NodeId == nv.Data.id))
            {
                var l = new Label((d.Severity == OSMSeverity.Info ? "• " : d.Severity == OSMSeverity.Warning ? "! " : "x ") + d.Message);
                l.style.whiteSpace = WhiteSpace.Normal;
                _inspector.Add(l);
            }

            var tex = nv.Data.GetObject<Texture>("texture");
            if (tex != null) _inspector.Add(BuildChannelViewer(tex));
        }

        VisualElement BuildChannelViewer(Texture tex)
        {
            var box = new VisualElement();
            box.style.marginTop = 6;
            var img = new Image { scaleMode = ScaleMode.ScaleToFit };
            img.style.width = 240; img.style.height = 240; img.style.flexShrink = 0; img.style.alignSelf = Align.Center;
            var dd = new DropdownField("Channel view", new List<string> { "RGB", "R", "G", "B", "A" }, 0);
            Action update = () =>
            {
                if (_channelRT == null) { _channelRT = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave }; _channelRT.Create(); }
                OSMUtilityShaders.BlitChannel(tex, _channelRT, dd.index);
                img.image = _channelRT;
                img.MarkDirtyRepaint();
            };
            dd.RegisterValueChangedCallback(e => update());
            box.Add(dd);
            box.Add(img);
            var info = new Label(tex.name + "   " + tex.width + " x " + tex.height);
            info.style.fontSize = 10;
            box.Add(info);
            update();
            return box;
        }

        // ===================================================================== IOSMGraphHost

        public void BeginEdit(string undoName)
        {
            if (_graph != null) Undo.RecordObject(_graph, undoName);
        }

        public void ParamChanged(OSMNodeData data, OSMParamDef param)
        {
            if (param.AffectsPorts)
            {
                _graph.PruneInvalidConnections();
                // Rebuilding inside the field callback would destroy the control that raised it.
                rootVisualElement.schedule.Execute(() => { _view.Populate(); _view.SelectNode(data.id, false); }).ExecuteLater(1);
            }
            ModelChanged(true);
            if (param.Kind == OSMParamKind.Texture) rootVisualElement.schedule.Execute(RefreshInspector).ExecuteLater(1);
        }

        public void ModelChanged(bool recompile)
        {
            if (_graph == null) return;
            EditorUtility.SetDirty(_graph);
            if (recompile) RequestCompile();
        }

        public void Notify(string message)
        {
            if (_toast == null) return;
            _toast.text = message;
            _toast.style.display = DisplayStyle.Flex;
            _toast.schedule.Execute(() => _toast.style.display = DisplayStyle.None).ExecuteLater(5000);
        }

        public void AddNodeAt(string typeId, Vector2 graphPosition)
        {
            if (_graph == null) { Notify("Open a graph first."); return; }
            BeginEdit("Add OSM Node");
            var n = _graph.AddNode(typeId, graphPosition);
            if (n == null) { Notify("This node type can only exist once in a graph."); return; }
            _view.AddNodeView(n);
            _view.SelectNode(n.id, false);
            ModelChanged(true);
        }

        // ===================================================================== toolbar actions

        void SaveGraph()
        {
            if (_graph == null) return;
            EditorUtility.SetDirty(_graph);
            AssetDatabase.SaveAssetIfDirty(_graph);
            Notify("Saved '" + _graph.name + "'.");
        }

        bool HasLinkedMaterial()
        {
            return _graph != null && _graph.generatedMaterial != null && _graph.generatedShader != null;
        }

        void RefreshMaterialButton()
        {
            if (_matButton == null) return;
            bool has = HasLinkedMaterial();
            string text = has ? "Update Material" : "Generate Material";
            if (_matButton.text != text) _matButton.text = text;
            _matButton.tooltip = has
                ? "Re-generate the shader and update '" + AssetDatabase.GetAssetPath(_graph.generatedMaterial) + "' in place. Scene and prefab references keep working."
                : "Create the shader and material for this graph. Afterwards this button updates the same material.";
        }

        void GenerateOrUpdateMaterial()
        {
            if (_graph == null) return;
            if (_graph.generatedMaterial != null && _graph.generatedShader != null) UpdateMaterial();
            else GenerateMaterial();
        }

        void GenerateMaterial()
        {
            if (_graph == null) return;
            CompileNow();
            if (_result == null || !_result.Success) { EditorUtility.DisplayDialog("Cannot generate", "The graph has errors. Fix the items marked ERROR in the Diagnostics panel first.", "OK"); return; }
            string graphPath = AssetDatabase.GetAssetPath(_graph);
            string folder = string.IsNullOrEmpty(graphPath) ? "Assets" : System.IO.Path.GetDirectoryName(graphPath).Replace('\\', '/');
            string path = EditorUtility.SaveFilePanelInProject("Generate OSM Material", _graph.name, "mat", "Choose where to save the material. The shader is written next to it.", folder);
            if (string.IsNullOrEmpty(path)) return;
            string msg;
            bool ok = OSMMaterialGenerator.Generate(_graph, path, out msg);
            RefreshMaterialButton();
            if (ok) Notify(msg); else EditorUtility.DisplayDialog("Generate failed", msg, "OK");
        }

        void UpdateMaterial()
        {
            if (_graph == null) return;
            string msg;
            bool ok = OSMMaterialGenerator.UpdateExisting(_graph, out msg);
            RefreshMaterialButton();
            if (ok) Notify("Updated the linked material. " + msg); else EditorUtility.DisplayDialog("Update failed", msg, "OK");
        }

        void BakeOsm()
        {
            if (_graph == null) return;
            OSMBakeWindow.Open(_graph, OnBaked);
        }

        void OnBaked(Texture2D tex)
        {
            if (_graph == null) return;
            var o = _graph.OutputNode;
            BeginEdit("Add Baked OSM Node");
            var pos = o != null ? o.position + new Vector2(-380, 420) : Vector2.zero;
            var n = _graph.AddNode(OSMNodeIds.OSMTexture, pos);
            if (n == null) return;
            n.SetObject("texture", tex);
            _view.AddNodeView(n);
            _view.SelectNode(n.id, true);
            ModelChanged(true);
            Notify("Added an 'OSM Texture Input' node for the baked texture. Connect its OSM output to OSM Lit to use it.");
        }

        // ===================================================================== events

        void OnUndoRedo()
        {
            if (_graph == null || _view == null) return;
            _view.Populate();
            RefreshInspector();
            RequestCompile();
        }

        void OnTexturesChanged()
        {
            if (_view == null || _graph == null) return;
            foreach (var nv in _view.NodeViews.Values) nv.RefreshState();
            RequestCompile();
        }
    }
}
