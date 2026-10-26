using System;
using System.Collections.Generic;
using System.Globalization;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Graph;

namespace SocialSimulation.Editor.Observer
{
    public sealed class ActorObserverData
    {
        public string ActorId;
        public string DisplayName;
        public string PlaceId;
        public string GoalId;
        public long Tick;
        public double Time;
        public readonly List<string> Identity = new List<string>();
        public readonly List<string> NeedsAndEmotion = new List<string>();
        public readonly List<string> Knowledge = new List<string>();
        public int KnowledgeTotalCount;
        public int KnowledgeMatchedCount;
        public int KnowledgeDisplayedCount;
        public readonly List<string> Decisions = new List<string>();
        public readonly List<string> Events = new List<string>();
        public readonly List<string> Norms = new List<string>();
        public readonly List<string> ResourcesAndSessions = new List<string>();
        public readonly List<string> Graphs = new List<string>();
    }

    /// <summary>Builds an actor-only, read-only view from shared simulation services.</summary>
    public static class ActorObserverDataBuilder
    {
        public static ActorObserverData Build(SimulationWorld world, string actorId,
            IReadOnlyList<ActorRuntimeSnapshot> runtime = null,
            GraphTraceEvent latestGraphTrace = null, string eventFilter = null, string knowledgeFilter = null)
        {
            if (world == null || string.IsNullOrEmpty(actorId) ||
                !world.Agents.TryGetValue(actorId, out var actor)) return null;
            var view = new ActorObserverData
            {
                ActorId = actor.Id, DisplayName = actor.DisplayName,
                PlaceId = actor.PlaceId, GoalId = actor.CurrentGoalId,
                Tick = world.TickIndex, Time = world.Time
            };
            BuildIdentity(world, actor, view.Identity);
            BuildNeeds(actor, view.NeedsAndEmotion);
            BuildKnowledge(world, actorId, knowledgeFilter, view);
            BuildDecisions(world, actor, view.Decisions);
            BuildEvents(world, actorId, eventFilter, view.Events);
            BuildNorms(world, actor, view.Norms);
            BuildResources(world, actorId, view.ResourcesAndSessions);
            BuildGraphs(actorId, runtime, latestGraphTrace, view.Graphs);
            return view;
        }

        private static void BuildIdentity(SimulationWorld world, AgentState actor, List<string> lines)
        {
            lines.Add("模板 " + Value(actor.TemplateId) + "  位置 " + Value(actor.PlaceId));
            foreach (var membershipId in actor.MembershipIds)
                if (world.Society.Memberships.TryGetValue(membershipId, out var member))
                {
                    var name = world.Society.Collectives.TryGetValue(member.CollectiveId,
                        out var collective) ? collective.Name : member.CollectiveId;
                    lines.Add((member.Active ? "成员 " : "原成员 ") + Value(name) +
                        " [" + member.CollectiveId + "]");
                }
            foreach (var roleId in actor.RoleAssignmentIds)
                if (world.Society.Roles.TryGetValue(roleId, out var role))
                    lines.Add((role.Active ? "角色 " : "原角色 ") + Value(role.RoleId) +
                        " @ " + Value(role.CollectiveId) +
                        (string.IsNullOrEmpty(role.ObjectScopeId) ? "" : " / " + role.ObjectScopeId));
            if (lines.Count == 1) lines.Add("无已登记的组织或角色");
        }

        private static void BuildNeeds(AgentState actor, List<string> lines)
        {
            var needs = actor.Needs;
            lines.Add("精力 " + F(needs.Energy) + "  社交 " + F(needs.Social) +
                "  娱乐 " + F(needs.Recreation) + "  饥饿 " + F(needs.Hunger) +
                "  舒适 " + F(needs.Comfort));
            var emotion = actor.Emotion;
            lines.Add("情绪效价 " + F(emotion.Valence) + "  唤醒 " + F(emotion.Arousal) +
                "  压力 " + F(emotion.Stress));
        }

        private static void BuildDecisions(SimulationWorld world, AgentState actor,
            List<string> lines)
        {
            lines.Add("当前目标 " + Value(actor.CurrentGoalId) +
                "  最短承诺至 " + F(actor.GoalCommittedUntil) + " s");
            if (!world.Decisions.LastTrace.TryGetValue(actor.Id, out var trace))
            {
                lines.Add("本次运行尚无决策轨迹；读档后需等待下一次决策。");
                return;
            }
            lines.Add("最近选择 " + Value(trace.SelectedGoalId) + " @ " + F(trace.Time) + " s");
            var candidates = new List<GoalCandidate>(trace.Candidates);
            candidates.Sort((a, b) =>
            {
                var score = b.Score.CompareTo(a.Score);
                return score != 0 ? score : StringComparer.Ordinal.Compare(a.Id, b.Id);
            });
            foreach (var candidate in candidates)
            {
                lines.Add((candidate.Id == trace.SelectedGoalId ? "▶ " : "  ") +
                    Value(candidate.Id) + "  分数 " + F(candidate.Score) +
                    " [职责 " + F(candidate.Duty) + " / 需求 " + F(candidate.Need) +
                    " / 性格 " + F(candidate.PersonalityFit) + " / 社交 " + F(candidate.Social) +
                    " / 承诺 " + F(candidate.Commitment) + " / 时间 " + F(candidate.TimeCost) +
                    " / 体力 " + F(candidate.EnergyCost) + " / 后果 " +
                    F(candidate.ExpectedConsequence) + "]" +
                    (string.IsNullOrEmpty(candidate.RejectionReason) ? "" :
                        "  拒绝原因：" + candidate.RejectionReason));
                if (candidate.KnowledgeAssessments == null) continue;
                for (int i = 0; i < candidate.KnowledgeAssessments.Count; i++)
                {
                    var assessment = candidate.KnowledgeAssessments[i];
                    if (assessment == null) continue;
                    var condition = candidate.KnowledgeConditions != null && i < candidate.KnowledgeConditions.Count
                        ? candidate.KnowledgeConditions[i] : null;
                    lines.Add("知识依据 " + Value(assessment.Topic) + " / " + Value(assessment.SubjectId) +
                        (condition == null ? "" : "  期望陈述「" + Value(condition.ExpectedValue) + "」") +
                        "  " + ConditionState(assessment.State) + (assessment.Allowed ? "  允许候选" : "  拒绝候选") +
                        "  未知策略：" + UnknownPolicy(assessment.UnknownPolicy) + "  扣分 " + F(assessment.ScorePenalty));
                    if (assessment.Evidence != null)
                    {
                        var evidence = assessment.Evidence;
                        lines.Add("决策记录 陈述「" + Value(evidence.Value) + "」  决策时置信度 " + F(assessment.CurrentConfidence) +
                            " @ " + F(trace.Time) + " s  原始事件 " + Value(evidence.OriginEventId));
                        lines.Add("来源 原始 " + Value(evidence.OriginSourceId) + " / 最后传达 " + Value(evidence.LastSourceId) +
                            "  渠道 " + Value(evidence.OriginChannelId) + " → " + Value(evidence.ChannelId) +
                            "  原始时间 " + F(evidence.OccurredAt) + " s  获知 " + F(evidence.LearnedAt) + " s");
                    }
                    else lines.Add("决策记录 无合格个人陈述；" + Value(assessment.Reason));
                }
            }
        }

        private static void BuildKnowledge(SimulationWorld world, string actorId, string filter, ActorObserverData view)
        {
            var records = world.Knowledge.ForAgent(actorId).Query();
            view.KnowledgeTotalCount = records.Count;
            filter = filter?.Trim();
            var matches = new List<KnowledgeRecord>();
            foreach (var record in records)
                if (string.IsNullOrEmpty(filter) || Contains(record.Topic, filter) || Contains(record.SubjectId, filter) ||
                    Contains(record.Value, filter) || Contains(record.OriginSourceId, filter) || Contains(record.LastSourceId, filter) ||
                    Contains(record.OriginChannelId, filter) || Contains(record.ChannelId, filter) || Contains(record.OriginEventId, filter))
                    matches.Add(record);
            matches.Sort((a, b) =>
            {
                int time = b.OccurredAt.CompareTo(a.OccurredAt);
                return time != 0 ? time : StringComparer.Ordinal.Compare(a.OriginEventId, b.OriginEventId);
            });
            view.KnowledgeMatchedCount = matches.Count;
            view.KnowledgeDisplayedCount = Math.Min(64, matches.Count);
            for (int i = 0; i < view.KnowledgeDisplayedCount; i++)
            {
                var record = matches[i];
                view.Knowledge.Add("陈述 " + Value(record.Topic) + " / " + Value(record.SubjectId) + "：「" + Value(record.Value) + "」" +
                    "  当前置信度 " + F(record.ConfidenceAt(world.Time)) + (record.DirectlyObserved ? "  直接观察" : "  收到信息") +
                    "  转述 " + record.RelayDepth + " 次");
                view.Knowledge.Add("来源 原始 " + Value(record.OriginSourceId) + " / 最后传达 " + Value(record.LastSourceId) +
                    "  渠道 " + Value(record.OriginChannelId) + " → " + Value(record.ChannelId) + "  原始事件 " + Value(record.OriginEventId));
                view.Knowledge.Add("时间 发生 " + F(record.OccurredAt) + " s / 获知 " + F(record.LearnedAt) +
                    " s / 到期 " + (record.ExpiresAt == double.MaxValue ? "无期限" : F(record.ExpiresAt) + " s") +
                    (world.Time >= record.ExpiresAt ? "  已过有效期（保留历史）" : "") + (record.Protected ? "  受保护" : ""));
            }
            if (matches.Count == 0) view.Knowledge.Add(records.Count == 0 ? "无当前可查询的个人知识" : "无匹配的个人知识");
        }

        private static bool Contains(string value, string filter) =>
            value != null && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string ConditionState(KnowledgeConditionState state)
        {
            switch (state)
            {
                case KnowledgeConditionState.Matched: return "个人陈述匹配";
                case KnowledgeConditionState.Contradicted: return "个人陈述与目标矛盾";
                case KnowledgeConditionState.Conflicting: return "个人陈述互相冲突";
                case KnowledgeConditionState.Invalid: return "知识条件配置非法";
                default: return "未知或无合格个人信息";
            }
        }

        private static string UnknownPolicy(UnknownKnowledgePolicy policy)
        {
            switch (policy)
            {
                case UnknownKnowledgePolicy.Reject: return "拒绝";
                case UnknownKnowledgePolicy.Penalize: return "允许并扣分";
                default: return "允许尝试";
            }
        }

        private static void BuildEvents(SimulationWorld world, string actorId,
            string filter, List<string> lines)
        {
            for (var i = world.Trace.Count - 1; i >= 0 && lines.Count < 24; i--)
            {
                var item = world.Trace[i];
                if (item.EmitterId != actorId && item.SubjectId != actorId) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    (item.Type == null || item.Type.IndexOf(filter,
                        StringComparison.OrdinalIgnoreCase) < 0)) continue;
                lines.Add(F(item.Time) + "s  " + Value(item.Type) + "  " +
                    Value(item.EmitterId) + " → " + Value(item.SubjectId) +
                    "  事件 " + Value(item.Id) + " / 根因 " + Value(item.RootCauseId) +
                    (string.IsNullOrEmpty(item.ParentEventId) ? "" :
                        " / 上游 " + item.ParentEventId) +
                    (string.IsNullOrEmpty(item.Payload) ? "" : "  " + item.Payload));
            }
            if (lines.Count == 0) lines.Add("无匹配事件");
        }

        private static void BuildNorms(SimulationWorld world, AgentState actor,
            List<string> lines)
        {
            foreach (var known in actor.KnownNormVersions)
                lines.Add("已知 " + known);
            foreach (var evidence in world.Norms.Evidence.Values)
                if (evidence.SubjectId == actor.Id || evidence.ObserverId == actor.Id)
                    lines.Add("证据 " + evidence.Id + "  行动 " + evidence.ActionId +
                        "  观察者 " + evidence.ObserverId + "  可信度 " + F(evidence.Confidence));
            foreach (var violation in world.Norms.Violations.Values)
                if (violation.SubjectId == actor.Id)
                    lines.Add("违规 " + violation.Id + "  规范实例 " + violation.NormInstanceId +
                        "  证据数 " + violation.EvidenceIds.Count +
                        (violation.Processed ? "  已处理" : "  待处理"));
            if (lines.Count == 0) lines.Add("无已知规范、观察证据或违规记录");
        }

        private static void BuildResources(SimulationWorld world, string actorId,
            List<string> lines)
        {
            foreach (var token in world.Resources.Tokens.Values)
                if (token.Active && token.AgentId == actorId)
                    lines.Add("预约 " + token.SlotId + "  Token " + token.Id +
                        "  作用域 " + token.OwnerScopeId + "  到期 " + F(token.ExpiresAt) + " s");
            foreach (var request in world.Resources.Requests.Values)
                if (request.AgentId == actorId && request.Status == "Waiting")
                    lines.Add("等待 " + request.SlotId + "  请求 " + request.Id +
                        "  到期 " + F(request.ExpiresAt) + " s");
            foreach (var session in world.Activities.Sessions.Values)
                foreach (var participant in session.Participants)
                    if (participant.AgentId == actorId)
                    {
                        lines.Add("会话 " + session.Id + "  " + Value(session.Kind) +
                            "  " + session.State + "  身份 " + participant.Role +
                            "  参与 " + participant.Status);
                        break;
                    }
            if (lines.Count == 0) lines.Add("无当前预约、等待或会话");
        }

        private static void BuildGraphs(string actorId,
            IReadOnlyList<ActorRuntimeSnapshot> runtime,
            GraphTraceEvent trace, List<string> lines)
        {
            if (runtime != null)
                foreach (var graph in runtime)
                {
                    if (graph == null || graph.ActorId != actorId) continue;
                    lines.Add("图 " + Value(graph.GraphId) + " v" + graph.GraphVersion +
                        " #" + Short(graph.GraphRevision) + "  实例 " +
                        Value(graph.GraphInstanceId) + "  节点 " + Value(graph.NodeId) +
                        "  动作 " + Value(graph.ActionId) + "  阶段 " +
                        (graph.Phase.HasValue ? graph.Phase.Value.ToString() : "空闲"));
                    if (graph.LastFailure != null)
                        lines.Add("失败 " + graph.LastFailure.Code + ": " +
                            graph.LastFailure.Message);
                }
            if (trace != null && trace.ActorId == actorId)
                lines.Add("最近图事件 " + trace.DocumentId + " v" + trace.Version +
                    " #" + Short(trace.Revision) +
                    "  节点 " + trace.NodeId + "  " + trace.Status +
                    (string.IsNullOrEmpty(trace.Reason) ? "" : "  " + trace.Reason));
            if (lines.Count == 0) lines.Add("无可用的图运行轨迹");
        }

        private static string Value(string value) { return string.IsNullOrEmpty(value) ? "—" : value; }
        private static string Short(string value)
        { return string.IsNullOrEmpty(value) ? "—" : value.Substring(0, Math.Min(12, value.Length)); }
        private static string F(double value)
        { return value.ToString("0.00", CultureInfo.InvariantCulture); }
    }
}
