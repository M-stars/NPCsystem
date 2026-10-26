using NUnit.Framework;
using SocialSimulation.Core;
using SocialSimulation.Domains.Household;
using UnityEngine;

namespace SocialSimulation.Domains.Tests
{
    /// <summary>
    /// The second validation domain for event-triggered goal re-evaluation: family members act on
    /// the breakfast notice they personally received, never on objective world state.
    /// </summary>
    public sealed class HouseholdBreakfastJoinTests
    {
        private static SimulationWorld CreateWorld()
        {
            var world = new SimulationWorld("household.breakfast", 61) { TickSeconds = 5 };
            AddPlace(world, "home", "household.home");
            AddPlace(world, "park", "public.square");
            world.Society.Collectives.Add("family", new CollectiveState { Id = "family", Name = "family" });
            AddAgent(world, "parent", "home", "family");
            AddAgent(world, "carer", "home", "family");
            AddAgent(world, "child", "home", "family");
            AddAgent(world, "sibling", "home", "family");
            return world;
        }

        private static void AddPlace(SimulationWorld world, string id, string tag)
        {
            world.Places.Add(id, new PlaceState { Id = id, Name = id, Tag = tag, RegionInstanceId = id, Capacity = 10 });
        }

        private static void AddAgent(SimulationWorld world, string id, string place, params string[] collectives)
        {
            world.AddAgent(new AgentState { Id = id, DisplayName = id, TemplateId = "test.actor", PlaceId = place });
            foreach (var collective in collectives) world.Society.Join(id, collective);
        }

        private static HouseholdDomainModule Module(GameObject root, SimulationWorld world)
        {
            var module = root.AddComponent<HouseholdDomainModule>();
            module.Configure("home", "family", "parent", "carer", "child", "sibling");
            module.Initialize(null, world);
            return module;
        }

        private static ActivitySession Breakfast(SimulationWorld world)
        {
            foreach (var session in world.Activities.Sessions.Values)
                if (session.Kind == "household.breakfast") return session;
            throw new System.InvalidOperationException("Breakfast session is missing.");
        }

        private static string Status(SimulationWorld world, string actorId)
        {
            foreach (var person in Breakfast(world).Participants)
                if (person.AgentId == actorId) return person.Status;
            return "Absent";
        }

        private static void AdvanceTo(SimulationWorld world, double seconds)
        {
            while (world.Time < seconds) world.StepOne();
        }

        private static int CountTrace(SimulationWorld world, string type, string actorId, bool byEmitter = false)
        {
            var count = 0;
            foreach (var item in world.Trace)
                if (item.Type == type && (byEmitter ? item.EmitterId : item.SubjectId) == actorId) count++;
            return count;
        }

        [Test]
        public void AwayDependentLearnsTheBreakfastNoticeAndLateJoinsAfterArrivingHome()
        {
            var root = new GameObject("Breakfast knowledge join");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                world.Agents["sibling"].PlaceId = "park";
                AdvanceTo(world, 80);
                var breakfast = Breakfast(world);
                Assert.AreEqual("Active", breakfast.State);
                Assert.AreEqual("Accepted", Status(world, "sibling"),
                    "Being invited is not the same as taking part.");
                var reports = world.Knowledge.ForAgent("sibling").Query(HouseholdDomainModule.BreakfastReadyTopic, "home");
                Assert.AreEqual(1, reports.Count);
                Assert.AreEqual("serving", reports[0].Value);
                Assert.AreEqual("household.breakfast.join.home", world.Decisions.LastTrace["sibling"].SelectedGoalId);
                Assert.AreEqual(KnowledgeConditionState.Matched,
                    world.Decisions.LastTrace["sibling"].Candidates[0].KnowledgeAssessments[0].State);
                world.Agents["sibling"].PlaceId = "home";
                world.Emit("agent.arrived", "runtime", "sibling", "household.breakfast", "home");
                world.StepOne();
                Assert.AreEqual("Participating", Status(world, "sibling"));
                Assert.AreEqual(1, CountTrace(world, "household.breakfast.joined", "sibling", byEmitter: true));
                AdvanceTo(world, 260);
                Assert.IsTrue(module.CareCompleted);
                Assert.AreEqual("Completed", Status(world, "sibling"));
                Assert.AreEqual("Completed", Status(world, "child"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ArrivalWithoutPersonalKnowledgeCannotForceAJoin()
        {
            var root = new GameObject("Uninformed breakfast arrival");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                world.Society.Leave("sibling", "family");
                world.Agents["sibling"].PlaceId = "park";
                AdvanceTo(world, 80);
                Assert.AreEqual(0, world.Knowledge.ForAgent("sibling").Query(HouseholdDomainModule.BreakfastReadyTopic).Count,
                    "Only household members receive the family notice.");
                world.Agents["sibling"].PlaceId = "home";
                world.Emit("agent.arrived", "runtime", "sibling", "household.breakfast", "home");
                world.StepOne();
                Assert.AreEqual("Accepted", Status(world, "sibling"), "An uninformed arrival must not join.");
                Assert.AreEqual("household.idle.home", world.Decisions.LastTrace["sibling"].SelectedGoalId);
                Assert.IsTrue(world.Trace.Exists(item => item.Type == "household.breakfast.join_deferred" && item.SubjectId == "sibling"));
                AdvanceTo(world, 260);
                Assert.IsTrue(module.CareCompleted, "Care needs the caregiver and the first dependent only.");
                Assert.AreEqual("Expired", Status(world, "sibling"), "An uninformed sibling stays out of the breakfast.");
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
