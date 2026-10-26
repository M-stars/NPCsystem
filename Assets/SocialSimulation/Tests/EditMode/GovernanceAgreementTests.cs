using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Tests
{
    public sealed class GovernanceAgreementTests
    {
        private static SimulationWorld World()
        {
            var world = new SimulationWorld("governance", 17);
            foreach (var id in new[] { "leader", "worker", "customer" })
                world.AddAgent(new AgentState { Id = id, CurrentGoalId = "personal.rest" });
            world.Society.Collectives.Add("shop", new CollectiveState { Id = "shop", Formal = true });
            world.Society.Join("leader", "shop");
            world.Society.Join("worker", "shop");
            world.Governance.CreateOrganization("shop.organization", "shop");
            world.Society.Grant("shop", "leader", GovernanceService.AssignAuthority, "shop");
            return world;
        }

        private static OrganizationTaskRequest Task(string key = "shift.1", double due = 20)
        {
            return new OrganizationTaskRequest { RequestKey = key, AssigneeId = "worker", ActionId = "shop.restock",
                TargetId = "shelf", DueTime = due };
        }

        private static Commitment Offer(SimulationWorld world, bool resource = false, int quantity = 1,
            double due = 20, string creditor = "customer")
        {
            return world.Agreements.Propose("agreement.test", new[] { "worker", creditor },
                new[] { new AgreementObligation { DebtorId = "worker", CreditorId = creditor, Quantity = quantity,
                    ActionId = resource ? null : "service.deliver", ResourceId = resource ? "money" : null, DueTime = due } },
                due, "shop");
        }

        private static void Accept(SimulationWorld world, Commitment agreement)
        {
            foreach (var party in agreement.PartyIds)
            {
                var actor = party == "shop" ? "leader" : party;
                if (party == "shop") world.Society.Grant("shop", actor, "agreement.accept", "shop");
                Assert.IsTrue(world.Agreements.Accept(agreement.Id, party, actor, out var reason), reason);
            }
            Assert.AreEqual("Active", agreement.Status);
        }

        private static SimulationWorld JsonRoundTrip(SimulationWorld world)
        {
            return WorldPersistence.Restore(JsonUtility.FromJson<SimulationSnapshot>(
                JsonUtility.ToJson(WorldPersistence.Capture(world))));
        }

        [Test]
        public void OrganizationAuthorityCreatesARequestAndPreservesMemberChoice()
        {
            var world = World();
            Assert.IsFalse(world.Governance.TryRequestTask("shop.organization", "worker", Task(), out _, out var refusal));
            Assert.AreEqual("MissingAuthority", refusal);
            Assert.IsTrue(world.Governance.TryRequestTask("shop.organization", "leader", Task(), out var task, out refusal), refusal);
            Assert.AreEqual("Pending", task.Status);
            Assert.AreEqual("personal.rest", world.Agents["worker"].CurrentGoalId);
            Assert.IsFalse(world.Governance.TryAccept(task.Id, "leader", out refusal));
            Assert.AreEqual("WrongActor", refusal);
            Assert.IsTrue(world.Governance.Decline(task.Id, "worker", "PersonalPriority"));
            Assert.AreEqual("Declined", task.Status);
            Assert.AreEqual("personal.rest", world.Agents["worker"].CurrentGoalId);
            Assert.IsFalse(world.Governance.Complete(task.Id, "worker"));
        }

        [Test]
        public void ScopedGrantCannotIssueTaskOutsideItsObject()
        {
            var world = World();
            foreach (var grant in world.Society.Grants.Values) grant.Active = false;
            world.Society.Grant("shop", "leader", GovernanceService.AssignAuthority, "shop", objectScopeId: "approved.shelf");
            Assert.IsFalse(world.Governance.TryRequestTask("shop.organization", "leader", Task(), out _, out var reason));
            Assert.AreEqual("MissingAuthority", reason);
            var request = Task(); request.TargetId = "approved.shelf";
            Assert.IsTrue(world.Governance.TryRequestTask("shop.organization", "leader", request, out _, out reason), reason);
        }

        private sealed class RejectingPolicy : IGovernanceStrategy
        {
            public string Id => "DomainBudgetPolicy";
            public GovernanceDecision Evaluate(GovernanceContext context, OrganizationTaskRequest request)
            {
                context.Assignee.CurrentGoalId = "forced.goal";
                context.Organization.Active = false;
                request.AssigneeId = "customer";
                return GovernanceDecision.Reject("BudgetReserved");
            }
        }

        [Test]
        public void ReplacementPolicyGetsCopiesAndCanVetoTheRequest()
        {
            var world = World();
            world.Governance.RegisterStrategy(new RejectingPolicy());
            world.Society.Organizations["shop.organization"].Governance = "DomainBudgetPolicy";
            var request = Task();
            Assert.IsFalse(world.Governance.TryRequestTask("shop.organization", "leader", request, out _, out var reason));
            Assert.AreEqual("BudgetReserved", reason);
            Assert.AreEqual(0, world.Tasks.Count);
            Assert.IsTrue(world.Society.Organizations["shop.organization"].Active);
            Assert.AreEqual("worker", request.AssigneeId);
            Assert.AreEqual("personal.rest", world.Agents["worker"].CurrentGoalId);
        }

        [Test]
        public void RequestRetrySurvivesJsonAndRejectsChangedPayload()
        {
            var world = World();
            Assert.IsTrue(world.Governance.TryRequestTask("shop.organization", "leader", Task(due: 1), out var original, out _));
            world.StepOne(); world.StepOne();
            var restored = JsonRoundTrip(world);
            Assert.IsTrue(restored.Governance.TryRequestTask("shop.organization", "leader", Task(due: 1), out var duplicate, out _));
            Assert.AreEqual(original.Id, duplicate.Id);
            Assert.AreEqual("Missed", duplicate.Status);
            Assert.AreEqual(1, restored.Tasks.Count);
            var changed = Task(due: 1); changed.ActionId = "shop.sell";
            Assert.IsFalse(restored.Governance.TryRequestTask("shop.organization", "leader", changed, out _, out var reason));
            Assert.AreEqual("RequestKeyConflict", reason);
        }

        [Test]
        public void AcceptanceRechecksRevokedIssuerAndLeftAssignee()
        {
            var world = World();
            Assert.IsTrue(world.Governance.TryRequestTask("shop.organization", "leader", Task(), out var task, out _));
            foreach (var grant in world.Society.Grants.Values) grant.Active = false;
            Assert.IsFalse(world.Governance.TryAccept(task.Id, "worker", out var reason));
            Assert.AreEqual("IssuerAuthorityExpired", reason);
            world.Society.Leave("worker", "shop"); world.StepOne();
            Assert.AreEqual("Cancelled", task.Status);
            Assert.AreEqual("AssigneeLeft", task.LastReason);
        }

        [Test]
        public void DissolutionCancelsOrganizationalWorkButPreservesDebt()
        {
            var world = World();
            var agreement = Offer(world); Accept(world, agreement);
            Assert.IsTrue(world.Governance.TryRequestTask("shop.organization", "leader", Task(), out var task, out _));
            Assert.IsTrue(world.Governance.TryAccept(task.Id, "worker", out _));
            world.Society.DissolveCollective("shop"); world.StepOne();
            Assert.AreEqual("Cancelled", task.Status);
            Assert.AreEqual("Active", agreement.Status);
            Assert.IsTrue(world.Society.Obligations.ContainsKey(agreement.ObligationIds[0]));
        }

        [Test]
        public void RemovingAgentClosesAcceptedOrganizationTaskBeforeAnyNextTick()
        {
            var world = World();
            Assert.IsTrue(world.Governance.TryRequestTask("shop.organization", "leader", Task(), out var task, out _));
            Assert.IsTrue(world.Governance.TryAccept(task.Id, "worker", out _));
            world.RemoveAgent("worker");
            Assert.AreEqual("Cancelled", task.Status);
            Assert.AreEqual("Cancelled", JsonRoundTrip(world).Tasks[task.Id].Status);
        }

        [Test]
        public void MetricsComeFromTasksAndTransactionLedgerIncludingDeduplication()
        {
            var world = World();
            world.Economy.Seed("shop", "money", 50);
            Assert.IsTrue(world.Economy.Transfer("wage", "shop", "worker", "money", 7));
            Assert.IsTrue(world.Economy.Transfer("wage", "shop", "worker", "money", 7));
            Assert.IsTrue(world.Governance.TryRequestTask("shop.organization", "leader", Task(), out var task, out _));
            Assert.IsTrue(world.Governance.TryAccept(task.Id, "worker", out _));
            Assert.IsTrue(world.Governance.Complete(task.Id, "worker"));
            var metrics = world.Governance.DeriveMetrics("shop.organization");
            Assert.AreEqual(1, metrics.CompletedTasks);
            Assert.AreEqual(0, metrics.PendingTasks);
            Assert.AreEqual(1, metrics.ResourceFlows.Count);
            Assert.AreEqual(7, metrics.ResourceFlows[0].Sent);
            Assert.AreEqual(0, metrics.ResourceFlows[0].Received, "Initial inventory is not revenue.");
            Assert.AreEqual(-7, metrics.ResourceFlows[0].Net);
        }

        [Test]
        public void AgreementRequiresEveryDistinctPartyAndAuthorizedCollectiveConsent()
        {
            var world = World();
            var agreement = Offer(world, creditor: "shop");
            Assert.IsTrue(world.Agreements.Accept(agreement.Id, "worker", "worker", out _));
            Assert.AreEqual("Proposed", agreement.Status);
            Assert.IsFalse(world.Agreements.Accept(agreement.Id, "shop", "leader", out var reason));
            Assert.AreEqual("PartyAuthorityMissing", reason);
            world.Society.Grant("shop", "leader", "agreement.accept", "shop", objectScopeId: agreement.Id);
            Assert.IsTrue(world.Agreements.Accept(agreement.Id, "shop", "leader", out reason), reason);
            Assert.AreEqual("Active", agreement.Status);
            Assert.AreEqual("Outstanding", world.Society.Obligations[agreement.ObligationIds[0]].Status);
        }

        [Test]
        public void InvalidOfferIsAtomicAndDoesNotConsumeIds()
        {
            var world = World();
            long sequence = world.CurrentSequence;
            Assert.Throws<ArgumentException>(() => world.Agreements.Propose("invalid", new[] { "worker", "worker" },
                new[] { new AgreementObligation { DebtorId = "worker", CreditorId = "customer", ActionId = "x", DueTime = 10 } }, 10));
            Assert.AreEqual(sequence, world.CurrentSequence);
            Assert.AreEqual(0, world.Society.Commitments.Count);
            Assert.AreEqual(0, world.Society.Obligations.Count);
        }

        [Test]
        public void UnacceptedProposalExpiresWithoutCreatingABreach()
        {
            var world = World();
            var offer = Offer(world, due: 1);
            world.StepOne(); world.StepOne();
            Assert.AreEqual("Expired", offer.Status);
            Assert.AreEqual("Cancelled", world.Society.Obligations[offer.ObligationIds[0]].Status);
            Assert.IsFalse(world.Agreements.Settlements.Any(x => x.Kind == "Breached"));
            Assert.AreEqual("Expired", JsonRoundTrip(world).Society.Commitments[offer.Id].Status);
        }

        [Test]
        public void RealTransferQuantityCannotBeDoubleCountedAcrossAgreements()
        {
            var world = World();
            var first = Offer(world, resource: true, quantity: 4); Accept(world, first);
            var second = Offer(world, resource: true, quantity: 4); Accept(world, second);
            world.Economy.Seed("worker", "money", 8);
            Assert.IsTrue(world.Economy.Transfer("pay.1", "worker", "customer", "money", 6));
            Assert.IsTrue(world.Agreements.ReconcileTransfer(first.ObligationIds[0], "pay.1", out var reason), reason);
            Assert.IsTrue(world.Agreements.ReconcileTransfer(second.ObligationIds[0], "pay.1", out reason), reason);
            Assert.AreEqual("Fulfilled", first.Status);
            Assert.AreEqual(2, world.Society.Obligations[second.ObligationIds[0]].FulfilledQuantity);
            var restored = JsonRoundTrip(world);
            Assert.IsTrue(restored.Agreements.ReconcileTransfer(second.ObligationIds[0], "pay.1", out reason), reason);
            Assert.AreEqual(2, restored.Society.Obligations[second.ObligationIds[0]].FulfilledQuantity);
            Assert.IsTrue(restored.Economy.Transfer("pay.2", "worker", "customer", "money", 2));
            Assert.IsTrue(restored.Agreements.ReconcileTransfer(second.ObligationIds[0], "pay.2", out reason), reason);
            Assert.AreEqual("Fulfilled", restored.Society.Commitments[second.Id].Status);
            Assert.AreEqual(8, restored.Agreements.Settlements.Where(x => x.Kind == "Transfer").Sum(x => x.Quantity));
        }

        [Test]
        public void TransferBeforeConsentCannotBeReusedEvenWithinTheSameTick()
        {
            var world = World();
            var agreement = Offer(world, resource: true);
            world.Economy.Seed("worker", "money", 2);
            Assert.IsTrue(world.Economy.Transfer("old.payment", "worker", "customer", "money", 1));
            Accept(world, agreement);
            Assert.IsFalse(world.Agreements.ReconcileTransfer(agreement.ObligationIds[0], "old.payment", out var reason));
            Assert.AreEqual("NoUnclaimedMatchingTransfer", reason);
            Assert.AreEqual(0, world.Society.Obligations[agreement.ObligationIds[0]].FulfilledQuantity);
        }

        [Test]
        public void DebtorCannotSelfCertifyPerformanceAndReceiptIsIdempotent()
        {
            var world = World();
            var agreement = Offer(world, quantity: 2); Accept(world, agreement);
            var obligation = agreement.ObligationIds[0];
            Assert.IsFalse(world.Agreements.ConfirmPerformance(obligation, "worker", "delivery.1", 1, out var reason));
            Assert.AreEqual("CreditorAuthorityMissing", reason);
            Assert.IsTrue(world.Agreements.ConfirmPerformance(obligation, "customer", "delivery.1", 1, out reason), reason);
            Assert.IsTrue(world.Agreements.ConfirmPerformance(obligation, "customer", "delivery.1", 1, out reason), reason);
            Assert.AreEqual(1, world.Society.Obligations[obligation].FulfilledQuantity);
            Assert.IsFalse(world.Agreements.ConfirmPerformance(obligation, "customer", "delivery.1", 2, out reason));
            Assert.AreEqual("ReceiptAlreadyUsed", reason);
            Assert.IsTrue(world.Agreements.ConfirmPerformance(obligation, "customer", "delivery.2", 1, out reason), reason);
            Assert.AreEqual("Fulfilled", agreement.Status);
        }

        [Test]
        public void PartialPerformanceAndConsentSurviveRealJsonSerialization()
        {
            var world = World();
            var agreement = Offer(world, quantity: 2);
            Assert.IsTrue(world.Agreements.Accept(agreement.Id, "worker", "worker", out _));
            world = JsonRoundTrip(world);
            Assert.IsTrue(world.Agreements.Accept(agreement.Id, "customer", "customer", out _));
            Assert.IsTrue(world.Agreements.ConfirmPerformance(agreement.ObligationIds[0], "customer", "delivery.1", 1, out _));
            world = JsonRoundTrip(world);
            Assert.IsTrue(world.Agreements.ConfirmPerformance(agreement.ObligationIds[0], "customer", "delivery.2", 1, out _));
            Assert.AreEqual("Fulfilled", JsonRoundTrip(world).Society.Commitments[agreement.Id].Status);
        }

        [Test]
        public void GoalSwitchAndAgentRemovalPreserveDebtAndLatePaymentRetainsBreachHistory()
        {
            var world = World();
            var agreement = Offer(world, resource: true, due: 1); Accept(world, agreement);
            world.Economy.Seed("worker", "money", 1);
            world.Agents["worker"].CurrentGoalId = "recreation";
            world.RemoveAgent("worker");
            world.StepOne(); world.StepOne();
            Assert.AreEqual("Breached", agreement.Status);
            Assert.AreEqual("Breached", world.Society.Obligations[agreement.ObligationIds[0]].Status);
            Assert.IsTrue(world.Economy.Transfer("late.payment", "worker", "customer", "money", 1));
            Assert.IsTrue(world.Agreements.ReconcileTransfer(agreement.ObligationIds[0], "late.payment", out var reason), reason);
            Assert.AreEqual("Fulfilled", agreement.Status);
            Assert.AreEqual(1, world.Agreements.Settlements.Count(x => x.Kind == "Breached"));
            Assert.AreEqual("Fulfilled", JsonRoundTrip(world).Society.Commitments[agreement.Id].Status);
        }

        [Test]
        public void BreachingOneObligationDoesNotEraseOrAccelerateOtherObligations()
        {
            var world = World();
            var agreement = world.Agreements.Propose("staged", new[] { "worker", "customer" }, new[]
            {
                new AgreementObligation { DebtorId = "worker", CreditorId = "customer", ActionId = "deliver", DueTime = 1 },
                new AgreementObligation { DebtorId = "customer", CreditorId = "worker", ResourceId = "money", DueTime = 10 }
            }, 10);
            Accept(world, agreement); world.StepOne(); world.StepOne();
            Assert.AreEqual("Breached", world.Society.Obligations[agreement.ObligationIds[0]].Status);
            Assert.AreEqual("Outstanding", world.Society.Obligations[agreement.ObligationIds[1]].Status);
            Assert.AreEqual("Breached", JsonRoundTrip(world).Society.Commitments[agreement.Id].Status);
        }

        [Test]
        public void TerminationRequiresAllPartiesAndKeepsPartialFulfillment()
        {
            var world = World();
            var agreement = Offer(world, quantity: 3); Accept(world, agreement);
            Assert.IsTrue(world.Agreements.ConfirmPerformance(agreement.ObligationIds[0], "customer", "part.1", 1, out _));
            Assert.IsTrue(world.Agreements.RequestTermination(agreement.Id, "worker", "worker", "Reschedule", out _));
            Assert.AreEqual("Active", agreement.Status);
            world = JsonRoundTrip(world);
            Assert.IsTrue(world.Agreements.RequestTermination(agreement.Id, "customer", "customer", "Agreed", out _));
            Assert.AreEqual("Terminated", world.Society.Commitments[agreement.Id].Status);
            Assert.AreEqual(1, world.Society.Obligations[agreement.ObligationIds[0]].FulfilledQuantity);
            Assert.AreEqual("Cancelled", world.Society.Obligations[agreement.ObligationIds[0]].Status);
            Assert.AreEqual("Terminated", JsonRoundTrip(world).Society.Commitments[agreement.Id].Status);
        }

        [Test]
        public void ExactDeadlineIsInclusiveAndLegacyCommitmentsRemainCompatible()
        {
            var world = World();
            var agreement = Offer(world, due: 1); Accept(world, agreement);
            var legacy = world.Society.CreateCommitment("legacy", new[] { "worker", "customer" }, 1);
            world.StepOne();
            Assert.IsTrue(world.Agreements.ConfirmPerformance(agreement.ObligationIds[0], "customer", "on.time", 1, out _));
            world.StepOne();
            Assert.AreEqual("Fulfilled", agreement.Status);
            Assert.AreEqual("Active", legacy.Status);
            Assert.IsFalse(world.Agreements.Settlements.Any(x => x.Kind == "Breached"));
        }

        [Test]
        public void CorruptQuantityOrFabricatedActivationIsRejectedOnRestore()
        {
            var world = World();
            var agreement = Offer(world); Accept(world, agreement);
            Assert.IsTrue(world.Agreements.ConfirmPerformance(agreement.ObligationIds[0], "customer", "receipt", 1, out _));
            var bad = WorldPersistence.Capture(world);
            bad.Obligations[0].FulfilledQuantity = 0;
            Assert.Throws<InvalidOperationException>(() => WorldPersistence.Restore(bad));
            bad = WorldPersistence.Capture(world);
            bad.AgreementSettlements.RemoveAll(x => x.Kind == "Accepted");
            Assert.Throws<InvalidOperationException>(() => WorldPersistence.Restore(bad));
        }
    }
}
