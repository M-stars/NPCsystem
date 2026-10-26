using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using SocialSimulation.Core;

namespace SocialSimulation.Tests
{
    public sealed class CoreInvariantRegressionTests
    {
        private sealed class CallbackSystem : IWorldSystem
        {
            public int Order { get; set; }
            public Action<SimulationWorld> Callback;
            public void Tick(SimulationWorld world, double deltaSeconds) { Callback(world); }
        }

        private static SimulationWorld World(params string[] agents)
        {
            var world = new SimulationWorld("invariants", 71);
            world.Places.Add("hall", new PlaceState { Id = "hall", Tag = "corridor", Capacity = 8 });
            foreach (var id in agents) world.AddAgent(new AgentState { Id = id, PlaceId = "hall" });
            return world;
        }

        private static void Seat(SimulationWorld world, string id, int capacity = 1)
        { world.Resources.Slots.Add(id, new ResourceSlot { Id = id, PlaceId = "hall", Capacity = capacity }); }

        [Test]
        public void MemoryCapacityKeepsProtectedEntriesAndDoesNotAliasIncomingReports()
        {
            var actor = new AgentState();
            var reported = new MemoryEvent { EventId = "event-one", Confidence = 0.2f, Protected = true };
            Assert.IsTrue(actor.Remember(reported, 1));
            reported.Confidence = 1;
            Assert.AreEqual(0.2f, actor.Memories[0].Confidence);
            Assert.IsFalse(actor.Remember(new MemoryEvent { EventId = "event-two", Protected = true }, 1));
            Assert.AreEqual(1, actor.Memories.Count);
            Assert.AreEqual("event-one", actor.Memories[0].EventId);
        }

        [Test]
        public void ScheduleRoleQualificationIsRestrictedToItsCollectiveAndObject()
        {
            var world = World("a");
            world.Society.Collectives.Add("shop-a", new CollectiveState { Id = "shop-a" });
            world.Society.Collectives.Add("shop-b", new CollectiveState { Id = "shop-b" });
            world.Society.Join("a", "shop-a"); world.Society.Join("a", "shop-b");
            world.Society.AssignRole("a", "shop-a", "manager", "dept-a");
            var duty = world.Schedules.Create(new ScheduleDefinition { Id = "inspect", DomainId = "trade", ActionId = "inspect",
                RequiredRoleId = "manager", RequiredCollectiveId = "shop-b", RequiredObjectScopeId = "dept-b",
                StartOffsetSeconds = 0, LateGraceSeconds = 10, DurationSeconds = 30 }, "a", 0);
            Assert.IsFalse(world.Schedules.TryAccept(duty.Id, "a", out _));
            world.Society.AssignRole("a", "shop-b", "manager", "dept-wrong");
            Assert.IsFalse(world.Schedules.TryAccept(duty.Id, "a", out _));
            world.Society.AssignRole("a", "shop-b", "manager", "dept-b");
            Assert.IsTrue(world.Schedules.TryAccept(duty.Id, "a", out _));
        }

        [Test]
        public void SystemsCanDetachDuringTickWithoutCallingRemovedOrNewlyAddedSystems()
        {
            var world = World("a"); var calls = new List<string>();
            var later = new CallbackSystem { Order = 1, Callback = w => calls.Add("later") };
            var added = new CallbackSystem { Order = 1, Callback = w => calls.Add("added") };
            CallbackSystem first = null;
            first = new CallbackSystem { Order = 0, Callback = w => {
                calls.Add("first"); w.RemoveSystem(later); w.RemoveSystem(first); w.AddSystem(added);
            } };
            world.AddSystem(first); world.AddSystem(later);
            world.StepOne();
            CollectionAssert.AreEqual(new[] { "first" }, calls);
            world.StepOne();
            CollectionAssert.AreEqual(new[] { "first", "added" }, calls);
        }

        [Test]
        public void CancelScopeCannotGrantItsOwnPendingRequestDuringCleanup()
        {
            var world = World("a", "b");
            Seat(world, "seat");
            world.Resources.TryAcquire("seat", "shared", "a", 100);
            var waiting = world.Resources.Request("seat", "shared", "b", 100, 10);
            Assert.AreEqual("Waiting", waiting.Status);
            world.Resources.ReleaseScope("shared");
            Assert.AreEqual("Cancelled", waiting.Status);
            Assert.AreEqual(0, world.Resources.Tokens.Count);
        }

        [Test]
        public void WaitersKeepTheirRequestedLeaseAcrossReleaseAndSnapshot()
        {
            var world = World("a", "b", "c");
            Seat(world, "first"); Seat(world, "second");
            var first = world.Resources.TryAcquire("first", "holder", "a", 100);
            var second = world.Resources.TryAcquire("second", "holder", "a", 100);
            var shortWait = world.Resources.Request("first", "short", "b", 100, 7);
            var longWait = world.Resources.Request("second", "long", "c", 100, 91);
            var restored = WorldPersistence.Restore(WorldPersistence.Capture(world));
            restored.Resources.Release(first.Id, "holder");
            restored.Resources.Release(second.Id, "holder");
            Assert.AreEqual(7, restored.Resources.Tokens[restored.Resources.Requests[shortWait.Id].TokenId].ExpiresAt);
            Assert.AreEqual(91, restored.Resources.Tokens[restored.Resources.Requests[longWait.Id].TokenId].ExpiresAt);
        }

        [Test]
        public void RemoveAgentKeepsOtherMembersSharedScopeResourcesAndCancelsItsWaiter()
        {
            var world = World("a", "b", "c");
            Seat(world, "shared-seat", 2); Seat(world, "busy-seat");
            world.Resources.TryAcquire("shared-seat", "session", "a", 100);
            var other = world.Resources.TryAcquire("shared-seat", "session", "b", 100);
            var busy = world.Resources.TryAcquire("busy-seat", "busy", "c", 100);
            var pending = world.Resources.Request("busy-seat", "only-waiting", "a", 100, 20);
            world.RemoveAgent("a");
            Assert.IsTrue(world.Resources.Tokens.ContainsKey(other.Id));
            Assert.AreEqual("Cancelled", pending.Status);
            world.Resources.Release(busy.Id, "busy");
            Assert.AreEqual(1, world.Resources.Tokens.Count);
        }

        [Test]
        public void AtomicAcquireIsIdempotentAndRejectsInvalidLeaseWithoutPartialTokens()
        {
            var world = World("a"); Seat(world, "one"); Seat(world, "two");
            var first = world.Resources.TryAcquire("one", "scope", "a", 20);
            var both = world.Resources.TryAcquireMany(new[] { "two", "one" }, "scope", "a", 30);
            Assert.NotNull(both);
            Assert.AreEqual(first.Id, both[0].Id);
            Assert.AreEqual(2, world.Resources.Tokens.Count);
            world.Resources.ReleaseScope("scope");
            Assert.IsNull(world.Resources.TryAcquireMany(new[] { "one", "two" }, "scope", "a", double.NaN));
            Assert.AreEqual(0, world.Resources.Tokens.Count);
        }

        [Test]
        public void CompletedActivityRejectsLateResourceTransfer()
        {
            var world = World("host", "late"); Seat(world, "seat");
            var session = world.Activities.Create("meeting", "host", "hall", 0, 10, 30, 1, 2);
            world.Activities.Invite(session.Id, "late"); world.Activities.Respond(session.Id, "late", true);
            world.Agents["late"].PlaceId = "away";
            Assert.IsTrue(world.Activities.Start(session.Id));
            Assert.IsTrue(world.Activities.Complete(session.Id, "done"));
            var token = world.Resources.TryAcquire("seat", "late-action", "late", 60);
            Assert.IsFalse(world.Activities.AttachResource(session.Id, "late", token.Id, "late-action"));
            Assert.AreEqual("late-action", token.OwnerScopeId);
        }

        [Test]
        public void LeavingSocialActivityBelowMinimumCancelsAndReleasesRemainingTokens()
        {
            var world = World("a", "b"); Seat(world, "seat", 2);
            var session = world.Activities.Create("chat", "a", "hall", 0, 10, 60, 2, 2);
            world.Activities.Invite(session.Id, "b"); world.Activities.Respond(session.Id, "b", true);
            var first = world.Resources.TryAcquire("seat", "a-action", "a", 60);
            var second = world.Resources.TryAcquire("seat", "b-action", "b", 60);
            world.Activities.AttachResource(session.Id, "a", first.Id, "a-action");
            world.Activities.AttachResource(session.Id, "b", second.Id, "b-action");
            Assert.IsTrue(world.Activities.Start(session.Id));
            world.Activities.Leave(session.Id, "a");
            Assert.AreEqual("Cancelled", session.State);
            Assert.AreEqual(0, world.Resources.Tokens.Count);
            Assert.IsFalse(world.Activities.Complete(session.Id, "invalid success"));
        }

        [Test]
        public void DelegatedAuthorityExpiresWithAncestorAndCannotBeRenewedByDelegation()
        {
            var world = World("manager", "deputy", "worker");
            world.Society.Collectives.Add("shop", new CollectiveState { Id = "shop" });
            var parent = world.Society.Grant("shop", "manager", "inspect", "shop", durationSeconds: 2, canDelegate: true);
            var child = world.Society.Grant("manager", "deputy", "inspect", "shop", canDelegate: true, parentGrantId: parent.Id);
            var grandchild = world.Society.Grant("deputy", "worker", "inspect", "shop", parentGrantId: child.Id);
            Assert.AreEqual(parent.EndsAt, grandchild.EndsAt);
            Assert.IsTrue(world.Society.HasAuthority("worker", "inspect", "shop"));
            world.StepOne(); world.StepOne();
            Assert.IsFalse(world.Society.HasAuthority("worker", "inspect", "shop"));
            Assert.Throws<InvalidOperationException>(() => world.Society.Grant("manager", "worker", "inspect", "shop", parentGrantId: parent.Id));
        }

        [Test]
        public void RevokingOneRoleRevokesOnlyItsBoundAuthorityAndAllDelegates()
        {
            var world = World("manager", "deputy", "worker");
            world.Society.Collectives.Add("shop", new CollectiveState { Id = "shop" });
            world.Society.Join("manager", "shop");
            var manager = world.Society.AssignRole("manager", "shop", "manager");
            var auditor = world.Society.AssignRole("manager", "shop", "auditor");
            var parent = world.Society.Grant("shop", "manager", "inspect", "shop", canDelegate: true, roleAssignmentId: manager.Id);
            var child = world.Society.Grant("manager", "deputy", "inspect", "shop", canDelegate: true, parentGrantId: parent.Id);
            world.Society.Grant("deputy", "worker", "inspect", "shop", parentGrantId: child.Id);
            world.Society.Grant("shop", "manager", "audit", "shop", roleAssignmentId: auditor.Id);
            world.Society.RevokeRole(manager.Id);
            Assert.IsFalse(world.Society.HasAuthority("worker", "inspect", "shop"));
            Assert.IsFalse(world.Society.HasAuthority("manager", "inspect", "shop"));
            Assert.IsTrue(world.Society.HasAuthority("manager", "audit", "shop"));
        }

        [Test]
        public void NormJurisdictionAndPublishedVersionRemainIndependentFromTemplateEdits()
        {
            var world = World("runner", "observer");
            world.Society.Collectives.Add("school", new CollectiveState { Id = "school" });
            world.Society.Collectives.Add("shop", new CollectiveState { Id = "shop" });
            world.Places["hall"].OwnerCollectiveId = "school";
            world.Places.Add("shop-floor", new PlaceState { Id = "shop-floor", Tag = "corridor", OwnerCollectiveId = "shop" });
            var template = new NormDefinition { Id = "quiet", Version = "1", ActionId = "run", CollectiveId = "school", PlaceTag = "corridor" };
            world.Norms.Definitions.Add(template.Id, template);
            var norm = world.Norms.Publish(template.Id, "school", "school");
            world.Norms.Inform("runner", norm.Id, "notice");
            Assert.Greater(world.Norms.KnownPenalty("runner", "run", "hall"), 0);
            Assert.AreEqual(0, world.Norms.KnownPenalty("runner", "run", "shop-floor"));
            template.Version = "2"; template.ActionId = "walk";
            Assert.Greater(world.Norms.KnownPenalty("runner", "run", "hall"), 0);
            Assert.AreEqual(0, world.Norms.KnownPenalty("runner", "walk", "hall"));
            var restored = WorldPersistence.Restore(WorldPersistence.Capture(world));
            Assert.Greater(restored.Norms.KnownPenalty("runner", "run", "hall"), 0);
        }

        [Test]
        public void ExplicitNormOverrideNeedsKnowledgeForDecisionButPreventsOldPunishment()
        {
            var world = World("runner", "observer");
            world.Society.Collectives.Add("school", new CollectiveState { Id = "school" });
            world.Places["hall"].OwnerCollectiveId = "school";
            world.Norms.Definitions.Add("old", new NormDefinition { Id = "old", Version = "1", ActionId = "run", CollectiveId = "school" });
            var old = world.Norms.Publish("old", "school", "school");
            world.Norms.Inform("runner", old.Id, "notice");
            world.Norms.Definitions.Add("new", new NormDefinition { Id = "new", Version = "1", ActionId = "run", Kind = "Require", CollectiveId = "school", OverridesNormId = "old" });
            var replacement = world.Norms.Publish("new", "school", "school");
            Assert.Greater(world.Norms.KnownPenalty("runner", "run", "hall"), 0);
            var evidence = world.Norms.Observe(world.Emit("run", "runner", "runner", null, "hall"), "observer");
            Assert.IsNull(world.Norms.RecordViolation(old.Id, evidence.Id));
            world.Norms.Inform("runner", replacement.Id, "notice");
            Assert.Less(world.Norms.KnownPenalty("runner", "run", "hall"), 0);
        }

        [Test]
        public void SnapshotRetainsRuntimeConfigurationAndOwnsRestoredEventData()
        {
            var world = World("a");
            world.MaxEventsPerRoot = 3; world.MaxCascadeDepth = 2; world.TraceCapacity = 19;
            world.Decisions.Temperature = 0.25f; world.Decisions.MinimumCommitmentSeconds = 42;
            world.Schedule(1, "scheduled", "a", "a", payload: "original");
            world.Emit("pending", "a", "a", "original");
            var snapshot = WorldPersistence.Capture(world);
            var restored = WorldPersistence.Restore(snapshot);
            snapshot.ScheduledEvents[0].Payload = "changed";
            snapshot.PendingEvents[snapshot.PendingEvents.Count - 1].Payload = "changed";
            var seen = new List<string>();
            restored.EventPublished += e => { if (e.Type == "pending" || e.Type == "scheduled") seen.Add(e.Payload); };
            restored.StepOne();
            CollectionAssert.AreEqual(new[] { "original", "original" }, seen);
            Assert.AreEqual(3, restored.MaxEventsPerRoot); Assert.AreEqual(2, restored.MaxCascadeDepth);
            Assert.AreEqual(19, restored.TraceCapacity); Assert.AreEqual(0.25f, restored.Decisions.Temperature);
            Assert.AreEqual(42, restored.Decisions.MinimumCommitmentSeconds);
        }

        [Test]
        public void NonFiniteClockInputIsRejectedAndSnapshotsRequireTickBoundary()
        {
            var world = World("a");
            Assert.Throws<ArgumentOutOfRangeException>(() => world.Advance(double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.Schedule(double.NaN, "event", "a", "a"));
            world.EventPublished += e => {
                Assert.Throws<InvalidOperationException>(() => WorldPersistence.Capture(world));
                Assert.Throws<InvalidOperationException>(() => world.StepOne());
            };
            world.StepOne();
            Assert.IsFalse(world.IsStepping);
            var snapshot = WorldPersistence.Capture(world); snapshot.TickSeconds = double.NaN;
            Assert.Throws<InvalidOperationException>(() => WorldPersistence.Restore(snapshot));
        }

        [Test]
        public void EffectIdempotencyAlsoRejectsReentrantCommit()
        {
            var world = World("a"); int effects = 0;
            Assert.IsTrue(world.TryCommitEffect("once", () => {
                effects++;
                Assert.IsFalse(world.TryCommitEffect("once", () => effects++));
            }));
            Assert.AreEqual(1, effects);
            Assert.IsFalse(world.TryCommitEffect("once", () => effects++));
        }

        [Test]
        public void JsonWorldRoundTripKeepsOptionalNormJurisdictionAndPublicationIdempotency()
        {
            var world = World("a");
            world.Norms.Definitions.Add("global", new NormDefinition { Id = "global", Version = "1", ActionId = "run" });
            world.Norms.Definitions.Add("unpublished", new NormDefinition { Id = "unpublished", Version = "1", ActionId = "walk" });
            var published = world.Norms.Publish("global", "a", null);
            var restored = WorldPersistence.Restore(UnityEngine.JsonUtility.FromJson<SimulationSnapshot>(
                UnityEngine.JsonUtility.ToJson(WorldPersistence.Capture(world))));
            Assert.AreEqual(published.Id, restored.Norms.Publish("global", "a", null).Id);
            Assert.AreEqual(published.Id, restored.Norms.Publish("global", "a", string.Empty).Id);
            Assert.AreEqual(1, restored.Norms.Instances.Count);
            var newlyPublished = restored.Norms.Publish("unpublished", "a", null);
            Assert.NotNull(newlyPublished);
            Assert.AreEqual(2, restored.Norms.Instances.Count);
            restored.Society.Collectives.Add("shop", new CollectiveState { Id = "shop" });
            Assert.NotNull(restored.Norms.Publish("unpublished", "a", "shop"));
            Assert.AreEqual(3, restored.Norms.Instances.Count);
        }

        [Test]
        public void JsonWorldRoundTripKeepsUnparentedSchedulesIndependentAndCausalSchedulesLinked()
        {
            var world = World("a");
            world.Schedule(3, "independent", "a", "a");
            var root = world.Emit("cause", "a", "a", null);
            world.Schedule(4, "delayed-child", "a", "a", parent: root);
            var duty = world.Schedules.Create(new ScheduleDefinition { Id = "scheduled-duty", DomainId = "test",
                ActionId = "test.work", StartOffsetSeconds = 5, LateGraceSeconds = 3, DurationSeconds = 20 }, "a", 0);
            world.StepOne();
            var json = UnityEngine.JsonUtility.ToJson(WorldPersistence.Capture(world));
            var restored = WorldPersistence.Restore(UnityEngine.JsonUtility.FromJson<SimulationSnapshot>(json));
            WorldEvent independent = null, child = null;
            restored.EventPublished += item => {
                if (item.Type == "independent") independent = item;
                if (item.Type == "delayed-child") child = item;
            };
            for (int i = 0; i < 4; i++) restored.StepOne();
            Assert.NotNull(independent);
            Assert.AreEqual(independent.Id, independent.RootCauseId);
            Assert.IsTrue(string.IsNullOrEmpty(independent.ParentEventId));
            Assert.NotNull(child);
            Assert.AreEqual(root.Id, child.ParentEventId);
            Assert.AreEqual(root.RootCauseId, child.RootCauseId);
            Assert.AreEqual(1, child.Depth);
            Assert.AreEqual("Due", restored.Schedules.Assignments[duty.Id].State);
            Assert.IsFalse(restored.Diagnostics.Exists(d => d.Code == "CASCADE_CLOSED"));
        }

        [Test]
        public void JsonWorldRoundTripCannotResetDelayedCauseBudget()
        {
            var world = World("a"); world.MaxEventsPerRoot = 2;
            var root = world.Emit("cause", "a", "a", null);
            world.Schedule(4, "last-child", "a", "a", parent: root);
            world.StepOne();
            var restored = WorldPersistence.Restore(UnityEngine.JsonUtility.FromJson<SimulationSnapshot>(
                UnityEngine.JsonUtility.ToJson(WorldPersistence.Capture(world))));
            restored.EventPublished += item => {
                if (item.Type == "last-child") Assert.IsNull(restored.Emit("too-many", "a", "a", null, parent: item));
            };
            restored.StepOne(); restored.StepOne(); restored.StepOne();
            Assert.IsTrue(restored.Diagnostics.Exists(d => d.Code == "CASCADE_BUDGET"));
        }

        [Test]
        public void EffectServicesInheritCauseForEventsSchedulesAndNestedCommands()
        {
            var world = World("a"); world.MaxEffectsPerRoot = 2;
            var root = world.Emit("cause", "a", "a", null);
            WorldEvent nested = null;
            Assert.IsTrue(world.TryCommitEffect("outer", () => {
                Assert.Throws<InvalidOperationException>(() => WorldPersistence.Capture(world));
                Assert.Throws<InvalidOperationException>(() => world.Advance(1));
                Assert.Throws<InvalidOperationException>(() => world.StepOne());
                world.CreateTask("a", "issuer", "inspect", "hall", 10);
                var scheduled = world.Schedule(3, "follow-up", "a", "a");
                Assert.IsTrue(scheduled.HasParent);
                Assert.AreEqual(root.RootCauseId, scheduled.Parent.RootCauseId);
                Assert.IsTrue(world.TryCommitEffect("inner", () => nested = world.Emit("nested", "a", "a", null)));
                Assert.IsFalse(world.TryCommitEffect("over-budget", () => Assert.Fail("Effect budget must apply to nested commands.")));
            }, root));
            Assert.AreEqual(root.RootCauseId, nested.RootCauseId);
            var taskEvent = world.Trace.Find(e => e.Type == "task.created");
            Assert.NotNull(taskEvent);
            Assert.AreEqual(root.RootCauseId, taskEvent.RootCauseId);
            Assert.AreEqual(root.Id, taskEvent.ParentEventId);
            var independent = world.Emit("external", "a", "a", null);
            Assert.AreEqual(independent.Id, independent.RootCauseId);
        }

        [Test]
        public void ThrowingEffectRestoresOuterCauseAndDoesNotLeakToExternalEvents()
        {
            var world = World("a");
            var root = world.Emit("cause", "a", "a", null);
            var second = world.Emit("second-cause", "a", "a", null);
            WorldEvent afterNested = null;
            Assert.Throws<InvalidOperationException>(() => world.TryCommitEffect("outer", () => {
                Assert.Throws<InvalidOperationException>(() => world.TryCommitEffect("inner", () => {
                    throw new InvalidOperationException("nested failure");
                }, second));
                afterNested = world.Emit("after-nested", "a", "a", null);
                throw new InvalidOperationException("outer failure");
            }, root));
            Assert.AreEqual(root.RootCauseId, afterNested.RootCauseId);
            var outside = world.Emit("external", "a", "a", null);
            Assert.AreEqual(outside.Id, outside.RootCauseId);
        }

        [Test]
        public void DelayedCausalEventsPreserveBudgetAcrossIdleTicksAndSnapshot()
        {
            var world = World("a"); world.MaxEventsPerRoot = 2;
            var root = world.Emit("root", "a", "a", null);
            Assert.NotNull(world.Schedule(5, "delayed", "a", "a", parent: root));
            world.StepOne(); world.StepOne();
            var restored = WorldPersistence.Restore(WorldPersistence.Capture(world));
            restored.EventPublished += e => { if (e.Type == "delayed") Assert.IsNull(restored.Emit("overflow", "a", "a", null, parent: e)); };
            restored.StepOne(); restored.StepOne(); restored.StepOne();
            Assert.IsTrue(restored.Diagnostics.Exists(d => d.Code == "CASCADE_BUDGET"));
            Assert.IsNull(restored.Emit("reopen", "a", "a", null, parent: root));
            Assert.IsTrue(restored.Diagnostics.Exists(d => d.Code == "CASCADE_CLOSED"));
        }

        [Test]
        public void RootEffectBudgetAndDeduplicationSurviveSnapshot()
        {
            var world = World("a"); world.MaxEffectsPerRoot = 1;
            var root = world.Emit("root", "a", "a", null);
            int committed = 0;
            Assert.IsTrue(world.TryCommitEffect("first", () => committed++, root));
            var restored = WorldPersistence.Restore(WorldPersistence.Capture(world));
            Assert.AreEqual(1, restored.MaxEffectsPerRoot);
            Assert.IsFalse(restored.TryCommitEffect("first", () => committed++, root));
            Assert.IsFalse(restored.TryCommitEffect("second", () => committed++, root));
            Assert.AreEqual(1, committed);
            Assert.IsTrue(restored.Diagnostics.Exists(d => d.Code == "CASCADE_EFFECT_BUDGET"));
        }

        [Test]
        public void RegisteredPackageFingerprintIsCultureIndependent()
        {
            var registry = new ContentRegistry();
            var manifest = new PackageManifest { Id = "body", Version = "1", Kind = "NPC", Body = new BodyProfile { Id = "body", HeightMeters = 1.75f } };
            Assert.IsTrue(registry.Register(manifest).Success);
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                var english = registry.GetPackageFingerprint(manifest.Id);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Assert.AreEqual(english, registry.GetPackageFingerprint(manifest.Id));
                Assert.IsTrue(registry.Register(manifest).AlreadyRegistered);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }
    }
}
