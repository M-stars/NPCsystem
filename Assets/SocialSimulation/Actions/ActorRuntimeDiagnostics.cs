using System;
using System.Collections.Generic;

namespace SocialSimulation.Actions
{
    /// <summary>A read-only view of one actor's graph execution, independent of its domain.</summary>
    public sealed class ActorRuntimeSnapshot
    {
        public string ActorId { get; }
        public string GraphId { get; }
        public int GraphVersion { get; }
        public string GraphRevision { get; }
        public string GraphInstanceId { get; }
        public string NodeId { get; }
        public string ActionId { get; }
        public string ActionInstanceId { get; }
        public ActionPhase? Phase { get; }
        public ActionFailureDiagnostic LastFailure { get; }

        public ActorRuntimeSnapshot(string actorId, string graphId, int graphVersion,
            string graphRevision, string graphInstanceId, string nodeId = null, string actionId = null,
            string actionInstanceId = null, ActionPhase? phase = null,
            ActionFailureDiagnostic lastFailure = null)
        {
            if (string.IsNullOrWhiteSpace(actorId))
                throw new ArgumentException("Actor ID is required.", nameof(actorId));
            ActorId = actorId;
            GraphId = graphId;
            GraphVersion = graphVersion;
            GraphRevision = graphRevision;
            GraphInstanceId = graphInstanceId;
            NodeId = nodeId;
            ActionId = actionId;
            ActionInstanceId = actionInstanceId;
            Phase = phase;
            LastFailure = lastFailure;
        }
    }

    /// <summary>Optional provider implemented by any scene domain that owns actor graphs.</summary>
    public interface ISocialActorRuntimeDiagnostics
    {
        IReadOnlyList<ActorRuntimeSnapshot> GetActorRuntimeSnapshots(string actorId);
    }
}
