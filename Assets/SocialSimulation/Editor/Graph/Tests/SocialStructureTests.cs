using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SocialSimulation.Core;
using SocialSimulation.Editor.Graph;
using SocialSimulation.Graph;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Graph.Tests
{
    public sealed class SocialStructureTests
    {
        [Test]
        public void CombinedSampleAssetCompilesAndCreatesAWorld()
        {
            const string path = "Assets/SocialSimulation/Graph/Samples/CombinedSocialStructure.asset";
            var asset = AssetDatabase.LoadAssetAtPath<SocialStructureDocument>(path);
            Assert.NotNull(asset, path);
            var result = SocialStructureCompiler.Compile(asset);
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
            var world = result.Plan.CreateWorld("sample.structure.test");
            Assert.IsTrue(world.Agents.ContainsKey("npc.student.01"));
            Assert.IsTrue(world.Society.Organizations.ContainsKey("trade.corner_shop"));
            Assert.AreEqual("trade.corner_shop", world.Places["trade.shop.corner"].OwnerCollectiveId);
            Assert.IsTrue(world.Society.HasAuthority("npc.clerk.01", "policy.publish", "trade.corner_shop"));
        }

        private static StructureNode Node(StructureNodeType type, string entityId, string linkedCollectiveId = null)
        {
            return new StructureNode { Id = Guid.NewGuid().ToString("N"), EntityId = entityId,
                Label = entityId, Type = type, LinkedCollectiveId = linkedCollectiveId };
        }

        private static StructureEdge Link(SocialStructureDocument document, StructureNode from,
            StructureNode to, StructureEdgeType type)
        {
            var edge = new StructureEdge { Id = Guid.NewGuid().ToString("N"),
                FromNodeId = from.Id, ToNodeId = to.Id, Type = type };
            document.Edges.Add(edge);
            return edge;
        }

        private static SocialStructureDocument Document()
        {
            var document = ScriptableObject.CreateInstance<SocialStructureDocument>();
            document.DocumentId = Guid.NewGuid().ToString("N");
            return document;
        }

        [Test]
        public void StructurePlanAppliesMultiMembershipRoleRelationAuthorityAndPlaceOwner()
        {
            var document = Document();
            var alice = Node(StructureNodeType.Agent, "alice");
            var bob = Node(StructureNodeType.Agent, "bob");
            var family = Node(StructureNodeType.Collective, "family");
            var shop = Node(StructureNodeType.Collective, "shop");
            var organization = Node(StructureNodeType.Organization, "shop-org", "shop");
            organization.Governance = "LeaderAuthorized";
            organization.AssetIds = "register, inventory";
            var role = Node(StructureNodeType.Role, "manager", "shop");
            var place = Node(StructureNodeType.Place, "shop-floor");
            place.Capacity = 8;
            document.Nodes.AddRange(new[] { alice, bob, family, shop, organization, role, place });
            Link(document, alice, family, StructureEdgeType.Membership);
            Link(document, alice, shop, StructureEdgeType.Membership);
            Link(document, alice, role, StructureEdgeType.Membership);
            Link(document, bob, family, StructureEdgeType.Membership);
            var relation = Link(document, alice, bob, StructureEdgeType.Relation);
            relation.RelationType = "friend"; relation.Liking = 0.5f; relation.Trust = 0.4f;
            var grant = Link(document, shop, alice, StructureEdgeType.Authority);
            grant.CollectiveId = "shop"; grant.ActionId = "task.issue";
            Link(document, organization, place, StructureEdgeType.Jurisdiction);
            try
            {
                var compiled = SocialStructureCompiler.Compile(document);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                var world = new SimulationWorld("structure-test");
                Assert.IsTrue(compiled.Plan.ApplyTo(world).Success);
                Assert.AreEqual(2, world.Agents.Count);
                Assert.AreEqual(2, world.Society.Collectives.Count);
                Assert.AreEqual(3, world.Society.Memberships.Count);
                Assert.AreEqual(1, world.Society.Roles.Count);
                Assert.AreEqual(1, world.Society.Grants.Count);
                Assert.IsTrue(world.Society.HasAuthority("alice", "task.issue", "shop"));
                Assert.AreEqual("shop", world.Places["shop-floor"].OwnerCollectiveId);
                CollectionAssert.AreEquivalent(new[] { "register", "inventory" },
                    world.Society.Organizations["shop-org"].AssetIds);
                Assert.AreEqual(0.5f, world.Society.Relations.Values.Single().Liking);
                Assert.IsTrue(compiled.Plan.ApplyTo(world).Success);
                Assert.AreEqual(3, world.Society.Memberships.Count);
                Assert.AreEqual(1, world.Society.Roles.Count);
                Assert.AreEqual(1, world.Society.Grants.Count);
                Assert.AreEqual(0.5f, world.Society.Relations.Values.Single().Liking);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void InvalidRoleAssignmentAndTypedConnectionAreRejected()
        {
            var document = Document();
            var agent = Node(StructureNodeType.Agent, "agent");
            var collective = Node(StructureNodeType.Collective, "collective");
            var role = Node(StructureNodeType.Role, "worker", "collective");
            document.Nodes.AddRange(new[] { agent, collective, role });
            var edge = Link(document, agent, role, StructureEdgeType.Membership);
            try
            {
                Assert.IsFalse(SocialStructureCompiler.CanConnect(StructureNodeType.Place,
                    StructureNodeType.Agent, StructureEdgeType.Membership));
                Assert.IsFalse(SocialStructureCompiler.Compile(document).Success);
                Link(document, agent, collective, StructureEdgeType.Membership);
                Assert.IsTrue(SocialStructureCompiler.Compile(document).Success);
                edge.Type = StructureEdgeType.Jurisdiction;
                Assert.IsFalse(SocialStructureCompiler.Compile(document).Success);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void DirectedAgentsMayHaveMultipleRelationTypes()
        {
            var document = Document();
            var first = Node(StructureNodeType.Agent, "first");
            var second = Node(StructureNodeType.Agent, "second");
            document.Nodes.Add(first); document.Nodes.Add(second);
            var friend = Link(document, first, second, StructureEdgeType.Relation);
            friend.RelationType = "friend";
            var coworker = Link(document, first, second, StructureEdgeType.Relation);
            coworker.RelationType = "coworker";
            try
            {
                Assert.IsTrue(SocialStructureCompiler.Compile(document).Success);
                coworker.RelationType = "friend";
                Assert.IsFalse(SocialStructureCompiler.Compile(document).Success);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void ApplyPreflightRejectsConflictingJurisdictionBeforeCreatingEntities()
        {
            var document = Document();
            var agent = Node(StructureNodeType.Agent, "new-agent");
            var collective = Node(StructureNodeType.Collective, "new-collective");
            var place = Node(StructureNodeType.Place, "existing-place");
            document.Nodes.AddRange(new[] { agent, collective, place });
            Link(document, collective, place, StructureEdgeType.Jurisdiction);
            try
            {
                var compiled = SocialStructureCompiler.Compile(document);
                Assert.IsTrue(compiled.Success);
                var world = new SimulationWorld("preflight-test");
                world.Places.Add(place.EntityId, new PlaceState
                    { Id = place.EntityId, OwnerCollectiveId = "another-collective" });
                var result = compiled.Plan.ApplyTo(world);
                Assert.IsFalse(result.Success);
                Assert.AreEqual(0, world.Agents.Count);
                Assert.AreEqual(0, world.Society.Collectives.Count);
                Assert.AreEqual("another-collective", world.Places[place.EntityId].OwnerCollectiveId);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void StagedApplyFailureLeavesLiveWorldUntouched()
        {
            var document = Document();
            var agent = Node(StructureNodeType.Agent, "new-agent");
            var collective = Node(StructureNodeType.Collective, "existing-group");
            document.Nodes.Add(agent); document.Nodes.Add(collective);
            Link(document, agent, collective, StructureEdgeType.Membership);
            try
            {
                var compiled = SocialStructureCompiler.Compile(document);
                Assert.IsTrue(compiled.Success);
                var world = new SimulationWorld("staged-test");
                world.Society.Collectives.Add(collective.EntityId,
                    new CollectiveState { Id = collective.EntityId, MemberIds = null });
                var beforeTrace = world.Trace.Count;
                var result = compiled.Plan.ApplyTo(world);
                Assert.IsFalse(result.Success);
                Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "StagingFailure"));
                Assert.AreEqual(0, world.Agents.Count);
                Assert.AreEqual(0, world.Society.Memberships.Count);
                Assert.AreEqual(beforeTrace, world.Trace.Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void CollectiveAndOrganizationMayShareTheirWorldIdentity()
        {
            var document = Document();
            var collective = Node(StructureNodeType.Collective, "trade.shop");
            var organization = Node(StructureNodeType.Organization, "trade.shop", "trade.shop");
            document.Nodes.Add(collective); document.Nodes.Add(organization);
            try
            {
                var compiled = SocialStructureCompiler.Compile(document);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                var world = compiled.Plan.CreateWorld("shared-id");
                Assert.IsTrue(world.Society.Collectives.ContainsKey("trade.shop"));
                Assert.AreEqual("trade.shop", world.Society.Organizations["trade.shop"].CollectiveId);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void StructureCanvasConnectsDeletesSavesAndUndoRedoWorks()
        {
            var path = "Assets/SocialSimulation/Editor/Graph/Tests/__structure_" + Guid.NewGuid().ToString("N") + ".asset";
            var document = Document();
            var agent = Node(StructureNodeType.Agent, "agent");
            var collective = Node(StructureNodeType.Collective, "collective");
            document.Nodes.Add(agent); document.Nodes.Add(collective);
            SocialStructureWorkbenchWindow window = null;
            try
            {
                AssetDatabase.CreateAsset(document, path);
                window = EditorWindow.CreateWindow<SocialStructureWorkbenchWindow>();
                window.CreateGUI(); window.Open(document);
                Assert.NotNull(window.rootVisualElement.Q<VisualElement>("graphHost"));
                var field = typeof(SocialStructureWorkbenchWindow)
                    .GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic);
                var view = (GraphView)field.GetValue(window);
                var source = view.nodes.ToList().Single(node => node.viewDataKey == agent.Id);
                var target = view.nodes.ToList().Single(node => node.viewDataKey == collective.Id);
                var edgeView = ((Port)source.outputContainer[0]).ConnectTo((Port)target.inputContainer[0]);
                view.graphViewChanged(new GraphViewChange { edgesToCreate = new List<Edge> { edgeView } });
                Assert.AreEqual(1, document.Edges.Count);
                var firstEdgeId = document.Edges[0].Id;
                Undo.PerformUndo();
                Assert.AreEqual(0, document.Edges.Count);
                Undo.PerformRedo();
                Assert.AreEqual(firstEdgeId, document.Edges[0].Id);

                source = view.nodes.ToList().Single(node => node.viewDataKey == agent.Id);
                view.AddToSelection(source);
                typeof(SocialStructureWorkbenchWindow).GetMethod("RefreshInspector",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                var entityField = window.rootVisualElement.Q<ScrollView>("inspector").Q<TextField>();
                Assert.NotNull(entityField);
                entityField.value = "agent-renamed";
                Assert.AreEqual("agent-renamed", document.FindNode(agent.Id).EntityId);
                Undo.PerformUndo();
                Assert.AreEqual("agent", document.FindNode(agent.Id).EntityId);
                Undo.PerformRedo();
                Assert.AreEqual("agent-renamed", document.FindNode(agent.Id).EntityId);

                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var loaded = AssetDatabase.LoadAssetAtPath<SocialStructureDocument>(path);
                Assert.AreEqual(firstEdgeId, loaded.Edges[0].Id);
                Assert.AreEqual("agent-renamed", loaded.FindNode(agent.Id).EntityId);
                var liveEdge = view.edges.ToList().Single();
                view.graphViewChanged(new GraphViewChange { elementsToRemove = new List<GraphElement> { liveEdge } });
                Assert.AreEqual(0, document.Edges.Count);
            }
            finally
            {
                if (window != null) window.Close();
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void RecursiveBehaviorCheckpointIncludesSubgraphState()
        {
            var child = ScriptableObject.CreateInstance<GraphDocument>();
            child.Kind = GraphKind.Behavior; child.DocumentId = "child";
            var childRoot = new GraphNode { Id = "child-root", Type = GraphNodeType.Root };
            var wait = new GraphNode { Id = "wait", Type = GraphNodeType.Wait }; wait.Set("ticks", "2");
            child.Nodes.Add(childRoot); child.Nodes.Add(wait);
            child.Edges.Add(new GraphEdge { FromNodeId = childRoot.Id, ToNodeId = wait.Id });
            var parent = ScriptableObject.CreateInstance<GraphDocument>();
            parent.Kind = GraphKind.Behavior; parent.DocumentId = "parent";
            var root = new GraphNode { Id = "root", Type = GraphNodeType.Root };
            var subtree = new GraphNode { Id = "subtree", Type = GraphNodeType.Subtree, Subgraph = child };
            parent.Nodes.Add(root); parent.Nodes.Add(subtree);
            parent.Edges.Add(new GraphEdge { FromNodeId = root.Id, ToNodeId = subtree.Id });
            try
            {
                var result = GraphCompiler.Compile(parent);
                Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
                subtree.Id = "subtree/unsafe";
                parent.Edges[0].ToNodeId = subtree.Id;
                Assert.IsFalse(GraphCompiler.Compile(parent).Success);
                subtree.Id = "subtree";
                parent.Edges[0].ToNodeId = subtree.Id;
                var host = new DummyHost();
                var instance = new BehaviorInstance(result.BehaviorPlan, "agent", "i");
                Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 0));
                var checkpoint = JsonUtility.FromJson<BehaviorCheckpoint>(JsonUtility.ToJson(instance.CaptureCheckpoint()));
                var restored = new BehaviorInstance(result.BehaviorPlan, "agent", "i");
                restored.RestoreCheckpoint(checkpoint);
                Assert.AreEqual(GraphStatus.Success, restored.Tick(host, 2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
                UnityEngine.Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void StructureClipboardRegeneratesEntityIdsAndInternalEdges()
        {
            var document = Document();
            var agent = Node(StructureNodeType.Agent, "agent");
            var collective = Node(StructureNodeType.Collective, "collective");
            document.Nodes.Add(agent); document.Nodes.Add(collective);
            Link(document, agent, collective, StructureEdgeType.Membership);
            SocialStructureWorkbenchWindow window = null;
            try
            {
                window = EditorWindow.CreateWindow<SocialStructureWorkbenchWindow>();
                window.CreateGUI(); window.Open(document);
                var field = typeof(SocialStructureWorkbenchWindow)
                    .GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic);
                var view = (GraphView)field.GetValue(window);
                var selected = view.nodes.ToList().Cast<GraphElement>().ToList();
                var json = view.serializeGraphElements(selected);
                view.unserializeAndPaste("Paste structure", json);
                Assert.AreEqual(4, document.Nodes.Count);
                Assert.AreEqual(2, document.Edges.Count);
                Assert.AreEqual(4, document.Nodes.Select(node => node.EntityId).Distinct().Count());
                Assert.IsTrue(SocialStructureCompiler.Compile(document).Success);
            }
            finally
            {
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(document);
            }
        }

        private sealed class DummyHost : IGraphActionHost
        {
            public GraphStatus TickAction(GraphNodeExecutionContext context, string actionId) { return GraphStatus.Success; }
            public bool CheckCondition(GraphNodeExecutionContext context, string conditionId) { return true; }
            public void CancelAction(GraphNodeExecutionContext context, string actionId) { }
        }
    }
}
