using System;
using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Tests
{
    public sealed class KnowledgeTests
    {
        private static SimulationWorld World()
        {
            var world = new SimulationWorld("knowledge.test", 71);
            foreach (var id in new[] { "a", "b", "c", "d" })
                world.AddAgent(new AgentState { Id = id, PlaceId = "hall" });
            world.Places.Add("hall", new PlaceState { Id = "hall", Capacity = 20 });
            world.Places.Add("other", new PlaceState { Id = "other", Capacity = 20 });
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "talk", RequiresSamePlace = true,
                TransmissionConfidence = 0.8f, RelayCooldownSeconds = 10 });
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "radio", TransmissionConfidence = 0.8f });
            return world;
        }

        private static WorldEvent Event(SimulationWorld world, string value = "closed")
        { return world.Emit("shop.state", "a", "shop", value, "hall"); }

        private static void AddOrganization(SimulationWorld world)
        {
            world.Society.Collectives.Add("org", new CollectiveState { Id = "org" });
            world.Society.Join("a", "org"); world.Society.Join("b", "org");
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "members", CollectiveId = "org",
                Audience = KnowledgeAudience.CollectiveMembers });
        }

        [Test]
        public void UnobservedWorldEventsAndNormsAreNotAutomaticallyPersonalKnowledge()
        {
            var world = World();
            var occurred = Event(world);
            world.Norms.Definitions.Add("rule", new NormDefinition { Id = "rule", Version = "1", ActionId = "shop.state" });
            var norm = world.Norms.Publish("rule", "a", null);
            Assert.IsTrue(world.Norms.IsApplicable(norm, "shop.state", "hall", world.Time));
            Assert.IsFalse(world.Knowledge.ForAgent("b").TryGet(occurred.Id, out _));
            Assert.AreEqual(0, world.Knowledge.Query("b").Count);
            Assert.AreEqual(0, world.Norms.KnownPenalty("b", "shop.state", "hall"));
        }

        [Test]
        public void ObservationRequiresCanonicalRecentEventAndCurrentLogicalPlace()
        {
            var world = World(); var occurred = Event(world);
            world.Agents["b"].PlaceId = "other";
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Observe("b", occurred));
            var forgery = new WorldEvent { Id = occurred.Id, Type = occurred.Type, SubjectId = occurred.SubjectId,
                EmitterId = occurred.EmitterId, Time = occurred.Time, PlaceId = occurred.PlaceId, Payload = "fabricated" };
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Observe("a", forgery));
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Observe("a", occurred));
            world.Advance(2);
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Observe("c", occurred));
        }

        [Test]
        public void CyclicRumorKeepsOneOriginalEventAndCannotAmplifyConfidenceOrCreateEvidence()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred, halfLifeSeconds: 0);
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Relay("a", "b", occurred.Id, "talk"));
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Relay("b", "c", occurred.Id, "talk"));
            Assert.AreEqual(KnowledgeDelivery.Unchanged, world.Knowledge.Relay("c", "a", occurred.Id, "talk"));
            Assert.IsTrue(world.Knowledge.TryGet("c", occurred.Id, out var heard));
            Assert.AreEqual("a", heard.OriginSourceId); Assert.AreEqual("b", heard.LastSourceId);
            Assert.AreEqual(2, heard.RelayDepth); Assert.IsFalse(heard.DirectlyObserved);
            Assert.That(heard.ConfidenceAt(world.Time), Is.EqualTo(0.64f).Within(0.00001));
            Assert.AreEqual(1, world.Knowledge.Query("a").Count);
            Assert.IsTrue(world.Knowledge.TryGet("a", occurred.Id, out var direct));
            Assert.AreEqual(1, direct.Confidence); Assert.IsTrue(direct.DirectlyObserved);
            Assert.AreEqual(0, world.Norms.Evidence.Count); Assert.AreEqual(0, world.Norms.Violations.Count);
        }

        [Test]
        public void IndependentTransmissionPathsOfSameEventAreNotIndependentEvidence()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred, halfLifeSeconds: 0);
            world.Knowledge.Relay("a", "b", occurred.Id, "talk");
            world.Knowledge.Relay("a", "c", occurred.Id, "talk");
            Assert.AreEqual(KnowledgeDelivery.Unchanged, world.Knowledge.Relay("c", "b", occurred.Id, "talk"));
            Assert.AreEqual(1, world.Knowledge.Query("b").Count);
            world.Knowledge.TryGet("b", occurred.Id, out var belief);
            Assert.That(belief.Confidence, Is.EqualTo(0.8f).Within(0.00001));
        }

        [Test]
        public void HigherQualityReportCanReplaceWeakerSameOriginWithoutCreatingAnotherItem()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred, halfLifeSeconds: 0);
            world.Knowledge.Relay("a", "b", occurred.Id, "talk");
            world.Knowledge.Relay("b", "c", occurred.Id, "talk");
            Assert.AreEqual(KnowledgeDelivery.Updated, world.Knowledge.Relay("a", "c", occurred.Id, "talk"));
            Assert.AreEqual(1, world.Knowledge.Query("c").Count);
            world.Knowledge.TryGet("c", occurred.Id, out var belief);
            Assert.That(belief.Confidence, Is.EqualTo(0.8f).Within(0.00001));
            Assert.IsFalse(belief.DirectlyObserved);
        }

        [Test]
        public void DifferentEventsRemainDistinctEvenWhenTheirTopicAndStatementAreEqual()
        {
            var world = World();
            world.Knowledge.Observe("a", Event(world));
            world.Knowledge.Observe("a", Event(world));
            Assert.AreEqual(2, world.Knowledge.Query("a", "shop.state", "shop").Count);
            Assert.AreEqual(0, world.Knowledge.Query("a", "shop.state", "different").Count);
        }

        [Test]
        public void CooldownUsesSimulationTimeAndCannotBeBypassedBySwitchingChannel()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred);
            world.Knowledge.Relay("a", "b", occurred.Id, "talk");
            Assert.AreEqual(KnowledgeDelivery.Cooldown, world.Knowledge.Relay("a", "b", occurred.Id, "radio"));
            world.Paused = true; world.Advance(100);
            Assert.AreEqual(KnowledgeDelivery.Cooldown, world.Knowledge.Relay("a", "b", occurred.Id, "talk"));
            world.Paused = false; world.Advance(10);
            Assert.AreEqual(KnowledgeDelivery.Unchanged, world.Knowledge.Relay("a", "b", occurred.Id, "talk"));
        }

        [Test]
        public void RelayDoesNotResetDecayOrExpiration()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred, retentionSeconds: 20, halfLifeSeconds: 10);
            world.Advance(10);
            world.Knowledge.Relay("a", "b", occurred.Id, "talk");
            world.Knowledge.TryGet("b", occurred.Id, out var heard);
            Assert.AreEqual(0, heard.DecayFromTime); Assert.AreEqual(20, heard.ExpiresAt);
            Assert.That(heard.ConfidenceAt(world.Time), Is.EqualTo(0.4f).Within(0.00001));
            world.Advance(10);
            Assert.IsFalse(world.Knowledge.TryGet("a", occurred.Id, out _));
            Assert.IsFalse(world.Knowledge.TryGet("b", occurred.Id, out _));
        }

        [Test]
        public void LowConfidenceKnowledgeIsForgottenBeforeRetentionDeadline()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.ForgetBelowConfidence = 0.2f;
            world.Knowledge.Observe("a", occurred, retentionSeconds: 1000, halfLifeSeconds: 10);
            world.Advance(20); Assert.IsTrue(world.Knowledge.TryGet("a", occurred.Id, out _));
            world.Advance(10); Assert.IsFalse(world.Knowledge.TryGet("a", occurred.Id, out _));
            Assert.AreEqual(0, world.Knowledge.Capture().Agents.Count);
        }

        [Test]
        public void TickTimeForgettingEmitsTypedEventsWhileProtectedMemoriesStaySilent()
        {
            var world = World(); var expiring = Event(world); var kept = Event(world);
            Assert.AreEqual(KnowledgeDelivery.Learned,
                world.Knowledge.Observe("a", expiring, retentionSeconds: 5, halfLifeSeconds: 0));
            Assert.AreEqual(KnowledgeDelivery.Learned,
                world.Knowledge.Observe("a", kept, retentionSeconds: 5, halfLifeSeconds: 0, isProtected: true));
            world.Advance(6);
            Assert.IsFalse(world.Knowledge.TryGet("a", expiring.Id, out _));
            Assert.IsTrue(world.Knowledge.TryGet("a", kept.Id, out _), "Protected history is never forgotten.");
            WorldEvent forgotten = null;
            foreach (var item in world.Trace)
                if (item.Type == "knowledge.forgotten" && item.SubjectId == "a") forgotten = item;
            Assert.IsNotNull(forgotten, "Expiry is a real belief change and must be observable.");
            Assert.IsTrue(KnowledgeService.TryParseForgottenPayload(forgotten.Payload, out var topic, out var subjectId, out var origin));
            Assert.AreEqual("shop.state", topic);
            Assert.AreEqual("shop", subjectId);
            Assert.AreEqual(expiring.Id, origin);
            foreach (var item in world.Trace)
                if (item.Type == "knowledge.forgotten")
                    Assert.AreNotEqual(kept.Id, KnowledgeService.TryParseForgottenPayload(item.Payload, out _, out _, out var id) ? id : null,
                        "Protected memories leave without an event.");
        }

        [Test]
        public void ProtectedKnowledgeSurvivesForgettingButCannotGrowPastCapacity()
        {
            var world = World(); world.Knowledge.CapacityPerAgent = 1;
            var important = Event(world);
            world.Knowledge.Observe("a", important, retentionSeconds: 5, halfLifeSeconds: 1, isProtected: true);
            Assert.AreEqual(KnowledgeDelivery.Capacity, world.Knowledge.Observe("a", Event(world), isProtected: true));
            world.Advance(20);
            Assert.IsTrue(world.Knowledge.TryGet("a", important.Id, out _));
            Assert.AreEqual(1, world.Knowledge.Query("a").Count);
        }

        [Test]
        public void CapacityEvictsLowestSalienceUnprotectedInformation()
        {
            var world = World(); world.Knowledge.CapacityPerAgent = 2;
            var weak = Event(world); var strong = Event(world); var newest = Event(world);
            world.Knowledge.Observe("a", weak, 0.2f);
            world.Knowledge.Observe("a", strong, 0.9f, emotionalWeight: 0.5f);
            world.Knowledge.Observe("a", newest);
            Assert.IsFalse(world.Knowledge.TryGet("a", weak.Id, out _));
            Assert.IsTrue(world.Knowledge.TryGet("a", strong.Id, out _));
            Assert.IsTrue(world.Knowledge.TryGet("a", newest.Id, out _));
        }

        [Test]
        public void ChannelsAndPersonalQueriesReturnIndependentCopies()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred);
            world.Knowledge.TryGetChannel("talk", out var channel); channel.Active = false;
            Assert.IsTrue(world.Knowledge.CanReceive("a", "talk"));
            var view = world.Knowledge.ForAgent("a");
            view.TryGet(occurred.Id, out var copy); copy.Value = "tampered";
            view.Query()[0].Confidence = 0;
            view.TryGet(occurred.Id, out var stillOriginal);
            Assert.AreEqual("closed", stillOriginal.Value); Assert.AreEqual(1, stillOriginal.Confidence);
        }

        [Test]
        public void MemberChannelsExcludeOutsidersAndImmediatelyRespectLeaving()
        {
            var world = World(); AddOrganization(world);
            var bulletin = world.Knowledge.Announce("members", "a", "shift", "org", "morning");
            Assert.AreEqual(2, bulletin.LearnedCount);
            Assert.IsTrue(world.Knowledge.TryGet("b", bulletin.OriginEventId, out _));
            Assert.IsFalse(world.Knowledge.TryGet("c", bulletin.OriginEventId, out _));
            world.Society.Leave("b", "org");
            Assert.IsFalse(world.Knowledge.CanReceive("b", "members"));
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("a", "b", bulletin.OriginEventId, "members"));
            // Departure withdraws the channel entitlement, not memories already legitimately acquired.
            Assert.IsTrue(world.Knowledge.TryGet("b", bulletin.OriginEventId, out _));
        }

        [Test]
        public void RoleChannelsRequireChannelAssignmentAndCurrentMembershipAndRoleWindow()
        {
            var world = World(); AddOrganization(world);
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "staff", CollectiveId = "org",
                Audience = KnowledgeAudience.AssignedRoles, RequiredRoleId = "clerk" });
            var role = world.Society.AssignRole("a", "org", "clerk");
            Assert.IsFalse(world.Knowledge.CanReceive("a", "staff"));
            role.InformationChannels.Add("staff"); role.EndsAt = 3;
            Assert.IsTrue(world.Knowledge.CanReceive("a", "staff"));
            var wrongRole = world.Society.AssignRole("b", "org", "visitor"); wrongRole.InformationChannels.Add("staff");
            Assert.IsFalse(world.Knowledge.CanReceive("b", "staff"));
            world.Advance(3);
            Assert.IsFalse(world.Knowledge.CanReceive("a", "staff"));
        }

        [Test]
        public void RoleInformationChannelPreservesAssignedObjectScope()
        {
            var world = World(); AddOrganization(world);
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "care.child1", CollectiveId = "org",
                Audience = KnowledgeAudience.AssignedRoles, RequiredRoleId = "guardian", ObjectScopeId = "child1" });
            var otherChild = world.Society.AssignRole("a", "org", "guardian", "child2");
            otherChild.InformationChannels.Add("care.child1");
            Assert.IsFalse(world.Knowledge.CanReceive("a", "care.child1"));
            var correct = world.Society.AssignRole("a", "org", "guardian", "child1");
            correct.InformationChannels.Add("care.child1");
            Assert.IsTrue(world.Knowledge.CanReceive("a", "care.child1"));
        }

        [Test]
        public void OfficialChannelPublicationAndRelayNeedLiveScopedAuthority()
        {
            var world = World(); AddOrganization(world);
            world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "official", CollectiveId = "org",
                Audience = KnowledgeAudience.CollectiveMembers, PublisherAuthorityAction = "notice.publish" });
            Assert.IsNull(world.Knowledge.Announce("official", "a", "work", "org", "open"));
            var grant = world.Society.Grant("org", "a", "notice.publish", "org");
            var message = world.Knowledge.Announce("official", "a", "work", "org", "open");
            Assert.IsNotNull(message);
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("b", "a", message.OriginEventId, "official"));
            world.Society.RevokeGrant(grant.Id);
            Assert.IsNull(world.Knowledge.Announce("official", "a", "work", "org", "late"));
        }

        [Test]
        public void ConfidentialInformationCannotBeLaunderedThroughPublicChannel()
        {
            var world = World(); AddOrganization(world);
            var notice = world.Knowledge.Announce("members", "a", "private.plan", "org", "secret");
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("a", "c", notice.OriginEventId, "radio"));
            world.Society.Join("c", "org");
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Relay("a", "c", notice.OriginEventId, "radio"));
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("c", "d", notice.OriginEventId, "radio"));
            world.Society.DissolveCollective("org");
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("a", "b", notice.OriginEventId, "radio"));
        }

        [Test]
        public void ConversationRequiresSameNonemptyPlaceWhileRadioIsExplicitlyRemote()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred); world.Agents["b"].PlaceId = "other";
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("a", "b", occurred.Id, "talk"));
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Relay("a", "b", occurred.Id, "radio"));
            world.Agents["a"].PlaceId = null; world.Agents["c"].PlaceId = null;
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("a", "c", occurred.Id, "talk"));
        }

        [Test]
        public void SystemNoticeRequiresRegisteredChannelAndCannotInventAnActiveSource()
        {
            var world = World(); AddOrganization(world);
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.DeliverSystemNotice("b", "norm/1", "norm", "org", "v1", "org", "missing"));
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.DeliverSystemNotice("b", "norm/1", "norm", "org", "v1", "missing", "members"));
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.DeliverSystemNotice("b", "norm/1", "norm", "org", "v1", "org", "members"));
            Assert.IsFalse(world.Knowledge.Query("b")[0].DirectlyObserved);
            Assert.AreEqual(0, world.Norms.Evidence.Count);
            Assert.AreEqual(KnowledgeDelivery.Invalid, world.Knowledge.DeliverSystemNotice("b", "norm/1", "norm", "org", "changed", "org", "members"));
        }

        [Test]
        public void RepeatedSystemNoticeCannotRenewAgeConfidenceOrExpiration()
        {
            var world = World();
            world.Knowledge.DeliverSystemNotice("b", "message/1", "notice", "shop", "closed", "a", "radio",
                retentionSeconds: 20, halfLifeSeconds: 10, isProtected: false);
            world.Advance(10);
            Assert.AreEqual(KnowledgeDelivery.Unchanged, world.Knowledge.DeliverSystemNotice("b", "message/1", "notice", "shop", "closed", "a", "radio",
                retentionSeconds: 100, halfLifeSeconds: 10, isProtected: false));
            world.Knowledge.TryGet("b", "message/1", out var known);
            Assert.AreEqual(20, known.ExpiresAt); Assert.AreEqual(0, known.DecayFromTime);
            Assert.That(known.ConfidenceAt(world.Time), Is.EqualTo(0.5f).Within(0.00001));
        }

        [Test]
        public void CooldownCapacityCannotBeEvictedToBypassOutstandingLimits()
        {
            var world = World(); world.Knowledge.MaximumCooldownsPerAgent = 1;
            var occurred = Event(world); world.Knowledge.Observe("a", occurred);
            world.Knowledge.Relay("a", "b", occurred.Id, "talk");
            Assert.AreEqual(KnowledgeDelivery.Capacity, world.Knowledge.Relay("a", "c", occurred.Id, "talk"));
            Assert.AreEqual(KnowledgeDelivery.Cooldown, world.Knowledge.Relay("a", "b", occurred.Id, "talk"));
            world.Advance(10);
            Assert.AreEqual(KnowledgeDelivery.Learned, world.Knowledge.Relay("a", "c", occurred.Id, "talk"));
        }

        [Test]
        public void RelayDepthIsFiniteAndUnknownInformationCannotBeRelayed()
        {
            var world = World(); world.Knowledge.MaximumRelayDepth = 1;
            var occurred = Event(world); world.Knowledge.Observe("a", occurred);
            Assert.AreEqual(KnowledgeDelivery.Unknown, world.Knowledge.Relay("c", "d", occurred.Id, "talk"));
            world.Knowledge.Relay("a", "b", occurred.Id, "talk");
            Assert.AreEqual(KnowledgeDelivery.Denied, world.Knowledge.Relay("b", "c", occurred.Id, "talk"));
        }

        [Test]
        public void WholeWorldJsonRoundTripPreservesProvenanceAndOutstandingCooldown()
        {
            var world = World(); AddOrganization(world);
            var occurred = Event(world); world.Knowledge.Observe("a", occurred, retentionSeconds: 50, halfLifeSeconds: 20);
            world.Knowledge.Relay("a", "b", occurred.Id, "talk"); world.Advance(3);
            var json = JsonUtility.ToJson(WorldPersistence.Capture(world));
            var restored = WorldPersistence.Restore(JsonUtility.FromJson<SimulationSnapshot>(json));
            Assert.AreEqual(KnowledgeDelivery.Cooldown, restored.Knowledge.Relay("a", "b", occurred.Id, "talk"));
            Assert.IsTrue(restored.Knowledge.TryGet("b", occurred.Id, out var known));
            Assert.AreEqual("a", known.OriginSourceId); Assert.AreEqual(1, known.RelayDepth);
            Assert.AreEqual(50, known.ExpiresAt); Assert.AreEqual(20, known.ConfidenceHalfLifeSeconds);
            Assert.IsTrue(restored.Knowledge.CanReceive("a", "members"));
            restored.Advance(7);
            Assert.AreEqual(KnowledgeDelivery.Unchanged, restored.Knowledge.Relay("a", "b", occurred.Id, "talk"));
        }

        [Test]
        public void SnapshotCaptureAndRestoreOwnTheirDataAndInvalidRestoreIsAtomic()
        {
            var world = World(); var occurred = Event(world); world.Knowledge.Observe("a", occurred);
            var snapshot = world.Knowledge.Capture();
            world.Knowledge.Restore(snapshot);
            snapshot.Agents[0].Records[0].Value = "mutated";
            world.Knowledge.TryGet("a", occurred.Id, out var unchanged); Assert.AreEqual("closed", unchanged.Value);
            var invalid = world.Knowledge.Capture(); invalid.Agents[0].Records[0].Confidence = float.NaN;
            Assert.Throws<InvalidOperationException>(() => world.Knowledge.Restore(invalid));
            Assert.IsTrue(world.Knowledge.TryGet("a", occurred.Id, out _));
            Assert.IsTrue(world.Knowledge.CanReceive("b", "talk"));
            Assert.Throws<InvalidOperationException>(() => world.Knowledge.Restore(null));
        }

        [Test]
        public void RemovingAgentClearsOwnKnowledgeAndCooldownButRetainsOthersSourceHistory()
        {
            var world = World(); var occurred = Event(world);
            world.Knowledge.Observe("a", occurred); world.Knowledge.Relay("a", "b", occurred.Id, "talk");
            world.RemoveAgent("a");
            Assert.IsFalse(world.Knowledge.TryGet("a", occurred.Id, out _));
            Assert.IsTrue(world.Knowledge.TryGet("b", occurred.Id, out var remembered));
            Assert.AreEqual("a", remembered.OriginSourceId);
            Assert.AreEqual(0, world.Knowledge.Capture().Cooldowns.Count);
            var restored = WorldPersistence.Restore(JsonUtility.FromJson<SimulationSnapshot>(JsonUtility.ToJson(WorldPersistence.Capture(world))));
            Assert.IsTrue(restored.Knowledge.TryGet("b", occurred.Id, out _));
        }

        [Test]
        public void InvalidNumericValuesCannotCreateImmortalOrAmplifyingInformation()
        {
            var world = World(); var occurred = Event(world);
            Assert.AreEqual(KnowledgeDelivery.Invalid, world.Knowledge.Observe("a", occurred, float.NaN));
            Assert.AreEqual(KnowledgeDelivery.Invalid, world.Knowledge.Observe("a", occurred, retentionSeconds: double.PositiveInfinity));
            Assert.AreEqual(KnowledgeDelivery.Invalid, world.Knowledge.Observe("a", occurred, halfLifeSeconds: -1));
            Assert.Throws<ArgumentException>(() => world.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "amplifier", TransmissionConfidence = 1.1f }));
            Assert.AreEqual(0, world.Knowledge.Query("a").Count);
        }
    }
}
