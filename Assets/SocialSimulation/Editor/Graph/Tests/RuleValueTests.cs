using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using SocialSimulation.Editor.Graph;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Graph.Tests
{
    public sealed class RuleValueTests
    {
        private static GraphDocument Rule(string type = "number", string comparison = "greater", string expected = "1.5", string unit = "m/s")
        {
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.DocumentId = "test.rule.typed";
            document.Kind = GraphKind.Rule;
            var trigger = new GraphNode { Id = "event", Type = GraphNodeType.RuleEvent }; trigger.Set("eventType", "actor.moved");
            var scope = new GraphNode { Id = "scope", Type = GraphNodeType.RuleScope };
            var condition = new GraphNode { Id = "condition", Type = GraphNodeType.RuleCondition };
            condition.Set("field", "speed"); condition.Set("valueType", type); condition.Set("comparison", comparison);
            condition.Set("equals", expected); condition.Set("unit", unit); condition.Set("requiresEvidence", "false");
            var effect = new GraphNode { Id = "effect", Type = GraphNodeType.RuleEffect }; effect.Set("commandId", "safety.remind");
            document.Nodes.AddRange(new[] { trigger, scope, condition, effect });
            foreach (var pair in new[] { new[] { "event", "scope" }, new[] { "scope", "condition" }, new[] { "condition", "effect" } })
                document.Edges.Add(new GraphEdge { FromNodeId = pair[0], ToNodeId = pair[1] });
            return document;
        }

        private static RuleContext Context(RuleFieldValue value = null)
        {
            var context = new RuleContext { EventId = "e-1", EventType = "actor.moved", ActorId = "actor-1", TimeSeconds = 120 };
            if (value != null) context.TypedAttributes.Add("speed", value);
            return context;
        }

        private static CompiledRulePlan Compile(GraphDocument document)
        {
            var compiled = GraphCompiler.Compile(document);
            Assert.IsTrue(compiled.Success, string.Join("\n", compiled.Diagnostics));
            return compiled.RulePlan;
        }

        [TestCase("equals", 1.5, true)]
        [TestCase("equals", 1.6, false)]
        [TestCase("notEqual", 1.5, false)]
        [TestCase("notEqual", 1.6, true)]
        [TestCase("greater", 1.5, false)]
        [TestCase("greater", 1.6, true)]
        [TestCase("greaterEqual", 1.5, true)]
        [TestCase("greaterEqual", 1.4, false)]
        [TestCase("less", 1.5, false)]
        [TestCase("less", 1.4, true)]
        [TestCase("lessEqual", 1.5, true)]
        [TestCase("lessEqual", 1.6, false)]
        public void NumberComparisonsRespectEqualityBoundaries(string comparison, double actual, bool expectedMatch)
        {
            var document = Rule(comparison: comparison);
            try
            {
                var result = RuleExecutor.Evaluate(Compile(document), Context(RuleFieldValue.Number(actual, "m/s")), publishTrace: false);
                Assert.AreEqual(expectedMatch ? 1 : 0, result.MatchCount);
                Assert.IsEmpty(result.Diagnostics);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [TestCase("number", "greater", "NaN", "m/s", "InvalidRuleExpectedValue")]
        [TestCase("number", "greater", "Infinity", "m/s", "InvalidRuleExpectedValue")]
        [TestCase("number", "greater", "1e9999", "m/s", "InvalidRuleExpectedValue")]
        [TestCase("number", "greater", "1,5", "m/s", "InvalidRuleExpectedValue")]
        [TestCase("bool", "equals", "1", "", "InvalidRuleExpectedValue")]
        [TestCase("bool", "greater", "true", "", "InvalidRuleComparison")]
        [TestCase("string", "less", "abc", "", "InvalidRuleComparison")]
        [TestCase("string", "equals", "abc", "seconds", "InvalidRuleUnit")]
        [TestCase("decimal", "equals", "1", "", "InvalidRuleValueType")]
        public void InvalidRuleSchemaCannotReplacePublishedPlan(string type, string comparison, string expected, string unit, string code)
        {
            var document = Rule();
            try
            {
                var slot = new GraphPlanSlot(); Assert.IsTrue(slot.TryApply(document).Success);
                var original = slot.RulePlan;
                var condition = document.FindNode("condition");
                condition.Set("valueType", type); condition.Set("comparison", comparison);
                condition.Set("equals", expected); condition.Set("unit", unit);
                var invalid = slot.TryApply(document);
                Assert.IsFalse(invalid.Success);
                Assert.IsTrue(invalid.Diagnostics.Any(item => item.Code == code));
                Assert.AreSame(original, slot.RulePlan);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [TestCase("NaN")]
        [TestCase("-Infinity")]
        [TestCase("1,75")]
        public void MalformedRuntimeNumberCannotBecomeTrueThroughNotOrOr(string raw)
        {
            var document = Rule();
            try
            {
                document.FindNode("condition").Set("negate", "true");
                var fallback = new GraphNode { Id = "fallback", Type = GraphNodeType.RuleCondition };
                fallback.Set("requiresEvidence", "false"); fallback.Set("combine", "or");
                document.Nodes.Add(fallback);
                document.Edges.Single(item => item.ToNodeId == "effect").ToNodeId = fallback.Id;
                document.Edges.Add(new GraphEdge { FromNodeId = fallback.Id, ToNodeId = "effect" });
                var result = RuleExecutor.Evaluate(Compile(document),
                    Context(new RuleFieldValue(RuleValueType.Number, raw, "m/s")), publishTrace: false);
                Assert.AreEqual(0, result.MatchCount);
                Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "InvalidRuleFieldValue"));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [TestCase("km/h")]
        [TestCase("")]
        public void UnitMismatchIsDiagnosedWithoutImplicitConversion(string actualUnit)
        {
            var document = Rule();
            try
            {
                var result = RuleExecutor.Evaluate(Compile(document), Context(RuleFieldValue.Number(60, actualUnit)), publishTrace: false);
                Assert.AreEqual(0, result.MatchCount);
                Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "RuleUnitMismatch"));
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void BareTextNumberMissingFieldAndDuplicateDeclarationAreRejected()
        {
            var document = Rule();
            try
            {
                var plan = Compile(document);
                var missing = RuleExecutor.Evaluate(plan, Context(), publishTrace: false);
                Assert.AreEqual("MissingRuleFieldValue", missing.Diagnostics.Single().Code);
                var text = Context(); text.Attributes.Add("speed", "2.0");
                Assert.AreEqual("RuleFieldTypeMismatch", RuleExecutor.Evaluate(plan, text, publishTrace: false).Diagnostics.Single().Code);
                text.TypedAttributes.Add("speed", RuleFieldValue.Number(2, "m/s"));
                var duplicate = RuleExecutor.Evaluate(plan, text, publishTrace: false);
                Assert.AreEqual(0, duplicate.MatchCount);
                Assert.AreEqual("AmbiguousRuleField", duplicate.Diagnostics.Single().Code);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void BooleanComparisonAndLegacyStringRuleRemainCompatible()
        {
            var document = Rule("bool", "notEqual", "FALSE", "");
            try
            {
                Assert.AreEqual(1, RuleExecutor.Evaluate(Compile(document), Context(RuleFieldValue.Boolean(true)), publishTrace: false).MatchCount);
                Assert.AreEqual(0, RuleExecutor.Evaluate(Compile(document), Context(RuleFieldValue.Boolean(false)), publishTrace: false).MatchCount);
                var node = document.FindNode("condition");
                node.Parameters.RemoveAll(item => item.Key == "valueType" || item.Key == "comparison" || item.Key == "unit");
                node.Set("field", "roleId"); node.Set("equals", "worker");
                var context = Context(); context.RoleId = "worker";
                Assert.AreEqual(1, RuleExecutor.Evaluate(Compile(document), context, publishTrace: false).MatchCount);
                context.RoleId = "Worker";
                Assert.AreEqual(0, RuleExecutor.Evaluate(Compile(document), context, publishTrace: false).MatchCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void NumberParsingUsesInvariantCultureAndBuiltInTimeDeclaresSeconds()
        {
            var document = Rule("number", "greaterEqual", "1.25", "seconds");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                document.FindNode("condition").Set("field", "timeSeconds");
                var context = Context(); context.TimeSeconds = 1.25;
                Assert.AreEqual(1, RuleExecutor.Evaluate(Compile(document), context, publishTrace: false).MatchCount);
                document.FindNode("condition").Set("unit", "minutes");
                Assert.AreEqual("RuleUnitMismatch", RuleExecutor.Evaluate(Compile(document), context, publishTrace: false).Diagnostics.Single().Code);
            }
            finally { CultureInfo.CurrentCulture = culture; UnityEngine.Object.DestroyImmediate(document); }
        }

        [Test]
        public void PreviewUsesTheSameTypedComparisonAndShowsUnitErrors()
        {
            var document = Rule();
            GraphWorkbenchWindow window = null;
            try
            {
                window = EditorWindow.CreateWindow<GraphWorkbenchWindow>();
                window.CreateGUI(); window.Open(document);
                var ui = window.rootVisualElement;
                ui.Q<TextField>("preview_actorId").value = "actor-1";
                ui.Q<TextField>("previewTypedName0").value = "speed";
                ui.Q<TextField>("previewTypedUnit0").value = "m/s";
                ui.Q<TextField>("previewTypedValue0").value = "1.75";
                window.PreviewRule();
                Assert.IsTrue(ui.Q<ScrollView>("rulePreviewResults").Query<Button>().ToList().Any(item => item.text.Contains("拟提交 1 条命令")));
                ui.Q<TextField>("previewTypedUnit0").value = "km/h";
                window.PreviewRule();
                var output = ui.Q<ScrollView>("rulePreviewResults").Query<Button>().ToList();
                Assert.IsTrue(output.Any(item => item.text.Contains("拟提交 0 条命令")));
                Assert.IsTrue(output.Any(item => item.text.Contains("RuleUnitMismatch")));
            }
            finally
            {
                if (window != null) window.Close();
                UnityEngine.Object.DestroyImmediate(document);
            }
        }
    }
}
