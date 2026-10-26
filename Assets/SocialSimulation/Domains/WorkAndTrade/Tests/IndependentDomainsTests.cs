using NUnit.Framework;
using SocialSimulation.Core;
using SocialSimulation.Domains.Household;
using SocialSimulation.Domains.DomainActions;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEditor;
using UnityEngine;

namespace SocialSimulation.Domains.WorkAndTrade.Tests
{
    public sealed class IndependentDomainsTests
    {
        [Test]
        public void SameActorCompletesCareWagePurchaseAndClubWithoutCampus()
        {
            var root = new GameObject("Household and Work only");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                work.Configure("shop", "store", "square", "club", "person.care_worker",
                    "coworker", "club.member");
                var world = CreateWorld();
                household.Initialize(null, world);
                work.Initialize(null, world);

                AdvanceTo(world, 70);
                Assert.IsFalse(household.CareCompleted, "Inviting someone is not completed care.");
                Assert.AreEqual(1, household.ActiveGraphCount);
                AdvanceTo(world, 250);
                Assert.IsTrue(household.CareCompleted, "Household care should complete after the actual breakfast.");
                Assert.IsFalse(work.WagePaid);
                world.Agents["person.care_worker"].PlaceId = "shop";
                AdvanceTo(world, 3910);
                Assert.IsTrue(work.WagePaid, "Actual shift participation should pay wages.");
                Assert.AreEqual(6, world.Economy.Balance("person.care_worker", "money.credit"));
                AdvanceTo(world, 3980);
                Assert.IsTrue(work.ClerkPurchaseCompleted, "The worker should spend earned money.");
                Assert.AreEqual(3, world.Economy.Balance("person.care_worker", "money.credit"));
                Assert.AreEqual(1, world.Economy.Balance("person.care_worker", "food.snack"));

                world.Agents["person.care_worker"].PlaceId = "square";
                AdvanceTo(world, 5710);
                Assert.IsTrue(work.ClubCompleted, "The same person should attend the community activity.");
                Assert.AreEqual(0, world.Diagnostics.Count);
                Assert.AreEqual(4, CountCompletedDuties(world, "person.care_worker"));
                Assert.IsFalse(world.Places.ContainsKey("campus.classroom.a"));
                Assert.AreEqual(0, household.ActiveGraphCount + work.ActiveGraphCount);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ActiveCareShiftPurchaseAndClubSurviveLoadWithoutDuplicateEffects()
        {
            var root = new GameObject("Domain save and restore");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member");
                var world = CreateWorld();
                household.Initialize(null, world); work.Initialize(null, world);
                AdvanceTo(world, 100);
                Assert.AreEqual(1, household.ActiveGraphCount);
                world = Restore(world, household, work);
                AdvanceTo(world, 250);
                Assert.IsTrue(household.CareCompleted);
                world.Agents["person.care_worker"].PlaceId = "shop";
                AdvanceTo(world, 1800);
                Assert.AreEqual(1, work.ActiveGraphCount);
                world = Restore(world, household, work);
                AdvanceTo(world, 3970);
                Assert.IsTrue(work.WagePaid);
                Assert.IsFalse(work.ClerkPurchaseCompleted);
                world = Restore(world, household, work);
                AdvanceTo(world, 3990);
                Assert.IsTrue(work.ClerkPurchaseCompleted);
                world = Restore(world, household, work);
                Assert.AreEqual(3, world.Economy.Balance("person.care_worker", "money.credit"));
                Assert.AreEqual(1, world.Economy.Balance("person.care_worker", "food.snack"));
                world.Agents["person.care_worker"].PlaceId = "square";
                AdvanceTo(world, 5200);
                world = Restore(world, household, work);
                AdvanceTo(world, 5800);
                Assert.IsTrue(work.ClubCompleted);
                Assert.AreEqual(2, world.Economy.CopyTransactionIds().Count, "One wage and one purchase, even after four loads.");
                Assert.AreEqual(4, CountCompletedDuties(world, "person.care_worker"));
                Assert.AreEqual(0, world.Diagnostics.Count);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ForgedArrivalCannotDeliverGoodsOrPayWages()
        {
            var root = new GameObject("Arrival ownership");
            try
            {
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member", "child");
                var world = CreateWorld(); work.Initialize(null, world);
                world.Emit("agent.arrived", "intruder", "child", "purchase.snack", "shop");
                world.Emit("agent.arrived", "intruder", "person.care_worker", "clerk.purchase", "shop");
                world.StepOne();
                Assert.IsFalse(work.StudentPurchaseCompleted);
                Assert.IsFalse(work.ClerkPurchaseCompleted);
                Assert.IsFalse(work.WagePaid);
                Assert.AreEqual(0, world.Economy.Balance("child", "food.snack"));
                Assert.AreEqual(0, world.Economy.CopyTransactionIds().Count);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void LeavingDuringShiftCancelsBusinessGraphAndDoesNotPay()
        {
            var root = new GameObject("Interrupted shift");
            try
            {
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member");
                var world = CreateWorld(); work.Initialize(null, world);
                world.Agents["person.care_worker"].PlaceId = "shop";
                AdvanceTo(world, 1800);
                Assert.AreEqual(1, work.ActiveGraphCount);
                world.Agents["person.care_worker"].PlaceId = "home";
                world.StepOne();
                Assert.AreEqual(0, work.ActiveGraphCount);
                AdvanceTo(world, 4000);
                Assert.IsFalse(work.WagePaid);
                Assert.IsFalse(work.ClerkPurchaseCompleted);
                Assert.AreEqual(0, world.Economy.Balance("person.care_worker", "money.credit"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void LoadingAgainstChangedModuleBindingsIsRejected()
        {
            var root = new GameObject("Binding validation");
            try
            {
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member");
                var world = CreateWorld(); work.Initialize(null, world);
                var saved = work.CaptureScenarioState();
                work.Configure("shop", "store", "square", "club", "coworker", "person.care_worker", "club.member");
                Assert.Throws<System.InvalidOperationException>(() => work.ValidateScenarioState(null, world, saved));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static SimulationWorld Restore(SimulationWorld world, HouseholdDomainModule household, WorkAndTradeDomainModule work)
        {
            var snapshot = WorldPersistence.Capture(world);
            var care = household.CaptureScenarioState();
            var trade = work.CaptureScenarioState();
            var restored = WorldPersistence.Restore(snapshot);
            household.ValidateScenarioState(null, restored, care);
            work.ValidateScenarioState(null, restored, trade);
            household.RestoreScenarioState(null, restored, care);
            work.RestoreScenarioState(null, restored, trade);
            return restored;
        }

        [Test]
        public void WorkbenchGraphVersionsCanBeAppliedAndRestartedWithoutRepeatingBusinessEffects()
        {
            var root = new GameObject("Live graph versions");
            var draft = DomainGraphRunner.CreateDefault("behavior.trade.shift", "trade.shift", "trade.pay_wage");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member");
                var world = CreateWorld(); household.Initialize(null, world); work.Initialize(null, world);
                AdvanceTo(world, 250); world.Agents["person.care_worker"].PlaceId = "shop";
                AdvanceTo(world, 1800);
                Assert.IsTrue(work.TryGetGraphState(draft.DocumentId, GraphKind.Behavior, out var before));
                draft.IncrementVersion();
                Assert.IsFalse(work.TryApplyGraph(draft, GraphApplyMode.RestartAffected, out _), "Restart requires a paused world.");
                Assert.IsTrue(work.TryApplyGraph(draft, GraphApplyMode.ForNewInstances, out var error), error);
                Assert.IsTrue(work.TryGetGraphState(draft.DocumentId, GraphKind.Behavior, out var staged));
                Assert.AreEqual(draft.Version, staged.Version);
                StringAssert.Contains("@" + before.Version + "#", staged.ActiveVersions[0]);
                world = Restore(world, household, work);
                Assert.IsTrue(work.TryGetGraphState(draft.DocumentId, GraphKind.Behavior, out var afterLoad));
                Assert.AreEqual(staged.Revision, afterLoad.Revision);
                CollectionAssert.AreEqual(staged.ActiveVersions, afterLoad.ActiveVersions);
                world.Paused = true;
                var sessionCount = world.Activities.Sessions.Count;
                Assert.IsTrue(work.TryApplyGraph(draft, GraphApplyMode.RestartAffected, out error), error);
                Assert.IsTrue(work.TryGetGraphState(draft.DocumentId, GraphKind.Behavior, out var restarted));
                StringAssert.Contains("@" + draft.Version + "#", restarted.ActiveVersions[0]);
                AdvanceTo(world, 3910);
                Assert.IsTrue(work.WagePaid, "Restarting the graph must preserve the accepted duty and ongoing shift.");
                Assert.AreEqual(sessionCount, world.Activities.Sessions.Count);
                Assert.AreEqual(6, world.Economy.Balance("person.care_worker", "money.credit"));
                Assert.AreEqual(1, world.Economy.CopyTransactionIds().Count);
                draft.IncrementVersion();
                work.ConfigureGraphs(draft, null, null);
                work.Initialize(null, CreateWorld());
                Assert.IsTrue(work.TryGetGraphState(draft.DocumentId, GraphKind.Behavior, out var newWorldGraph));
                Assert.AreEqual(draft.Version, newWorldGraph.Version, "A new world must compile the newly configured asset.");
            }
            finally { Object.DestroyImmediate(draft); Object.DestroyImmediate(root); }
        }

        [Test]
        public void RequiredBusinessActionsCannotBeDisconnectedOrSkippedBySelector()
        {
            var root = new GameObject("Required business graph paths");
            var draft = DomainGraphRunner.CreateDefault("behavior.household.care", "household.care");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                var world = CreateWorld(); household.Initialize(null, world);
                AdvanceTo(world, 100);
                Assert.IsTrue(household.TryGetGraphState(draft.DocumentId, GraphKind.Behavior, out var original));
                var wait = new GraphNode { Id = "wait", Type = GraphNodeType.Wait };
                wait.Set("ticks", "1"); draft.Nodes.Add(wait);
                draft.Edges.Clear();
                draft.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "wait" });
                Assert.IsFalse(household.TryApplyGraph(draft, GraphApplyMode.ForNewInstances, out _),
                    "An unreachable required action must not count as part of the graph.");
                draft.Nodes.Add(new GraphNode { Id = "choice", Type = GraphNodeType.Selector });
                draft.Edges.Clear();
                draft.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "choice" });
                draft.Edges.Add(new GraphEdge { FromNodeId = "choice", ToNodeId = "wait", Order = 0 });
                draft.Edges.Add(new GraphEdge { FromNodeId = "choice", ToNodeId = "action.0", Order = 1 });
                Assert.IsFalse(household.TryApplyGraph(draft, GraphApplyMode.ForNewInstances, out _),
                    "A successful selector branch must not be allowed to bypass the required business action.");
                Assert.IsTrue(household.TryGetGraphState(draft.DocumentId, GraphKind.Behavior, out var unchanged));
                Assert.AreEqual(original.Revision, unchanged.Revision);
                AdvanceTo(world, 250);
                Assert.IsTrue(household.CareCompleted, "Rejected drafts must leave the active graph running.");
            }
            finally { Object.DestroyImmediate(draft); Object.DestroyImmediate(root); }
        }

        [Test]
        public void DefaultNavigationPointsExistInTheRegisteredSampleRegionPrefabs()
        {
            var root = new GameObject("Domain navigation bindings");
            try
            {
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                const string packages = "Assets/SocialSimulation/Domains/WorkAndTrade/Packages/";
                var shop = AssetDatabase.LoadAssetAtPath<GameObject>(packages + "Shop/Prefab/Region.prefab");
                var square = AssetDatabase.LoadAssetAtPath<GameObject>(packages + "Square/Prefab/Region.prefab");
                Assert.IsNotNull(shop); Assert.IsNotNull(square);
                AssertPoint(shop, work.ShiftPointId);
                AssertPoint(shop, work.PurchasePointId);
                AssertPoint(square, work.ClubPointId);
                AssertPoint(square, work.ClubMemberPointId);
                work.ConfigureNavigation(null, null, null, null);
                Assert.IsNull(work.ClubMemberPointId, "Custom domains can explicitly choose a region origin instead of a named point.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void LongPurchaseTravelHasABoundedConfigurableSimulationTimeBudget()
        {
            var root = new GameObject("Purchase travel budget");
            try
            {
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member", "child");
                Assert.Throws<System.ArgumentOutOfRangeException>(() => work.ConfigurePurchaseTimeout(float.PositiveInfinity));
                Assert.Throws<System.ArgumentOutOfRangeException>(() => work.ConfigurePurchaseTimeout(0));
                var world = CreateWorld(); work.Initialize(null, world);
                AdvanceTo(world, 4300); // Travel starts at 3600. The previous 600-second budget ended at 4205.
                Assert.IsFalse(work.StudentPurchaseCompleted);
                world.Agents["child"].PlaceId = "shop";
                AdvanceTo(world, 4320);
                Assert.IsTrue(work.StudentPurchaseCompleted);
                Assert.AreEqual(1, world.Economy.Balance("child", "food.snack"));
                Assert.AreEqual(8, world.Economy.Balance("child", "money.credit"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ClosedHomeCannotStartCareEvenWhenTheCaregiverIsAlreadyInside()
        {
            var root = new GameObject("Closed household");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                var world = CreateWorld(); household.Initialize(null, world);
                world.Places["home"].Open = false;
                AdvanceTo(world, 250);
                Assert.IsFalse(household.CareCompleted);
                Assert.AreEqual(0, household.ActiveGraphCount);
                Assert.AreEqual(0, world.Activities.Sessions.Count, "Same-region movement must not bypass access checks.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PausedHouseholdRegionCancellationCanBeSavedBeforeAnotherTick(bool unloaded)
        {
            var root = new GameObject("Paused household region lifecycle");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member");
                var world = CreateWorld(); household.Initialize(null, world); work.Initialize(null, world);
                AdvanceTo(world, 100);
                Assert.AreEqual(1, household.ActiveGraphCount);
                world.Paused = true;
                var tick = world.TickIndex;
                world.Places["home"].Open = false;
                ((ISocialRegionLifecycle)household).OnRegionUnavailable("home", unloaded);
                ((ISocialRegionLifecycle)work).OnRegionUnavailable("home", unloaded);
                Assert.AreEqual(0, household.ActiveGraphCount, "Cancellation cannot wait for the next simulation tick.");
                Assert.AreEqual("Cancelled", Duty(world, "household.care").State);
                foreach (var session in world.Activities.Sessions.Values) Assert.AreEqual("Cancelled", session.State);
                world = Restore(world, household, work);
                Assert.AreEqual(tick, world.TickIndex);
                Assert.AreEqual(0, household.ActiveGraphCount);
                AdvanceTo(world, 250);
                Assert.IsFalse(household.CareCompleted);
                Assert.AreEqual(0, world.Economy.CopyTransactionIds().Count);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase("shop", 1800, false)]
        [TestCase("shop", 3970, true)]
        [TestCase("square", 5200, true)]
        public void PausedWorkRegionCancellationRestoresWithoutRepeatingBusinessEffects(string regionId, double stopAt, bool unloaded)
        {
            var root = new GameObject("Paused work region lifecycle");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member");
                var world = CreateWorld(); household.Initialize(null, world); work.Initialize(null, world);
                AdvanceTo(world, 250);
                world.Agents["person.care_worker"].PlaceId = "shop";
                AdvanceTo(world, System.Math.Min(stopAt, 4000));
                if (stopAt > 4000)
                {
                    world.Agents["person.care_worker"].PlaceId = "square";
                    AdvanceTo(world, stopAt);
                }
                Assert.AreEqual(1, work.ActiveGraphCount);
                world.Paused = true;
                var tick = world.TickIndex;
                var transactionCount = world.Economy.CopyTransactionIds().Count;
                var money = world.Economy.Balance("person.care_worker", "money.credit");
                var goods = world.Economy.Balance("person.care_worker", "food.snack");
                world.Places[regionId].Open = false;
                ((ISocialRegionLifecycle)work).OnRegionUnavailable(regionId, unloaded);
                ((ISocialRegionLifecycle)household).OnRegionUnavailable(regionId, unloaded);
                Assert.AreEqual(0, work.ActiveGraphCount);
                var action = regionId == "square" ? "community.walking_club.meetup" :
                    stopAt < 3900 ? "trade.shift" : "trade.purchase";
                Assert.AreEqual("Cancelled", Duty(world, action).State);
                world = Restore(world, household, work);
                Assert.AreEqual(tick, world.TickIndex);
                Assert.AreEqual(0, work.ActiveGraphCount);
                if (regionId == "shop") world.Agents["person.care_worker"].PlaceId = "square";
                AdvanceTo(world, 5800);
                Assert.AreEqual(transactionCount, world.Economy.CopyTransactionIds().Count);
                Assert.AreEqual(money, world.Economy.Balance("person.care_worker", "money.credit"));
                Assert.AreEqual(goods, world.Economy.Balance("person.care_worker", "food.snack"));
                Assert.IsTrue(household.CareCompleted, "A different region closing must retain completed care.");
                Assert.AreEqual(regionId == "shop", work.ClubCompleted, "The unrelated community region can still complete its future duty.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegionCancellationFindsActionsBehindWaitNodesAndPreservesUnrelatedGraphs()
        {
            var root = new GameObject("Waiting graph region lifecycle");
            var draft = DomainGraphRunner.CreateDefault("behavior.household.care", "household.care");
            try
            {
                var household = root.AddComponent<HouseholdDomainModule>();
                var work = root.AddComponent<WorkAndTradeDomainModule>();
                household.Configure("home", "family", "parent", "person.care_worker", "child");
                work.Configure("shop", "store", "square", "club", "person.care_worker", "coworker", "club.member");
                draft.Nodes.Add(new GraphNode { Id = "sequence", Type = GraphNodeType.Sequence });
                var wait = new GraphNode { Id = "wait", Type = GraphNodeType.Wait }; wait.Set("ticks", "10000");
                draft.Nodes.Add(wait); draft.Edges.Clear();
                draft.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "sequence" });
                draft.Edges.Add(new GraphEdge { FromNodeId = "sequence", ToNodeId = "wait", Order = 0 });
                draft.Edges.Add(new GraphEdge { FromNodeId = "sequence", ToNodeId = "action.0", Order = 1 });
                household.ConfigureGraphs(draft);
                var world = CreateWorld(); household.Initialize(null, world); work.Initialize(null, world);
                AdvanceTo(world, 100);
                world.Paused = true;
                Assert.AreEqual(1, household.ActiveGraphCount);
                Assert.AreEqual(0, world.Activities.Sessions.Count, "The business adapter has not run yet.");
                household.OnRegionUnavailable("square", false); work.OnRegionUnavailable("square", false);
                Assert.AreEqual(1, household.ActiveGraphCount, "Closing another region must not cancel household care.");
                Assert.AreEqual("Planned", Duty(world, "community.walking_club.meetup").State,
                    "Temporary closure must preserve duties that have not started.");
                household.OnRegionUnavailable("home", true); work.OnRegionUnavailable("home", true);
                Assert.AreEqual(0, household.ActiveGraphCount);
                Assert.AreEqual("Cancelled", Duty(world, "household.care").State);
                world = Restore(world, household, work);
                Assert.AreEqual(0, household.ActiveGraphCount);
                Assert.AreEqual(0, world.Activities.Sessions.Count);
                Assert.IsFalse(household.CareCompleted);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(draft); }
        }

        private static DutyAssignment Duty(SimulationWorld world, string actionId)
        {
            foreach (var duty in world.Schedules.Assignments.Values) if (duty.ActionId == actionId) return duty;
            throw new System.InvalidOperationException("Missing test duty: " + actionId);
        }

        private static void AssertPoint(GameObject region, string id)
        {
            foreach (var point in region.GetComponentsInChildren<SocialInteractionPoint>(true))
                if (point.LocalId == id) return;
            Assert.Fail("Domain navigation point is not present in region prefab: " + region.name + "/" + id);
        }

        private static SimulationWorld CreateWorld()
        {
            var world = new SimulationWorld("noncampus.integration", 44) { TickSeconds = 5 };
            AddPlace(world, "home", "household.home");
            AddPlace(world, "shop", "trade.shop");
            AddPlace(world, "square", "community.square");
            foreach (var id in new[] { "family", "store", "club" })
                world.Society.Collectives.Add(id, new CollectiveState { Id = id, Name = id });
            AddAgent(world, "parent", "home", "family");
            AddAgent(world, "child", "home", "family");
            AddAgent(world, "person.care_worker", "home", "family", "store", "club");
            AddAgent(world, "coworker", "shop", "store");
            AddAgent(world, "club.member", "square", "club");
            return world;
        }

        private static void AddPlace(SimulationWorld world, string id, string tag)
        {
            world.Places.Add(id, new PlaceState
            { Id = id, Name = id, Tag = tag, RegionInstanceId = id, Capacity = 10 });
        }

        private static void AddAgent(SimulationWorld world, string id, string place, params string[] collectives)
        {
            world.AddAgent(new AgentState { Id = id, DisplayName = id, TemplateId = "test.actor", PlaceId = place });
            foreach (var collective in collectives) world.Society.Join(id, collective);
        }

        private static void AdvanceTo(SimulationWorld world, double seconds)
        {
            while (world.Time < seconds) world.StepOne();
        }

        private static int CountCompletedDuties(SimulationWorld world, string actorId)
        {
            var count = 0;
            foreach (var duty in world.Schedules.Assignments.Values)
                if (duty.AgentId == actorId && duty.State == "Completed") count++;
            return count;
        }
    }
}
