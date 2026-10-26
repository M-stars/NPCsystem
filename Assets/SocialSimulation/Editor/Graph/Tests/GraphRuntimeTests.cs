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
    public sealed class GraphRuntimeTests
    {
        private sealed class Host : IGraphActionHost
        {
            public GraphStatus ActionStatus = GraphStatus.Success;
            public int Ticks;
            public int Cancels;
            public string LastAction;
            public readonly List<string> Calls = new List<string>();
            public readonly List<GraphNodeExecutionContext> Contexts = new List<GraphNodeExecutionContext>();
            public GraphNodeExecutionContext LastCancelledContext;

            public GraphStatus TickAction(GraphNodeExecutionContext context, string actionId)
            { Ticks++; LastAction = actionId; Calls.Add(actionId); Contexts.Add(context); return ActionStatus; }

            public bool CheckCondition(GraphNodeExecutionContext context, string conditionId)
            { return true; }

            public void CancelAction(GraphNodeExecutionContext context, string actionId)
            { Cancels++; LastCancelledContext = context; }
        }

        private sealed class Bindings : IRuntimeGraphBindings
        {
            public readonly Dictionary<string, ActionDefinition> Actions =
                new Dictionary<string, ActionDefinition>();
            public ActionDefinition ResolveAction(string actionId)
            { return Actions.TryGetValue(actionId, out var value) ? value : null; }
            public bool HasExecutor(string executorId) { return executorId == "family" || executorId == "trade"; }
            public bool HasCapability(string actorId, string capabilityId)
            { return actorId == "worker" && capabilityId == "social.daily"; }
            public bool CanEvaluateCondition(string conditionId) { return false; }
            public bool CanExecuteRuleCommand(string commandId) { return commandId == "family.remind"; }
        }

        private static GraphNode Node(GraphNodeType type)
        { return new GraphNode { Id = Guid.NewGuid().ToString("N"), Type = type }; }

        private static void Link(GraphDocument document, GraphNode from, GraphNode to, int order = 0)
        { document.Edges.Add(new GraphEdge { FromNodeId = from.Id, ToNodeId = to.Id, Order = order }); }

        private static GraphDocument Behavior(GraphNode child)
        {
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.Kind = GraphKind.Behavior;
            document.DocumentId = Guid.NewGuid().ToString("N");
            var root = Node(GraphNodeType.Root);
            document.Nodes.Add(root);
            document.Nodes.Add(child);
            Link(document, root, child);
            return document;
        }

        [Test]
        public void OneCompiledPlanKeepsActorsWaitStateSeparate()
        {
            var wait = Node(GraphNodeType.Wait);
            wait.Set("ticks", "2");
            var document = Behavior(wait);
            try
            {
                var result = GraphCompiler.Compile(document);
                Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
                var host = new Host();
                var a = new BehaviorInstance(result.BehaviorPlan, "a");
                var b = new BehaviorInstance(result.BehaviorPlan, "b");
                Assert.AreEqual(GraphStatus.Running, a.Tick(host, 0));
                Assert.AreEqual(GraphStatus.Running, b.Tick(host, 1));
                Assert.AreEqual(GraphStatus.Success, a.Tick(host, 2));
                Assert.AreEqual(GraphStatus.Running, b.Tick(host, 2));
                Assert.AreEqual(GraphStatus.Success, b.Tick(host, 3));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void BlackboardValuesAreTypedIsolatedAndRestoredPerActor()
        {
            var wait = Node(GraphNodeType.Wait); wait.Set("ticks", "3");
            var document = Behavior(wait);
            document.Variables.Add(new BlackboardVariable { Id = "score-id", Name = "Score",
                Type = BlackboardValueType.Integer, DefaultValue = "0", Scope = BlackboardScope.Agent,
                Access = BlackboardAccess.ReadWrite, SavePolicy = BlackboardSavePolicy.Checkpoint });
            try
            {
                var compiled = GraphCompiler.Compile(document);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                var host = new Host();
                var alice = new BehaviorInstance(compiled.BehaviorPlan, "alice", "task-alice");
                var bob = new BehaviorInstance(compiled.BehaviorPlan, "bob", "task-bob");
                Assert.AreEqual(0, alice.Blackboard.GetInteger("score-id"));
                Assert.IsTrue(alice.Blackboard.SetInteger("score-id", 17));
                Assert.AreEqual(0, bob.Blackboard.GetInteger("score-id"));
                Assert.IsFalse(bob.Blackboard.TrySetRaw("score-id", "not-an-integer"));
                Assert.AreEqual(GraphStatus.Running, alice.Tick(host, 10));
                Assert.AreEqual(GraphStatus.Running, bob.Tick(host, 11));
                var saved = JsonUtility.FromJson<BehaviorCheckpoint>(JsonUtility.ToJson(alice.CaptureCheckpoint()));
                Assert.AreEqual("17", saved.Variables.Single().Value);
                Assert.IsTrue(alice.Blackboard.SetInteger("score-id", 99));
                Assert.AreEqual("17", saved.Variables.Single().Value);
                var resumed = new BehaviorInstance(compiled.BehaviorPlan, "alice", "task-alice");
                resumed.RestoreCheckpoint(saved);
                Assert.AreEqual(17, resumed.Blackboard.GetInteger("score-id"));
                Assert.AreEqual(0, bob.Blackboard.GetInteger("score-id"));
                Assert.AreEqual(GraphStatus.Success, resumed.Tick(host, 13));
                Assert.AreEqual(GraphStatus.Running, bob.Tick(host, 13));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void MissingOrMismatchedBlackboardVariableKeepsPublishedPlan()
        {
            var set = Node(GraphNodeType.BlackboardSet);
            set.Set("variableId", "energy-id"); set.Set("value", "5");
            var document = Behavior(set);
            document.Variables.Add(new BlackboardVariable { Id = "energy-id", Name = "Energy",
                Type = BlackboardValueType.Integer, DefaultValue = "0", Scope = BlackboardScope.Agent,
                Access = BlackboardAccess.ReadWrite, SavePolicy = BlackboardSavePolicy.Checkpoint });
            try
            {
                var slot = new GraphPlanSlot();
                var first = slot.TryApply(document);
                Assert.IsTrue(first.Success, string.Join("\n", first.Diagnostics));
                var previous = slot.BehaviorPlan;
                document.Variables.Clear();
                var missing = slot.TryApply(document);
                Assert.IsFalse(missing.Success);
                Assert.IsTrue(missing.Diagnostics.Any(item => item.Code == "MissingVariable"));
                Assert.AreSame(previous, slot.BehaviorPlan);
                document.Variables.Add(new BlackboardVariable { Id = "energy-id", Name = "Energy",
                    Type = BlackboardValueType.Boolean, DefaultValue = "false" });
                var mismatch = slot.TryApply(document);
                Assert.IsFalse(mismatch.Success);
                Assert.IsTrue(mismatch.Diagnostics.Any(item => item.Code == "InvalidVariableValue"));
                Assert.AreSame(previous, slot.BehaviorPlan);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void BlackboardNodeUsesStableIdAfterVariableRename()
        {
            var set = Node(GraphNodeType.BlackboardSet);
            set.Set("variableId", "stable-id"); set.Set("value", "true");
            var document = Behavior(set);
            var variable = new BlackboardVariable { Id = "stable-id", Name = "Old name",
                Type = BlackboardValueType.Boolean, DefaultValue = "false" };
            document.Variables.Add(variable);
            try
            {
                Assert.IsTrue(GraphCompiler.Compile(document).Success);
                variable.Name = "New name";
                var compiled = GraphCompiler.Compile(document);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                Assert.AreEqual(GraphStatus.Success,
                    new BehaviorInstance(compiled.BehaviorPlan, "actor").Tick(new Host(), 0));
                Assert.AreEqual("stable-id", set.Get("variableId"));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void WorkbenchCreatesAndBindsBlackboardVariableThroughInspector()
        {
            var path = "Assets/SocialSimulation/Editor/Graph/Tests/__blackboard_" +
                Guid.NewGuid().ToString("N") + ".asset";
            var set = Node(GraphNodeType.BlackboardSet); set.Set("value", "true");
            var document = Behavior(set);
            GraphWorkbenchWindow window = null;
            try
            {
                AssetDatabase.CreateAsset(document, path);
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI(); window.Open(document);
                typeof(GraphWorkbenchWindow).GetMethod("AddVariable", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, null);
                var variable = document.Variables.Single();
                Assert.IsFalse(string.IsNullOrEmpty(variable.Id));
                var graph = (GraphView)typeof(GraphWorkbenchWindow)
                    .GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                var nodeView = graph.nodes.ToList().Single(item => item.viewDataKey == set.Id);
                graph.AddToSelection(nodeView);
                typeof(GraphWorkbenchWindow).GetMethod("RefreshInspector", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, null);
                var inspector = window.rootVisualElement.Q<VisualElement>("inspector");
                var binding = inspector.Q<PopupField<string>>();
                Assert.IsNotNull(binding);
                binding.value = variable.Id;
                Assert.AreEqual(variable.Id, document.FindNode(set.Id).Get("variableId"));
                var card = window.rootVisualElement.Q<VisualElement>("blackboardList");
                card.Q<TextField>().value = "Renamed score";
                Assert.AreEqual(variable.Id, document.FindNode(set.Id).Get("variableId"));
                Assert.IsTrue(GraphCompiler.Compile(document).Success);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var reopened = AssetDatabase.LoadAssetAtPath<GraphDocument>(path);
                Assert.AreEqual("Renamed score", reopened.Variables.Single().Name);
                Assert.AreEqual(variable.Id, reopened.FindNode(set.Id).Get("variableId"));
            }
            finally
            {
                if (window != null) window.Close();
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void HouseholdAndTradeActionsCompileExecuteAndAppearWithoutCampusPackage()
        {
            var sequence = Node(GraphNodeType.Sequence);
            var document = Behavior(sequence);
            var breakfast = Node(GraphNodeType.Action); breakfast.Set("actionId", "activity.breakfast");
            var trade = Node(GraphNodeType.Action); trade.Set("actionId", "trade.exchange");
            document.Nodes.Add(breakfast); document.Nodes.Add(trade);
            Link(document, sequence, breakfast, 0); Link(document, sequence, trade, 1);
            GraphWorkbenchWindow window = null;
            try
            {
                var compiled = GraphCompiler.Compile(document);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                var host = new Host();
                Assert.AreEqual(GraphStatus.Success,
                    new BehaviorInstance(compiled.BehaviorPlan, "household.worker").Tick(host, 0));
                CollectionAssert.AreEqual(new[] { "activity.breakfast", "trade.exchange" }, host.Calls);

                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI(); window.Open(document);
                var palette = window.rootVisualElement.Q<VisualElement>("palette");
                Assert.IsTrue(palette.Query<Button>().ToList().Any(item => item.text.Contains("activity.breakfast")));
                Assert.IsTrue(palette.Query<Button>().ToList().Any(item => item.text.Contains("trade.exchange")));
            }
            finally
            {
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(document);
            }
        }

        [Test]
        public void HostBindingFailureKeepsLastPublishedPlan()
        {
            var action = Node(GraphNodeType.Action); action.Set("actionId", "activity.breakfast");
            var document = Behavior(action);
            var bindings = new Bindings();
            bindings.Actions.Add("activity.breakfast", new ActionDefinition
                { Id = "activity.breakfast", ExecutorId = "family", CapabilityId = "social.daily" });
            bindings.Actions.Add("trade.exchange", new ActionDefinition
                { Id = "trade.exchange", ExecutorId = "trade", CapabilityId = "social.daily" });
            try
            {
                var slot = new GraphPlanSlot();
                var first = slot.TryApply(document, bindings, new[] { "worker" });
                Assert.IsTrue(first.Success, string.Join("\n", first.Diagnostics));
                var oldPlan = slot.BehaviorPlan;
                action.Set("actionId", "trade.exchange");
                Assert.IsTrue(slot.TryApply(document, bindings, new[] { "worker" }).Success);
                Assert.AreNotSame(oldPlan, slot.BehaviorPlan);
                oldPlan = slot.BehaviorPlan;
                action.Set("actionId", "unregistered.action");
                var rejected = slot.TryApply(document, bindings, new[] { "worker" });
                Assert.IsFalse(rejected.Success);
                Assert.IsTrue(rejected.Diagnostics.Any(item => item.Code == "MissingActionBinding"));
                Assert.AreSame(oldPlan, slot.BehaviorPlan);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void TimeoutCancelsRunningActionAndRetryIsBounded()
        {
            var timeout = Node(GraphNodeType.Timeout);
            timeout.Set("ticks", "2");
            var document = Behavior(timeout);
            var action = Node(GraphNodeType.Action);
            action.Set("actionId", "move");
            document.Nodes.Add(action);
            Link(document, timeout, action);
            try
            {
                var plan = GraphCompiler.Compile(document);
                Assert.IsTrue(plan.Success, string.Join("\n", plan.Diagnostics));
                var host = new Host { ActionStatus = GraphStatus.Running };
                var instance = new BehaviorInstance(plan.BehaviorPlan, "a");
                Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 0));
                Assert.AreEqual(GraphStatus.Failure, instance.Tick(host, 2));
                Assert.AreEqual(1, host.Cancels);
                Assert.AreEqual(host.Contexts[0].NodeId, host.LastCancelledContext.NodeId);
                Assert.AreEqual(host.Contexts[0].InstanceId, host.LastCancelledContext.InstanceId);

                timeout.Type = GraphNodeType.Retry;
                timeout.Set("attempts", "2");
                var retryPlan = GraphCompiler.Compile(document);
                Assert.IsTrue(retryPlan.Success, string.Join("\n", retryPlan.Diagnostics));
                host = new Host { ActionStatus = GraphStatus.Failure };
                instance = new BehaviorInstance(retryPlan.BehaviorPlan, "a");
                Assert.AreEqual(GraphStatus.Running, instance.Tick(host, 0));
                Assert.AreEqual(GraphStatus.Failure, instance.Tick(host, 1));
                Assert.AreEqual(2, host.Ticks);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void RuleWithEvidenceGateEmitsOnlyWhenObserved()
        {
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.Kind = GraphKind.Rule;
            document.DocumentId = Guid.NewGuid().ToString("N");
            var trigger = Node(GraphNodeType.RuleEvent); trigger.Set("eventType", "locomotion.run");
            var scope = Node(GraphNodeType.RuleScope); scope.Set("areaId", "hall");
            var condition = Node(GraphNodeType.RuleCondition); condition.Set("requiresEvidence", "true");
            var effect = Node(GraphNodeType.RuleEffect); effect.Set("commandId", "warning.task");
            effect.Set("target", "actor");
            document.Nodes.AddRange(new[] { trigger, scope, condition, effect });
            Link(document, trigger, scope); Link(document, scope, condition); Link(document, condition, effect);
            try
            {
                var result = GraphCompiler.Compile(document);
                Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
                var context = new RuleContext { EventId = "e", EventType = "locomotion.run",
                    ActorId = "student", AreaId = "hall", HasEvidence = false };
                Assert.AreEqual(0, RuleExecutor.Evaluate(result.RulePlan, context).MatchCount);
                context.HasEvidence = true;
                Assert.AreEqual(1, RuleExecutor.Evaluate(result.RulePlan, context).MatchCount);
                context.AreaId = "elsewhere";
                Assert.AreEqual(0, RuleExecutor.Evaluate(result.RulePlan, context).MatchCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void InvalidDraftCannotReplacePreviouslyCompiledPlan()
        {
            var action = Node(GraphNodeType.Action);
            action.Set("actionId", "sit");
            var document = Behavior(action);
            try
            {
                var slot = new GraphPlanSlot();
                var valid = slot.TryApply(document);
                Assert.IsTrue(valid.Success);
                Assert.AreSame(valid.BehaviorPlan, slot.BehaviorPlan);
                action.Set("actionId", "");
                var invalid = slot.TryApply(document);
                Assert.IsFalse(invalid.Success);
                Assert.IsNull(invalid.BehaviorPlan);
                Assert.AreSame(valid.BehaviorPlan, slot.BehaviorPlan);
                var host = new Host();
                Assert.AreEqual(GraphStatus.Success, new BehaviorInstance(slot.BehaviorPlan, "a").Tick(host, 0));
                Assert.AreEqual("sit", host.LastAction);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void AssetSaveReloadPreservesIdentifiersEdgesAndParameters()
        {
            var path = "Assets/SocialSimulation/Editor/Graph/Tests/__temp_" + Guid.NewGuid().ToString("N") + ".asset";
            var action = Node(GraphNodeType.Action);
            action.Set("actionId", "walk");
            var document = Behavior(action);
            string documentId = document.DocumentId;
            string nodeId = action.Id;
            try
            {
                AssetDatabase.CreateAsset(document, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var loaded = AssetDatabase.LoadAssetAtPath<GraphDocument>(path);
                Assert.NotNull(loaded);
                Assert.AreEqual(documentId, loaded.DocumentId);
                Assert.AreEqual("walk", loaded.FindNode(nodeId).Get("actionId"));
                Assert.AreEqual(nodeId, loaded.Edges[0].ToNodeId);
                Assert.IsTrue(GraphCompiler.Compile(loaded).Success);
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void WorkbenchParameterEditUndoRedoAndSaveReload()
        {
            var path = "Assets/SocialSimulation/Editor/Graph/Tests/__ui_" + Guid.NewGuid().ToString("N") + ".asset";
            var action = Node(GraphNodeType.Action);
            action.Set("actionId", "walk");
            var document = Behavior(action);
            GraphWorkbenchWindow window = null;
            try
            {
                AssetDatabase.CreateAsset(document, path);
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI();
                window.Open(document);
                var graphField = typeof(GraphWorkbenchWindow).GetField("graph",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var view = (GraphView)graphField.GetValue(window);
                var nodeView = view.nodes.ToList().Single(node => node.viewDataKey == action.Id);
                view.AddToSelection(nodeView);
                typeof(GraphWorkbenchWindow).GetMethod("RefreshInspector",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                var inspector = window.rootVisualElement.Q<ScrollView>("inspector");
                var parameter = inspector.Q<TextField>();
                Assert.NotNull(parameter);
                parameter.value = "sit";
                Assert.AreEqual("sit", document.FindNode(action.Id).Get("actionId"));
                Undo.PerformUndo();
                Assert.AreEqual("walk", document.FindNode(action.Id).Get("actionId"));
                Undo.PerformRedo();
                Assert.AreEqual("sit", document.FindNode(action.Id).Get("actionId"));
                typeof(GraphWorkbenchWindow).GetMethod("Save",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var loaded = AssetDatabase.LoadAssetAtPath<GraphDocument>(path);
                Assert.AreEqual("sit", loaded.FindNode(action.Id).Get("actionId"));
                Assert.IsTrue(GraphCompiler.Compile(loaded).Success);
            }
            finally
            {
                if (window != null) window.Close();
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void EditorClipboardCopiesInternalEdgesWithFreshIds()
        {
            var branch = Node(GraphNodeType.Sequence);
            var document = Behavior(branch);
            var first = Node(GraphNodeType.Action); first.Set("actionId", "walk");
            var second = Node(GraphNodeType.Action); second.Set("actionId", "sit");
            document.Nodes.Add(first); document.Nodes.Add(second);
            Link(document, branch, first, 0); Link(document, branch, second, 1);
            GraphWorkbenchWindow window = null;
            try
            {
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI();
                window.Open(document);
                Assert.NotNull(window.rootVisualElement.Q<VisualElement>("graphHost"));
                var field = typeof(GraphWorkbenchWindow).GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic);
                var view = (GraphView)field.GetValue(window);
                var selected = view.nodes.ToList().Where(node =>
                    node.viewDataKey == branch.Id || node.viewDataKey == first.Id || node.viewDataKey == second.Id)
                    .Cast<GraphElement>().ToList();
                var json = view.serializeGraphElements(selected);
                view.unserializeAndPaste("Paste", json);
                Assert.AreEqual(7, document.Nodes.Count);
                Assert.AreEqual(5, document.Edges.Count);
                Assert.AreEqual(7, document.Nodes.Select(node => node.Id).Distinct().Count());
                Assert.AreEqual(1, document.Nodes.Count(node => node.Type == GraphNodeType.Root));
            }
            finally
            {
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(document);
            }
        }

        [Test]
        public void BehaviorChildOrderUsesExplicitEdgeOrder()
        {
            var sequence = Node(GraphNodeType.Sequence);
            var document = Behavior(sequence);
            var first = Node(GraphNodeType.Action); first.Set("actionId", "first"); first.Position = new Vector2(900, 900);
            var second = Node(GraphNodeType.Action); second.Set("actionId", "second"); second.Position = new Vector2(0, 0);
            document.Nodes.Add(first); document.Nodes.Add(second);
            Link(document, sequence, second, 2); Link(document, sequence, first, 1);
            try
            {
                var result = GraphCompiler.Compile(document);
                Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
                var host = new Host();
                Assert.AreEqual(GraphStatus.Success,
                    new BehaviorInstance(result.BehaviorPlan, "a").Tick(host, 0));
                CollectionAssert.AreEqual(new[] { "first", "second" }, host.Calls);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void NormQueryDoesNotNeedEventToExist()
        {
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.Kind = GraphKind.Rule;
            document.DocumentId = Guid.NewGuid().ToString("N");
            var scope = Node(GraphNodeType.RuleScope); scope.Set("areaId", "hall");
            var norm = Node(GraphNodeType.RuleNorm); norm.Set("normKind", "prohibition");
            norm.Set("actionId", "run"); norm.Set("priority", "10");
            document.Nodes.Add(scope); document.Nodes.Add(norm); Link(document, scope, norm);
            try
            {
                var result = GraphCompiler.Compile(document);
                Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
                var context = new RuleContext { AreaId = "hall", ActionId = "run" };
                Assert.AreEqual(1, RuleExecutor.QueryNorms(result.RulePlan, context).Count);
                Assert.AreEqual(0, RuleExecutor.Evaluate(result.RulePlan, context).MatchCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void ReusedSubtreeCallsHaveDistinctExecutionContexts()
        {
            var action = Node(GraphNodeType.Action); action.Set("actionId", "sit");
            var child = Behavior(action);
            var sequence = Node(GraphNodeType.Sequence);
            var parent = Behavior(sequence);
            var first = Node(GraphNodeType.Subtree); first.Subgraph = child;
            var second = Node(GraphNodeType.Subtree); second.Subgraph = child;
            parent.Nodes.Add(first); parent.Nodes.Add(second);
            Link(parent, sequence, first, 0); Link(parent, sequence, second, 1);
            try
            {
                var result = GraphCompiler.Compile(parent);
                Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
                var host = new Host();
                var instance = new BehaviorInstance(result.BehaviorPlan, "a", "behavior-1");
                Assert.AreEqual(GraphStatus.Success, instance.Tick(host, 0));
                Assert.AreEqual(2, host.Contexts.Count);
                Assert.AreEqual(host.Contexts[0].GraphId, host.Contexts[1].GraphId);
                Assert.AreEqual(host.Contexts[0].NodeId, host.Contexts[1].NodeId);
                Assert.AreNotEqual(host.Contexts[0].InstanceId, host.Contexts[1].InstanceId);
                Assert.AreEqual("behavior-1/" + first.Id, host.Contexts[0].InstanceId);
                Assert.AreEqual("behavior-1/" + second.Id, host.Contexts[1].InstanceId);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
                UnityEngine.Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void SubtreeChangeChangesParentRevisionWithoutParentVersionIncrement()
        {
            var childAction = Node(GraphNodeType.Action); childAction.Set("actionId", "family.talk");
            var child = Behavior(childAction);
            var subtree = Node(GraphNodeType.Subtree); subtree.Subgraph = child;
            var parent = Behavior(subtree);
            try
            {
                var before = GraphCompiler.Compile(parent);
                Assert.IsTrue(before.Success);
                var oldInstance = new BehaviorInstance(before.BehaviorPlan, "parent", "assignment");
                var checkpoint = oldInstance.CaptureCheckpoint();
                childAction.Set("actionId", "work.perform");
                var after = GraphCompiler.Compile(parent);
                Assert.IsTrue(after.Success);
                Assert.AreEqual(before.BehaviorPlan.Version, after.BehaviorPlan.Version);
                Assert.AreNotEqual(before.BehaviorPlan.Revision, after.BehaviorPlan.Revision);
                Assert.Throws<InvalidOperationException>(() =>
                    new BehaviorInstance(after.BehaviorPlan, "parent", "assignment")
                        .RestoreCheckpoint(checkpoint));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
                UnityEngine.Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void BehaviorCheckpointRestoresSequenceAndWaitWithoutRepeatingFinishedAction()
        {
            var sequence = Node(GraphNodeType.Sequence);
            var document = Behavior(sequence);
            var action = Node(GraphNodeType.Action); action.Set("actionId", "reserve");
            var wait = Node(GraphNodeType.Wait); wait.Set("ticks", "2");
            document.Nodes.Add(action); document.Nodes.Add(wait);
            Link(document, sequence, action, 0); Link(document, sequence, wait, 1);
            try
            {
                var compiled = GraphCompiler.Compile(document);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                var firstHost = new Host();
                var first = new BehaviorInstance(compiled.BehaviorPlan, "a", "instance-a");
                Assert.AreEqual(GraphStatus.Running, first.Tick(firstHost, 0));
                CollectionAssert.AreEqual(new[] { "reserve" }, firstHost.Calls);
                var json = JsonUtility.ToJson(first.CaptureCheckpoint());
                var checkpoint = JsonUtility.FromJson<BehaviorCheckpoint>(json);
                var restored = new BehaviorInstance(compiled.BehaviorPlan, "a", "instance-a");
                restored.RestoreCheckpoint(checkpoint);
                var secondHost = new Host();
                Assert.AreEqual(GraphStatus.Success, restored.Tick(secondHost, 2));
                Assert.AreEqual(0, secondHost.Calls.Count);
                Assert.Throws<InvalidOperationException>(() =>
                    new BehaviorInstance(compiled.BehaviorPlan, "a", "wrong-instance").RestoreCheckpoint(checkpoint));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void UndoRedoAndReloadPreserveGraphChanges()
        {
            var path = "Assets/SocialSimulation/Editor/Graph/Tests/__undo_" + Guid.NewGuid().ToString("N") + ".asset";
            var action = Node(GraphNodeType.Action); action.Set("actionId", "walk");
            var document = Behavior(action);
            try
            {
                AssetDatabase.CreateAsset(document, path);
                Undo.IncrementCurrentGroup();
                Undo.RegisterCompleteObjectUndo(document, "Change action ID");
                document.FindNode(action.Id).Set("actionId", "sit");
                EditorUtility.SetDirty(document);
                Undo.FlushUndoRecordObjects();
                Assert.AreEqual("sit", document.FindNode(action.Id).Get("actionId"));
                Undo.PerformUndo();
                Assert.AreEqual("walk", document.FindNode(action.Id).Get("actionId"));
                Undo.PerformRedo();
                Assert.AreEqual("sit", document.FindNode(action.Id).Get("actionId"));
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Assert.AreEqual("sit", AssetDatabase.LoadAssetAtPath<GraphDocument>(path)
                    .FindNode(action.Id).Get("actionId"));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void InvalidBehaviorCheckpointLeavesInstanceFreshForValidRestore()
        {
            var wait = Node(GraphNodeType.Wait); wait.Set("ticks", "4");
            var document = Behavior(wait);
            document.Variables.Add(new BlackboardVariable { Id = "counter", Name = "Counter",
                Type = BlackboardValueType.Integer, DefaultValue = "0", SavePolicy = BlackboardSavePolicy.Checkpoint });
            try
            {
                var plan = GraphCompiler.Compile(document).BehaviorPlan;
                var original = new BehaviorInstance(plan, "actor", "instance");
                original.Blackboard.SetInteger("counter", 7);
                original.Tick(new Host(), 10);
                var valid = original.CaptureCheckpoint();
                var corrupt = JsonUtility.FromJson<BehaviorCheckpoint>(JsonUtility.ToJson(valid));
                corrupt.Nodes.Add(new BehaviorNodeCheckpoint { NodeId = "missing-node" });
                var restored = new BehaviorInstance(plan, "actor", "instance");
                Assert.Throws<InvalidOperationException>(() => restored.RestoreCheckpoint(corrupt));
                Assert.AreEqual(0, restored.CaptureCheckpoint().Nodes.Count);
                Assert.AreEqual(0, restored.Blackboard.GetInteger("counter"));
                restored.RestoreCheckpoint(valid);
                Assert.AreEqual(7, restored.Blackboard.GetInteger("counter"));
                Assert.AreEqual(GraphStatus.Success, restored.Tick(new Host(), 14));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void ThreeNestedSubtreesRoundTripWithoutSerializationDepthLoss()
        {
            var documents = new List<GraphDocument>();
            var depthWarnings = new List<string>();
            Application.LogCallback observe = (message, stack, type) =>
            {
                if (message.IndexOf("serialization depth", StringComparison.OrdinalIgnoreCase) >= 0)
                    depthWarnings.Add(message);
            };
            Application.logMessageReceived += observe;
            try
            {
                var sequence = Node(GraphNodeType.Sequence);
                var leaf = Behavior(sequence);
                documents.Add(leaf);
                var reserve = Node(GraphNodeType.Action); reserve.Set("actionId", "reserve");
                var wait = Node(GraphNodeType.Wait); wait.Set("ticks", "4");
                leaf.Nodes.Add(reserve); leaf.Nodes.Add(wait);
                Link(leaf, sequence, reserve, 0); Link(leaf, sequence, wait, 1);
                GraphDocument current = leaf;
                for (int depth = 0; depth < 3; depth++)
                {
                    var branch = Node(GraphNodeType.Sequence);
                    var parent = Behavior(branch);
                    documents.Add(parent);
                    parent.Variables.Add(new BlackboardVariable { Id = "depth", Name = "Depth",
                        Type = BlackboardValueType.Integer, DefaultValue = "0", SavePolicy = BlackboardSavePolicy.Checkpoint });
                    var set = Node(GraphNodeType.BlackboardSet); set.Set("variableId", "depth");
                    set.Set("value", (depth + 1).ToString());
                    var subtree = Node(GraphNodeType.Subtree); subtree.Subgraph = current;
                    parent.Nodes.Add(set); parent.Nodes.Add(subtree);
                    Link(parent, branch, set, 0); Link(parent, branch, subtree, 1);
                    current = parent;
                }
                var compiled = GraphCompiler.Compile(current);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                var host = new Host();
                var original = new BehaviorInstance(compiled.BehaviorPlan, "actor", "nested-run");
                Assert.AreEqual(GraphStatus.Running, original.Tick(host, 10));
                CollectionAssert.AreEqual(new[] { "reserve" }, host.Calls);
                string json = JsonUtility.ToJson(original.CaptureCheckpoint());
                var saved = JsonUtility.FromJson<BehaviorCheckpoint>(json);
                var cursor = saved;
                for (int depth = 3; depth > 0; depth--)
                {
                    Assert.AreEqual(2, cursor.SchemaVersion);
                    Assert.AreEqual(depth.ToString(), cursor.Variables.Single().Value);
                    var nested = cursor.Nodes.Single(node => node.HasSubtree);
                    Assert.IsFalse(string.IsNullOrEmpty(nested.SubtreeJson));
                    cursor = nested.Subtree;
                    Assert.NotNull(cursor);
                }
                Assert.AreEqual(leaf.DocumentId, cursor.GraphId);
                Assert.AreEqual(10, cursor.Nodes.Single(node => node.NodeId == wait.Id).StartTick);
                var restored = new BehaviorInstance(compiled.BehaviorPlan, "actor", "nested-run");
                restored.RestoreCheckpoint(saved);
                var resumedHost = new Host();
                Assert.AreEqual(GraphStatus.Running, restored.Tick(resumedHost, 13));
                Assert.AreEqual(GraphStatus.Success, restored.Tick(resumedHost, 14));
                Assert.AreEqual(0, resumedHost.Calls.Count, "The completed reservation must not be replayed.");
                Assert.IsEmpty(depthWarnings);
            }
            finally
            {
                Application.logMessageReceived -= observe;
                foreach (var document in documents) UnityEngine.Object.DestroyImmediate(document);
            }
        }

        [Test]
        public void WorkbenchSubgraphNavigationReturnsToParentWithoutChangingEitherAsset()
        {
            var wait = Node(GraphNodeType.Wait); wait.Set("ticks", "2");
            var child = Behavior(wait);
            var subtree = Node(GraphNodeType.Subtree); subtree.Subgraph = child;
            var parent = Behavior(subtree);
            GraphWorkbenchWindow window = null;
            try
            {
                string originalParent = JsonUtility.ToJson(parent);
                string originalChild = JsonUtility.ToJson(child);
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI(); window.Open(parent);
                typeof(GraphWorkbenchWindow).GetMethod("EnterSubgraph", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, new object[] { child });
                var documentField = typeof(GraphWorkbenchWindow).GetField("document", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.AreSame(child, documentField.GetValue(window));
                Assert.IsTrue(window.rootVisualElement.Q<Button>("backToParentGraph").enabledSelf);
                typeof(GraphWorkbenchWindow).GetMethod("NavigateBack", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, null);
                Assert.AreSame(parent, documentField.GetValue(window));
                Assert.IsFalse(window.rootVisualElement.Q<Button>("backToParentGraph").enabledSelf);
                Assert.AreEqual(originalParent, JsonUtility.ToJson(parent));
                Assert.AreEqual(originalChild, JsonUtility.ToJson(child));
            }
            finally
            {
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(parent);
                UnityEngine.Object.DestroyImmediate(child);
            }
        }

        [Test]
        public void CanvasConnectionAndDisconnectionUpdateDocument()
        {
            var action = Node(GraphNodeType.Action); action.Set("actionId", "walk");
            var document = Behavior(action);
            document.Edges.Clear();
            GraphWorkbenchWindow window = null;
            try
            {
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI();
                window.Open(document);
                var field = typeof(GraphWorkbenchWindow).GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic);
                var view = (GraphView)field.GetValue(window);
                var root = view.nodes.ToList().Single(node => node.viewDataKey == document.Nodes[0].Id);
                var leaf = view.nodes.ToList().Single(node => node.viewDataKey == action.Id);
                var output = root.outputContainer[0] as Port;
                var input = leaf.inputContainer[0] as Port;
                var edge = output.ConnectTo(input);
                view.graphViewChanged(new GraphViewChange { edgesToCreate = new List<Edge> { edge } });
                Assert.AreEqual(1, document.Edges.Count);
                view.graphViewChanged(new GraphViewChange { elementsToRemove = new List<GraphElement> { edge } });
                Assert.AreEqual(0, document.Edges.Count);
            }
            finally
            {
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(document);
            }
        }
    }
}
