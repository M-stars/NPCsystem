using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    /// <summary>A request for cooperation. Issuing it does not select an agent's goal or start an action.</summary>
    [Serializable]
    public sealed class OrganizationTaskRequest
    {
        public string RequestKey;
        public string AssigneeId;
        public string ActionId;
        public string TargetId;
        public string PlaceId;
        public string CommitmentId;
        public double DueTime;
    }

    /// <summary>Independent values passed to a policy; policies never receive mutable world services.</summary>
    public sealed class GovernanceContext
    {
        public OrganizationState Organization { get; internal set; }
        public AgentState Issuer { get; internal set; }
        public AgentState Assignee { get; internal set; }
        public OrganizationMetrics Metrics { get; internal set; }
        public double Time { get; internal set; }
    }

    public sealed class GovernanceDecision
    {
        public bool Approved;
        public string Reason;
        public static GovernanceDecision Approve() => new GovernanceDecision { Approved = true };
        public static GovernanceDecision Reject(string reason) => new GovernanceDecision { Reason = reason };
    }

    public interface IGovernanceStrategy
    {
        string Id { get; }
        GovernanceDecision Evaluate(GovernanceContext context, OrganizationTaskRequest request);
    }

    /// <summary>Authority is checked by the service before a replaceable policy is consulted.</summary>
    public sealed class LeaderAuthorizedGovernance : IGovernanceStrategy
    {
        public string Id => "LeaderAuthorized";
        public GovernanceDecision Evaluate(GovernanceContext context, OrganizationTaskRequest request)
        { return GovernanceDecision.Approve(); }
    }

    public sealed class OrganizationResourceFlow
    {
        public string ResourceId;
        public long Received;
        public long Sent;
        public long Net => Received - Sent;
    }

    /// <summary>Recomputed facts, not authoritative counters or simulated organization emotions.</summary>
    public sealed class OrganizationMetrics
    {
        public string OrganizationId;
        public string AccountOwnerId;
        public int PendingTasks;
        public int AcceptedTasks;
        public int CompletedTasks;
        public int DeclinedTasks;
        public int CancelledTasks;
        public int MissedTasks;
        public int ActiveAgreements;
        public int FulfilledAgreements;
        public int BreachedAgreements;
        public int AgreementsWithRecordedBreach;
        public int TerminatedAgreements;
        public readonly List<OrganizationResourceFlow> ResourceFlows = new List<OrganizationResourceFlow>();
    }

    public sealed class GovernanceService
    {
        public const string AssignAuthority = "organization.task.assign";
        private readonly SimulationWorld world;
        private readonly Dictionary<string, IGovernanceStrategy> strategies =
            new Dictionary<string, IGovernanceStrategy>(StringComparer.Ordinal);

        internal GovernanceService(SimulationWorld simulation)
        {
            world = simulation ?? throw new ArgumentNullException(nameof(simulation));
            RegisterStrategy(new LeaderAuthorizedGovernance());
        }

        public void RegisterStrategy(IGovernanceStrategy strategy)
        {
            if (strategy == null || string.IsNullOrWhiteSpace(strategy.Id))
                throw new ArgumentException("A governance strategy needs a stable ID.", nameof(strategy));
            strategies[strategy.Id] = strategy;
        }

        public OrganizationState CreateOrganization(string id, string collectiveId,
            string governanceId = "LeaderAuthorized")
        {
            if (string.IsNullOrWhiteSpace(id) || world.Society.Organizations.ContainsKey(id) ||
                !world.Society.Collectives.TryGetValue(collectiveId ?? string.Empty, out var collective) ||
                !collective.Active || string.IsNullOrWhiteSpace(governanceId) || !strategies.ContainsKey(governanceId))
                throw new ArgumentException("Organization requires a unique ID, active collective and registered governance policy.");
            var organization = new OrganizationState { Id = id, CollectiveId = collectiveId, Governance = governanceId };
            world.Society.Organizations.Add(id, organization);
            world.Emit("organization.created", collectiveId, null, id);
            return organization;
        }

        public bool TryRequestTask(string organizationId, string issuerId, OrganizationTaskRequest request,
            out WorldTask task, out string reason)
        {
            task = null;
            reason = null;
            if (!TryGetActiveOrganization(organizationId, out var organization))
                return Refuse("OrganizationInactive", out reason);
            if (request == null || string.IsNullOrWhiteSpace(request.RequestKey) ||
                string.IsNullOrWhiteSpace(request.ActionId) || !SimulationWorld.Finite(request.DueTime) ||
                !world.Agents.ContainsKey(request.AssigneeId ?? string.Empty) ||
                (!string.IsNullOrEmpty(request.PlaceId) && !world.Places.ContainsKey(request.PlaceId)))
                return Refuse("InvalidRequest", out reason);

            // A duplicate must be matched before checking its expired deadline. It never creates a new command.
            foreach (var existing in world.Tasks.Values)
                if (existing.OrganizationId == organizationId && existing.IssuerId == issuerId &&
                    existing.RequestKey == request.RequestKey)
                {
                    if (!SameRequest(existing, request)) return Refuse("RequestKeyConflict", out reason);
                    task = existing;
                    return true;
                }

            if (request.DueTime < world.Time) return Refuse("DeadlinePassed", out reason);
            if (!IsMember(issuerId, organization.CollectiveId)) return Refuse("IssuerNotMember", out reason);
            if (!IsMember(request.AssigneeId, organization.CollectiveId)) return Refuse("AssigneeNotMember", out reason);
            if (!world.Society.HasAuthority(issuerId, AssignAuthority, organization.CollectiveId,
                request.PlaceId, request.TargetId)) return Refuse("MissingAuthority", out reason);
            if (!ValidCommitment(request.CommitmentId, request.AssigneeId))
                return Refuse("CommitmentInactiveOrUnrelated", out reason);
            if (!strategies.TryGetValue(organization.Governance ?? string.Empty, out var strategy))
                return Refuse("GovernanceUnavailable", out reason);

            GovernanceDecision decision;
            try
            {
                decision = strategy.Evaluate(new GovernanceContext
                {
                    Organization = SnapshotClone.Copy(organization),
                    Issuer = SnapshotClone.Copy(world.Agents[issuerId]),
                    Assignee = SnapshotClone.Copy(world.Agents[request.AssigneeId]),
                    Metrics = DeriveMetrics(organizationId), Time = world.Time
                }, SnapshotClone.Copy(request));
            }
            catch (Exception error)
            {
                world.Diagnostics.Add(new Diagnostic("GOVERNANCE_POLICY_ERROR", organizationId, error.Message));
                return Refuse("GovernanceFailed", out reason);
            }
            if (decision == null || !decision.Approved)
                return Refuse(decision?.Reason ?? "GovernanceRejected", out reason);

            task = world.CreateTask(request.AssigneeId, issuerId, request.ActionId, request.TargetId,
                request.DueTime, request.CommitmentId);
            task.OrganizationId = organizationId;
            task.RequestKey = request.RequestKey;
            task.PlaceId = request.PlaceId;
            task.CreatedAt = world.Time;
            organization.TaskIds.Add(task.Id);
            world.Emit("organization.task_requested", issuerId, request.AssigneeId, task.Id, request.PlaceId);
            return true;
        }

        public bool TryAccept(string taskId, string actorId, out string reason)
        {
            if (!TryOwnedTask(taskId, actorId, out var task, out reason)) return false;
            if (task.Status == "Accepted") return true;
            if (task.Status != "Pending") return Refuse("TaskNotPending", out reason);
            if (!TryGetActiveOrganization(task.OrganizationId, out var organization))
                return Refuse("OrganizationInactive", out reason);
            if (world.Time > task.DueTime) return Refuse("DeadlinePassed", out reason);
            if (!IsMember(actorId, organization.CollectiveId)) return Refuse("AssigneeNotMember", out reason);
            if (!IsMember(task.IssuerId, organization.CollectiveId) ||
                !world.Society.HasAuthority(task.IssuerId, AssignAuthority, organization.CollectiveId,
                    task.PlaceId, task.TargetId)) return Refuse("IssuerAuthorityExpired", out reason);
            if (!ValidCommitment(task.CommitmentId, actorId)) return Refuse("CommitmentInactiveOrUnrelated", out reason);
            task.Status = "Accepted";
            task.AcceptedAt = world.Time;
            task.LastReason = null;
            world.Emit("organization.task_accepted", actorId, actorId, task.Id, task.PlaceId);
            return true;
        }

        public bool Decline(string taskId, string actorId, string explanation)
        {
            if (!TryOwnedTask(taskId, actorId, out var task, out _) ||
                (task.Status != "Pending" && task.Status != "Accepted")) return false;
            Close(task, "Declined", string.IsNullOrWhiteSpace(explanation) ? "ActorDeclined" : explanation);
            return true;
        }

        public bool Complete(string taskId, string actorId)
        {
            if (!TryOwnedTask(taskId, actorId, out var task, out _)) return false;
            if (task.Status == "Completed") return true;
            if (task.Status != "Accepted" || world.Time > task.DueTime ||
                !TryGetActiveOrganization(task.OrganizationId, out var organization) ||
                !IsMember(actorId, organization.CollectiveId)) return false;
            Close(task, "Completed", null);
            return true;
        }

        public bool Cancel(string taskId, string actorId, string explanation)
        {
            if (!world.Tasks.TryGetValue(taskId ?? string.Empty, out var task) ||
                (task.Status != "Pending" && task.Status != "Accepted") ||
                !TryGetActiveOrganization(task.OrganizationId, out var organization) ||
                !IsMember(actorId, organization.CollectiveId) ||
                !world.Society.HasAuthority(actorId, AssignAuthority, organization.CollectiveId, task.PlaceId, task.TargetId))
                return false;
            Close(task, "Cancelled", string.IsNullOrWhiteSpace(explanation) ? "IssuerCancelled" : explanation);
            return true;
        }

        public void CancelForAgent(string agentId, string reason)
        {
            var ids = new List<string>();
            foreach (var task in world.Tasks.Values)
                if (!string.IsNullOrEmpty(task.OrganizationId) && task.AssigneeId == agentId &&
                    (task.Status == "Pending" || task.Status == "Accepted")) ids.Add(task.Id);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids) Close(world.Tasks[id], "Cancelled", reason);
        }

        public void CancelForCollective(string collectiveId, string reason)
        {
            var ids = new List<string>();
            foreach (var task in world.Tasks.Values)
                if (!string.IsNullOrEmpty(task.OrganizationId) &&
                    world.Society.Organizations.TryGetValue(task.OrganizationId, out var organization) &&
                    organization.CollectiveId == collectiveId &&
                    (task.Status == "Pending" || task.Status == "Accepted")) ids.Add(task.Id);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids) Close(world.Tasks[id], "Cancelled", reason);
        }

        /// <summary>Run after domain systems so a completion at the deadline can be committed first.</summary>
        public void Tick()
        {
            var ids = new List<string>();
            foreach (var task in world.Tasks.Values)
                if (!string.IsNullOrEmpty(task.OrganizationId) &&
                    (task.Status == "Pending" || task.Status == "Accepted")) ids.Add(task.Id);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var task = world.Tasks[id];
                if (!TryGetActiveOrganization(task.OrganizationId, out var organization))
                    Close(task, "Cancelled", "OrganizationInactive");
                else if (!IsMember(task.AssigneeId, organization.CollectiveId))
                    Close(task, "Cancelled", "AssigneeLeft");
                else if (world.Time > task.DueTime) Close(task, "Missed", "DeadlinePassed");
            }
        }

        public OrganizationMetrics DeriveMetrics(string organizationId, double fromTime = 0,
            double toTime = double.MaxValue)
        {
            if (!world.Society.Organizations.TryGetValue(organizationId ?? string.Empty, out var organization))
                throw new ArgumentException("Unknown organization.", nameof(organizationId));
            if (!SimulationWorld.Finite(fromTime) || !SimulationWorld.Finite(toTime) || fromTime < 0 || toTime < fromTime)
                throw new ArgumentException("Metric window must be finite and ordered.");
            var result = new OrganizationMetrics { OrganizationId = organization.Id, AccountOwnerId = organization.CollectiveId };
            foreach (var task in world.Tasks.Values)
            {
                if (task.OrganizationId != organizationId || task.CreatedAt < fromTime || task.CreatedAt > toTime) continue;
                switch (task.Status)
                {
                    case "Pending": result.PendingTasks++; break;
                    case "Accepted": result.AcceptedTasks++; break;
                    case "Completed": result.CompletedTasks++; break;
                    case "Declined": result.DeclinedTasks++; break;
                    case "Cancelled": result.CancelledTasks++; break;
                    case "Missed": result.MissedTasks++; break;
                }
            }
            foreach (var agreement in world.Society.Commitments.Values)
            {
                if (!agreement.ManagedLifecycle || agreement.CreatedAt < fromTime || agreement.CreatedAt > toTime ||
                    (agreement.ScopeId != organizationId && agreement.ScopeId != organization.CollectiveId &&
                     !agreement.PartyIds.Contains(organization.CollectiveId))) continue;
                switch (agreement.Status)
                {
                    case "Active": result.ActiveAgreements++; break;
                    case "Fulfilled": result.FulfilledAgreements++; break;
                    case "Breached": result.BreachedAgreements++; break;
                    case "Terminated": result.TerminatedAgreements++; break;
                }
            }
            var breachedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in world.Agreements.Settlements)
                if (record.Kind == "Breached" && record.Time >= fromTime && record.Time <= toTime &&
                    world.Society.Commitments.TryGetValue(record.CommitmentId, out var agreement) &&
                    (agreement.ScopeId == organizationId || agreement.ScopeId == organization.CollectiveId ||
                     agreement.PartyIds.Contains(organization.CollectiveId))) breachedIds.Add(agreement.Id);
            result.AgreementsWithRecordedBreach = breachedIds.Count;
            var flows = new Dictionary<string, OrganizationResourceFlow>(StringComparer.Ordinal);
            foreach (var entry in world.Economy.Ledger)
            {
                if (entry.Time < fromTime || entry.Time > toTime ||
                    (entry.FromOwnerId != result.AccountOwnerId && entry.ToOwnerId != result.AccountOwnerId)) continue;
                if (!flows.TryGetValue(entry.ResourceId, out var flow))
                    flows.Add(entry.ResourceId, flow = new OrganizationResourceFlow { ResourceId = entry.ResourceId });
                if (entry.FromOwnerId == result.AccountOwnerId) flow.Sent += entry.Quantity;
                if (entry.ToOwnerId == result.AccountOwnerId) flow.Received += entry.Quantity;
            }
            result.ResourceFlows.AddRange(flows.Values);
            result.ResourceFlows.Sort((a, b) => StringComparer.Ordinal.Compare(a.ResourceId, b.ResourceId));
            return result;
        }

        private bool TryGetActiveOrganization(string id, out OrganizationState organization)
        {
            return world.Society.Organizations.TryGetValue(id ?? string.Empty, out organization) && organization.Active &&
                world.Society.Collectives.TryGetValue(organization.CollectiveId ?? string.Empty, out var collective) && collective.Active;
        }

        private bool IsMember(string agentId, string collectiveId)
        {
            if (!world.Agents.ContainsKey(agentId ?? string.Empty)) return false;
            foreach (var member in world.Society.Memberships.Values)
                if (member.Active && member.AgentId == agentId && member.CollectiveId == collectiveId &&
                    member.BeginsAt <= world.Time && world.Time < member.EndsAt) return true;
            return false;
        }

        private bool ValidCommitment(string commitmentId, string assigneeId)
        {
            return string.IsNullOrEmpty(commitmentId) ||
                (world.Society.Commitments.TryGetValue(commitmentId, out var commitment) &&
                 commitment.Status == "Active" && commitment.PartyIds.Contains(assigneeId));
        }

        private bool TryOwnedTask(string taskId, string actorId, out WorldTask task, out string reason)
        {
            task = null;
            reason = null;
            if (!world.Agents.ContainsKey(actorId ?? string.Empty) ||
                !world.Tasks.TryGetValue(taskId ?? string.Empty, out task) ||
                string.IsNullOrEmpty(task.OrganizationId)) return Refuse("UnknownTaskOrActor", out reason);
            return task.AssigneeId == actorId || Refuse("WrongActor", out reason);
        }

        private static bool SameRequest(WorldTask task, OrganizationTaskRequest request)
        {
            return task.AssigneeId == request.AssigneeId && task.Kind == request.ActionId &&
                (task.TargetId ?? string.Empty) == (request.TargetId ?? string.Empty) &&
                (task.PlaceId ?? string.Empty) == (request.PlaceId ?? string.Empty) &&
                (task.CommitmentId ?? string.Empty) == (request.CommitmentId ?? string.Empty) && task.DueTime == request.DueTime;
        }

        private void Close(WorldTask task, string status, string reason)
        {
            task.Status = status;
            task.ClosedAt = world.Time;
            task.LastReason = reason;
            world.Emit("organization.task_" + status.ToLowerInvariant(), task.AssigneeId, task.AssigneeId, task.Id, task.PlaceId);
        }

        private static bool Refuse(string value, out string reason) { reason = value; return false; }
    }
}
