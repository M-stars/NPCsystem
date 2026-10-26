using System.Collections;
using NUnit.Framework;
using SocialSimulation.Core;
using SocialSimulation.Domains.Household;
using SocialSimulation.Domains.WorkAndTrade;
using SocialSimulation.UnityRuntime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace SocialSimulation.Domains.Combined.Tests
{
    public sealed class KnowledgePurchasePlayModeTests
    {
        [UnityTest, Timeout(120000)]
        public IEnumerator ActualConversationChangesGoalAndReopeningAllowsAnimatedPurchaseAfterLoad()
        {
            yield return SceneManager.LoadSceneAsync("IndependentCommunity", LoadSceneMode.Single);
            var runner = Object.FindAnyObjectByType<SocialWorldRunner>();
            Assert.IsNotNull(runner?.World);
            runner.SetPaused(true);
            runner.GetComponent<HouseholdDomainModule>().enabled = false;
            var trade = runner.GetComponent<WorkAndTradeDomainModule>();
            const string customer = "npc.student.01", staff = "npc.clerk.02", shop = "trade.shop.corner";
            runner.World.Society.Grant("trade.corner_shop", staff, "trade.availability.publish", "trade.corner_shop");
            trade.SetShopServiceAvailable(false, false);
            var closed = trade.PublishShopAvailability(staff);
            Assert.IsNotNull(closed);
            Assert.AreEqual("trade.purchase", trade.SelectPurchaseGoal(customer).Kind);
            Assert.AreEqual("trade.purchase.defer", trade.SelectPurchaseGoal(staff).Kind);
            Assert.AreEqual(KnowledgeDelivery.Denied, trade.RelayShopAvailability(staff, customer, closed.OriginEventId));
            Assert.IsTrue(runner.RequestTravel(customer, shop, trade.PurchasePointId, "knowledge.conversation.visit"));
            runner.World.TimeScale = 10; runner.SetPaused(false);
            var deadline = Time.realtimeSinceStartup + 45;
            while (runner.GetTravelStatus(customer, shop, "knowledge.conversation.visit") == SocialNavigationStatus.InProgress &&
                Time.realtimeSinceStartup < deadline && !runner.IsFaulted) yield return null;
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
            Assert.AreEqual(SocialNavigationStatus.Succeeded, runner.GetTravelStatus(customer, shop, "knowledge.conversation.visit"));
            runner.SetPaused(true);
            Assert.AreEqual(KnowledgeDelivery.Learned, trade.RelayShopAvailability(staff, customer, closed.OriginEventId));
            Assert.IsFalse(trade.TryStartCustomerPurchase());
            Assert.AreEqual(0, runner.World.Economy.Balance(customer, "food.snack"));
            runner.LoadFromJson(runner.SaveToJson());
            Assert.IsFalse(trade.ShopServiceAvailable);
            Assert.IsFalse(trade.TryStartCustomerPurchase());
            Assert.AreEqual("trade.purchase.defer", trade.SelectPurchaseGoal(customer).Kind);
            Assert.IsTrue(runner.TryStepOne(out var error), error);
            trade.SetShopServiceAvailable(true, false);
            Assert.IsNotNull(trade.PublishShopAvailability(staff));
            Assert.IsTrue(trade.TryStartCustomerPurchase());
            runner.SetPaused(false);
            deadline = Time.realtimeSinceStartup + 30;
            bool actionCueSeen = false;
            while (!trade.StudentPurchaseCompleted && Time.realtimeSinceStartup < deadline && !runner.IsFaulted)
            {
                actionCueSeen |= runner.NpcViews[customer].Animator.GetBool("ActionActive");
                yield return null;
            }
            Assert.IsFalse(runner.IsFaulted, runner.FaultMessage);
            Assert.IsTrue(trade.StudentPurchaseCompleted);
            Assert.IsTrue(actionCueSeen, "The real purchase graph must drive the character's action presentation.");
            Assert.AreEqual(1, runner.World.Economy.Balance(customer, "food.snack"));
            Assert.AreEqual(8, runner.World.Economy.Balance(customer, "money.credit"));
            runner.SetPaused(true);
        }
    }
}
