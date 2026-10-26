using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class PersonalityProfile
    {
        public float Extraversion = 0.5f;
        public float Conscientiousness = 0.5f;
        public float Cooperation = 0.5f;
        public float Curiosity = 0.5f;
        public float Sensitivity = 0.5f;
        public float RuleValue = 0.5f;
        public float AchievementValue = 0.5f;
        public float FriendshipValue = 0.5f;
        public float FreedomValue = 0.5f;
        public float StatusValue = 0.5f;
    }

    [Serializable]
    public sealed class NeedState
    {
        public float Energy = 1f;
        public float Social = 0.5f;
        public float Recreation = 0.5f;
        public float Hunger = 0f;
        public float Comfort = 0.5f;

        public void Advance(double seconds)
        {
            Energy = Clamp01(Energy - (float)(seconds / 18000.0));
            Social = Clamp01(Social - (float)(seconds / 12000.0));
            Recreation = Clamp01(Recreation - (float)(seconds / 16000.0));
            Hunger = Clamp01(Hunger + (float)(seconds / 14000.0));
        }

        public static float Clamp01(float value) { return Math.Max(0f, Math.Min(1f, value)); }
    }

    [Serializable]
    public sealed class EmotionState
    {
        public float Valence;
        public float Arousal;
        public float Stress;

        public void Decay(double seconds)
        {
            var factor = (float)Math.Exp(-seconds / 900.0);
            Valence *= factor;
            Arousal *= factor;
            Stress *= factor;
        }
    }

    [Serializable]
    public sealed class MemoryEvent
    {
        public string EventId;
        public string Type;
        public string SubjectId;
        public string PlaceId;
        public string SourceId;
        public double Time;
        public float Confidence;
        public float EmotionalWeight;
        public bool Protected;
    }

    [Serializable]
    public sealed class AgentState
    {
        public string Id;
        public string TemplateId;
        public string DisplayName;
        public string PlaceId;
        public string CurrentGoalId;
        public double GoalCommittedUntil;
        public PersonalityProfile Personality = new PersonalityProfile();
        public NeedState Needs = new NeedState();
        public EmotionState Emotion = new EmotionState();
        public List<string> KnownNormVersions = new List<string>();
        public List<MemoryEvent> Memories = new List<MemoryEvent>();
        public List<string> CapabilityIds = new List<string>();
        public List<string> MembershipIds = new List<string>();
        public List<string> RoleAssignmentIds = new List<string>();
        public List<string> CommitmentIds = new List<string>();
        public List<string> TaskIds = new List<string>();
        public ulong DecisionRandomState;

        /// <summary>Store an independent memory. Returns false when duplicate or when every retained memory is protected.</summary>
        public bool Remember(MemoryEvent memory, int capacity = 48)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (memory == null || string.IsNullOrWhiteSpace(memory.EventId)) return false;
            for (int i = 0; i < Memories.Count; i++) if (Memories[i].EventId == memory.EventId) return false;
            while (Memories.Count >= capacity)
            {
                int oldest = -1;
                for (int i = 0; i < Memories.Count; i++)
                    if (!Memories[i].Protected && (oldest < 0 || Memories[i].Time < Memories[oldest].Time)) oldest = i;
                if (oldest < 0) return false;
                Memories.RemoveAt(oldest);
            }
            Memories.Add(SnapshotClone.Copy(memory));
            return true;
        }
    }

    [Serializable]
    public sealed class CollectiveState
    {
        public string Id;
        public string DefinitionId;
        public string Name;
        public bool Formal;
        public bool Active = true;
        public List<string> MemberIds = new List<string>();
        public List<string> NormIds = new List<string>();
    }

    [Serializable]
    public sealed class OrganizationState
    {
        public string Id;
        public string CollectiveId;
        public string Governance = "LeaderAuthorized";
        public bool Active = true;
        public List<string> GoalIds = new List<string>();
        public List<string> TaskIds = new List<string>();
        public List<string> AssetIds = new List<string>();
    }

    [Serializable]
    public sealed class Membership
    {
        public string Id;
        public string AgentId;
        public string CollectiveId;
        public double BeginsAt;
        public double EndsAt = double.MaxValue;
        public bool Active = true;
    }

    [Serializable]
    public sealed class RoleAssignment
    {
        public string Id;
        public string AgentId;
        public string RoleId;
        public string CollectiveId;
        public string ObjectScopeId;
        public double BeginsAt;
        public double EndsAt = double.MaxValue;
        public bool Active = true;
        public List<string> Duties = new List<string>();
        public List<string> InformationChannels = new List<string>();
    }

    [Serializable]
    public sealed class AuthorityGrant
    {
        public string Id;
        public string GrantorId;
        public string GranteeId;
        public string ActionId;
        public string CollectiveId;
        public string PlaceScopeId;
        public string ObjectScopeId;
        public double BeginsAt;
        public double EndsAt = double.MaxValue;
        public bool CanDelegate;
        public bool Active = true;
        public string ParentGrantId;
        public string RoleAssignmentId;
    }

    [Serializable]
    public sealed class SocialRelation
    {
        public string Id;
        public string SourceId;
        public string TargetId;
        public string Type;
        public string ScopeId;
        public float Liking;
        public float Trust;
        public float Familiarity;
        public float Respect;
        public double UpdatedAt;
        /// <summary>Seconds of unreinforced time already applied by decay; reinforcement resets it to zero.</summary>
        public double DecayedFor;
        public bool Active = true;
    }

    [Serializable]
    public sealed class Commitment
    {
        public string Id;
        public string Kind;
        public string ScopeId;
        public string Condition;
        public double DueTime;
        public string Status = "Active";
        public bool ManagedLifecycle;
        public double CreatedAt;
        public double ClosedAt;
        public string CloseReason;
        public List<string> PartyIds = new List<string>();
        public List<string> ObligationIds = new List<string>();
    }

    [Serializable]
    public sealed class Obligation
    {
        public string Id;
        public string CommitmentId;
        public string DebtorId;
        public string CreditorId;
        public string ActionId;
        public string ResourceId;
        public int Quantity;
        public int FulfilledQuantity;
        public double DueTime;
        public string Status = "Outstanding";
    }

    [Serializable]
    public sealed class PlaceState
    {
        public string Id;
        public string DefinitionId;
        public string Name;
        public string Tag;
        public string RegionInstanceId;
        public string OwnerCollectiveId;
        public int Capacity;
        public bool Open = true;
        public List<string> NeighbourIds = new List<string>();
    }

    public sealed class SocietyService
    {
        private readonly SimulationWorld world;
        public readonly Dictionary<string, CollectiveState> Collectives = new Dictionary<string, CollectiveState>(StringComparer.Ordinal);
        public readonly Dictionary<string, OrganizationState> Organizations = new Dictionary<string, OrganizationState>(StringComparer.Ordinal);
        public readonly Dictionary<string, Membership> Memberships = new Dictionary<string, Membership>(StringComparer.Ordinal);
        public readonly Dictionary<string, RoleAssignment> Roles = new Dictionary<string, RoleAssignment>(StringComparer.Ordinal);
        public readonly Dictionary<string, AuthorityGrant> Grants = new Dictionary<string, AuthorityGrant>(StringComparer.Ordinal);
        public readonly Dictionary<string, SocialRelation> Relations = new Dictionary<string, SocialRelation>(StringComparer.Ordinal);
        public readonly Dictionary<string, Commitment> Commitments = new Dictionary<string, Commitment>(StringComparer.Ordinal);
        public readonly Dictionary<string, Obligation> Obligations = new Dictionary<string, Obligation>(StringComparer.Ordinal);

        internal SocietyService(SimulationWorld world) { this.world = world; }

        public Membership Join(string agentId, string collectiveId)
        {
            if (!world.Agents.ContainsKey(agentId) || !Collectives.TryGetValue(collectiveId, out var collective) || !collective.Active)
                throw new InvalidOperationException("Unknown agent or inactive collective.");
            foreach (var membership in Memberships.Values)
                if (membership.Active && membership.AgentId == agentId && membership.CollectiveId == collectiveId) return membership;
            var result = new Membership { Id = world.NextId(), AgentId = agentId, CollectiveId = collectiveId, BeginsAt = world.Time };
            Memberships.Add(result.Id, result);
            collective.MemberIds.Add(agentId);
            world.Agents[agentId].MembershipIds.Add(result.Id);
            world.Emit("membership.joined", collectiveId, agentId, null);
            return result;
        }

        public void Leave(string agentId, string collectiveId)
        {
            foreach (var membership in Memberships.Values)
                if (membership.Active && membership.AgentId == agentId && membership.CollectiveId == collectiveId)
                { membership.Active = false; membership.EndsAt = world.Time; }
            if (Collectives.TryGetValue(collectiveId, out var collective)) collective.MemberIds.Remove(agentId);
            foreach (var role in Roles.Values)
                if (role.Active && role.AgentId == agentId && role.CollectiveId == collectiveId)
                { role.Active = false; role.EndsAt = world.Time; }
            foreach (var grant in Grants.Values)
                if (grant.Active && grant.GranteeId == agentId && grant.CollectiveId == collectiveId) RevokeGrant(grant.Id);
            world.Emit("membership.left", collectiveId, agentId, null);
        }

        public RoleAssignment AssignRole(string agentId, string collectiveId, string roleId, string objectScopeId = null)
        {
            bool member = false;
            foreach (var item in Memberships.Values)
                if (item.Active && item.AgentId == agentId && item.CollectiveId == collectiveId) { member = true; break; }
            if (!member) throw new InvalidOperationException("Role requires active membership.");
            var role = new RoleAssignment { Id = world.NextId(), AgentId = agentId, CollectiveId = collectiveId,
                RoleId = roleId, ObjectScopeId = objectScopeId, BeginsAt = world.Time };
            Roles.Add(role.Id, role);
            world.Agents[agentId].RoleAssignmentIds.Add(role.Id);
            world.Emit("role.assigned", collectiveId, agentId, roleId);
            return role;
        }

        public void RevokeRole(string roleId)
        {
            if (!Roles.TryGetValue(roleId, out var role) || !role.Active) return;
            role.Active = false;
            role.EndsAt = world.Time;
            foreach (var grant in Grants.Values)
                if (grant.Active && grant.RoleAssignmentId == roleId) RevokeGrant(grant.Id);
            world.Emit("role.revoked", role.CollectiveId, role.AgentId, role.Id);
        }

        public AuthorityGrant Grant(string grantorId, string granteeId, string actionId, string collectiveId,
            string placeScopeId = null, string objectScopeId = null, double durationSeconds = double.MaxValue,
            bool canDelegate = false, string parentGrantId = null, string roleAssignmentId = null)
        {
            if (string.IsNullOrWhiteSpace(granteeId) || !world.Agents.ContainsKey(granteeId) ||
                string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(collectiveId) ||
                !Collectives.TryGetValue(collectiveId, out var collective) || !collective.Active ||
                durationSeconds <= 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
                throw new InvalidOperationException("Grant requires a known grantee, active collective, action, and positive finite duration.");
            var endsAt = durationSeconds == double.MaxValue ? double.MaxValue : world.Time + durationSeconds;
            if (double.IsInfinity(endsAt)) throw new InvalidOperationException("Grant expiration exceeds the clock range.");
            if (!string.IsNullOrEmpty(roleAssignmentId))
            {
                if (!Roles.TryGetValue(roleAssignmentId, out var role) || !RoleIsActive(role) ||
                    role.AgentId != granteeId || role.CollectiveId != collectiveId)
                    throw new InvalidOperationException("Grant role does not belong to the grantee in this collective.");
                endsAt = Math.Min(endsAt, role.EndsAt);
            }
            if (!string.IsNullOrEmpty(parentGrantId))
            {
                if (!Grants.TryGetValue(parentGrantId, out var parent) || !GrantChainIsActive(parent) || !parent.CanDelegate ||
                    parent.GranteeId != grantorId || parent.ActionId != actionId || parent.CollectiveId != collectiveId ||
                    (!string.IsNullOrEmpty(parent.PlaceScopeId) && parent.PlaceScopeId != placeScopeId) ||
                    (!string.IsNullOrEmpty(parent.ObjectScopeId) && parent.ObjectScopeId != objectScopeId))
                    throw new InvalidOperationException("Invalid delegation chain or scope.");
                endsAt = Math.Min(endsAt, parent.EndsAt);
            }
            else if (grantorId != collectiveId && !HasAuthority(grantorId, "authority.grant", collectiveId, placeScopeId, objectScopeId))
                throw new InvalidOperationException("Grantor has no delegation authority.");
            var grant = new AuthorityGrant { Id = world.NextId(), GrantorId = grantorId, GranteeId = granteeId,
                ActionId = actionId, CollectiveId = collectiveId, PlaceScopeId = placeScopeId,
                ObjectScopeId = objectScopeId, BeginsAt = world.Time, EndsAt = endsAt,
                CanDelegate = canDelegate, ParentGrantId = parentGrantId, RoleAssignmentId = roleAssignmentId };
            Grants.Add(grant.Id, grant);
            world.Emit("authority.granted", grantorId, granteeId, grant.Id);
            return grant;
        }

        public void RevokeGrant(string grantId)
        {
            if (!Grants.TryGetValue(grantId, out var grant) || !grant.Active) return;
            grant.Active = false;
            foreach (var child in Grants.Values) if (child.Active && child.ParentGrantId == grantId) RevokeGrant(child.Id);
            world.Emit("authority.revoked", grant.GrantorId, grant.GranteeId, grantId);
        }

        public bool HasAuthority(string agentId, string actionId, string collectiveId, string placeId = null, string objectId = null)
        {
            if (string.IsNullOrWhiteSpace(agentId) || !world.Agents.ContainsKey(agentId) ||
                string.IsNullOrWhiteSpace(collectiveId) || !Collectives.TryGetValue(collectiveId, out var collective) || !collective.Active)
                return false;
            foreach (var grant in Grants.Values)
                if (grant.GranteeId == agentId && grant.ActionId == actionId &&
                    grant.CollectiveId == collectiveId && GrantChainIsActive(grant) &&
                    (string.IsNullOrEmpty(grant.PlaceScopeId) || grant.PlaceScopeId == placeId) &&
                    (string.IsNullOrEmpty(grant.ObjectScopeId) || grant.ObjectScopeId == objectId))
                    return true;
            return false;
        }

        private bool RoleIsActive(RoleAssignment role)
        { return role != null && role.Active && world.Time >= role.BeginsAt && world.Time < role.EndsAt; }

        private bool GrantChainIsActive(AuthorityGrant grant)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (grant != null)
            {
                if (!visited.Add(grant.Id) || !grant.Active || world.Time < grant.BeginsAt || world.Time >= grant.EndsAt ||
                    !world.Agents.ContainsKey(grant.GranteeId) ||
                    (!string.IsNullOrEmpty(grant.RoleAssignmentId) &&
                     (!Roles.TryGetValue(grant.RoleAssignmentId, out var role) || !RoleIsActive(role) ||
                      role.AgentId != grant.GranteeId || role.CollectiveId != grant.CollectiveId))) return false;
                if (string.IsNullOrEmpty(grant.ParentGrantId)) return true;
                if (!Grants.TryGetValue(grant.ParentGrantId, out var parent) || !parent.CanDelegate ||
                    parent.GranteeId != grant.GrantorId || parent.ActionId != grant.ActionId ||
                    parent.CollectiveId != grant.CollectiveId ||
                    (!string.IsNullOrEmpty(parent.PlaceScopeId) && parent.PlaceScopeId != grant.PlaceScopeId) ||
                    (!string.IsNullOrEmpty(parent.ObjectScopeId) && parent.ObjectScopeId != grant.ObjectScopeId)) return false;
                grant = parent;
            }
            return false;
        }

        public SocialRelation Relate(string sourceId, string targetId, string type, float likingDelta, float trustDelta)
        {
            if (!world.Agents.ContainsKey(sourceId) || !world.Agents.ContainsKey(targetId) || sourceId == targetId)
                throw new InvalidOperationException("Relationship requires two distinct agents.");
            var key = Identity.Definition(Identity.Definition(sourceId, targetId), type);
            if (!Relations.TryGetValue(key, out var relation))
            {
                relation = new SocialRelation { Id = key, SourceId = sourceId, TargetId = targetId, Type = type };
                Relations.Add(key, relation);
            }
            relation.Liking = Math.Max(-1, Math.Min(1, relation.Liking + likingDelta));
            relation.Trust = Math.Max(-1, Math.Min(1, relation.Trust + trustDelta));
            relation.Familiarity = Math.Min(1, relation.Familiarity + 0.05f);
            relation.UpdatedAt = world.Time;
            relation.DecayedFor = 0;
            return relation;
        }

        /// <summary>Half-life of unreinforced liking and trust; zero disables decay. Familiarity never decays.</summary>
        public double RelationDecayHalfLifeSeconds { get; private set; } = 172800;
        /// <summary>Unreinforced time before decay starts, so one-day gaps do not erode relationships.</summary>
        public double RelationDecayGraceSeconds { get; private set; } = 3600;

        public void ConfigureRelationDecay(double halfLifeSeconds, double graceSeconds)
        {
            if (!SimulationWorld.Finite(halfLifeSeconds) || halfLifeSeconds < 0 ||
                !SimulationWorld.Finite(graceSeconds) || graceSeconds < 0 ||
                halfLifeSeconds > 0 && halfLifeSeconds < 1)
                throw new ArgumentException("Relation decay settings require a zero or >=1 second half-life and a non-negative grace.");
            RelationDecayHalfLifeSeconds = halfLifeSeconds;
            RelationDecayGraceSeconds = graceSeconds;
        }

        /// <summary>Passive drift toward neutral; not an event, because no discrete social act happened.</summary>
        public void Tick()
        {
            if (RelationDecayHalfLifeSeconds <= 0) return;
            foreach (var relation in Relations.Values)
            {
                if (!relation.Active) continue;
                var unreinforced = world.Time - relation.UpdatedAt - RelationDecayGraceSeconds;
                if (unreinforced <= relation.DecayedFor) continue;
                var factor = (float)Math.Pow(0.5, (unreinforced - relation.DecayedFor) / RelationDecayHalfLifeSeconds);
                relation.Liking = Math.Max(-1, Math.Min(1, relation.Liking * factor));
                relation.Trust = Math.Max(-1, Math.Min(1, relation.Trust * factor));
                relation.DecayedFor = unreinforced;
            }
        }

        public Commitment CreateCommitment(string kind, IList<string> parties, double dueTime, string scopeId = null)
        {
            if (parties == null || parties.Count < 2) throw new ArgumentException("Commitment needs at least two parties.");
            var commitment = new Commitment { Id = world.NextId(), Kind = kind, DueTime = dueTime, ScopeId = scopeId };
            foreach (var party in parties)
            {
                if (!world.Agents.ContainsKey(party) && !Collectives.ContainsKey(party))
                    throw new InvalidOperationException("Unknown commitment party: " + party);
                if (!commitment.PartyIds.Contains(party)) commitment.PartyIds.Add(party);
            }
            Commitments.Add(commitment.Id, commitment);
            foreach (var party in commitment.PartyIds)
                if (world.Agents.TryGetValue(party, out var agent)) agent.CommitmentIds.Add(commitment.Id);
            world.Emit("commitment.created", commitment.Id, null, kind);
            return commitment;
        }

        public void DissolveCollective(string collectiveId)
        {
            if (!Collectives.TryGetValue(collectiveId, out var collective) || !collective.Active) return;
            collective.Active = false;
            foreach (var membership in Memberships.Values)
                if (membership.Active && membership.CollectiveId == collectiveId) Leave(membership.AgentId, collectiveId);
            foreach (var org in Organizations.Values) if (org.CollectiveId == collectiveId) org.Active = false;
            world.Governance.CancelForCollective(collectiveId, "collective.dissolved");
            foreach (var grant in Grants.Values) if (grant.CollectiveId == collectiveId) grant.Active = false;
            // Open obligations stay in the ledger for explicit settlement, rather than disappearing.
            world.Emit("collective.dissolved", collectiveId, null, null);
        }
    }
}
