using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class NormDefinition
    {
        public string Id;
        public string Version;
        public string Kind = "Prohibit"; // Prohibit, Require, Prefer
        public string ActionId;
        public string PlaceTag;
        public string CollectiveId;
        public string ExceptionEventType;
        public string OverridesNormId;
        public bool Formal;
        public double ActiveFrom;
        public double ActiveUntil = double.MaxValue;
        public string EvidenceRequirement = "DirectWitness";
        public string FirstResponseTask = "warn";
        public string RepeatResponseTask = "report";
        public string SubjectScope = "MembersOrJurisdiction"; // Members, Jurisdiction, MembersOrJurisdiction
    }

    [Serializable]
    public sealed class NormInstance
    {
        public string Id;
        public string DefinitionId;
        public string Version;
        public string IssuerId;
        public string CollectiveId;
        public double PublishedAt;
        public bool Active = true;
        public NormDefinition PublishedDefinition;
    }

    [Serializable]
    public sealed class ObservationEvidence
    {
        public string Id;
        public string EventId;
        public string ObserverId;
        public string SubjectId;
        public string ActionId;
        public string PlaceId;
        public double Time;
        public float Confidence;
        public string Channel = "DirectWitness";
    }

    [Serializable]
    public sealed class ViolationRecord
    {
        public string Id;
        public string NormInstanceId;
        public string ActionEventId;
        public string SubjectId;
        public string PlaceId;
        public double Time;
        public int ObservationCount;
        public bool Processed;
        public List<string> EvidenceIds = new List<string>();
    }

    public sealed class NormService
    {
        private readonly SimulationWorld world;
        public readonly Dictionary<string, NormDefinition> Definitions = new Dictionary<string, NormDefinition>(StringComparer.Ordinal);
        public readonly Dictionary<string, NormInstance> Instances = new Dictionary<string, NormInstance>(StringComparer.Ordinal);
        public readonly Dictionary<string, ObservationEvidence> Evidence = new Dictionary<string, ObservationEvidence>(StringComparer.Ordinal);
        public readonly Dictionary<string, ViolationRecord> Violations = new Dictionary<string, ViolationRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> violationKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        internal NormService(SimulationWorld world) { this.world = world; }

        public NormInstance Publish(string definitionId, string issuerId, string collectiveId)
        {
            if (!Definitions.TryGetValue(definitionId, out var definition)) throw new InvalidOperationException("Unknown norm definition.");
            if (string.IsNullOrWhiteSpace(definition.Version) || string.IsNullOrWhiteSpace(definition.ActionId) ||
                (definition.Kind != "Prohibit" && definition.Kind != "Require" && definition.Kind != "Prefer") ||
                !SimulationWorld.Finite(definition.ActiveFrom) || !SimulationWorld.Finite(definition.ActiveUntil) ||
                definition.ActiveUntil <= definition.ActiveFrom ||
                (definition.SubjectScope != "Members" && definition.SubjectScope != "Jurisdiction" && definition.SubjectScope != "MembersOrJurisdiction"))
                throw new InvalidOperationException("Invalid norm version, action, kind, time window, or subject scope.");
            if (!string.IsNullOrEmpty(collectiveId) &&
                (!world.Society.Collectives.TryGetValue(collectiveId, out var collective) || !collective.Active))
                throw new InvalidOperationException("Norm requires an active jurisdiction collective.");
            if (definition.Formal && !world.Society.HasAuthority(issuerId, "policy.publish", collectiveId))
                throw new InvalidOperationException("Issuer lacks scoped policy authority.");
            if (!string.IsNullOrEmpty(definition.CollectiveId) && definition.CollectiveId != collectiveId)
                throw new InvalidOperationException("Norm jurisdiction differs from requested collective.");
            var overrides = new HashSet<string>(StringComparer.Ordinal) { definitionId };
            var next = definition.OverridesNormId;
            while (!string.IsNullOrEmpty(next))
            {
                if (!overrides.Add(next) || !Definitions.TryGetValue(next, out var ancestor) ||
                    ancestor.ActionId != definition.ActionId || (ancestor.Formal && !definition.Formal))
                    throw new InvalidOperationException("Norm override requires compatible definitions without a cycle.");
                next = ancestor.OverridesNormId;
            }
            foreach (var existing in Instances.Values)
                if (existing.Active && existing.DefinitionId == definitionId && existing.Version == definition.Version &&
                    string.Equals(existing.CollectiveId ?? string.Empty, collectiveId ?? string.Empty,
                        StringComparison.Ordinal)) return existing;
            var instance = new NormInstance { Id = world.NextId(), DefinitionId = definitionId, Version = definition.Version,
                IssuerId = issuerId, CollectiveId = collectiveId, PublishedAt = world.Time,
                PublishedDefinition = SnapshotClone.Copy(definition) };
            Instances.Add(instance.Id, instance);
            world.Emit("norm.published", issuerId, instance.Id, definitionId);
            return instance;
        }

        public bool Inform(string agentId, string normInstanceId, string channel)
        {
            if (!world.Agents.TryGetValue(agentId, out var agent) || !Instances.TryGetValue(normInstanceId, out var norm) || !norm.Active)
                return false;
            var key = norm.DefinitionId + "@" + norm.Version;
            if (agent.KnownNormVersions.Contains(key)) return false;
            if (world.Knowledge.TryGetChannel(channel, out _))
            {
                var delivery = world.Knowledge.DeliverSystemNotice(agentId, norm.Id, "norm.known",
                    norm.Id, key, norm.IssuerId, channel, occurredAt: norm.PublishedAt);
                if (delivery != KnowledgeDelivery.Learned && delivery != KnowledgeDelivery.Updated &&
                    delivery != KnowledgeDelivery.Unchanged) return false;
            }
            agent.KnownNormVersions.Add(key);
            world.Emit("norm.learned", channel, agentId, normInstanceId);
            return true;
        }

        public bool IsApplicable(NormInstance instance, string actionId, string placeId, double time, string exceptionEventType = null,
            string subjectId = null)
        {
            var definition = DefinitionFor(instance);
            if (instance == null || !instance.Active || definition == null || time < instance.PublishedAt ||
                definition.ActionId != actionId || time < definition.ActiveFrom || time >= definition.ActiveUntil ||
                (!string.IsNullOrEmpty(definition.ExceptionEventType) && definition.ExceptionEventType == exceptionEventType)) return false;
            if (!string.IsNullOrEmpty(definition.PlaceTag))
            {
                if (!world.Places.TryGetValue(placeId ?? "", out var place) || place.Tag != definition.PlaceTag) return false;
            }
            if (!string.IsNullOrEmpty(instance.CollectiveId))
            {
                if (!world.Society.Collectives.TryGetValue(instance.CollectiveId, out var collective) || !collective.Active) return false;
                bool jurisdiction = world.Places.TryGetValue(placeId ?? "", out var place) && place.OwnerCollectiveId == instance.CollectiveId;
                bool member = false;
                if (!string.IsNullOrEmpty(subjectId))
                    foreach (var membership in world.Society.Memberships.Values)
                        if (membership.AgentId == subjectId && membership.CollectiveId == instance.CollectiveId &&
                            time >= membership.BeginsAt && time < membership.EndsAt) { member = true; break; }
                if (definition.SubjectScope == "Members" ? !member : definition.SubjectScope == "Jurisdiction" ? !jurisdiction : !member && !jurisdiction)
                    return false;
            }
            return true;
        }

        public float KnownPenalty(string agentId, string actionId, string placeId)
        {
            if (!world.Agents.TryGetValue(agentId, out var agent)) return 0;
            float result = 0;
            foreach (var instance in Instances.Values)
            {
                if (!IsApplicable(instance, actionId, placeId, world.Time, subjectId: agentId)) continue;
                var definition = DefinitionFor(instance);
                if (!agent.KnownNormVersions.Contains(definition.Id + "@" + definition.Version)) continue;
                bool overridden = false;
                foreach (var other in Instances.Values)
                {
                    var replacement = DefinitionFor(other);
                    if (other.Id != instance.Id && other.CollectiveId == instance.CollectiveId && replacement != null &&
                        replacement.OverridesNormId == definition.Id && (replacement.Formal || !definition.Formal) &&
                        agent.KnownNormVersions.Contains(replacement.Id + "@" + replacement.Version) &&
                        IsApplicable(other, actionId, placeId, world.Time, subjectId: agentId)) { overridden = true; break; }
                }
                if (overridden) continue;
                result += (definition.Kind == "Prohibit" ? 0.45f : definition.Kind == "Require" ? -0.25f : -0.1f) * agent.Personality.RuleValue;
            }
            return Math.Max(-1, Math.Min(1, result));
        }

        /// <summary>Only a current, canonical action witnessed in the same place creates evidence.</summary>
        public ObservationEvidence Observe(WorldEvent actionEvent, string observerId, float confidence = 1f)
        {
            if (actionEvent == null || string.IsNullOrWhiteSpace(actionEvent.Id) ||
                string.IsNullOrWhiteSpace(actionEvent.PlaceId) ||
                !SimulationWorld.Finite(confidence) || confidence <= 0 ||
                !world.Agents.TryGetValue(observerId ?? string.Empty, out var observer) ||
                !world.Agents.ContainsKey(actionEvent.SubjectId ?? string.Empty) ||
                actionEvent.SubjectId == observerId || observer.PlaceId != actionEvent.PlaceId ||
                !SimulationWorld.Finite(actionEvent.Time) || actionEvent.Time > world.Time ||
                world.Time - actionEvent.Time > world.TickSeconds)
                return null;
            WorldEvent canonical = null;
            foreach (var item in world.Trace)
                if (item.Id == actionEvent.Id) { canonical = item; break; }
            if (canonical == null || canonical.RootCauseId != actionEvent.RootCauseId ||
                canonical.ParentEventId != actionEvent.ParentEventId || canonical.Time != actionEvent.Time ||
                canonical.Type != actionEvent.Type || canonical.EmitterId != actionEvent.EmitterId ||
                canonical.SubjectId != actionEvent.SubjectId || canonical.PlaceId != actionEvent.PlaceId ||
                canonical.EvidenceId != actionEvent.EvidenceId || canonical.Payload != actionEvent.Payload ||
                canonical.Depth != actionEvent.Depth)
                return null;
            var evidence = new ObservationEvidence { Id = world.NextId(), EventId = canonical.Id, ObserverId = observerId,
                SubjectId = canonical.SubjectId, ActionId = canonical.Type, PlaceId = canonical.PlaceId,
                Time = canonical.Time, Confidence = Math.Min(1, confidence) };
            Evidence.Add(evidence.Id, evidence);
            world.Emit("action.observed", observerId, canonical.SubjectId, canonical.Id, canonical.PlaceId, evidence.Id, canonical);
            return evidence;
        }

        public ViolationRecord RecordViolation(string normInstanceId, string evidenceId)
        {
            if (!Instances.TryGetValue(normInstanceId, out var norm) || !Evidence.TryGetValue(evidenceId, out var evidence) ||
                !IsApplicable(norm, evidence.ActionId, evidence.PlaceId, evidence.Time, subjectId: evidence.SubjectId)) return null;
            var definition = DefinitionFor(norm);
            if (definition.Kind != "Prohibit" || (definition.EvidenceRequirement == "DirectWitness" && evidence.Channel != "DirectWitness")) return null;
            foreach (var other in Instances.Values)
            {
                var replacement = DefinitionFor(other);
                if (other.Id != norm.Id && other.CollectiveId == norm.CollectiveId && replacement != null &&
                    replacement.OverridesNormId == definition.Id && (replacement.Formal || !definition.Formal) &&
                    IsApplicable(other, evidence.ActionId, evidence.PlaceId, evidence.Time, subjectId: evidence.SubjectId)) return null;
            }
            var key = normInstanceId + ":" + evidence.EventId;
            if (violationKeys.TryGetValue(key, out var existingId))
            {
                var existing = Violations[existingId];
                if (!existing.EvidenceIds.Contains(evidenceId))
                { existing.EvidenceIds.Add(evidenceId); existing.ObservationCount++; }
                return existing;
            }
            var record = new ViolationRecord { Id = world.NextId(), NormInstanceId = normInstanceId,
                ActionEventId = evidence.EventId, SubjectId = evidence.SubjectId, PlaceId = evidence.PlaceId,
                Time = evidence.Time, ObservationCount = 1 };
            record.EvidenceIds.Add(evidenceId);
            Violations.Add(record.Id, record);
            violationKeys.Add(key, record.Id);
            world.Emit("violation.recorded", evidence.ObserverId, evidence.SubjectId, record.Id, evidence.PlaceId, evidenceId);
            return record;
        }

        public WorldTask RequestResponse(string violationId, string processorId)
        {
            if (!Violations.TryGetValue(violationId, out var violation) || violation.Processed) return null;
            var norm = Instances[violation.NormInstanceId];
            if (!world.Society.HasAuthority(processorId, "violation.handle", norm.CollectiveId, violation.PlaceId)) return null;
            var definition = DefinitionFor(norm);
            int prior = 0;
            foreach (var other in Violations.Values)
                if (other.Processed && other.SubjectId == violation.SubjectId && other.NormInstanceId == violation.NormInstanceId) prior++;
            var task = world.CreateTask(processorId, norm.CollectiveId,
                prior == 0 ? definition.FirstResponseTask : definition.RepeatResponseTask,
                violation.SubjectId, world.Time + 600);
            violation.Processed = true;
            return task;
        }

        private NormDefinition DefinitionFor(NormInstance instance)
        {
            if (instance == null) return null;
            if (instance.PublishedDefinition != null) return instance.PublishedDefinition;
            return Definitions.TryGetValue(instance.DefinitionId, out var definition) && definition.Version == instance.Version ? definition : null;
        }

        internal void RebuildViolationKeys()
        {
            violationKeys.Clear();
            foreach (var record in Violations.Values)
                violationKeys[record.NormInstanceId + ":" + record.ActionEventId] = record.Id;
        }
    }
}
