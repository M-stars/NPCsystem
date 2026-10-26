using System;
using System.Collections.Generic;
using System.Linq;
using SocialSimulation.Authoring;
using SocialSimulation.Domains;
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
    public sealed partial class GraphWorkbenchWindow : EditorWindow
    {
        private const string LayoutPath = "Assets/SocialSimulation/Editor/Graph/GraphWorkbench.uxml";
        private const string StylePath = "Assets/SocialSimulation/Editor/Graph/GraphWorkbench.uss";
        [SerializeField] private GraphDocument document;
        private SocialGraphView graph;
        private VisualElement palette;
        private VisualElement inspector;
        private VisualElement blackboardToolbar;
        private ScrollView blackboardList;
        private ScrollView diagnostics;
        private Label status;
        private ObjectField documentField;
        private ObjectField bridgeField;
        private TextField searchField;
        private TextField actorField;
        private Label versionLabel;
        private Button pauseButton;
        private Button stepButton;
        private Button restartApplyButton;
        private RulePreviewPanel rulePreview;
        private readonly GraphPlanSlot appliedPlan = new GraphPlanSlot();
        private GraphDocument lastCompiledDocument;
        private int lastCompiledVersion;
        private string lastCompiledRevision;
        private double nextRuntimeRefresh;
        private bool rebuilding;
        private int spawnIndex;
        private string hostResolutionMessage;
        private readonly Stack<GraphDocument> navigation = new Stack<GraphDocument>();
        [SerializeField] private List<string> breakpoints = new List<string>();
        private Button backButton;

        [Serializable]
        private sealed class ClipboardData
        {
            public List<ClipboardNode> Nodes = new List<ClipboardNode>();
            public List<GraphEdge> Edges = new List<GraphEdge>();
            public List<GraphGroup> Groups = new List<GraphGroup>();
            public List<GraphNote> Notes = new List<GraphNote>();
        }

        [Serializable]
        private sealed class ClipboardNode
        {
            public string OldId;
            public GraphNodeType Type;
            public Vector2 Position;
            public List<GraphParameter> Parameters = new List<GraphParameter>();
            public string SubgraphGuid;
        }

        private sealed class PaletteBinding
        {
            public string Package;
            public string Id;
            public string CapabilityId;
            public GraphNodeType Type;
            public GraphCompatibility Compatibility;
        }

        [MenuItem("Tools/Social Simulation/Graph Workbench")]
        public static void ShowWindow()
        {
            var window = GetWindow<GraphWorkbenchWindow>();
            window.titleContent = new GUIContent("Social Graph");
            window.minSize = new Vector2(900, 600);
            window.Show();
        }

        [OnOpenAsset(1)]
        private static bool OpenGraphAsset(int instanceId, int line)
        {
            var selected = Selection.activeObject as GraphDocument;
            if (selected == null) return false;
            ShowWindow();
            GetWindow<GraphWorkbenchWindow>().Open(selected);
            return true;
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            Selection.selectionChanged += OnSelectionChanged;
            GraphTraceHub.Published += OnTrace;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.update += UpdateRuntimeUi;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Selection.selectionChanged -= OnSelectionChanged;
            GraphTraceHub.Published -= OnTrace;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.update -= UpdateRuntimeUi;
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (layout == null)
            {
                rootVisualElement.Add(new Label("缺少 GraphWorkbench.uxml"));
                return;
            }
            layout.CloneTree(rootVisualElement);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (sheet != null) rootVisualElement.styleSheets.Add(sheet);

            palette = rootVisualElement.Q<VisualElement>("palette");
            inspector = rootVisualElement.Q<VisualElement>("inspector");
            blackboardToolbar = rootVisualElement.Q<VisualElement>("blackboardToolbar");
            blackboardList = rootVisualElement.Q<ScrollView>("blackboardList");
            diagnostics = rootVisualElement.Q<ScrollView>("diagnostics");
            status = rootVisualElement.Q<Label>("status");
            var toolbar = rootVisualElement.Q<VisualElement>("toolbar");
            var host = rootVisualElement.Q<VisualElement>("graphHost");
            graph = new SocialGraphView();
            graph.graphViewChanged = OnGraphChanged;
            ConfigureLayoutCallbacks();
            graph.RegisterCallback<MouseUpEvent>(_ => graph.schedule.Execute(RefreshInspector));
            graph.RegisterCallback<KeyUpEvent>(_ => graph.schedule.Execute(RefreshInspector));
            graph.serializeGraphElements = SerializeSelection;
            graph.unserializeAndPaste = PasteSelection;
            graph.RegisterCallback<ContextualMenuPopulateEvent>(PopulateContextMenu);
            graph.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            graph.RegisterCallback<DragPerformEvent>(OnDragPerform);
            host.Add(graph);

            var newBehavior = new Button(() => CreateDocument(GraphKind.Behavior)) { text = "新建行为图" };
            var newRule = new Button(() => CreateDocument(GraphKind.Rule)) { text = "新建规则图" };
            toolbar.Add(newBehavior);
            toolbar.Add(newRule);
            documentField = new ObjectField("图资产") { objectType = typeof(GraphDocument), value = document };
            documentField.AddToClassList("document-field");
            documentField.RegisterValueChangedCallback(evt => Open(evt.newValue as GraphDocument));
            toolbar.Add(documentField);
            toolbar.Add(new Button(Save) { text = "保存" });
            toolbar.Add(new Button(ValidateDocument) { text = "校验" });
            toolbar.Add(new Button(() => graph.FrameAll()) { text = "适配画布" });
            backButton = new Button(NavigateBack) { text = "返回父图", name = "backToParentGraph" };
            toolbar.Add(backButton);
            toolbar.Add(new Button(SocialStructureWorkbenchWindow.ShowWindow) { text = "社会结构" });
            actorField = new TextField("观察 NPC ID") { name = "observedActorId" };
            actorField.tooltip = "只高亮此 NPC 的运行轨迹。空值时不显示任何实例。";
            actorField.AddToClassList("actor-field");
            toolbar.Add(actorField);

            var runtimeToolbar = rootVisualElement.Q<VisualElement>("runtimeToolbar");
            bridgeField = new ObjectField("图宿主") { objectType = typeof(MonoBehaviour), allowSceneObjects = true };
            bridgeField.style.minWidth = 190;
            bridgeField.tooltip = "按当前图 ID 自动匹配 ISocialGraphHost；多个模块拥有同一图时请显式选择。兼容旧单图宿主。";
            bridgeField.RegisterValueChangedCallback(_ => { nextRuntimeRefresh = 0; UpdateRuntimeUi(); });
            runtimeToolbar.Add(bridgeField);
            runtimeToolbar.Add(new Button(() => ApplyDocument(GraphApplyMode.ForNewInstances))
                { text = "应用" });
            restartApplyButton = new Button(() => ApplyDocument(GraphApplyMode.RestartAffected))
                { text = "重启实例应用", tooltip = "需先暂停世界；取消受影响行动并以新图重建实例。" };
            runtimeToolbar.Add(restartApplyButton);
            runtimeToolbar.Add(new Button(PlayWorld) { text = "播放" });
            pauseButton = new Button(PauseWorld) { text = "暂停" };
            runtimeToolbar.Add(pauseButton);
            stepButton = new Button(StepWorld) { text = "单步" };
            runtimeToolbar.Add(stepButton);
            versionLabel = new Label { name = "runtimeVersions" };
            versionLabel.AddToClassList("runtime-versions");
            runtimeToolbar.Add(versionLabel);

            searchField = new TextField("搜索节点");
            searchField.RegisterValueChangedCallback(_ => RefreshPalette());
            rootVisualElement.Q<VisualElement>("paletteHeader").Add(searchField);
            CreateTemplateControls(rootVisualElement.Q<VisualElement>("paletteHeader"));
            blackboardToolbar.Add(new Label("黑板变量"));
            blackboardToolbar.Add(new Button(AddVariable) { text = "添加" });
            rulePreview = new RulePreviewPanel(rootVisualElement, FocusDiagnostic,
                entry => graph.FindNodeView(entry.NodeId)?.SetTrace(entry.Status),
                () => graph.ClearTrace());
            Open(document);
            UpdateRuntimeUi();
        }

        private static SocialWorldRunner RunningWorld()
        {
            return EditorApplication.isPlaying
                ? UnityEngine.Object.FindAnyObjectByType<SocialWorldRunner>() : null;
        }

        private MonoBehaviour RunningHost()
        {
            hostResolutionMessage = "";
            if (!EditorApplication.isPlaying || document == null) return null;
            var selected = bridgeField?.value as MonoBehaviour;
            if (selected != null)
            {
                if (TryGetRuntimeState(selected, out _)) return selected;
                hostResolutionMessage = "所选模块未注册当前图 ID。";
                return null;
            }
            var candidates = UnityEngine.Object.FindObjectsByType<MonoBehaviour>()
                .Where(component => TryGetRuntimeState(component, out _)).ToList();
            if (candidates.Count != 1)
            {
                hostResolutionMessage = candidates.Count == 0 ? "场景中没有注册此图 ID 的运行宿主。" :
                    "此图属于 " + candidates.Count + " 个宿主，请选择要应用的模块。";
                return null;
            }
            return candidates[0];
        }

        private bool TryGetRuntimeState(MonoBehaviour component, out GraphRuntimeState state)
        {
            state = null;
            if (document == null || component == null || !component.isActiveAndEnabled) return false;
            if (component is ISocialGraphHost host)
                return host.TryGetGraphState(document.DocumentId, document.Kind, out state) && state != null;
            if (!(component is IGraphWorkbenchRuntimeBridge bridge)) return false;
            if (document.Kind == GraphKind.Behavior && bridge.CurrentBehaviorGraphId == document.DocumentId)
                state = new GraphRuntimeState(bridge.CurrentBehaviorGraphId, GraphKind.Behavior,
                    bridge.CurrentBehaviorGraphVersion, bridge.CurrentBehaviorGraphRevision, bridge.ActiveBehaviorGraphVersions);
            else if (document.Kind == GraphKind.Rule && bridge.CurrentRuleGraphId == document.DocumentId)
                state = new GraphRuntimeState(bridge.CurrentRuleGraphId, GraphKind.Rule,
                    bridge.CurrentRuleGraphVersion, bridge.CurrentRuleGraphRevision);
            return state != null;
        }

        private void PlayWorld()
        {
            if (!EditorApplication.isPlaying) { EditorApplication.isPlaying = true; return; }
            var runner = RunningWorld();
            if (runner != null) runner.SetPaused(false);
            if (status != null) status.text = runner == null ? "当前场景没有 SocialWorldRunner。" : "世界正在播放。";
            nextRuntimeRefresh = 0;
            UpdateRuntimeUi();
        }

        private void PauseWorld()
        {
            var runner = RunningWorld();
            if (runner == null || runner.World == null) return;
            runner.SetPaused(true);
            if (status != null) status.text = "世界已暂停。";
            nextRuntimeRefresh = 0;
            UpdateRuntimeUi();
        }

        private void StepWorld()
        {
            var runner = RunningWorld();
            if (runner == null || runner.World == null) return;
            runner.SetPaused(true);
            runner.World.StepOne();
            if (status != null) status.text = "已执行 1 个逻辑 Tick（#" + runner.World.TickIndex + "）。";
            nextRuntimeRefresh = 0;
            UpdateRuntimeUi();
        }

        private void ApplyDocument(GraphApplyMode mode)
        {
            if (document == null) { if (status != null) status.text = "请先选择图资产。"; return; }
            var result = GraphCompiler.Compile(document);
            ValidateDocument();
            if (!result.Success)
            {
                if (status != null) status.text = "应用失败：草稿含编译错误，当前有效计划保留。";
                nextRuntimeRefresh = 0;
                UpdateRuntimeUi();
                return;
            }

            var templateDiagnostics = TemplateDiagnostics(result);
            if (templateDiagnostics.Any(item => item.Severity == GraphDiagnosticSeverity.Error))
            {
                if (status != null) status.text = "应用失败：所选 NPC 模板兼容性未通过，当前有效计划保留。";
                return;
            }

            EditorUtility.SetDirty(document);
            AssetDatabase.SaveAssets();
            var host = RunningHost();
            if (host == null)
            {
                if (status != null) status.text = EditorApplication.isPlaying
                    ? "已编译并保存；" + hostResolutionMessage
                    : "已编译并保存；进入 Play 后选择图宿主应用，运行计划未更改。";
                nextRuntimeRefresh = 0;
                UpdateRuntimeUi();
                return;
            }
            string error;
            bool success;
            if (host is ISocialGraphHost graphHost)
                success = graphHost.TryApplyGraph(document, mode, out error);
            else
            {
                var bridge = (IGraphWorkbenchRuntimeBridge)host;
                success = document.Kind == GraphKind.Behavior
                    ? bridge.TryApplyBehaviorGraph(document, mode, out error)
                    : bridge.TryApplyRuleGraph(document, out error);
            }
            if (success) appliedPlan.TryApply(document);
            if (status != null)
                status.text = success ? "已应用到运行世界：" + document.Kind + " v" + document.Version
                    : "运行世界未应用：" + error;
            nextRuntimeRefresh = 0;
            UpdateRuntimeUi();
        }

        private void UpdateRuntimeUi()
        {
            if (versionLabel == null) return;
            if (EditorApplication.timeSinceStartup < nextRuntimeRefresh) return;
            nextRuntimeRefresh = EditorApplication.timeSinceStartup + 0.25;
            var runner = RunningWorld();
            var host = RunningHost();
            bool hasWorld = runner != null && runner.World != null;
            pauseButton?.SetEnabled(hasWorld && !runner.World.Paused);
            stepButton?.SetEnabled(hasWorld);
            restartApplyButton?.SetEnabled(document != null && document.Kind == GraphKind.Behavior &&
                hasWorld && runner.World.Paused && host != null);
            if (document == null) { versionLabel.text = "草稿 — · 编译 — · 应用 — · 运行 —"; return; }

            int localVersion = document.Kind == GraphKind.Behavior && appliedPlan.BehaviorPlan != null &&
                appliedPlan.BehaviorPlan.DocumentId == document.DocumentId
                    ? appliedPlan.BehaviorPlan.Version
                    : document.Kind == GraphKind.Rule && appliedPlan.RulePlan != null &&
                      appliedPlan.RulePlan.DocumentId == document.DocumentId
                        ? appliedPlan.RulePlan.Version : 0;
            string compiled = lastCompiledDocument == document && lastCompiledVersion == document.Version
                ? "v" + lastCompiledVersion + ShortRevision(lastCompiledRevision) : "—";
            string running = "—";
            if (host != null && TryGetRuntimeState(host, out var runtime))
            {
                running = host.GetType().Name + " v" + runtime.Version + ShortRevision(runtime.Revision);
                if (runtime.ActiveVersions != null && runtime.ActiveVersions.Count > 0)
                    running += " · 活动实例 " + string.Join(", ", runtime.ActiveVersions);
            }
            else if (!string.IsNullOrEmpty(hostResolutionMessage)) running = hostResolutionMessage;
            versionLabel.text = "草稿 v" + document.Version + " · 编译 " + compiled +
                " · 本地应用 " + (localVersion == 0 ? "—" : "v" + localVersion) + " · 场景运行 " + running;
        }

        private static string ShortRevision(string revision)
        { return string.IsNullOrEmpty(revision) ? "" : " @" + revision.Substring(0, Math.Min(8, revision.Length)); }

        public void Open(GraphDocument asset)
        {
            document = asset;
            if (documentField != null) documentField.SetValueWithoutNotify(asset);
            if (graph == null) return;
            rebuilding = true;
            graph.Load(asset);
            rebuilding = false;
            rulePreview?.SetDocument(asset);
            backButton?.SetEnabled(navigation.Count > 0);
            rootVisualElement.Q<VisualElement>("blackboardPanel")?.EnableInClassList("hidden",
                asset != null && asset.Kind == GraphKind.Rule);
            RefreshPalette();
            RefreshBlackboard();
            RefreshInspector();
            ValidateDocument();
            nextRuntimeRefresh = 0;
            UpdateRuntimeUi();
        }

        public void PreviewRule() { rulePreview?.RunPreview(); }

        private void EnterSubgraph(GraphDocument child)
        {
            if (document == null || child == null || child == document) return;
            navigation.Push(document);
            Open(child);
        }

        private void NavigateBack()
        {
            while (navigation.Count > 0)
            {
                var parent = navigation.Pop();
                if (parent != null) { Open(parent); return; }
            }
            backButton?.SetEnabled(false);
        }

        private void OnSelectionChanged()
        {
            if (Selection.activeObject is GraphDocument && Selection.activeObject != document)
                Open(Selection.activeObject as GraphDocument);
        }

        private void OnUndoRedo()
        {
            if (document == null) return;
            Open(document);
        }

        private void OnProjectChanged()
        {
            if (document == null || graph == null) return;
            RefreshPalette();
            ValidateDocument();
        }

        private void CreateDocument(GraphKind kind)
        {
            string path = EditorUtility.SaveFilePanelInProject("创建图资产", kind == GraphKind.Behavior ? "BehaviorGraph" : "RuleGraph",
                "asset", "选择图资产保存路径");
            if (string.IsNullOrEmpty(path)) return;
            var asset = CreateInstance<GraphDocument>();
            asset.Kind = kind;
            asset.DocumentId = Guid.NewGuid().ToString("N");
            if (kind == GraphKind.Behavior)
                asset.Nodes.Add(NewNode(GraphNodeType.Root, new Vector2(80, 100)));
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            Open(asset);
        }

        private static GraphNode NewNode(GraphNodeType type, Vector2 position)
        {
            var node = new GraphNode { Id = Guid.NewGuid().ToString("N"), Type = type, Position = position };
            foreach (var parameter in NodeDescriptor.Get(type).Parameters)
                node.Parameters.Add(new GraphParameter(parameter.Key, parameter.DefaultValue));
            return node;
        }

        private void AddNode(GraphNodeType type, Vector2 position, string bindingId = null)
        {
            if (document == null || NodeDescriptor.Get(type).Kind != document.Kind) return;
            if (!CanAddBinding(type, bindingId)) return;
            if (type == GraphNodeType.Root && document.Nodes.Any(node => node.Type == GraphNodeType.Root)) return;
            Undo.RecordObject(document, "Add graph node");
            var node = NewNode(type, position);
            if (!string.IsNullOrEmpty(bindingId))
                node.Set(type == GraphNodeType.Action ? "actionId" : "conditionId", bindingId);
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
            if (document == null)
            {
                palette.Add(new Label("创建或选择一张图资产。"));
                return;
            }
            string filter = searchField == null ? "" : (searchField.value ?? "").Trim();
            foreach (var descriptor in NodeDescriptor.For(document.Kind))
            {
                if (filter.Length > 0 && descriptor.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    descriptor.Category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    descriptor.Type.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var captured = descriptor;
                var item = new Button(() => AddNode(captured.Type, NextSpawnPosition()))
                    { text = captured.Category + "  /  " + captured.Title };
                item.AddToClassList("palette-item");
                item.tooltip = "点击添加，或拖到画布指定位置。";
                Vector2 pressPosition = Vector2.zero;
                bool pressed = false;
                item.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    pressed = true;
                    pressPosition = evt.mousePosition;
                });
                item.RegisterCallback<MouseMoveEvent>(evt =>
                {
                    if (!pressed || (evt.mousePosition - pressPosition).sqrMagnitude < 64) return;
                    pressed = false;
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.SetGenericData("SocialGraphNodeType", captured.Type);
                    DragAndDrop.SetGenericData("SocialGraphBindingId", null);
                    DragAndDrop.StartDrag(captured.Title);
                    evt.StopPropagation();
                });
                item.RegisterCallback<MouseUpEvent>(_ => pressed = false);
                palette.Add(item);
            }
            if (document.Kind == GraphKind.Behavior)
                AddPackageBindings(filter);
        }

        private void AddPackageBindings(string filter)
        {
            var bindings = RegisteredPaletteBindings();
            bindings.Sort((a, b) =>
            {
                int type = a.Type.CompareTo(b.Type);
                if (type != 0) return type;
                int package = string.CompareOrdinal(a.Package, b.Package);
                return package != 0 ? package : string.CompareOrdinal(a.Id, b.Id);
            });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in bindings)
            {
                string key = binding.Type + "|" + binding.Package + "|" + binding.Id;
                if (!seen.Add(key)) continue;
                if (filter.Length > 0 && (binding.Id ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (binding.Package ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var captured = binding;
                bool compatible = (selectedTemplate == null || templateManifest != null) &&
                    (captured.Compatibility == null || captured.Compatibility.CanUse);
                if (compatibleOnly && (selectedTemplate == null || !compatible)) continue;
                var button = new Button(() => AddNode(captured.Type, NextSpawnPosition(), captured.Id))
                    { text = (captured.Type == GraphNodeType.Action ? "行动" : "能力条件") + " · " +
                        captured.Package + " / " + captured.Id };
                button.AddToClassList("palette-item");
                button.tooltip = captured.Type == GraphNodeType.Action
                    ? "来源：" + captured.Package +
                      (string.IsNullOrEmpty(captured.CapabilityId) ? "" : "，需要能力 " + captured.CapabilityId) +
                      "。运行宿主应用时仍会校验实际执行器。"
                    : "NPC 包声明的能力；行为宿主须绑定此条件判断。";
                var definition = actionCatalog.ResolveAction(captured.Id);
                if (definition != null)
                    button.tooltip += " 目标：" + (definition.TargetKind ?? "未限定") +
                        "；资源：" + (definition.ResourceKind ?? "未限定") + "；执行器：" + definition.ExecutorId;
                if (captured.Compatibility != null) button.tooltip += " " + captured.Compatibility.Reason;
                button.SetEnabled(compatible);
                Vector2 press = Vector2.zero;
                bool pressed = false;
                button.RegisterCallback<MouseDownEvent>(evt =>
                { if (evt.button == 0) { pressed = true; press = evt.mousePosition; } });
                button.RegisterCallback<MouseMoveEvent>(evt =>
                {
                    if (!compatible || !pressed || (evt.mousePosition - press).sqrMagnitude < 64) return;
                    pressed = false;
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.SetGenericData("SocialGraphNodeType", captured.Type);
                    DragAndDrop.SetGenericData("SocialGraphBindingId", captured.Id);
                    DragAndDrop.StartDrag(captured.Id);
                    evt.StopPropagation();
                });
                button.RegisterCallback<MouseUpEvent>(_ => pressed = false);
                palette.Add(button);
                if (captured.Compatibility != null)
                {
                    var reason = new Label(captured.Compatibility.Reason);
                    reason.AddToClassList("palette-compatibility");
                    if (!compatible) reason.AddToClassList("diagnostic-warning");
                    palette.Add(reason);
                }
            }
        }

        private Vector2 NextSpawnPosition()
        {
            int i = spawnIndex++;
            return new Vector2(80 + (i % 4) * 260, 100 + (i / 4) * 180);
        }

        private GraphViewChange OnGraphChanged(GraphViewChange change)
        {
            if (rebuilding || document == null) return change;
            bool modified = false;
            bool recorded = false;
            Action record = () =>
            {
                if (recorded) return;
                Undo.RecordObject(document, "Edit social graph");
                recorded = true;
            };

            if (change.elementsToRemove != null)
            {
                foreach (var element in change.elementsToRemove)
                {
                    var node = element as SocialNodeView;
                    if (node != null)
                    {
                        record();
                        document.Nodes.RemoveAll(item => item != null && item.Id == node.Model.Id);
                        document.Edges.RemoveAll(edge => edge != null &&
                            (edge.FromNodeId == node.Model.Id || edge.ToNodeId == node.Model.Id));
                        RemoveLayoutMembership(node.Model.Id);
                        modified = true;
                        continue;
                    }
                    if (RemoveLayoutElement(element, record)) continue;
                    var edgeView = element as Edge;
                    if (edgeView == null) continue;
                    var source = edgeView.output != null ? edgeView.output.node as SocialNodeView : null;
                    var target = edgeView.input != null ? edgeView.input.node as SocialNodeView : null;
                    if (source == null || target == null) continue;
                    record();
                    modified |= document.Edges.RemoveAll(edge => edge != null &&
                        edge.FromNodeId == source.Model.Id && edge.ToNodeId == target.Model.Id) > 0;
                }
            }
            if (change.edgesToCreate != null)
            {
                foreach (var edgeView in change.edgesToCreate.ToList())
                {
                    var source = edgeView.output != null ? edgeView.output.node as SocialNodeView : null;
                    var target = edgeView.input != null ? edgeView.input.node as SocialNodeView : null;
                    if (source == null || target == null ||
                        !GraphCompiler.CanConnect(source.Model.Type, target.Model.Type, document.Kind) ||
                        document.Edges.Any(edge => edge.ToNodeId == target.Model.Id ||
                            (edge.FromNodeId == source.Model.Id && edge.ToNodeId == target.Model.Id)))
                    {
                        change.edgesToCreate.Remove(edgeView);
                        continue;
                    }
                    record();
                    int order = document.Edges.Where(edge => edge.FromNodeId == source.Model.Id)
                        .Select(edge => edge.Order).DefaultIfEmpty(-1).Max() + 1;
                    document.Edges.Add(new GraphEdge { FromNodeId = source.Model.Id,
                        ToNodeId = target.Model.Id, Order = order });
                    modified = true;
                }
            }
            if (change.movedElements != null)
                SaveMovedLayout(change.movedElements, record);
            if (modified) Changed();
            return change;
        }

        private void Changed()
        {
            document.IncrementVersion();
            lastCompiledDocument = null;
            lastCompiledRevision = null;
            graph.ClearTrace();
            rulePreview?.Invalidate();
            EditorUtility.SetDirty(document);
            if (status != null) status.text = document.Kind + " · 草稿 v" + document.Version + " · 尚未保存";
            nextRuntimeRefresh = 0;
            UpdateRuntimeUi();
        }

        private void Save()
        {
            if (document == null) return;
            EditorUtility.SetDirty(document);
            AssetDatabase.SaveAssets();
            ValidateDocument();
        }

        private void ValidateDocument()
        {
            if (diagnostics == null) return;
            diagnostics.Clear();
            if (document == null)
            {
                if (status != null) status.text = "选择一张行为图或规则图";
                return;
            }
            var result = GraphCompiler.Compile(document);
            if (result.Success)
            {
                lastCompiledDocument = document;
                lastCompiledVersion = document.Version;
                lastCompiledRevision = document.Kind == GraphKind.Behavior
                    ? result.BehaviorPlan.Revision : result.RulePlan.Revision;
            }
            else
            {
                lastCompiledDocument = null;
                lastCompiledRevision = null;
            }
            if (document.Nodes != null)
                foreach (var node in document.Nodes)
                {
                    if (node == null) continue;
                    var view = graph.FindNodeView(node.Id);
                    if (view != null) view.SetDiagnostic(false);
                }
            var allDiagnostics = new List<GraphDiagnostic>(result.Diagnostics);
            allDiagnostics.AddRange(TemplateDiagnostics(result));
            foreach (var entry in allDiagnostics)
            {
                var captured = entry;
                var button = new Button(() => FocusDiagnostic(captured.NodeId))
                    { text = entry.Severity + " · " + entry.Message };
                button.AddToClassList(entry.Severity == GraphDiagnosticSeverity.Error ? "diagnostic-error" : "diagnostic-warning");
                diagnostics.Add(button);
                var view = graph.FindNodeView(entry.NodeId);
                if (view != null && entry.Severity == GraphDiagnosticSeverity.Error) view.SetDiagnostic(true);
            }
            if (allDiagnostics.Count == 0) diagnostics.Add(new Label("校验通过。"));
            if (status != null)
                status.text = document.Kind + " · v" + document.Version +
                    (result.Success && !allDiagnostics.Any(item => item.Severity == GraphDiagnosticSeverity.Error)
                        ? " · 已编译" : " · 校验未通过") +
                    " · " + allDiagnostics.Count + " 条诊断";
            nextRuntimeRefresh = 0;
            UpdateRuntimeUi();
        }

        private void FocusDiagnostic(string nodeId)
        {
            var view = graph.FindNodeView(nodeId);
            if (view == null) return;
            graph.ClearSelection();
            graph.AddToSelection(view);
            graph.FrameSelection();
        }

        private void AddVariable()
        {
            if (document == null || document.Variables == null) return;
            Undo.RecordObject(document, "Add blackboard variable");
            string name = "variable";
            int suffix = 2;
            while (document.Variables.Any(item => item != null &&
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = "variable" + suffix++;
            document.Variables.Add(new BlackboardVariable
            {
                Id = Guid.NewGuid().ToString("N"), Name = name,
                Type = BlackboardValueType.Boolean, DefaultValue = "false",
                Scope = BlackboardScope.Agent, Access = BlackboardAccess.ReadWrite,
                SavePolicy = BlackboardSavePolicy.Checkpoint
            });
            Changed();
            RefreshBlackboard();
            RefreshInspector();
            ValidateDocument();
        }

        private void EditVariable(BlackboardVariable variable, Action edit)
        {
            if (document == null || variable == null || !document.Variables.Contains(variable)) return;
            Undo.RecordObject(document, "Edit blackboard variable");
            edit();
            Changed();
            RefreshBlackboard();
            RefreshInspector();
            ValidateDocument();
        }

        private void RemoveVariable(BlackboardVariable variable)
        {
            if (document == null || variable == null) return;
            int count = document.Nodes.Count(node => node != null &&
                (node.Type == GraphNodeType.BlackboardSet || node.Type == GraphNodeType.BlackboardCheck) &&
                node.Get("variableId") == variable.Id);
            if (count > 0 && !EditorUtility.DisplayDialog("删除黑板变量",
                "有 " + count + " 个节点引用此变量。删除后草稿将无法编译，直到重新绑定节点。", "删除", "取消"))
                return;
            Undo.RecordObject(document, "Remove blackboard variable");
            document.Variables.Remove(variable);
            Changed();
            RefreshBlackboard();
            RefreshInspector();
            ValidateDocument();
        }

        private void RefreshBlackboard()
        {
            if (blackboardList == null) return;
            blackboardList.Clear();
            if (document == null || document.Variables == null)
            {
                blackboardList.Add(new Label("创建或选择一张图资产。"));
                return;
            }
            if (document.Variables.Count == 0)
                blackboardList.Add(new Label("此图没有黑板变量。点击“添加”定义变量。"));
            foreach (var variable in document.Variables)
            {
                if (variable == null) continue;
                var card = new VisualElement();
                card.AddToClassList("blackboard-card");
                var heading = new VisualElement();
                heading.style.flexDirection = FlexDirection.Row;
                var id = new Label("ID " + variable.Id) { tooltip = "稳定变量 ID：重命名时引用不会改变。" };
                id.style.flexGrow = 1;
                heading.Add(id);
                heading.Add(new Button(() => RemoveVariable(variable)) { text = "删除" });
                card.Add(heading);
                var name = new TextField("名称") { value = variable.Name ?? "" };
                name.RegisterValueChangedCallback(evt => EditVariable(variable, () => variable.Name = evt.newValue));
                card.Add(name);
                var type = new EnumField("类型", variable.Type);
                type.RegisterValueChangedCallback(evt => EditVariable(variable, () =>
                {
                    variable.Type = (BlackboardValueType)evt.newValue;
                    if (!BlackboardValueCodec.TryNormalize(variable.Type, variable.DefaultValue, out _))
                        variable.DefaultValue = variable.Type == BlackboardValueType.Boolean ? "false" :
                            variable.Type == BlackboardValueType.Integer || variable.Type == BlackboardValueType.Float
                                ? "0" : variable.Type == BlackboardValueType.EntityId ? "entity.id" : "";
                }));
                card.Add(type);
                var defaultValue = new TextField("默认值") { value = variable.DefaultValue ?? "" };
                defaultValue.RegisterValueChangedCallback(evt => EditVariable(variable,
                    () => variable.DefaultValue = evt.newValue));
                card.Add(defaultValue);
                var scope = new EnumField("作用域", variable.Scope);
                scope.RegisterValueChangedCallback(evt => EditVariable(variable,
                    () => variable.Scope = (BlackboardScope)evt.newValue));
                card.Add(scope);
                var access = new EnumField("权限", variable.Access);
                access.RegisterValueChangedCallback(evt => EditVariable(variable,
                    () => variable.Access = (BlackboardAccess)evt.newValue));
                card.Add(access);
                var save = new EnumField("保存策略", variable.SavePolicy);
                save.RegisterValueChangedCallback(evt => EditVariable(variable,
                    () => variable.SavePolicy = (BlackboardSavePolicy)evt.newValue));
                card.Add(save);
                blackboardList.Add(card);
            }
        }

        private void RefreshInspector()
        {
            if (inspector == null || graph == null) return;
            inspector.Clear();
            if (RefreshLayoutInspector()) return;
            var selected = graph.selection.OfType<SocialNodeView>().FirstOrDefault();
            if (document == null || selected == null)
            {
                inspector.Add(new Label("选中节点以编辑参数。"));
                return;
            }
            var node = document.FindNode(selected.Model.Id);
            if (node == null) return;
            if (!Enum.IsDefined(typeof(GraphNodeType), node.Type))
            {
                inspector.Add(new Label("未知节点类型 " + (int)node.Type + " · " + node.Id));
                inspector.Add(new Label("原始参数与连线已保留。安装对应节点支持后重新打开。"));
                if (node.Parameters != null)
                    foreach (var parameter in node.Parameters)
                        if (parameter != null) inspector.Add(new Label(parameter.Key + " = " + parameter.Value));
                return;
            }
            var descriptor = NodeDescriptor.Get(node.Type);
            inspector.Add(new Label(descriptor.Title + " · " + node.Id.Substring(0, Math.Min(8, node.Id.Length))));
            inspector.Add(new Label("类型 ID：" + node.Type));
            string breakpointKey = document.DocumentId + "/" + node.Id;
            var breakpoint = new Toggle("命中后暂停世界") { value = breakpoints.Contains(breakpointKey),
                tooltip = "对顶部观察 NPC ID 生效；在当前 Tick 完成后暂停。试运行不会触发断点。" };
            breakpoint.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue) { if (!breakpoints.Contains(breakpointKey)) breakpoints.Add(breakpointKey); }
                else breakpoints.Remove(breakpointKey);
            });
            inspector.Add(breakpoint);
            foreach (var parameter in descriptor.Parameters)
            {
                var captured = parameter;
                if (node.Type == GraphNodeType.RuleCondition && (parameter.Key == "valueType" || parameter.Key == "comparison"))
                {
                    var choices = parameter.Key == "valueType" ? new List<string> { "string", "number", "bool" } :
                        new List<string> { "equals", "notEqual", "greater", "greaterEqual", "less", "lessEqual" };
                    string current = node.Get(parameter.Key, parameter.DefaultValue);
                    if (!choices.Contains(current)) choices.Add(current);
                    var choice = new PopupField<string>(parameter.Label, choices, current)
                        { name = "rule_" + parameter.Key };
                    choice.formatListItemCallback = RuleChoiceLabel;
                    choice.formatSelectedValueCallback = RuleChoiceLabel;
                    choice.RegisterValueChangedCallback(evt =>
                    {
                        Undo.RecordObject(document, "Edit rule value schema");
                        node.Set(captured.Key, evt.newValue);
                        Changed(); selected.RefreshSummary(); ValidateDocument();
                    });
                    inspector.Add(choice);
                    continue;
                }
                if (parameter.Key == "variableId")
                {
                    var choices = document.Variables.Where(item => item != null && !string.IsNullOrEmpty(item.Id))
                        .Select(item => item.Id).Distinct(StringComparer.Ordinal).ToList();
                    string current = node.Get("variableId");
                    if (!string.IsNullOrEmpty(current) && !choices.Contains(current)) choices.Add(current);
                    if (choices.Count == 0)
                    {
                        inspector.Add(new Label("请先在下方黑板中添加变量。"));
                        continue;
                    }
                    choices.Insert(0, "");
                    var variableField = new PopupField<string>(parameter.Label, choices, current);
                    Func<string, string> format = id =>
                    {
                        if (string.IsNullOrEmpty(id)) return "请选择变量";
                        var variable = document.Variables.FirstOrDefault(item => item != null && item.Id == id);
                        return variable == null ? "缺失：" + id : variable.Name + " · " + variable.Type;
                    };
                    variableField.formatListItemCallback = format;
                    variableField.formatSelectedValueCallback = format;
                    variableField.RegisterValueChangedCallback(evt =>
                    {
                        Undo.RecordObject(document, "Bind blackboard variable");
                        node.Set("variableId", evt.newValue);
                        Changed();
                        selected.RefreshSummary();
                        ValidateDocument();
                    });
                    inspector.Add(variableField);
                    continue;
                }
                var field = new TextField(parameter.Label) { value = node.Get(parameter.Key, parameter.DefaultValue) };
                field.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(document, "Edit graph parameter");
                    node.Set(captured.Key, evt.newValue);
                    Changed();
                    selected.RefreshSummary();
                    ValidateDocument();
                });
                inspector.Add(field);
            }
            if (node.Type == GraphNodeType.Subtree)
            {
                var field = new ObjectField("子行为图") { objectType = typeof(GraphDocument), value = node.Subgraph };
                field.RegisterValueChangedCallback(evt =>
                {
                    Undo.RecordObject(document, "Assign graph subtree");
                    node.Subgraph = evt.newValue as GraphDocument;
                    Changed();
                    selected.RefreshSummary();
                    ValidateDocument();
                    RefreshInspector();
                });
                inspector.Add(field);
                var enter = new Button(() => EnterSubgraph(node.Subgraph)) { text = "进入子图", name = "enterSubgraph" };
                enter.SetEnabled(node.Subgraph != null && node.Subgraph != document);
                inspector.Add(enter);
            }
            var inbound = document.Edges.FirstOrDefault(edge => edge.ToNodeId == node.Id);
            if (inbound != null)
            {
                var siblings = document.Edges.Where(edge => edge.FromNodeId == inbound.FromNodeId)
                    .OrderBy(edge => edge.Order).ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal).ToList();
                if (siblings.Count > 1)
                {
                    inspector.Add(new Label("执行顺序：" + (siblings.IndexOf(inbound) + 1) + " / " + siblings.Count));
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.Add(new Button(() => MoveSibling(node.Id, -1)) { text = "上移" });
                    row.Add(new Button(() => MoveSibling(node.Id, 1)) { text = "下移" });
                    inspector.Add(row);
                }
            }
        }

        private static string RuleChoiceLabel(string value)
        {
            switch (value)
            {
                case "string": return "文本 (string)";
                case "number": return "数值 (number)";
                case "bool": return "布尔 (bool)";
                case "equals": return "相等 (=)";
                case "notEqual": return "不等 (≠)";
                case "greater": return "大于 (>)";
                case "greaterEqual": return "大于等于 (≥)";
                case "less": return "小于 (<)";
                case "lessEqual": return "小于等于 (≤)";
                default: return "无效：" + value;
            }
        }

        private void MoveSibling(string nodeId, int direction)
        {
            var inbound = document.Edges.FirstOrDefault(edge => edge.ToNodeId == nodeId);
            if (inbound == null) return;
            var siblings = document.Edges.Where(edge => edge.FromNodeId == inbound.FromNodeId)
                .OrderBy(edge => edge.Order).ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal).ToList();
            int index = siblings.IndexOf(inbound);
            int swap = index + direction;
            if (swap < 0 || swap >= siblings.Count) return;
            Undo.RecordObject(document, "Reorder graph children");
            var temporary = siblings[index];
            siblings[index] = siblings[swap];
            siblings[swap] = temporary;
            for (int i = 0; i < siblings.Count; i++) siblings[i].Order = i;
            Changed();
            RefreshInspector();
        }

        private void PopulateContextMenu(ContextualMenuPopulateEvent evt)
        {
            if (document == null) return;
            Vector2 position = graph.contentViewContainer.WorldToLocal(graph.LocalToWorld(evt.localMousePosition));
            evt.menu.AppendAction("布局/将所选节点与注释分组", _ => AddGroup(position));
            evt.menu.AppendAction("布局/创建注释", _ => AddNote(position));
            evt.menu.AppendAction("布局/从分组移出所选项", _ => UngroupSelection());
            foreach (var descriptor in NodeDescriptor.For(document.Kind))
            {
                var captured = descriptor;
                evt.menu.AppendAction("创建/" + captured.Category + "/" + captured.Title,
                    _ => AddNode(captured.Type, position));
            }
        }

        private string SerializeSelection(IEnumerable<GraphElement> elements)
        {
            var data = new ClipboardData();
            if (document == null) return JsonUtility.ToJson(data);
            var selected = elements.ToList();
            var ids = new HashSet<string>(selected.Where(view => view is SocialNodeView || view is SocialNoteView)
                .Select(view => view.viewDataKey));
            var groupIds = new HashSet<string>(selected.OfType<SocialGroupView>().Select(view => view.Model.Id));
            foreach (var group in document.Groups)
                if (group != null && groupIds.Contains(group.Id))
                    foreach (var id in group.MemberIds ?? new List<string>()) ids.Add(id);
            foreach (var node in document.Nodes)
            {
                if (!ids.Contains(node.Id)) continue;
                var copied = new ClipboardNode { OldId = node.Id, Type = node.Type,
                    Position = node.Position };
                if (node.Parameters != null)
                    foreach (var parameter in node.Parameters)
                        if (parameter != null) copied.Parameters.Add(new GraphParameter(parameter.Key, parameter.Value));
                if (node.Subgraph != null)
                    copied.SubgraphGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(node.Subgraph));
                data.Nodes.Add(copied);
            }
            foreach (var edge in document.Edges)
                if (ids.Contains(edge.FromNodeId) && ids.Contains(edge.ToNodeId))
                    data.Edges.Add(edge.Clone(edge.FromNodeId, edge.ToNodeId));
            foreach (var note in document.Notes)
                if (note != null && ids.Contains(note.Id)) data.Notes.Add(note);
            foreach (var group in document.Groups)
                if (group != null && groupIds.Contains(group.Id)) data.Groups.Add(group);
            return JsonUtility.ToJson(data);
        }

        private void PasteSelection(string operationName, string json)
        {
            if (document == null || string.IsNullOrEmpty(json)) return;
            ClipboardData data;
            try { data = JsonUtility.FromJson<ClipboardData>(json); }
            catch (Exception) { return; }
            if (data == null || (data.Nodes == null || data.Nodes.Count == 0) &&
                (data.Notes == null || data.Notes.Count == 0) && (data.Groups == null || data.Groups.Count == 0)) return;
            Undo.RecordObject(document, operationName);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var added = new List<string>();
            foreach (var copied in data.Nodes ?? new List<ClipboardNode>())
            {
                if (copied == null || string.IsNullOrEmpty(copied.OldId) || map.ContainsKey(copied.OldId) ||
                    !Enum.IsDefined(typeof(GraphNodeType), copied.Type) ||
                    NodeDescriptor.Get(copied.Type).Kind != document.Kind) continue;
                if (copied.Type == GraphNodeType.Root && document.Nodes.Any(item => item.Type == GraphNodeType.Root)) continue;
                var node = NewNode(copied.Type, copied.Position + new Vector2(40, 40));
                node.Parameters.Clear();
                if (copied.Parameters != null)
                    foreach (var parameter in copied.Parameters)
                        if (parameter != null) node.Parameters.Add(new GraphParameter(parameter.Key, parameter.Value));
                if (!string.IsNullOrEmpty(copied.SubgraphGuid))
                    node.Subgraph = AssetDatabase.LoadAssetAtPath<GraphDocument>(AssetDatabase.GUIDToAssetPath(copied.SubgraphGuid));
                document.Nodes.Add(node);
                map[copied.OldId] = node.Id;
                added.Add(node.Id);
            }
            bool addedNodes = added.Count > 0;
            foreach (var copied in data.Notes ?? new List<GraphNote>())
            {
                if (copied == null || string.IsNullOrEmpty(copied.Id) || map.ContainsKey(copied.Id)) continue;
                var note = new GraphNote { Id = Guid.NewGuid().ToString("N"), Title = copied.Title,
                    Contents = copied.Contents, Theme = copied.Theme, FontSize = copied.FontSize,
                    Position = new Rect(copied.Position.position + new Vector2(40, 40), copied.Position.size) };
                document.Notes.Add(note); map[copied.Id] = note.Id; added.Add(note.Id);
            }
            foreach (var copied in data.Groups ?? new List<GraphGroup>())
            {
                if (copied == null || string.IsNullOrEmpty(copied.Id) || map.ContainsKey(copied.Id)) continue;
                var group = new GraphGroup { Id = Guid.NewGuid().ToString("N"), Title = copied.Title,
                    Position = new Rect(copied.Position.position + new Vector2(40, 40), copied.Position.size) };
                foreach (var id in copied.MemberIds ?? new List<string>())
                    if (map.TryGetValue(id ?? "", out var remapped) && !group.MemberIds.Contains(remapped)) group.MemberIds.Add(remapped);
                document.Groups.Add(group); map[copied.Id] = group.Id; added.Add(group.Id);
            }
            if (data.Edges != null)
                foreach (var edge in data.Edges)
                {
                    if (edge == null) continue;
                    string from, to;
                    if (edge.FromNodeId != null && edge.ToNodeId != null &&
                        map.TryGetValue(edge.FromNodeId, out from) && map.TryGetValue(edge.ToNodeId, out to))
                        document.Edges.Add(edge.Clone(from, to));
                }
            if (addedNodes) Changed(); else LayoutChanged();
            Open(document);
            graph.ClearSelection();
            foreach (var id in added)
            {
                var view = graph.FindLayoutElement(id);
                if (view != null) graph.AddToSelection(view);
            }
            ValidateDocument();
        }

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (document == null) return;
            var type = DragAndDrop.GetGenericData("SocialGraphNodeType");
            if (type is GraphNodeType && NodeDescriptor.Get((GraphNodeType)type).Kind == document.Kind)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                return;
            }
            if (document.Kind != GraphKind.Behavior) return;
            if (DragAndDrop.objectReferences.OfType<GraphDocument>().Any(asset => asset.Kind == GraphKind.Behavior))
                DragAndDrop.visualMode = DragAndDropVisualMode.Link;
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            if (document == null) return;
            var type = DragAndDrop.GetGenericData("SocialGraphNodeType");
            if (type is GraphNodeType && NodeDescriptor.Get((GraphNodeType)type).Kind == document.Kind)
            {
                DragAndDrop.AcceptDrag();
                var drop = graph.contentViewContainer.WorldToLocal(graph.LocalToWorld(evt.localMousePosition));
                AddNode((GraphNodeType)type, drop,
                    DragAndDrop.GetGenericData("SocialGraphBindingId") as string);
                evt.StopPropagation();
                return;
            }
            if (document.Kind != GraphKind.Behavior) return;
            var asset = DragAndDrop.objectReferences.OfType<GraphDocument>()
                .FirstOrDefault(item => item.Kind == GraphKind.Behavior);
            if (asset == null) return;
            DragAndDrop.AcceptDrag();
            var position = graph.contentViewContainer.WorldToLocal(graph.LocalToWorld(evt.localMousePosition));
            Undo.RecordObject(document, "Drop behavior subtree");
            var node = NewNode(GraphNodeType.Subtree, position);
            node.Subgraph = asset;
            document.Nodes.Add(node);
            Changed();
            Open(document);
            evt.StopPropagation();
        }

        private void OnTrace(GraphTraceEvent entry)
        {
            if (document == null || graph == null || actorField == null ||
                string.IsNullOrEmpty(actorField.value) || actorField.value != entry.ActorId ||
                entry.DocumentId != document.DocumentId || entry.Version != document.Version ||
                lastCompiledDocument != document || lastCompiledVersion != document.Version ||
                string.IsNullOrEmpty(entry.Revision) || entry.Revision != lastCompiledRevision) return;
            var view = graph.FindNodeView(entry.NodeId);
            if (view != null) view.SetTrace(entry.Status);
            if (breakpoints.Contains(entry.DocumentId + "/" + entry.NodeId))
            {
                var runner = RunningWorld();
                if (runner != null && runner.World != null)
                {
                    runner.SetPaused(true);
                    status.text = "断点命中 · NPC " + entry.ActorId + " · Tick " + entry.Tick + " · " + entry.Reason;
                    nextRuntimeRefresh = 0;
                }
            }
        }
    }
}
