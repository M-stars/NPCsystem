using System;
using NUnit.Framework;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Tests
{
    public sealed class SaveSchemaVersionTests
    {
        [Test]
        public void SnapshotRejectsPriorSchemaAfterKnowledgeAndAgreementShapeChange()
        {
            var world = new SimulationWorld("schema.test");
            var snapshot = WorldPersistence.Capture(world);
            Assert.AreEqual(3, snapshot.SchemaVersion);

            var json = JsonUtility.ToJson(snapshot);
            StringAssert.Contains("\"Knowledge\":", json);
            StringAssert.Contains("\"AgreementSettlements\":", json);
            Assert.AreEqual("schema.test", WorldPersistence.Restore(JsonUtility.FromJson<SimulationSnapshot>(json)).WorldId);

            snapshot.SchemaVersion = 2;
            Assert.Throws<InvalidOperationException>(() => WorldPersistence.Restore(snapshot));
        }
    }
}
