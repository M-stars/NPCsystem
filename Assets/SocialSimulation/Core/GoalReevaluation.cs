using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    /// <summary>
    /// One domain-registered reaction to a personal knowledge change. The handler re-runs the
    /// actor's own goal selection; it never receives objective world state.
    /// </summary>
    public sealed class GoalReevaluationRule
    {
        public string AgentId;
        public string Topic;
        public string SubjectId;
        public Action Handler;
    }

    /// <summary>
    /// Event-triggered goal re-evaluation. When an agent's personal knowledge of a registered
    /// topic changes, a deduplicated scheduled event re-runs that agent's registered selector on
    /// the next tick. The service holds no per-agent mutable state, so a restored world only needs
    /// the domain modules to register their rules again; pending events survive the snapshot.
    /// </summary>
    public sealed class GoalReevaluationService
    {
        public const string DueEventType = "goal.reevaluation.due";
        public const string ServiceEmitterId = "goal.reevaluation";
        private const char KeySeparator = '|';
        private const string Wildcard = "*";

        private readonly SimulationWorld world;
        private readonly Dictionary<string, GoalReevaluationRule> rules =
            new Dictionary<string, GoalReevaluationRule>(StringComparer.Ordinal);

        public GoalReevaluationService(SimulationWorld simulation)
        {
            world = simulation ?? throw new ArgumentNullException(nameof(simulation));
            world.EventPublished += OnWorldEvent;
        }

        public int RuleCount => rules.Count;

        /// <summary>Register or replace the rule for (agent, topic, subject). Null subject matches every subject.</summary>
        public void Register(string agentId, string topic, string subjectId, Action handler)
        {
            if (string.IsNullOrWhiteSpace(agentId) || !world.Agents.ContainsKey(agentId))
                throw new ArgumentException("Goal re-evaluation requires a live agent.", nameof(agentId));
            if (string.IsNullOrWhiteSpace(topic) || handler == null ||
                ContainsKeySeparator(agentId) || ContainsKeySeparator(topic) ||
                (!string.IsNullOrEmpty(subjectId) && ContainsKeySeparator(subjectId)))
                throw new ArgumentException("Goal re-evaluation rule is invalid.");
            var key = RuleKey(agentId, topic, subjectId);
            rules[key] = new GoalReevaluationRule { AgentId = agentId, Topic = topic, SubjectId = subjectId, Handler = handler };
        }

        public bool Unregister(string agentId, string topic, string subjectId)
        { return rules.Remove(RuleKey(agentId, topic, subjectId)); }

        public bool HasRule(string agentId, string topic, string subjectId)
        { return rules.ContainsKey(RuleKey(agentId, topic, subjectId)); }

        /// <summary>True while a deduplicated re-evaluation for this rule waits in the scheduled queue.</summary>
        public bool IsPending(string agentId, string topic, string subjectId)
        {
            var key = RuleKey(agentId, topic, subjectId);
            foreach (var item in world.ScheduledEvents)
                if (item.Type == DueEventType && item.EmitterId == ServiceEmitterId && item.Payload == key) return true;
            return false;
        }

        private void OnWorldEvent(WorldEvent item)
        {
            if (item.Type == DueEventType)
            {
                if (item.EmitterId == ServiceEmitterId && rules.TryGetValue(item.Payload ?? string.Empty, out var rule) &&
                    world.Agents.ContainsKey(rule.AgentId))
                    rule.Handler();
                return;
            }
            if (item.Type == "knowledge.forgotten")
            {
                // The record is already gone, so the event carries its topic and subject.
                if (!KnowledgeService.TryParseForgottenPayload(item.Payload, out var topic, out var subjectId, out _)) return;
                foreach (var pair in rules)
                {
                    var rule = pair.Value;
                    if (rule.AgentId != item.SubjectId || rule.Topic != topic) continue;
                    if (rule.SubjectId != null && rule.SubjectId != subjectId) continue;
                    Schedule(rule);
                }
                return;
            }
            if (item.Type != "knowledge.learned" && item.Type != "knowledge.updated") return;
            // The knowledge event only carries the origin event ID; resolve topic and subject
            // through the learner's personal record so registration matches what was learned.
            if (!world.Knowledge.TryGet(item.SubjectId, item.Payload, out var record)) return;
            var agentId = item.SubjectId;
            foreach (var pair in rules)
            {
                var rule = pair.Value;
                if (rule.AgentId != agentId || rule.Topic != record.Topic) continue;
                if (rule.SubjectId != null && rule.SubjectId != record.SubjectId) continue;
                Schedule(rule);
            }
        }

        private void Schedule(GoalReevaluationRule rule)
        {
            var key = RuleKey(rule.AgentId, rule.Topic, rule.SubjectId);
            // One pending event per rule keeps a multi-record burst to a single re-evaluation.
            foreach (var item in world.ScheduledEvents)
                if (item.Type == DueEventType && item.EmitterId == ServiceEmitterId && item.Payload == key) return;
            // Rules outlive their agent: a forgotten event can still be in flight while the
            // agent is removed mid-dispatch, and the scheduled event needs its place.
            if (!world.Agents.TryGetValue(rule.AgentId, out var agent)) return;
            world.Schedule(world.Time, DueEventType, ServiceEmitterId, rule.AgentId,
                agent.PlaceId, key);
        }

        private static bool ContainsKeySeparator(string value) => value != null && value.IndexOf(KeySeparator) >= 0;

        private static string RuleKey(string agentId, string topic, string subjectId)
        { return agentId + KeySeparator + topic + KeySeparator + (string.IsNullOrEmpty(subjectId) ? Wildcard : subjectId); }
    }
}
