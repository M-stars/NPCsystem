using System;
using System.Collections.Generic;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;

namespace SocialSimulation.Domains.Combined
{
    [Serializable]
    public sealed class CourseAssignmentCheckpoint
    {
        public string AgentId;
        public string SessionId;
        public string PlaceId;
        public string LocalSeatId;
        public string TokenId;
        public bool TravelRequested;
        public string TravelReason;
        public bool Arrived;
        public bool TravelFailed;
        public ActionGraphCheckpoint Graph;
    }

    /// <summary>Runs one shared authored graph through per-student action scopes.</summary>
    public sealed class CourseBehaviorSystem : IWorldSystem, IWorldActionAdapter,
        IRuntimeGraphBindings, ISocialActorRuntimeDiagnostics
    {
        private sealed class Assignment
        {
            public string AgentId;
            public string SessionId;
            public string PlaceId;
            public string LocalSeatId;
            public string SeatId;
            public ResourceToken Token;
            public bool TravelRequested;
            public string TravelReason;
            public bool Arrived;
            public bool TravelFailed;
            public ActionGraphHost Host;
        }

        private readonly SimulationWorld world;
        private readonly SocialWorldRunner runner;
        private CompiledBehaviorPlan currentPlan;
        private readonly Dictionary<string, CompiledBehaviorPlan> plans =
            new Dictionary<string, CompiledBehaviorPlan>(StringComparer.Ordinal);
        private readonly Dictionary<string, Assignment> assignments = new Dictionary<string, Assignment>(StringComparer.Ordinal);
        private readonly Dictionary<string, ActionDefinition> definitions = new Dictionary<string, ActionDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, IWorldActionAdapter> adapters = new Dictionary<string, IWorldActionAdapter>(StringComparer.Ordinal);
        private readonly IWorldActionPresentation presentation;

        public int Order => 20;
        public int ActiveCount => assignments.Count;
        public CompiledBehaviorPlan CurrentPlan => currentPlan;
        public IReadOnlyCollection<string> AssignedActorIds => assignments.Keys;
        public List<string> ActiveGraphVersions
        {
            get
            {
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var assignment in assignments.Values)
                {
                    var behavior = assignment.Host.Behavior;
                    unique.Add(PlanKey(behavior.Plan.DocumentId, behavior.Plan.Version, behavior.Plan.Revision));
                }
                var result = new List<string>(unique);
                result.Sort(StringComparer.Ordinal);
                return result;
            }
        }

        public CourseBehaviorSystem(SimulationWorld simulation, SocialWorldRunner sceneRunner, CompiledBehaviorPlan compiled,
            bool enablePresentation = true)
        {
            world = simulation ?? throw new ArgumentNullException(nameof(simulation));
            runner = sceneRunner ?? throw new ArgumentNullException(nameof(sceneRunner));
            currentPlan = compiled ?? throw new ArgumentNullException(nameof(compiled));
            plans.Add(PlanKey(compiled.DocumentId, compiled.Version, compiled.Revision), compiled);
            Define("course.reserve_seat", 60, "posture.sit");
            Define("course.move_to_seat", 900, "locomotion.walk");
            Define("course.sit", 60, "posture.sit");
            Define("course.attend", 3600, null);
            Define("course.release_seat", 60, null);
            adapters.Add("course", this);
            presentation = !enablePresentation ? null : new UnityActionAnimationPresenter(runner,
                new Dictionary<string, ActionVisualCue>(StringComparer.Ordinal)
                {
                    ["course.move_to_seat"] = ActionVisualCue.Travel,
                    ["course.sit"] = ActionVisualCue.Sit,
                    ["course.attend"] = ActionVisualCue.Sit
                });
        }

        private void Define(string id, double timeout, string capability)
        {
            definitions.Add(id, new ActionDefinition
            { Id = id, ExecutorId = "course", CapabilityId = capability, TimeoutSeconds = timeout });
        }

        public void Assign(string agentId, string sessionId, string placeId, string localSeatId)
        {
            if (assignments.ContainsKey(agentId)) throw new InvalidOperationException("Duplicate course assignment: " + agentId);
            var behavior = new BehaviorInstance(currentPlan, agentId);
            assignments.Add(agentId, new Assignment
            {
                AgentId = agentId,
                SessionId = sessionId,
                PlaceId = placeId,
                LocalSeatId = localSeatId,
                SeatId = placeId + "/" + localSeatId,
                Host = new ActionGraphHost(world, behavior, definitions, adapters, null, presentation)
            });
        }

        public void CopyAvailablePlansTo(CourseBehaviorSystem target)
        {
            foreach (var pair in plans) target.plans[pair.Key] = pair.Value;
            target.currentPlan = currentPlan;
        }

        /// <summary>Cancel only assignments whose destination has become unavailable.</summary>
        public void CancelRegion(string regionId)
        {
            var ids = new List<string>();
            foreach (var pair in assignments)
                if (pair.Value.PlaceId == regionId) ids.Add(pair.Key);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var assignment = assignments[id];
                assignment.Host.Cancel(world.TickIndex);
                world.Activities.Leave(assignment.SessionId, id);
                assignments.Remove(id);
                world.Emit("course.behavior_cancelled", id, id, "region.unavailable", regionId);
            }
        }

        public void ApplyPlan(CompiledBehaviorPlan compiled, DemoGraphApplyMode mode)
        {
            if (compiled == null) throw new ArgumentNullException(nameof(compiled));
            if (mode == DemoGraphApplyMode.RestartAffected && !world.Paused)
                throw new InvalidOperationException("RestartAffected requires a paused simulation.");
            plans[PlanKey(compiled.DocumentId, compiled.Version, compiled.Revision)] = compiled;
            currentPlan = compiled;
            if (mode != DemoGraphApplyMode.RestartAffected) return;
            var ids = new List<string>(assignments.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var assignment = assignments[id];
                assignment.Host.Cancel(world.TickIndex);
                runner.CancelTravel(id);
                if (assignment.Token != null && assignment.Token.Active &&
                    assignment.Token.OwnerScopeId == assignment.SessionId)
                    world.Resources.Release(assignment.Token.Id, assignment.SessionId);
                if (world.Activities.Sessions.TryGetValue(assignment.SessionId, out var session))
                {
                    foreach (var participant in session.Participants)
                        if (participant.AgentId == id) participant.ResourceTokenId = null;
                    if (session.State == "Completed" || session.State == "Cancelled")
                    { assignments.Remove(id); continue; }
                }
                assignment.Token = null;
                assignment.TravelRequested = assignment.Arrived = assignment.TravelFailed = false;
                assignment.TravelReason = null;
                assignment.Host = NewHost(new BehaviorInstance(currentPlan, id));
            }
        }

        public List<CourseAssignmentCheckpoint> CaptureAssignments()
        {
            var result = new List<CourseAssignmentCheckpoint>();
            foreach (var assignment in assignments.Values)
                result.Add(new CourseAssignmentCheckpoint
                {
                    AgentId = assignment.AgentId, SessionId = assignment.SessionId,
                    PlaceId = assignment.PlaceId, LocalSeatId = assignment.LocalSeatId,
                    TokenId = assignment.Token?.Id, TravelRequested = assignment.TravelRequested,
                    TravelReason = assignment.TravelReason, Arrived = assignment.Arrived,
                    TravelFailed = assignment.TravelFailed, Graph = assignment.Host.CaptureCheckpoint()
                });
            result.Sort((a, b) => StringComparer.Ordinal.Compare(a.AgentId, b.AgentId));
            return result;
        }

        public void RestoreAssignments(IEnumerable<CourseAssignmentCheckpoint> saved)
        {
            if (saved == null) throw new ArgumentNullException(nameof(saved));
            foreach (var item in saved)
            {
                if (item == null || item.Graph == null || item.Graph.Behavior == null ||
                    !world.Agents.ContainsKey(item.AgentId) || assignments.ContainsKey(item.AgentId) ||
                    !world.Activities.Sessions.ContainsKey(item.SessionId) ||
                    !runner.RegionViews.ContainsKey(item.PlaceId) ||
                    !runner.PointViews.ContainsKey(item.PlaceId + "/" + item.LocalSeatId) ||
                    !plans.TryGetValue(PlanKey(item.Graph.Behavior.GraphId, item.Graph.Behavior.Version,
                        item.Graph.Behavior.Revision),
                        out var compiled))
                    throw new InvalidOperationException("Saved course assignment or graph plan is unavailable.");
                var behavior = new BehaviorInstance(compiled, item.AgentId, item.Graph.GraphInstanceId);
                var assignment = new Assignment
                {
                    AgentId = item.AgentId, SessionId = item.SessionId, PlaceId = item.PlaceId,
                    LocalSeatId = item.LocalSeatId, SeatId = item.PlaceId + "/" + item.LocalSeatId,
                    TravelRequested = item.TravelRequested, TravelReason = item.TravelReason,
                    Arrived = item.Arrived, TravelFailed = item.TravelFailed,
                    Host = NewHost(behavior)
                };
                if (!string.IsNullOrEmpty(item.TokenId))
                    world.Resources.Tokens.TryGetValue(item.TokenId, out assignment.Token);
                assignments.Add(item.AgentId, assignment);
                if (!assignment.Host.TryRestoreCheckpoint(item.Graph, world.TickIndex))
                {
                    assignments.Remove(item.AgentId);
                    throw new InvalidOperationException("Course graph checkpoint rejected: " +
                        assignment.Host.LastRestoreFailure);
                }
            }
        }

        private ActionGraphHost NewHost(BehaviorInstance behavior)
        { return new ActionGraphHost(world, behavior, definitions, adapters, null, presentation); }

        private static string PlanKey(string id, int version, string revision)
        { return id + "#" + version + "#" + revision; }

        public ActionDefinition ResolveAction(string actionId)
        { return definitions.TryGetValue(actionId ?? string.Empty, out var definition) ? definition : null; }

        public bool HasExecutor(string executorId)
        { return adapters.ContainsKey(executorId ?? string.Empty); }

        public bool HasCapability(string actorId, string capabilityId)
        { return world.Agents.TryGetValue(actorId ?? string.Empty, out var actor) &&
            actor.CapabilityIds.Contains(capabilityId); }

        public bool CanEvaluateCondition(string conditionId) { return false; }
        public bool CanExecuteRuleCommand(string commandId) { return commandId == "violation.warn"; }

        public IReadOnlyList<ActorRuntimeSnapshot> GetActorRuntimeSnapshots(string actorId)
        {
            return assignments.TryGetValue(actorId ?? string.Empty, out var assignment)
                ? assignment.Host.CaptureRuntimeSnapshots()
                : (IReadOnlyList<ActorRuntimeSnapshot>)Array.Empty<ActorRuntimeSnapshot>();
        }

        public void NotifyTravelResult(string agentId, string reason, bool arrived)
        {
            if (!assignments.TryGetValue(agentId, out var assignment) || assignment.TravelReason != reason) return;
            assignment.Arrived = arrived;
            assignment.TravelFailed = !arrived;
        }

        public void Tick(SimulationWorld simulation, double deltaSeconds)
        {
            var ids = new List<string>(assignments.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!assignments.TryGetValue(id, out var assignment)) continue;
                var result = assignment.Host.Tick(world.TickIndex);
                if (result == GraphStatus.Running) continue;
                if (result == GraphStatus.Failure)
                {
                    world.Activities.Leave(assignment.SessionId, id);
                    world.Emit("course.behavior_failed", id, id,
                        assignment.Host.LastFinishedAction?.FailureCode.ToString() ?? "unknown", assignment.PlaceId);
                }
                assignments.Remove(id);
            }
        }

        public ActionStepResult Step(ActionExecutionContext context, ActionPhase phase)
        {
            if (!assignments.TryGetValue(context.Instance.ActorId, out var assignment))
                return context.Fail(ActionFailureCode.UnknownActor);
            switch (context.Instance.ActionId)
            {
                case "course.reserve_seat": return Reserve(context, assignment, phase);
                case "course.move_to_seat": return Move(context, assignment, phase);
                case "course.sit": return Sit(context, assignment, phase);
                case "course.attend": return Attend(context, assignment, phase);
                case "course.release_seat": return Release(context, assignment, phase);
                default: return context.Fail(ActionFailureCode.UnknownAction);
            }
        }

        private ActionStepResult Reserve(ActionExecutionContext context, Assignment assignment, ActionPhase phase)
        {
            if (phase == ActionPhase.Validating)
                return world.Resources.Slots.ContainsKey(assignment.SeatId)
                    ? ActionStepResult.Success : context.Fail(ActionFailureCode.InvalidTarget);
            if (phase == ActionPhase.Reserving)
            {
                if (assignment.Token == null)
                    assignment.Token = context.TryReserveForScope(assignment.SeatId, 3600);
                return assignment.Token != null ? ActionStepResult.Success : context.Fail(ActionFailureCode.ResourceUnavailable);
            }
            return ActionStepResult.Success;
        }

        private ActionStepResult Move(ActionExecutionContext context, Assignment assignment, ActionPhase phase)
        {
            if (phase == ActionPhase.Validating)
                return runner.RegionViews.ContainsKey(assignment.PlaceId) &&
                       runner.PointViews.ContainsKey(assignment.SeatId)
                    ? ActionStepResult.Success : context.Fail(ActionFailureCode.InvalidTarget);
            if (phase != ActionPhase.Moving && phase != ActionPhase.Executing) return ActionStepResult.Success;
            if (assignment.TravelFailed) return context.Fail(ActionFailureCode.NavigationFailed);
            if (!assignment.TravelRequested)
            {
                assignment.TravelReason = context.Instance.Id;
                if (!runner.RequestTravel(assignment.AgentId, assignment.PlaceId,
                        assignment.LocalSeatId, assignment.TravelReason))
                    return context.Fail(ActionFailureCode.NavigationFailed);
                assignment.TravelRequested = true;
            }
            return assignment.Arrived ? ActionStepResult.Success : ActionStepResult.Running;
        }

        private ActionStepResult Sit(ActionExecutionContext context, Assignment assignment, ActionPhase phase)
        {
            if (phase == ActionPhase.Validating)
                return assignment.Arrived && assignment.Token != null && assignment.Token.Active
                    ? ActionStepResult.Success : context.Fail(ActionFailureCode.InvalidTarget);
            if (phase != ActionPhase.Executing) return ActionStepResult.Success;
            if (!world.Activities.AttachResource(assignment.SessionId, assignment.AgentId,
                    assignment.Token.Id, context.Scope.Id)) return context.Fail(ActionFailureCode.ResourceUnavailable);
            if (!world.Resources.Occupy(assignment.Token.Id, assignment.AgentId))
                return context.Fail(ActionFailureCode.ResourceUnavailable);
            world.Activities.JoinLate(assignment.SessionId, assignment.AgentId);
            return ActionStepResult.Success;
        }

        private ActionStepResult Attend(ActionExecutionContext context, Assignment assignment, ActionPhase phase)
        {
            if (!world.Activities.Sessions.TryGetValue(assignment.SessionId, out var session))
                return context.Fail(ActionFailureCode.InvalidTarget);
            if (session.State == "Cancelled") return context.Fail(ActionFailureCode.ExecutionFailed);
            if (phase != ActionPhase.Executing) return ActionStepResult.Success;
            if (session.State == "Completed")
            {
                foreach (var person in session.Participants)
                    if (person.AgentId == assignment.AgentId)
                        return person.Status == "Completed" ? ActionStepResult.Success :
                            context.Fail(ActionFailureCode.ExecutionFailed);
                return context.Fail(ActionFailureCode.ExecutionFailed);
            }
            if (session.State == "Active") world.Activities.JoinLate(session.Id, assignment.AgentId);
            return ActionStepResult.Running;
        }

        private ActionStepResult Release(ActionExecutionContext context, Assignment assignment, ActionPhase phase)
        {
            if (phase == ActionPhase.Executing && assignment.Token != null && assignment.Token.Active)
                world.Resources.Release(assignment.Token.Id, assignment.Token.OwnerScopeId);
            return ActionStepResult.Success;
        }

        public void OnExit(ActionExecutionContext context, ActionExitReason reason)
        {
            if (reason == ActionExitReason.Completed || context.Instance.ActionId != "course.move_to_seat") return;
            if (assignments.TryGetValue(context.Instance.ActorId, out var assignment) &&
                assignment.TravelReason == context.Instance.Id)
                runner.CancelTravel(context.Instance.ActorId);
        }
    }
}
