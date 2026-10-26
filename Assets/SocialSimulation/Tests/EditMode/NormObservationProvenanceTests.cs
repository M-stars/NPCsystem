using NUnit.Framework;
using SocialSimulation.Core;

namespace SocialSimulation.Tests
{
    public sealed class NormObservationProvenanceTests
    {
        private static SimulationWorld World(string id = "norm.provenance")
        {
            var world = new SimulationWorld(id, 7);
            world.Places.Add("hall", new PlaceState { Id = "hall", Tag = "corridor", Capacity = 10 });
            world.AddAgent(new AgentState { Id = "runner", PlaceId = "hall" });
            world.AddAgent(new AgentState { Id = "observer", PlaceId = "hall" });
            world.AddAgent(new AgentState { Id = "remote", PlaceId = "elsewhere" });
            world.Norms.Definitions.Add("no-run", new NormDefinition
            { Id = "no-run", Version = "1", ActionId = "run", PlaceTag = "corridor" });
            world.Norms.Publish("no-run", "observer", null);
            return world;
        }

        [Test]
        public void UnknownOrAlteredEventCannotCreatePunishableEvidence()
        {
            var world = World();
            var forged = new WorldEvent { Id = "invented", Time = world.Time, Type = "run",
                EmitterId = "runner", SubjectId = "runner", PlaceId = "hall" };
            Assert.IsNull(world.Norms.Observe(forged, "observer"));

            var walking = world.Emit("walk", "runner", "runner", "walking", "hall");
            var altered = new WorldEvent { Id = walking.Id, RootCauseId = walking.RootCauseId,
                ParentEventId = walking.ParentEventId, Time = walking.Time, Type = "run",
                EmitterId = walking.EmitterId, SubjectId = walking.SubjectId, PlaceId = walking.PlaceId,
                Payload = walking.Payload, Depth = walking.Depth };
            Assert.IsNull(world.Norms.Observe(altered, "observer"));
            Assert.AreEqual(0, world.Norms.Evidence.Count);
            Assert.AreEqual(0, world.Norms.Violations.Count);

            var running = world.Emit("run", "runner", "runner", null, "hall");
            var evidence = world.Norms.Observe(running, "observer");
            Assert.NotNull(evidence);
            Assert.AreEqual(running.Id, evidence.EventId);
            Assert.AreEqual(1, world.Norms.Instances.Count);
            foreach (var norm in world.Norms.Instances.Values)
                Assert.NotNull(world.Norms.RecordViolation(norm.Id, evidence.Id));
        }

        [Test]
        public void ForeignOrHistoricalEventCannotBeObservedAsCurrentEvidence()
        {
            var world = World();
            var other = World("different.world");
            var foreign = other.Emit("run", "runner", "runner", null, "hall");
            Assert.IsNull(world.Norms.Observe(foreign, "observer"));

            var old = world.Emit("run", "runner", "runner", null, "hall");
            world.StepOne();
            world.StepOne();
            Assert.IsNull(world.Norms.Observe(old, "observer"));
            Assert.AreEqual(0, world.Norms.Evidence.Count);
        }

        [Test]
        public void CanonicalEventStillRequiresRealSubjectColocationAndFiniteConfidence()
        {
            var world = World();
            var running = world.Emit("run", "runner", "runner", null, "hall");
            Assert.IsNull(world.Norms.Observe(running, "remote"));
            Assert.IsNull(world.Norms.Observe(running, "runner"));
            Assert.IsNull(world.Norms.Observe(running, "observer", float.NaN));
            Assert.IsNull(world.Norms.Observe(running, "observer", float.PositiveInfinity));
            var unknownSubject = world.Emit("run", "unknown", "unknown", null, "hall");
            Assert.IsNull(world.Norms.Observe(unknownSubject, "observer"));
            Assert.AreEqual(0, world.Norms.Evidence.Count);
        }
    }
}
