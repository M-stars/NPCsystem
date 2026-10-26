using System.Collections.Generic;
using System.Linq;
using SocialSimulation.Graph;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Graph
{
    internal sealed class StructureNodeView : Node
    {
        public readonly StructureNode Model;
        public readonly Port Input;
        public readonly Port Output;
        private readonly Label summary;

        public StructureNodeView(StructureNode model)
        {
            Model = model;
            viewDataKey = model.Id;
            title = model.Type.ToString();
            AddToClassList("structure-node");
            AddToClassList("structure-" + model.Type.ToString().ToLowerInvariant());
            if (model.Type != StructureNodeType.Organization)
            {
                Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
                Input.portName = "关系输入";
                inputContainer.Add(Input);
            }
            if (model.Type != StructureNodeType.Place && model.Type != StructureNodeType.Role)
            {
                Output = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
                Output.portName = "关系输出";
                outputContainer.Add(Output);
            }
            summary = new Label();
            summary.AddToClassList("node-summary");
            extensionContainer.Add(summary);
            RefreshSummary();
            RefreshExpandedState();
            RefreshPorts();
            SetPosition(new Rect(model.Position, new Vector2(220, 130)));
        }

        public void RefreshSummary()
        {
            title = Model.Type.ToString() + " · " + (string.IsNullOrEmpty(Model.Label) ? Model.EntityId : Model.Label);
            summary.text = Model.EntityId;
        }

        public void SetDiagnostic(bool error) { EnableInClassList("node-error", error); }
    }

    internal sealed class SocialStructureGraphView : GraphView
    {
        public StructureEdgeType CurrentEdgeType { get; set; }
        private readonly Dictionary<string, StructureNodeView> nodeViews = new Dictionary<string, StructureNodeView>();
        private readonly Dictionary<string, Edge> edgeViews = new Dictionary<string, Edge>();

        public SocialStructureGraphView()
        {
            name = "socialStructureGraphView";
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

        public void Load(SocialStructureDocument document)
        {
            nodeViews.Clear();
            edgeViews.Clear();
            foreach (var element in graphElements.ToList()) RemoveElement(element);
            if (document == null) return;
            if (document.Nodes == null) return;
            foreach (var node in document.Nodes)
            {
                if (node == null || string.IsNullOrEmpty(node.Id) || nodeViews.ContainsKey(node.Id) ||
                    !System.Enum.IsDefined(typeof(StructureNodeType), node.Type)) continue;
                var view = new StructureNodeView(node);
                nodeViews.Add(node.Id, view);
                AddElement(view);
            }
            if (document.Edges == null) return;
            foreach (var edge in document.Edges)
            {
                if (edge == null) continue;
                StructureNodeView from, to;
                if (!nodeViews.TryGetValue(edge.FromNodeId ?? "", out from) ||
                    !nodeViews.TryGetValue(edge.ToNodeId ?? "", out to) ||
                    from.Output == null || to.Input == null) continue;
                var view = from.Output.ConnectTo(to.Input);
                StyleEdge(view, edge.Id, edge.Type);
                if (!string.IsNullOrEmpty(edge.Id) && !edgeViews.ContainsKey(edge.Id)) edgeViews.Add(edge.Id, view);
                AddElement(view);
            }
        }

        public StructureNodeView FindNodeView(string id)
        { return id != null && nodeViews.TryGetValue(id, out var result) ? result : null; }

        public Edge FindEdgeView(string id)
        { return id != null && edgeViews.TryGetValue(id, out var result) ? result : null; }

        public void RegisterEdge(string id, Edge view)
        { if (!string.IsNullOrEmpty(id) && view != null) edgeViews[id] = view; }

        public static void StyleEdge(Edge edge, string id, StructureEdgeType type)
        {
            edge.userData = id;
            edge.tooltip = type.ToString() + " · " + id;
            foreach (StructureEdgeType value in System.Enum.GetValues(typeof(StructureEdgeType)))
                edge.RemoveFromClassList("edge-" + value.ToString().ToLowerInvariant());
            edge.AddToClassList("edge-" + type.ToString().ToLowerInvariant());
            var badge = edge.Q<Label>("structureEdgeLabel");
            if (badge == null)
            {
                badge = new Label { name = "structureEdgeLabel" };
                badge.AddToClassList("structure-edge-label");
                badge.pickingMode = PickingMode.Ignore;
                edge.Add(badge);
            }
            badge.text = type.ToString();
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            return ports.ToList().Where(candidate =>
            {
                if (candidate == startPort || candidate.node == startPort.node ||
                    candidate.direction == startPort.direction) return false;
                var from = (startPort.direction == Direction.Output ? startPort.node : candidate.node) as StructureNodeView;
                var to = (startPort.direction == Direction.Input ? startPort.node : candidate.node) as StructureNodeView;
                return from != null && to != null &&
                    SocialStructureCompiler.CanConnect(from.Model.Type, to.Model.Type, CurrentEdgeType);
            }).ToList();
        }
    }
}
