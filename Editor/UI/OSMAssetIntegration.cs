using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>Project-window integration: create menu, double-click to open, and a small asset inspector.</summary>
    public static class OSMAssetIntegration
    {
        // Creates the asset immediately in the active Project folder (rename with F2). This avoids the
        // name-editing callback classes, whose signatures changed between Unity versions.
        static void Create(System.Func<OSMGraphAsset> factory, string name)
        {
            // Use the folder of the current Project-window selection (the active-folder helper is not public API).
            string folder = "Assets";
            string sel = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(sel))
                folder = AssetDatabase.IsValidFolder(sel) ? sel : System.IO.Path.GetDirectoryName(sel).Replace('\\', '/');
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".asset");
            var g = factory();
            AssetDatabase.CreateAsset(g, path);
            AssetDatabase.SaveAssets();
            ProjectWindowUtil.ShowCreatedAsset(g);
            Selection.activeObject = g;
            OSMGraphWindow.Open(g);
        }

        [MenuItem("Assets/Create/OSM Workflow/Shader Graph (Separate Maps template)", false, 1000)]
        static void CreateSeparate() { Create(OSMGraphAsset.CreateSeparateMapsTemplate, "New OSM Graph"); }

        [MenuItem("Assets/Create/OSM Workflow/Shader Graph (Packed OSM template)", false, 1001)]
        static void CreatePacked() { Create(OSMGraphAsset.CreatePackedTemplate, "New OSM Graph"); }

        [MenuItem("Assets/Create/OSM Workflow/Shader Graph (Empty)", false, 1002)]
        static void CreateEmpty() { Create(OSMGraphAsset.CreateEmpty, "New OSM Graph"); }

        // Unity 6.4+ identifies objects with EntityId; the int-based callback and lookup are obsolete.
        [OnOpenAsset]
        static bool OnOpen(EntityId entityId, int line)
        {
            var g = EditorUtility.EntityIdToObject(entityId) as OSMGraphAsset;
            if (g == null) return false;
            OSMGraphWindow.Open(g);
            return true;
        }
    }

    [CustomEditor(typeof(OSMGraphAsset))]
    public sealed class OSMGraphAssetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var g = (OSMGraphAsset)target;
            EditorGUILayout.LabelField("OSM Shader Graph", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Nodes", g.nodes.Count.ToString());
            EditorGUILayout.LabelField("Connections", g.connections.Count.ToString());
            EditorGUILayout.LabelField("Schema version", g.schemaVersion.ToString());
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Generated shader", g.generatedShader, typeof(Shader), false);
                EditorGUILayout.ObjectField("Generated material", g.generatedMaterial, typeof(Material), false);
            }
            EditorGUILayout.Space();
            if (GUILayout.Button("Open in OSM Graph Editor", GUILayout.Height(28))) OSMGraphWindow.Open(g);
        }
    }
}
