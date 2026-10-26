using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SocialSimulation.Graph
{
    public enum GraphDiagnosticSeverity { Warning, Error }

    public sealed class GraphDiagnostic
    {
        public readonly GraphDiagnosticSeverity Severity;
        public readonly string Code;
        public readonly string NodeId;
        public readonly string Message;

        public GraphDiagnostic(GraphDiagnosticSeverity severity, string code, string nodeId, string message)
        { Severity = severity; Code = code; NodeId = nodeId; Message = message; }

        public override string ToString() { return Severity + " " + Code + " " + Message; }
    }

    public sealed class CompiledGraphNode
    {
        public readonly string Id;
        public readonly GraphNodeType Type;
        public readonly IReadOnlyDictionary<string, string> Parameters;
        public readonly IReadOnlyList<string> Children;
        public readonly CompiledBehaviorPlan Subtree;

        internal CompiledGraphNode(GraphNode source, string[] children, CompiledBehaviorPlan subtree)
        {
            Id = source.Id;
            Type = source.Type;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (source.Parameters != null)
                foreach (var parameter in source.Parameters)
                    if (parameter != null && !string.IsNullOrEmpty(parameter.Key))
                        parameters[parameter.Key] = parameter.Value ?? "";
            Parameters = new ReadOnlyDictionary<string, string>(parameters);
            Children = Array.AsReadOnly(children);
            Subtree = subtree;
        }

        public string Get(string key, string fallback = "")
        {
            string value;
            return Parameters.TryGetValue(key, out value) ? value : fallback;
        }
    }

    public sealed class CompiledBehaviorPlan
    {
        public readonly string DocumentId;
        public readonly int Version;
        public readonly string Revision;
        public readonly string RootId;
        public readonly IReadOnlyDictionary<string, CompiledGraphNode> Nodes;
        public readonly CompiledBlackboardSchema Blackboard;

        internal CompiledBehaviorPlan(string documentId, int version, string rootId,
            Dictionary<string, CompiledGraphNode> nodes, CompiledBlackboardSchema blackboard)
        {
            DocumentId = documentId;
            Version = version;
            RootId = rootId;
            Nodes = new ReadOnlyDictionary<string, CompiledGraphNode>(nodes);
            Blackboard = blackboard;
            Revision = GraphRevision.Calculate(documentId, version, GraphKind.Behavior, Nodes, Blackboard);
        }
    }

    public sealed class RuleEventPlan
    {
        public readonly string EventId;
        public readonly string ScopeId;
        public readonly IReadOnlyList<string> Conditions;
        public readonly string EffectId;

        internal RuleEventPlan(string eventId, string scopeId, string[] conditions, string effectId)
        { EventId = eventId; ScopeId = scopeId; Conditions = Array.AsReadOnly((string[])conditions.Clone()); EffectId = effectId; }
    }

    public sealed class RuleNormPlan
    {
        public readonly string ScopeId;
        public readonly string NormId;

        internal RuleNormPlan(string scopeId, string normId)
        { ScopeId = scopeId; NormId = normId; }
    }

    public sealed class CompiledRulePlan
    {
        public readonly string DocumentId;
        public readonly int Version;
        public readonly string Revision;
        public readonly IReadOnlyDictionary<string, CompiledGraphNode> Nodes;
        public readonly IReadOnlyList<RuleEventPlan> EventPlans;
        public readonly IReadOnlyList<RuleNormPlan> NormPlans;
        public readonly CompiledBlackboardSchema Blackboard;

        internal CompiledRulePlan(string documentId, int version,
            Dictionary<string, CompiledGraphNode> nodes, List<RuleEventPlan> events, List<RuleNormPlan> norms,
            CompiledBlackboardSchema blackboard)
        {
            DocumentId = documentId;
            Version = version;
            Nodes = new ReadOnlyDictionary<string, CompiledGraphNode>(nodes);
            EventPlans = events.AsReadOnly();
            NormPlans = norms.AsReadOnly();
            Blackboard = blackboard;
            Revision = GraphRevision.Calculate(documentId, version, GraphKind.Rule, Nodes, Blackboard);
        }
    }

    internal static class GraphRevision
    {
        public static string Calculate(string documentId, int version, GraphKind kind,
            IReadOnlyDictionary<string, CompiledGraphNode> nodes, CompiledBlackboardSchema blackboard)
        {
            var text = new StringBuilder();
            Field(text, documentId);
            Field(text, version.ToString(CultureInfo.InvariantCulture));
            Field(text, ((int)kind).ToString(CultureInfo.InvariantCulture));
            var variableIds = new List<string>(blackboard.Variables.Keys);
            variableIds.Sort(StringComparer.Ordinal);
            foreach (var variableId in variableIds)
            {
                var variable = blackboard.Variables[variableId];
                Field(text, variable.Id); Field(text, variable.Name);
                Field(text, ((int)variable.Type).ToString(CultureInfo.InvariantCulture));
                Field(text, variable.DefaultValue);
                Field(text, ((int)variable.Scope).ToString(CultureInfo.InvariantCulture));
                Field(text, ((int)variable.Access).ToString(CultureInfo.InvariantCulture));
                Field(text, ((int)variable.SavePolicy).ToString(CultureInfo.InvariantCulture));
            }
            var ids = new List<string>(nodes.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var node = nodes[id];
                Field(text, node.Id);
                Field(text, ((int)node.Type).ToString(CultureInfo.InvariantCulture));
                var parameterIds = new List<string>(node.Parameters.Keys);
                parameterIds.Sort(StringComparer.Ordinal);
                Field(text, parameterIds.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var parameterId in parameterIds)
                {
                    Field(text, parameterId);
                    Field(text, node.Parameters[parameterId]);
                }
                Field(text, node.Children.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var childId in node.Children) Field(text, childId);
                Field(text, node.Subtree == null ? "" : node.Subtree.Revision);
            }
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                const string digits = "0123456789abcdef";
                var result = new char[bytes.Length * 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    result[i * 2] = digits[bytes[i] >> 4];
                    result[i * 2 + 1] = digits[bytes[i] & 15];
                }
                return new string(result);
            }
        }

        private static void Field(StringBuilder text, string value)
        {
            value = value ?? "";
            text.Append(value.Length).Append(':').Append(value);
        }
    }

    public sealed class GraphCompileResult
    {
        public readonly IReadOnlyList<GraphDiagnostic> Diagnostics;
        public readonly CompiledBehaviorPlan BehaviorPlan;
        public readonly CompiledRulePlan RulePlan;
        public bool Success { get; private set; }

        internal GraphCompileResult(List<GraphDiagnostic> diagnostics,
            CompiledBehaviorPlan behaviorPlan, CompiledRulePlan rulePlan)
        {
            Diagnostics = diagnostics.AsReadOnly();
            Success = !diagnostics.Exists(item => item.Severity == GraphDiagnosticSeverity.Error);
            BehaviorPlan = Success ? behaviorPlan : null;
            RulePlan = Success ? rulePlan : null;
        }
    }

    /// <summary>Publishing keeps the last valid immutable plan when an edited draft fails validation.</summary>
    public sealed class GraphPlanSlot
    {
        public CompiledBehaviorPlan BehaviorPlan { get; private set; }
        public CompiledRulePlan RulePlan { get; private set; }

        public GraphCompileResult TryApply(GraphDocument draft)
        {
            var result = GraphCompiler.Compile(draft);
            if (result.Success)
            {
                if (result.BehaviorPlan != null) BehaviorPlan = result.BehaviorPlan;
                if (result.RulePlan != null) RulePlan = result.RulePlan;
            }
            return result;
        }

        public GraphCompileResult TryApply(GraphDocument draft, IRuntimeGraphBindings bindings,
            IEnumerable<string> actorIds = null)
        {
            if (bindings == null) throw new ArgumentNullException("bindings");
            var structural = GraphCompiler.Compile(draft);
            if (!structural.Success) return structural;
            var binding = structural.BehaviorPlan != null
                ? GraphBindingValidator.ValidateBehavior(structural.BehaviorPlan, bindings, actorIds)
                : GraphBindingValidator.ValidateRule(structural.RulePlan, bindings);
            var diagnostics = new List<GraphDiagnostic>(structural.Diagnostics);
            diagnostics.AddRange(binding.Diagnostics);
            var result = new GraphCompileResult(diagnostics, structural.BehaviorPlan, structural.RulePlan);
            if (result.Success)
            {
                if (result.BehaviorPlan != null) BehaviorPlan = result.BehaviorPlan;
                if (result.RulePlan != null) RulePlan = result.RulePlan;
            }
            return result;
        }
    }

    public static class GraphCompiler
    {
        public static GraphCompileResult Compile(GraphDocument document)
        {
            return CompileInternal(document, new HashSet<GraphDocument>());
        }

        private static GraphCompileResult CompileInternal(GraphDocument document, HashSet<GraphDocument> stack)
        {
            var diagnostics = new List<GraphDiagnostic>();
            if (document == null)
            {
                AddError(diagnostics, "MissingDocument", "", "图资产为空。");
                return new GraphCompileResult(diagnostics, null, null);
            }
            if (!stack.Add(document))
            {
                AddError(diagnostics, "RecursiveSubtree", "", "子行为图存在递归引用。");
                return new GraphCompileResult(diagnostics, null, null);
            }
            if (string.IsNullOrWhiteSpace(document.DocumentId))
                AddError(diagnostics, "MissingDocumentId", "", "图资产缺少稳定 ID。");
            if (!Enum.IsDefined(typeof(GraphKind), document.Kind))
            {
                AddError(diagnostics, "UnknownGraphKind", "", "图资产类型无效。");
                stack.Remove(document);
                return new GraphCompileResult(diagnostics, null, null);
            }
            if (document.Nodes == null || document.Edges == null || document.Variables == null)
            {
                AddError(diagnostics, "MissingGraphData", "", "图资产的节点或连线列表不存在。");
                stack.Remove(document);
                return new GraphCompileResult(diagnostics, null, null);
            }
            var blackboard = CompileBlackboard(document, diagnostics);

            var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
            foreach (var node in document.Nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.Id))
                {
                    AddError(diagnostics, "MissingNodeId", "", "节点缺少稳定 ID。");
                    continue;
                }
                if (!Enum.IsDefined(typeof(GraphNodeType), node.Type))
                {
                    AddError(diagnostics, "UnknownNodeType", node.Id, "节点类型无效。");
                    continue;
                }
                if (nodes.ContainsKey(node.Id))
                {
                    AddError(diagnostics, "DuplicateNodeId", node.Id, "节点 ID 重复。");
                    continue;
                }
                nodes.Add(node.Id, node);
                if (node.Type == GraphNodeType.Subtree && node.Id.Contains("/"))
                    AddError(diagnostics, "InvalidSubtreeId", node.Id,
                        "子图节点 ID 不能包含 /，否则运行实例路径无法恢复。");
                if (NodeDescriptor.Get(node.Type).Kind != document.Kind)
                    AddError(diagnostics, "WrongGraphKind", node.Id, "节点类型与图类型不匹配。");
                ValidateParameters(node, blackboard, diagnostics);
            }

            var children = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
            var parents = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
            foreach (var pair in nodes)
            {
                children.Add(pair.Key, new List<GraphEdge>());
                parents.Add(pair.Key, new List<GraphEdge>());
            }

            var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in document.Edges)
            {
                if (edge == null) { AddError(diagnostics, "NullEdge", "", "连线为空。"); continue; }
                GraphNode source, target;
                if (!nodes.TryGetValue(edge.FromNodeId ?? "", out source) ||
                    !nodes.TryGetValue(edge.ToNodeId ?? "", out target))
                {
                    AddError(diagnostics, "MissingEndpoint", edge.ToNodeId, "连线引用不存在的节点。");
                    continue;
                }
                if (edge.FromPort != "out" || edge.ToPort != "in" ||
                    !NodeDescriptor.Get(source.Type).HasOutput || !NodeDescriptor.Get(target.Type).HasInput)
                    AddError(diagnostics, "InvalidPort", target.Id, "连线端口类型或方向不合法。");
                if (!CanConnect(source.Type, target.Type, document.Kind))
                    AddError(diagnostics, "InvalidConnection", target.Id, "此节点类型之间不能连接。");
                if (edge.FromNodeId == edge.ToNodeId)
                    AddError(diagnostics, "SelfConnection", target.Id, "节点不能连接自身。");
                string key = edge.FromNodeId + "|" + edge.ToNodeId;
                if (!edgeKeys.Add(key)) AddError(diagnostics, "DuplicateEdge", target.Id, "重复连线。");
                children[source.Id].Add(edge);
                parents[target.Id].Add(edge);
            }

            foreach (var pair in nodes)
            {
                var descriptor = NodeDescriptor.Get(pair.Value.Type);
                int count = children[pair.Key].Count;
                if (count < descriptor.MinChildren || count > descriptor.MaxChildren)
                    AddError(diagnostics, "ChildCount", pair.Key, "子节点数量不符合该节点要求。");
                if (parents[pair.Key].Count > 1)
                    AddError(diagnostics, "MultipleParents", pair.Key, "节点只能有一个上游连接；需要复用时请创建子图引用。");
            }

            var visit = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in nodes.Keys) Visit(id, children, visit, diagnostics);

            CompiledBehaviorPlan behavior = null;
            CompiledRulePlan rule = null;
            if (document.Kind == GraphKind.Behavior)
                behavior = CompileBehavior(document, nodes, children, parents, blackboard, diagnostics, stack);
            else
                rule = CompileRule(document, nodes, children, parents, blackboard, diagnostics);

            stack.Remove(document);
            return new GraphCompileResult(diagnostics, behavior, rule);
        }

        private static CompiledBlackboardSchema CompileBlackboard(GraphDocument document,
            List<GraphDiagnostic> diagnostics)
        {
            // Assets authored before blackboard schemas have Unity's default int value (0).
            if (document.SchemaVersion != 0 && document.SchemaVersion != 1)
                AddError(diagnostics, "UnsupportedSchema", "", "图资产 SchemaVersion 不受支持。");
            var variables = new Dictionary<string, CompiledBlackboardVariable>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in document.Variables)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.Id) || variables.ContainsKey(source.Id))
                {
                    AddError(diagnostics, "DuplicateVariable", source == null ? "" : source.Id,
                        "黑板变量 ID 缺失或重复。");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(source.Name) || !names.Add(source.Name.Trim()))
                    AddError(diagnostics, "DuplicateVariableName", source.Id, "黑板变量名称缺失或重复。");
                if (!Enum.IsDefined(typeof(BlackboardValueType), source.Type) ||
                    !Enum.IsDefined(typeof(BlackboardScope), source.Scope) ||
                    !Enum.IsDefined(typeof(BlackboardAccess), source.Access) ||
                    !Enum.IsDefined(typeof(BlackboardSavePolicy), source.SavePolicy))
                {
                    AddError(diagnostics, "InvalidVariableSchema", source.Id, "黑板变量类型、作用域或保存策略无效。");
                    continue;
                }
                if (!BlackboardValueCodec.TryNormalize(source.Type, source.DefaultValue, out var normalized))
                {
                    AddError(diagnostics, "InvalidVariableDefault", source.Id, "黑板变量默认值与类型不匹配。");
                    continue;
                }
                variables.Add(source.Id, new CompiledBlackboardVariable(source, normalized));
            }
            return new CompiledBlackboardSchema(variables);
        }

        private static void ValidateParameters(GraphNode node, CompiledBlackboardSchema blackboard,
            List<GraphDiagnostic> diagnostics)
        {
            var parameterKeys = new HashSet<string>(StringComparer.Ordinal);
            if (node.Parameters != null)
                foreach (var parameter in node.Parameters)
                    if (parameter == null || string.IsNullOrWhiteSpace(parameter.Key) || !parameterKeys.Add(parameter.Key))
                        AddError(diagnostics, "InvalidParameterKey", node.Id, "参数键为空或重复，无法确定运行值。");
            int number;
            switch (node.Type)
            {
                case GraphNodeType.Action:
                    if (string.IsNullOrWhiteSpace(node.Get("actionId"))) AddError(diagnostics, "MissingAction", node.Id, "请填写行动 ID。");
                    break;
                case GraphNodeType.Condition:
                    if (string.IsNullOrWhiteSpace(node.Get("conditionId"))) AddError(diagnostics, "MissingCondition", node.Id, "请填写条件 ID。");
                    break;
                case GraphNodeType.Wait:
                case GraphNodeType.Timeout:
                case GraphNodeType.Cooldown:
                    if (!int.TryParse(node.Get("ticks"), out number) || number <= 0)
                        AddError(diagnostics, "InvalidTicks", node.Id, "Tick 数必须为正整数。");
                    break;
                case GraphNodeType.Retry:
                    if (!int.TryParse(node.Get("attempts"), out number) || number < 1 || number > 32)
                        AddError(diagnostics, "InvalidRetry", node.Id, "重试次数必须在 1 到 32 之间。");
                    break;
                case GraphNodeType.Subtree:
                    if (node.Subgraph == null || node.Subgraph.Kind != GraphKind.Behavior)
                        AddError(diagnostics, "MissingSubtree", node.Id, "请选择行为图子资产。");
                    break;
                case GraphNodeType.BlackboardSet:
                case GraphNodeType.BlackboardCheck:
                    if (!blackboard.TryGet(node.Get("variableId"), out var variable))
                    {
                        AddError(diagnostics, "MissingVariable", node.Id, "节点引用的黑板变量不存在。");
                        break;
                    }
                    if (node.Type == GraphNodeType.BlackboardSet)
                    {
                        if (variable.Access != BlackboardAccess.ReadWrite)
                            AddError(diagnostics, "ReadOnlyVariable", node.Id, "只读黑板变量不可写入。");
                        if (!BlackboardValueCodec.TryNormalize(variable.Type, node.Get("value"), out _))
                            AddError(diagnostics, "InvalidVariableValue", node.Id, "写入值与变量类型不匹配。");
                    }
                    else
                    {
                        var comparison = node.Get("comparison", "eq");
                        if (comparison != "eq" && comparison != "ne" && comparison != "gt" &&
                            comparison != "gte" && comparison != "lt" && comparison != "lte")
                            AddError(diagnostics, "InvalidComparison", node.Id, "黑板比较运算符无效。");
                        if ((comparison == "gt" || comparison == "gte" || comparison == "lt" ||
                             comparison == "lte") && variable.Type != BlackboardValueType.Integer &&
                            variable.Type != BlackboardValueType.Float)
                            AddError(diagnostics, "InvalidComparisonType", node.Id, "大小比较只适用于整数或浮点数。");
                        if (!BlackboardValueCodec.TryNormalize(variable.Type, node.Get("expected"), out _))
                            AddError(diagnostics, "InvalidVariableValue", node.Id, "期望值与变量类型不匹配。");
                    }
                    break;
                case GraphNodeType.RuleEvent:
                    if (string.IsNullOrWhiteSpace(node.Get("eventType"))) AddError(diagnostics, "MissingEvent", node.Id, "请填写事件类型。");
                    break;
                case GraphNodeType.RuleScope:
                    string fromText = node.Get("fromSeconds");
                    string untilText = node.Get("untilSeconds");
                    bool hasFrom = !string.IsNullOrWhiteSpace(fromText);
                    bool hasUntil = !string.IsNullOrWhiteSpace(untilText);
                    double from = 0, until = 0;
                    bool validFrom = !hasFrom || double.TryParse(fromText, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out from) && !double.IsNaN(from) && !double.IsInfinity(from);
                    bool validUntil = !hasUntil || double.TryParse(untilText, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out until) && !double.IsNaN(until) && !double.IsInfinity(until);
                    if (!validFrom || !validUntil)
                        AddError(diagnostics, "InvalidTimeWindow", node.Id, "时间窗口须填写有限的仿真秒数。");
                    else if (hasFrom && hasUntil && from >= until)
                        AddError(diagnostics, "InvalidTimeWindow", node.Id, "截止仿真秒必须大于起始仿真秒。");
                    break;
                case GraphNodeType.RuleNorm:
                    if (node.Get("normKind") != "obligation" && node.Get("normKind") != "prohibition")
                        AddError(diagnostics, "InvalidNorm", node.Id, "规范类别只能是 obligation 或 prohibition。");
                    if (string.IsNullOrWhiteSpace(node.Get("actionId"))) AddError(diagnostics, "MissingNormAction", node.Id, "请填写规范作用的动作 ID。");
                    if (!int.TryParse(node.Get("priority"), out number)) AddError(diagnostics, "InvalidPriority", node.Id, "优先级必须是整数。");
                    break;
                case GraphNodeType.RuleEffect:
                    if (string.IsNullOrWhiteSpace(node.Get("commandId"))) AddError(diagnostics, "MissingCommand", node.Id, "请填写效果命令 ID。");
                    if (node.Get("target", "actor") != "actor" && node.Get("target", "actor") != "witness")
                        AddError(diagnostics, "InvalidCommandTarget", node.Id, "目标只能是 actor 或 witness。");
                    break;
                case GraphNodeType.RuleCondition:
                    if (!RuleValueCodec.TryGetType(node.Get("valueType", "string"), out var ruleType))
                        AddError(diagnostics, "InvalidRuleValueType", node.Id, "规则值类型只能是 string、number 或 bool。");
                    else
                    {
                        string comparison = node.Get("comparison", "equals");
                        if (!RuleValueCodec.IsComparisonSupported(ruleType, comparison))
                            AddError(diagnostics, "InvalidRuleComparison", node.Id, "规则比较方式无效；大小比较仅支持 number。");
                        if (!RuleValueCodec.TryNormalize(ruleType, node.Get("equals"), out _))
                            AddError(diagnostics, "InvalidRuleExpectedValue", node.Id,
                                "期望值与类型不符；number 须为有限数字，使用小数点，不使用千位分隔符；bool 须为 true/false。");
                        if (ruleType != RuleValueType.Number && !string.IsNullOrEmpty(node.Get("unit")))
                            AddError(diagnostics, "InvalidRuleUnit", node.Id, "只有 number 可以声明单位。");
                        if (string.IsNullOrEmpty(node.Get("field")) &&
                            (ruleType != RuleValueType.String || comparison != "equals" || !string.IsNullOrEmpty(node.Get("unit"))))
                            AddError(diagnostics, "MissingRuleField", node.Id, "类型化比较必须指定事件字段。");
                    }
                    if (node.Get("requiresEvidence", "true") != "true" &&
                        node.Get("requiresEvidence", "true") != "false")
                        AddError(diagnostics, "InvalidEvidenceFlag", node.Id, "证据参数应为 true 或 false。");
                    if (node.Get("combine", "and") != "and" && node.Get("combine", "and") != "or")
                        AddError(diagnostics, "InvalidConditionJoin", node.Id, "条件组合只能是 and 或 or。");
                    if (node.Get("negate", "false") != "true" && node.Get("negate", "false") != "false")
                        AddError(diagnostics, "InvalidConditionNegation", node.Id, "条件取反只能是 true 或 false。");
                    break;
            }
        }

        public static bool CanConnect(GraphNodeType from, GraphNodeType to, GraphKind kind)
        {
            if (!Enum.IsDefined(typeof(GraphNodeType), from) || !Enum.IsDefined(typeof(GraphNodeType), to)) return false;
            var source = NodeDescriptor.Get(from);
            var target = NodeDescriptor.Get(to);
            if (source.Kind != kind || target.Kind != kind || !source.HasOutput || !target.HasInput) return false;
            if (kind == GraphKind.Behavior) return true;
            if (from == GraphNodeType.RuleEvent) return to == GraphNodeType.RuleScope;
            if (from == GraphNodeType.RuleScope) return to == GraphNodeType.RuleCondition || to == GraphNodeType.RuleNorm || to == GraphNodeType.RuleEffect;
            if (from == GraphNodeType.RuleCondition) return to == GraphNodeType.RuleCondition || to == GraphNodeType.RuleEffect;
            return false;
        }

        private static void Visit(string id, Dictionary<string, List<GraphEdge>> children,
            Dictionary<string, int> visit, List<GraphDiagnostic> diagnostics)
        {
            int state;
            if (visit.TryGetValue(id, out state)) return;
            visit[id] = 1;
            foreach (var edge in children[id])
            {
                if (visit.TryGetValue(edge.ToNodeId, out state) && state == 1)
                    AddError(diagnostics, "Cycle", edge.ToNodeId, "图包含环。重复行为须使用有限重试节点。");
                else if (state != 2) Visit(edge.ToNodeId, children, visit, diagnostics);
            }
            visit[id] = 2;
        }

        private static CompiledBehaviorPlan CompileBehavior(GraphDocument document,
            Dictionary<string, GraphNode> nodes, Dictionary<string, List<GraphEdge>> children,
            Dictionary<string, List<GraphEdge>> parents, CompiledBlackboardSchema blackboard,
            List<GraphDiagnostic> diagnostics,
            HashSet<GraphDocument> stack)
        {
            string root = null;
            foreach (var node in nodes.Values)
                if (node.Type == GraphNodeType.Root)
                {
                    if (root != null) AddError(diagnostics, "MultipleRoots", node.Id, "行为图只能有一个入口节点。");
                    root = node.Id;
                }
            if (root == null) AddError(diagnostics, "MissingRoot", "", "行为图需要入口节点。");
            if (root != null && parents[root].Count != 0) AddError(diagnostics, "RootHasParent", root, "入口节点不能有上游连接。");

            var compiled = new Dictionary<string, CompiledGraphNode>(StringComparer.Ordinal);
            foreach (var node in nodes.Values)
            {
                CompiledBehaviorPlan subtree = null;
                if (node.Type == GraphNodeType.Subtree && node.Subgraph != null)
                {
                    var result = CompileInternal(node.Subgraph, stack);
                    foreach (var diagnostic in result.Diagnostics)
                        diagnostics.Add(new GraphDiagnostic(diagnostic.Severity, diagnostic.Code,
                            node.Id, "子图 " + node.Subgraph.name + ": " + diagnostic.Message));
                    subtree = result.BehaviorPlan;
                }
                compiled.Add(node.Id, new CompiledGraphNode(node, OrderedIds(children[node.Id]), subtree));
            }
            if (root != null)
            {
                var reachable = new HashSet<string>(StringComparer.Ordinal);
                MarkReachable(root, children, reachable);
                foreach (var node in nodes.Values)
                    if (!reachable.Contains(node.Id))
                        diagnostics.Add(new GraphDiagnostic(GraphDiagnosticSeverity.Warning, "UnreachableNode", node.Id, "节点不从入口可达。"));
            }
            return new CompiledBehaviorPlan(document.DocumentId, document.Version, root, compiled, blackboard);
        }

        private static CompiledRulePlan CompileRule(GraphDocument document,
            Dictionary<string, GraphNode> nodes, Dictionary<string, List<GraphEdge>> children,
            Dictionary<string, List<GraphEdge>> parents, CompiledBlackboardSchema blackboard,
            List<GraphDiagnostic> diagnostics)
        {
            var compiled = new Dictionary<string, CompiledGraphNode>(StringComparer.Ordinal);
            foreach (var node in nodes.Values)
                compiled.Add(node.Id, new CompiledGraphNode(node, OrderedIds(children[node.Id]), null));
            var eventPlans = new List<RuleEventPlan>();
            var normPlans = new List<RuleNormPlan>();
            foreach (var node in nodes.Values)
            {
                if (node.Type != GraphNodeType.RuleNorm && node.Type != GraphNodeType.RuleEffect) continue;
                var chain = new List<GraphNode>();
                var current = node;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (current != null && seen.Add(current.Id))
                {
                    chain.Add(current);
                    var incoming = parents[current.Id];
                    current = incoming.Count == 1 ? nodes[incoming[0].FromNodeId] : null;
                }
                chain.Reverse();
                if (node.Type == GraphNodeType.RuleNorm)
                {
                    if (chain.Count < 2 || chain[chain.Count - 2].Type != GraphNodeType.RuleScope)
                        AddError(diagnostics, "NormNeedsScope", node.Id, "规范必须直接连接适用范围节点。");
                    else normPlans.Add(new RuleNormPlan(chain[chain.Count - 2].Id, node.Id));
                }
                else
                {
                    if (chain.Count < 3 || chain[0].Type != GraphNodeType.RuleEvent ||
                        chain[1].Type != GraphNodeType.RuleScope)
                    {
                        AddError(diagnostics, "EffectNeedsEvent", node.Id, "效果命令需要事件 → 适用范围的上游路径。");
                        continue;
                    }
                    var conditions = new List<string>();
                    for (int i = 2; i < chain.Count - 1; i++)
                        if (chain[i].Type == GraphNodeType.RuleCondition) conditions.Add(chain[i].Id);
                    if (conditions.Count > 0 && nodes[conditions[0]].Get("combine", "and") == "or")
                        AddError(diagnostics, "FirstConditionCannotOr", conditions[0],
                            "路径首个条件没有前项，组合方式必须是 and。");
                    eventPlans.Add(new RuleEventPlan(chain[0].Id, chain[1].Id, conditions.ToArray(), node.Id));
                }
            }
            if (eventPlans.Count == 0 && normPlans.Count == 0)
                AddError(diagnostics, "EmptyRule", "", "规则图至少需要一项规范声明或一条事件效果路径。");
            // Canvas insertion order must not change which effect runs first. Roots use stable IDs;
            // within a root use the same explicit child order as behavior graphs.
            var executionOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            var roots = new List<string>();
            foreach (var id in nodes.Keys) if (parents[id].Count == 0) roots.Add(id);
            roots.Sort(StringComparer.Ordinal);
            foreach (var id in roots) AssignRuleOrder(id, children, executionOrder);
            eventPlans.Sort((a, b) => CompareRuleOrder(a.EffectId, b.EffectId, executionOrder));
            normPlans.Sort((a, b) => CompareRuleOrder(a.NormId, b.NormId, executionOrder));
            return new CompiledRulePlan(document.DocumentId, document.Version,
                compiled, eventPlans, normPlans, blackboard);
        }

        private static void AssignRuleOrder(string id, Dictionary<string, List<GraphEdge>> children,
            Dictionary<string, int> order)
        {
            if (order.ContainsKey(id)) return;
            order.Add(id, order.Count);
            foreach (var child in OrderedIds(children[id])) AssignRuleOrder(child, children, order);
        }

        private static int CompareRuleOrder(string first, string second, Dictionary<string, int> order)
        {
            int left = order.TryGetValue(first, out var a) ? a : int.MaxValue;
            int right = order.TryGetValue(second, out var b) ? b : int.MaxValue;
            return left != right ? left.CompareTo(right) : string.CompareOrdinal(first, second);
        }

        private static string[] OrderedIds(List<GraphEdge> edges)
        {
            var sorted = new List<GraphEdge>(edges);
            sorted.Sort((a, b) =>
            {
                int value = a.Order.CompareTo(b.Order);
                return value != 0 ? value : string.CompareOrdinal(a.ToNodeId, b.ToNodeId);
            });
            var ids = new string[sorted.Count];
            for (int i = 0; i < sorted.Count; i++) ids[i] = sorted[i].ToNodeId;
            return ids;
        }

        private static void MarkReachable(string id, Dictionary<string, List<GraphEdge>> children, HashSet<string> seen)
        {
            if (!seen.Add(id)) return;
            foreach (var edge in children[id]) MarkReachable(edge.ToNodeId, children, seen);
        }

        private static void AddError(List<GraphDiagnostic> diagnostics, string code, string id, string message)
        { diagnostics.Add(new GraphDiagnostic(GraphDiagnosticSeverity.Error, code, id, message)); }
    }
}
