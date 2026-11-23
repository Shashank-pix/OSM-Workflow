using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OSMWorkflow
{
    public enum OSMPreviewMesh { Sphere = 0, Quad = 1, Cube = 2 }

    public static class OSMViewModes
    {
        public static readonly string[] Names = { "Lit", "AO", "Smoothness", "Metallic", "Normal", "Base Color" };
    }

    [Serializable]
    public sealed class OSMPreviewSettings
    {
        public OSMPreviewMesh mesh = OSMPreviewMesh.Sphere;
        public int viewMode;
        public float exposure = 1f;
        public float lightYaw = -35f;
        public float lightPitch = 40f;
        public Vector2 orbit = new Vector2(-20f, 15f);
        public float zoom = 1f;
    }

    /// <summary>
    /// Owns a preview material and renders it. A new shader is only created when the generated source text
    /// actually changes; property-only edits just update the material. If a new compile fails, the last
    /// good shader/material keeps rendering.
    /// </summary>
    public sealed class OSMPreviewController : IDisposable
    {
        PreviewRenderUtility _pru;
        Shader _shader;
        Material _material;
        string _hash;
        readonly Dictionary<string, Shader> _cache = new Dictionary<string, Shader>();
        readonly List<string> _cacheOrder = new List<string>();
        const int CacheSize = 6;
        readonly Dictionary<OSMPreviewMesh, Mesh> _meshes = new Dictionary<OSMPreviewMesh, Mesh>();

        public Material Material { get { return _material; } }

        /// <summary>True when applying this result requires building a new shader (not cached, not current).</summary>
        public bool NeedsShaderBuild(OSMCompileResult r)
        {
            if (r == null || !r.Success) return false;
            if (_shader != null && _hash == r.Hash) return false;
            Shader cached;
            return !(_cache.TryGetValue(r.Hash, out cached) && cached != null);
        }

        /// <summary>
        /// Applies a compile result. A new shader is only built when the generated source is new; recently used
        /// shaders are cached so undo/redo and toggling back are instant. If the build fails, the last good shader stays.
        /// </summary>
        public bool Apply(OSMCompileResult r, int viewMode, out string error)
        {
            error = null;
            if (r == null || !r.Success) return false;

            if (_shader == null || _hash != r.Hash)
            {
                Shader sh;
                if (!_cache.TryGetValue(r.Hash, out sh) || sh == null)
                {
                    sh = ShaderUtil.CreateShaderAsset(r.ShaderSource, false);
                    if (sh == null || ShaderUtil.ShaderHasError(sh))
                    {
                        error = OSMShaderMessages.Collect(sh);
                        if (sh != null) UnityEngine.Object.DestroyImmediate(sh);
                        return false;
                    }
                    sh.hideFlags = HideFlags.HideAndDontSave;
                    _cache[r.Hash] = sh;
                    _cacheOrder.Remove(r.Hash);
                    _cacheOrder.Add(r.Hash);
                    TrimCache(sh);
                }
                if (_material == null) _material = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                else _material.shader = sh;
                _shader = sh;
                _hash = r.Hash;
            }
            r.ApplyTo(_material);
            _material.SetFloat("_OSM_Debug", viewMode);
            return true;
        }

        void TrimCache(Shader keep)
        {
            int i = 0;
            while (_cacheOrder.Count > CacheSize && i < _cacheOrder.Count)
            {
                string key = _cacheOrder[i];
                Shader sh = _cache[key];
                if (sh == keep || sh == _shader) { i++; continue; }
                if (sh != null) UnityEngine.Object.DestroyImmediate(sh);
                _cache.Remove(key);
                _cacheOrder.RemoveAt(i);
            }
        }

        public void SetViewMode(Material m, int mode) { if (m != null) m.SetFloat("_OSM_Debug", mode); }

        Mesh GetMesh(OSMPreviewMesh k)
        {
            Mesh m;
            if (_meshes.TryGetValue(k, out m) && m != null) return m;
            string n = k == OSMPreviewMesh.Sphere ? "Sphere.fbx" : (k == OSMPreviewMesh.Cube ? "Cube.fbx" : "Quad.fbx");
            m = Resources.GetBuiltinResource<Mesh>(n);
            _meshes[k] = m;
            return m;
        }

        void EnsureUtility()
        {
            if (_pru != null) return;
            _pru = new PreviewRenderUtility();
            _pru.cameraFieldOfView = 30f;
        }

        /// <summary>Call from a Repaint event. Returns the preview texture owned by the utility (draw it immediately).</summary>
        public Texture Render(Rect rect, Material mat, OSMPreviewSettings s)
        {
            if (mat == null || rect.width < 4 || rect.height < 4) return null;
            EnsureUtility();
            _pru.BeginPreview(rect, GUIStyle.none);

            var cam = _pru.camera;
            // Every mesh is normalised so its largest dimension is 1 unit, then seen from the same distance:
            // sphere, quad and cube therefore occupy the same area on screen.
            var mesh = GetMesh(s.mesh);
            var bounds = mesh.bounds;
            float maxExtent = Mathf.Max(0.0001f, Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)));
            float meshScale = 0.5f / maxExtent;
            float dist = 0.5f * 1.5f / Mathf.Tan(_pru.cameraFieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(0.25f, s.zoom);
            cam.transform.position = new Vector3(0, 0, -dist);
            cam.transform.rotation = Quaternion.identity;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.17f, 0.17f, 0.18f, 1f);

            float e = Mathf.Max(0.05f, s.exposure);
            _pru.lights[0].intensity = 1.2f * e;
            _pru.lights[0].transform.rotation = Quaternion.Euler(s.lightPitch, s.lightYaw, 0f);
            _pru.lights[1].intensity = 0.35f * e;
            _pru.ambientColor = new Color(0.35f, 0.35f, 0.38f) * e;

            var rot = Quaternion.Euler(s.orbit.y, s.orbit.x, 0f);
            _pru.DrawMesh(mesh, Matrix4x4.TRS(rot * (-bounds.center * meshScale), rot, Vector3.one * meshScale), mat, 0);
            _pru.Render(true);
            return _pru.EndPreview();
        }

        /// <summary>Renders into a private RenderTexture so two materials can be compared under identical conditions.</summary>
        public void RenderTo(RenderTexture target, Material mat, OSMPreviewSettings s)
        {
            var tex = Render(new Rect(0, 0, target.width, target.height), mat, s);
            if (tex == null) return;
            var prev = RenderTexture.active;
            Graphics.Blit(tex, target);
            RenderTexture.active = prev;
        }

        public void Dispose()
        {
            if (_pru != null) { _pru.Cleanup(); _pru = null; }
            foreach (var sh in _cache.Values) if (sh != null) UnityEngine.Object.DestroyImmediate(sh);
            _cache.Clear(); _cacheOrder.Clear();
            if (_material != null) UnityEngine.Object.DestroyImmediate(_material);
            _shader = null; _material = null; _hash = null;
        }
    }
}
