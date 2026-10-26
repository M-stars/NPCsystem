using System.Collections.Generic;
using System.Linq;
using SocialSimulation.Graph;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Graph
{
    internal sealed class SocialGroupView : Group
    {
        public readonly GraphGroup Model;
        public SocialGroupView(GraphGroup model)
        {
            Model = model; viewDataKey = model.Id; title = model.Title ?? "";
            SetPosition(model.Position);
        }

        public override bool AcceptsElement(GraphElement element, ref string reason)
        {
            if (!(element is SocialNodeView) && !(element is SocialNoteView))
            { reason = "分组仅容纳节点与注释。"; return false; }
            return base.AcceptsElement(element, ref reason);
        }
    }

    internal sealed class SocialNoteView : StickyNote
    {
        public readonly GraphNote Model;
        public SocialNoteView(GraphNote model)
        {
            Model = model; viewDataKey = model.Id; title = model.Title; contents = model.Contents;
            if (System.Enum.IsDefined(typeof(StickyNoteTheme), model.Theme)) theme = (StickyNoteTheme)model.Theme;
            if (System.Enum.IsDefined(typeof(StickyNoteFontSize), model.FontSize)) fontSize = (StickyNoteFontSize)model.FontSize;
            SetPosition(model.Position);
        }
    }

    internal sealed class SocialNodeView : Node
    {
        public readonly GraphNode Model;
        public readonly Port Input;
        public readonly Port Output;
        private readonly Label summary;
        private readonly Label traceStatus;

        public SocialNodeView(GraphNode model)
        {
            Model = model;
            viewDataKey = model.Id;
            bool known = System.Enum.IsDefined(typeof(GraphNodeType), model.Type);
            var descriptor = known ? NodeDescriptor.Get(model.Type) : null;
            title = known ? descriptor.Title : "未知节点 · " + (int)model.Type;
            tooltip = known ? "类型 ID：" + model.Type : "原始数据已保留，当前版本无法执行此节点。";
            AddToClassList(descriptor?.Kind == GraphKind.Behavior ? "behavior-node" : "rule-node");

            if (!known || descriptor.HasInput)
            {
                Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Single, typeof(bool));
                Input.portName = "进入";
                Input.userData = "in";
                inputContainer.Add(Input);
            }
            if (!known || descriptor.HasOutput)
            {
                var capacity = known && descriptor.MaxChildren == 1 ? Port.Capacity.Single : Port.Capacity.Multi;
                Output = InstantiatePort(Orientation.Horizontal, Direction.Output, capacity, typeof(bool));
                Output.portName = "控制流";
                Output.userData = "out";
                outputContainer.Add(Output);
            }
            summary = new Label();
            summary.AddToClassList("node-summary");
            extensionContainer.Add(summary);
            traceStatus = new Label { name = "traceStatus" };
            traceStatus.AddToClassList("node-summary");
            extensionContainer.Add(traceStatus);
            RefreshSummary();
            RefreshExpandedState();
            RefreshPorts();
            SetPosition(new Rect(model.Position, new Vector2(220, 130)));
        }

        public void RefreshSummary()
        {
            if (!System.Enum.IsDefined(typeof(GraphNodeType), Model.Type))
            {
                summary.text = "缺少节点定义 · ID " + Model.Id;
                return;
            }
            switch (Model.Type)
            {
                case GraphNodeType.Action: summary.text = Model.Get("actionId", "填写行动 ID"); break;
                case GraphNodeType.Condition: summary.text = Model.Get("conditionId", "填写条件 ID"); break;
                case GraphNodeType.RuleEvent: summary.text = Model.Get("eventType", "填写事件类型"); break;
                case GraphNodeType.RuleNorm: summary.text = Model.Get("normKind") + " · " + Model.Get("actionId"); break;
                case GraphNodeType.RuleEffect: summary.text = Model.Get("commandId", "填写命令 ID"); break;
                case GraphNodeType.Subtree: summary.text = Model.Subgraph == null ? "拖入行为图资产" : Model.Subgraph.name; break;
                case GraphNodeType.Wait:
                case GraphNodeType.Timeout:
                case GraphNodeType.Cooldown: summary.text = Model.Get("ticks") + " Tick"; break;
                case GraphNodeType.Retry: summary.text = "最多 " + Model.Get("attempts") + " 次"; break;
                case GraphNodeType.RuleCondition:
                    summary.text = Model.Get("combine", "and").ToUpperInvariant() + " " +
                        (Model.Get("negate", "false") == "true" ? "NOT " : "") +
                        (string.IsNullOrEmpty(Model.Get("field")) ? "证据检查" : Model.Get("field") + " " +
                            Model.Get("comparison", "equals") + " " + Model.Get("equals") + " " + Model.Get("unit"));
                    break;
                case GraphNodeType.RuleScope:
                    summary.text = "地区 " + Model.Get("areaId", "任意") + " · 角色 " + Model.Get("roleId", "任意");
                    break;
                default: summary.text = NodeDescriptor.Get(Model.Type).Category; break;
            }
        }

        public void SetTrace(GraphStatus status)
        {
            ClearTrace();
            AddToClassList(status == GraphStatus.Running ? "trace-running" :
                status == GraphStatus.Success ? "trace-success" : "trace-failure");
            traceStatus.text = status == GraphStatus.Running ? "运行中" :
                status == GraphStatus.Success ? "成功 / 真" : "失败 / 假";
        }

        public void ClearTrace()
        {
            RemoveFromClassList("trace-running");
            RemoveFromClassList("trace-success");
            RemoveFromClassList("trace-failure");
            traceStatus.text = "";
        }

        public void SetDiagnostic(bool hasError)
        {
            EnableInClassList("node-error", hasError);
        }
    }

    internal sealed class SocialGraphView : GraphView
    {
        private GraphKind kind;
        private readonly Dictionary<string, SocialNodeView> nodeViews = new Dictionary<string, SocialNodeView>();
        public System.Action<SocialNoteView> NoteChanged;

        public SocialGraphView()
        {
            name = "socialGraphView";
            AddToClassList("social-graph-view");
            style.flexGrow = 1;
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();
        }

        public void Load(GraphDocument document)
        {
            kind = document == null ? GraphKind.Behavior : document.Kind;
            nodeViews.Clear();
            ClearSelection();
            foreach (var element in graphElements.ToList()) RemoveElement(element);
            if (document == null) return;
            if (document.Nodes == null) return;
            foreach (var node in document.Nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.Id) || nodeViews.ContainsKey(node.Id)) continue;
                var view = new SocialNodeView(node);
                AddElement(view);
                nodeViews.Add(node.Id, view);
            }
            foreach (var note in document.Notes)
            {
                if (note == null || string.IsNullOrEmpty(note.Id) || FindLayoutElement(note.Id) != null) continue;
                var view = new SocialNoteView(note);
                view.RegisterCallback<StickyNoteChangeEvent>(_ => NoteChanged?.Invoke(view));
                AddElement(view);
            }
            var grouped = new HashSet<string>();
            foreach (var group in document.Groups)
            {
                if (group == null || string.IsNullOrEmpty(group.Id) || FindLayoutElement(group.Id) != null) continue;
                var view = new SocialGroupView(group);
                AddElement(view);
                foreach (var id in group.MemberIds ?? new List<string>())
                {
                    var member = FindLayoutElement(id);
                    if ((member is SocialNodeView || member is SocialNoteView) && grouped.Add(id)) view.AddElement(member);
                }
            }
            if (document.Edges == null) return;
            foreach (var edge in document.Edges)
            {
                if (edge == null) continue;
                SocialNodeView source, target;
                if (!nodeViews.TryGetValue(edge.FromNodeId ?? "", out source) ||
                    !nodeViews.TryGetValue(edge.ToNodeId ?? "", out target) ||
                    source.Output == null || target.Input == null) continue;
                var view = source.Output.ConnectTo(target.Input);
                AddElement(view);
            }
        }

        public SocialNodeView FindNodeView(string id)
        {
            SocialNodeView view;
            return id != null && nodeViews.TryGetValue(id, out view) ? view : null;
        }

        public GraphElement FindLayoutElement(string id)
        { return graphElements.FirstOrDefault(item => item.viewDataKey == id); }

        public void ClearTrace()
        {
            foreach (var view in nodeViews.Values) view.ClearTrace();
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            return ports.ToList().Where(candidate =>
            {
                if (candidate == startPort || candidate.node == startPort.node ||
                    candidate.direction == startPort.direction) return false;
                var from = (startPort.direction == Direction.Output ? startPort.node : candidate.node) as SocialNodeView;
                var to = (startPort.direction == Direction.Input ? startPort.node : candidate.node) as SocialNodeView;
                return from != null && to != null &&
                    GraphCompiler.CanConnect(from.Model.Type, to.Model.Type, kind);
            }).ToList();
        }
    }
}
