using System;
using UnityEditor;
using UnityEngine;

namespace OSMWorkflow
{
    [Serializable]
    public sealed class OSMTextureMetadata
    {
        public string format = "OSMWorkflow.PackedTexture";
        public string convention = OSMTextureTools.Convention;
        public int version = 1;
        public int width;
        public int height;
        public int bitDepth = 8;
        public string sourceGraphGuid;
        public string bakedUtc;
    }

    /// <summary>
    /// Metadata lives in the texture importer's userData, so it travels with the asset's .meta file,
    /// survives moves/renames and needs no extra sidecar asset. Filenames are never used for recognition.
    /// </summary>
    public static class OSMTextureTools
    {
        public const string Convention = "OSM_R_AO_G_SMOOTHNESS_B_METALLIC";
        const string Prefix = "OSMW1:";

        public static TextureImporter GetImporter(Texture tex)
        {
            if (tex == null) return null;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetImporter.GetAtPath(path) as TextureImporter;
        }

        public static bool TryReadMetadata(Texture tex, out OSMTextureMetadata meta)
        {
            meta = null;
            var imp = GetImporter(tex);
            if (imp == null || string.IsNullOrEmpty(imp.userData) || !imp.userData.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            try { meta = JsonUtility.FromJson<OSMTextureMetadata>(imp.userData.Substring(Prefix.Length)); }
            catch (Exception) { meta = null; }
            return meta != null && meta.format == "OSMWorkflow.PackedTexture";
        }

        public static void WriteMetadata(TextureImporter imp, OSMTextureMetadata meta)
        {
            imp.userData = Prefix + JsonUtility.ToJson(meta);
        }

        public static bool IsNormalMapImport(Texture tex)
        {
            var imp = GetImporter(tex);
            return imp != null && imp.textureType == TextureImporterType.NormalMap;
        }

        public static bool IsSRGB(Texture tex)
        {
            var imp = GetImporter(tex);
            return imp != null && imp.sRGBTexture;
        }

        static void Fix(Texture tex, Action<TextureImporter> change)
        {
            var imp = GetImporter(tex);
            if (imp == null) return;
            change(imp);
            imp.SaveAndReimport();
            OSMEvents.RaiseTexturesReimported();
        }

        // ------------------------------------------------------------------ checks used by nodes

        /// <summary>Scalar data (AO, roughness, metallic, packed OSM) must be sampled as linear data.</summary>
        public static void CheckLinearData(OSMEmitContext c, OSMNodeData n, Texture tex, string what)
        {
            if (tex == null) return;
            var imp = GetImporter(tex);
            if (imp == null) return;
            if (imp.textureType == TextureImporterType.NormalMap)
                c.Warn(n, what + " texture '" + tex.name + "' is imported as a Normal Map. Use the Default texture type.");
            if (imp.sRGBTexture)
                c.Warn(n, what + " texture '" + tex.name + "' has sRGB (Color Texture) enabled. Its values will be gamma-decoded, which is wrong for data maps.",
                    "Disable sRGB", () => Fix(tex, i => i.sRGBTexture = false));
        }

        public static void CheckColorData(OSMEmitContext c, OSMNodeData n, Texture tex)
        {
            var imp = GetImporter(tex);
            if (imp == null) return;
            if (!imp.sRGBTexture && imp.textureType == TextureImporterType.Default)
                c.Info(n, "Base colour texture '" + tex.name + "' has sRGB disabled. Colour textures normally use sRGB.");
        }

        public static void CheckNormalImport(OSMEmitContext c, OSMNodeData n, Texture tex, int encoding)
        {
            var imp = GetImporter(tex);
            if (imp == null) return;
            bool isNormal = imp.textureType == TextureImporterType.NormalMap;
            if (encoding == 1 && !isNormal)
                c.Warn(n, "Encoding is 'Unity Normal Map' but '" + tex.name + "' is not imported as a Normal Map.",
                    "Set to Normal Map", () => Fix(tex, i => i.textureType = TextureImporterType.NormalMap));
            else if (encoding == 2 && isNormal)
                c.Warn(n, "Encoding is 'Raw RGB' but '" + tex.name + "' is imported as a Normal Map (swizzled/compressed). Use 'Auto' or 'Unity Normal Map'.");
            else if (encoding == 0 && !isNormal)
                c.Warn(n, "'" + tex.name + "' is not imported as a Normal Map; it is decoded as raw RGB. Set the texture type to Normal Map for best quality.",
                    "Set to Normal Map", () => Fix(tex, i => i.textureType = TextureImporterType.NormalMap));
        }

        public static void CheckOSMTexture(OSMEmitContext c, OSMNodeData n, Texture tex)
        {
            if (tex == null) return;
            CheckLinearData(c, n, tex, "OSM");
            OSMTextureMetadata meta;
            if (TryReadMetadata(tex, out meta))
            {
                if (meta.convention != Convention)
                    c.Warn(n, "Texture metadata declares convention '" + meta.convention + "', which this tool version does not understand.");
                else
                    c.Info(n, "Recognised as generated by OSM Workflow (v" + meta.version + ", " + meta.width + "x" + meta.height + ", " + meta.bitDepth + "-bit).");
            }
            else if (GetImporter(tex) != null)
            {
                c.Info(n, "No OSM metadata found. The texture is assumed to follow R=AO, G=Smoothness, B=Metallic.");
            }
        }
    }

    /// <summary>Notifies open editors when textures are (re)imported so validation refreshes.</summary>
    public sealed class OSMTextureWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var p in imported)
            {
                var t = AssetDatabase.GetMainAssetTypeAtPath(p);
                if (t != null && typeof(Texture).IsAssignableFrom(t)) { OSMEvents.RaiseTexturesReimported(); return; }
            }
        }
    }
}
