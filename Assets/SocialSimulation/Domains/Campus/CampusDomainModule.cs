using System;
using System.Collections.Generic;
using SocialSimulation.Actions;
using SocialSimulation.Domains.Combined;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Domains.Campus
{
    /// <summary>Campus course, inspection and corridor rules on a shared world clock.</summary>
    [DisallowMultipleComponent]
    public sealed class CampusDomainModule : MonoBehaviour, ISocialScenarioPersistence, IRuleEffectHost,
        ISocialActorRuntimeDiagnostics, IGraphWorkbenchRuntimeBridge, ISocialRegionLifecycle
    {
        [Serializable]
        private sealed class StudentCourseLink
        {
            public string StudentId;
            public string SessionId;
            public string DutyId;
        }

        [Serializable]
        private sealed class ScenarioCheckpoint
        {
            public int SchemaVersion = 1;
            public string CourseA;
            public string CourseB;
            public string CorridorNormId;
            public string WarningTaskId;
            public string TeacherDutyA;
            public string TeacherDutyB;
            public string RuleGraphId;
            public int RuleGraphVersion;
            public string RuleGraphRevision;
            public List<StudentCourseLink> CourseLinks = new List<StudentCourseLink>();
            public List<CourseAssignmentCheckpoint> CourseAssignments = new List<CourseAssignmentCheckpoint>();
        }

        [SerializeField] private GraphDocument courseBehaviorGraph;
        [SerializeField] private GraphDocument corridorRuleGraph;

        private SocialWorldRunner runner;
        private SimulationWorld world;
        private readonly Dictionary<string, string> courseByStudent = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> dutyByStudent = new Dictionary<string, string>(StringComparer.Ordinal);
        private CourseBehaviorSystem courseBehavior;
        private CompiledRulePlan corridorRule;
        private string courseA;
        private string courseB;
        private string corridorNormId;
        private string warningTaskId;
        private string teacherDutyA;
        private string teacherDutyB;
        public string CurrentBehaviorGraphId => courseBehavior?.CurrentPlan.DocumentId;
        public int CurrentBehaviorGraphVersion => courseBehavior?.CurrentPlan.Version ?? 0;
        public string CurrentBehaviorGraphRevision => courseBehavior?.CurrentPlan.Revision;
        public string CurrentRuleGraphId => corridorRule?.DocumentId;
        public int CurrentRuleGraphVersion => corridorRule?.Version ?? 0;
        public string CurrentRuleGraphRevision => corridorRule?.Revision;
        public IReadOnlyList<string> ActiveBehaviorGraphVersions => courseBehavior?.ActiveGraphVersions ??
            (IReadOnlyList<string>)Array.Empty<string>();

        public IReadOnlyList<ActorRuntimeSnapshot> GetActorRuntimeSnapshots(string actorId) =>
            courseBehavior?.GetActorRuntimeSnapshots(actorId) ??
            (IReadOnlyList<ActorRuntimeSnapshot>)Array.Empty<ActorRuntimeSnapshot>();

        public bool TryApplyBehaviorGraph(GraphDocument draft, GraphApplyMode mode, out string error) =>
            TryApplyBehaviorGraph(draft, mode == GraphApplyMode.RestartAffected
                ? DemoGraphApplyMode.RestartAffected : DemoGraphApplyMode.ForNewInstances, out error);

        public bool TryApplyBehaviorGraph(GraphDocument draft, DemoGraphApplyMode mode, out string error)
        {
            error = null;
            if (world == null || courseBehavior == null || draft == null)
            { error = "组合世界尚未运行，或行为图为空。"; return false; }
            if (mode == DemoGraphApplyMode.RestartAffected && !world.Paused)
            { error = "重启受影响行为前请暂停模拟。"; return false; }
            var compilation = GraphCompiler.Compile(draft);
            if (!compilation.Success || compilation.BehaviorPlan == null)
            { error = "行为图编译失败：" + string.Join("; ", compilation.Diagnostics); return false; }
            var binding = GraphBindingValidator.ValidateBehavior(compilation.BehaviorPlan,
                courseBehavior, courseBehavior.AssignedActorIds);
            if (!binding.Success)
            { error = "行为图未绑定当前宿主：" + string.Join("; ", binding.Diagnostics); return false; }
            courseBehavior.ApplyPlan(compilation.BehaviorPlan, mode);
            world.Emit("graph.behavior.applied", "graph.workbench", null,
                compilation.BehaviorPlan.DocumentId + "#" + compilation.BehaviorPlan.Version + ":" + mode);
            return true;
        }

        public bool TryApplyRuleGraph(GraphDocument draft, out string error)
        {
            error = null;
            if (world == null || draft == null)
            { error = "组合世界尚未运行，或规则图为空。"; return false; }
            var compilation = GraphCompiler.Compile(draft);
            if (!compilation.Success || compilation.RulePlan == null)
            { error = "规则图编译失败：" + string.Join("; ", compilation.Diagnostics); return false; }
            var binding = GraphBindingValidator.ValidateRule(compilation.RulePlan, courseBehavior);
            if (!binding.Success)
            { error = "规则图未绑定当前宿主：" + string.Join("; ", binding.Diagnostics); return false; }
            corridorRule = compilation.RulePlan;
            world.Emit("graph.rule.applied", "graph.workbench", null,
                corridorRule.DocumentId + "#" + corridorRule.Version);
            return true;
        }

        public string CaptureScenarioState()
        {
            var saved = new ScenarioCheckpoint
            {
                CourseA = courseA, CourseB = courseB, CorridorNormId = corridorNormId,
                WarningTaskId = warningTaskId,
                TeacherDutyA = teacherDutyA, TeacherDutyB = teacherDutyB,
                RuleGraphId = corridorRule?.DocumentId, RuleGraphVersion = corridorRule?.Version ?? 0,
                RuleGraphRevision = corridorRule?.Revision,
                CourseAssignments = courseBehavior.CaptureAssignments()
            };
            foreach (var pair in courseByStudent)
                saved.CourseLinks.Add(new StudentCourseLink { StudentId = pair.Key, SessionId = pair.Value,
                    DutyId = dutyByStudent.TryGetValue(pair.Key, out var duty) ? duty : null });
            saved.CourseLinks.Sort((a, b) => StringComparer.Ordinal.Compare(a.StudentId, b.StudentId));
            return JsonUtility.ToJson(saved);
        }

        public void ValidateScenarioState(SocialWorldRunner sceneRunner, SimulationWorld simulation, string json)
        {
            var saved = ParseCheckpoint(json);
            var behaviorResult = GraphCompiler.Compile(courseBehaviorGraph);
            var ruleResult = GraphCompiler.Compile(corridorRuleGraph);
            if (!behaviorResult.Success || behaviorResult.BehaviorPlan == null ||
                !ruleResult.Success || ruleResult.RulePlan == null)
                throw new InvalidOperationException("Combined demo graph assets are invalid after load.");
            var availableRule = corridorRule != null && corridorRule.DocumentId == saved.RuleGraphId &&
                corridorRule.Version == saved.RuleGraphVersion &&
                corridorRule.Revision == saved.RuleGraphRevision ? corridorRule : ruleResult.RulePlan;
            if (availableRule.DocumentId != saved.RuleGraphId || availableRule.Version != saved.RuleGraphVersion ||
                availableRule.Revision != saved.RuleGraphRevision)
                throw new InvalidOperationException("Saved rule graph version is unavailable.");
            foreach (var link in saved.CourseLinks)
                if (link == null || !simulation.Agents.ContainsKey(link.StudentId) ||
                    !simulation.Activities.Sessions.ContainsKey(link.SessionId) ||
                    (!string.IsNullOrEmpty(link.DutyId) &&
                     !simulation.Schedules.Assignments.ContainsKey(link.DutyId)))
                    throw new InvalidOperationException("Saved course link is invalid.");
            if ((!string.IsNullOrEmpty(saved.TeacherDutyA) &&
                 !simulation.Schedules.Assignments.ContainsKey(saved.TeacherDutyA)) ||
                (!string.IsNullOrEmpty(saved.TeacherDutyB) &&
                 !simulation.Schedules.Assignments.ContainsKey(saved.TeacherDutyB)))
                throw new InvalidOperationException("Saved teacher duty is missing.");
            var validation = new CourseBehaviorSystem(simulation, sceneRunner,
                courseBehavior?.CurrentPlan ?? behaviorResult.BehaviorPlan, false);
            courseBehavior?.CopyAvailablePlansTo(validation);
            validation.RestoreAssignments(saved.CourseAssignments);
        }

        private static ScenarioCheckpoint ParseCheckpoint(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("Scenario checkpoint is missing.");
            var saved = JsonUtility.FromJson<ScenarioCheckpoint>(json);
            if (saved == null || saved.SchemaVersion != 1 || saved.CourseLinks == null ||
                saved.CourseAssignments == null)
                throw new InvalidOperationException("Invalid combined scenario checkpoint.");
            return saved;
        }

        public void RestoreScenarioState(SocialWorldRunner sceneRunner, SimulationWorld simulation, string json)
        {
            var saved = ParseCheckpoint(json);
            if (world != null) world.EventPublished -= OnWorldEvent;
            var previousCourseBehavior = courseBehavior;
            var previousRule = corridorRule;
            runner = sceneRunner;
            world = simulation;
            courseA = saved.CourseA;
            courseB = saved.CourseB;
            corridorNormId = saved.CorridorNormId;
            warningTaskId = saved.WarningTaskId;
            teacherDutyA = saved.TeacherDutyA;
            teacherDutyB = saved.TeacherDutyB;
            courseByStudent.Clear();
            dutyByStudent.Clear();
            foreach (var pair in saved.CourseLinks)
            {
                courseByStudent.Add(pair.StudentId, pair.SessionId);
                if (!string.IsNullOrEmpty(pair.DutyId)) dutyByStudent.Add(pair.StudentId, pair.DutyId);
            }
            var behaviorResult = GraphCompiler.Compile(courseBehaviorGraph);
            var ruleResult = GraphCompiler.Compile(corridorRuleGraph);
            if (!behaviorResult.Success || behaviorResult.BehaviorPlan == null ||
                !ruleResult.Success || ruleResult.RulePlan == null)
                throw new InvalidOperationException("Combined demo graph assets are invalid after load.");
            courseBehavior = new CourseBehaviorSystem(world, runner,
                previousCourseBehavior?.CurrentPlan ?? behaviorResult.BehaviorPlan);
            previousCourseBehavior?.CopyAvailablePlansTo(courseBehavior);
            courseBehavior.RestoreAssignments(saved.CourseAssignments);
            corridorRule = previousRule != null && previousRule.DocumentId == saved.RuleGraphId &&
                previousRule.Version == saved.RuleGraphVersion &&
                previousRule.Revision == saved.RuleGraphRevision ? previousRule : ruleResult.RulePlan;
            if (corridorRule.DocumentId != saved.RuleGraphId || corridorRule.Version != saved.RuleGraphVersion ||
                corridorRule.Revision != saved.RuleGraphRevision)
                throw new InvalidOperationException("Saved rule graph version is unavailable.");
            world.AddSystem(courseBehavior);
            world.EventPublished += OnWorldEvent;
        }

        public void Configure(GraphDocument behavior, GraphDocument rule)
        {
            courseBehaviorGraph = behavior;
            corridorRuleGraph = rule;
        }

        public void Initialize(SocialWorldRunner sceneRunner, SimulationWorld simulation)
        {
            if (world != null) world.EventPublished -= OnWorldEvent;
            runner = sceneRunner;
            world = simulation;
            courseByStudent.Clear();
            dutyByStudent.Clear();
            courseA = courseB = corridorNormId = warningTaskId = null;
            teacherDutyA = teacherDutyB = null;
            var behaviorResult = GraphCompiler.Compile(courseBehaviorGraph);
            var ruleResult = GraphCompiler.Compile(corridorRuleGraph);
            if (!behaviorResult.Success || behaviorResult.BehaviorPlan == null ||
                !ruleResult.Success || ruleResult.RulePlan == null)
                throw new InvalidOperationException("Combined demo graphs failed compilation; open the Graph Workbench diagnostics.");
            courseBehavior = new CourseBehaviorSystem(world, runner, behaviorResult.BehaviorPlan);
            corridorRule = ruleResult.RulePlan;
            world.AddSystem(courseBehavior);
            SeedCampus();
            teacherDutyA = CreateCourseDuty("npc.teacher.01", "campus.classroom.a", "teacher.a");
            teacherDutyB = CreateCourseDuty("npc.teacher.02", "campus.classroom.b", "teacher.b");
            ScheduleDay();
            world.EventPublished += OnWorldEvent;
        }

        private void SeedCampus()
        {
            foreach (var place in world.Places.Values)
                if (place.DefinitionId != null && place.DefinitionId.StartsWith("campus.", StringComparison.Ordinal))
                    place.OwnerCollectiveId = "campus.management";
            world.Economy.Seed("npc.teacher.01", "money.credit", 20);

            world.Society.Grant("campus.management", "npc.leader.01", "policy.publish", "campus.management");
            world.Society.Grant("campus.management", "npc.teacher.02", "violation.handle",
                "campus.management", "campus.corridor");
            world.Knowledge.RegisterChannel(new KnowledgeChannel
            {
                Id = "campus.noticeboard", CollectiveId = "campus.management",
                Audience = KnowledgeAudience.Public, PublisherAuthorityAction = "policy.publish",
                RequiresSamePlace = false, ConfidenceHalfLifeSeconds = 0,
                AllowRelay = true
            });
            world.Knowledge.RegisterChannel(new KnowledgeChannel
            {
                Id = "staff.notice", CollectiveId = "campus.management",
                Audience = KnowledgeAudience.Public, PublisherAuthorityAction = "policy.publish",
                RequiresSamePlace = false, ConfidenceHalfLifeSeconds = 0,
                AllowRelay = true
            });
            world.Norms.Definitions.Add("campus.corridor.no_running", new NormDefinition
            {
                Id = "campus.corridor.no_running", Version = "1", Kind = "Prohibit",
                ActionId = "locomotion.run", PlaceTag = "campus.corridor",
                CollectiveId = "campus.management", Formal = true, ActiveFrom = 3900
            });
        }

        private void ScheduleDay()
        {
            Schedule(900, "demo.course.start");
            Schedule(2100, "demo.campus.inspect");
            Schedule(3600, "demo.campus.break");
            Schedule(3900, "demo.policy.publish");
            Schedule(4200, "demo.corridor.choice");
            Schedule(4500, "demo.second.period");
        }

        private void Schedule(double at, string type)
        {
            world.Schedule(at, type, "scenario.combined", null);
        }

        private void OnWorldEvent(WorldEvent item)
        {
            switch (item.Type)
            {
                case "schedule.due": HandleDutyDue(item); break;
                case "demo.course.start": StartCourses(); break;
                case "demo.campus.inspect": StartInspection(); break;
                case "demo.campus.break": StartBreak(); break;
                case "demo.policy.publish": PublishNorms(); break;
                case "demo.corridor.choice": CorridorChoice(); break;
                case "demo.second.period": StartSecondPeriod(); break;
                case "agent.arrived": HandleArrival(item); break;
                case "agent.travel_failed":
                    courseBehavior.NotifyTravelResult(item.SubjectId, item.Payload, false);
                    break;
                case "action.observed": EvaluateObservedAction(item); break;
                case "activity.completed": HandleCourseCompleted(item); break;
            }
        }

        private string CreateCourseDuty(string actorId, string placeId, string suffix)
        {
            // Class times stay anchored to the world origin (start 900, ends 3600), but duties
            // are created lazily from the teacher's due event (At=300), by which point
            // world.Time may already sit past the nominal 300s departure deadline (tick
            // granularity or drift). Clamping DueAt to "now" keeps ScheduleService's expiry
            // rejection from firing while the class window stays put; at Time<=300 this is
            // exactly the nominal DueAt of 300.
            var dueAt = Math.Max(300, world.Time);
            return world.Schedules.Create(new DutyAssignment
            {
                DefinitionId = "campus.course." + suffix, DefinitionVersion = "1.0.0",
                DomainId = "campus", AgentId = actorId, ActionId = "campus.course", PlaceId = placeId,
                DueAt = dueAt, StartsAt = 900, LatestStartAt = 1020, EndsAt = 3600, Priority = 90
            }).Id;
        }

        private void HandleDutyDue(WorldEvent item)
        {
            if (item.Payload != teacherDutyA && item.Payload != teacherDutyB) return;
            if (!world.Schedules.TryAccept(item.Payload, item.SubjectId, out var reason))
            {
                world.Emit("campus.course.duty_refused", item.SubjectId, item.SubjectId,
                    reason, item.PlaceId);
                return;
            }
            StartTravelToClass();
        }

        private void HandleCourseCompleted(WorldEvent item)
        {
            if (item.SubjectId != courseA && item.SubjectId != courseB) return;
            var teacherId = item.SubjectId == courseA ? "npc.teacher.01" : "npc.teacher.02";
            var teacherDuty = item.SubjectId == courseA ? teacherDutyA : teacherDutyB;
            if (Participated(item.SubjectId, teacherId)) world.Schedules.Complete(teacherDuty, teacherId);
            else world.Schedules.Cancel(teacherDuty, "CourseNotAttended");
            foreach (var pair in courseByStudent)
            {
                if (pair.Value != item.SubjectId || !dutyByStudent.TryGetValue(pair.Key, out var dutyId)) continue;
                if (Participated(item.SubjectId, pair.Key)) world.Schedules.Complete(dutyId, pair.Key);
                else world.Schedules.Cancel(dutyId, "CourseNotAttended");
            }
        }

        private bool Participated(string sessionId, string actorId)
        {
            if (!world.Activities.Sessions.TryGetValue(sessionId, out var session)) return false;
            foreach (var person in session.Participants)
                if (person.AgentId == actorId &&
                    (person.Status == "Participating" || person.Status == "Completed")) return true;
            return false;
        }

        private void StartTravelToClass()
        {
            PrepareCourse("campus.classroom.a", "npc.teacher.01", teacherDutyA, 1, 10, ref courseA);
            PrepareCourse("campus.classroom.b", "npc.teacher.02", teacherDutyB, 11, 20, ref courseB);
        }

        private void PrepareCourse(string classroom, string teacherId, string teacherDutyId,
            int firstStudent, int lastStudent, ref string session)
        {
            if (!string.IsNullOrEmpty(session) || !RegionAvailable(classroom) ||
                !world.Schedules.Assignments.TryGetValue(teacherDutyId ?? "", out var teacherDuty) ||
                teacherDuty.State != "Accepted") return;
            // Past the anchored 900s class start there is no course left to prepare, and the
            // duties below could only be rejected as expired; skip instead of throwing.
            if (world.Time > 900) return;
            session = world.Activities.Create("campus.course", teacherId, classroom,
                900, 1020, 2700, 1, 11).Id;
            runner.RequestTravel(teacherId, classroom, "teacher.podium", "campus.course");
            for (var i = firstStudent; i <= lastStudent; i++)
            {
                var id = StudentId(i);
                courseByStudent[id] = session;
                world.Activities.Invite(session, id, "Student");
                var accept = i != 10 && i != 20;
                world.Activities.Respond(session, id, accept);
                if (!accept) continue;
                var dutyId = CreateCourseDuty(id, classroom, "student." + i.ToString("00"));
                if (!world.Schedules.TryAccept(dutyId, id, out var refusal))
                {
                    world.Activities.Respond(session, id, false);
                    world.Emit("campus.course.duty_refused", id, id, refusal, classroom);
                    continue;
                }
                dutyByStudent[id] = dutyId;
                var localSeat = "seat." + (i <= 10 ? i : i - 10).ToString("00");
                courseBehavior.Assign(id, session, classroom, localSeat);
            }
        }

        private void StartCourses()
        {
            if (!TryStartSession(courseA)) world.Emit("course.waiting_for_teacher", "scenario.combined", courseA, null);
            if (!TryStartSession(courseB)) world.Emit("course.waiting_for_teacher", "scenario.combined", courseB, null);
        }

        private bool TryStartSession(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId) && world.Activities.Sessions.TryGetValue(sessionId, out var session) &&
                   RegionAvailable(session.PlaceId) &&
                   world.Activities.Start(sessionId);
        }

        public void OnRegionUnavailable(string regionId, bool unloaded)
        {
            if (world == null || string.IsNullOrEmpty(regionId)) return;
            courseBehavior?.CancelRegion(regionId);
            if (currentRuleEvent != null && currentRuleEvent.PlaceId == regionId) currentRuleEvent = null;
            if (regionId != "campus.corridor" || string.IsNullOrEmpty(warningTaskId) ||
                !world.Tasks.TryGetValue(warningTaskId, out var task) || task.Status != "Pending") return;
            task.Status = "Cancelled";
            world.Emit("warning.cancelled", task.AssigneeId, task.TargetId,
                unloaded ? "region.unloaded" : "region.closed", regionId);
        }

        private bool RegionAvailable(string regionId)
        {
            return runner != null && world != null &&
                runner.RegionViews.TryGetValue(regionId ?? "", out var region) && region != null &&
                world.Places.TryGetValue(regionId, out var place) && place.Open &&
                runner.Content.Regions.TryGetValue(place.DefinitionId, out var definition) &&
                RegionAccess.IsOpenAt(definition, world.Time);
        }


        private void StartInspection()
        {
            runner.RequestTravel("npc.leader.01", "campus.classroom.a", "observation", "campus.inspect");
        }

        private void StartBreak()
        {
            runner.RequestTravel("npc.teacher.02", "campus.corridor", "checkpoint.2", "campus.monitor");
            runner.RequestTravel("npc.student.03", "campus.corridor", "checkpoint.2", "campus.break");
            runner.RequestTravel("npc.student.04", "campus.corridor", "checkpoint.3", "campus.break");
            runner.RequestTravel("npc.student.05", "campus.corridor", "checkpoint.1", "campus.break");
            // One seat is requested by two agents at the same logical instant.
            var first = world.Resources.TryAcquire("campus.office/desk.01", "demo.seat.contest.a", "npc.teacher.01", 30);
            var second = world.Resources.TryAcquire("campus.office/desk.01", "demo.seat.contest.b", "npc.leader.01", 30);
            world.Emit("demo.seat.contest", "scenario.combined", first != null ? "npc.teacher.01" : "none",
                "winner=" + (first?.AgentId ?? "none") + ";loser=" + (second == null ? "npc.leader.01" : "none"), "campus.office");
        }

        private void PublishNorms()
        {
            var norm = world.Norms.Publish("campus.corridor.no_running", "npc.leader.01", "campus.management");
            corridorNormId = norm.Id;
            for (var i = 2; i <= 20; i += 2)
                world.Norms.Inform(StudentId(i), norm.Id, "campus.noticeboard");
            world.Norms.Inform("npc.teacher.02", norm.Id, "staff.notice");
        }

        private void CorridorChoice()
        {
            DecideAndAct("npc.student.03");
            DecideAndAct("npc.student.05");
            DecideAndAct("npc.student.04");
        }

        private void DecideAndAct(string id)
        {
            if (!RegionAvailable("campus.corridor") || world.Agents[id].PlaceId != "campus.corridor") return;
            var personality = world.Agents[id].Personality;
            var choices = new List<GoalCandidate>
            {
                new GoalCandidate { Id = id + ".walk", Kind = "Travel", ActionId = "locomotion.walk",
                    PlaceId = "campus.corridor", Duty = 0.4f, Need = 0.3f,
                    PersonalityFit = (personality.RuleValue + personality.Conscientiousness) * 0.5f },
                new GoalCandidate { Id = id + ".run", Kind = "Travel", ActionId = "locomotion.run",
                    PlaceId = "campus.corridor", Duty = 0.8f, Need = 0.7f,
                    PersonalityFit = (personality.FreedomValue + personality.Extraversion) * 0.5f,
                    TimeCost = 0.1f }
            };
            var selected = world.Decisions.Select(id, choices);
            if (selected == null) return;
            var action = world.Emit(selected.ActionId, id, id, "hurrying.to.next.activity", "campus.corridor");
            if (selected.ActionId != "locomotion.run" || string.IsNullOrEmpty(corridorNormId)) return;
            if (!CanWitness("npc.teacher.02", id, 7f)) return;
            world.Norms.Observe(action, "npc.teacher.02");
        }

        private WorldEvent currentRuleEvent;

        private void EvaluateObservedAction(WorldEvent item)
        {
            var activeRule = corridorRule;
            if (activeRule == null || !RegionAvailable(item.PlaceId) || string.IsNullOrEmpty(item.EvidenceId) ||
                !world.Norms.Evidence.TryGetValue(item.EvidenceId, out var evidence)) return;
            var roleId = runner.NpcViews.TryGetValue(item.SubjectId, out var view) ? view.PrimaryRoleId : null;
            var context = new RuleContext
            {
                EventId = item.Id,
                EventType = item.Type,
                ActorId = item.SubjectId,
                WitnessId = item.EmitterId,
                AreaId = item.PlaceId,
                RoleId = roleId,
                ActionId = evidence.ActionId,
                EvidenceId = evidence.Id,
                HasEvidence = true,
                Tick = world.TickIndex,
                TimeSeconds = world.Time
            };
            var previous = currentRuleEvent;
            currentRuleEvent = item;
            try { RuleExecutor.Evaluate(activeRule, context, this); }
            finally { currentRuleEvent = previous; }
        }

        public void EmitRuleCommand(RuleCommand command, RuleContext context)
        {
            if (command.CommandId != "violation.warn" || string.IsNullOrEmpty(corridorNormId) ||
                !RegionAvailable(context.AreaId) ||
                currentRuleEvent == null || command.EventId != currentRuleEvent.Id ||
                context.EventId != currentRuleEvent.Id || command.TargetActorId != context.ActorId) return;
            var key = "rule:" + command.DocumentId + ":" + command.Version + ":" +
                command.RuleNodeId + ":" + command.EventId;
            world.TryCommitEffect(key, () =>
            {
                var violation = world.Norms.RecordViolation(corridorNormId, context.EvidenceId);
                var response = violation == null ? null : world.Norms.RequestResponse(violation.Id, context.WitnessId);
                if (response == null) return;
                warningTaskId = response.Id;
                runner.RequestTravel(context.WitnessId, context.AreaId, null, "violation.warning");
            }, currentRuleEvent);
        }

        private bool CanWitness(string observerId, string subjectId, float maximumDistance)
        {
            if (!world.Agents.TryGetValue(observerId, out var observer) ||
                !world.Agents.TryGetValue(subjectId, out var subject) || observer.PlaceId != subject.PlaceId ||
                !runner.NpcViews.TryGetValue(observerId, out var observerView) ||
                !runner.NpcViews.TryGetValue(subjectId, out var subjectView)) return false;
            return Vector3.Distance(observerView.transform.position, subjectView.transform.position) <= maximumDistance;
        }

        private void StartSecondPeriod()
        {
            world.Emit("campus.second_period.begins", "scenario.combined", null, null);
            runner.RequestTravel("npc.student.03", "campus.classroom.b", "seat.11", "campus.second_period");
        }

        private void HandleArrival(WorldEvent item)
        {
            if (courseByStudent.TryGetValue(item.SubjectId, out var courseId) &&
                !string.IsNullOrEmpty(courseId) && world.Activities.Sessions.TryGetValue(courseId, out var session))
            {
                courseBehavior.NotifyTravelResult(item.SubjectId, item.Payload, true);
                if (session.State == "Inviting" && world.Time >= session.EarliestStart)
                    TryStartSession(courseId);
            }
            else if (item.Payload == "campus.course")
            {
                if (item.SubjectId == "npc.teacher.01" && world.Time >= 900)
                    TryStartSession(courseA);
                else if (item.SubjectId == "npc.teacher.02" && world.Time >= 900)
                    TryStartSession(courseB);
            }
            if (item.Payload == "violation.warning" && !string.IsNullOrEmpty(warningTaskId) &&
                     world.Tasks.TryGetValue(warningTaskId, out var task) && task.Status == "Pending")
            {
                if (world.Agents.TryGetValue(task.TargetId, out var target) && target.PlaceId == item.PlaceId)
                {
                    task.Status = "Completed";
                    world.Emit("warning.delivered", item.SubjectId, task.TargetId, task.Id, item.PlaceId);
                }
            }
        }

        private static string StudentId(int number) { return "npc.student." + number.ToString("00"); }

        // Mirror WorkAndTrade/Household: disabling must also detach the course behavior system,
        // and re-enabling must resubscribe — otherwise the module never hears events again.
        private void OnEnable()
        {
            if (world == null || courseBehavior == null) return;
            world.AddSystem(courseBehavior);
            world.EventPublished -= OnWorldEvent;
            world.EventPublished += OnWorldEvent;
        }

        private void OnDisable()
        {
            if (world == null) return;
            world.EventPublished -= OnWorldEvent;
            world.RemoveSystem(courseBehavior);
        }
    }
}
