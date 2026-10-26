using System;

namespace SocialSimulation.Core
{
    public enum UnknownKnowledgePolicy { AllowAttempt, Reject, Penalize }
    public enum KnowledgeConditionState { Unknown, Matched, Contradicted, Conflicting, Invalid }

    /// <summary>A requirement on the actor's beliefs, not a predicate over objective world state.</summary>
    [Serializable]
    public sealed class GoalKnowledgeCondition
    {
        public string Topic;
        public string SubjectId;
        public string ExpectedValue;
        public string RequiredOriginSourceId;
        public float MinimumConfidence = 0.5f;
        public double MaximumAgeSeconds = double.MaxValue;
        public UnknownKnowledgePolicy UnknownPolicy = UnknownKnowledgePolicy.AllowAttempt;
        public float UnknownScorePenalty = 0.1f;

        public GoalKnowledgeCondition Copy() => (GoalKnowledgeCondition)MemberwiseClone();
    }

    [Serializable]
    public sealed class GoalKnowledgeAssessment
    {
        public string Topic;
        public string SubjectId;
        public KnowledgeConditionState State;
        public UnknownKnowledgePolicy UnknownPolicy;
        public bool Allowed;
        public float ScorePenalty;
        public string Reason;
        public KnowledgeRecord Evidence;
        public float CurrentConfidence;

        public GoalKnowledgeAssessment Copy()
        {
            var copy = (GoalKnowledgeAssessment)MemberwiseClone();
            copy.Evidence = SnapshotClone.Copy(Evidence);
            return copy;
        }
    }

    public static class GoalKnowledgeEvaluator
    {
        public static GoalKnowledgeAssessment Evaluate(ObserverKnowledge personal, double now, GoalKnowledgeCondition condition)
        {
            var result = new GoalKnowledgeAssessment { Topic = condition?.Topic, SubjectId = condition?.SubjectId,
                UnknownPolicy = condition?.UnknownPolicy ?? UnknownKnowledgePolicy.Reject };
            if (personal == null || condition == null || !SimulationWorld.Finite(now) ||
                string.IsNullOrWhiteSpace(condition.Topic) || condition.ExpectedValue == null ||
                !SimulationWorld.Finite(condition.MinimumConfidence) || condition.MinimumConfidence < 0 || condition.MinimumConfidence > 1 ||
                !SimulationWorld.Finite(condition.MaximumAgeSeconds) || condition.MaximumAgeSeconds < 0 ||
                !SimulationWorld.Finite(condition.UnknownScorePenalty) || condition.UnknownScorePenalty < 0 || condition.UnknownScorePenalty > 1 ||
                !Enum.IsDefined(typeof(UnknownKnowledgePolicy), condition.UnknownPolicy))
            { result.State = KnowledgeConditionState.Invalid; result.Reason = "Invalid knowledge condition"; return result; }

            KnowledgeRecord selected = null;
            float selectedConfidence = 0;
            bool conflict = false;
            foreach (var item in personal.Query(condition.Topic, condition.SubjectId))
            {
                // Protected memories can outlive a service announcement's validity, so a decision
                // independently checks the original expiration and age even if storage retains it.
                float confidence = item.ConfidenceAt(now);
                if (item.OccurredAt > now || now >= item.ExpiresAt || now - item.OccurredAt > condition.MaximumAgeSeconds ||
                    confidence < condition.MinimumConfidence || string.IsNullOrWhiteSpace(item.OriginSourceId) ||
                    (!string.IsNullOrEmpty(condition.RequiredOriginSourceId) && item.OriginSourceId != condition.RequiredOriginSourceId)) continue;
                if (selected == null || item.OccurredAt > selected.OccurredAt ||
                    (item.OccurredAt == selected.OccurredAt && confidence > selectedConfidence))
                { selected = item; selectedConfidence = confidence; conflict = false; }
                else if (item.OccurredAt == selected.OccurredAt && confidence == selectedConfidence && item.Value != selected.Value)
                    conflict = true;
            }
            result.Evidence = selected;
            result.CurrentConfidence = selectedConfidence;
            if (selected == null || conflict)
            {
                result.State = conflict ? KnowledgeConditionState.Conflicting : KnowledgeConditionState.Unknown;
                result.Allowed = condition.UnknownPolicy != UnknownKnowledgePolicy.Reject;
                result.ScorePenalty = condition.UnknownPolicy == UnknownKnowledgePolicy.Penalize ? condition.UnknownScorePenalty : 0;
                result.Reason = conflict ? "Equally recent and credible personal reports conflict" : "No valid personal report meets the condition";
                return result;
            }
            result.Allowed = string.Equals(selected.Value, condition.ExpectedValue, StringComparison.Ordinal);
            result.State = result.Allowed ? KnowledgeConditionState.Matched : KnowledgeConditionState.Contradicted;
            result.Reason = result.Allowed ? "Latest eligible personal report matches" : "Latest eligible personal report contradicts the goal";
            return result;
        }
    }
}
