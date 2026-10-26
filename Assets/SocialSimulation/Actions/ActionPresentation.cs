namespace SocialSimulation.Actions
{
    public enum ActionPresentationMoment { Entered, PhaseChanged, Exited, Restored }

    /// <summary>Immutable semantic notification; visual listeners cannot commit world effects.</summary>
    public sealed class ActionPresentationEvent
    {
        public string ActorId { get; }
        public string GraphId { get; }
        public int GraphVersion { get; }
        public string GraphRevision { get; }
        public string GraphInstanceId { get; }
        public string NodeId { get; }
        public string ActionId { get; }
        public string ActionInstanceId { get; }
        public ActionPresentationMoment Moment { get; }
        public ActionPhase Phase { get; }
        public ActionPhase? PreviousPhase { get; }
        public ActionExitReason? ExitReason { get; }
        public int CancellationGeneration { get; }

        internal ActionPresentationEvent(ActionInstance action,
            ActionPresentationMoment moment, ActionPhase? previousPhase = null,
            ActionExitReason? exitReason = null)
        {
            ActorId = action.ActorId;
            GraphId = action.GraphId;
            GraphVersion = action.GraphVersion;
            GraphRevision = action.GraphRevision;
            GraphInstanceId = action.GraphInstanceId;
            NodeId = action.NodeId;
            ActionId = action.ActionId;
            ActionInstanceId = action.Id;
            Moment = moment;
            Phase = action.Phase;
            PreviousPhase = previousPhase;
            ExitReason = exitReason;
            CancellationGeneration = action.CancellationGeneration;
        }
    }

    /// <summary>Optional consumer for animation, sound, or view state; never controls action success.</summary>
    public interface IWorldActionPresentation
    {
        void OnActionPresentation(ActionPresentationEvent entry);
    }
}
