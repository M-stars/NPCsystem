using System;
using System.Collections.Generic;
using System.Linq;
using SocialSimulation.Graph;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Graph
{
    /// <summary>Runs the draft against a private context. No runtime host or global trace is used.</summary>
    internal sealed class RulePreviewPanel
    {
        private readonly VisualElement panel;
        private readonly ScrollView results;
        private readonly Action<string> focus;
        private readonly Action<GraphTraceEvent> highlight;
        private readonly Action clearTrace;
        private readonly Dictionary<string, TextField> fields = new Dictionary<string, TextField>();
        private readonly DoubleField time;
        private readonly LongField tick;
        private readonly Toggle evidence;
        private readonly TextField attributes;
        private readonly VisualElement typedFields;
        private readonly List<TypedInput> typedInputs = new List<TypedInput>();
        private int nextInputId;
        private GraphDocument document;

        private sealed class TypedInput
        {
            public TextField Name;
            public DropdownField Type;
            public TextField Unit;
            public TextField Value;
        }

        public RulePreviewPanel(VisualElement root, Action<string> focus,
            Action<GraphTraceEvent> highlight, Action clearTrace)
        {
            this.focus = focus;
            this.highlight = highlight;
            this.clearTrace = clearTrace;
            panel = root.Q<VisualElement>("rulePreviewPanel");
            results = root.Q<ScrollView>("rulePreviewResults");
            var toolbar = root.Q<VisualElement>("rulePreviewToolbar");
            toolbar.Add(new Label("规则试运行"));
            toolbar.Add(new Button(RunPreview) { text = "试运行", name = "runRulePreview" });
            var input = root.Q<ScrollView>("rulePreviewFields");
            var row = Row(input);
            time = new DoubleField("仿真秒") { name = "previewTimeSeconds" };
            tick = new LongField("Tick") { name = "previewTick" };
            row.Add(time); row.Add(tick);
            AddPair(input, "eventType", "事件类型", "eventId", "事件 ID");
            fields["eventId"].SetValueWithoutNotify("preview-event");
            AddPair(input, "actorId", "行动者 ID", "witnessId", "观察者 ID");
            AddPair(input, "areaId", "地区 ID", "roleId", "角色 ID");
            AddPair(input, "actionId", "动作 ID", "evidenceId", "证据 ID");
            row = Row(input);
            evidence = new Toggle("存在有效证据") { name = "previewHasEvidence" };
            row.Add(evidence);
            attributes = new TextField("附加事件字段") { name = "previewAttributes", multiline = true,
                tooltip = "文本字段，每行 key=value；数值与布尔字段使用下方类型字段。" };
            input.Add(attributes);
            input.Add(new Button(AddTypedInput) { text = "添加类型字段", name = "addTypedRuleField" });
            var heading = new VisualElement();
            heading.AddToClassList("typed-rule-heading");
            foreach (var title in new[] { "字段名", "类型", "单位", "实际值" }) heading.Add(new Label(title));
            input.Add(heading);
            typedFields = new VisualElement { name = "previewTypedFields" };
            input.Add(typedFields);
            AddTypedInput();
            var hint = new Label("条件按路径从左到右计算 AND / OR，NOT 只作用于当前条件。时间窗为 [起始秒, 截止秒)。");
            hint.AddToClassList("preview-explanation");
            input.Add(hint);
            var typeHint = new Label("内置类型字段：timeSeconds = number / seconds，tick = number / ticks，hasEvidence = bool。单位须完全一致；空单位表示无量纲，系统不自动换算。");
            typeHint.AddToClassList("preview-explanation");
            input.Add(typeHint);
        }

        private void AddTypedInput()
        {
            int id = nextInputId++;
            var row = new VisualElement();
            row.AddToClassList("typed-rule-field");
            var value = new TypedInput
            {
                Name = new TextField { name = "previewTypedName" + id, tooltip = "字段名，例如 speed" },
                Type = new DropdownField(new List<string> { "string", "number", "bool" }, 1)
                    { name = "previewTypedType" + id, tooltip = "字段实际类型" },
                Unit = new TextField { name = "previewTypedUnit" + id, tooltip = "数值单位，例如 m/s；空表示无量纲" },
                Value = new TextField { name = "previewTypedValue" + id, tooltip = "实际值；数字使用小数点，例如 1.5" }
            };
            row.Add(value.Name); row.Add(value.Type); row.Add(value.Unit); row.Add(value.Value);
            row.Add(new Button(() => { typedInputs.Remove(value); row.RemoveFromHierarchy(); }) { text = "删除" });
            typedInputs.Add(value);
            typedFields.Add(row);
        }

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement();
            row.AddToClassList("rule-preview-fields-row");
            parent.Add(row);
            return row;
        }

        private void AddPair(VisualElement parent, string first, string firstLabel, string second, string secondLabel)
        {
            var row = Row(parent);
            fields[first] = new TextField(firstLabel) { name = "preview_" + first };
            fields[second] = new TextField(secondLabel) { name = "preview_" + second };
            row.Add(fields[first]); row.Add(fields[second]);
        }

        public void SetDocument(GraphDocument asset)
        {
            bool changed = document != asset;
            document = asset;
            bool isRule = asset != null && asset.Kind == GraphKind.Rule;
            panel.EnableInClassList("hidden", !isRule);
            if (!isRule) return;
            if (changed)
            {
                var trigger = asset.Nodes?.FirstOrDefault(node => node != null && node.Type == GraphNodeType.RuleEvent);
                fields["eventType"].SetValueWithoutNotify(trigger?.Get("eventType") ?? "");
            }
            Invalidate();
        }

        public void Invalidate()
        {
            results.Clear();
            results.Add(new Label("填写上下文后试运行；拟提交命令仅显示在此面板。"));
        }

        public void RunPreview()
        {
            results.Clear();
            clearTrace();
            if (document == null || document.Kind != GraphKind.Rule) return;
            if (double.IsNaN(time.value) || double.IsInfinity(time.value) || time.value < 0 || tick.value < 0)
            {
                AddResult("时间和 Tick 必须是有限的非负数。", "", true);
                return;
            }
            var compiled = GraphCompiler.Compile(document);
            if (!compiled.Success)
            {
                foreach (var entry in compiled.Diagnostics)
                    AddResult(entry.Message, entry.NodeId, true);
                return;
            }
            var context = new RuleContext
            {
                EventType = fields["eventType"].value,
                EventId = fields["eventId"].value,
                ActorId = fields["actorId"].value,
                WitnessId = fields["witnessId"].value,
                AreaId = fields["areaId"].value,
                RoleId = fields["roleId"].value,
                ActionId = fields["actionId"].value,
                EvidenceId = fields["evidenceId"].value,
                HasEvidence = evidence.value,
                TimeSeconds = time.value,
                Tick = tick.value
            };
            foreach (var line in (attributes.value ?? "").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int separator = line.IndexOf('=');
                string key = separator < 0 ? "" : line.Substring(0, separator).Trim();
                if (string.IsNullOrEmpty(key) || RuleContext.IsBuiltInField(key) || context.Attributes.ContainsKey(key))
                {
                    AddResult("附加字段无效或重复：" + line.Trim(), "", true);
                    return;
                }
                context.Attributes.Add(key, line.Substring(separator + 1).Trim());
            }
            foreach (var item in typedInputs)
            {
                string key = (item.Name.value ?? "").Trim();
                string unit = (item.Unit.value ?? "").Trim();
                string raw = item.Value.value ?? "";
                if (key.Length == 0 && unit.Length == 0 && raw.Length == 0) continue;
                if (key.Length == 0 || RuleContext.IsBuiltInField(key) || context.Attributes.ContainsKey(key) ||
                    context.TypedAttributes.ContainsKey(key))
                { AddResult("类型字段名称缺失、重复或覆盖内置字段：" + key, "", true); return; }
                if (!RuleValueCodec.TryGetType(item.Type.value, out var type) ||
                    !RuleValueCodec.TryNormalize(type, raw, out _) || (type != RuleValueType.Number && unit.Length != 0))
                { AddResult("类型字段值或单位无效：" + key + "；number 须为有限数字，bool 须为 true/false。", "", true); return; }
                context.TypedAttributes.Add(key, new RuleFieldValue(type, raw, unit));
            }
            var trace = new List<GraphTraceEvent>();
            var result = RuleExecutor.Evaluate(compiled.RulePlan, context, null, trace.Add, false);
            var norms = RuleExecutor.QueryNorms(compiled.RulePlan, context, trace.Add);
            AddResult("拟提交 " + result.Commands.Count + " 条命令；命中 " + norms.Count + " 项规范。", "");
            foreach (var diagnostic in result.Diagnostics)
                AddResult(diagnostic.Code + " · " + diagnostic.Message, diagnostic.NodeId, true);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in trace)
            {
                visited.Add(entry.NodeId);
                var node = compiled.RulePlan.Nodes[entry.NodeId];
                AddResult((entry.Status == GraphStatus.Success ? "真" : "假") + " · " +
                    NodeDescriptor.Get(node.Type).Title + " · " + entry.Reason, entry.NodeId);
                highlight(entry);
            }
            foreach (var match in norms)
            {
                visited.Add(match.NodeId);
                AddResult("规范 · " + match.NormKind + " " + match.ActionId + " · 优先级 " + match.Priority, match.NodeId);
            }
            foreach (var node in compiled.RulePlan.Nodes.Values)
                if (!visited.Contains(node.Id)) AddResult("未执行 · " + NodeDescriptor.Get(node.Type).Title, node.Id);
            foreach (var command in result.Commands)
                AddResult("拟提交 · " + command.CommandId + " → " +
                    (string.IsNullOrEmpty(command.TargetActorId) ? "（目标为空）" : command.TargetActorId), command.RuleNodeId);
        }

        private void AddResult(string message, string nodeId, bool error = false)
        {
            var item = new Button(() => focus(nodeId)) { text = message, tooltip = message };
            item.AddToClassList("rule-preview-result");
            if (error) item.AddToClassList("diagnostic-error");
            results.Add(item);
        }
    }
}
