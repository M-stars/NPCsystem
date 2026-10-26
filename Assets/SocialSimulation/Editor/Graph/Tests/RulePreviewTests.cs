using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SocialSimulation.Editor.Graph;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Graph.Tests
{
    public sealed class RulePreviewTests
    {
        private static GraphNode Node(GraphNodeType type, string id)
        { return new GraphNode { Id = id, Type = type }; }

        private static void Connect(GraphDocument document, string from, string to, int order = 0)
        { document.Edges.Add(new GraphEdge { FromNodeId = from, ToNodeId = to, Order = order }); }

        private static GraphDocument Rule()
        {
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.Kind = GraphKind.Rule;
            document.DocumentId = "test.rule.preview";
            var trigger = Node(GraphNodeType.RuleEvent, "event"); trigger.Set("eventType", "work.enter");
            var scope = Node(GraphNodeType.RuleScope, "scope"); scope.Set("areaId", "workshop");
            scope.Set("fromSeconds", "10"); scope.Set("untilSeconds", "20");
            var first = Node(GraphNodeType.RuleCondition, "first");
            first.Set("field", "roleId"); first.Set("equals", "worker"); first.Set("requiresEvidence", "true");
            var second = Node(GraphNodeType.RuleCondition, "second");
            second.Set("field", "roleId"); second.Set("equals", "guest"); second.Set("requiresEvidence", "false");
            second.Set("combine", "or"); second.Set("negate", "true");
            var effect = Node(GraphNodeType.RuleEffect, "effect"); effect.Set("commandId", "work.check");
            document.Nodes.AddRange(new[] { trigger, scope, first, second, effect });
            Connect(document, "event", "scope"); Connect(document, "scope", "first");
            Connect(document, "first", "second"); Connect(document, "second", "effect");
            return document;
        }

        private static RuleContext Context()
        {
            return new RuleContext { EventId = "event-1", EventType = "work.enter", ActorId = "actor-1",
                AreaId = "workshop", RoleId = "manager", TimeSeconds = 15, HasEvidence = true };
        }

        [Test]
        public void RuleOrAndNotEvaluateInPathOrderWithoutBypassingEvidence()
        {
            var document = Rule();
            try
            {
                var compiled = GraphCompiler.Compile(document);
                Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
                var context = Context();
                // false OR NOT false -> true.
                Assert.AreEqual(1, RuleExecutor.Evaluate(compiled.RulePlan, context, publishTrace: false).MatchCount);
                context.RoleId = "guest";
                Assert.AreEqual(0, RuleExecutor.Evaluate(compiled.RulePlan, context, publishTrace: false).MatchCount);
                context.RoleId = "worker";
                context.HasEvidence = false;
                var trace = new List<GraphTraceEvent>();
                Assert.AreEqual(0, RuleExecutor.Evaluate(compiled.RulePlan, context, trace: trace.Add, publishTrace: false).MatchCount);
                Assert.IsTrue(trace.Any(entry => entry.Reason.StartsWith("MissingEvidence", StringComparison.Ordinal)));
                document.FindNode("second").Set("combine", "and");
                context.HasEvidence = true;
                context.RoleId = "manager";
                Assert.AreEqual(0, RuleExecutor.Evaluate(GraphCompiler.Compile(document).RulePlan,
                    context, publishTrace: false).MatchCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [TestCase(9.999, 0)]
        [TestCase(10, 1)]
        [TestCase(19.999, 1)]
        [TestCase(20, 0)]
        public void RuleTimeWindowHasInclusiveStartAndExclusiveEnd(double time, int count)
        {
            var document = Rule();
            try
            {
                var context = Context(); context.TimeSeconds = time;
                Assert.AreEqual(count, RuleExecutor.Evaluate(GraphCompiler.Compile(document).RulePlan,
                    context, publishTrace: false).MatchCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [TestCase("fromSeconds", "NaN", "InvalidTimeWindow")]
        [TestCase("untilSeconds", "9", "InvalidTimeWindow")]
        [TestCase("untilSeconds", "Infinity", "InvalidTimeWindow")]
        public void InvalidTimeWindowCannotPublish(string key, string value, string code)
        {
            var document = Rule();
            try
            {
                var slot = new GraphPlanSlot();
                Assert.IsTrue(slot.TryApply(document).Success);
                var original = slot.RulePlan;
                document.FindNode("scope").Set(key, value);
                var invalid = slot.TryApply(document);
                Assert.IsFalse(invalid.Success);
                Assert.IsTrue(invalid.Diagnostics.Any(entry => entry.Code == code));
                Assert.AreSame(original, slot.RulePlan);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void RuleCommandsFollowEdgeOrderAfterNodesAreReordered()
        {
            var document = Rule();
            try
            {
                var earlier = Node(GraphNodeType.RuleEffect, "earlier"); earlier.Set("commandId", "work.prepare");
                document.Nodes.Add(earlier);
                document.Edges.Single(edge => edge.ToNodeId == "effect").Order = 10;
                Connect(document, "second", "earlier", -1);
                var initial = GraphCompiler.Compile(document);
                document.Nodes.Reverse();
                var reordered = GraphCompiler.Compile(document);
                Assert.IsTrue(initial.Success); Assert.IsTrue(reordered.Success);
                CollectionAssert.AreEqual(new[] { "work.prepare", "work.check" },
                    RuleExecutor.Evaluate(reordered.RulePlan, Context(), publishTrace: false).Commands.Select(command => command.CommandId));
                Assert.AreEqual(initial.RulePlan.Revision, reordered.RulePlan.Revision);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void DuplicateParameterIsRejectedBeforeCompiledValueCanDifferFromValidatedValue()
        {
            var document = Rule();
            try
            {
                document.FindNode("first").Parameters.Add(new GraphParameter("requiresEvidence", "false"));
                var result = GraphCompiler.Compile(document);
                Assert.IsFalse(result.Success);
                Assert.IsTrue(result.Diagnostics.Any(entry => entry.Code == "InvalidParameterKey"));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void PreviewShowsTraceAndCommandsWithoutPublishingOrChangingDocument()
        {
            var document = Rule();
            GraphWorkbenchWindow window = null;
            int published = 0;
            Action<GraphTraceEvent> listener = _ => published++;
            GraphTraceHub.Published += listener;
            try
            {
                string before = JsonUtility.ToJson(document);
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI(); window.Open(document);
                var ui = window.rootVisualElement;
                ui.Q<DoubleField>("previewTimeSeconds").value = 15;
                ui.Q<TextField>("preview_actorId").value = "actor-1";
                ui.Q<TextField>("preview_areaId").value = "workshop";
                ui.Q<TextField>("preview_roleId").value = "manager";
                ui.Q<Toggle>("previewHasEvidence").value = true;
                window.PreviewRule();
                var output = ui.Q<ScrollView>("rulePreviewResults").Query<Button>().ToList().Select(item => item.text).ToList();
                Assert.IsTrue(output.Any(text => text.Contains("拟提交 1 条命令")));
                Assert.IsTrue(output.Any(text => text.Contains("actual=manager")));
                Assert.IsTrue(output.Any(text => text.Contains("work.check → actor-1")));
                Assert.AreEqual(0, published, "A draft preview must not publish into the running world's trace.");
                Assert.AreEqual(before, JsonUtility.ToJson(document));
                ui.Q<Toggle>("previewHasEvidence").value = false;
                window.PreviewRule();
                Assert.IsTrue(ui.Q<ScrollView>("rulePreviewResults").Query<Button>().ToList()
                    .Any(item => item.text.Contains("拟提交 0 条命令")));
            }
            finally
            {
                GraphTraceHub.Published -= listener;
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(document);
            }
        }

        [Test]
        public void UnknownNodeRemainsVisibleAndKeepsItsParameters()
        {
            var document = Rule();
            GraphWorkbenchWindow window = null;
            try
            {
                document.FindNode("first").Type = (GraphNodeType)10001;
                string before = JsonUtility.ToJson(document);
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI(); window.Open(document);
                Assert.IsTrue(window.rootVisualElement.Query<Label>().ToList().Any(label => label.text == "未知节点 · 10001"));
                Assert.AreEqual(before, JsonUtility.ToJson(document));
                Assert.IsFalse(GraphCompiler.Compile(document).Success);
            }
            finally
            {
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(document);
            }
        }
    }
}
