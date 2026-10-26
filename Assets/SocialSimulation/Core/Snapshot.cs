using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class SnapshotNumber
    {
        public string Key;
        public double Value;
    }

    [Serializable]
    public sealed class SnapshotInteger
    {
        public string Key;
        public int Value;
    }

    /// <summary>Serializable data only. Runtime callbacks, transforms, and navigation state are rebuilt after load.</summary>
    [Serializable]
    public sealed class SimulationSnapshot
    {
        public int SchemaVersion = 3;
        public string WorldId;
        public uint Seed;
        public double Time;
        public long TickIndex;
        public long Sequence;
        public double Accumulator;
        public double TickSeconds;
        public double TimeScale;
        public bool Paused;
        public int MaxCascadeDepth = 8;
        public int MaxEventsPerRoot = 128;
        public int MaxEffectsPerRoot = 128;
        public int TraceCapacity = 2048;
        public double DecisionMinimumCommitmentSeconds = 12;
        public float DecisionSwitchThreshold = 0.12f;
        public float DecisionTemperature = 0.08f;
        public double RelationDecayHalfLifeSeconds;
        public double RelationDecayGraceSeconds;
        public List<AgentState> Agents = new List<AgentState>();
        public List<PlaceState> Places = new List<PlaceState>();
        public List<WorldTask> Tasks = new List<WorldTask>();
        public List<CollectiveState> Collectives = new List<CollectiveState>();
        public List<OrganizationState> Organizations = new List<OrganizationState>();
        public List<Membership> Memberships = new List<Membership>();
        public List<RoleAssignment> Roles = new List<RoleAssignment>();
        public List<AuthorityGrant> Grants = new List<AuthorityGrant>();
        public List<SocialRelation> Relations = new List<SocialRelation>();
        public List<Commitment> Commitments = new List<Commitment>();
        public List<Obligation> Obligations = new List<Obligation>();
        public List<AgreementSettlement> AgreementSettlements = new List<AgreementSettlement>();
        public KnowledgeSnapshot Knowledge = new KnowledgeSnapshot();
        public List<ResourceSlot> Slots = new List<ResourceSlot>();
        public List<ResourceToken> Tokens = new List<ResourceToken>();
        public List<ResourceRequest> ResourceRequests = new List<ResourceRequest>();
        public List<LedgerEntry> Ledger = new List<LedgerEntry>();
        public List<SnapshotInteger> Balances = new List<SnapshotInteger>();
        public List<string> CommittedTransactions = new List<string>();
        public List<TransactionReceipt> TransactionReceipts = new List<TransactionReceipt>();
        public List<ActivitySession> Sessions = new List<ActivitySession>();
        public List<DutyAssignment> DutyAssignments = new List<DutyAssignment>();
        public List<NormDefinition> NormDefinitions = new List<NormDefinition>();
        public List<NormInstance> NormInstances = new List<NormInstance>();
        public List<ObservationEvidence> Evidence = new List<ObservationEvidence>();
        public List<ViolationRecord> Violations = new List<ViolationRecord>();
        public List<SnapshotNumber> DecisionCooldowns = new List<SnapshotNumber>();
        public List<ScheduledWorldEvent> ScheduledEvents = new List<ScheduledWorldEvent>();
        public List<WorldEvent> PendingEvents = new List<WorldEvent>();
        public List<WorldEvent> EventTrace = new List<WorldEvent>();
        public List<string> CommittedEffects = new List<string>();
        public List<SnapshotInteger> CascadeCounts = new List<SnapshotInteger>();
        public List<SnapshotInteger> CascadeEffectCounts = new List<SnapshotInteger>();
    }

    internal static class SnapshotClone
    {
        public static T Copy<T>(T item) { return (T)CopyObject(item); }

        private static object CopyObject(object item)
        {
            if (item == null) return null;
            var type = item.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return item;
            if (typeof(IList).IsAssignableFrom(type))
            {
                var source = (IList)item;
                var target = (IList)Activator.CreateInstance(type);
                foreach (var element in source) target.Add(CopyObject(element));
                return target;
            }
            var copy = Activator.CreateInstance(type);
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                field.SetValue(copy, CopyObject(field.GetValue(item)));
            return copy;
        }
    }

    public static class WorldPersistence
    {
        private static void AddCopies<T>(IEnumerable<T> source, List<T> target)
        { foreach (var item in source) target.Add(SnapshotClone.Copy(item)); }

        public static SimulationSnapshot Capture(SimulationWorld world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (world.IsStepping || world.IsCommittingEffect)
                throw new InvalidOperationException("Save only at a completed simulation tick and effect boundary.");
            var snapshot = new SimulationSnapshot { WorldId = world.WorldId, Seed = world.Seed,
                Time = world.Time, TickIndex = world.TickIndex, Sequence = world.CurrentSequence,
                Accumulator = world.Accumulator, TickSeconds = world.TickSeconds,
                TimeScale = world.TimeScale, Paused = world.Paused,
                MaxCascadeDepth = world.MaxCascadeDepth, MaxEventsPerRoot = world.MaxEventsPerRoot,
                MaxEffectsPerRoot = world.MaxEffectsPerRoot,
                TraceCapacity = world.TraceCapacity,
                DecisionMinimumCommitmentSeconds = world.Decisions.MinimumCommitmentSeconds,
                DecisionSwitchThreshold = world.Decisions.SwitchThreshold,
                DecisionTemperature = world.Decisions.Temperature,
                RelationDecayHalfLifeSeconds = world.Society.RelationDecayHalfLifeSeconds,
                RelationDecayGraceSeconds = world.Society.RelationDecayGraceSeconds };
            AddCopies(world.Agents.Values, snapshot.Agents);
            AddCopies(world.Places.Values, snapshot.Places);
            AddCopies(world.Tasks.Values, snapshot.Tasks);
            AddCopies(world.Society.Collectives.Values, snapshot.Collectives);
            AddCopies(world.Society.Organizations.Values, snapshot.Organizations);
            AddCopies(world.Society.Memberships.Values, snapshot.Memberships);
            AddCopies(world.Society.Roles.Values, snapshot.Roles);
            AddCopies(world.Society.Grants.Values, snapshot.Grants);
            AddCopies(world.Society.Relations.Values, snapshot.Relations);
            AddCopies(world.Society.Commitments.Values, snapshot.Commitments);
            AddCopies(world.Society.Obligations.Values, snapshot.Obligations);
            AddCopies(world.Agreements.Settlements, snapshot.AgreementSettlements);
            snapshot.Knowledge = world.Knowledge.Capture();
            AddCopies(world.Resources.Slots.Values, snapshot.Slots);
            AddCopies(world.Resources.Tokens.Values, snapshot.Tokens);
            AddCopies(world.Resources.Requests.Values, snapshot.ResourceRequests);
            AddCopies(world.Economy.Ledger, snapshot.Ledger);
            foreach (var item in world.Economy.CopyBalances()) snapshot.Balances.Add(new SnapshotInteger { Key = item.Key, Value = item.Value });
            snapshot.CommittedTransactions.AddRange(world.Economy.CopyTransactionIds());
            AddCopies(world.Economy.CopyTransactionReceipts(), snapshot.TransactionReceipts);
            AddCopies(world.Activities.Sessions.Values, snapshot.Sessions);
            AddCopies(world.Schedules.Assignments.Values, snapshot.DutyAssignments);
            AddCopies(world.Norms.Definitions.Values, snapshot.NormDefinitions);
            AddCopies(world.Norms.Instances.Values, snapshot.NormInstances);
            AddCopies(world.Norms.Evidence.Values, snapshot.Evidence);
            AddCopies(world.Norms.Violations.Values, snapshot.Violations);
            foreach (var item in world.Decisions.CopyCooldowns()) snapshot.DecisionCooldowns.Add(new SnapshotNumber { Key = item.Key, Value = item.Value });
            AddCopies(world.ScheduledEvents, snapshot.ScheduledEvents);
            AddCopies(world.CopyPending(), snapshot.PendingEvents);
            AddCopies(world.Trace, snapshot.EventTrace);
            snapshot.CommittedEffects.AddRange(world.CommittedEffects);
            foreach (var item in world.CopyCascadeCounts()) snapshot.CascadeCounts.Add(new SnapshotInteger { Key = item.Key, Value = item.Value });
            foreach (var item in world.CopyCascadeEffectCounts()) snapshot.CascadeEffectCounts.Add(new SnapshotInteger { Key = item.Key, Value = item.Value });
            return snapshot;
        }

        private static void RestoreCopies<T>(IEnumerable<T> source, Dictionary<string, T> target, Func<T, string> key)
        { foreach (var item in source) { var copy = SnapshotClone.Copy(item); target.Add(key(copy), copy); } }

        public static SimulationWorld Restore(SimulationSnapshot snapshot)
        {
            if (snapshot == null || snapshot.SchemaVersion != 3 || string.IsNullOrWhiteSpace(snapshot.WorldId))
                throw new InvalidOperationException("Unsupported or invalid simulation snapshot.");
            if (!SimulationWorld.Finite(snapshot.Time) || snapshot.Time < 0 || snapshot.TickIndex < 0 || snapshot.Sequence < 0 ||
                !SimulationWorld.Finite(snapshot.Accumulator) || snapshot.Accumulator < 0 ||
                !SimulationWorld.Finite(snapshot.TickSeconds) || snapshot.TickSeconds <= 0 ||
                !SimulationWorld.Finite(snapshot.TimeScale) || snapshot.TimeScale <= 0 ||
                snapshot.MaxCascadeDepth < 0 || snapshot.MaxEventsPerRoot < 1 || snapshot.MaxEffectsPerRoot < 1 || snapshot.TraceCapacity < 1 ||
                !SimulationWorld.Finite(snapshot.DecisionMinimumCommitmentSeconds) || snapshot.DecisionMinimumCommitmentSeconds < 0 ||
                !SimulationWorld.Finite(snapshot.DecisionSwitchThreshold) || snapshot.DecisionSwitchThreshold < 0 ||
                !SimulationWorld.Finite(snapshot.DecisionTemperature) || snapshot.DecisionTemperature < 0 ||
                !SimulationWorld.Finite(snapshot.RelationDecayHalfLifeSeconds) || snapshot.RelationDecayHalfLifeSeconds < 0 ||
                !SimulationWorld.Finite(snapshot.RelationDecayGraceSeconds) || snapshot.RelationDecayGraceSeconds < 0)
                throw new InvalidOperationException("Snapshot contains invalid clock, budget, or decision configuration.");
            var world = new SimulationWorld(snapshot.WorldId, snapshot.Seed) { TickSeconds = snapshot.TickSeconds,
                TimeScale = snapshot.TimeScale, Paused = snapshot.Paused,
                MaxCascadeDepth = snapshot.MaxCascadeDepth, MaxEventsPerRoot = snapshot.MaxEventsPerRoot,
                MaxEffectsPerRoot = snapshot.MaxEffectsPerRoot,
                TraceCapacity = snapshot.TraceCapacity };
            world.Decisions.MinimumCommitmentSeconds = snapshot.DecisionMinimumCommitmentSeconds;
            world.Decisions.SwitchThreshold = snapshot.DecisionSwitchThreshold;
            world.Decisions.Temperature = snapshot.DecisionTemperature;
            // Snapshots saved before relation decay have zeroed settings; keep the current defaults.
            if (snapshot.RelationDecayHalfLifeSeconds > 0 || snapshot.RelationDecayGraceSeconds > 0)
                world.Society.ConfigureRelationDecay(Math.Max(0, snapshot.RelationDecayHalfLifeSeconds),
                    Math.Max(0, snapshot.RelationDecayGraceSeconds));
            world.RestoreClock(snapshot.Time, snapshot.TickIndex, snapshot.Sequence, snapshot.Accumulator);
            RestoreCopies(snapshot.Agents, world.Agents, x => x.Id);
            RestoreCopies(snapshot.Places, world.Places, x => x.Id);
            RestoreCopies(snapshot.Tasks, world.Tasks, x => x.Id);
            RestoreCopies(snapshot.Collectives, world.Society.Collectives, x => x.Id);
            RestoreCopies(snapshot.Organizations, world.Society.Organizations, x => x.Id);
            RestoreCopies(snapshot.Memberships, world.Society.Memberships, x => x.Id);
            RestoreCopies(snapshot.Roles, world.Society.Roles, x => x.Id);
            RestoreCopies(snapshot.Grants, world.Society.Grants, x => x.Id);
            RestoreCopies(snapshot.Relations, world.Society.Relations, x => x.Id);
            RestoreCopies(snapshot.Commitments, world.Society.Commitments, x => x.Id);
            RestoreCopies(snapshot.Obligations, world.Society.Obligations, x => x.Id);
            world.Knowledge.Restore(snapshot.Knowledge);
            RestoreCopies(snapshot.Slots, world.Resources.Slots, x => x.Id);
            RestoreCopies(snapshot.Tokens, world.Resources.Tokens, x => x.Id);
            RestoreCopies(snapshot.ResourceRequests, world.Resources.Requests, x => x.Id);
            AddCopies(snapshot.Ledger, world.Economy.Ledger);
            var balances = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in snapshot.Balances) balances.Add(item.Key, item.Value);
            if (snapshot.TransactionReceipts == null ||
                snapshot.TransactionReceipts.Count != snapshot.CommittedTransactions.Count)
                throw new InvalidOperationException("Snapshot is missing transaction receipts; migration is required.");
            world.Economy.Restore(balances, snapshot.TransactionReceipts);
            world.Agreements.Restore(snapshot.AgreementSettlements);
            RestoreCopies(snapshot.Sessions, world.Activities.Sessions, x => x.Id);
            world.Schedules.Restore(snapshot.DutyAssignments);
            RestoreCopies(snapshot.NormDefinitions, world.Norms.Definitions, x => x.Id);
            RestoreCopies(snapshot.NormInstances, world.Norms.Instances, x => x.Id);
            RestoreCopies(snapshot.Evidence, world.Norms.Evidence, x => x.Id);
            RestoreCopies(snapshot.Violations, world.Norms.Violations, x => x.Id);
            world.Norms.RebuildViolationKeys();
            var cooldowns = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var item in snapshot.DecisionCooldowns) cooldowns.Add(item.Key, item.Value);
            world.Decisions.RestoreCooldowns(cooldowns);
            world.RestoreScheduled(snapshot.ScheduledEvents);
            world.RestorePending(snapshot.PendingEvents);
            AddCopies(snapshot.EventTrace, world.Trace);
            world.RestoreEffects(snapshot.CommittedEffects);
            var cascade = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in snapshot.CascadeCounts) cascade.Add(item.Key, item.Value);
            world.RestoreCascadeCounts(cascade);
            var effectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in snapshot.CascadeEffectCounts) effectCounts.Add(item.Key, item.Value);
            world.RestoreCascadeEffectCounts(effectCounts);
            return world;
        }
    }
}
