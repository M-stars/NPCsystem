using System;
using System.Collections.Generic;
using SocialSimulation.Graph;

namespace SocialSimulation.Actions
{
    /// <summary>Serializable identity and ownership of a token held while an action was running.</summary>
    [Serializable]
    public sealed class ActionTokenCheckpoint
    {
        public string TokenId;
        public string OwnerScopeId;
        public string SlotId;
        public string AgentId;
    }

    /// <summary>Serializable state of one active graph action. Store with the same world snapshot.</summary>
    [Serializable]
    public sealed class ActionCheckpoint
    {
        public string ActionInstanceId;
        public string ActorId;
        public string SessionId;
        public string GraphInstanceId;
        public string GraphId;
        public int GraphVersion;
        public string GraphRevision;
        public string NodeId;
        public string ActionId;
        public string ExecutorId;
        public string CapabilityId;
        public string TargetKind;
        public string ResourceKind;
        public double TimeoutSeconds;
        public int RetryBudget;
        public bool Required;
        public string TargetId;
        public string ScopeId;
        public ActionPhase Phase;
        public ActionFailureCode FailureCode;
        public double StartedAt;
        public double Deadline;
        public int CancellationGeneration;
        public List<string> ResourceTokenIds = new List<string>();
        public List<ActionTokenCheckpoint> OwnedTokens = new List<ActionTokenCheckpoint>();
        public List<string> CommittedEffects = new List<string>();
    }

    /// <summary>Graph and action state captured at the same simulation Tick as the world snapshot.</summary>
    [Serializable]
    public sealed class ActionGraphCheckpoint
    {
        public int SchemaVersion = 3;
        public string WorldId;
        public long WorldTick;
        public double WorldTime;
        public string ActorId;
        public string GraphInstanceId;
        public string ScopeId;
        public BehaviorCheckpoint Behavior;
        public List<string> ScopeTokenIds = new List<string>();
        public List<ActionCheckpoint> Actions = new List<ActionCheckpoint>();
    }
}
