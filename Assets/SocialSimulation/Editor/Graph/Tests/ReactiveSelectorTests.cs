using System;
using System.Collections.Generic;
using NUnit.Framework;
using SocialSimulation.Graph;
using UnityEngine;

namespace SocialSimulation.Graph.Tests
{
    public sealed class ReactiveSelectorTests
    {
        private class ResourceHost : IGraphActionHost
        {
            public bool Urgent;
            public bool FailHigh;
            public bool CompleteHigh;
            public string Owner;
            public int LowTicks, HighTicks, Cancels, BusyFailures;
            public readonly List<string> Calls = new List<string>();

            public GraphStatus TickAction(GraphNodeExecutionContext context, string actionId)
            {
                if (Owner != null && Owner != actionId)
                {
                    BusyFailures++;
                    Calls.Add(actionId + ":ResourceBusy");
                    return GraphStatus.Failure;
                }
                Owner = actionId;
                if (actionId == "high") HighTicks++; else LowTicks++;
                Calls.Add(actionId + ":Tick");
                if (actionId == "high" && (FailHigh || CompleteHigh))
                {
                    Owner = null;
                    return FailHigh ? GraphStatus.Failure : GraphStatus.Success;
                }
                return GraphStatus.Running;
            }

            public bool CheckCondition(GraphNodeExecutionContext context, string conditionId) { return Urgent; }
            public void CancelAction(GraphNodeExecutionContext context, string actionId)
            {
                Calls.Add(actionId + ":Cancel");
                Cancels++;
                if (Owner == actionId) Owner = null;
            }
        }

        private sealed class ReadinessHost : ResourceHost, IGraphActionReadinessHost
        {
            public bool HighReady;
            public int Probes;
            public bool CanStartAction(GraphNodeExecutionContext context, string actionId)
            { Probes++; return actionId != "high" || HighReady; }
        }

        private readonly List<GraphDocument> documents = new List<GraphDocument>();

        [TearDown]
        public void TearDown()
        {
            foreach (var document in documents) UnityEngine.Object.DestroyImmediate(document);
            documents.Clear();
        }

        private GraphDocument Document()
        {
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.DocumentId = Guid.NewGuid().ToString("N");
            document.Kind = GraphKind.Behavior;
            documents.Add(document);
            return document;
        }

        private static GraphNode Add(GraphDocument document, GraphNodeType type, string id,
            string key = null, string value = null)
        {
            var node = new GraphNode { Id = id, Type = type };
            if (key != null) node.Set(key, value);
            document.Nodes.Add(node);
            return node;
        }

        private static void Link(GraphDocument document, GraphNode from, GraphNode to, int order = 0)
        { document.Edges.Add(new GraphEdge { FromNodeId = from.Id, ToNodeId = to.Id, Order = order }); }

        private GraphDocument Tree(bool highSubtree = false, bool lowWait = false)
        {
            var document = Document();
            var root = Add(document, GraphNodeType.Root, "root");
            var selector = Add(document, GraphNodeType.ReactiveSelector, "select");
            Link(document, root, selector);
            var highDocument = highSubtree ? Document() : document;
            var sequence = Add(highDocument, GraphNodeType.Sequence, "urgent-sequence");
            var condition = Add(highDocument, GraphNodeType.Condition, "urgent", "conditionId", "urgent");
            var high = Add(highDocument, GraphNodeType.Action, "high", "actionId", "high");
            Link(highDocument, sequence, condition);
            Link(highDocument, sequence, high, 1);
            if (highSubtree)
            {
                var nestedRoot = Add(highDocument, GraphNodeType.Root, "nested-root");
                Link(highDocument, nestedRoot, sequence);
                var subtree = Add(document, GraphNodeType.Subtree, "subtree");
                subtree.Subgraph = highDocument;
                Link(document, selector, subtree);
            }
            else Link(document, selector, sequence);
            var low = lowWait
                ? Add(document, GraphNodeType.Wait, "low", "ticks", "5")
                : Add(document, GraphNodeType.Action, "low", "actionId", "low");
            Link(document, selector, low, 1);
            return document;
        }

        private static BehaviorInstance Instance(GraphDocument document)
        {
            var compiled = GraphCompiler.Compile(document);
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
            return new BehaviorInstance(compiled.BehaviorPlan, "actor", "activity");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HigherPriorityBranchReleasesOldResourceBeforeStarting(bool highSubtree)
        {
            var instance = Instance(Tree(highSubtree));
            var host = new ResourceHost();
            Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 0));
            Assert.AreEqual("low", host.Owner);
            host.Urgent = true;
            Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 1));
            CollectionAssert.AreEqual(new[] { "low:Tick", "low:Cancel", "high:Tick" }, host.Calls);
            Assert.AreEqual("high", host.Owner);
            Assert.AreEqual(0, host.BusyFailures);
            Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 2));
            Assert.AreEqual(1, host.Cancels);
            instance.Cancel(host, 3);
            Assert.IsNull(host.Owner);
            Assert.AreEqual(2, host.Cancels);
        }

        [Test]
        public void FalseGuardDoesNotCancelOrRestartLowerPriorityAction()
        {
            var instance = Instance(Tree());
            var host = new ResourceHost();
            for (int tick = 0; tick < 10; tick++)
                Assert.AreEqual(GraphStatus.Running, instance.Tick(host, tick));
            Assert.AreEqual(10, host.LowTicks);
            Assert.AreEqual(0, host.HighTicks);
            Assert.AreEqual(0, host.Cancels);
            Assert.AreEqual("low", host.Owner);
        }

        [Test]
        public void FalseGuardPreservesWaitStartAcrossTicks()
        {
            var instance = Instance(Tree(lowWait: true));
            var host = new ResourceHost();
            for (int tick = 0; tick < 5; tick++)
                Assert.AreEqual(GraphStatus.Running, instance.Tick(host, tick));
            Assert.AreEqual(GraphStatus.Success, instance.Tick(host, 5));
            Assert.AreEqual(0, host.HighTicks);
            Assert.AreEqual(0, host.Cancels);
        }

        [Test]
        public void FailedHighActionFallsBackAfterOldBranchWasCancelled()
        {
            var instance = Instance(Tree());
            var host = new ResourceHost();
            instance.Tick(host, 0);
            host.Urgent = true;
            host.FailHigh = true;
            Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 1));
            CollectionAssert.AreEqual(new[] { "low:Tick", "low:Cancel", "high:Tick", "low:Tick" }, host.Calls);
            Assert.AreEqual("low", host.Owner);
            Assert.AreEqual(0, host.BusyFailures);
            Assert.AreEqual(1, host.Cancels);
            host.Urgent = false;
            instance.Tick(host, 2);
            Assert.AreEqual(1, host.Cancels);
            Assert.AreEqual(3, host.LowTicks);
        }

        [Test]
        public void OptionalReadinessRejectsActionWithoutDisturbingCurrentBranch()
        {
            var instance = Instance(Tree());
            var host = new ReadinessHost();
            instance.Tick(host, 0);
            host.Urgent = true;
            for (int tick = 1; tick <= 3; tick++) instance.Tick(host, tick);
            Assert.AreEqual(3, host.Probes);
            Assert.AreEqual(0, host.Cancels);
            Assert.AreEqual(0, host.HighTicks);
            Assert.AreEqual(4, host.LowTicks);
            host.HighReady = true;
            instance.Tick(host, 4);
            Assert.AreEqual("high", host.Owner);
            Assert.AreEqual(1, host.Cancels);
            Assert.AreEqual(0, host.BusyFailures);
        }

        [Test]
        public void RestoredRunningBranchCanBePreemptedWithoutLeakingResource()
        {
            var instance = Instance(Tree());
            var host = new ResourceHost();
            instance.Tick(host, 0);
            var checkpoint = JsonUtility.FromJson<BehaviorCheckpoint>(JsonUtility.ToJson(instance.CaptureCheckpoint()));
            var restored = new BehaviorInstance(instance.Plan, "actor", "activity");
            restored.RestoreCheckpoint(checkpoint);
            host.Urgent = true;
            restored.Tick(host, 1);
            Assert.AreEqual("high", host.Owner);
            Assert.AreEqual(1, host.Cancels);
            Assert.AreEqual(0, host.BusyFailures);
            restored.Cancel(host, 2);
            Assert.IsNull(host.Owner);
        }

        [Test]
        public void BlackboardGuardIsReadWithoutChangingValues()
        {
            var document = Tree();
            var guard = document.FindNode("urgent");
            guard.Type = GraphNodeType.BlackboardCheck;
            guard.Parameters.Clear();
            guard.Set("variableId", "urgent-value");
            guard.Set("comparison", "eq");
            guard.Set("expected", "true");
            document.Variables.Add(new BlackboardVariable { Id = "urgent-value", Name = "Urgent",
                Type = BlackboardValueType.Boolean, DefaultValue = "false" });
            var instance = Instance(document);
            var host = new ResourceHost();
            instance.Tick(host, 0);
            instance.Tick(host, 1);
            Assert.AreEqual(0, host.Cancels);
            Assert.IsFalse(instance.Blackboard.GetBoolean("urgent-value"));
            Assert.IsTrue(instance.Blackboard.TrySetRaw("urgent-value", "true"));
            instance.Tick(host, 2);
            Assert.AreEqual("high", host.Owner);
            Assert.IsTrue(instance.Blackboard.GetBoolean("urgent-value"));
        }

        [Test]
        public void CooldownGuardDoesNotInterruptUntilReady()
        {
            var document = Tree();
            var selector = document.FindNode("select");
            var sequence = document.FindNode("urgent-sequence");
            document.Edges.RemoveAll(edge => edge.FromNodeId == selector.Id && edge.ToNodeId == sequence.Id);
            var cooldown = Add(document, GraphNodeType.Cooldown, "cooldown", "ticks", "4");
            Link(document, selector, cooldown);
            Link(document, cooldown, sequence);
            var instance = Instance(document);
            var host = new ResourceHost { Urgent = true, CompleteHigh = true };
            Assert.AreEqual(GraphStatus.Success, instance.Tick(host, 0));
            for (int tick = 1; tick <= 3; tick++)
                Assert.AreEqual(GraphStatus.Running, instance.Tick(host, tick));
            Assert.AreEqual(0, host.Cancels);
            host.CompleteHigh = false;
            Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 4));
            Assert.AreEqual("high", host.Owner);
            Assert.AreEqual(1, host.Cancels);
            Assert.AreEqual(0, host.BusyFailures);
        }
    }
}
