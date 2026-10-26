using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Domains.WorkAndTrade.Tests
{
    public sealed class TradeKnowledgeBehaviorTests
    {
        private static SimulationWorld CreateWorld()
        {
            var world = new SimulationWorld("trade.knowledge", 43);
            foreach (var id in new[] { "shop", "square" })
                world.Places.Add(id, new PlaceState { Id = id, Capacity = 20 });
            foreach (var id in new[] { "store", "club" })
                world.Society.Collectives.Add(id, new CollectiveState { Id = id });
            foreach (var id in new[] { "clerk", "coworker", "member", "customer" })
                world.AddAgent(new AgentState { Id = id, PlaceId = id == "clerk" || id == "coworker" ? "shop" : "square" });
            world.Society.Join("clerk", "store"); world.Society.Join("coworker", "store");
            world.Society.Join("clerk", "club"); world.Society.Join("member", "club");
            return world;
        }

        private static WorkAndTradeDomainModule Module(GameObject root, SimulationWorld world)
        {
            var module = root.AddComponent<WorkAndTradeDomainModule>();
            module.Configure("shop", "store", "square", "club", "clerk", "coworker", "member", "customer");
            module.Initialize(null, world); return module;
        }

        [Test]
        public void LocalAnnouncementAndConversationChangeRealPurchaseAndSurviveJsonRestore()
        {
            var root = new GameObject("Trade knowledge integration");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                var closed = module.SetShopServiceAvailable(false);
                Assert.IsNotNull(closed);
                Assert.AreEqual("trade.purchase.defer", module.SelectPurchaseGoal("coworker").Kind);
                Assert.AreEqual("trade.purchase", module.SelectPurchaseGoal("customer").Kind);
                Assert.IsNull(module.PublishShopAvailability("coworker"), "Membership alone does not authorize publication.");
                Assert.AreEqual(KnowledgeDelivery.Denied, module.RelayShopAvailability("coworker", "customer", closed.OriginEventId));
                world.Agents["coworker"].PlaceId = "square";
                Assert.AreEqual(KnowledgeDelivery.Learned, module.RelayShopAvailability("coworker", "customer", closed.OriginEventId));
                Assert.IsFalse(module.TryStartCustomerPurchase());
                Assert.AreEqual(0, module.ActiveGraphCount);
                var domainJson = module.CaptureScenarioState();
                world = WorldPersistence.Restore(JsonUtility.FromJson<SimulationSnapshot>(JsonUtility.ToJson(WorldPersistence.Capture(world))));
                module.ValidateScenarioState(null, world, domainJson);
                module.RestoreScenarioState(null, world, domainJson);
                Assert.IsFalse(module.ShopServiceAvailable);
                Assert.IsFalse(module.TryStartCustomerPurchase(), "Restoring knowledge must preserve the choice.");
                world.StepOne(); world.Agents["coworker"].PlaceId = "shop";
                var opened = module.SetShopServiceAvailable(true);
                Assert.IsFalse(module.TryStartCustomerPurchase(), "A distant customer cannot see the new state yet.");
                world.Agents["coworker"].PlaceId = "square";
                Assert.AreEqual(KnowledgeDelivery.Learned, module.RelayShopAvailability("coworker", "customer", opened.OriginEventId));
                Assert.IsTrue(module.TryStartCustomerPurchase());
                world.Agents["customer"].PlaceId = "shop";
                world.Advance(30);
                Assert.IsTrue(module.StudentPurchaseCompleted);
                Assert.AreEqual(1, world.Economy.Balance("customer", "food.snack"));
                Assert.AreEqual(8, world.Economy.Balance("customer", "money.credit"));
                Assert.IsFalse(module.TryStartCustomerPurchase(), "A completed purchase must not charge twice.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnknownCustomerAttemptsClosedServiceThenLearnsAtCounterWithoutReceivingGoods()
        {
            var root = new GameObject("Unknown service attempt");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                module.SetShopServiceAvailable(false, false);
                Assert.IsTrue(module.TryStartCustomerPurchase());
                Assert.AreEqual("trade.purchase.shop", world.Decisions.LastTrace["customer"].SelectedGoalId);
                world.Agents["customer"].PlaceId = "shop";
                world.Advance(30);
                Assert.IsFalse(module.StudentPurchaseCompleted);
                Assert.AreEqual(0, world.Economy.Balance("customer", "food.snack"));
                Assert.AreEqual(10, world.Economy.Balance("customer", "money.credit"));
                Assert.AreEqual("trade.purchase.defer", module.SelectPurchaseGoal("customer").Kind);
                var reports = world.Knowledge.ForAgent("customer").Query(WorkAndTradeDomainModule.AvailabilityTopic, "shop");
                Assert.AreEqual(1, reports.Count); Assert.IsTrue(reports[0].DirectlyObserved);
                Assert.AreEqual(0, world.Knowledge.ForAgent("member").Query(WorkAndTradeDomainModule.AvailabilityTopic).Count);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void OldTradeCheckpointIsRejectedBeforeChangingTheCurrentWorld()
        {
            var root = new GameObject("Trade schema compatibility");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                module.SetShopServiceAvailable(false, false);
                var current = module.CaptureScenarioState();
                var old = current.Replace("\"SchemaVersion\":3", "\"SchemaVersion\":2");
                Assert.AreNotEqual(current, old);
                Assert.Throws<System.InvalidOperationException>(() => module.ValidateScenarioState(null, world, old));
                Assert.Throws<System.InvalidOperationException>(() => module.RestoreScenarioState(null, world, old));
                Assert.AreEqual(current, module.CaptureScenarioState());
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ReopeningKnowledgeAutomaticallyRestartsTheDeferredPurchaseWithoutAnotherRequest()
        {
            var root = new GameObject("Automatic purchase re-evaluation");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                var closed = module.SetShopServiceAvailable(false);
                world.Agents["coworker"].PlaceId = "square";
                Assert.AreEqual(KnowledgeDelivery.Learned,
                    module.RelayShopAvailability("coworker", "customer", closed.OriginEventId));
                world.Advance(2);
                Assert.AreEqual(0, module.ActiveGraphCount, "Closed service defers the purchase automatically.");
                Assert.AreEqual("trade.purchase.defer.shop", world.Decisions.LastTrace["customer"].SelectedGoalId);
                Assert.IsTrue(world.Trace.Exists(item => item.Type == "trade.purchase.deferred" && item.SubjectId == "customer"));
                world.Agents["coworker"].PlaceId = "shop";
                var opened = module.SetShopServiceAvailable(true);
                Assert.IsNotNull(opened, "The clerk republishes availability at the counter.");
                world.Agents["coworker"].PlaceId = "square";
                Assert.AreEqual(KnowledgeDelivery.Learned, module.RelayShopAvailability("coworker", "customer", opened.OriginEventId));
                // No module code or test asks the customer to buy again: the knowledge change alone restarts it.
                world.Advance(2);
                Assert.AreEqual(1, module.ActiveGraphCount);
                Assert.AreEqual("trade.purchase.shop", world.Decisions.LastTrace["customer"].SelectedGoalId);
                world.Agents["customer"].PlaceId = "shop";
                world.Advance(30);
                Assert.IsTrue(module.StudentPurchaseCompleted);
                Assert.AreEqual(1, world.Economy.CopyTransactionIds().Count, "Re-evaluation must not duplicate the purchase.");
                Assert.AreEqual(1, world.Economy.Balance("customer", "food.snack"));
                Assert.AreEqual(8, world.Economy.Balance("customer", "money.credit"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ExpiredAvailabilityMemoryAloneRestartsTheAttemptAndFailsAtTheRealCounter()
        {
            var root = new GameObject("Expired memory retry");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                module.SetShopServiceAvailable(false, false);
                Assert.IsTrue(module.TryStartCustomerPurchase(), "Unknown availability allows an attempt.");
                world.Agents["customer"].PlaceId = "shop";
                world.Advance(30);
                Assert.IsFalse(module.StudentPurchaseCompleted);
                Assert.AreEqual(0, module.ActiveGraphCount);
                Assert.AreEqual("trade.purchase.defer.shop", world.Decisions.LastTrace["customer"].SelectedGoalId);
                // The observation expires after its retention window; no module or test intervenes.
                world.Advance(1810);
                Assert.AreEqual(1, module.ActiveGraphCount,
                    "A forgotten memory alone re-attempts the purchase.");
                world.Advance(30);
                Assert.IsFalse(module.StudentPurchaseCompleted, "The real counter is still suspended.");
                Assert.AreEqual(0, world.Economy.CopyTransactionIds().Count);
                Assert.AreEqual(0, world.Economy.Balance("customer", "food.snack"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ScheduledCustomerPurchaseUsesPersonalInformationBeforeStartingAction()
        {
            var root = new GameObject("Scheduled knowledge decision");
            try
            {
                var world = CreateWorld(); var module = Module(root, world);
                world.Advance(3550);
                var notice = module.SetShopServiceAvailable(false);
                world.Agents["coworker"].PlaceId = "square";
                Assert.AreEqual(KnowledgeDelivery.Learned, module.RelayShopAvailability("coworker", "customer", notice.OriginEventId));
                world.Advance(100);
                Assert.IsFalse(module.StudentPurchaseCompleted);
                Assert.AreEqual("trade.purchase.defer.shop", world.Decisions.LastTrace["customer"].SelectedGoalId);
                Assert.IsTrue(world.Trace.Exists(item => item.Type == "trade.purchase.deferred" && item.SubjectId == "customer"));
                Assert.AreEqual(0, world.Economy.Balance("customer", "food.snack"));
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
