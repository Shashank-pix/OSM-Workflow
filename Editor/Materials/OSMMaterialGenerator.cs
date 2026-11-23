using System.IO;
using UnityEditor;
using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>Creates or updates the generated shader and material. Generated assets work without the editor tool.</summary>
    public static class OSMMaterialGenerator
    {
        /// <summary>
        /// Writes <c>name.shader</c> and <c>name.mat</c> into the folder of <paramref name="materialPath"/>.
        /// If the graph already references generated assets, they are updated in place (GUIDs and scene references stay valid).
        /// </summary>
        public static bool Generate(OSMGraphAsset graph, string materialPath, out string message)
        {
            if (string.IsNullOrEmpty(materialPath)) { message = "No path chosen."; return false; }
            string folder = Path.GetDirectoryName(materialPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(materialPath);
            string shaderPath = folder + "/" + name + ".shader";
            return Write(graph, shaderPath, materialPath, out message);
        }

        public static bool UpdateExisting(OSMGraphAsset graph, out string message)
        {
            if (graph.generatedShader == null || graph.generatedMaterial == null)
            {
                message = "This graph has no generated material yet. Use 'Generate Material…' first.";
                return false;
            }
            return Write(graph, AssetDatabase.GetAssetPath(graph.generatedShader), AssetDatabase.GetAssetPath(graph.generatedMaterial), out message);
        }

        static bool Write(OSMGraphAsset graph, string shaderPath, string materialPath, out string message)
        {
            try
            {
                EditorUtility.DisplayProgressBar("OSM Workflow", "Compiling the graph...", 0.15f);
                return WriteInner(graph, shaderPath, materialPath, out message);
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static bool WriteInner(OSMGraphAsset graph, string shaderPath, string materialPath, out string message)
        {
            string name = Path.GetFileNameWithoutExtension(materialPath);
            var result = OSMGraphCompiler.CompileLit(graph, new OSMCompileOptions { IncludeDebug = false, ShaderName = "OSM Workflow/" + name });
            if (!result.Success)
            {
                message = "The graph has errors; nothing was written. Fix the errors listed in the Diagnostics panel first.";
                return false;
            }

            EditorUtility.DisplayProgressBar("OSM Workflow", "Writing and importing the shader (Unity compiles it)...", 0.5f);
            string absShader = Path.GetFullPath(shaderPath);
            string previous = File.Exists(absShader) ? File.ReadAllText(absShader) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(absShader));
            File.WriteAllText(absShader, result.ShaderSource);
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
            {
                string err = OSMShaderMessages.Collect(shader);
                if (previous != null)
                {
                    File.WriteAllText(absShader, previous);               // keep the last working shader
                    AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceUpdate);
                }
                else AssetDatabase.DeleteAsset(shaderPath);
                message = "The generated shader failed to compile" + (previous != null ? " (previous version restored)" : "") + ":\n" + err;
                return false;
            }

            EditorUtility.DisplayProgressBar("OSM Workflow", "Updating the material...", 0.85f);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, materialPath);
            }
            mat.shader = shader;
            result.ApplyTo(mat);
            EditorUtility.SetDirty(mat);

            Undo.RecordObject(graph, "Generate OSM Material");
            graph.generatedShader = shader;
            graph.generatedMaterial = mat;
            EditorUtility.SetDirty(graph);
            AssetDatabase.SaveAssets();

            int warnings = 0;
            foreach (var d in result.Diagnostics) if (d.Severity == OSMSeverity.Warning) warnings++;
            message = "Generated '" + materialPath + "' and '" + shaderPath + "'." + (warnings > 0 ? " (" + warnings + " warning(s) in the graph.)" : "");
            EditorGUIUtility.PingObject(mat);
            return true;
        }
    }
}
