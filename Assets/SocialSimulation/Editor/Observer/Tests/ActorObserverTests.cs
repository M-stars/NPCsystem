using System.Collections.Generic;
using NUnit.Framework;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Observer.Tests
{
    public sealed class ActorObserverTests
    {
        [Test]
        public void WindowOpensWithActorAndControlWidgets()
        {
            var window = ActorObserverWindow.ShowWindow();
            try
            {
                window.CreateGUI();
                Assert.IsNotNull(window.rootVisualElement.Q<VisualElement>("observer-root"));
                Assert.IsNotNull(window.rootVisualElement.Q<TextField>("actor-id"));
                Assert.IsNotNull(window.rootVisualElement.Q<Button>("pause"));
                Assert.IsNotNull(window.rootVisualElement.Q<Button>("step"));
                Assert.IsNotNull(window.rootVisualElement.Q<VisualElement>("decision-rows"));
                Assert.IsNotNull(window.rootVisualElement.Q<TextField>("knowledge-filter"));
                Assert.IsNotNull(window.rootVisualElement.Q<VisualElement>("knowledge-rows"));
                Assert.IsNotNull(window.rootVisualElement.Q<Label>("knowledge-summary"));
            }
            finally { window.Close(); }
        }

        [Test]
        public void BuilderMapsOneActorAcrossRolesDecisionsEvidenceAndResources()
        {
            var world = new SimulationWorld("observer-test");
            var actor = world.AddAgent(new AgentState
            { Id = "actor", DisplayName = "Observer Actor", PlaceId = "home" });
            world.AddAgent(new AgentState { Id = "other", PlaceId = "shop" });
            world.Society.Collectives.Add("family", new CollectiveState
            { Id = "family", Name = "Family" });
            world.Society.Collectives.Add("work", new CollectiveState
            { Id = "work", Name = "Workshop" });
            world.Society.Join("actor", "family");
            world.Society.Join("actor", "work");
            world.Society.AssignRole("actor", "family", "caregiver");
            world.Society.AssignRole("actor", "work", "worker");
            actor.CurrentGoalId = "buy-food";
            world.Decisions.LastTrace.Add("actor", new DecisionTrace
            {
                AgentId = "actor", SelectedGoalId = "buy-food",
                Candidates = new List<GoalCandidate>
                {
                    new GoalCandidate { Id = "buy-food", Score = 0.75f, Duty = 0.2f },
                    new GoalCandidate { Id = "go-work", RejectionReason = "Failure cooldown" }
                }
            });
            actor.KnownNormVersions.Add("home.quiet@2");
            world.Norms.Evidence.Add("evidence-1", new ObservationEvidence
            { Id = "evidence-1", ObserverId = "other", SubjectId = "actor",
                ActionId = "noise", Confidence = 0.8f });
            world.Norms.Violations.Add("violation-1", new ViolationRecord
            { Id = "violation-1", SubjectId = "actor", NormInstanceId = "norm-1",
                EvidenceIds = new List<string> { "evidence-1" } });
            world.Resources.Slots.Add("bed", new ResourceSlot
            { Id = "bed", PlaceId = "home", Capacity = 1 });
            Assert.IsNotNull(world.Resources.TryAcquire("bed", "care-scope", "actor", 30));
            world.Activities.Create("club", "actor", "home", 0, 10, 5, 1, 3);
            world.Emit("family.care", "actor", "other", "done", "home");
            var graph = new ActorRuntimeSnapshot("actor", "graph.family", 2,
                "revision-123", "instance", "care-node", "family.care", "action-1",
                ActionPhase.Moving);
            var trace = new GraphTraceEvent("graph.family", 2, "revision-123",
                "instance", "actor", "care-node", 0, GraphStatus.Running, null);

            var view = ActorObserverDataBuilder.Build(world, "actor",
                new[] { graph }, trace, "family");
            Assert.IsNotNull(view);
            StringAssert.Contains("caregiver", string.Join(" ", view.Identity));
            StringAssert.Contains("worker", string.Join(" ", view.Identity));
            StringAssert.Contains("Failure cooldown", string.Join(" ", view.Decisions));
            StringAssert.Contains("home.quiet@2", string.Join(" ", view.Norms));
            StringAssert.Contains("evidence-1", string.Join(" ", view.Norms));
            StringAssert.Contains("care-scope", string.Join(" ", view.ResourcesAndSessions));
            StringAssert.Contains("club", string.Join(" ", view.ResourcesAndSessions));
            StringAssert.Contains("family.care", string.Join(" ", view.Events));
            StringAssert.Contains("graph.family", string.Join(" ", view.Graphs));
            Assert.IsNull(ActorObserverDataBuilder.Build(world, "missing"));
        }
    }
}
