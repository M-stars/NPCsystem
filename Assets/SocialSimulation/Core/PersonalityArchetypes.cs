using System;

namespace SocialSimulation.Core
{
    /// <summary>Authorable average traits and within-archetype correlated variation.</summary>
    [Serializable]
    public sealed class PersonalityArchetype
    {
        public string Id = "balanced";
        public PersonalityProfile Mean = new PersonalityProfile();
        public float StandardDeviation = 0.12f;
        public float SocialCoupling = 0.75f;
        public float DutyCoupling = 0.75f;
        public float ExplorationCoupling = 0.75f;

        public PersonalityArchetype Copy()
        {
            return new PersonalityArchetype { Id = Id, Mean = SnapshotClone.Copy(Mean),
                StandardDeviation = StandardDeviation, SocialCoupling = SocialCoupling,
                DutyCoupling = DutyCoupling, ExplorationCoupling = ExplorationCoupling };
        }
    }

    /// <summary>Stable per-agent sampling; no global random stream or rendering-frame dependency.</summary>
    public static class PersonalityGenerator
    {
        public static PersonalityProfile Generate(uint worldSeed, string agentId, PersonalityArchetype archetype = null)
        {
            if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("Agent ID is required.", nameof(agentId));
            archetype = archetype ?? new PersonalityArchetype();
            Validate(archetype);
            var state = DecisionService.SeedFor(worldSeed, "personality:" + archetype.Id + ":" + agentId);
            var social = Gaussian(ref state);
            var duty = Gaussian(ref state);
            var explore = Gaussian(ref state);
            var mean = archetype.Mean;
            var spread = archetype.StandardDeviation;
            return new PersonalityProfile
            {
                Extraversion = Trait(mean.Extraversion, spread, social, archetype.SocialCoupling, ref state),
                Cooperation = Trait(mean.Cooperation, spread, social, archetype.SocialCoupling, ref state),
                FriendshipValue = Trait(mean.FriendshipValue, spread, social, archetype.SocialCoupling, ref state),
                Conscientiousness = Trait(mean.Conscientiousness, spread, duty, archetype.DutyCoupling, ref state),
                RuleValue = Trait(mean.RuleValue, spread, duty, archetype.DutyCoupling, ref state),
                AchievementValue = Trait(mean.AchievementValue, spread, duty, archetype.DutyCoupling, ref state),
                Curiosity = Trait(mean.Curiosity, spread, explore, archetype.ExplorationCoupling, ref state),
                FreedomValue = Trait(mean.FreedomValue, spread, explore, archetype.ExplorationCoupling, ref state),
                Sensitivity = Trait(mean.Sensitivity, spread, 0, 0, ref state),
                StatusValue = Trait(mean.StatusValue, spread, 0, 0, ref state)
            };
        }

        public static void Validate(PersonalityArchetype value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.Id) || value.Mean == null ||
                !FiniteRange(value.StandardDeviation, 0, 0.5f) ||
                !FiniteRange(value.SocialCoupling, 0, 1) || !FiniteRange(value.DutyCoupling, 0, 1) ||
                !FiniteRange(value.ExplorationCoupling, 0, 1))
                throw new ArgumentException("Personality archetype has invalid ID, profile, spread or coupling.", nameof(value));
            var mean = value.Mean;
            if (!FiniteRange(mean.Extraversion, 0, 1) || !FiniteRange(mean.Conscientiousness, 0, 1) ||
                !FiniteRange(mean.Cooperation, 0, 1) || !FiniteRange(mean.Curiosity, 0, 1) ||
                !FiniteRange(mean.Sensitivity, 0, 1) || !FiniteRange(mean.RuleValue, 0, 1) ||
                !FiniteRange(mean.AchievementValue, 0, 1) || !FiniteRange(mean.FriendshipValue, 0, 1) ||
                !FiniteRange(mean.FreedomValue, 0, 1) || !FiniteRange(mean.StatusValue, 0, 1))
                throw new ArgumentException("Personality archetype contains an invalid mean trait.", nameof(value));
        }

        private static bool FiniteRange(float value, float min, float max) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;

        private static float Trait(float mean, float spread, double latent, float coupling, ref ulong state)
        {
            var independent = Math.Sqrt(Math.Max(0, 1 - coupling * coupling));
            return NeedState.Clamp01((float)(mean + spread * (coupling * latent + independent * Gaussian(ref state))));
        }

        private static double Uniform(ref ulong state)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return ((state >> 11) + 1.0) * (1.0 / (1UL << 53));
        }

        private static double Gaussian(ref ulong state)
        {
            var u1 = Uniform(ref state);
            var u2 = Uniform(ref state);
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
