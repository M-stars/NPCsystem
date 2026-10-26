using System;
using NUnit.Framework;
using SocialSimulation.Core;

namespace SocialSimulation.Tests
{
    public sealed class PersonalityArchetypeTests
    {
        [Test]
        public void SamplingIsStablePerAgentAndDoesNotMutateTheTemplate()
        {
            var archetype = new PersonalityArchetype { Id = "neighborhood", StandardDeviation = 0.18f };
            archetype.Mean.FriendshipValue = 0.8f;
            var first = PersonalityGenerator.Generate(177, "resident.01", archetype);
            PersonalityGenerator.Generate(177, "resident.02", archetype);
            var repeated = PersonalityGenerator.Generate(177, "resident.01", archetype);
            Assert.AreEqual(first.Extraversion, repeated.Extraversion);
            Assert.AreEqual(first.FriendshipValue, repeated.FriendshipValue);
            Assert.AreNotEqual(first.Extraversion, PersonalityGenerator.Generate(177, "resident.03", archetype).Extraversion);
            Assert.AreEqual(0.8f, archetype.Mean.FriendshipValue);
            Assert.That(first.FriendshipValue, Is.InRange(0f, 1f));
        }

        [Test]
        public void SharedSocialAndDutyFactorsProduceCorrelatedTraits()
        {
            const int count = 800;
            var archetype = new PersonalityArchetype { Id = "sample", StandardDeviation = 0.16f,
                SocialCoupling = 0.85f, DutyCoupling = 0.85f, ExplorationCoupling = 0.85f };
            var outgoing = new double[count];
            var friendship = new double[count];
            var diligent = new double[count];
            var dutiful = new double[count];
            for (var i = 0; i < count; i++)
            {
                var profile = PersonalityGenerator.Generate(19, "sample." + i, archetype);
                outgoing[i] = profile.Extraversion;
                friendship[i] = profile.FriendshipValue;
                diligent[i] = profile.Conscientiousness;
                dutiful[i] = profile.RuleValue;
            }
            Assert.Greater(Correlation(outgoing, friendship), 0.5);
            Assert.Greater(Correlation(diligent, dutiful), 0.5);
            Assert.Less(Math.Abs(Correlation(outgoing, diligent)), 0.18);
        }

        [Test]
        public void InvalidMeansSpreadAndCouplingCannotBeSampled()
        {
            var archetype = new PersonalityArchetype();
            archetype.Mean.Curiosity = float.NaN;
            Assert.Throws<ArgumentException>(() => PersonalityGenerator.Generate(1, "a", archetype));
            archetype.Mean.Curiosity = 0.5f;
            archetype.StandardDeviation = -0.01f;
            Assert.Throws<ArgumentException>(() => PersonalityGenerator.Generate(1, "a", archetype));
            archetype.StandardDeviation = 0.12f;
            archetype.DutyCoupling = 1.01f;
            Assert.Throws<ArgumentException>(() => PersonalityGenerator.Generate(1, "a", archetype));
        }

        private static double Correlation(double[] left, double[] right)
        {
            var count = left.Length;
            var sumLeft = 0.0;
            var sumRight = 0.0;
            for (var i = 0; i < count; i++) { sumLeft += left[i]; sumRight += right[i]; }
            var meanLeft = sumLeft / count;
            var meanRight = sumRight / count;
            var covariance = 0.0;
            var varianceLeft = 0.0;
            var varianceRight = 0.0;
            for (var i = 0; i < count; i++)
            {
                var a = left[i] - meanLeft;
                var b = right[i] - meanRight;
                covariance += a * b;
                varianceLeft += a * a;
                varianceRight += b * b;
            }
            return covariance / Math.Sqrt(varianceLeft * varianceRight);
        }
    }
}
