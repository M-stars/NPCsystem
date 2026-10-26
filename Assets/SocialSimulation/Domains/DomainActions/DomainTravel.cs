using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.UnityRuntime;

namespace SocialSimulation.Domains.DomainActions
{
    /// <summary>Navigation used by domain actions with instance-specific place bindings.</summary>
    public sealed class DomainTravel
    {
        private readonly SocialWorldRunner runner;

        public DomainTravel(SocialWorldRunner sceneRunner) { runner = sceneRunner; }

        public bool CanRestore(ActionExecutionContext context, string placeId, string pointId)
        {
            if (!context.World.Places.ContainsKey(placeId ?? "") ||
                !context.World.Agents.ContainsKey(context.Instance.ActorId)) return false;
            return runner == null || (runner.RegionViews.ContainsKey(placeId) &&
                runner.NpcViews.ContainsKey(context.Instance.ActorId) &&
                (string.IsNullOrEmpty(pointId) || runner.PointViews.ContainsKey(placeId + "/" + pointId)));
        }

        public ActionStepResult Move(ActionExecutionContext context, string placeId, string pointId)
        {
            if (!CanRestore(context, placeId, pointId)) return context.Fail(ActionFailureCode.InvalidTarget);
            if (!CanAccess(context, placeId)) return context.Fail(ActionFailureCode.InvalidTarget);
            // In a headless world another movement system owns arrival; this adapter never teleports.
            if (runner == null) return context.World.Agents[context.Instance.ActorId].PlaceId == placeId
                ? ActionStepResult.Success : ActionStepResult.Running;
            if (runner.World != context.World) return context.Fail(ActionFailureCode.InvalidTarget);
            // The runner owns orders, pending admissions and committed results across Save/Load.
            // Checking its action identity prevents restoring a graph from restarting an existing trip's deadline.
            var status = runner.GetTravelStatus(context.Instance.ActorId, placeId, context.Instance.Id);
            if (status == SocialNavigationStatus.Idle)
            {
                // An activity bound to the whole region does not require walking to its geometric centre.
                if (string.IsNullOrEmpty(pointId) && context.World.Agents[context.Instance.ActorId].PlaceId == placeId)
                    return ActionStepResult.Success;
                if (!runner.RequestTravel(context.Instance.ActorId, placeId, pointId, context.Instance.Id))
                    return context.Fail(ActionFailureCode.NavigationFailed);
                return ActionStepResult.Running;
            }
            if (status == SocialNavigationStatus.InProgress) return ActionStepResult.Running;
            if (status == SocialNavigationStatus.Succeeded &&
                context.World.Agents[context.Instance.ActorId].PlaceId == placeId) return ActionStepResult.Success;
            return context.Fail(ActionFailureCode.NavigationFailed);
        }

        /// <summary>Check dynamic access on the action's own world, including candidate worlds during load.</summary>
        public bool CanAccess(ActionExecutionContext context, string placeId)
        {
            if (!context.World.Places.TryGetValue(placeId ?? "", out var place) || !place.Open) return false;
            if (runner == null) return true;
            return runner.Content != null && runner.Content.Regions.TryGetValue(place.DefinitionId ?? "", out var definition) &&
                RegionAccess.Evaluate(context.World, place, definition, context.Instance.ActorId, context.World.Time).Allowed;
        }

        public void Exit(ActionExecutionContext context, string placeId, ActionExitReason reason)
        {
            if (reason != ActionExitReason.Completed && runner != null && runner.World == context.World &&
                runner.GetTravelStatus(context.Instance.ActorId, placeId, context.Instance.Id) == SocialNavigationStatus.InProgress)
                runner.CancelTravel(context.Instance.ActorId);
        }
    }
}
