using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Tests
{
    public sealed class KnowledgeDecisionTests
    {
        private static SimulationWorld World()
        {
            var world = new SimulationWorld("personal.decisions", 77);
            world.Places.Add("square", new PlaceState { Id = "square", Capacity = 10 });
            foreach (var id in new[] { "a", "b", "c" }) world.AddAgent(new AgentState { Id = id, PlaceId = "square" });
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "talk", RequiresSamePlace = true,
                TransmissionConfidence = 0.9f, ConfidenceHalfLifeSeconds = 0 });
            return world;
        }

        private static GoalCandidate Purchase(UnknownKnowledgePolicy policy = UnknownKnowledgePolicy.AllowAttempt) =>
            new GoalCandidate { Id = "purchase", Need = 1, Urgent = true,
                KnowledgeConditions = new System.Collections.Generic.List<GoalKnowledgeCondition>
                { new GoalKnowledgeCondition { Topic = "service", SubjectId = "shop", ExpectedValue = "open", UnknownPolicy = policy } } };

        private static GoalCandidate Choose(SimulationWorld world, string actor, GoalCandidate purchase = null) =>
            world.Decisions.Select(actor, new[] { purchase ?? Purchase(), new GoalCandidate { Id = "rest" } });

        private static void Notice(SimulationWorld world, string actor, string id, string value,
            float confidence = 1, double retention = 100, bool protect = false, double? occurredAt = null) =>
            Assert.That(world.Knowledge.DeliverSystemNotice(actor, id, "service", "shop", value, "a", "talk",
                confidence, retention, 0, protect, occurredAt), Is.EqualTo(KnowledgeDelivery.Learned));

        [Test]
        public void DifferentNpcKnowledgeChangesGoalAndRelayChangesRecipientChoice()
        {
            var world = World();
            world.Emit("service", "a", "shop", "closed", "square");
            Assert.AreEqual("purchase", Choose(world, "b").Id, "Global events cannot enter an NPC's decision.");
            Notice(world, "a", "closure", "closed");
            Assert.AreEqual("rest", Choose(world, "a").Id);
            Assert.AreEqual("purchase", Choose(world, "b").Id);
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Relay("a", "b", "closure", "talk"));
            Assert.AreEqual("rest", Choose(world, "b").Id);
            var assessment = world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0];
            Assert.AreEqual(KnowledgeConditionState.Contradicted, assessment.State);
            Assert.AreEqual("a", assessment.Evidence.OriginSourceId);
            Assert.AreEqual("a", assessment.Evidence.LastSourceId);
            Assert.AreEqual("closure", assessment.Evidence.OriginEventId);
            Assert.That(assessment.CurrentConfidence, Is.EqualTo(0.9f).Within(0.0001));
            Assert.AreEqual("purchase", Choose(world, "c").Id);
        }

        [Test]
        public void UnknownPolicyIsExplicitAndCanRejectOrPenalizeWithoutInventingFalseFact()
        {
            var world = World();
            Assert.AreEqual("rest", Choose(world, "b", Purchase(UnknownKnowledgePolicy.Reject)).Id);
            Assert.AreEqual(KnowledgeConditionState.Unknown,
                world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].State);
            Choose(world, "b", Purchase());
            float unpenalized = world.Decisions.LastTrace["b"].Candidates[0].Score;
            Assert.AreEqual("purchase", Choose(world, "b", Purchase(UnknownKnowledgePolicy.Penalize)).Id);
            Assert.That(world.Decisions.LastTrace["b"].Candidates[0].Score, Is.EqualTo(unpenalized - 0.1f).Within(0.00001));
        }

        [Test]
        public void ExpiredProtectedNoticeBecomesUnknownForChoiceEvenWhenMemoryIsRetained()
        {
            var world = World(); Notice(world, "b", "closure", "closed", retention: 5, protect: true);
            Assert.AreEqual("rest", Choose(world, "b").Id);
            world.Advance(5);
            Assert.IsTrue(world.Knowledge.TryGet("b", "closure", out _), "Protected history is retained.");
            Assert.AreEqual("purchase", Choose(world, "b").Id);
            Assert.AreEqual(KnowledgeConditionState.Unknown,
                world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].State);
        }

        [Test]
        public void NewerOriginalReportWinsEvenWhenAnOlderClosureIsRelayedLater()
        {
            var world = World(); Notice(world, "a", "old.closed", "closed");
            world.Advance(5); Notice(world, "b", "new.open", "open");
            world.Advance(5); world.Knowledge.Relay("a", "b", "old.closed", "talk");
            Assert.AreEqual("purchase", Choose(world, "b").Id);
            Assert.AreEqual("new.open", world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].Evidence.OriginEventId);
        }

        [Test]
        public void WeakReportsAndWrongOriginalSourcesDoNotOverrideEligibleKnowledge()
        {
            var world = World(); Notice(world, "b", "old.open", "open");
            world.Advance(1); Notice(world, "b", "weak.closed", "closed", confidence: 0.4f);
            Assert.AreEqual("purchase", Choose(world, "b").Id);
            var goal = Purchase(UnknownKnowledgePolicy.Reject);
            goal.KnowledgeConditions[0].RequiredOriginSourceId = "c";
            Assert.AreEqual("rest", Choose(world, "b", goal).Id);
            Assert.AreEqual(KnowledgeConditionState.Unknown,
                world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].State);
        }

        [Test]
        public void EqualAgeAndConfidenceContradictionsUseUnknownPolicy()
        {
            var world = World(); Notice(world, "b", "one", "open"); Notice(world, "b", "two", "closed");
            Assert.AreEqual("purchase", Choose(world, "b").Id);
            Assert.AreEqual(KnowledgeConditionState.Conflicting,
                world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].State);
            Assert.AreEqual("rest", Choose(world, "b", Purchase(UnknownKnowledgePolicy.Reject)).Id);
        }

        [Test]
        public void MaximumAgeAndConfidenceDecayAreReevaluatedAtDecisionTime()
        {
            var world = World();
            world.Knowledge.DeliverSystemNotice("b", "decay", "service", "shop", "closed", "a", "talk",
                retentionSeconds: 100, halfLifeSeconds: 2, isProtected: false);
            Assert.AreEqual("rest", Choose(world, "b").Id);
            world.Advance(3);
            Assert.AreEqual("purchase", Choose(world, "b").Id);
            Notice(world, "c", "long.lived", "closed");
            var candidate = Purchase(); candidate.KnowledgeConditions[0].MaximumAgeSeconds = 1;
            world.Advance(2);
            Assert.AreEqual("purchase", Choose(world, "c", candidate).Id);
        }

        [Test]
        public void JsonWorldRestorePreservesKnowledgeProvenanceAndNextChoice()
        {
            var world = World(); Notice(world, "a", "closure", "closed");
            world.Knowledge.Relay("a", "b", "closure", "talk");
            Choose(world, "b"); world.Advance(3);
            var restored = WorldPersistence.Restore(JsonUtility.FromJson<SimulationSnapshot>(JsonUtility.ToJson(WorldPersistence.Capture(world))));
            Assert.AreEqual(Choose(world, "b").Id, Choose(restored, "b").Id);
            Assert.AreEqual("rest", restored.Decisions.LastTrace["b"].SelectedGoalId);
            var evidence = restored.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].Evidence;
            Assert.AreEqual("closure", evidence.OriginEventId); Assert.AreEqual(1, evidence.RelayDepth);
            Assert.AreEqual("purchase", Choose(restored, "c").Id);
            restored.Advance(100);
            Assert.AreEqual("purchase", Choose(restored, "b").Id);
        }

        [Test]
        public void ConditionsAndEvidenceInTracesAreOwnedCopiesAndInvalidConditionsReject()
        {
            var world = World(); Notice(world, "b", "closure", "closed");
            var candidate = Purchase(); Choose(world, "b", candidate);
            candidate.KnowledgeConditions[0].Topic = "mutated";
            var copied = world.Decisions.LastTrace["b"].Candidates[0].Copy();
            copied.KnowledgeAssessments[0].Evidence.Value = "mutated";
            Assert.AreEqual("service", world.Decisions.LastTrace["b"].Candidates[0].KnowledgeConditions[0].Topic);
            Assert.AreEqual("closed", world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].Evidence.Value);
            candidate.KnowledgeConditions[0].MinimumConfidence = float.NaN;
            Assert.AreEqual("rest", Choose(world, "b", candidate).Id);
            Assert.AreEqual(KnowledgeConditionState.Invalid,
                world.Decisions.LastTrace["b"].Candidates[0].KnowledgeAssessments[0].State);
        }
    }
}
