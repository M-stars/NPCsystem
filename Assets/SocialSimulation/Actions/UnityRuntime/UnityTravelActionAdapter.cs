using System;
using SocialSimulation.UnityRuntime;

namespace SocialSimulation.Actions
{
    /// <summary>Requests travel once, then accepts only the matching navigation generation and world arrival.</summary>
    public sealed class UnityTravelActionAdapter : IWorldActionAdapter,
        IWorldActionRestoreAdapter, IWorldActionRestoreValidator
    {
        private readonly SocialWorldRunner runner;

        public UnityTravelActionAdapter(SocialWorldRunner runner)
        {
            this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public ActionStepResult Step(ActionExecutionContext context, ActionPhase phase)
        {
            if (!ReferenceEquals(runner.World, context.World)) return context.Fail(ActionFailureCode.InvalidTarget);
            switch (phase)
            {
                case ActionPhase.Validating:
                    return Validate(context);
                case ActionPhase.Reserving:
                    return ActionStepResult.Success;
                case ActionPhase.Moving:
                    return Move(context);
                case ActionPhase.Aligning:
                    return ActionStepResult.Success;
                case ActionPhase.Executing:
                    return context.World.Agents.TryGetValue(context.Instance.ActorId, out var agent) &&
                           agent.PlaceId == PlaceId(context)
                        ? ActionStepResult.Success : context.Fail(ActionFailureCode.NavigationFailed);
                default:
                    return context.Fail(ActionFailureCode.ExecutionFailed);
            }
        }

        public void OnExit(ActionExecutionContext context, ActionExitReason reason)
        {
            if (reason != ActionExitReason.Completed && ReferenceEquals(runner.World, context.World) &&
                runner.GetTravelStatus(context.Instance.ActorId, PlaceId(context), context.Instance.Id) == SocialNavigationStatus.InProgress)
                runner.CancelTravel(context.Instance.ActorId);
        }

        public void OnRestored(ActionExecutionContext context, ActionPhase savedPhase)
        {
            // The runner restores travel orders and committed outcomes before restoring action hosts.
        }

        public bool CanRestore(ActionExecutionContext context)
        {
            return Validate(context) == ActionStepResult.Success;
        }

        private ActionStepResult Validate(ActionExecutionContext context)
        {
            var placeId = PlaceId(context);
            if (string.IsNullOrWhiteSpace(placeId) ||
                !context.World.Places.ContainsKey(placeId) || !runner.RegionViews.ContainsKey(placeId) ||
                !runner.NpcViews.TryGetValue(context.Instance.ActorId, out var npc) ||
                npc.GetComponent<SocialNpcNavigation>() == null)
                return context.Fail(ActionFailureCode.InvalidTarget);
            var localPoint = context.Get("localPointId");
            if (!string.IsNullOrEmpty(localPoint) &&
                !runner.PointViews.ContainsKey(placeId + "/" + localPoint))
                return context.Fail(ActionFailureCode.InvalidTarget);
            return ActionStepResult.Success;
        }

        private ActionStepResult Move(ActionExecutionContext context)
        {
            var id = context.Instance.Id;
            var result = runner.GetTravelStatus(context.Instance.ActorId, PlaceId(context), id);
            if (result == SocialNavigationStatus.Idle)
            {
                if (!runner.RequestTravel(context.Instance.ActorId, PlaceId(context),
                        context.Get("localPointId"), id))
                    return context.Fail(ActionFailureCode.NavigationFailed);
                return ActionStepResult.Running;
            }
            if (result == SocialNavigationStatus.Failed || result == SocialNavigationStatus.Cancelled)
                return context.Fail(ActionFailureCode.NavigationFailed);
            if (result != SocialNavigationStatus.Succeeded) return ActionStepResult.Running;
            // The scene runner commits the logical PlaceId at a world Tick boundary.
            return context.World.Agents.TryGetValue(context.Instance.ActorId, out var agent) &&
                   agent.PlaceId == PlaceId(context)
                ? ActionStepResult.Success : ActionStepResult.Running;
        }

        private static string PlaceId(ActionExecutionContext context)
        {
            return context.Get("placeId", context.Instance.TargetId);
        }
    }
}
