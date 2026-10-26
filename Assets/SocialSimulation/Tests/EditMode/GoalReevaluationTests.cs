using System;
using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Tests
{
    public sealed class GoalReevaluationTests
    {
        private static SimulationWorld World()
        {
            var world = new SimulationWorld("goal.reevaluation", 91);
            world.Places.Add("square", new PlaceState { Id = "square", Capacity = 10 });
            world.Places.Add("shop", new PlaceState { Id = "shop", Capacity = 10 });
            foreach (var id in new[] { "a", "b" }) world.AddAgent(new AgentState { Id = id, PlaceId = "square" });
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "talk", TransmissionConfidence = 0.9f });
            return world;
        }

        private static void Notice(SimulationWorld world, string actor, string id, string value,
            string topic = "service", string subject = "shop", double retention = 86400) =>
            Assert.That(world.Knowledge.DeliverSystemNotice(actor, id, topic, subject, value, "a", "talk",
                retentionSeconds: retention), Is.EqualTo(KnowledgeDelivery.Learned));

        [Test]
        public void KnowledgeChangeRunsTheRegisteredSelectorOnTheNextTickExactlyOnce()
        {
            var world = World();
            var runs = 0;
            world.GoalReevaluation.Register("b", "service", "shop", () => runs++);
            Notice(world, "b", "n1", "closed");
            Notice(world, "b", "n2", "open");
            Assert.AreEqual(0, runs, "The selector must not run inside the knowledge change.");
            Assert.IsFalse(world.GoalReevaluation.IsPending("b", "service", "shop"),
                "Knowledge events are only dispatched on a simulation tick.");
            world.StepOne();
            Assert.AreEqual(0, runs, "The deduplicated event waits for the next tick.");
            Assert.IsTrue(world.GoalReevaluation.IsPending("b", "service", "shop"));
            world.StepOne();
            Assert.AreEqual(1, runs);
            Assert.IsFalse(world.GoalReevaluation.IsPending("b", "service", "shop"));
            world.Advance(30);
            Assert.AreEqual(1, runs, "Re-evaluation must terminate without further knowledge changes.");
        }

        [Test]
        public void RulesMatchAgentTopicAndSubjectWithoutLeakingAcrossRegistrations()
        {
            var world = World();
            var shopRuns = 0;
            var otherRuns = 0;
            var otherTopicRuns = 0;
            var agentRuns = 0;
            world.GoalReevaluation.Register("b", "service", "shop", () => shopRuns++);
            world.GoalReevaluation.Register("b", "other", "shop", () => otherRuns++);
            world.GoalReevaluation.Register("b", "service", "square", () => otherRuns++);
            world.GoalReevaluation.Register("a", "service", "shop", () => agentRuns++);
            Notice(world, "b", "n1", "closed");
            world.StepOne(); world.StepOne();
            Assert.AreEqual(1, shopRuns);
            Assert.AreEqual(0, otherRuns, "A different subject or topic must not fire.");
            Assert.AreEqual(0, agentRuns, "Another agent's knowledge change must not fire.");
            Notice(world, "b", "n2", "crowded", topic: "other", subject: "shop");
            world.StepOne(); world.StepOne();
            Assert.AreEqual(1, otherRuns);
            Assert.AreEqual(1, shopRuns);
            // A null subject matches every subject of the topic.
            var wildcardRuns = 0;
            world.GoalReevaluation.Register("b", "service", null, () => wildcardRuns++);
            Notice(world, "b", "n3", "closed", subject: "square");
            world.StepOne(); world.StepOne();
            Assert.AreEqual(1, wildcardRuns);
            Assert.AreEqual(1, shopRuns, "The concrete rule must not fire again for another subject.");
        }

        [Test]
        public void UnregisteredReplacedAndInvalidRulesBehaveExplicitly()
        {
            var world = World();
            var runs = 0;
            Action handler = () => runs++;
            world.GoalReevaluation.Register("b", "service", "shop", handler);
            Assert.AreEqual(1, world.GoalReevaluation.RuleCount);
            world.GoalReevaluation.Register("b", "service", "shop", handler);
            Assert.AreEqual(1, world.GoalReevaluation.RuleCount, "Registering the same key replaces the rule.");
            Assert.IsTrue(world.GoalReevaluation.Unregister("b", "service", "shop"));
            Assert.IsFalse(world.GoalReevaluation.Unregister("b", "service", "shop"));
            Notice(world, "b", "n1", "closed");
            world.StepOne(); world.StepOne();
            Assert.AreEqual(0, runs, "An unregistered rule must not fire on a pending knowledge change.");
            Assert.Throws<ArgumentException>(() => world.GoalReevaluation.Register("missing", "service", "shop", handler));
            Assert.Throws<ArgumentException>(() => world.GoalReevaluation.Register("b", " ", "shop", handler));
            Assert.Throws<ArgumentException>(() => world.GoalReevaluation.Register("b", "service", "shop", null));
            Assert.Throws<ArgumentException>(() => world.GoalReevaluation.Register("b", "ser|vice", "shop", handler));
            // A knowledge event without a resolvable personal record schedules nothing.
            world.Emit("knowledge.learned", "a", "b", "unknown.origin");
            world.Emit("goal.reevaluation.due", GoalReevaluationService.ServiceEmitterId, "b", "b|service|shop");
            world.StepOne();
            Assert.AreEqual(0, runs, "A due event for a missing rule must be ignored.");
        }

        [Test]
        public void ExpiringKnowledgeTriggersReevaluationSoWaitingAgentsRetryAlone()
        {
            var world = World();
            var runs = 0;
            world.GoalReevaluation.Register("b", "service", "shop", () => runs++);
            Assert.That(world.Knowledge.DeliverSystemNotice("b", "n1", "service", "shop", "closed", "a", "talk",
                retentionSeconds: 2, isProtected: false), Is.EqualTo(KnowledgeDelivery.Learned));
            world.StepOne();
            world.StepOne();
            Assert.AreEqual(1, runs);
            world.StepOne();
            Assert.AreEqual(2, runs, "Forgetting the notice is itself a knowledge change.");
            Assert.IsFalse(world.Knowledge.TryGet("b", "n1", out _));
            world.Advance(30);
            Assert.AreEqual(2, runs, "The retry loop terminates until new information arrives.");
        }

        [Test]
        public void PendingReevaluationSurvivesWorldRestoreAndSkipsDepartedAgents()
        {
            var world = World();
            world.GoalReevaluation.Register("b", "service", "shop", () => { });
            Notice(world, "b", "n1", "closed");
            world.StepOne();
            var restored = WorldPersistence.Restore(
                JsonUtility.FromJson<SimulationSnapshot>(JsonUtility.ToJson(WorldPersistence.Capture(world))));
            Assert.AreEqual(0, restored.GoalReevaluation.RuleCount, "Rules belong to domain modules, not the snapshot.");
            Assert.IsTrue(restored.GoalReevaluation.IsPending("b", "service", "shop"),
                "The deduplicated event survives the snapshot.");
            var restoredRuns = 0;
            restored.GoalReevaluation.Register("b", "service", "shop", () => restoredRuns++);
            restored.StepOne();
            Assert.AreEqual(1, restoredRuns, "The restored world re-runs the re-registered rule once.");
            // Drain the pending original event before the departure case, then a rule whose agent
            // leaves before its event fires is skipped, not thrown.
            world.StepOne();
            var departedRuns = 0;
            world.GoalReevaluation.Register("b", "service", "shop", () => departedRuns++);
            Notice(world, "b", "n2", "open");
            world.StepOne();
            world.RemoveAgent("b");
            world.StepOne();
            Assert.AreEqual(0, departedRuns);
        }
    }
}
