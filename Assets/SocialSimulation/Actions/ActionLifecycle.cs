using System;
using System.Collections.Generic;
using SocialSimulation.Core;

namespace SocialSimulation.Actions
{
    public enum ActionPhase
    {
        Created, Validating, Reserving, Moving, Aligning, Executing,
        Completed, Failed, Cancelled
    }

    public enum ActionStepResult { Running, Success, Failure }
    public enum ActionExitReason { Completed, Failed, Cancelled }

    public enum ActionFailureCode
    {
        None, UnknownActor, UnknownAction, MissingAdapter, InvalidTarget,
        ResourceUnavailable, NavigationFailed, AlignmentFailed, ExecutionFailed,
        TimedOut, Cancelled, AdapterException, InvalidGraphContext,
        InvalidActionDefinition, MissingCapability, RestoreRejected,
        GraphRuntimeException
    }

    /// <summary>Human-readable reason for the latest failed action in a graph Tick.</summary>
    public sealed class ActionFailureDiagnostic
    {
        public readonly string ActorId;
        public readonly string GraphId;
        public readonly int GraphVersion;
        public readonly string GraphRevision;
        public readonly string GraphInstanceId;
        public readonly string NodeId;
        public readonly string ActionId;
        public readonly string ActionInstanceId;
        public readonly ActionPhase Phase;
        public readonly ActionFailureCode Code;
        public readonly long Tick;
        public readonly string Message;

        internal ActionFailureDiagnostic(ActionInstance instance, long tick, string message)
        {
            ActorId = instance.ActorId;
            GraphId = instance.GraphId;
            GraphVersion = instance.GraphVersion;
            GraphRevision = instance.GraphRevision;
            GraphInstanceId = instance.GraphInstanceId;
            NodeId = instance.NodeId;
            ActionId = instance.ActionId;
            ActionInstanceId = instance.Id;
            Phase = instance.Phase;
            Code = instance.FailureCode;
            Tick = tick;
            Message = message;
        }

        public override string ToString()
        {
            return Code + " at " + GraphId + "@" + GraphVersion +
                (string.IsNullOrEmpty(GraphRevision) ? "" :
                    "#" + GraphRevision.Substring(0, Math.Min(12, GraphRevision.Length))) +
                "/" + NodeId +
                " (actor " + ActorId + ", tick " + Tick + "): " + Message;
        }
    }

    /// <summary>Runtime progress for one graph leaf invocation. A new invocation receives a new ID.</summary>
    public sealed class ActionInstance
    {
        private readonly List<string> resourceTokenIds = new List<string>();
        private readonly HashSet<string> committedEffects = new HashSet<string>(StringComparer.Ordinal);

        internal ActionInstance(string id, string actorId, string sessionId, string graphInstanceId, string graphId,
            int graphVersion, string graphRevision, string nodeId, string actionId, string targetId, string scopeId,
            double startedAt, double deadline)
        {
            Id = id;
            ActorId = actorId;
            SessionId = sessionId;
            GraphInstanceId = graphInstanceId;
            GraphId = graphId;
            GraphVersion = graphVersion;
            GraphRevision = graphRevision;
            NodeId = nodeId;
            ActionId = actionId;
            TargetId = targetId;
            ScopeId = scopeId;
            StartedAt = startedAt;
            Deadline = deadline;
            Phase = ActionPhase.Created;
        }

        public string Id { get; private set; }
        public string ActorId { get; private set; }
        public string SessionId { get; private set; }
        public string GraphInstanceId { get; private set; }
        public string GraphId { get; private set; }
        public int GraphVersion { get; private set; }
        public string GraphRevision { get; private set; }
        public string NodeId { get; private set; }
        public string ActionId { get; private set; }
        public string TargetId { get; private set; }
        public string ScopeId { get; private set; }
        public double StartedAt { get; private set; }
        public double Deadline { get; private set; }
        public ActionPhase Phase { get; internal set; }
        public ActionFailureCode FailureCode { get; internal set; }
        public int CancellationGeneration { get; internal set; }
        public IReadOnlyList<string> ResourceTokenIds { get { return resourceTokenIds.AsReadOnly(); } }
        public IReadOnlyCollection<string> CommittedEffects { get { return committedEffects; } }

        public bool IsTerminal
        {
            get { return Phase == ActionPhase.Completed || Phase == ActionPhase.Failed || Phase == ActionPhase.Cancelled; }
        }

        public bool AcceptsCallback(int generation)
        {
            return !IsTerminal && CancellationGeneration == generation;
        }

        internal void Track(ResourceToken token)
        {
            if (token != null && !resourceTokenIds.Contains(token.Id)) resourceTokenIds.Add(token.Id);
        }

        internal void RestoreTokenId(string tokenId)
        {
            if (!string.IsNullOrEmpty(tokenId) && !resourceTokenIds.Contains(tokenId))
                resourceTokenIds.Add(tokenId);
        }

        internal void MarkEffect(string phase) { committedEffects.Add(phase); }
    }

    /// <summary>Owns resources that must survive successful leaf nodes until the composite action ends.</summary>
    public sealed class ActionScope
    {
        private readonly SimulationWorld world;

        internal ActionScope(SimulationWorld world, string id, string actorId)
        {
            this.world = world;
            Id = id;
            ActorId = actorId;
        }

        public string Id { get; private set; }
        public string ActorId { get; private set; }
        public bool IsClosed { get; private set; }

        public ResourceToken TryReserve(string slotId, double leaseSeconds)
        {
            if (IsClosed) return null;
            return world.Resources.TryAcquire(slotId, Id, ActorId, leaseSeconds);
        }

        public bool TryTransfer(string tokenId, string newOwnerScopeId)
        {
            if (IsClosed || string.IsNullOrWhiteSpace(newOwnerScopeId)) return false;
            return world.Resources.Transfer(tokenId, Id, newOwnerScopeId);
        }

        public void Close()
        {
            if (IsClosed) return;
            IsClosed = true;
            world.Resources.ReleaseScope(Id);
        }
    }

    public sealed class ActionExecutionContext
    {
        internal ActionExecutionContext(SimulationWorld world, ActionInstance instance,
            ActionDefinition definition, ActionScope scope,
            IReadOnlyDictionary<string, string> parameters, long tick)
        {
            World = world;
            Instance = instance;
            Definition = definition;
            Scope = scope;
            Parameters = parameters;
            Tick = tick;
        }

        public SimulationWorld World { get; private set; }
        public ActionInstance Instance { get; private set; }
        public ActionDefinition Definition { get; private set; }
        public ActionScope Scope { get; private set; }
        public IReadOnlyDictionary<string, string> Parameters { get; private set; }
        public long Tick { get; private set; }

        public string Get(string key, string fallback = "")
        {
            string value;
            return Parameters != null && Parameters.TryGetValue(key, out value) ? value : fallback;
        }

        public ActionStepResult Fail(ActionFailureCode code)
        {
            Instance.FailureCode = code == ActionFailureCode.None ? ActionFailureCode.ExecutionFailed : code;
            return ActionStepResult.Failure;
        }

        public ResourceToken TryReserveLocal(string slotId, double leaseSeconds)
        {
            if (Instance.IsTerminal) return null;
            var token = World.Resources.TryAcquire(slotId, Instance.Id, Instance.ActorId, leaseSeconds);
            Instance.Track(token);
            return token;
        }

        public ResourceToken TryReserveForScope(string slotId, double leaseSeconds)
        {
            if (Instance.IsTerminal) return null;
            var token = Scope.TryReserve(slotId, leaseSeconds);
            Instance.Track(token);
            return token;
        }

        public bool TryTransferToSession(string tokenId, string sessionScopeId)
        {
            if (Instance.IsTerminal || string.IsNullOrWhiteSpace(tokenId) ||
                string.IsNullOrWhiteSpace(sessionScopeId)) return false;
            if (!World.Resources.Tokens.TryGetValue(tokenId, out var token) ||
                !token.Active || token.AgentId != Instance.ActorId ||
                !Contains(Instance.ResourceTokenIds, tokenId)) return false;
            if (token.OwnerScopeId != Instance.Id && token.OwnerScopeId != Scope.Id) return false;
            return World.Activities.AttachResource(sessionScopeId, Instance.ActorId, tokenId, token.OwnerScopeId);
        }

        /// <summary>Commits an effect once per action instance and phase, including across repeated callbacks.</summary>
        public bool TryCommitEffect(string phase, Action effect)
        {
            return TryCommitEffect(phase, Instance.CancellationGeneration, effect);
        }

        public bool TryCommitEffect(string phase, int generation, Action effect)
        {
            if (string.IsNullOrWhiteSpace(phase) || effect == null || !Instance.AcceptsCallback(generation)) return false;
            var key = Instance.Id + "|effect|" + phase;
            if (!World.TryCommitEffect(key, effect)) return false;
            Instance.MarkEffect(phase);
            return true;
        }

        public bool AcceptsCallback(int generation) { return Instance.AcceptsCallback(generation); }

        private static bool Contains(IReadOnlyList<string> ids, string id)
        {
            for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return true;
            return false;
        }
    }

    /// <summary>One executor performs the semantic phases of one or more registered action definitions.</summary>
    public interface IWorldActionAdapter
    {
        ActionStepResult Step(ActionExecutionContext context, ActionPhase phase);
        void OnExit(ActionExecutionContext context, ActionExitReason reason);
    }

    /// <summary>Optional hook to discard scene-only state before a restored action revalidates.</summary>
    public interface IWorldActionRestoreAdapter
    {
        void OnRestored(ActionExecutionContext context, ActionPhase savedPhase);
    }

    /// <summary>Optional scene-level target validation before a checkpoint can resume.</summary>
    public interface IWorldActionRestoreValidator
    {
        bool CanRestore(ActionExecutionContext context);
    }
}
