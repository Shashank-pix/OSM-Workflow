using UnityEngine;

namespace OSMWorkflow
{
    /// <summary>What the graph view and node views need from the window that hosts them.</summary>
    public interface IOSMGraphHost
    {
        OSMGraphAsset Graph { get; }
        /// <summary>Registers an undo step on the graph asset before it is modified.</summary>
        void BeginEdit(string undoName);
        void ParamChanged(OSMNodeData data, OSMParamDef param);
        /// <summary>The model changed. <paramref name="recompile"/> is false for cosmetic changes such as moving nodes.</summary>
        void ModelChanged(bool recompile);
        void Notify(string message);
        void AddNodeAt(string typeId, Vector2 graphPosition);
    }
}
