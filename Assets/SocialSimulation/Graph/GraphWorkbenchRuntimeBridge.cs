using System.Collections.Generic;

namespace SocialSimulation.Graph
{
    public enum GraphApplyMode { ForNewInstances, RestartAffected }

    public sealed class GraphRuntimeState
    {
        public readonly string DocumentId;
        public readonly GraphKind Kind;
        public readonly int Version;
        public readonly string Revision;
        public readonly IReadOnlyList<string> ActiveVersions;

        public GraphRuntimeState(string documentId, GraphKind kind, int version, string revision,
            IReadOnlyList<string> activeVersions = null)
        {
            DocumentId = documentId; Kind = kind; Version = version; Revision = revision;
            ActiveVersions = activeVersions == null ? (IReadOnlyList<string>)System.Array.Empty<string>() :
                new List<string>(activeVersions).AsReadOnly();
        }
    }

    /// <summary>A module can own multiple graphs. Ownership is resolved by stable document ID.</summary>
    public interface ISocialGraphHost
    {
        bool TryGetGraphState(string documentId, GraphKind kind, out GraphRuntimeState state);
        bool TryApplyGraph(GraphDocument draft, GraphApplyMode mode, out string error);
    }

    /// <summary>A scene domain module implements this to publish validated graph drafts from the editor.</summary>
    public interface IGraphWorkbenchRuntimeBridge
    {
        string CurrentBehaviorGraphId { get; }
        int CurrentBehaviorGraphVersion { get; }
        string CurrentBehaviorGraphRevision { get; }
        string CurrentRuleGraphId { get; }
        int CurrentRuleGraphVersion { get; }
        string CurrentRuleGraphRevision { get; }
        IReadOnlyList<string> ActiveBehaviorGraphVersions { get; }
        bool TryApplyBehaviorGraph(GraphDocument draft, GraphApplyMode mode, out string error);
        bool TryApplyRuleGraph(GraphDocument draft, out string error);
    }
}
