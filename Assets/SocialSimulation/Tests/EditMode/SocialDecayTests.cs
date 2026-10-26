using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Tests
{
    /// <summary>Unreinforced relationships drift toward neutral; reinforcement resets the drift.</summary>
    public sealed class SocialDecayTests
    {
        private static SimulationWorld World()
        {
            var world = new SimulationWorld("social.decay", 83);
            world.Places.Add("square", new PlaceState { Id = "square", Capacity = 10 });
            foreach (var id in new[] { "a", "b", "c" }) world.AddAgent(new AgentState { Id = id, PlaceId = "square" });
            return world;
        }

        private static void AdvanceTo(SimulationWorld world, double seconds)
        {
            while (world.Time < seconds) world.StepOne();
        }

        private static SocialRelation Friend(SimulationWorld world, string source = "a", string target = "b")
        { return world.Society.Relate(source, target, "friend", 1, 0.8f); }

        [Test]
        public void UnreinforcedRelationshipsDriftTowardNeutralAfterTheGracePeriod()
        {
            var world = World();
            world.Society.ConfigureRelationDecay(100, 10);
            var friend = Friend(world);
            AdvanceTo(world, 10);
            Assert.AreEqual(1f, friend.Liking, 0.0001, "The grace period protects fresh relationships.");
            AdvanceTo(world, 35);
            // 25 unreinforced seconds of a 100 second half-life.
            Assert.That(friend.Liking, Is.EqualTo(0.8408f).Within(0.001));
            Assert.That(friend.Trust, Is.EqualTo(0.6726f).Within(0.001));
            Assert.AreEqual(0.05f, friend.Familiarity, 0.0001, "Familiarity never decays.");
            AdvanceTo(world, 235);
            Assert.That(friend.Liking, Is.EqualTo(0.2102f).Within(0.001));
            Assert.Greater(friend.Liking, 0f, "Decay approaches neutral without flipping the sign.");
        }

        [Test]
        public void ReinforcementResetsTheDriftAndDisabledDecayFreezesValues()
        {
            var world = World();
            world.Society.ConfigureRelationDecay(100, 10);
            var friend = Friend(world);
            AdvanceTo(world, 35);
            Assert.That(friend.Liking, Is.EqualTo(0.8408f).Within(0.001));
            world.Society.Relate("a", "b", "friend", 0, 0); // Renewing the tie resets the decay clock.
            AdvanceTo(world, 44);
            Assert.AreEqual(0.8408f, friend.Liking, 0.001, "A reinforced relationship enjoys a fresh grace period.");
            AdvanceTo(world, 47);
            Assert.That(friend.Liking, Is.EqualTo(0.8408f * Mathf.Pow(0.5f, 2f / 100f)).Within(0.001));
            world.Society.ConfigureRelationDecay(0, 0);
            var frozen = friend.Liking;
            AdvanceTo(world, 1000);
            Assert.AreEqual(frozen, friend.Liking, 0.0001, "A zero half-life disables decay entirely.");
            Assert.Throws<System.ArgumentException>(() => world.Society.ConfigureRelationDecay(-1, 0));
            Assert.Throws<System.ArgumentException>(() => world.Society.ConfigureRelationDecay(0.5, 0));
            Assert.Throws<System.ArgumentException>(() => world.Society.ConfigureRelationDecay(100, -1));
            Assert.Throws<System.ArgumentException>(() => world.Society.ConfigureRelationDecay(float.NaN, 0));
        }

        [Test]
        public void DecayedRelationshipsSurviveSnapshotAndContinueFromTheirRecordedDebt()
        {
            var world = World();
            world.Society.ConfigureRelationDecay(100, 10);
            var friend = Friend(world);
            AdvanceTo(world, 35);
            var restored = WorldPersistence.Restore(
                JsonUtility.FromJson<SimulationSnapshot>(JsonUtility.ToJson(WorldPersistence.Capture(world))));
            Assert.AreEqual(100, restored.Society.RelationDecayHalfLifeSeconds, "Decay settings belong to the snapshot.");
            var restoredFriend = restored.Society.Relations[friend.Id];
            Assert.AreEqual(friend.Liking, restoredFriend.Liking, 0.0001);
            Assert.AreEqual(friend.DecayedFor, restoredFriend.DecayedFor, 0.0001);
            AdvanceTo(restored, 45);
            // Continued decay from the recorded debt, not a fresh full-span decay from UpdatedAt.
            Assert.That(restoredFriend.Liking, Is.EqualTo(0.8408f * Mathf.Pow(0.5f, 10f / 100f)).Within(0.001));
            Assert.Greater(restoredFriend.Liking, 0.7f, "A restarted decay would have dropped far lower.");
        }
    }
}
