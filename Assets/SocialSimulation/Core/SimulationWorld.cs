using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class ScheduledWorldEvent
    {
        public string Id;
        public double At;
        public string Type;
        public string EmitterId;
        public string SubjectId;
        public string PlaceId;
        public string Payload;
        public bool HasParent;
        public WorldEvent Parent;
    }

    /// <summary>A single authority for time, identity, events, and mutable simulation services.</summary>
    public sealed class SimulationWorld
    {
        private readonly List<IWorldSystem> systems = new List<IWorldSystem>();
        private readonly List<ScheduledWorldEvent> scheduled = new List<ScheduledWorldEvent>();
        private readonly Queue<WorldEvent> pendingEvents = new Queue<WorldEvent>();
        private readonly Dictionary<string, int> cascadeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> cascadeEffectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> committedEffects = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> committingEffects = new HashSet<string>(StringComparer.Ordinal);
        private long sequence;
        private double accumulator;
        private WorldEvent effectParent;
        public bool IsStepping { get; private set; }

        public string WorldId { get; private set; }
        public uint Seed { get; private set; }
        public double Time { get; private set; }
        public long TickIndex { get; private set; }
        public double TickSeconds { get; set; } = 1.0;
        public double TimeScale { get; set; } = 1.0;
        public bool Paused { get; set; }
        public int MaxCascadeDepth { get; set; } = 8;
        public int MaxEventsPerRoot { get; set; } = 128;
        public int MaxEffectsPerRoot { get; set; } = 128;
        public int TraceCapacity { get; set; } = 2048;

        public readonly Dictionary<string, AgentState> Agents = new Dictionary<string, AgentState>(StringComparer.Ordinal);
        public readonly Dictionary<string, PlaceState> Places = new Dictionary<string, PlaceState>(StringComparer.Ordinal);
        public readonly Dictionary<string, WorldTask> Tasks = new Dictionary<string, WorldTask>(StringComparer.Ordinal);
        public readonly List<WorldEvent> Trace = new List<WorldEvent>();
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        public ResourceService Resources { get; private set; }
        public EconomyService Economy { get; private set; }
        public SocietyService Society { get; private set; }
        public ActivityService Activities { get; private set; }
        public NormService Norms { get; private set; }
        public DecisionService Decisions { get; private set; }
        public ScheduleService Schedules { get; private set; }
        public KnowledgeService Knowledge { get; private set; }
        public GovernanceService Governance { get; private set; }
        public AgreementLifecycleService Agreements { get; private set; }
        public GoalReevaluationService GoalReevaluation { get; private set; }
        public event Action<WorldEvent> EventPublished;

        public SimulationWorld(string worldId, uint seed = 1)
        {
            if (string.IsNullOrWhiteSpace(worldId)) throw new ArgumentException("World ID is required.");
            WorldId = worldId;
            Seed = seed == 0 ? 1u : seed;
            Resources = new ResourceService(this);
            Economy = new EconomyService(this);
            Society = new SocietyService(this);
            Activities = new ActivityService(this);
            Norms = new NormService(this);
            Decisions = new DecisionService(this);
            Schedules = new ScheduleService(this);
            Knowledge = new KnowledgeService(this);
            Governance = new GovernanceService(this);
            Agreements = new AgreementLifecycleService(this);
            GoalReevaluation = new GoalReevaluationService(this);
        }

        public string NextId() { return Identity.Instance(WorldId, ++sequence); }

        public AgentState AddAgent(AgentState agent)
        {
            if (agent == null) throw new ArgumentNullException(nameof(agent));
            if (string.IsNullOrWhiteSpace(agent.Id)) agent.Id = NextId();
            if (Agents.ContainsKey(agent.Id)) throw new InvalidOperationException("Duplicate agent ID: " + agent.Id);
            if (agent.DecisionRandomState == 0) agent.DecisionRandomState = DecisionService.SeedFor(Seed, agent.Id);
            Agents.Add(agent.Id, agent);
            Emit("agent.created", agent.Id, agent.Id, agent.TemplateId);
            return agent;
        }

        public void RemoveAgent(string agentId)
        {
            if (!Agents.ContainsKey(agentId)) return;
            Resources.ReleaseAgent(agentId);
            Activities.LeaveAll(agentId);
            Schedules.CancelForAgent(agentId, "agent.removed");
            Knowledge.RemoveAgent(agentId);
            Governance.CancelForAgent(agentId, "agent.removed");
            var membershipIds = new List<string>(Agents[agentId].MembershipIds);
            foreach (var id in membershipIds)
                if (Society.Memberships.TryGetValue(id, out var member) && member.Active) Society.Leave(agentId, member.CollectiveId);
            foreach (var task in Tasks.Values) if (task.AssigneeId == agentId && task.Status == "Pending") task.Status = "Cancelled";
            Agents.Remove(agentId);
            Emit("agent.removed", agentId, agentId, null);
        }

        public void AddSystem(IWorldSystem system)
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            if (systems.Contains(system)) return;
            int index = systems.FindIndex(existing => existing.Order > system.Order);
            if (index < 0) systems.Add(system); else systems.Insert(index, system);
        }

        /// <summary>Detach a module's system; a removed system is skipped even during the current tick.</summary>
        public bool RemoveSystem(IWorldSystem system)
        { return system != null && systems.Remove(system); }

        public ScheduledWorldEvent Schedule(double at, string type, string emitterId, string subjectId, string placeId = null, string payload = null,
            WorldEvent parent = null)
        {
            if (!Finite(at) || at < Time) throw new ArgumentOutOfRangeException(nameof(at));
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Event type is required.", nameof(type));
            parent = parent ?? effectParent;
            if (parent != null)
            {
                if (string.IsNullOrWhiteSpace(parent.RootCauseId) || !cascadeCounts.TryGetValue(parent.RootCauseId, out var count))
                { CascadeDiagnostic("CASCADE_CLOSED", parent, type, "Cannot schedule from a completed or unknown root cause."); return null; }
                int reserved = 0;
                foreach (var pending in scheduled) if (pending.HasParent && pending.Parent?.RootCauseId == parent.RootCauseId) reserved++;
                if (parent.Depth >= MaxCascadeDepth || count + reserved >= MaxEventsPerRoot)
                { CascadeDiagnostic("CASCADE_BUDGET", parent, type, "Scheduled event exceeds root cause budget."); return null; }
            }
            var item = new ScheduledWorldEvent { Id = NextId(), At = at, Type = type, EmitterId = emitterId,
                SubjectId = subjectId, PlaceId = placeId, Payload = payload,
                HasParent = parent != null, Parent = SnapshotClone.Copy(parent) };
            scheduled.Add(item);
            scheduled.Sort((a, b) => { int time = a.At.CompareTo(b.At); return time != 0 ? time : StringComparer.Ordinal.Compare(a.Id, b.Id); });
            return item;
        }

        public void Advance(double realSeconds)
        {
            if (IsStepping || committingEffects.Count > 0)
                throw new InvalidOperationException("Time cannot advance during a tick or effect callback.");
            if (!Finite(realSeconds)) throw new ArgumentOutOfRangeException(nameof(realSeconds));
            if (Paused || realSeconds <= 0) return;
            if (!Finite(TickSeconds) || !Finite(TimeScale) || TickSeconds <= 0 || TimeScale <= 0 ||
                !Finite(accumulator + realSeconds * TimeScale)) throw new InvalidOperationException("Time settings and elapsed time must be finite and positive.");
            accumulator += realSeconds * TimeScale;
            while (accumulator >= TickSeconds)
            {
                if (Paused) break;
                double tick = TickSeconds;
                accumulator -= tick;
                StepOne();
            }
        }

        /// <summary>Runs exactly one logical tick even when paused.</summary>
        public void StepOne()
        {
            if (IsStepping || committingEffects.Count > 0)
                throw new InvalidOperationException("A simulation tick cannot be entered recursively or during an effect callback.");
            if (!Finite(TickSeconds) || TickSeconds <= 0 || !Finite(Time + TickSeconds))
                throw new InvalidOperationException("Tick duration and resulting time must be finite and positive.");
            IsStepping = true;
            try
            {
            Time += TickSeconds;
            TickIndex++;
            while (scheduled.Count > 0 && scheduled[0].At <= Time)
            {
                var item = scheduled[0];
                scheduled.RemoveAt(0);
                Emit(item.Type, item.EmitterId, item.SubjectId, item.Payload, item.PlaceId,
                    parent: item.HasParent ? item.Parent : null);
            }
            Resources.Expire();
            Activities.Tick();
            var agentIds = new List<string>(Agents.Keys);
            agentIds.Sort(StringComparer.Ordinal);
            foreach (var agentId in agentIds)
            {
                var agent = Agents[agentId];
                agent.Needs.Advance(TickSeconds);
                agent.Emotion.Decay(TickSeconds);
            }
            var tickSystems = systems.ToArray();
            foreach (var system in tickSystems) if (systems.Contains(system)) system.Tick(this, TickSeconds);
            Knowledge.Tick();
            Society.Tick();
            Governance.Tick();
            Agreements.Tick();
            int count = pendingEvents.Count;
            for (int i = 0; i < count; i++)
            {
                var item = pendingEvents.Dequeue();
                EventPublished?.Invoke(item);
            }
            // Keep a root's budget only while its causal chain is still queued.
            var activeRoots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var queued in pendingEvents) activeRoots.Add(queued.RootCauseId);
            foreach (var queued in scheduled) if (queued.HasParent && queued.Parent != null) activeRoots.Add(queued.Parent.RootCauseId);
            if (cascadeCounts.Count > activeRoots.Count)
            {
                var expiredRoots = new List<string>();
                foreach (var root in cascadeCounts.Keys) if (!activeRoots.Contains(root)) expiredRoots.Add(root);
                foreach (var root in expiredRoots) { cascadeCounts.Remove(root); cascadeEffectCounts.Remove(root); }
            }
            }
            finally { IsStepping = false; }
        }

        public WorldEvent Emit(string type, string emitterId, string subjectId, string payload,
            string placeId = null, string evidenceId = null, WorldEvent parent = null)
        {
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Event type is required.");
            parent = parent ?? effectParent;
            if (parent != null && (string.IsNullOrWhiteSpace(parent.RootCauseId) || !cascadeCounts.ContainsKey(parent.RootCauseId)))
            { CascadeDiagnostic("CASCADE_CLOSED", parent, type, "Cannot reopen a completed or unknown root cause; schedule delayed causal events before the root closes."); return null; }
            var id = NextId();
            var root = parent == null ? id : parent.RootCauseId;
            int depth = parent == null ? 0 : parent.Depth + 1;
            int count = cascadeCounts.TryGetValue(root, out var old) ? old : 0;
            if (depth > MaxCascadeDepth || count >= MaxEventsPerRoot)
            {
                CascadeDiagnostic("CASCADE_BUDGET", parent, type, "Event exceeded cascade budget; root=" + root + ", depth=" + depth + ", events=" + count + ".");
                return null;
            }
            cascadeCounts[root] = count + 1;
            var item = new WorldEvent { Id = id, RootCauseId = root, ParentEventId = parent?.Id,
                Time = Time, Type = type, EmitterId = emitterId, SubjectId = subjectId,
                PlaceId = placeId, EvidenceId = evidenceId, Payload = payload, Depth = depth };
            pendingEvents.Enqueue(item);
            Trace.Add(item);
            if (Trace.Count > TraceCapacity) Trace.RemoveRange(0, Trace.Count - TraceCapacity);
            return item;
        }

        public bool TryCommitEffect(string idempotencyKey, Action effect, WorldEvent parent = null)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey) || effect == null) return false;
            if (committedEffects.Contains(idempotencyKey) || !committingEffects.Add(idempotencyKey)) return false;
            parent = parent ?? effectParent;
            var previousParent = effectParent;
            try
            {
                if (parent != null)
                {
                    if (string.IsNullOrWhiteSpace(parent.RootCauseId) || !cascadeCounts.ContainsKey(parent.RootCauseId))
                    { CascadeDiagnostic("CASCADE_CLOSED", parent, idempotencyKey, "Effect root cause is completed or unknown."); return false; }
                    int count = cascadeEffectCounts.TryGetValue(parent.RootCauseId, out var previous) ? previous : 0;
                    if (count >= MaxEffectsPerRoot)
                    { CascadeDiagnostic("CASCADE_EFFECT_BUDGET", parent, idempotencyKey, "Effect exceeded root cause budget."); return false; }
                    cascadeEffectCounts[parent.RootCauseId] = count + 1;
                }
                // Services called by this command inherit its cause even when their Emit/Schedule
                // calls do not know about the rule host. This context lasts only for this callback.
                effectParent = parent;
                effect();
                committedEffects.Add(idempotencyKey);
                return true;
            }
            finally { effectParent = previousParent; committingEffects.Remove(idempotencyKey); }
        }

        public WorldTask CreateTask(string assigneeId, string issuerId, string kind, string targetId, double dueTime, string commitmentId = null)
        {
            if (!Agents.ContainsKey(assigneeId)) throw new InvalidOperationException("Unknown assignee.");
            var task = new WorldTask { Id = NextId(), AssigneeId = assigneeId, IssuerId = issuerId,
                Kind = kind, TargetId = targetId, DueTime = dueTime, CommitmentId = commitmentId };
            Tasks.Add(task.Id, task);
            Agents[assigneeId].TaskIds.Add(task.Id);
            Emit("task.created", issuerId, assigneeId, task.Id);
            return task;
        }

        public IReadOnlyList<ScheduledWorldEvent> ScheduledEvents { get { return scheduled; } }
        public IReadOnlyCollection<string> CommittedEffects { get { return committedEffects; } }
        public long CurrentSequence { get { return sequence; } }
        internal void RestoreClock(double time, long tickIndex, long restoredSequence, double restoredAccumulator)
        { Time = time; TickIndex = tickIndex; sequence = restoredSequence; accumulator = restoredAccumulator; }
        internal void RestoreScheduled(IEnumerable<ScheduledWorldEvent> events)
        {
            scheduled.Clear();
            foreach (var item in events)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Type) ||
                    !Finite(item.At) || item.At < Time ||
                    (item.HasParent && (item.Parent == null || string.IsNullOrWhiteSpace(item.Parent.Id) ||
                        string.IsNullOrWhiteSpace(item.Parent.RootCauseId) || item.Parent.Depth < 0)))
                    throw new InvalidOperationException("Snapshot contains an invalid scheduled event or causal parent.");
                var copy = SnapshotClone.Copy(item);
                // Unity's JsonUtility represents a null serializable class as an empty object.
                // The explicit discriminator is authoritative; reference nullness is not.
                if (!copy.HasParent) copy.Parent = null;
                scheduled.Add(copy);
            }
            scheduled.Sort((a, b) => { int order = a.At.CompareTo(b.At); return order != 0 ? order : StringComparer.Ordinal.Compare(a.Id, b.Id); }); }
        internal void RestoreEffects(IEnumerable<string> effects)
        { committedEffects.Clear(); foreach (var key in effects) committedEffects.Add(key); }
        internal void RestorePending(IEnumerable<WorldEvent> events)
        { pendingEvents.Clear(); foreach (var item in events) pendingEvents.Enqueue(SnapshotClone.Copy(item)); }
        internal List<WorldEvent> CopyPending() { return new List<WorldEvent>(pendingEvents); }
        internal Dictionary<string, int> CopyCascadeCounts() { return new Dictionary<string, int>(cascadeCounts, StringComparer.Ordinal); }
        internal Dictionary<string, int> CopyCascadeEffectCounts() { return new Dictionary<string, int>(cascadeEffectCounts, StringComparer.Ordinal); }
        internal void RestoreCascadeEffectCounts(Dictionary<string, int> values)
        { cascadeEffectCounts.Clear(); foreach (var item in values) cascadeEffectCounts.Add(item.Key, item.Value); }
        internal void RestoreCascadeCounts(Dictionary<string, int> values)
        { cascadeCounts.Clear(); foreach (var item in values) cascadeCounts.Add(item.Key, item.Value); }
        internal double Accumulator { get { return accumulator; } }
        internal bool IsCommittingEffect { get { return committingEffects.Count > 0; } }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private void CascadeDiagnostic(string code, WorldEvent parent, string attempted, string reason)
        { Diagnostics.Add(new Diagnostic(code, parent?.RootCauseId ?? attempted,
            reason + " Parent=" + (parent?.Id ?? "<root>") + ", attempted=" + attempted + ".")); }
    }
}
