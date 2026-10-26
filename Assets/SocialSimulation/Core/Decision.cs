using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class GoalCandidate
    {
        public string Id;
        public string Kind;
        public string TargetId;
        public string ActionId;
        public string PlaceId;
        public bool Feasible = true;
        public bool Urgent;
        public double Deadline = double.MaxValue;
        public float Duty;
        public float Need;
        public float PersonalityFit;
        public float Social;
        public float Commitment;
        public float TimeCost;
        public float EnergyCost;
        public float ExpectedConsequence;
        public float Score;
        public string RejectionReason;
        public List<GoalKnowledgeCondition> KnowledgeConditions = new List<GoalKnowledgeCondition>();
        public List<GoalKnowledgeAssessment> KnowledgeAssessments = new List<GoalKnowledgeAssessment>();

        public GoalCandidate Copy()
        {
            var copy = new GoalCandidate { Id = Id, Kind = Kind, TargetId = TargetId, ActionId = ActionId,
                PlaceId = PlaceId, Feasible = Feasible, Urgent = Urgent, Deadline = Deadline,
                Duty = Duty, Need = Need, PersonalityFit = PersonalityFit, Social = Social,
                Commitment = Commitment, TimeCost = TimeCost, EnergyCost = EnergyCost,
                ExpectedConsequence = ExpectedConsequence, Score = Score, RejectionReason = RejectionReason };
            if (KnowledgeConditions != null)
                foreach (var condition in KnowledgeConditions) copy.KnowledgeConditions.Add(condition?.Copy());
            if (KnowledgeAssessments != null)
                foreach (var assessment in KnowledgeAssessments) copy.KnowledgeAssessments.Add(assessment?.Copy());
            return copy;
        }
    }

    [Serializable]
    public sealed class DecisionTrace
    {
        public string AgentId;
        public double Time;
        public string SelectedGoalId;
        public List<GoalCandidate> Candidates = new List<GoalCandidate>();
    }

    public sealed class DecisionService
    {
        private readonly SimulationWorld world;
        private readonly Dictionary<string, double> failedUntil = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, DecisionTrace> LastTrace = new Dictionary<string, DecisionTrace>(StringComparer.Ordinal);
        public double MinimumCommitmentSeconds { get; set; } = 12;
        public float SwitchThreshold { get; set; } = 0.12f;
        public float Temperature { get; set; } = 0.08f;

        internal DecisionService(SimulationWorld world) { this.world = world; }

        public static ulong SeedFor(uint worldSeed, string agentId)
        {
            ulong hash = 1469598103934665603UL ^ worldSeed;
            foreach (char c in agentId) { hash ^= c; hash *= 1099511628211UL; }
            return hash == 0 ? 1UL : hash;
        }

        private static double Next(ref ulong state)
        {
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            return (state >> 11) * (1.0 / (1UL << 53));
        }

        public void RecordFailure(string agentId, string goalId, double backoffSeconds)
        { failedUntil[agentId + ":" + goalId] = world.Time + Math.Max(1, backoffSeconds); }

        public GoalCandidate Select(string agentId, IList<GoalCandidate> candidates)
        {
            if (!world.Agents.TryGetValue(agentId, out var agent)) return null;
            var trace = new DecisionTrace { AgentId = agentId, Time = world.Time };
            LastTrace[agentId] = trace;
            var eligible = new List<GoalCandidate>();
            GoalCandidate current = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (candidates == null) return null;
            var personalKnowledge = world.Knowledge.ForAgent(agentId);
            foreach (var supplied in candidates)
            {
                if (supplied == null) continue;
                var candidate = supplied.Copy();
                candidate.Score = 0;
                candidate.RejectionReason = null;
                candidate.KnowledgeAssessments.Clear();
                trace.Candidates.Add(candidate);
                if (string.IsNullOrWhiteSpace(candidate.Id) || !seen.Add(candidate.Id))
                { candidate.RejectionReason = "Missing or duplicate goal ID"; continue; }
                if (!candidate.Feasible || world.Time > candidate.Deadline)
                { candidate.RejectionReason = "Infeasible or deadline passed"; continue; }
                if (failedUntil.TryGetValue(agentId + ":" + candidate.Id, out var until) && world.Time < until)
                { candidate.RejectionReason = "Failure cooldown"; continue; }
                float knowledgePenalty = 0;
                foreach (var condition in candidate.KnowledgeConditions)
                {
                    var assessment = GoalKnowledgeEvaluator.Evaluate(personalKnowledge, world.Time, condition);
                    candidate.KnowledgeAssessments.Add(assessment);
                    knowledgePenalty += assessment.ScorePenalty;
                    if (!assessment.Allowed && candidate.RejectionReason == null)
                        candidate.RejectionReason = "Knowledge: " + assessment.Reason + " (" + assessment.Topic + ")";
                }
                if (candidate.RejectionReason != null) continue;
                var raw = 0.25f * candidate.Duty + 0.2f * candidate.Need + 0.15f * candidate.PersonalityFit +
                    0.12f * candidate.Social + 0.18f * candidate.Commitment - 0.04f * candidate.TimeCost -
                    0.03f * candidate.EnergyCost - 0.15f * candidate.ExpectedConsequence;
                raw -= world.Norms.KnownPenalty(agentId, candidate.ActionId, candidate.PlaceId);
                raw -= knowledgePenalty;
                candidate.Score = Math.Max(0, Math.Min(1, raw));
                eligible.Add(candidate);
                if (candidate.Id == agent.CurrentGoalId) current = candidate;
            }
            if (eligible.Count == 0) return null;
            eligible.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            GoalCandidate urgent = null;
            foreach (var candidate in eligible)
                if (candidate.Urgent && (urgent == null || candidate.Deadline < urgent.Deadline)) urgent = candidate;
            GoalCandidate chosen;
            if (urgent != null) chosen = urgent;
            else if (current != null && world.Time < agent.GoalCommittedUntil) chosen = current;
            else
            {
                float highest = 0;
                foreach (var candidate in eligible) highest = Math.Max(highest, candidate.Score);
                if (current != null && highest - current.Score < SwitchThreshold) chosen = current;
                else
                {
                    double total = 0;
                    var weights = new double[eligible.Count];
                    for (int i = 0; i < eligible.Count; i++)
                    { weights[i] = Math.Exp((eligible[i].Score - highest) / Math.Max(0.001, Temperature)); total += weights[i]; }
                    double draw = Next(ref agent.DecisionRandomState) * total;
                    chosen = eligible[eligible.Count - 1];
                    for (int i = 0; i < eligible.Count; i++)
                    { draw -= weights[i]; if (draw <= 0) { chosen = eligible[i]; break; } }
                }
            }
            if (chosen.Id != agent.CurrentGoalId)
            { agent.CurrentGoalId = chosen.Id; agent.GoalCommittedUntil = world.Time + MinimumCommitmentSeconds; }
            trace.SelectedGoalId = chosen.Id;
            return chosen;
        }

        internal Dictionary<string, double> CopyCooldowns() { return new Dictionary<string, double>(failedUntil, StringComparer.Ordinal); }
        internal void RestoreCooldowns(Dictionary<string, double> values)
        { failedUntil.Clear(); foreach (var item in values) failedUntil.Add(item.Key, item.Value); LastTrace.Clear(); }
    }
}
