using System;
using System.Collections.Generic;
using System.Linq;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Graph
{
    public sealed class SocialStructureWorkbenchWindow : EditorWindow
    {
        private const string LayoutPath = "Assets/SocialSimulation/Editor/Graph/GraphWorkbench.uxml";
        private const string StylePath = "Assets/SocialSimulation/Editor/Graph/GraphWorkbench.uss";
        [SerializeField] private SocialStructureDocument document;
        private SocialStructureGraphView graph;
        private VisualElement palette;
        private VisualElement inspector;
        private ScrollView diagnostics;
        private Label status;
        private ObjectField documentField;
        private TextField searchField;
        private bool rebuilding;
        private int spawnIndex;

        [Serializable]
        private sealed class ClipboardData
        {
            public List<StructureNode> Nodes = new List<StructureNode>();
            public List<StructureEdge> Edges = new List<StructureEdge>();
        }

        [MenuItem("Tools/Social Simulation/Social Structure Workbench")]
        public static void ShowWindow()
        {
            var window = GetWindow<SocialStructureWorkbenchWindow>();
            window.titleContent = new GUIContent("Social Structure");
            window.minSize = new Vector2(900, 520);
            window.Show();
        }

        [OnOpenAsset(2)]
        private static bool OpenStructureAsset(int instanceId, int line)
        {
            var selected = Selection.activeObject as SocialStructureDocument;
            if (selected == null) return false;
            ShowWindow();
            GetWindow<SocialStructureWorkbenchWindow>().Open(selected);
            return true;
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            Selection.selectionChanged += OnSelectionChanged;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Selection.selectionChanged -= OnSelectionChanged;
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (layout == null) { rootVisualElement.Add(new Label("缺少 GraphWorkbench.uxml")); return; }
            layout.CloneTree(rootVisualElement);
            var stylesheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (stylesheet != null) rootVisualElement.styleSheets.Add(stylesheet);
            palette = rootVisualElement.Q<VisualElement>("palette");
            inspector = rootVisualElement.Q<VisualElement>("inspector");
            diagnostics = rootVisualElement.Q<ScrollView>("diagnostics");
            status = rootVisualElement.Q<Label>("status");
            var toolbar = rootVisualElement.Q<VisualElement>("toolbar");
            var runtimeToolbar = rootVisualElement.Q<VisualElement>("runtimeToolbar");
            runtimeToolbar.Add(new Button(ApplyToRunningWorld)
                { text = "应用到暂停世界", tooltip = "只接受已由场景绑定的 NPC 与地区；集体、角色、关系和授权可更新。" });
            runtimeToolbar.Add(new Label("结构应用只改逻辑社会关系；新增 NPC 或地区请先由内容包生成场景对象。")
                { name = "structureApplyHint" });
            rootVisualElement.Q<VisualElement>("blackboardPanel").style.display = DisplayStyle.None;
            rootVisualElement.Q<VisualElement>("rulePreviewPanel").style.display = DisplayStyle.None;
            rootVisualElement.Q<VisualElement>("bottom").style.height = 112;
            graph = new SocialStructureGraphView();
            graph.graphViewChanged = OnGraphChanged;
            graph.serializeGraphElements = SerializeSelection;
            graph.unserializeAndPaste = PasteSelection;
            graph.RegisterCallback<MouseUpEvent>(_ => graph.schedule.Execute(RefreshInspector));
            graph.RegisterCallback<KeyUpEvent>(_ => graph.schedule.Execute(RefreshInspector));
            graph.RegisterCallback<ContextualMenuPopulateEvent>(PopulateContextMenu);
            graph.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            graph.RegisterCallback<DragPerformEvent>(OnDragPerform);
            rootVisualElement.Q<VisualElement>("graphHost").Add(graph);

            toolbar.Add(new Button(CreateDocument) { text = "新建结构图" });
            documentField = new ObjectField("结构资产") { objectType = typeof(SocialStructureDocument), value = document };
            documentField.AddToClassList("document-field");
            documentField.RegisterValueChangedCallback(evt => Open(evt.newValue as SocialStructureDocument));
            toolbar.Add(documentField);
            var edgeType = new EnumField("连线类型", StructureEdgeType.Membership);
            edgeType.AddToClassList("edge-type-field");
            edgeType.RegisterValueChangedCallback(evt => graph.CurrentEdgeType = (StructureEdgeType)evt.newValue);
            toolbar.Add(edgeType);
            toolbar.Add(new Button(Save) { text = "保存" });
            toolbar.Add(new Button(ValidateDocument) { text = "校验" });
            toolbar.Add(new Button(() => graph.FrameAll()) { text = "适配画布" });
            toolbar.Add(new Button(GraphWorkbenchWindow.ShowWindow) { text = "行为/规则图" });
            searchField = new TextField("搜索节点");
            searchField.RegisterValueChangedCallback(_ => RefreshPalette());
            rootVisualElement.Q<VisualElement>("paletteHeader").Add(searchField);
            rootVisualElement.Q<Label>(className: "panel-title").text = "结构节点库";
            Open(document);
        }

        public void Open(SocialStructureDocument asset)
        {
            document = asset;
            if (documentField != null) documentField.SetValueWithoutNotify(asset);
            if (graph == null) return;
            rebuilding = true;
            graph.Load(asset);
            rebuilding = false;
            RefreshPalette();
            RefreshInspector();
            ValidateDocument();
        }

        private void OnSelectionChanged()
        {
            if (Selection.activeObject is SocialStructureDocument && Selection.activeObject != document)
                Open(Selection.activeObject as SocialStructureDocument);
        }

        private void OnUndoRedo() { if (document != null) Open(document); }

        private void CreateDocument()
        {
            string path = EditorUtility.SaveFilePanelInProject("创建社会结构图", "SocialStructure", "asset", "选择保存路径");
            if (string.IsNullOrEmpty(path)) return;
            var asset = CreateInstance<SocialStructureDocument>();
            asset.DocumentId = Guid.NewGuid().ToString("N");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            Open(asset);
        }

        private static StructureNode NewNode(StructureNodeType type, Vector2 position)
        {
            var guid = Guid.NewGuid().ToString("N");
            return new StructureNode { Id = guid, EntityId = type.ToString().ToLowerInvariant() + "." + guid.Substring(0, 8),
                Type = type, Position = position, Label = type.ToString() };
        }

        private void AddNode(StructureNodeType type, Vector2 position)
        {
            if (document == null) return;
            Undo.RecordObject(document, "Add structure node");
            var node = NewNode(type, position);
            document.Nodes.Add(node);
            Changed();
            Open(document);
            var view = graph.FindNodeView(node.Id);
            if (view != null) { graph.ClearSelection(); graph.AddToSelection(view); graph.FrameSelection(); }
        }

        private void RefreshPalette()
        {
            if (palette == null) return;
            palette.Clear();
            if (document == null) { palette.Add(new Label("创建或选择社会结构图资产。")); return; }
            string filter = (searchField.value ?? "").Trim();
            foreach (StructureNodeType type in Enum.GetValues(typeof(StructureNodeType)))
            {
                if (filter.Length > 0 && type.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var captured = type;
                var item = new Button(() => AddNode(captured, NextSpawnPosition())) { text = LabelFor(type) };
                item.AddToClassList("palette-item");
                item.tooltip = "点击添加，或拖到画布指定位置。";
                Vector2 press = Vector2.zero;
                bool pressed = false;
                item.RegisterCallback<MouseDownEvent>(evt => { if (evt.button == 0) { pressed = true; press = evt.mousePosition; } });
                item.RegisterCallback<MouseMoveEvent>(evt =>
                {
                    if (!pressed || (evt.mousePosition - press).sqrMagnitude < 64) return;
                    pressed = false;
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.SetGenericData("SocialStructureNodeType", captured);
                    DragAndDrop.StartDrag(LabelFor(captured));
                    evt.StopPropagation();
                });
                item.RegisterCallback<MouseUpEvent>(_ => pressed = false);
                palette.Add(item);
            }
        }

        private Vector2 NextSpawnPosition()
        {
            int index = spawnIndex++;
            return new Vector2(80 + (index % 4) * 260, 100 + (index / 4) * 180);
        }

        private static string LabelFor(StructureNodeType type)
        {
            switch (type)
            {
                case StructureNodeType.Agent: return "NPC / Agent";
                case StructureNodeType.Collective: return "集体 / Collective";
                case StructureNodeType.Organization: return "组织 / Organization";
                case StructureNodeType.Role: return "角色 / Role";
                default: return "地点 / Place";
            }
        }

        private GraphViewChange OnGraphChanged(GraphViewChange change)
        {
            if (rebuilding || document == null) return change;
            bool modified = false;
            bool recorded = false;
            Action record = () => { if (!recorded) { Undo.RecordObject(document, "Edit social structure"); recorded = true; } };
            if (change.elementsToRemove != null)
                foreach (var element in change.elementsToRemove)
                {
                    var node = element as StructureNodeView;
                    if (node != null)
                    {
                        record();
                        document.Nodes.RemoveAll(item => item != null && item.Id == node.Model.Id);
                        document.Edges.RemoveAll(edge => edge != null &&
                            (edge.FromNodeId == node.Model.Id || edge.ToNodeId == node.Model.Id));
                        modified = true;
                        continue;
                    }
                    var edgeView = element as Edge;
                    if (edgeView == null) continue;
                    var id = edgeView.userData as string;
                    if (string.IsNullOrEmpty(id)) continue;
                    record();
                    modified |= document.Edges.RemoveAll(item => item != null && item.Id == id) > 0;
                }
            if (change.edgesToCreate != null)
                foreach (var edgeView in change.edgesToCreate.ToList())
                {
                    var source = edgeView.output == null ? null : edgeView.output.node as StructureNodeView;
                    var target = edgeView.input == null ? null : edgeView.input.node as StructureNodeView;
                    if (source == null || target == null ||
                        !SocialStructureCompiler.CanConnect(source.Model.Type, target.Model.Type, graph.CurrentEdgeType) ||
                        ((graph.CurrentEdgeType == StructureEdgeType.Membership ||
                          graph.CurrentEdgeType == StructureEdgeType.Jurisdiction) &&
                         document.Edges.Any(item => item.FromNodeId == source.Model.Id &&
                             item.ToNodeId == target.Model.Id && item.Type == graph.CurrentEdgeType)))
                    { change.edgesToCreate.Remove(edgeView); continue; }
                    record();
                    var edge = new StructureEdge { Id = Guid.NewGuid().ToString("N"),
                        FromNodeId = source.Model.Id, ToNodeId = target.Model.Id,
                        Type = graph.CurrentEdgeType };
                    if (edge.Type == StructureEdgeType.Authority && source.Model.Type == StructureNodeType.Collective)
                        edge.CollectiveId = source.Model.EntityId;
                    if (edge.Type == StructureEdgeType.Relation)
                    {
                        string baseType = edge.RelationType;
                        int suffix = 2;
                        while (document.Edges.Any(item => item.Type == edge.Type &&
                            item.FromNodeId == edge.FromNodeId && item.ToNodeId == edge.ToNodeId &&
                            item.RelationType == edge.RelationType))
                            edge.RelationType = baseType + "." + suffix++;
                    }
                    if (edge.Type == StructureEdgeType.Authority)
                    {
                        edge.ActionId = "action.new";
                        int suffix = 2;
                        while (document.Edges.Any(item => item.Type == edge.Type &&
                            item.FromNodeId == edge.FromNodeId && item.ToNodeId == edge.ToNodeId &&
                            item.ActionId == edge.ActionId))
                            edge.ActionId = "action.new." + suffix++;
                    }
                    document.Edges.Add(edge);
                    SocialStructureGraphView.StyleEdge(edgeView, edge.Id, edge.Type);
                    graph.RegisterEdge(edge.Id, edgeView);
                    modified = true;
                }
            if (change.movedElements != null)
                foreach (var element in change.movedElements)
                {
                    var view = element as StructureNodeView;
                    if (view == null || view.Model.Position == view.GetPosition().position) continue;
                    record();
                    view.Model.Position = view.GetPosition().position;
                    modified = true;
                }
            if (modified) Changed();
            return change;
        }

        private void Changed()
        {
            document.IncrementVersion();
            EditorUtility.SetDirty(document);
            if (status != null) status.text = "社会结构 · 草稿 v" + document.Version + " · 尚未保存";
        }

        private void Save()
        {
            if (document == null) return;
            EditorUtility.SetDirty(document);
            AssetDatabase.SaveAssets();
            ValidateDocument();
        }

        private void ApplyToRunningWorld()
        {
            if (document == null) { status.text = "请先选择社会结构图。"; return; }
            var compilation = SocialStructureCompiler.Compile(document);
            ValidateDocument();
            if (!compilation.Success)
            { status.text = "结构草稿编译失败，运行世界未改动。"; return; }
            if (!EditorApplication.isPlaying)
            { status.text = "请先进入 Play 模式并暂停世界。"; return; }
            var runner = UnityEngine.Object.FindAnyObjectByType<SocialWorldRunner>();
            if (runner == null || runner.World == null)
            { status.text = "当前场景没有运行中的 SocialWorldRunner。"; return; }
            if (!runner.World.Paused)
            { status.text = "请先暂停世界，再应用社会结构。"; return; }
            foreach (var node in compilation.Plan.Nodes)
            {
                bool bound = node.Type == StructureNodeType.Agent
                    ? runner.World.Agents.ContainsKey(node.EntityId) && runner.NpcViews.ContainsKey(node.EntityId)
                    : node.Type != StructureNodeType.Place ||
                      (runner.World.Places.ContainsKey(node.EntityId) && runner.RegionViews.ContainsKey(node.EntityId));
                if (bound) continue;
                var message = node.Type == StructureNodeType.Agent
                    ? "NPC 没有现存世界实体及场景绑定：" + node.EntityId
                    : "地区没有现存世界实体及场景绑定：" + node.EntityId;
                var capturedId = node.Id;
                var item = new Button(() => FocusDiagnostic(capturedId)) { text = "Error · " + message };
                item.AddToClassList("diagnostic-error");
                diagnostics.Add(item);
                status.text = "结构未应用：新增 NPC/地区需要内容包与场景生成流程。";
                return;
            }
            var result = compilation.Plan.ApplyTo(runner.World);
            if (!result.Success)
            {
                foreach (var entry in result.Diagnostics)
                {
                    var captured = entry;
                    var item = new Button(() => FocusDiagnostic(captured.NodeId))
                        { text = entry.Severity + " · " + entry.Message };
                    item.AddToClassList("diagnostic-error");
                    diagnostics.Add(item);
                }
                status.text = "结构应用失败，查看下方诊断。";
                return;
            }
            EditorUtility.SetDirty(document);
            AssetDatabase.SaveAssets();
            status.text = "已应用社会结构 v" + document.Version + " 到世界 " + runner.World.WorldId +
                "；NPC/地区的位置和外观仍由场景控制。";
        }

        private void ValidateDocument()
        {
            if (diagnostics == null) return;
            diagnostics.Clear();
            if (document == null) { status.text = "选择社会结构图资产"; return; }
            var result = SocialStructureCompiler.Compile(document);
            if (document.Nodes != null)
                foreach (var node in document.Nodes)
                    if (node != null) graph.FindNodeView(node.Id)?.SetDiagnostic(false);
            foreach (var diagnostic in result.Diagnostics)
            {
                var captured = diagnostic;
                var button = new Button(() => FocusDiagnostic(captured.NodeId))
                    { text = diagnostic.Severity + " · " + diagnostic.Message };
                button.AddToClassList(diagnostic.Severity == GraphDiagnosticSeverity.Error
                    ? "diagnostic-error" : "diagnostic-warning");
                diagnostics.Add(button);
                graph.FindNodeView(diagnostic.NodeId)?.SetDiagnostic(diagnostic.Severity == GraphDiagnosticSeverity.Error);
            }
            if (result.Diagnostics.Count == 0) diagnostics.Add(new Label("校验通过，可将编译计划应用到 Core 世界。"));
            status.text = "社会结构 · v" + document.Version +
                (result.Success ? " · 已编译" : " · 校验未通过") +
                " · " + result.Diagnostics.Count + " 条诊断";
        }

        private void FocusDiagnostic(string id)
        {
            GraphElement view = graph.FindNodeView(id);
            if (view == null) view = graph.FindEdgeView(id);
            if (view == null) return;
            graph.ClearSelection(); graph.AddToSelection(view); graph.FrameSelection();
        }

        private void RefreshInspector()
        {
            if (inspector == null || graph == null) return;
            inspector.Clear();
            if (document == null) { inspector.Add(new Label("选择结构资产。")); return; }
            var selectedNode = graph.selection.OfType<StructureNodeView>().FirstOrDefault();
            if (selectedNode != null)
            {
                DrawNodeInspector(document.FindNode(selectedNode.Model.Id), selectedNode);
                return;
            }
            var selectedEdge = graph.selection.OfType<Edge>().FirstOrDefault();
            if (selectedEdge != null)
            {
                DrawEdgeInspector(document.FindEdge(selectedEdge.userData as string), selectedEdge);
                return;
            }
            inspector.Add(new Label("选中节点或连线以编辑。"));
        }

        private void DrawNodeInspector(StructureNode node, StructureNodeView view)
        {
            if (node == null) return;
            inspector.Add(new Label(LabelFor(node.Type) + " · " + node.Id.Substring(0, Math.Min(8, node.Id.Length))));
            AddText("实体 ID", node.EntityId, value => node.EntityId = value, view.RefreshSummary);
            AddText("显示名称", node.Label, value => node.Label = value, view.RefreshSummary);
            switch (node.Type)
            {
                case StructureNodeType.Agent:
                    AddText("NPC 模板 ID", node.TemplateId, value => node.TemplateId = value);
                    AddText("初始地点 ID", node.PlaceId, value => node.PlaceId = value);
                    break;
                case StructureNodeType.Collective:
                    AddToggle("正式组织", node.Formal, value => node.Formal = value);
                    break;
                case StructureNodeType.Organization:
                    AddText("所属集体 ID", node.LinkedCollectiveId, value => node.LinkedCollectiveId = value);
                    AddText("决策方式", node.Governance, value => node.Governance = value);
                    AddText("资产 ID（逗号分隔）", node.AssetIds, value => node.AssetIds = value);
                    break;
                case StructureNodeType.Role:
                    AddText("所属集体 ID", node.LinkedCollectiveId, value => node.LinkedCollectiveId = value);
                    break;
                case StructureNodeType.Place:
                    AddText("地点标签", node.Tag, value => node.Tag = value);
                    AddText("地区实例 ID", node.RegionInstanceId, value => node.RegionInstanceId = value);
                    AddInteger("容量", node.Capacity, value => node.Capacity = value);
                    break;
            }
        }

        private void DrawEdgeInspector(StructureEdge edge, Edge view)
        {
            if (edge == null) return;
            inspector.Add(new Label("连线 · " + edge.Id.Substring(0, Math.Min(8, edge.Id.Length))));
            var type = new EnumField("类型", edge.Type);
            type.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(document, "Change structure relation type");
                edge.Type = (StructureEdgeType)evt.newValue;
                Changed();
                SocialStructureGraphView.StyleEdge(view, edge.Id, edge.Type);
                ValidateDocument();
                RefreshInspector();
            });
            inspector.Add(type);
            switch (edge.Type)
            {
                case StructureEdgeType.Relation:
                    AddText("关系类型", edge.RelationType, value => edge.RelationType = value);
                    AddFloat("喜欢 (-1..1)", edge.Liking, value => edge.Liking = value);
                    AddFloat("信任 (-1..1)", edge.Trust, value => edge.Trust = value);
                    break;
                case StructureEdgeType.Authority:
                    AddText("授权动作 ID", edge.ActionId, value => edge.ActionId = value);
                    AddText("所属集体 ID", edge.CollectiveId, value => edge.CollectiveId = value);
                    AddText("地点范围 ID", edge.PlaceScopeId, value => edge.PlaceScopeId = value);
                    AddText("对象范围 ID", edge.ObjectScopeId, value => edge.ObjectScopeId = value);
                    AddText("父授权边 ID", edge.ParentEdgeId, value => edge.ParentEdgeId = value);
                    AddToggle("允许转授权", edge.CanDelegate, value => edge.CanDelegate = value);
                    break;
                case StructureEdgeType.Jurisdiction:
                    inspector.Add(new Label("应用时映射为地点的 OwnerCollectiveId。"));
                    break;
                case StructureEdgeType.Membership:
                    inspector.Add(new Label("Agent → Collective 表示成员；Agent → Role 表示角色指派。"));
                    break;
            }
        }

        private void AddText(string label, string current, Action<string> assign, Action after = null)
        {
            var field = new TextField(label) { value = current ?? "" };
            field.RegisterValueChangedCallback(evt =>
            {
                Undo.RecordObject(document, "Edit structure property");
                assign(evt.newValue); Changed(); after?.Invoke(); ValidateDocument();
            });
            inspector.Add(field);
        }

        private void AddToggle(string label, bool current, Action<bool> assign)
        {
            var field = new Toggle(label) { value = current };
            field.RegisterValueChangedCallback(evt =>
            { Undo.RecordObject(document, "Edit structure property"); assign(evt.newValue); Changed(); ValidateDocument(); });
            inspector.Add(field);
        }

        private void AddInteger(string label, int current, Action<int> assign)
        {
            var field = new IntegerField(label) { value = current };
            field.RegisterValueChangedCallback(evt =>
            { Undo.RecordObject(document, "Edit structure property"); assign(evt.newValue); Changed(); ValidateDocument(); });
            inspector.Add(field);
        }

        private void AddFloat(string label, float current, Action<float> assign)
        {
            var field = new FloatField(label) { value = current };
            field.RegisterValueChangedCallback(evt =>
            { Undo.RecordObject(document, "Edit structure property"); assign(evt.newValue); Changed(); ValidateDocument(); });
            inspector.Add(field);
        }

        private void PopulateContextMenu(ContextualMenuPopulateEvent evt)
        {
            if (document == null) return;
            Vector2 position = graph.contentViewContainer.WorldToLocal(graph.LocalToWorld(evt.localMousePosition));
            foreach (StructureNodeType type in Enum.GetValues(typeof(StructureNodeType)))
            {
                var captured = type;
                evt.menu.AppendAction("创建结构节点/" + LabelFor(type), _ => AddNode(captured, position));
            }
        }

        private string SerializeSelection(IEnumerable<GraphElement> elements)
        {
            var data = new ClipboardData();
            if (document == null) return JsonUtility.ToJson(data);
            var selected = new HashSet<string>(elements.OfType<StructureNodeView>().Select(node => node.Model.Id));
            foreach (var node in document.Nodes)
                if (node != null && selected.Contains(node.Id)) data.Nodes.Add(node.Clone(node.Id, node.Position));
            foreach (var edge in document.Edges)
                if (edge != null && selected.Contains(edge.FromNodeId) && selected.Contains(edge.ToNodeId))
                    data.Edges.Add(edge.Clone(edge.Id, edge.FromNodeId, edge.ToNodeId));
            return JsonUtility.ToJson(data);
        }

        private void PasteSelection(string operationName, string json)
        {
            if (document == null || string.IsNullOrEmpty(json)) return;
            ClipboardData data;
            try { data = JsonUtility.FromJson<ClipboardData>(json); }
            catch (Exception) { return; }
            if (data == null || data.Nodes == null || data.Nodes.Count == 0) return;
            Undo.RecordObject(document, operationName);
            var nodeMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var entityMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var edgeMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var added = new List<string>();
            foreach (var copied in data.Nodes)
            {
                if (copied == null || string.IsNullOrEmpty(copied.Id)) continue;
                string newId = Guid.NewGuid().ToString("N");
                var node = copied.Clone(newId, copied.Position + new Vector2(40, 40));
                string originalEntityId = copied.EntityId ?? "";
                string newEntityId;
                if (originalEntityId.Length == 0 || !entityMap.TryGetValue(originalEntityId, out newEntityId))
                {
                    string baseEntityId = originalEntityId.Length == 0
                        ? node.Type.ToString().ToLowerInvariant() : originalEntityId;
                    newEntityId = baseEntityId + ".copy";
                    int suffix = 2;
                    while (document.Nodes.Any(item => item != null && item.EntityId == newEntityId))
                        newEntityId = baseEntityId + ".copy" + suffix++;
                    if (originalEntityId.Length != 0) entityMap[originalEntityId] = newEntityId;
                }
                node.EntityId = newEntityId;
                nodeMap[copied.Id] = newId;
                document.Nodes.Add(node);
                added.Add(newId);
            }
            foreach (var id in added)
            {
                var node = document.FindNode(id);
                if (entityMap.TryGetValue(node.LinkedCollectiveId ?? "", out var linked)) node.LinkedCollectiveId = linked;
                if (entityMap.TryGetValue(node.PlaceId ?? "", out var place)) node.PlaceId = place;
            }
            if (data.Edges != null)
                foreach (var copied in data.Edges)
                {
                    if (copied == null || !nodeMap.TryGetValue(copied.FromNodeId ?? "", out var from) ||
                        !nodeMap.TryGetValue(copied.ToNodeId ?? "", out var to)) continue;
                    string newId = Guid.NewGuid().ToString("N");
                    var edge = copied.Clone(newId, from, to);
                    if (entityMap.TryGetValue(edge.CollectiveId ?? "", out var collective)) edge.CollectiveId = collective;
                    if (entityMap.TryGetValue(edge.PlaceScopeId ?? "", out var place)) edge.PlaceScopeId = place;
                    if (entityMap.TryGetValue(edge.ObjectScopeId ?? "", out var target)) edge.ObjectScopeId = target;
                    edgeMap[copied.Id] = newId;
                    document.Edges.Add(edge);
                }
            foreach (var edge in document.Edges)
                if (edgeMap.ContainsValue(edge.Id) && edgeMap.TryGetValue(edge.ParentEdgeId ?? "", out var parent))
                    edge.ParentEdgeId = parent;
            Changed();
            Open(document);
            graph.ClearSelection();
            foreach (var id in added)
            {
                var view = graph.FindNodeView(id);
                if (view != null) graph.AddToSelection(view);
            }
            ValidateDocument();
        }

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (document != null && DragAndDrop.GetGenericData("SocialStructureNodeType") is StructureNodeType)
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            if (document == null) return;
            var value = DragAndDrop.GetGenericData("SocialStructureNodeType");
            if (!(value is StructureNodeType)) return;
            DragAndDrop.AcceptDrag();
            Vector2 position = graph.contentViewContainer.WorldToLocal(graph.LocalToWorld(evt.localMousePosition));
            AddNode((StructureNodeType)value, position);
            evt.StopPropagation();
        }
    }
}
