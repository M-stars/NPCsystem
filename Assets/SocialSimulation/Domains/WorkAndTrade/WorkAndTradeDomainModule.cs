using System;
using System.Collections.Generic;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Domains.DomainActions;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Domains.WorkAndTrade
{
    /// <summary>Work, wages, purchases and community participation execute on the shared action host.</summary>
    [DisallowMultipleComponent]
    public sealed class WorkAndTradeDomainModule : MonoBehaviour, ISocialScenarioPersistence, ISocialActorRuntimeDiagnostics, ISocialGraphHost, ISocialRegionLifecycle
    {
        [Serializable]
        private sealed class Checkpoint
        {
            public int SchemaVersion = 3;
            public string ShiftId;
            public string ClubId;
            public string ShiftDutyId;
            public string PurchaseDutyId;
            public string ClubDutyId;
            public bool WagePaid;
            public bool ClerkPurchaseCompleted;
            public bool StudentPurchaseCompleted;
            public bool ClubCompleted;
            public bool ShopServiceSuspended;
            public List<DomainGraphRunCheckpoint> Graphs = new List<DomainGraphRunCheckpoint>();
        }

        [SerializeField] private string shopPlaceId = "trade.shop.corner";
        [SerializeField] private string shopCollectiveId = "trade.corner_shop";
        [SerializeField] private string clubPlaceId = "community.square";
        [SerializeField] private string clubCollectiveId = "community.walking_club";
        [SerializeField] private string clerkId = "npc.clerk.01";
        [SerializeField] private string coworkerId = "npc.clerk.02";
        [SerializeField] private string clubMemberId = "npc.worker.01";
        [SerializeField] private string studentCustomerId = "npc.student.01";
        [SerializeField] private GraphDocument shiftGraph;
        [SerializeField] private GraphDocument purchaseGraph;
        [SerializeField] private GraphDocument clubGraph;
        [SerializeField] private string shiftPointId = "counter";
        [SerializeField] private string purchasePointId = "queue";
        [SerializeField] private string clubPointId = "chat";
        [SerializeField] private string clubMemberPointId = "run.1";
        [SerializeField, Min(1)] private float purchaseActionTimeoutSeconds = 1200;
        private SocialWorldRunner runner;
        private SimulationWorld world;
        private Checkpoint state;
        private DomainGraphRunner graphs;

        public bool WagePaid => state != null && state.WagePaid;
        public bool ClerkPurchaseCompleted => state != null && state.ClerkPurchaseCompleted;
        public bool StudentPurchaseCompleted => state != null && state.StudentPurchaseCompleted;
        public bool ClubCompleted => state != null && state.ClubCompleted;
        public int ActiveGraphCount => graphs?.ActiveCount ?? 0;
        public string ShiftPointId => shiftPointId;
        public string PurchasePointId => purchasePointId;
        public string ClubPointId => clubPointId;
        public string ClubMemberPointId => clubMemberPointId;
        public float PurchaseActionTimeoutSeconds => purchaseActionTimeoutSeconds;
        public bool ShopServiceAvailable => state != null && !state.ShopServiceSuspended;
        public string AvailabilityNoticeChannelId => shopPlaceId + ".availability.notice";
        public string AvailabilityConversationChannelId => shopPlaceId + ".availability.conversation";
        public const string AvailabilityTopic = "trade.service.availability";

        public void Configure(string shopPlace, string shopCollective, string clubPlace,
            string clubCollective, string clerk, string coworker, string clubMember,
            string studentCustomer = null)
        {
            shopPlaceId = shopPlace; shopCollectiveId = shopCollective; clubPlaceId = clubPlace;
            clubCollectiveId = clubCollective; clerkId = clerk; coworkerId = coworker;
            clubMemberId = clubMember; studentCustomerId = studentCustomer;
        }

        public void ConfigureGraphs(GraphDocument shift, GraphDocument purchase, GraphDocument club)
        { shiftGraph = shift; purchaseGraph = purchase; clubGraph = club; }

        /// <summary>Point IDs are local to the configured region. Null or empty selects its origin.</summary>
        public void ConfigureNavigation(string shiftPoint, string purchasePoint, string clubPoint, string memberPoint)
        {
            shiftPointId = shiftPoint; purchasePointId = purchasePoint;
            clubPointId = clubPoint; clubMemberPointId = memberPoint;
        }

        /// <summary>Total simulated seconds for validation, navigation and purchase; configure before Initialize.</summary>
        public void ConfigurePurchaseTimeout(float simulatedSeconds)
        {
            if (simulatedSeconds <= 0 || float.IsNaN(simulatedSeconds) || float.IsInfinity(simulatedSeconds))
                throw new ArgumentOutOfRangeException(nameof(simulatedSeconds));
            purchaseActionTimeoutSeconds = simulatedSeconds;
        }

        public void Initialize(SocialWorldRunner sceneRunner, SimulationWorld simulation)
        {
            if (ReferenceEquals(world, simulation) && state != null) return;
            RequireBindings(simulation);
            Detach();
            runner = sceneRunner; world = simulation; state = new Checkpoint();
            world.Places[shopPlaceId].OwnerCollectiveId = shopCollectiveId;
            world.Economy.Seed(shopCollectiveId, "food.snack", 24);
            world.Economy.Seed(shopCollectiveId, "money.credit", 20);
            if (!string.IsNullOrEmpty(studentCustomerId)) world.Economy.Seed(studentCustomerId, "money.credit", 10);
            world.Society.CreateCommitment("trade.employment", new[] { clerkId, shopCollectiveId },
                world.Time + 7200, shopCollectiveId);
            world.Society.AssignRole(clerkId, clubCollectiveId, "community.member");
            world.Society.AssignRole(clubMemberId, clubCollectiveId, "community.member");
            world.Society.Grant(shopCollectiveId, clerkId, "policy.publish", shopCollectiveId);
            world.Society.Grant(shopCollectiveId, clerkId, "trade.availability.publish", shopCollectiveId);
            RegisterAvailabilityChannels();
            world.Norms.Definitions["trade.no_running"] = new NormDefinition
            {
                Id = "trade.no_running", Version = "1", Kind = "Prohibit", ActionId = "locomotion.run",
                PlaceTag = "trade.shop", CollectiveId = shopCollectiveId, Formal = true
            };
            state.ShiftDutyId = CreateDuty("trade.shift", "trade.shift", shopPlaceId, 1500, 2400, 1200, 600, 80);
            state.PurchaseDutyId = CreateDuty("trade.clerk.purchase", "trade.purchase", shopPlaceId, 3960, 300, 0, 120, 50);
            state.ClubDutyId = CreateDuty("community.club.meetup", "community.walking_club.meetup", clubPlaceId,
                5100, 600, 600, 300, 40);
            if (!string.IsNullOrEmpty(studentCustomerId))
                world.Schedule(world.Time + 3600, "trade.customer.purchase.due", "domain.work_and_trade",
                    studentCustomerId, shopPlaceId, state.PurchaseDutyId);
            InstallGraphs(BuildGraphs(runner, world, state));
        }

        private void RegisterAvailabilityChannels()
        {
            world.Knowledge.RegisterChannel(new KnowledgeChannel
            {
                Id = AvailabilityNoticeChannelId, CollectiveId = shopCollectiveId,
                Audience = KnowledgeAudience.Public, PublisherAuthorityAction = "trade.availability.publish",
                RequiresSamePlace = true, RetentionSeconds = 1800, ConfidenceHalfLifeSeconds = 0,
                TransmissionConfidence = 1
            });
            world.Knowledge.RegisterChannel(new KnowledgeChannel
            {
                Id = AvailabilityConversationChannelId, Audience = KnowledgeAudience.Public,
                RequiresSamePlace = true, RetentionSeconds = 1800, ConfidenceHalfLifeSeconds = 0,
                TransmissionConfidence = 0.9f
            });
        }

        /// <summary>Authority-side service switch. Publication reaches only people actually at the shop.</summary>
        public KnowledgeAnnouncement SetShopServiceAvailable(bool available, bool announce = true)
        {
            if (world == null || state == null) throw new InvalidOperationException("Work and trade is not initialized.");
            state.ShopServiceSuspended = !available;
            return announce ? PublishShopAvailability(clerkId) : null;
        }

        public KnowledgeAnnouncement PublishShopAvailability(string publisherId)
        {
            if (world == null || state == null || !world.Agents.TryGetValue(publisherId ?? "", out var publisher) ||
                publisher.PlaceId != shopPlaceId) return null;
            return world.Knowledge.Announce(AvailabilityNoticeChannelId, publisherId, AvailabilityTopic,
                shopPlaceId, state.ShopServiceSuspended ? "unavailable" : "available");
        }

        /// <summary>Called after an actual conversation. KnowledgeService checks both people's location.</summary>
        public KnowledgeDelivery RelayShopAvailability(string senderId, string recipientId, string originalEventId) =>
            world == null ? KnowledgeDelivery.Denied : world.Knowledge.Relay(senderId, recipientId,
                originalEventId, AvailabilityConversationChannelId);

        /// <summary>Goal selection reads personal information only; execution later checks the real service.</summary>
        public GoalCandidate SelectPurchaseGoal(string actorId)
        {
            if (world == null || state == null) return null;
            return world.Decisions.Select(actorId, new[]
            {
                new GoalCandidate
                {
                    Id = "trade.purchase." + shopPlaceId, Kind = "trade.purchase", ActionId = "trade.purchase",
                    PlaceId = shopPlaceId, TargetId = shopCollectiveId, Need = 1, Urgent = true,
                    Deadline = world.Time + purchaseActionTimeoutSeconds,
                    KnowledgeConditions = new List<GoalKnowledgeCondition>
                    {
                        new GoalKnowledgeCondition { Topic = AvailabilityTopic, SubjectId = shopPlaceId,
                            ExpectedValue = "available", MinimumConfidence = 0.5f,
                            UnknownPolicy = UnknownKnowledgePolicy.AllowAttempt }
                    }
                },
                new GoalCandidate { Id = "trade.purchase.defer." + shopPlaceId, Kind = "trade.purchase.defer",
                    PlaceId = shopPlaceId, Need = 0.1f }
            });
        }

        public bool TryStartCustomerPurchase()
        {
            if (world == null || graphs == null || state == null || !isActiveAndEnabled ||
                string.IsNullOrEmpty(studentCustomerId) || state.StudentPurchaseCompleted) return false;
            if (!PurchaseSelected(studentCustomerId)) return false;
            return graphs.Start("purchase.customer", "purchase", studentCustomerId);
        }

        private bool PurchaseSelected(string actorId)
        {
            var selected = SelectPurchaseGoal(actorId);
            if (selected?.Kind == "trade.purchase") return true;
            world.Emit("trade.purchase.deferred", actorId, actorId, selected?.Id ?? "NoEligibleGoal", shopPlaceId);
            return false;
        }

        private string CreateDuty(string id, string action, string place, double start, double duration,
            double lead, double grace, int priority) => world.Schedules.Create(new ScheduleDefinition
        {
            Id = id, DomainId = "work_and_trade", ActionId = action, PlaceId = place,
            StartOffsetSeconds = start, DurationSeconds = duration, TravelLeadSeconds = lead,
            LateGraceSeconds = grace, Priority = priority
        }, clerkId, world.Time).Id;

        private void RequireBindings(SimulationWorld simulation)
        {
            if (simulation == null || !simulation.Places.ContainsKey(shopPlaceId) || !simulation.Places.ContainsKey(clubPlaceId) ||
                !simulation.Society.Collectives.ContainsKey(shopCollectiveId) || !simulation.Society.Collectives.ContainsKey(clubCollectiveId) ||
                !simulation.Agents.ContainsKey(clerkId) || !simulation.Agents.ContainsKey(coworkerId) ||
                !simulation.Agents.ContainsKey(clubMemberId) ||
                (!string.IsNullOrEmpty(studentCustomerId) && !simulation.Agents.ContainsKey(studentCustomerId)))
                throw new InvalidOperationException("Work and trade module has an unbound place, collective, or actor.");
        }

        private DomainGraphRunner BuildGraphs(SocialWorldRunner sceneRunner, SimulationWorld simulation, Checkpoint saved,
            bool presentation = true, bool retainRuntimePlans = false)
        {
            var definitions = new Dictionary<string, ActionDefinition>();
            Define(definitions, "trade.shift", 4000);
            Define(definitions, "trade.pay_wage", 60);
            if (purchaseActionTimeoutSeconds <= 0 || float.IsNaN(purchaseActionTimeoutSeconds) || float.IsInfinity(purchaseActionTimeoutSeconds))
                throw new InvalidOperationException("Purchase action timeout must be finite and positive.");
            Define(definitions, "trade.purchase", purchaseActionTimeoutSeconds);
            Define(definitions, "community.walking_club.meetup", 1800);
            var adapter = new TradeAdapter(this, saved, sceneRunner);
            var result = new DomainGraphRunner(simulation, definitions,
                new Dictionary<string, IWorldActionAdapter> { ["work_and_trade"] = adapter },
                sceneRunner == null || !presentation ? null : new UnityActionAnimationPresenter(sceneRunner,
                    new Dictionary<string, ActionVisualCue>
                    {
                        ["trade.shift"] = ActionVisualCue.Work, ["trade.pay_wage"] = ActionVisualCue.Trade,
                        ["trade.purchase"] = ActionVisualCue.Trade,
                        ["community.walking_club.meetup"] = ActionVisualCue.Group
                    }), restarting => adapter.Restarting = restarting);
            result.RegisterGraph("shift", shiftGraph, "behavior.trade.shift", "trade.shift", "trade.pay_wage");
            result.RegisterGraph("purchase", purchaseGraph, "behavior.trade.purchase", "trade.purchase");
            result.RegisterGraph("club", clubGraph, "behavior.community.meetup", "community.walking_club.meetup");
            result.BindActionRegion("trade.shift", shopPlaceId);
            result.BindActionRegion("trade.pay_wage", shopPlaceId);
            result.BindActionRegion("trade.purchase", shopPlaceId);
            result.BindActionRegion("community.walking_club.meetup", clubPlaceId);
            if (retainRuntimePlans) graphs?.CopyAvailablePlansTo(result);
            return result;
        }

        private static void Define(Dictionary<string, ActionDefinition> definitions, string id, double timeout)
        { definitions.Add(id, new ActionDefinition { Id = id, ExecutorId = "work_and_trade", TimeoutSeconds = timeout }); }

        private void InstallGraphs(DomainGraphRunner value)
        {
            graphs = value; graphs.Enabled = isActiveAndEnabled;
            graphs.Failed += OnGraphFailed; world.AddSystem(graphs);
            if (isActiveAndEnabled) world.EventPublished += OnWorldEvent;
            RegisterCustomerReevaluation();
        }

        /// <summary>
        /// A deferred purchase waits for information, not for the schedule: any change to the
        /// customer's personal knowledge of shop availability re-runs their goal selection.
        /// </summary>
        private void RegisterCustomerReevaluation()
        {
            if (world == null || state == null || string.IsNullOrEmpty(studentCustomerId)) return;
            world.GoalReevaluation.Register(studentCustomerId, AvailabilityTopic, shopPlaceId, ReevaluateCustomerPurchase);
        }

        private void ReevaluateCustomerPurchase()
        {
            if (world == null || state == null || !isActiveAndEnabled || string.IsNullOrEmpty(studentCustomerId)) return;
            TryStartCustomerPurchase();
        }

        private void OnWorldEvent(WorldEvent item)
        {
            if (item.Type == "trade.customer.purchase.due" && item.EmitterId == "domain.work_and_trade" &&
                item.SubjectId == studentCustomerId && item.PlaceId == shopPlaceId &&
                item.Payload == state.PurchaseDutyId && !state.StudentPurchaseCompleted)
            { TryStartCustomerPurchase(); return; }
            if (item.Type != "schedule.due" || item.EmitterId != "schedule.service" || item.SubjectId != clerkId ||
                (item.Payload != state.ShiftDutyId && item.Payload != state.PurchaseDutyId && item.Payload != state.ClubDutyId)) return;
            if (item.Payload == state.PurchaseDutyId && !state.WagePaid)
            { world.Schedules.Decline(item.Payload, clerkId, "WageNotPaid"); return; }
            if (item.Payload == state.PurchaseDutyId && !PurchaseSelected(clerkId))
            { world.Schedules.Decline(item.Payload, clerkId, "PersonalKnowledgeUnavailable"); return; }
            if (!world.Schedules.TryAccept(item.Payload, clerkId, out var reason))
            { world.Emit("trade.duty.refused", clerkId, clerkId, reason, item.PlaceId); return; }
            var graph = item.Payload == state.ShiftDutyId ? "shift" : item.Payload == state.PurchaseDutyId ? "purchase" : "club";
            var run = graph == "purchase" ? "purchase.clerk" : graph;
            if (!graphs.Start(run, graph, clerkId))
            { world.Schedules.Cancel(item.Payload, "GraphStartRejected"); return; }
            if (graph == "club" && runner != null &&
                !runner.RequestTravel(clubMemberId, clubPlaceId, clubMemberPointId, "club.gather"))
            {
                graphs.Cancel(run);
                world.Schedules.Cancel(item.Payload, "ClubMemberTravelRejected");
                world.Emit("community.club.travel_refused", clubMemberId, clerkId,
                    clubMemberPointId ?? "region.origin", clubPlaceId);
            }
        }

        private void OnGraphFailed(string key, ActionFailureDiagnostic failure)
        {
            var duty = key == "shift" ? state.ShiftDutyId : key == "club" ? state.ClubDutyId :
                key == "purchase.clerk" ? state.PurchaseDutyId : null;
            if (duty != null) world.Schedules.Cancel(duty, "GraphFailed");
            world.Emit("trade.graph.failed", clerkId, key, failure?.ToString(), shopPlaceId);
        }

        public void OnRegionUnavailable(string regionId, bool unloaded)
        {
            if (world == null || state == null) return;
            var reason = unloaded ? "region.unloaded" : "region.closed";
            if (graphs != null)
                foreach (var runKey in graphs.CancelRegion(regionId))
                {
                    var dutyId = runKey == "shift" ? state.ShiftDutyId : runKey == "club" ? state.ClubDutyId :
                        runKey == "purchase.clerk" ? state.PurchaseDutyId : null;
                    if (dutyId != null) world.Schedules.Cancel(dutyId, reason);
                }
            if (regionId == shopPlaceId)
            {
                CancelUnavailableDuty(state.ShiftDutyId, unloaded, reason);
                CancelUnavailableDuty(state.PurchaseDutyId, unloaded, reason);
                if (!string.IsNullOrEmpty(state.ShiftId)) world.Activities.Cancel(state.ShiftId, reason);
            }
            if (regionId == clubPlaceId)
            {
                CancelUnavailableDuty(state.ClubDutyId, unloaded, reason);
                if (!string.IsNullOrEmpty(state.ClubId)) world.Activities.Cancel(state.ClubId, reason);
            }
        }

        private void CancelUnavailableDuty(string dutyId, bool unloaded, string reason)
        {
            if (world.Schedules.Assignments.TryGetValue(dutyId ?? "", out var duty) &&
                (unloaded || duty.State == "Accepted" || duty.State == "Due"))
                world.Schedules.Cancel(dutyId, reason);
        }

        private sealed class TradeAdapter : IWorldActionAdapter, IWorldActionRestoreValidator
        {
            private readonly WorkAndTradeDomainModule owner;
            private readonly Checkpoint saved;
            private readonly DomainTravel travel;
            public bool Restarting;
            public TradeAdapter(WorkAndTradeDomainModule module, Checkpoint checkpoint, SocialWorldRunner runner)
            { owner = module; saved = checkpoint; travel = new DomainTravel(runner); }
            private string Place(ActionExecutionContext context) =>
                context.Instance.ActionId == "community.walking_club.meetup" ? owner.clubPlaceId : owner.shopPlaceId;
            private string Point(ActionExecutionContext context) =>
                context.Instance.ActionId == "community.walking_club.meetup" ? owner.clubPointId :
                context.Instance.ActionId == "trade.purchase" ? owner.purchasePointId : owner.shiftPointId;
            public bool CanRestore(ActionExecutionContext context) =>
                (context.Instance.ActorId == owner.clerkId ||
                    (context.Instance.ActionId == "trade.purchase" && context.Instance.ActorId == owner.studentCustomerId)) &&
                travel.CanRestore(context, Place(context), Point(context));

            public ActionStepResult Step(ActionExecutionContext context, ActionPhase phase)
            {
                if (!CanRestore(context)) return context.Fail(ActionFailureCode.InvalidTarget);
                if (!travel.CanAccess(context, Place(context))) return context.Fail(ActionFailureCode.InvalidTarget);
                if (phase == ActionPhase.Moving && context.Instance.ActionId != "trade.pay_wage")
                    return travel.Move(context, Place(context), Point(context));
                if (phase != ActionPhase.Executing) return ActionStepResult.Success;
                if (context.World.Agents[context.Instance.ActorId].PlaceId != Place(context))
                    return context.Fail(ActionFailureCode.InvalidTarget);
                switch (context.Instance.ActionId)
                {
                    case "trade.shift": return Activity(context, false);
                    case "trade.pay_wage": return PayWage(context);
                    case "trade.purchase": return Purchase(context);
                    case "community.walking_club.meetup": return Activity(context, true);
                    default: return context.Fail(ActionFailureCode.UnknownAction);
                }
            }

            private ActionStepResult Activity(ActionExecutionContext context, bool club)
            {
                if (club ? saved.ClubCompleted : saved.WagePaid) return ActionStepResult.Success;
                var simulation = context.World;
                var dutyId = club ? saved.ClubDutyId : saved.ShiftDutyId;
                if (!simulation.Schedules.Assignments.TryGetValue(dutyId ?? "", out var duty) ||
                    duty.State != "Accepted") return context.Fail(ActionFailureCode.ExecutionFailed);
                var sessionId = club ? saved.ClubId : saved.ShiftId;
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.TryCommitEffect("activity.create", () =>
                    {
                        var session = simulation.Activities.Create(club ? "community.walking_club.meetup" : "trade.shift",
                            owner.clerkId, duty.PlaceId, duty.StartsAt, duty.LatestStartAt,
                            duty.EndsAt - duty.StartsAt, 2, 2);
                        if (club) saved.ClubId = session.Id; else saved.ShiftId = session.Id;
                        var partner = club ? owner.clubMemberId : owner.coworkerId;
                        simulation.Activities.Invite(session.Id, partner, club ? "Member" : "Coworker");
                        simulation.Activities.Respond(session.Id, partner, true);
                        if (!club)
                        {
                            var norm = simulation.Norms.Publish("trade.no_running", owner.clerkId, owner.shopCollectiveId);
                            simulation.Norms.Inform(owner.clerkId, norm.Id, "staff.notice");
                            simulation.Norms.Inform(owner.coworkerId, norm.Id, "staff.notice");
                        }
                    });
                    sessionId = club ? saved.ClubId : saved.ShiftId;
                }
                if (!simulation.Activities.Sessions.TryGetValue(sessionId ?? "", out var activity))
                    return context.Fail(ActionFailureCode.InvalidTarget);
                if (activity.State == "Inviting") simulation.Activities.Start(sessionId);
                if (activity.State == "Cancelled") return context.Fail(ActionFailureCode.ExecutionFailed);
                if (activity.State != "Completed") return ActionStepResult.Running;
                if (!DomainActivity.Participated(simulation, sessionId, owner.clerkId))
                    return context.Fail(ActionFailureCode.ExecutionFailed);
                if (club)
                    context.TryCommitEffect("club.complete", () =>
                    {
                        if (!simulation.Schedules.Complete(dutyId, owner.clerkId))
                            throw new InvalidOperationException("Club duty cannot be completed.");
                        saved.ClubCompleted = true;
                        simulation.Emit("community.club.completed", owner.clerkId, sessionId, null, owner.clubPlaceId);
                    });
                return ActionStepResult.Success;
            }

            private ActionStepResult PayWage(ActionExecutionContext context)
            {
                if (saved.WagePaid) return ActionStepResult.Success;
                var simulation = context.World;
                if (!simulation.Activities.Sessions.TryGetValue(saved.ShiftId ?? "", out var session) ||
                    session.State != "Completed" || !DomainActivity.Participated(simulation, session.Id, owner.clerkId) ||
                    !simulation.Schedules.Assignments.TryGetValue(saved.ShiftDutyId ?? "", out var duty) || duty.State != "Accepted")
                    return context.Fail(ActionFailureCode.ExecutionFailed);
                if (!simulation.Economy.Transfer("trade.wage." + saved.ShiftDutyId,
                    owner.shopCollectiveId, owner.clerkId, "money.credit", 6))
                    return context.Fail(ActionFailureCode.ResourceUnavailable);
                context.TryCommitEffect("wage.complete", () =>
                {
                    if (!simulation.Schedules.Complete(saved.ShiftDutyId, owner.clerkId))
                        throw new InvalidOperationException("Shift duty cannot be completed.");
                    saved.WagePaid = true;
                    simulation.Emit("trade.wage.paid", owner.shopCollectiveId, owner.clerkId, saved.ShiftId, owner.shopPlaceId);
                });
                return ActionStepResult.Success;
            }

            private ActionStepResult Purchase(ActionExecutionContext context)
            {
                bool clerk = context.Instance.ActorId == owner.clerkId;
                if (clerk ? saved.ClerkPurchaseCompleted : saved.StudentPurchaseCompleted) return ActionStepResult.Success;
                var simulation = context.World;
                if (saved.ShopServiceSuspended)
                {
                    // This is knowledge gained by reaching the counter, not a global fact lookup
                    // during planning. Observers elsewhere do not learn about it automatically.
                    context.TryCommitEffect("purchase.service_observed", () =>
                    {
                        var notice = simulation.Emit(AvailabilityTopic, context.Instance.ActorId, owner.shopPlaceId,
                            "unavailable", owner.shopPlaceId);
                        simulation.Knowledge.Observe(context.Instance.ActorId, notice,
                            retentionSeconds: 1800, halfLifeSeconds: 0);
                    });
                    return context.Fail(ActionFailureCode.ResourceUnavailable);
                }
                if (clerk && (!saved.WagePaid ||
                    !simulation.Schedules.Assignments.TryGetValue(saved.PurchaseDutyId ?? "", out var duty) ||
                    duty.State != "Accepted")) return context.Fail(ActionFailureCode.ExecutionFailed);
                // A visible interaction lasts at least one tick; it uses the simulation clock, including after load.
                if (simulation.Time < context.Instance.StartedAt + 10) return ActionStepResult.Running;
                var transaction = "trade.purchase." + saved.PurchaseDutyId + "." + context.Instance.ActorId;
                if (!simulation.Economy.Exchange(transaction, context.Instance.ActorId, owner.shopCollectiveId,
                    "food.snack", 1, "money.credit", clerk ? 3 : 2))
                    return context.Fail(ActionFailureCode.ResourceUnavailable);
                context.TryCommitEffect("purchase.complete", () =>
                {
                    if (clerk)
                    {
                        if (!simulation.Schedules.Complete(saved.PurchaseDutyId, owner.clerkId))
                            throw new InvalidOperationException("Purchase duty cannot be completed.");
                        saved.ClerkPurchaseCompleted = true;
                    }
                    else saved.StudentPurchaseCompleted = true;
                    simulation.Emit(clerk ? "trade.clerk.purchase.completed" : "trade.customer.purchase.completed",
                        context.Instance.ActorId, context.Instance.ActorId, transaction, owner.shopPlaceId);
                });
                return ActionStepResult.Success;
            }

            public void OnExit(ActionExecutionContext context, ActionExitReason reason)
            {
                travel.Exit(context, Place(context), reason);
                if (reason == ActionExitReason.Completed || Restarting) return;
                string session = context.Instance.ActionId == "trade.shift" ? saved.ShiftId :
                    context.Instance.ActionId == "community.walking_club.meetup" ? saved.ClubId : null;
                if (!string.IsNullOrEmpty(session)) context.World.Activities.Cancel(session, "graph.interrupted");
                string duty = context.Instance.ActionId == "trade.shift" || context.Instance.ActionId == "trade.pay_wage" ? saved.ShiftDutyId :
                    context.Instance.ActionId == "community.walking_club.meetup" ? saved.ClubDutyId :
                    context.Instance.ActorId == owner.clerkId ? saved.PurchaseDutyId : null;
                if (duty != null) context.World.Schedules.Cancel(duty, "graph.interrupted");
            }
        }

        public string CaptureScenarioState()
        {
            if (state == null) throw new InvalidOperationException("Work and trade is not initialized.");
            state.Graphs = graphs.Capture(); return JsonUtility.ToJson(state);
        }

        public void ValidateScenarioState(SocialWorldRunner sceneRunner, SimulationWorld simulation, string json)
        {
            var saved = Parse(json); RequireBindings(simulation);
            ValidateDuty(simulation, saved.ShiftDutyId, "trade.shift", shopPlaceId, saved.WagePaid);
            ValidateDuty(simulation, saved.PurchaseDutyId, "trade.purchase", shopPlaceId, saved.ClerkPurchaseCompleted);
            ValidateDuty(simulation, saved.ClubDutyId, "community.walking_club.meetup", clubPlaceId, saved.ClubCompleted);
            ValidateActivity(simulation, saved.ShiftId, "trade.shift", shopPlaceId, saved.WagePaid);
            ValidateActivity(simulation, saved.ClubId, "community.walking_club.meetup", clubPlaceId, saved.ClubCompleted);
            foreach (var run in saved.Graphs)
            {
                if (run == null || run.Graph == null) throw new InvalidOperationException("Missing saved domain graph.");
                var expectedGraph = run.RunKey == "shift" ? "shift" : run.RunKey == "club" ? "club" :
                    run.RunKey == "purchase.clerk" || run.RunKey == "purchase.customer" ? "purchase" : null;
                var actor = run.RunKey == "purchase.customer" ? studentCustomerId : clerkId;
                if (expectedGraph == null || run.GraphKey != expectedGraph || string.IsNullOrEmpty(actor) || run.Graph.ActorId != actor)
                    throw new InvalidOperationException("Saved work graph belongs to another duty or actor.");
            }
            RequireActiveDutyGraph(simulation, saved, saved.ShiftDutyId, "shift");
            RequireActiveDutyGraph(simulation, saved, saved.PurchaseDutyId, "purchase.clerk");
            RequireActiveDutyGraph(simulation, saved, saved.ClubDutyId, "club");
            BuildGraphs(sceneRunner, simulation, saved, false, true).Restore(saved.Graphs);
        }

        private static void RequireActiveDutyGraph(SimulationWorld simulation, Checkpoint saved, string dutyId, string runKey)
        {
            if (simulation.Schedules.Assignments[dutyId].State != "Accepted") return;
            foreach (var run in saved.Graphs) if (run.RunKey == runKey) return;
            throw new InvalidOperationException("Accepted work duty has no active graph checkpoint: " + runKey);
        }

        private void ValidateDuty(SimulationWorld simulation, string id, string action, string place, bool completed)
        {
            if (!simulation.Schedules.Assignments.TryGetValue(id ?? "", out var duty) ||
                duty.AgentId != clerkId || duty.ActionId != action || duty.PlaceId != place ||
                completed != (duty.State == "Completed"))
                throw new InvalidOperationException("Saved work duty does not match the module bindings.");
        }

        private void ValidateActivity(SimulationWorld simulation, string id, string kind, string place, bool completed)
        {
            if (string.IsNullOrEmpty(id)) { if (completed) throw new InvalidOperationException("Completed work has no activity."); return; }
            if (!simulation.Activities.Sessions.TryGetValue(id, out var session) || session.Kind != kind ||
                session.OrganizerId != clerkId || session.PlaceId != place || (completed && session.State != "Completed"))
                throw new InvalidOperationException("Saved work activity does not match the module bindings.");
        }

        public void RestoreScenarioState(SocialWorldRunner sceneRunner, SimulationWorld simulation, string json)
        {
            var saved = Parse(json); var restored = BuildGraphs(sceneRunner, simulation, saved, true, true);
            restored.Restore(saved.Graphs);
            Detach(); runner = sceneRunner; world = simulation; state = saved; InstallGraphs(restored);
        }

        private static Checkpoint Parse(string json)
        {
            var saved = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Checkpoint>(json);
            if (saved == null || saved.SchemaVersion != 3 || saved.Graphs == null)
                throw new InvalidOperationException("Invalid work and trade checkpoint; schema 3 is required (service availability and personal knowledge decisions).");
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
            error = "工作与交易领域尚未初始化。";
            return graphs != null && graphs.TryApplyGraph(draft, mode, out error);
        }
        private void Detach()
        {
            if (world != null)
            {
                world.EventPublished -= OnWorldEvent;
                world.GoalReevaluation.Unregister(studentCustomerId, AvailabilityTopic, shopPlaceId);
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
            RegisterCustomerReevaluation();
        }
        private void OnDisable() { Detach(); }
    }
}

