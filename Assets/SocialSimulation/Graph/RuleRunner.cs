using System;
using System.Collections.Generic;
using System.Globalization;

namespace SocialSimulation.Graph
{
    public sealed class RuleContext
    {
        public string EventId;
        public string EventType;
        public string ActorId;
        public string WitnessId;
        public string AreaId;
        public string RoleId;
        public string ActionId;
        public string EvidenceId;
        public bool HasEvidence;
        public long Tick;
        public double TimeSeconds;
        public readonly Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, RuleFieldValue> TypedAttributes = new Dictionary<string, RuleFieldValue>(StringComparer.Ordinal);

        public static bool IsBuiltInField(string name)
        {
            return name == "eventId" || name == "eventType" || name == "actorId" || name == "witnessId" ||
                name == "areaId" || name == "roleId" || name == "actionId" || name == "evidenceId" ||
                name == "timeSeconds" || name == "tick" || name == "hasEvidence";
        }

        public bool TryGetTypedField(string name, out RuleFieldValue value)
        {
            switch (name)
            {
                case "timeSeconds": value = RuleFieldValue.Number(TimeSeconds, "seconds"); return true;
                case "tick": value = RuleFieldValue.Number(Tick, "ticks"); return true;
                case "hasEvidence": value = RuleFieldValue.Boolean(HasEvidence); return true;
            }
            if (IsBuiltInField(name)) { value = RuleFieldValue.String(GetField(name)); return true; }
            if (TypedAttributes.TryGetValue(name ?? "", out value)) return true;
            if (Attributes.TryGetValue(name ?? "", out var raw)) { value = RuleFieldValue.String(raw); return true; }
            value = null;
            return false;
        }

        public string GetField(string name)
        {
            string value;
            switch (name)
            {
                case "eventId": return EventId ?? "";
                case "eventType": return EventType ?? "";
                case "actorId": return ActorId ?? "";
                case "witnessId": return WitnessId ?? "";
                case "areaId": return AreaId ?? "";
                case "roleId": return RoleId ?? "";
                case "actionId": return ActionId ?? "";
                case "evidenceId": return EvidenceId ?? "";
                default: return Attributes.TryGetValue(name ?? "", out value) ? value : "";
            }
        }
    }

    public sealed class RuleCommand
    {
        public readonly string DocumentId;
        public readonly int Version;
        public readonly string RuleNodeId;
        public readonly string EventId;
        public readonly string CommandId;
        public readonly string TargetActorId;

        public RuleCommand(string documentId, int version, string nodeId,
            string eventId, string commandId, string targetActorId)
        {
            DocumentId = documentId; Version = version; RuleNodeId = nodeId;
            EventId = eventId; CommandId = commandId; TargetActorId = targetActorId;
        }
    }

    public interface IRuleEffectHost
    {
        void EmitRuleCommand(RuleCommand command, RuleContext context);
    }

    public sealed class RuleNormMatch
    {
        public readonly string NodeId;
        public readonly string NormKind;
        public readonly string ActionId;
        public readonly int Priority;

        public RuleNormMatch(string nodeId, string normKind, string actionId, int priority)
        { NodeId = nodeId; NormKind = normKind; ActionId = actionId; Priority = priority; }
    }

    public sealed class RuleResult
    {
        public readonly IReadOnlyList<RuleCommand> Commands;
        public readonly IReadOnlyList<GraphDiagnostic> Diagnostics;
        public int MatchCount { get { return Commands.Count; } }

        internal RuleResult(List<RuleCommand> commands, List<GraphDiagnostic> diagnostics)
        { Commands = commands.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly(); }
    }

    public static class RuleExecutor
    {
        /// <summary>Host may be null for a read-only preview. The caller commits returned commands later.</summary>
        public static RuleResult Evaluate(CompiledRulePlan plan, RuleContext context,
            IRuleEffectHost host = null, Action<GraphTraceEvent> trace = null, bool publishTrace = true)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (context == null) throw new ArgumentNullException("context");
            ValidateTime(context);
            var commands = new List<RuleCommand>();
            var diagnostics = new List<GraphDiagnostic>();
            foreach (var path in plan.EventPlans)
            {
                var eventNode = plan.Nodes[path.EventId];
                if (eventNode.Get("eventType") != context.EventType)
                {
                    Trace(plan, context, trace, eventNode.Id, GraphStatus.Failure,
                        "EventMismatch; actual=" + context.EventType + "; expected=" + eventNode.Get("eventType"), publishTrace);
                    continue;
                }
                Trace(plan, context, trace, eventNode.Id, GraphStatus.Success,
                    "EventMatched; actual=" + context.EventType, publishTrace);
                var scope = plan.Nodes[path.ScopeId];
                if (!MatchesScope(scope, context))
                {
                    Trace(plan, context, trace, scope.Id, GraphStatus.Failure,
                        "OutsideScope; " + ScopeValues(scope, context), publishTrace);
                    continue;
                }
                Trace(plan, context, trace, scope.Id, GraphStatus.Success,
                    "ScopeMatched; " + ScopeValues(scope, context), publishTrace);
                // Evidence is a path-level prerequisite. OR and NOT cannot turn missing evidence
                // into permission to issue a consequence.
                bool needsEvidence = false;
                foreach (var id in path.Conditions)
                    if (plan.Nodes[id].Get("requiresEvidence", "true") == "true")
                    { needsEvidence = true; break; }
                if (needsEvidence && !context.HasEvidence)
                {
                    foreach (var id in path.Conditions)
                        if (plan.Nodes[id].Get("requiresEvidence", "true") == "true")
                        {
                            Trace(plan, context, trace, id, GraphStatus.Failure,
                                "MissingEvidence; hasEvidence=false; evidenceId=" + context.EvidenceId, publishTrace);
                            break;
                        }
                    continue;
                }
                bool accepted = true;
                bool validConditions = true;
                for (int i = 0; i < path.Conditions.Count; i++)
                {
                    var node = plan.Nodes[path.Conditions[i]];
                    if (!TryMatchCondition(node, context, out var predicate, out var actual, out var diagnostic))
                    {
                        diagnostics.Add(diagnostic);
                        Trace(plan, context, trace, node.Id, GraphStatus.Failure,
                            diagnostic.Code + "; " + diagnostic.Message, publishTrace);
                        validConditions = false;
                        break;
                    }
                    if (node.Get("negate", "false") == "true") predicate = !predicate;
                    if (i == 0) accepted = predicate;
                    else if (node.Get("combine", "and") == "or") accepted |= predicate;
                    else accepted &= predicate;
                    Trace(plan, context, trace, node.Id,
                        predicate ? GraphStatus.Success : GraphStatus.Failure,
                        (node.Get("negate", "false") == "true" ? "NOT " : "") +
                        (predicate ? "ConditionMatched" : "ConditionRejected") +
                        "; field=" + node.Get("field") + "; actual=" + actual +
                        "; expected=" + node.Get("equals") + "; type=" + node.Get("valueType", "string") +
                        "; comparison=" + node.Get("comparison", "equals") + "; unit=" + node.Get("unit") +
                        "; combine=" + node.Get("combine", "and") +
                        "; aggregate=" + accepted, publishTrace);
                }
                if (!validConditions || !accepted) continue;
                var effect = plan.Nodes[path.EffectId];
                string target = effect.Get("target", "actor") == "witness" ? context.WitnessId : context.ActorId;
                if (string.IsNullOrWhiteSpace(target))
                {
                    Trace(plan, context, trace, effect.Id, GraphStatus.Failure, "MissingCommandTarget", publishTrace);
                    continue;
                }
                var command = new RuleCommand(plan.DocumentId, plan.Version, effect.Id,
                    context.EventId, effect.Get("commandId"), target);
                commands.Add(command);
                Trace(plan, context, trace, effect.Id, GraphStatus.Success, "CommandEmitted", publishTrace);
                if (host != null) host.EmitRuleCommand(command, context);
            }
            return new RuleResult(commands, diagnostics);
        }

        /// <summary>Norms can be queried without any event; they inform a character's choice.</summary>
        public static IReadOnlyList<RuleNormMatch> QueryNorms(CompiledRulePlan plan, RuleContext context,
            Action<GraphTraceEvent> trace = null, bool publishTrace = false)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (context == null) throw new ArgumentNullException("context");
            ValidateTime(context);
            var matches = new List<RuleNormMatch>();
            foreach (var entry in plan.NormPlans)
            {
                var scope = plan.Nodes[entry.ScopeId];
                bool insideScope = MatchesScope(scope, context);
                if (trace != null || publishTrace)
                    Trace(plan, context, trace, scope.Id, insideScope ? GraphStatus.Success : GraphStatus.Failure,
                        (insideScope ? "ScopeMatched; " : "OutsideScope; ") + ScopeValues(scope, context), publishTrace);
                if (!insideScope) continue;
                var node = plan.Nodes[entry.NormId];
                bool matched = node.Get("actionId") == context.ActionId;
                if (trace != null || publishTrace)
                    Trace(plan, context, trace, node.Id, matched ? GraphStatus.Success : GraphStatus.Failure,
                        "NormAction; actual=" + context.ActionId + "; expected=" + node.Get("actionId"), publishTrace);
                if (!matched) continue;
                matches.Add(new RuleNormMatch(node.Id, node.Get("normKind"), node.Get("actionId"),
                    int.Parse(node.Get("priority"))));
            }
            matches.Sort((a, b) =>
            {
                int comparison = b.Priority.CompareTo(a.Priority);
                return comparison != 0 ? comparison : string.CompareOrdinal(a.NodeId, b.NodeId);
            });
            return matches.AsReadOnly();
        }

        private static bool MatchesScope(CompiledGraphNode scope, RuleContext context)
        {
            return Match(scope.Get("areaId"), context.AreaId) &&
                Match(scope.Get("roleId"), context.RoleId) &&
                Match(scope.Get("actionId"), context.ActionId) &&
                InTimeWindow(scope, context.TimeSeconds);
        }

        private static void ValidateTime(RuleContext context)
        {
            if (double.IsNaN(context.TimeSeconds) || double.IsInfinity(context.TimeSeconds) || context.TimeSeconds < 0 || context.Tick < 0)
                throw new ArgumentOutOfRangeException("context", "Rule time and tick must be finite and non-negative.");
        }

        private static string ScopeValues(CompiledGraphNode scope, RuleContext context)
        {
            return "area=" + context.AreaId + "; role=" + context.RoleId + "; action=" + context.ActionId +
                "; seconds=" + context.TimeSeconds.ToString(CultureInfo.InvariantCulture) +
                "; window=[" + scope.Get("fromSeconds", "−∞") + ", " + scope.Get("untilSeconds", "+∞") + ")";
        }

        private static bool InTimeWindow(CompiledGraphNode scope, double timeSeconds)
        {
            var from = scope.Get("fromSeconds");
            var until = scope.Get("untilSeconds");
            return (string.IsNullOrWhiteSpace(from) ||
                    timeSeconds >= double.Parse(from, CultureInfo.InvariantCulture)) &&
                (string.IsNullOrWhiteSpace(until) ||
                 timeSeconds < double.Parse(until, CultureInfo.InvariantCulture));
        }

        private static bool Match(string required, string actual)
        { return string.IsNullOrEmpty(required) || required == actual; }

        private static bool TryMatchCondition(CompiledGraphNode node, RuleContext context,
            out bool matched, out string actual, out GraphDiagnostic diagnostic)
        {
            matched = false;
            actual = "";
            diagnostic = null;
            var field = node.Get("field");
            if (string.IsNullOrEmpty(field)) { matched = true; return true; }
            RuleValueCodec.TryGetType(node.Get("valueType", "string"), out var expectedType);
            bool explicitField = context.TypedAttributes.ContainsKey(field);
            if (explicitField && (context.Attributes.ContainsKey(field) || RuleContext.IsBuiltInField(field)))
                return ConditionError(node, "AmbiguousRuleField", "字段被重复声明或覆盖内置字段：" + field, out diagnostic);
            RuleFieldValue supplied;
            // Legacy string comparisons keep their original missing-field-as-empty semantics.
            if (expectedType == RuleValueType.String && !explicitField)
                supplied = RuleFieldValue.String(context.GetField(field));
            else if (!context.TryGetTypedField(field, out supplied))
                return ConditionError(node, "MissingRuleFieldValue", "上下文缺少字段：" + field, out diagnostic);
            if (supplied == null || supplied.Type != expectedType)
                return ConditionError(node, "RuleFieldTypeMismatch", "字段类型不匹配：" + field + "；要求 " +
                    RuleValueCodec.TypeId(expectedType) + "，实际 " + (supplied == null ? "null" : RuleValueCodec.TypeId(supplied.Type)), out diagnostic);
            actual = supplied.Value;
            if (!string.Equals(supplied.Unit, node.Get("unit"), StringComparison.Ordinal))
                return ConditionError(node, "RuleUnitMismatch", "字段单位必须完全相同：" + field + "；要求 [" +
                    node.Get("unit") + "]，实际 [" + supplied.Unit + "]；没有隐式换算。", out diagnostic);
            if (!RuleValueCodec.TryNormalize(expectedType, supplied.Value, out var normalized))
                return ConditionError(node, "InvalidRuleFieldValue", "字段值无效：" + field + "=" + supplied.Value +
                    "；类型 " + RuleValueCodec.TypeId(expectedType) + "，数字须使用有限的 InvariantCulture 格式。", out diagnostic);
            if (!RuleValueCodec.TryNormalize(expectedType, node.Get("equals"), out var expected))
                return ConditionError(node, "InvalidRuleExpectedValue", "期望值无法解析。", out diagnostic);
            matched = RuleValueCodec.Compare(expectedType, normalized, expected, node.Get("comparison", "equals"));
            return true;
        }

        private static bool ConditionError(CompiledGraphNode node, string code, string message, out GraphDiagnostic diagnostic)
        {
            diagnostic = new GraphDiagnostic(GraphDiagnosticSeverity.Error, code, node.Id, message);
            return false;
        }

        private static void Trace(CompiledRulePlan plan, RuleContext context, Action<GraphTraceEvent> callback,
            string nodeId, GraphStatus status, string reason, bool publish)
        {
            var entry = new GraphTraceEvent(plan.DocumentId, plan.Version, plan.Revision,
                context.EventId, context.ActorId,
                nodeId, context.Tick, status, reason);
            if (publish) GraphTraceHub.Publish(entry);
            if (callback != null) callback(entry);
        }
    }
}
