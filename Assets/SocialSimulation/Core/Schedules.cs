using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SocialSimulation.Core
{
    /// <summary>A reusable duty template. Times are offsets from the caller's anchor time.</summary>
    [Serializable]
    public sealed class ScheduleDefinition
    {
        public string Id;
        public string Version = "1.0.0";
        public string DomainId;
        public string ActionId;
        public string PlaceId;
        public string RequiredRoleId;
        public string RequiredCollectiveId;
        public string RequiredObjectScopeId;
        public double StartOffsetSeconds;
        public double DurationSeconds = 60;
        public double TravelLeadSeconds;
        public double LateGraceSeconds;
        public int Priority;
    }

    /// <summary>One actor's concrete duty. Obligations remain separate from this attendance state.</summary>
    [Serializable]
    public sealed class DutyAssignment
    {
        public string Id;
        public string DefinitionId;
        public string DefinitionVersion;
        public string DomainId;
        public string AgentId;
        public string ActionId;
        public string PlaceId;
        public string RequiredRoleId;
        public string RequiredCollectiveId;
        public string RequiredObjectScopeId;
        public string CommitmentId;
        public double DueAt;
        public double StartsAt;
        public double LatestStartAt;
        public double EndsAt;
        public int Priority;
        public string State = "Planned"; // Planned, Due, Accepted, Declined, Completed, Cancelled, Missed
        public string LastRefusalReason;
        public double LastAttemptAt;
        public double AcceptedAt;
        public double FinishedAt;
    }

    /// <summary>Shared time windows and explicit actor acceptance for every social domain.</summary>
    public sealed class ScheduleService
    {
        private const string DueEvent = "schedule.due";
        private const string DeadlineEvent = "schedule.deadline";
        private const string CompletionDeadlineEvent = "schedule.completion_deadline";
        private readonly SimulationWorld world;
        private readonly Dictionary<string, DutyAssignment> assignments =
            new Dictionary<string, DutyAssignment>(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, DutyAssignment> readOnlyAssignments;

        public IReadOnlyDictionary<string, DutyAssignment> Assignments => readOnlyAssignments;

        internal ScheduleService(SimulationWorld simulation)
        {
            world = simulation ?? throw new ArgumentNullException(nameof(simulation));
            readOnlyAssignments = new ReadOnlyDictionary<string, DutyAssignment>(assignments);
            world.EventPublished += OnWorldEvent;
        }

        public DutyAssignment Create(ScheduleDefinition definition, string agentId, double anchorTime,
            string commitmentId = null)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.Id) ||
                string.IsNullOrWhiteSpace(definition.Version) || string.IsNullOrWhiteSpace(definition.DomainId) ||
                string.IsNullOrWhiteSpace(definition.ActionId) || !Finite(anchorTime) ||
                !Finite(definition.StartOffsetSeconds) || !Finite(definition.DurationSeconds) ||
                !Finite(definition.TravelLeadSeconds) || !Finite(definition.LateGraceSeconds) ||
                definition.DurationSeconds <= 0 || definition.TravelLeadSeconds < 0 ||
                definition.LateGraceSeconds < 0)
                throw new ArgumentException("Invalid schedule definition.");
            var start = anchorTime + definition.StartOffsetSeconds;
            return Create(new DutyAssignment
            {
                DefinitionId = definition.Id, DefinitionVersion = definition.Version,
                DomainId = definition.DomainId, AgentId = agentId, ActionId = definition.ActionId,
                PlaceId = definition.PlaceId, RequiredRoleId = definition.RequiredRoleId,
                RequiredCollectiveId = definition.RequiredCollectiveId, RequiredObjectScopeId = definition.RequiredObjectScopeId,
                CommitmentId = commitmentId, DueAt = start - definition.TravelLeadSeconds,
                StartsAt = start, LatestStartAt = start + definition.LateGraceSeconds,
                EndsAt = start + definition.DurationSeconds, Priority = definition.Priority
            });
        }

        public DutyAssignment Create(DutyAssignment proposed)
        {
            if (proposed == null || !world.Agents.ContainsKey(proposed.AgentId ?? string.Empty) ||
                string.IsNullOrWhiteSpace(proposed.DomainId) || string.IsNullOrWhiteSpace(proposed.ActionId) ||
                (!string.IsNullOrEmpty(proposed.PlaceId) && !world.Places.ContainsKey(proposed.PlaceId)) ||
                !Finite(proposed.DueAt) || !Finite(proposed.StartsAt) ||
                !Finite(proposed.LatestStartAt) || !Finite(proposed.EndsAt) ||
                proposed.DueAt > proposed.StartsAt || proposed.StartsAt > proposed.LatestStartAt ||
                proposed.LatestStartAt >= proposed.EndsAt || proposed.DueAt < world.Time ||
                proposed.State != "Planned" ||
                (!string.IsNullOrEmpty(proposed.CommitmentId) &&
                    !world.Society.Commitments.ContainsKey(proposed.CommitmentId)))
                throw new ArgumentException("Invalid or expired duty assignment.");
            var duty = SnapshotClone.Copy(proposed);
            if (string.IsNullOrWhiteSpace(duty.Id)) duty.Id = world.NextId();
            if (assignments.ContainsKey(duty.Id)) throw new InvalidOperationException("Duplicate duty ID: " + duty.Id);
            assignments.Add(duty.Id, duty);
            world.Schedule(duty.DueAt, DueEvent, "schedule.service", duty.AgentId, duty.PlaceId, duty.Id);
            world.Schedule(duty.LatestStartAt + 0.000001, DeadlineEvent,
                "schedule.service", duty.AgentId, duty.PlaceId, duty.Id);
            // Allow the activity/domain tick that crosses EndsAt to publish its completion first.
            world.Schedule(duty.EndsAt + Math.Max(world.TickSeconds, 0.000001) + 0.000001,
                CompletionDeadlineEvent, "schedule.service", duty.AgentId, duty.PlaceId, duty.Id);
            world.Emit("schedule.created", "schedule.service", duty.AgentId, duty.Id, duty.PlaceId);
            return duty;
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        public List<DutyAssignment> GetDueCandidates(string agentId)
        {
            var result = new List<DutyAssignment>();
            foreach (var duty in assignments.Values)
                if (duty.AgentId == agentId && (duty.State == "Due" || duty.State == "Planned") &&
                    world.Time >= duty.DueAt && world.Time <= duty.LatestStartAt)
                    result.Add(SnapshotClone.Copy(duty));
            result.Sort((a, b) =>
            {
                var priority = b.Priority.CompareTo(a.Priority);
                if (priority != 0) return priority;
                var time = a.StartsAt.CompareTo(b.StartsAt);
                return time != 0 ? time : StringComparer.Ordinal.Compare(a.Id, b.Id);
            });
            return result;
        }

        public bool TryAccept(string dutyId, string actorId, out string refusalReason)
        {
            refusalReason = null;
            if (!assignments.TryGetValue(dutyId ?? string.Empty, out var duty))
            { refusalReason = "UnknownDuty"; return false; }
            if (duty.AgentId != actorId) return Refuse(duty, "WrongActor", out refusalReason);
            if (duty.State != "Planned" && duty.State != "Due")
                return Refuse(duty, "NotOpen:" + duty.State, out refusalReason);
            if (world.Time < duty.DueAt || world.Time > duty.LatestStartAt)
                return Refuse(duty, "OutsideAcceptanceWindow", out refusalReason);
            if (!string.IsNullOrEmpty(duty.CommitmentId) &&
                (!world.Society.Commitments.TryGetValue(duty.CommitmentId, out var commitment) ||
                    commitment.Status != "Active"))
                return Refuse(duty, "CommitmentInactive", out refusalReason);
            if (!string.IsNullOrEmpty(duty.RequiredRoleId))
            {
                bool hasRole = false;
                var roleCheckAt = Math.Max(world.Time, duty.StartsAt);
                foreach (var role in world.Society.Roles.Values)
                    if (role.Active && role.AgentId == actorId && role.RoleId == duty.RequiredRoleId &&
                        (string.IsNullOrEmpty(duty.RequiredCollectiveId) || role.CollectiveId == duty.RequiredCollectiveId) &&
                        (string.IsNullOrEmpty(duty.RequiredObjectScopeId) || role.ObjectScopeId == duty.RequiredObjectScopeId) &&
                        roleCheckAt >= role.BeginsAt && roleCheckAt < role.EndsAt)
                    { hasRole = true; break; }
                if (!hasRole) return Refuse(duty, "MissingRole:" + duty.RequiredRoleId, out refusalReason);
            }
            foreach (var other in assignments.Values)
                if (other.Id != duty.Id && other.AgentId == actorId && other.State == "Accepted" &&
                    other.EndsAt > world.Time && duty.DueAt < other.EndsAt && other.DueAt < duty.EndsAt)
                    return Refuse(duty, "ConflictWith:" + other.Id, out refusalReason);
            duty.State = "Accepted";
            duty.AcceptedAt = world.Time;
            duty.LastRefusalReason = null;
            world.Emit("schedule.accepted", actorId, actorId, duty.Id, duty.PlaceId);
            return true;
        }

        private bool Refuse(DutyAssignment duty, string reason, out string refusalReason)
        {
            duty.LastAttemptAt = world.Time;
            duty.LastRefusalReason = reason;
            refusalReason = reason;
            world.Emit("schedule.acceptance_refused", "schedule.service", duty.AgentId,
                duty.Id + ":" + reason, duty.PlaceId);
            return false;
        }

        public bool Decline(string dutyId, string actorId, string reason)
        {
            if (!assignments.TryGetValue(dutyId ?? string.Empty, out var duty) || duty.AgentId != actorId ||
                (duty.State != "Planned" && duty.State != "Due")) return false;
            duty.State = "Declined";
            duty.LastRefusalReason = string.IsNullOrWhiteSpace(reason) ? "ActorDeclined" : reason;
            duty.FinishedAt = world.Time;
            world.Emit("schedule.declined", actorId, actorId, duty.Id + ":" + duty.LastRefusalReason,
                duty.PlaceId);
            return true;
        }

        public bool Complete(string dutyId, string actorId)
        {
            if (!assignments.TryGetValue(dutyId ?? string.Empty, out var duty) || duty.AgentId != actorId ||
                duty.State != "Accepted") return false;
            duty.State = "Completed";
            duty.FinishedAt = world.Time;
            world.Emit("schedule.completed", actorId, actorId, duty.Id, duty.PlaceId);
            return true;
        }

        public bool Cancel(string dutyId, string reason)
        {
            if (!assignments.TryGetValue(dutyId ?? string.Empty, out var duty) ||
                (duty.State != "Planned" && duty.State != "Due" && duty.State != "Accepted")) return false;
            duty.State = "Cancelled";
            duty.LastRefusalReason = reason;
            duty.FinishedAt = world.Time;
            world.Emit("schedule.cancelled", "schedule.service", duty.AgentId,
                duty.Id + ":" + reason, duty.PlaceId);
            return true;
        }

        public void CancelForAgent(string agentId, string reason)
        {
            var ids = new List<string>();
            foreach (var duty in assignments.Values)
                if (duty.AgentId == agentId &&
                    (duty.State == "Planned" || duty.State == "Due" || duty.State == "Accepted")) ids.Add(duty.Id);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids) Cancel(id, reason);
        }

        private void OnWorldEvent(WorldEvent item)
        {
            if (item.Type != DueEvent && item.Type != DeadlineEvent &&
                item.Type != CompletionDeadlineEvent) return;
            if (!assignments.TryGetValue(item.Payload ?? string.Empty, out var duty)) return;
            if (item.Type == DueEvent)
            {
                if (duty.State == "Planned" && world.Time <= duty.LatestStartAt) duty.State = "Due";
                return;
            }
            if (item.Type == CompletionDeadlineEvent)
            {
                if (duty.State != "Accepted") return;
                duty.State = "Missed";
                duty.LastRefusalReason = "AcceptedButUnfulfilled";
                duty.FinishedAt = world.Time;
                world.Emit("schedule.unfulfilled", "schedule.service", duty.AgentId,
                    duty.Id, duty.PlaceId, parent: item);
                return;
            }
            if (duty.State == "Planned" || duty.State == "Due")
            {
                duty.State = "Missed";
                duty.LastRefusalReason = "DeadlinePassed";
                duty.FinishedAt = world.Time;
                world.Emit("schedule.missed", "schedule.service", duty.AgentId, duty.Id, duty.PlaceId,
                    parent: item);
            }
        }

        internal void Restore(IEnumerable<DutyAssignment> saved)
        {
            assignments.Clear();
            if (saved == null) return;
            foreach (var duty in saved)
            {
                if (duty == null || string.IsNullOrWhiteSpace(duty.Id) ||
                    !world.Agents.ContainsKey(duty.AgentId ?? string.Empty))
                    throw new InvalidOperationException("Invalid saved duty assignment.");
                var copy = SnapshotClone.Copy(duty);
                assignments.Add(copy.Id, copy);
            }
        }
    }
}
