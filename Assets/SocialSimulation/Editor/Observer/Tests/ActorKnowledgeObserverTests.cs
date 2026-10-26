using System.Collections.Generic;
using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Observer.Tests
{
    public sealed class ActorKnowledgeObserverTests
    {
        private static SimulationWorld World()
        {
            var world = new SimulationWorld("observer.knowledge", 19);
            world.Places.Add("shop", new PlaceState { Id = "shop", Open = true, Capacity = 10 });
            world.AddAgent(new AgentState { Id = "a", PlaceId = "shop" });
            world.AddAgent(new AgentState { Id = "b", PlaceId = "shop" });
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "notice", TransmissionConfidence = 1 });
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "talk", RequiresSamePlace = true, TransmissionConfidence = 0.8f });
            return world;
        }

        private static void Tell(SimulationWorld world, string receiver, string id, string value = "false",
            double retention = 100, double halfLife = 0, bool protect = false)
        {
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.DeliverSystemNotice(receiver, id,
                "service.available", "shop", value, "a", "notice", 1, retention, halfLife, protect));
        }

        private static void Decide(SimulationWorld world, string actor)
        {
            world.Decisions.Select(actor, new[]
            {
                new GoalCandidate { Id = "visit", Need = 1, KnowledgeConditions = new List<GoalKnowledgeCondition>
                    { new GoalKnowledgeCondition { Topic = "service.available", SubjectId = "shop", ExpectedValue = "true" } } },
                new GoalCandidate { Id = "rest" }
            });
        }

        [Test]
        public void PersonalKnowledgeDoesNotLeakOtherActorsOrUnreceivedWorldFacts()
        {
            var world = World();
            world.Emit("private.event", "a", "shop", "unreceived-secret", "shop");
            Tell(world, "a", "a.statement");
            var a = ActorObserverDataBuilder.Build(world, "a");
            var b = ActorObserverDataBuilder.Build(world, "b");
            Assert.AreEqual(1, a.KnowledgeTotalCount); Assert.AreEqual(0, b.KnowledgeTotalCount);
            StringAssert.Contains("「false」", string.Join("\n", a.Knowledge));
            StringAssert.DoesNotContain("unreceived-secret", string.Join("\n", a.Knowledge));
            StringAssert.DoesNotContain("a.statement", string.Join("\n", b.Knowledge));
            Assert.IsTrue(world.Places["shop"].Open, "Showing a false statement must not replace objective state.");
        }

        [Test]
        public void HistoricDecisionConfidenceAndCurrentRelayedKnowledgeStayDistinct()
        {
            var world = World(); Tell(world, "a", "original.statement", halfLife: 10);
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Relay("a", "b", "original.statement", "talk"));
            Decide(world, "b");
            world.Advance(10);
            var view = ActorObserverDataBuilder.Build(world, "b");
            var personal = string.Join("\n", view.Knowledge);
            var decision = string.Join("\n", view.Decisions);
            StringAssert.Contains("当前置信度 0.40", personal);
            StringAssert.Contains("决策时置信度 0.80 @ 0.00 s", decision);
            StringAssert.Contains("原始 a / 最后传达 a", personal);
            StringAssert.Contains("notice → talk", personal);
            StringAssert.Contains("original.statement", decision);
            StringAssert.Contains("个人陈述与目标矛盾", decision);
            Assert.That(world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].CurrentConfidence,
                Is.EqualTo(0.8f).Within(0.0001));
        }

        [Test]
        public void ExpiredProtectedHistoryAndUnknownDecisionAreExplicitAfterJsonRestore()
        {
            var world = World();
            Tell(world, "b", "keep.history", retention: 5, protect: true);
            Tell(world, "b", "ordinary", "true", retention: 5);
            world.Advance(6);
            var restored = WorldPersistence.Restore(JsonUtility.FromJson<SimulationSnapshot>(JsonUtility.ToJson(WorldPersistence.Capture(world))));
            var beforeDecision = ActorObserverDataBuilder.Build(restored, "b");
            Assert.AreEqual(1, beforeDecision.KnowledgeTotalCount);
            StringAssert.Contains("keep.history", string.Join("\n", beforeDecision.Knowledge));
            StringAssert.Contains("已过有效期（保留历史）", string.Join("\n", beforeDecision.Knowledge));
            StringAssert.DoesNotContain("ordinary", string.Join("\n", beforeDecision.Knowledge));
            StringAssert.Contains("读档后需等待下一次决策", string.Join("\n", beforeDecision.Decisions));
            Decide(restored, "b");
            var decisions = string.Join("\n", ActorObserverDataBuilder.Build(restored, "b").Decisions);
            StringAssert.Contains("未知或无合格个人信息", decisions);
            StringAssert.Contains("未知策略：允许尝试", decisions);
            StringAssert.Contains("无合格个人陈述", decisions);
        }

        [Test]
        public void FilteringRunsBeforeDisplayLimitAndReportsMatchingAndTotalCounts()
        {
            var world = World(); world.Knowledge.CapacityPerAgent = 128;
            for (int i = 0; i < 72; i++) Tell(world, "b", "record." + i.ToString("D3"), i == 71 ? "unique-target" : "bulk");
            var all = ActorObserverDataBuilder.Build(world, "b");
            Assert.AreEqual(72, all.KnowledgeTotalCount); Assert.AreEqual(72, all.KnowledgeMatchedCount);
            Assert.AreEqual(64, all.KnowledgeDisplayedCount);
            StringAssert.DoesNotContain("unique-target", string.Join("\n", all.Knowledge));
            var filtered = ActorObserverDataBuilder.Build(world, "b", knowledgeFilter: " UNIQUE-TARGET ");
            Assert.AreEqual(72, filtered.KnowledgeTotalCount); Assert.AreEqual(1, filtered.KnowledgeMatchedCount);
            Assert.AreEqual(1, filtered.KnowledgeDisplayedCount);
            StringAssert.Contains("unique-target", string.Join("\n", filtered.Knowledge));
            var missing = ActorObserverDataBuilder.Build(world, "b", knowledgeFilter: "missing");
            Assert.AreEqual(72, missing.KnowledgeTotalCount); Assert.AreEqual(0, missing.KnowledgeMatchedCount);
            StringAssert.Contains("无匹配", string.Join("\n", missing.Knowledge));
        }

        [Test]
        public void ObservingAndEditingTheViewDoesNotMutateSimulationOrDecisionEvidence()
        {
            var world = World(); Tell(world, "b", "stable.statement"); Decide(world, "b");
            var before = JsonUtility.ToJson(WorldPersistence.Capture(world));
            var trace = JsonUtility.ToJson(world.Decisions.LastTrace["b"]);
            var view = ActorObserverDataBuilder.Build(world, "b");
            view.Knowledge.Clear(); view.Decisions.Add("edited view only");
            ActorObserverDataBuilder.Build(world, "b", knowledgeFilter: "notice");
            Assert.AreEqual(before, JsonUtility.ToJson(WorldPersistence.Capture(world)));
            Assert.AreEqual(trace, JsonUtility.ToJson(world.Decisions.LastTrace["b"]));
            Assert.AreEqual(1, world.Knowledge.ForAgent("b").Query().Count);
        }

        [Test]
        public void WindowBindsKnowledgeAndDecisionRowsAndClearsThemWhenActorChanges()
        {
            var world = World(); Tell(world, "a", "only-a", "<b>false</b>"); Decide(world, "a");
            var window = ActorObserverWindow.ShowWindow();
            try
            {
                window.CreateGUI();
                window.BindActorView(ActorObserverDataBuilder.Build(world, "a"));
                var labels = window.rootVisualElement.Q<VisualElement>("knowledge-rows").Query<Label>().ToList();
                Assert.IsTrue(labels.Exists(label => label.text.Contains("<b>false</b>")));
                Assert.IsTrue(labels.TrueForAll(label => !label.enableRichText), "Statements must render literally.");
                StringAssert.Contains("匹配 1 / 个人记录总数 1", window.rootVisualElement.Q<Label>("knowledge-summary").text);
                var decisions = window.rootVisualElement.Q<VisualElement>("decision-rows").Query<Label>().ToList();
                Assert.IsTrue(decisions.Exists(label => label.text.Contains("决策时置信度")));
                window.BindActorView(ActorObserverDataBuilder.Build(world, "b"));
                labels = window.rootVisualElement.Q<VisualElement>("knowledge-rows").Query<Label>().ToList();
                Assert.IsFalse(labels.Exists(label => label.text.Contains("only-a")));
                StringAssert.Contains("个人记录总数 0", window.rootVisualElement.Q<Label>("knowledge-summary").text);
                window.BindActorView(null);
                Assert.AreEqual(0, window.rootVisualElement.Q<VisualElement>("knowledge-rows").childCount);
                Assert.AreEqual(0, window.rootVisualElement.Q<VisualElement>("decision-rows").childCount);
                Assert.AreEqual("", window.rootVisualElement.Q<Label>("knowledge-summary").text);
            }
            finally { window.Close(); }
        }
    }
}
