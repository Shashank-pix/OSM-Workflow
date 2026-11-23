using NUnit.Framework;

namespace OSMWorkflow.Tests
{
    public sealed class OSMSelfTestTests
    {
        [Test]
        public void AllSelfTestsPass()
        {
            foreach (var r in OSMSelfTest.RunAll())
                Assert.IsTrue(r.Passed, r.Name + " -> " + r.Detail);
        }

        [Test]
        public void ConnectionRules_BlockUnsafeConversions()
        {
            Assert.IsFalse(OSMPortTypes.CanConnect(OSMPortType.Float, OSMPortType.OSM));
            Assert.IsFalse(OSMPortTypes.CanConnect(OSMPortType.Vector4, OSMPortType.OSM));
            Assert.IsFalse(OSMPortTypes.CanConnect(OSMPortType.Vector3, OSMPortType.Float));
            Assert.IsTrue(OSMPortTypes.CanConnect(OSMPortType.OSM, OSMPortType.OSM));
            Assert.IsTrue(OSMPortTypes.CanConnect(OSMPortType.Float, OSMPortType.Vector3));
        }

        [Test]
        public void Graph_RejectsCycles()
        {
            var g = OSMGraphAsset.CreateEmpty();
            var a = g.AddNode(OSMNodeIds.OneMinus, UnityEngine.Vector2.zero);
            var b = g.AddNode(OSMNodeIds.OneMinus, UnityEngine.Vector2.zero);
            Assert.IsTrue(g.Connect(a.id, "out", b.id, "in"));
            Assert.IsFalse(g.Connect(b.id, "out", a.id, "in"));
            UnityEngine.Object.DestroyImmediate(g);
        }
    }
}
