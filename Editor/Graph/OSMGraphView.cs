using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace OSMWorkflow
{
    /// <summary>Thin view over the graph asset. All truth lives in <see cref="OSMGraphAsset"/>.</summary>
    public sealed class OSMGraphView : GraphView
    {
        [Serializable]
        sealed class ClipRef { public string nodeId; public string key; public string path; }

        // Object references are stored as asset paths so the clipboard text never depends on Unity's internal object ids.
        [Serializable]
        sealed class Clip
        {
            public List<OSMNodeData> nodes = new List<OSMNodeData>();
            public List<OSMConnection> connections = new List<OSMConnection>();
            public List<ClipRef> refs = new List<ClipRef>();
        }

        public readonly Dictionary<string, OSMNodeView> NodeViews = new Dictionary<string, OSMNodeView>();
        readonly IOSMGraphHost _host;
        readonly EditorWindow _window;
        OSMSearchProvider _search;
        readonly MiniMap _mini;
        const string MiniKey = "OSMWorkflow.MiniMapOn";

        public OSMGraphView(IOSMGraphHost host, EditorWindow window)
        {
            _host = host;
            _window = window;
            style.flexGrow = 1;

            var guids = AssetDatabase.FindAssets("OSMGraphStyle t:StyleSheet");
            if (guids.Length > 0)
            {
                var ss = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
                if (ss != null) styleSheets.Add(ss);
            }

            SetupZoom(0.2f, 3f);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();

            // Mini-map: fixed small size, top-left, with its own on/off checkbox (remembered between sessions).
            _mini = new MiniMap { anchored = true };
            _mini.style.position = Position.Absolute;
            _mini.style.left = 8; _mini.style.top = 30;
            _mini.style.width = 100; _mini.style.height = 70;
            Add(_mini);

            var miniToggle = new Toggle("Mini-map") { value = EditorPrefs.GetBool(MiniKey, true) };
            miniToggle.style.position = Position.Absolute;
            miniToggle.style.left = 8; miniToggle.style.top = 6;
            miniToggle.style.marginLeft = 0; miniToggle.style.marginTop = 0;
            miniToggle.style.paddingLeft = 4; miniToggle.style.paddingRight = 6;
            miniToggle.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 0.85f);
            miniToggle.style.fontSize = 10;
            miniToggle.RegisterValueChangedCallback(e => { EditorPrefs.SetBool(MiniKey, e.newValue); _mini.style.display = e.newValue ? DisplayStyle.Flex : DisplayStyle.None; });
            miniToggle.RegisterCallback<MouseDownEvent>(e => e.StopPropagation());
            Add(miniToggle);
            _mini.style.display = miniToggle.value ? DisplayStyle.Flex : DisplayStyle.None;

            graphViewChanged = OnChanged;
            nodeCreationRequest = ctx => OpenSearch(ctx.screenMousePosition);
            serializeGraphElements = SerializeElements;
            canPasteSerializedData = data => !string.IsNullOrEmpty(data) && data.StartsWith("{\"nodes\"", StringComparison.Ordinal);
            unserializeAndPaste = Paste;
        }

        // ------------------------------------------------------------------ search / coordinates

        void OpenSearch(Vector2 screenPos)
        {
            if (_search == null) _search = ScriptableObject.CreateInstance<OSMSearchProvider>();
            _search.Init(this, _host);
            SearchWindow.Open(new SearchWindowContext(screenPos), _search);
        }

        public Vector2 ScreenToGraph(Vector2 screenPos)
        {
            var root = _window.rootVisualElement;
            var windowMouse = root.ChangeCoordinatesTo(root.parent, screenPos - _window.position.position);
            return contentViewContainer.WorldToLocal(windowMouse);
        }

        public Vector2 ViewCenterInGraph()
        {
            return contentViewContainer.WorldToLocal(worldBound.center);
        }

        // ------------------------------------------------------------------ population

        public void Populate()
        {
            foreach (var e in edges.ToList()) RemoveElement(e);
            foreach (var n in nodes.ToList()) RemoveElement(n);
            NodeViews.Clear();

            var g = _host.Graph;
            if (g == null) return;

            foreach (var d in g.nodes)
            {
                OSMNodeDef def;
                if (!OSMNodeRegistry.TryGet(d.typeId, out def)) continue;
                var nv = new OSMNodeView(_host, d, def);
                AddElement(nv);
                NodeViews[d.id] = nv;
            }
            foreach (var c in g.connections)
            {
                OSMNodeView a, b;
                if (!NodeViews.TryGetValue(c.fromNode, out a) || !NodeViews.TryGetValue(c.toNode, out b)) continue;
                Port po, pi;
                if (!a.OutPorts.TryGetValue(c.fromPort, out po) || !b.InPorts.TryGetValue(c.toPort, out pi)) continue;
                AddElement(po.ConnectTo(pi));
            }
        }

        /// <summary>Adds a single node view without rebuilding the whole graph.</summary>
        public OSMNodeView AddNodeView(OSMNodeData d)
        {
            OSMNodeDef def;
            if (!OSMNodeRegistry.TryGet(d.typeId, out def)) return null;
            var nv = new OSMNodeView(_host, d, def);
            AddElement(nv);
            NodeViews[d.id] = nv;
            return nv;
        }

        public void SelectNode(string id, bool frame)
        {
            OSMNodeView nv;
            if (!NodeViews.TryGetValue(id ?? "", out nv)) return;
            ClearSelection();
            AddToSelection(nv);
            if (frame) FrameSelection();
        }

        public IEnumerable<OSMNodeView> SelectedNodes
        {
            get { return selection.OfType<OSMNodeView>(); }
        }

        // ------------------------------------------------------------------ compatibility

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var result = new List<Port>();
            bool startIsOut = startPort.direction == Direction.Output;
            ports.ForEach(p =>
            {
                if (p == startPort || p.direction == startPort.direction || p.node == startPort.node) return;
                var outPort = startIsOut ? startPort : p;
                var inPort = startIsOut ? p : startPort;
                var o = (OSMPortDef)outPort.userData;
                var i = (OSMPortDef)inPort.userData;
                if (!OSMPortTypes.CanConnect(o.Type, i.Type)) return;
                var fromNode = outPort.node as OSMNodeView;
                var toNode = inPort.node as OSMNodeView;
                if (fromNode == null || toNode == null) return;
                if (_host.Graph.DependsOn(fromNode.Data.id, toNode.Data.id)) return;   // would create a cycle
                result.Add(p);
            });
            return result;
        }

        // ------------------------------------------------------------------ edit callbacks

        static bool TryConn(Edge e, out OSMConnection c)
        {
            c = null;
            var from = e.output != null ? e.output.node as OSMNodeView : null;
            var to = e.input != null ? e.input.node as OSMNodeView : null;
            if (from == null || to == null) return false;
            c = new OSMConnection
            {
                fromNode = from.Data.id, fromPort = ((OSMPortDef)e.output.userData).Key,
                toNode = to.Data.id, toPort = ((OSMPortDef)e.input.userData).Key
            };
            return true;
        }

        GraphViewChange OnChanged(GraphViewChange ch)
        {
            var g = _host.Graph;
            if (g == null) return ch;
            bool structural = false;
            OSMConnection c;

            if (ch.elementsToRemove != null)
            {
                _host.BeginEdit("Delete OSM Elements");
                foreach (var el in ch.elementsToRemove)
                {
                    var edge = el as Edge;
                    var nv = el as OSMNodeView;
                    if (edge != null && TryConn(edge, out c)) { g.Disconnect(c.fromNode, c.fromPort, c.toNode, c.toPort); structural = true; }
                    else if (nv != null && nv.Def.Deletable) { g.RemoveNode(nv.Data.id); NodeViews.Remove(nv.Data.id); structural = true; }
                }
            }

            if (ch.edgesToCreate != null)
            {
                _host.BeginEdit("Connect OSM Ports");
                var keep = new List<Edge>();
                foreach (var e in ch.edgesToCreate)
                {
                    if (!TryConn(e, out c)) continue;
                    if (!g.Connect(c.fromNode, c.fromPort, c.toNode, c.toPort)) { _host.Notify("That connection would create a loop and was refused."); continue; }
                    keep.Add(e);
                    structural = true;
                }
                ch.edgesToCreate = keep;
            }

            if (ch.movedElements != null)
            {
                _host.BeginEdit("Move OSM Nodes");
                foreach (var el in ch.movedElements)
                {
                    var nv = el as OSMNodeView;
                    if (nv != null) nv.Data.position = nv.GetPosition().position;
                }
                _host.ModelChanged(false);
            }

            if (structural) _host.ModelChanged(true);
            return ch;
        }

        // ------------------------------------------------------------------ copy / paste / duplicate

        string SerializeElements(IEnumerable<GraphElement> elements)
        {
            var clip = new Clip();
            var ids = new HashSet<string>();
            foreach (var nv in elements.OfType<OSMNodeView>())
            {
                if (!nv.Def.Deletable) continue;
                var copy = new OSMNodeData { id = nv.Data.id, typeId = nv.Data.typeId, position = nv.Data.position };
                foreach (var v in nv.Data.values)
                {
                    copy.values.Add(new OSMParamValue { key = v.key, number = v.number, vector = v.vector });
                    string path = v.reference != null ? AssetDatabase.GetAssetPath(v.reference) : null;
                    if (!string.IsNullOrEmpty(path)) clip.refs.Add(new ClipRef { nodeId = copy.id, key = v.key, path = path });
                }
                clip.nodes.Add(copy);
                ids.Add(copy.id);
            }
            if (clip.nodes.Count == 0) return string.Empty;
            foreach (var c in _host.Graph.connections)
                if (ids.Contains(c.fromNode) && ids.Contains(c.toNode)) clip.connections.Add(c);
            return JsonUtility.ToJson(clip);
        }

        void Paste(string operationName, string data)
        {
            Clip clip;
            try { clip = JsonUtility.FromJson<Clip>(data); } catch (Exception) { return; }
            if (clip == null || clip.nodes.Count == 0) return;

            var g = _host.Graph;
            _host.BeginEdit("Paste OSM Nodes");
            var map = new Dictionary<string, string>();
            foreach (var n in clip.nodes)
            {
                string old = n.id;
                n.id = g.GenerateNodeId();
                n.position += new Vector2(40, 40);
                map[old] = n.id;
                foreach (var r in clip.refs)
                    if (r.nodeId == old) n.SetObject(r.key, AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(r.path));
                g.nodes.Add(n);
            }
            foreach (var c in clip.connections)
            {
                string f, t;
                if (map.TryGetValue(c.fromNode, out f) && map.TryGetValue(c.toNode, out t))
                    g.connections.Add(new OSMConnection { fromNode = f, fromPort = c.fromPort, toNode = t, toPort = c.toPort });
            }
            Populate();
            ClearSelection();
            foreach (var id in map.Values) { OSMNodeView nv; if (NodeViews.TryGetValue(id, out nv)) AddToSelection(nv); }
            _host.ModelChanged(true);
        }
    }

    public sealed class OSMSearchProvider : ScriptableObject, ISearchWindowProvider
    {
        OSMGraphView _view;
        IOSMGraphHost _host;

        public void Init(OSMGraphView view, IOSMGraphHost host) { _view = view; _host = host; }

        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
        {
            var tree = new List<SearchTreeEntry> { new SearchTreeGroupEntry(new GUIContent("Create OSM Node")) };
            foreach (var cat in OSMNodeRegistry.CategoryOrder)
            {
                var defs = OSMNodeRegistry.All.Where(d => d.Category == cat && !d.Unique).ToList();
                if (defs.Count == 0) continue;
                tree.Add(new SearchTreeGroupEntry(new GUIContent(cat), 1));
                foreach (var d in defs)
                    tree.Add(new SearchTreeEntry(new GUIContent(d.Title)) { level = 2, userData = d.TypeId });
            }
            return tree;
        }

        public bool OnSelectEntry(SearchTreeEntry entry, SearchWindowContext context)
        {
            if (_view == null || _host == null) return false;
            _host.AddNodeAt((string)entry.userData, _view.ScreenToGraph(context.screenMousePosition));
            return true;
        }
    }
}
