using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace OSMWorkflow
{
    /// <summary>Builds an editing control for a parameter definition. Used by node bodies and the inspector.</summary>
    public static class OSMParamUI
    {
        public static VisualElement Create(IOSMGraphHost host, OSMNodeData d, OSMParamDef p, Action<OSMParamDef> after)
        {
            Action<Action> apply = set =>
            {
                host.BeginEdit("Edit " + p.Label);
                set();
                host.ParamChanged(d, p);
                if (after != null) after(p);
            };

            VisualElement f;
            switch (p.Kind)
            {
                case OSMParamKind.Number:
                {
                    var x = new FloatField(p.Label) { value = d.GetFloat(p.Key, p.Default) };
                    x.RegisterValueChangedCallback(e => apply(() => d.SetFloat(p.Key, e.newValue)));
                    f = x; break;
                }
                case OSMParamKind.Slider:
                {
                    var x = new Slider(p.Label, p.Min, p.Max) { showInputField = true, value = d.GetFloat(p.Key, p.Default) };
                    x.RegisterValueChangedCallback(e => apply(() => d.SetFloat(p.Key, e.newValue)));
                    f = x; break;
                }
                case OSMParamKind.Toggle:
                {
                    var x = new Toggle(p.Label) { value = d.GetBool(p.Key, p.Default > 0.5f) };
                    x.RegisterValueChangedCallback(e => apply(() => d.SetBool(p.Key, e.newValue)));
                    f = x; break;
                }
                case OSMParamKind.Choice:
                {
                    int idx = Mathf.Clamp(d.GetInt(p.Key, (int)p.Default), 0, p.Options.Length - 1);
                    var x = new DropdownField(p.Label, new List<string>(p.Options), idx);
                    x.RegisterValueChangedCallback(e => { int i = x.index; apply(() => d.SetFloat(p.Key, i)); });
                    f = x; break;
                }
                case OSMParamKind.Color:
                case OSMParamKind.ColorHDR:
                {
                    var x = new ColorField(p.Label) { showAlpha = true, hdr = p.Kind == OSMParamKind.ColorHDR, value = d.GetColor(p.Key, new Color(p.DefaultVector.x, p.DefaultVector.y, p.DefaultVector.z, p.DefaultVector.w)) };
                    x.RegisterValueChangedCallback(e => apply(() => d.SetColor(p.Key, e.newValue)));
                    f = x; break;
                }
                case OSMParamKind.Vector2:
                {
                    var v = d.GetVector(p.Key, p.DefaultVector);
                    var x = new Vector2Field(p.Label) { value = new Vector2(v.x, v.y) };
                    x.RegisterValueChangedCallback(e => apply(() => d.SetVector(p.Key, new Vector4(e.newValue.x, e.newValue.y, 0, 0))));
                    f = x; break;
                }
                case OSMParamKind.Vector3:
                {
                    var v = d.GetVector(p.Key, p.DefaultVector);
                    var x = new Vector3Field(p.Label) { value = new Vector3(v.x, v.y, v.z) };
                    x.RegisterValueChangedCallback(e => apply(() => d.SetVector(p.Key, new Vector4(e.newValue.x, e.newValue.y, e.newValue.z, 0))));
                    f = x; break;
                }
                default:
                {
                    var x = new ObjectField(p.Label) { objectType = typeof(Texture2D), allowSceneObjects = false, value = d.GetObject<Texture>(p.Key) };
                    x.RegisterValueChangedCallback(e => apply(() => d.SetObject(p.Key, e.newValue)));
                    f = x; break;
                }
            }
            f.tooltip = p.Tooltip ?? string.Empty;
            var label = f.Q<Label>(null, "unity-base-field__label");
            if (label != null) { label.style.minWidth = 70; label.style.width = 90; }
            return f;
        }
    }

    public sealed class OSMNodeView : Node
    {
        public readonly OSMNodeData Data;
        public readonly OSMNodeDef Def;
        public readonly Dictionary<string, Port> InPorts = new Dictionary<string, Port>();
        public readonly Dictionary<string, Port> OutPorts = new Dictionary<string, Port>();

        readonly IOSMGraphHost _host;
        readonly List<KeyValuePair<OSMParamDef, VisualElement>> _fields = new List<KeyValuePair<OSMParamDef, VisualElement>>();
        readonly Label _badge = new Label();
        Image _thumb;
        Label _info;

        public OSMNodeView(IOSMGraphHost host, OSMNodeData data, OSMNodeDef def)
        {
            _host = host;
            Data = data;
            Def = def;
            title = def.Title;
            tooltip = def.Help;
            viewDataKey = data.id;
            titleContainer.style.backgroundColor = def.TitleColor;
            SetPosition(new Rect(data.position, Vector2.zero));
            if (!def.Deletable) capabilities &= ~(Capabilities.Deletable | Capabilities.Copiable);

            _badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            _badge.style.marginRight = 4;
            _badge.style.display = DisplayStyle.None;
            titleContainer.Insert(1, _badge);

            foreach (var p in def.Inputs(data)) AddPort(p, Direction.Input, Port.Capacity.Single, inputContainer, InPorts);
            foreach (var p in def.Outputs(data)) AddPort(p, Direction.Output, Port.Capacity.Multi, outputContainer, OutPorts);

            bool hasTexture = false;
            foreach (var p in def.Params)
            {
                var field = OSMParamUI.Create(host, data, p, OnParamEdited);
                _fields.Add(new KeyValuePair<OSMParamDef, VisualElement>(p, field));
                extensionContainer.Add(field);
                if (p.Kind == OSMParamKind.Texture) hasTexture = true;
            }
            if (hasTexture)
            {
                _thumb = new Image { scaleMode = ScaleMode.ScaleToFit };
                _thumb.style.width = 72; _thumb.style.height = 72; _thumb.style.flexShrink = 0; _thumb.style.flexGrow = 0;
                _thumb.style.alignSelf = Align.Center; _thumb.style.marginTop = 2; _thumb.style.marginBottom = 2;
                extensionContainer.Add(_thumb);
            }
            if (data.typeId == OSMNodeIds.OSMTexture)
            {
                _info = new Label { style = { whiteSpace = WhiteSpace.Normal, fontSize = 10, marginLeft = 4 } };
                extensionContainer.Add(_info);
            }
            extensionContainer.style.backgroundColor = new Color(0.16f, 0.16f, 0.17f, 0.95f);
            style.minWidth = 230; style.maxWidth = 360;
            RefreshState();
            RefreshExpandedState();
            RefreshPorts();
        }

        void AddPort(OSMPortDef p, Direction dir, Port.Capacity cap, VisualElement container, Dictionary<string, Port> map)
        {
            var port = InstantiatePort(Orientation.Horizontal, dir, cap, typeof(float));
            port.portName = p.Label;
            port.portColor = OSMPortTypes.PortColor(p.Type);
            port.userData = p;
            port.tooltip = OSMPortTypes.Label(p.Type);
            container.Add(port);
            map[p.Key] = port;
        }

        void OnParamEdited(OSMParamDef p) { RefreshState(); }

        /// <summary>Updates thumbnail, metadata label and conditional field visibility from the model.</summary>
        public void RefreshState()
        {
            foreach (var kv in _fields)
                if (kv.Key.VisibleWhen != null) kv.Value.style.display = kv.Key.VisibleWhen(Data) ? DisplayStyle.Flex : DisplayStyle.None;
            var tex = Data.GetObject<Texture>("texture");
            if (_thumb != null) { _thumb.image = tex; _thumb.style.display = tex != null ? DisplayStyle.Flex : DisplayStyle.None; }
            if (_info != null)
            {
                OSMTextureMetadata m;
                if (tex == null) _info.text = "";
                else if (OSMTextureTools.TryReadMetadata(tex, out m)) { _info.text = "Generated by OSM Workflow  " + m.width + "x" + m.height + "  " + m.bitDepth + "-bit"; _info.style.color = new Color(0.55f, 1f, 0.6f); }
                else { _info.text = "No OSM metadata: assuming R=AO, G=Smoothness, B=Metallic"; _info.style.color = new Color(1f, 0.85f, 0.45f); }
            }
        }

        public void SetBadge(OSMSeverity? worst, string message)
        {
            if (worst == null || worst.Value == OSMSeverity.Info)
            {
                _badge.style.display = DisplayStyle.None;
                _badge.tooltip = string.Empty;
                return;
            }
            _badge.style.display = DisplayStyle.Flex;
            bool err = worst.Value == OSMSeverity.Error;
            _badge.text = err ? "(x)" : "(!)";
            _badge.style.color = err ? new Color(1f, 0.4f, 0.4f) : new Color(1f, 0.85f, 0.3f);
            _badge.tooltip = message;
        }
    }
}
