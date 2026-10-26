using System;
using System.Collections.Generic;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Domains.DomainActions;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Domains.Household
{
    /// <summary>A reusable household duty executed by an authored action graph.</summary>
    [DisallowMultipleComponent]
    public sealed class HouseholdDomainModule : MonoBehaviour, ISocialScenarioPersistence, ISocialActorRuntimeDiagnostics, ISocialGraphHost, ISocialRegionLifecycle
    {
        [Serializable]
        private sealed class Checkpoint
        {
            public int SchemaVersion = 2;
            public string CareCommitmentId;
            public string CareTaskId;
            public string CareDutyId;
            public string BreakfastId;
            public bool CareCompleted;
            public List<DomainGraphRunCheckpoint> Graphs = new List<DomainGraphRunCheckpoint>();
        }

        [SerializeField] private string homePlaceId = "household.home.river";
        [SerializeField] private string householdId = "household.river";
        [SerializeField] private string organizerId = "npc.teacher.01";
        [SerializeField] private string caregiverId = "npc.clerk.01";
        [SerializeField] private string dependentId = "npc.student.01";
        [SerializeField] private string secondDependentId = "npc.student.02";
        [SerializeField] private GraphDocument careGraph;
        private SocialWorldRunner runner;
        private SimulationWorld world;
        private Checkpoint state;
        private DomainGraphRunner graphs;
        public bool CareCompleted => state != null && state.CareCompleted;
        public int ActiveGraphCount => graphs?.ActiveCount ?? 0;
        public const string BreakfastReadyTopic = "household.breakfast.ready";
        public string FamilyNoticeChannelId => homePlaceId + ".family.notice";
        public const string BreakfastJoinTravelReason = "household.breakfast";
        private const string BreakfastServingValue = "serving";

        public void Configure(string homePlace, string household, string organizer,
            string caregiver, string dependent, string secondDependent = null)
        {
            homePlaceId = homePlace; householdId = household; organizerId = organizer;
            caregiverId = caregiver; dependentId = dependent; secondDependentId = secondDependent;
        }

        public void ConfigureGraphs(GraphDocument care) { careGraph = care; }

        public void Initialize(SocialWorldRunner runner, SimulationWorld simulation)
        {
            if (ReferenceEquals(world, simulation) && state != null) return;
            RequireBindings(simulation);
            Detach();
            this.runner = runner;
            world = simulation;
            state = new Checkpoint();
            world.Places[homePlaceId].OwnerCollectiveId = householdId;
            world.Knowledge.RegisterChannel(new KnowledgeChannel
            {
                Id = FamilyNoticeChannelId, CollectiveId = householdId,
                Audience = KnowledgeAudience.CollectiveMembers, RequiresSamePlace = false,
                RetentionSeconds = 3600, ConfidenceHalfLifeSeconds = 0, TransmissionConfidence = 0.9f
            });
            var care = world.Society.CreateCommitment("household.care",
                new[] { caregiverId, dependentId }, world.Time + 600, householdId);
            state.CareCommitmentId = care.Id;
            state.CareTaskId = world.CreateTask(caregiverId, householdId, "household.care",
                dependentId, world.Time + 600, care.Id).Id;
            state.CareDutyId = world.Schedules.Create(new ScheduleDefinition
            {
                Id = "household.care.breakfast", DomainId = "household", ActionId = "household.care",
                PlaceId = homePlaceId, StartOffsetSeconds = 60, DurationSeconds = 180,
                LateGraceSeconds = 60, Priority = 100
            }, caregiverId, world.Time, care.Id).Id;
            world.Norms.Definitions["household.share_breakfast"] = new NormDefinition
            {
                Id = "household.share_breakfast", Version = "1", Kind = "Prefer",
                ActionId = "activity.breakfast", PlaceTag = "household.home",
                CollectiveId = householdId, Formal = false
            };
            InstallGraphs(BuildGraphs(runner, world, state));
        }

        private void RequireBindings(SimulationWorld simulation)
        {
            if (simulation == null || !simulation.Places.ContainsKey(homePlaceId) ||
                !simulation.Society.Collectives.ContainsKey(householdId) ||
                !simulation.Agents.ContainsKey(organizerId) || !simulation.Agents.ContainsKey(caregiverId) ||
                !simulation.Agents.ContainsKey(dependentId) ||
                (!string.IsNullOrEmpty(secondDependentId) && !simulation.Agents.ContainsKey(secondDependentId)))
                throw new InvalidOperationException("Household module has an unbound place, collective, or actor.");
        }

        private DomainGraphRunner BuildGraphs(SocialWorldRunner runner, SimulationWorld simulation, Checkpoint saved,
            bool presentation = true, bool retainRuntimePlans = false)
        {
            var adapter = new CareAdapter(this, saved, runner);
            var result = new DomainGraphRunner(simulation,
                new Dictionary<string, ActionDefinition> { ["household.care"] = new ActionDefinition
                    { Id = "household.care", ExecutorId = "household", TimeoutSeconds = 300 } },
                new Dictionary<string, IWorldActionAdapter> { ["household"] = adapter },
                runner == null || !presentation ? null : new UnityActionAnimationPresenter(runner,
                    new Dictionary<string, ActionVisualCue> { ["household.care"] = ActionVisualCue.Converse }),
                restarting => adapter.Restarting = restarting);
            result.RegisterGraph("care", careGraph, "behavior.household.care", "household.care");
            result.BindActionRegion("household.care", homePlaceId);
            if (retainRuntimePlans) graphs?.CopyAvailablePlansTo(result);
            return result;
        }

        private void InstallGraphs(DomainGraphRunner value)
        {
            graphs = value;
            graphs.Enabled = isActiveAndEnabled;
            graphs.Failed += OnGraphFailed;
            world.AddSystem(graphs);
            if (isActiveAndEnabled) world.EventPublished += OnWorldEvent;
            RegisterDependentReevaluation();
        }

        /// <summary>
        /// Dependents re-evaluate joining breakfast whenever their personal knowledge of the
        /// family notice changes; the selection only sees what they were told, never world state.
        /// </summary>
        private void RegisterDependentReevaluation()
        {
            if (world == null || state == null) return;
            foreach (var actorId in new[] { dependentId, secondDependentId })
            {
                if (string.IsNullOrEmpty(actorId)) continue;
                world.GoalReevaluation.Register(actorId, BreakfastReadyTopic, homePlaceId,
                    () => ReevaluateBreakfastJoin(actorId));
            }
        }

        private void ReevaluateBreakfastJoin(string actorId)
        {
            if (world == null || state == null || !isActiveAndEnabled ||
                string.IsNullOrEmpty(actorId) || !world.Agents.ContainsKey(actorId)) return;
            var join = new GoalCandidate
            {
                Id = "household.breakfast.join." + homePlaceId, Kind = "household.breakfast.join",
                ActionId = "household.breakfast.join", PlaceId = homePlaceId, Need = 0.6f, Urgent = true,
                Deadline = world.Time + 1200,
                KnowledgeConditions = new List<GoalKnowledgeCondition>
                {
                    new GoalKnowledgeCondition { Topic = BreakfastReadyTopic, SubjectId = homePlaceId,
                        ExpectedValue = BreakfastServingValue, MinimumConfidence = 0.5f,
                        UnknownPolicy = UnknownKnowledgePolicy.Reject }
                }
            };
            var selected = world.Decisions.Select(actorId, new[]
            {
                join, new GoalCandidate { Id = "household.idle." + homePlaceId, Kind = "household.idle", Need = 0.1f }
            });
            if (selected?.Kind != "household.breakfast.join")
            {
                world.Emit("household.breakfast.join_deferred", actorId, actorId,
                    selected?.Id ?? "NoEligibleGoal", homePlaceId);
                return;
            }
            TryJoinBreakfast(actorId);
        }

        private void TryJoinBreakfast(string actorId)
        {
            if (!world.Activities.Sessions.TryGetValue(state.BreakfastId ?? string.Empty, out var session) ||
                session.State != "Active") return;
            ActivityParticipant participant = null;
            foreach (var person in session.Participants)
                if (person.AgentId == actorId) { participant = person; break; }
            if (participant == null || participant.Status == "Participating" || participant.Status == "Completed") return;
            var agent = world.Agents[actorId];
            if (agent.PlaceId != homePlaceId)
            {
                if (runner != null && !runner.RequestTravel(actorId, homePlaceId, null, BreakfastJoinTravelReason))
                    world.Emit("household.breakfast.travel_refused", actorId, organizerId,
                        homePlaceId, homePlaceId);
                return;
            }
            if (participant.Status != "Accepted") return;
            if (world.Activities.JoinLate(session.Id, actorId))
                world.Emit("household.breakfast.joined", actorId, session.Id, null, homePlaceId);
        }

        private bool IsRegisteredDependent(string actorId) =>
            actorId == dependentId || actorId == secondDependentId;

        private void OnWorldEvent(WorldEvent item)
        {
            if (item.Type == "agent.arrived" && item.PlaceId == homePlaceId &&
                IsRegisteredDependent(item.SubjectId) && world.Agents.TryGetValue(item.SubjectId, out var arrived) &&
                arrived.PlaceId == homePlaceId)
            { ReevaluateBreakfastJoin(item.SubjectId); return; }
            if (item.Type != "schedule.due" || item.EmitterId != "schedule.service" ||
                item.SubjectId != caregiverId || item.Payload != state.CareDutyId) return;
            if (!world.Schedules.TryAccept(state.CareDutyId, caregiverId, out var reason))
            { world.Emit("household.care.refused", caregiverId, caregiverId, reason, homePlaceId); return; }
            if (!graphs.Start("care", "care", caregiverId))
                world.Schedules.Cancel(state.CareDutyId, "GraphStartRejected");
        }

        private void OnGraphFailed(string key, ActionFailureDiagnostic failure)
        {
            world.Schedules.Cancel(state.CareDutyId, "GraphFailed");
            world.Emit("household.care.failed", caregiverId, dependentId, failure?.ToString(), homePlaceId);
        }

        public void OnRegionUnavailable(string regionId, bool unloaded)
        {
            if (world == null || state == null || regionId != homePlaceId) return;
            graphs?.CancelRegion(regionId);
            var reason = unloaded ? "region.unloaded" : "region.closed";
            if (!string.IsNullOrEmpty(state.BreakfastId)) world.Activities.Cancel(state.BreakfastId, reason);
            if (world.Schedules.Assignments.TryGetValue(state.CareDutyId ?? "", out var duty) &&
                (unloaded || duty.State == "Accepted" || duty.State == "Due"))
                world.Schedules.Cancel(duty.Id, reason);
        }

        private sealed class CareAdapter : IWorldActionAdapter, IWorldActionRestoreValidator
        {
            private readonly HouseholdDomainModule owner;
            private readonly Checkpoint saved;
            private readonly DomainTravel travel;
            public bool Restarting;
            public CareAdapter(HouseholdDomainModule module, Checkpoint checkpoint, SocialWorldRunner runner)
            { owner = module; saved = checkpoint; travel = new DomainTravel(runner); }

            public bool CanRestore(ActionExecutionContext context) =>
                context.Instance.ActorId == owner.caregiverId &&
                context.World.Schedules.Assignments.ContainsKey(saved.CareDutyId ?? "") &&
                travel.CanRestore(context, owner.homePlaceId, null);

            public ActionStepResult Step(ActionExecutionContext context, ActionPhase phase)
            {
                var simulation = context.World;
                if (!CanRestore(context)) return context.Fail(ActionFailureCode.InvalidTarget);
                if (!travel.CanAccess(context, owner.homePlaceId)) return context.Fail(ActionFailureCode.InvalidTarget);
                var duty = simulation.Schedules.Assignments[saved.CareDutyId];
                if (phase == ActionPhase.Moving) return travel.Move(context, owner.homePlaceId, null);
                if (phase != ActionPhase.Executing) return ActionStepResult.Success;
                if (saved.CareCompleted) return ActionStepResult.Success;
                if (duty.State != "Accepted" || simulation.Agents[owner.caregiverId].PlaceId != owner.homePlaceId)
                    return context.Fail(ActionFailureCode.ExecutionFailed);
                if (string.IsNullOrEmpty(saved.BreakfastId))
                {
                    context.TryCommitEffect("breakfast.create", () =>
                    {
                        var session = simulation.Activities.Create("household.breakfast", owner.organizerId,
                            owner.homePlaceId, duty.StartsAt, duty.LatestStartAt, duty.EndsAt - duty.StartsAt, 2, 4);
                        saved.BreakfastId = session.Id;
                        Invite(simulation, session.Id, owner.dependentId);
                        Invite(simulation, session.Id, owner.caregiverId);
                        if (!string.IsNullOrEmpty(owner.secondDependentId)) Invite(simulation, session.Id, owner.secondDependentId);
                        var norm = simulation.Norms.Publish("household.share_breakfast", owner.organizerId, owner.householdId);
                        foreach (var actor in new[] { owner.organizerId, owner.dependentId, owner.caregiverId, owner.secondDependentId })
                            if (!string.IsNullOrEmpty(actor)) simulation.Norms.Inform(actor, norm.Id, "family.talk");
                        // Family members away from home learn breakfast is served through this notice;
                        // their later goal re-evaluation decides whether they come and join.
                        simulation.Knowledge.Announce(owner.FamilyNoticeChannelId, owner.organizerId,
                            HouseholdDomainModule.BreakfastReadyTopic, owner.homePlaceId, BreakfastServingValue);
                    });
                }
                if (!simulation.Activities.Sessions.TryGetValue(saved.BreakfastId ?? "", out var breakfast))
                    return context.Fail(ActionFailureCode.InvalidTarget);
                if (breakfast.State == "Inviting") simulation.Activities.Start(breakfast.Id);
                if (breakfast.State == "Cancelled") return context.Fail(ActionFailureCode.ExecutionFailed);
                if (breakfast.State != "Completed") return ActionStepResult.Running;
                if (!DomainActivity.Participated(simulation, breakfast.Id, owner.caregiverId) ||
                    !DomainActivity.Participated(simulation, breakfast.Id, owner.dependentId))
                    return context.Fail(ActionFailureCode.ExecutionFailed);
                context.TryCommitEffect("care.fulfil", () =>
                {
                    if (!simulation.Schedules.Complete(saved.CareDutyId, owner.caregiverId))
                        throw new InvalidOperationException("Care duty cannot be completed.");
                    simulation.Tasks[saved.CareTaskId].Status = "Completed";
                    simulation.Society.Commitments[saved.CareCommitmentId].Status = "Fulfilled";
                    saved.CareCompleted = true;
                    simulation.Emit("household.care.completed", owner.caregiverId, owner.dependentId,
                        saved.CareTaskId, owner.homePlaceId);
                });
                return ActionStepResult.Success;
            }

            private static void Invite(SimulationWorld simulation, string session, string actor)
            { if (simulation.Activities.Invite(session, actor, "FamilyMember")) simulation.Activities.Respond(session, actor, true); }

            public void OnExit(ActionExecutionContext context, ActionExitReason reason)
            {
                travel.Exit(context, owner.homePlaceId, reason);
                if (reason == ActionExitReason.Completed || Restarting) return;
                if (!string.IsNullOrEmpty(saved.BreakfastId)) context.World.Activities.Cancel(saved.BreakfastId, "care.interrupted");
                context.World.Schedules.Cancel(saved.CareDutyId, "care.interrupted");
            }
        }

        public string CaptureScenarioState()
        {
            if (state == null) throw new InvalidOperationException("Household is not initialized.");
            state.Graphs = graphs.Capture();
            return JsonUtility.ToJson(state);
        }

        public void ValidateScenarioState(SocialWorldRunner runner, SimulationWorld simulation, string json)
        {
            var saved = Parse(json);
            RequireBindings(simulation);
            if (!simulation.Society.Commitments.TryGetValue(saved.CareCommitmentId ?? "", out var commitment) ||
                !simulation.Tasks.TryGetValue(saved.CareTaskId ?? "", out var task) ||
                task.AssigneeId != caregiverId ||
                !simulation.Schedules.Assignments.TryGetValue(saved.CareDutyId ?? "", out var duty) ||
                duty.CommitmentId != commitment.Id ||
                duty.AgentId != caregiverId || duty.ActionId != "household.care" || duty.PlaceId != homePlaceId ||
                (!string.IsNullOrEmpty(saved.BreakfastId) &&
                    (!simulation.Activities.Sessions.TryGetValue(saved.BreakfastId, out var breakfast) ||
                        breakfast.PlaceId != homePlaceId || breakfast.Kind != "household.breakfast")) ||
                saved.CareCompleted != (duty.State == "Completed") ||
                (saved.CareCompleted && (task.Status != "Completed" || commitment.Status != "Fulfilled")))
                throw new InvalidOperationException("Saved household state does not match this module's bindings.");
            foreach (var run in saved.Graphs)
                if (run == null || run.RunKey != "care" || run.GraphKey != "care" || run.Graph?.ActorId != caregiverId)
                    throw new InvalidOperationException("Saved household graph belongs to another duty or actor.");
            if (duty.State == "Accepted" && saved.Graphs.Count != 1)
                throw new InvalidOperationException("Accepted household duty has no active graph checkpoint.");
            BuildGraphs(runner, simulation, saved, false, true).Restore(saved.Graphs);
        }

        public void RestoreScenarioState(SocialWorldRunner runner, SimulationWorld simulation, string json)
        {
            var saved = Parse(json);
            this.runner = runner;
            var restored = BuildGraphs(runner, simulation, saved, true, true);
            restored.Restore(saved.Graphs);
            Detach(); world = simulation; state = saved; InstallGraphs(restored);
        }

        private static Checkpoint Parse(string json)
        {
            var saved = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Checkpoint>(json);
            if (saved == null || saved.SchemaVersion != 2 || saved.Graphs == null)
                throw new InvalidOperationException("Invalid household checkpoint; schema 2 is required.");
            return saved;
        }

        public IReadOnlyList<ActorRuntimeSnapshot> GetActorRuntimeSnapshots(string actorId) =>
            graphs?.GetActorRuntimeSnapshots(actorId) ?? (IReadOnlyList<ActorRuntimeSnapshot>)Array.Empty<ActorRuntimeSnapshot>();

        public bool TryGetGraphState(string documentId, GraphKind kind, out GraphRuntimeState graphState)
        {
            graphState = null;
            return graphs != null && graphs.TryGetGraphState(documentId, kind, out graphState);
        }
        public bool TryApplyGraph(GraphDocument draft, GraphApplyMode mode, out string error)
        {
            error = "家庭领域尚未初始化。";
            return graphs != null && graphs.TryApplyGraph(draft, mode, out error);
        }

        private void Detach()
        {
            if (world != null)
            {
                world.EventPublished -= OnWorldEvent;
                foreach (var actorId in new[] { dependentId, secondDependentId })
                    if (!string.IsNullOrEmpty(actorId))
                        world.GoalReevaluation.Unregister(actorId, BreakfastReadyTopic, homePlaceId);
            }
            if (graphs == null) return;
            graphs.Failed -= OnGraphFailed; graphs.Enabled = false; graphs.CancelAll();
            world?.RemoveSystem(graphs);
        }
        private void OnEnable()
        {
            if (world == null || graphs == null) return;
            world.AddSystem(graphs);
            graphs.Enabled = true; graphs.Failed -= OnGraphFailed; graphs.Failed += OnGraphFailed;
            world.EventPublished -= OnWorldEvent; world.EventPublished += OnWorldEvent;
        }
        private void OnDisable() { Detach(); }
    }
}

