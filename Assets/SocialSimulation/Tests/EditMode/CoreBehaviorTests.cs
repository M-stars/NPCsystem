using System;
using System.Collections.Generic;
using NUnit.Framework;
using SocialSimulation.Core;

namespace SocialSimulation.Tests
{
    public sealed class CoreBehaviorTests
    {
        private static SimulationWorld WorldWithAgents(params string[] ids)
        {
            var world = new SimulationWorld("test", 42);
            foreach (var id in ids) world.AddAgent(new AgentState { Id = id, DisplayName = id, PlaceId = "hall" });
            world.Places.Add("hall", new PlaceState { Id = "hall", Tag = "corridor", Capacity = 10 });
            return world;
        }

        [Test]
        public void PackageRegistrationIsAtomicAndIdempotent()
        {
            var registry = new ContentRegistry();
            registry.RegisterExecutor("move");
            var invalid = new PackageManifest { Id = "people", Version = "1", Kind = "NPC" };
            invalid.Capabilities.Add(new CapabilityDefinition { Id = "walk", ExecutorId = "missing" });
            Assert.IsFalse(registry.Register(invalid).Success);
            Assert.AreEqual(0, registry.Packages.Count);
            invalid.Capabilities[0].ExecutorId = "move";
            invalid.Actions.Add(new ActionDefinition { Id = "go", CapabilityId = "walk", ExecutorId = "move" });
            Assert.IsTrue(registry.Register(invalid).Success);
            Assert.IsTrue(registry.Register(invalid).AlreadyRegistered);
            Assert.AreEqual(1, registry.Actions.Count);
        }

        [Test]
        public void PackageBatchFailureLeavesExistingRegistryUntouched()
        {
            var registry = new ContentRegistry();
            registry.RegisterExecutor("move");
            Assert.IsTrue(registry.Register(new PackageManifest { Id = "base", Version = "1", Kind = "Domain" }).Success);
            var batch = registry.RegisterBatch(new[]
            {
                new PackageManifest { Id = "valid", Version = "1", Kind = "Domain" },
                new PackageManifest { Id = "invalid", Version = "1", Kind = "Domain",
                    Dependencies = new List<string> { "missing" } }
            });
            Assert.IsFalse(batch.Success);
            Assert.IsFalse(registry.Packages.ContainsKey("valid"));
            Assert.AreEqual(1, registry.Packages.Count);
        }

        [Test]
        public void PackageBatchResolvesDependenciesAndRejectsChangedSameVersion()
        {
            var registry = new ContentRegistry();
            registry.RegisterExecutor("move");
            var basePackage = new PackageManifest { Id = "base", Version = "1", Kind = "NPC" };
            basePackage.Capabilities.Add(new CapabilityDefinition { Id = "walk", ExecutorId = "move",
                BodyConstraint = "Humanoid" });
            var extension = new PackageManifest { Id = "extension", Version = "1", Kind = "Domain",
                Dependencies = new List<string> { "base" } };
            Assert.IsTrue(registry.RegisterBatch(new[] { extension, basePackage }).Success);
            Assert.AreEqual(2, registry.Packages.Count);
            var changed = new PackageManifest { Id = "base", Version = "1", Kind = "NPC" };
            changed.Capabilities.Add(new CapabilityDefinition { Id = "walk", ExecutorId = "move",
                BodyConstraint = "Generic" });
            Assert.IsFalse(registry.Register(changed).Success);
            Assert.AreEqual("Humanoid", registry.Capabilities["walk"].BodyConstraint);
        }

        [Test]
        public void DifferentNpcPackagesCanShareCompatibleSemanticCapabilities()
        {
            var registry = new ContentRegistry();
            registry.RegisterExecutor("navmesh.move");
            var first = new PackageManifest { Id = "npc.alpha", Version = "1", Kind = "NPC" };
            first.Capabilities.Add(new CapabilityDefinition
            { Id = "locomotion.walk", ExecutorId = "navmesh.move", BodyConstraint = "Humanoid" });
            var second = new PackageManifest { Id = "npc.beta", Version = "1", Kind = "NPC" };
            second.Capabilities.Add(new CapabilityDefinition
            { Id = "locomotion.walk", ExecutorId = "navmesh.move", BodyConstraint = "Humanoid" });
            Assert.IsTrue(registry.RegisterBatch(new[] { first, second }).Success);
            Assert.AreEqual(2, registry.Packages.Count);
            Assert.AreEqual(1, registry.Capabilities.Count);
            var conflicting = new PackageManifest { Id = "npc.gamma", Version = "1", Kind = "NPC" };
            conflicting.Capabilities.Add(new CapabilityDefinition
            { Id = "locomotion.walk", ExecutorId = "navmesh.move", BodyConstraint = "Generic" });
            Assert.IsFalse(registry.Register(conflicting).Success);
        }

        [Test]
        public void RegionInstanceIdentityCombinesInstanceAndLocalPoint()
        {
            var first = Identity.SceneObject("region.instance.01", "seat.01");
            var second = Identity.SceneObject("region.instance.02", "seat.01");
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(first, Identity.SceneObject("region.instance.01", "seat.01"));
        }

        [Test]
        public void RegionPackageRegistersZoneHierarchyAndRejectsSameVersionMutation()
        {
            var registry = new ContentRegistry();
            var manifest = new PackageManifest { Id = "region.shop", Version = "1", Kind = "Region" };
            var region = new RegionDefinition { Id = "shop", PackageId = "region.shop",
                Tag = "trade.shop", Capacity = 8 };
            region.Zones.Add(new RegionZoneDefinition { LocalId = "counter", Tag = "trade.counter", Capacity = 2 });
            region.Points.Add(new InteractionPointDefinition { LocalId = "checkout", ZoneLocalId = "counter",
                Kind = "trade.purchase", Capacity = 1 });
            manifest.Regions.Add(region);
            Assert.IsTrue(registry.Register(manifest).Success);
            Assert.AreEqual("counter", registry.Regions["shop"].Points[0].ZoneLocalId);
            region.Zones[0].Tag = "changed";
            Assert.IsFalse(registry.Register(manifest).Success);
            Assert.AreEqual("trade.counter", registry.Regions["shop"].Zones[0].Tag);
            var invalid = new PackageManifest { Id = "region.bad", Version = "1", Kind = "Region" };
            var badRegion = new RegionDefinition { Id = "bad", PackageId = "region.bad", Capacity = 2 };
            badRegion.Zones.Add(new RegionZoneDefinition { LocalId = "one", Tag = "test", Capacity = 1 });
            badRegion.Points.Add(new InteractionPointDefinition { LocalId = "point", ZoneLocalId = "absent", Capacity = 1 });
            invalid.Regions.Add(badRegion);
            Assert.IsFalse(registry.Register(invalid).Success);
        }

        [Test]
        public void LastSeatHasOneOwnerAndScopeTransferSurvivesLeafCancellation()
        {
            var world = WorldWithAgents("a", "b");
            world.Resources.Slots.Add("seat", new ResourceSlot { Id = "seat", PlaceId = "hall", Kind = "seat", Capacity = 1 });
            var first = world.Resources.TryAcquire("seat", "action-a", "a", 30);
            var second = world.Resources.TryAcquire("seat", "action-b", "b", 30);
            Assert.NotNull(first);
            Assert.IsNull(second);
            Assert.IsTrue(world.Resources.Transfer(first.Id, "action-a", "session-1"));
            world.Resources.ReleaseScope("action-a");
            Assert.IsTrue(first.Active);
            Assert.IsFalse(world.Resources.CanAcquire("seat", "b"));
            world.Resources.ReleaseScope("session-1");
            Assert.IsTrue(world.Resources.CanAcquire("seat", "b"));
            Assert.IsFalse(world.Resources.Release(first.Id, "action-a"));
        }

        [Test]
        public void ExchangeIsAtomicAndSnapshotRestoresOwnershipAndDeduplication()
        {
            var world = WorldWithAgents("buyer", "seller");
            world.Economy.Seed("buyer", "coin", 10);
            world.Economy.Seed("seller", "apple", 2);
            Assert.IsFalse(world.Economy.Exchange("bad", "buyer", "seller", "apple", 3, "coin", 5));
            Assert.AreEqual(10, world.Economy.Balance("buyer", "coin"));
            Assert.IsTrue(world.Economy.Exchange("sale-1", "buyer", "seller", "apple", 1, "coin", 5));
            world.Resources.Slots.Add("seat", new ResourceSlot { Id = "seat", PlaceId = "hall", Capacity = 1 });
            var lease = world.Resources.TryAcquire("seat", "action-1", "buyer", 30);
            var snapshot = WorldPersistence.Capture(world);
            world.Economy.Seed("buyer", "coin", 100);
            world.Resources.ReleaseScope("action-1");
            var restored = WorldPersistence.Restore(snapshot);
            Assert.AreEqual(5, restored.Economy.Balance("buyer", "coin"));
            Assert.IsTrue(restored.Resources.Tokens[lease.Id].Active);
            Assert.IsTrue(restored.Economy.Exchange("sale-1", "buyer", "seller", "apple", 1, "coin", 5));
            Assert.IsFalse(restored.Economy.Exchange("sale-1", "buyer", "seller", "apple", 1, "coin", 6));
            Assert.AreEqual(1, restored.Economy.Balance("buyer", "apple"));
        }

        [Test]
        public void ZeroPriceExchangeCommitsAtomicallyEvenWhenBuyerNeverHeldMoney()
        {
            var world = WorldWithAgents("buyer", "seller");
            world.Economy.Seed("seller", "apple", 2);
            Assert.IsTrue(world.Economy.Exchange("gift-1", "buyer", "seller", "apple", 1, "coin", 0));
            Assert.AreEqual(1, world.Economy.Balance("buyer", "apple"));
            Assert.AreEqual(1, world.Economy.Balance("seller", "apple"));
            Assert.AreEqual(0, world.Economy.Balance("buyer", "coin"));
            Assert.AreEqual(1, world.Economy.Ledger.Count, "A free exchange writes no counter-resource ledger entry.");
            // The receipt commits even for a free transfer, so a replay cannot move the item twice.
            Assert.IsTrue(world.Economy.Exchange("gift-1", "buyer", "seller", "apple", 1, "coin", 0));
            Assert.AreEqual(1, world.Economy.Balance("buyer", "apple"));
        }

        [Test]
        public void TransactionIdCannotBeReusedForAnotherTransfer()
        {
            var world = WorldWithAgents("a", "b", "c");
            world.Economy.Seed("a", "coin", 20);
            Assert.IsTrue(world.Economy.Transfer("payment-1", "a", "b", "coin", 4));
            Assert.IsTrue(world.Economy.Transfer("payment-1", "a", "b", "coin", 4));
            Assert.IsFalse(world.Economy.Transfer("payment-1", "a", "c", "coin", 4));
            Assert.IsFalse(world.Economy.Exchange("payment-1", "a", "b", "apple", 1, "coin", 4));
            Assert.AreEqual(1, world.Economy.Ledger.Count);
            Assert.AreEqual(16, world.Economy.Balance("a", "coin"));
            Assert.AreEqual(0, world.Economy.Balance("c", "coin"));
        }

        [Test]
        public void TransferReceiptTreatsOnlyOptionalCounterResourceAsEmptyAfterSerialization()
        {
            var world = WorldWithAgents("a", "b");
            world.Economy.Seed("a", "coin", 10);
            Assert.IsTrue(world.Economy.Transfer("wage", "a", "b", "coin", 3));
            var snapshot = WorldPersistence.Capture(world);
            Assert.AreEqual(1, snapshot.TransactionReceipts.Count);
            // JsonUtility deserializes a null optional string as an empty string.
            snapshot.TransactionReceipts[0].CounterResourceId = string.Empty;
            var restored = WorldPersistence.Restore(snapshot);
            Assert.IsTrue(restored.Economy.Transfer("wage", "a", "b", "coin", 3));
            Assert.IsFalse(restored.Economy.Transfer("wage", "a", "b", "coin", 4));
            Assert.AreEqual(1, restored.Economy.Ledger.Count);
            Assert.AreEqual(3, restored.Economy.Balance("b", "coin"));
        }

        [Test]
        public void LeavingJobRevokesScopedAuthorityButKeepsFriendship()
        {
            var world = WorldWithAgents("manager", "friend");
            world.Society.Collectives.Add("shop", new CollectiveState { Id = "shop", Name = "Shop", Formal = true });
            world.Society.Join("manager", "shop");
            world.Society.AssignRole("manager", "shop", "manager");
            world.Society.Grant("shop", "manager", "policy.publish", "shop", "hall");
            world.Society.Relate("manager", "friend", "friend", 0.4f, 0.2f);
            Assert.IsTrue(world.Society.HasAuthority("manager", "policy.publish", "shop", "hall"));
            Assert.IsFalse(world.Society.HasAuthority("manager", "policy.publish", "shop", "elsewhere"));
            world.Society.Leave("manager", "shop");
            Assert.IsFalse(world.Society.HasAuthority("manager", "policy.publish", "shop", "hall"));
            Assert.AreEqual(1, world.Society.Relations.Count);
        }

        [Test]
        public void WitnessEvidenceIsRequiredBeforeViolationAndResponse()
        {
            var world = WorldWithAgents("runner", "teacher", "remote");
            world.Agents["remote"].PlaceId = "elsewhere";
            world.Society.Collectives.Add("school", new CollectiveState { Id = "school", Formal = true });
            world.Places["hall"].OwnerCollectiveId = "school";
            world.Society.Join("teacher", "school");
            world.Society.Grant("school", "teacher", "violation.handle", "school", "hall");
            var definition = new NormDefinition { Id = "no-run", Version = "1", ActionId = "run",
                PlaceTag = "corridor", CollectiveId = "school", Formal = false };
            world.Norms.Definitions.Add(definition.Id, definition);
            var norm = world.Norms.Publish(definition.Id, "school", "school");
            var action = world.Emit("run", "runner", "runner", null, "hall");
            Assert.IsNull(world.Norms.RecordViolation(norm.Id, "missing"));
            Assert.IsNull(world.Norms.Observe(action, "remote"));
            var evidence = world.Norms.Observe(action, "teacher");
            Assert.NotNull(evidence);
            var record = world.Norms.RecordViolation(norm.Id, evidence.Id);
            Assert.NotNull(record);
            Assert.AreEqual(record.Id, world.Norms.RecordViolation(norm.Id, evidence.Id).Id);
            Assert.NotNull(world.Norms.RequestResponse(record.Id, "teacher"));
            Assert.IsNull(world.Norms.RequestResponse(record.Id, "teacher"));
        }

        [Test]
        public void CourseCanStartWithoutLateStudentAndJoinLater()
        {
            var world = WorldWithAgents("teacher", "student");
            world.Places.Add("classroom", new PlaceState { Id = "classroom", Capacity = 30 });
            world.Agents["teacher"].PlaceId = "classroom";
            var session = world.Activities.Create("course", "teacher", "classroom", 0, 10, 60, 1, 30);
            Assert.IsTrue(world.Activities.Invite(session.Id, "student", "Student"));
            Assert.IsTrue(world.Activities.Respond(session.Id, "student", true));
            Assert.IsTrue(world.Activities.Start(session.Id));
            Assert.AreEqual("Accepted", session.Participants[1].Status);
            world.Agents["student"].PlaceId = "classroom";
            Assert.IsTrue(world.Activities.JoinLate(session.Id, "student"));
            Assert.AreEqual("Participating", session.Participants[1].Status);
        }

        [Test]
        public void DeclinedInvitationFreesCapacityButUnstartedSessionCannotComplete()
        {
            var world = WorldWithAgents("host", "first", "replacement");
            var session = world.Activities.Create("club", "host", "hall", 0, 10, 20, 1, 2);
            Assert.IsFalse(world.Activities.Complete(session.Id, "never started"));
            Assert.IsTrue(world.Activities.Invite(session.Id, "first"));
            Assert.IsFalse(world.Activities.Invite(session.Id, "replacement"));
            Assert.IsTrue(world.Activities.Respond(session.Id, "first", false));
            Assert.IsTrue(world.Activities.Invite(session.Id, "replacement"));
            Assert.IsTrue(world.Activities.Respond(session.Id, "replacement", true));
            Assert.IsTrue(world.Activities.Start(session.Id));
            Assert.IsTrue(world.Activities.Complete(session.Id, "met"));
            Assert.AreEqual("Completed", session.State);
        }

        [Test]
        public void SharedScheduleExplainsCrossDomainConflictAndRestoresDecision()
        {
            var world = WorldWithAgents("worker");
            world.TickSeconds = 5;
            var work = world.Schedules.Create(new ScheduleDefinition { Id = "work.shift", Version = "2",
                DomainId = "work", ActionId = "work.perform", PlaceId = "hall",
                StartOffsetSeconds = 10, TravelLeadSeconds = 5, LateGraceSeconds = 5,
                DurationSeconds = 40, Priority = 5 }, "worker", 0);
            var family = world.Schedules.Create(new ScheduleDefinition { Id = "family.care", Version = "1",
                DomainId = "household", ActionId = "household.care", PlaceId = "hall",
                StartOffsetSeconds = 15, LateGraceSeconds = 10, DurationSeconds = 20,
                Priority = 3 }, "worker", 0);
            world.StepOne(); // t=5, work is due for early travel
            Assert.AreEqual("Due", work.State);
            Assert.IsTrue(world.Schedules.TryAccept(work.Id, "worker", out var acceptedReason));
            Assert.IsNull(acceptedReason);
            world.StepOne(); world.StepOne(); // t=15, family becomes due
            Assert.IsFalse(world.Schedules.TryAccept(family.Id, "worker", out var conflict));
            StringAssert.StartsWith("ConflictWith:" + work.Id, conflict);
            Assert.AreEqual("Due", family.State); // still available for a deliberate later choice
            var restored = WorldPersistence.Restore(WorldPersistence.Capture(world));
            Assert.AreEqual("Accepted", restored.Schedules.Assignments[work.Id].State);
            Assert.AreEqual(conflict, restored.Schedules.Assignments[family.Id].LastRefusalReason);
            Assert.AreEqual("2", restored.Schedules.Assignments[work.Id].DefinitionVersion);
            Assert.IsTrue(restored.Schedules.Complete(work.Id, "worker"));
            Assert.IsTrue(restored.Schedules.TryAccept(family.Id, "worker", out var afterWork));
            Assert.IsNull(afterWork);
        }

        [Test]
        public void ScheduleRejectsMissingScopedRoleAndMarksUnacceptedDutyMissed()
        {
            var world = WorldWithAgents("a");
            world.TickSeconds = 5;
            world.Society.Collectives.Add("shop", new CollectiveState { Id = "shop", Formal = true });
            world.Society.Join("a", "shop");
            world.Society.AssignRole("a", "shop", "trade.clerk");
            var duty = world.Schedules.Create(new ScheduleDefinition { Id = "shop.shift", DomainId = "trade",
                ActionId = "work.perform", PlaceId = "hall", RequiredRoleId = "trade.clerk",
                StartOffsetSeconds = 5, DurationSeconds = 30 }, "a", 0);
            world.Society.Leave("a", "shop");
            world.StepOne();
            Assert.IsFalse(world.Schedules.TryAccept(duty.Id, "a", out var reason));
            Assert.AreEqual("MissingRole:trade.clerk", reason);
            world.StepOne();
            Assert.AreEqual("Missed", duty.State);
            Assert.AreEqual("DeadlinePassed", duty.LastRefusalReason);
        }

        [Test]
        public void AcceptedDutyBecomesUnfulfilledOnlyAfterCompletionGraceTick()
        {
            var world = WorldWithAgents("a");
            world.TickSeconds = 5;
            var duty = world.Schedules.Create(new ScheduleDefinition { Id = "work.shift",
                DomainId = "work", ActionId = "work.perform", StartOffsetSeconds = 5,
                DurationSeconds = 10 }, "a", 0);
            world.StepOne(); // t=5, duty starts
            Assert.IsTrue(world.Schedules.TryAccept(duty.Id, "a", out _));
            world.StepOne(); // t=10
            world.StepOne(); // t=15, the nominal end; business completion can still arrive
            Assert.AreEqual("Accepted", duty.State);
            world.StepOne(); // t=20, one tick of event-queue grace
            Assert.AreEqual("Accepted", duty.State);
            world.StepOne(); // t=25
            Assert.AreEqual("Missed", duty.State);
            Assert.AreEqual("AcceptedButUnfulfilled", duty.LastRefusalReason);
            Assert.IsFalse(world.Schedules.Cancel(duty.Id, "late cancellation"));
        }

        [Test]
        public void DutyCanBeAcceptedDuringTravelLeadWhenScopedRoleStartsAtDutyTime()
        {
            var world = WorldWithAgents("a");
            world.TickSeconds = 5;
            world.Society.Collectives.Add("shop", new CollectiveState { Id = "shop", Formal = true });
            world.Society.Join("a", "shop");
            var role = world.Society.AssignRole("a", "shop", "trade.clerk");
            role.BeginsAt = 10;
            var duty = world.Schedules.Create(new ScheduleDefinition { Id = "shop.shift",
                DomainId = "trade", ActionId = "work.perform", RequiredRoleId = "trade.clerk",
                StartOffsetSeconds = 10, TravelLeadSeconds = 5, DurationSeconds = 30 }, "a", 0);
            world.StepOne(); // t=5, actor can plan travel for t=10
            Assert.IsTrue(world.Schedules.TryAccept(duty.Id, "a", out var reason));
            Assert.IsNull(reason);
            world.StepOne(); // t=10
            Assert.IsTrue(world.Schedules.Complete(duty.Id, "a"));
            for (var i = 0; i < 8; i++) world.StepOne();
            Assert.AreEqual("Completed", duty.State);
        }

        [Test]
        public void SameSeedKeepsDecisionStreamIndependentPerAgent()
        {
            var first = WorldWithAgents("a", "b");
            var second = WorldWithAgents("a", "b");
            var options = new List<GoalCandidate> {
                new GoalCandidate { Id = "rest", Need = 0.6f, Feasible = true },
                new GoalCandidate { Id = "chat", Social = 0.9f, Feasible = true }
            };
            first.Decisions.Select("b", options);
            var chosenFirst = first.Decisions.Select("a", options)?.Id;
            var chosenSecond = second.Decisions.Select("a", options)?.Id;
            Assert.AreEqual(chosenFirst, chosenSecond);
        }

        [Test]
        public void DecisionTraceOwnsCandidateCopiesAndRejectsDuplicateIds()
        {
            var world = WorldWithAgents("a");
            var source = new GoalCandidate { Id = "work", Duty = 1, Score = 0.99f,
                RejectionReason = "stale" };
            var options = new List<GoalCandidate> { source, new GoalCandidate { Id = "work", Duty = 0.2f } };
            Assert.AreEqual("work", world.Decisions.Select("a", options).Id);
            Assert.AreEqual(0.99f, source.Score);
            Assert.AreEqual("stale", source.RejectionReason);
            var trace = world.Decisions.LastTrace["a"];
            Assert.AreNotSame(source, trace.Candidates[0]);
            Assert.AreEqual("Missing or duplicate goal ID", trace.Candidates[1].RejectionReason);
            source.Duty = 0;
            Assert.AreEqual(1f, trace.Candidates[0].Duty);
        }

        [Test]
        public void CascadeBudgetPersistsAcrossTicksAndPauseFreezesTime()
        {
            var world = WorldWithAgents("a");
            world.MaxEventsPerRoot = 3;
            world.EventPublished += item => { if (item.Type == "cascade") world.Emit("cascade", "a", "a", null, parent: item); };
            world.Emit("cascade", "a", "a", null);
            world.StepOne(); world.StepOne(); world.StepOne(); world.StepOne();
            Assert.AreEqual(3, world.Trace.Count - 1); // agent.created plus three linked events
            Assert.IsTrue(world.Diagnostics.Exists(d => d.Code == "CASCADE_BUDGET"));
            var before = world.Time;
            world.Paused = true;
            world.Advance(500);
            Assert.AreEqual(before, world.Time);
            world.StepOne();
            Assert.AreEqual(before + world.TickSeconds, world.Time);
        }
    }
}
