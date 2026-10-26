using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    /// <summary>IDs are persisted values; neither Unity instance IDs nor scene names are identities.</summary>
    public static class Identity
    {
        public static string Definition(string packageId, string localId)
        {
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(localId))
                throw new ArgumentException("Package and local identifiers are required.");
            return packageId.Trim() + ":" + localId.Trim();
        }

        public static string Instance(string worldId, long sequence)
        {
            if (string.IsNullOrWhiteSpace(worldId) || sequence <= 0)
                throw new ArgumentException("A world ID and positive sequence are required.");
            return worldId + "/" + sequence.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string SceneObject(string regionInstanceId, string localId)
        {
            return Definition(regionInstanceId, localId);
        }
    }

    [Serializable]
    public sealed class Diagnostic
    {
        public string Code;
        public string Path;
        public string Message;

        public Diagnostic(string code, string path, string message)
        {
            Code = code;
            Path = path;
            Message = message;
        }

        public override string ToString() { return Code + " at " + Path + ": " + Message; }
    }

    [Serializable]
    public sealed class WorldEvent
    {
        public string Id;
        public string RootCauseId;
        public string ParentEventId;
        public double Time;
        public string Type;
        public string EmitterId;
        public string SubjectId;
        public string PlaceId;
        public string EvidenceId;
        public string Payload;
        public int Depth;
    }

    [Serializable]
    public sealed class WorldTask
    {
        public string Id;
        public string AssigneeId;
        public string IssuerId;
        public string Kind;
        public string TargetId;
        public string CommitmentId;
        public string OrganizationId;
        public double CreatedAt;
        public double AcceptedAt;
        public double ClosedAt;
        public string LastReason;
        public string RequestKey;
        public string PlaceId;
        public double DueTime;
        public string Status = "Pending";
    }

    public interface IWorldSystem
    {
        int Order { get; }
        void Tick(SimulationWorld world, double deltaSeconds);
    }

    public sealed class StableComparer : IComparer<string>
    {
        public static readonly StableComparer Instance = new StableComparer();
        public int Compare(string x, string y) { return StringComparer.Ordinal.Compare(x, y); }
    }
}
