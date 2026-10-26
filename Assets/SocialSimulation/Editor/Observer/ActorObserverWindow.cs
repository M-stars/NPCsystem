using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

[assembly: InternalsVisibleTo("SocialSimulation.Observer.Tests")]

namespace SocialSimulation.Editor.Observer
{
    public sealed class ActorObserverWindow : EditorWindow
    {
        private const string LayoutPath =
            "Assets/SocialSimulation/Editor/Observer/ActorObserverWindow.uxml";
        private const string StylePath =
            "Assets/SocialSimulation/Editor/Observer/ActorObserverWindow.uss";

        private readonly Dictionary<string, GraphTraceEvent> latestGraphTrace =
            new Dictionary<string, GraphTraceEvent>(StringComparer.Ordinal);
        private readonly List<ISocialActorRuntimeDiagnostics> providers =
            new List<ISocialActorRuntimeDiagnostics>();
        private SocialWorldRunner runner;
        private SimulationWorld observedWorld;
        private SocialNpcMarker selectedMarker;
        private bool hasMarkerSelection;
        private bool hasWorldBinding;
        private string bindingMessage;
        private DropdownField actorPicker;
        private TextField actorField;
        private TextField eventFilter;
        private TextField knowledgeFilter;
        private Label knowledgeSummary;
        private UnityEngine.UIElements.FloatField timeScale;
        private Label worldStatus;
        private Label selectionStatus;
        private Button pauseButton;
        private VisualElement root;
        private string selectedActorId;
        private double nextRefresh;

        [MenuItem("Tools/Social Simulation/Runtime Observer")]
        public static ActorObserverWindow ShowWindow()
        {
            var window = GetWindow<ActorObserverWindow>();
            window.titleContent = new GUIContent("NPC Observer");
            window.minSize = new Vector2(700, 560);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            EditorApplication.update += UpdateView;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Selection.selectionChanged += OnSelectionChanged;
            GraphTraceHub.Published += OnGraphTrace;
        }

        private void OnDisable()
        {
            EditorApplication.update -= UpdateView;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            GraphTraceHub.Published -= OnGraphTrace;
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (layout == null || style == null)
            {
                rootVisualElement.Add(new Label("观察器界面资源缺失：" + LayoutPath));
                return;
            }
            layout.CloneTree(rootVisualElement);
            rootVisualElement.styleSheets.Add(style);
            root = rootVisualElement.Q<VisualElement>("observer-root");
            worldStatus = root.Q<Label>("world-status");
            selectionStatus = root.Q<Label>("selection-status");
            actorField = root.Q<TextField>("actor-id");
            eventFilter = root.Q<TextField>("event-filter");
            knowledgeFilter = root.Q<TextField>("knowledge-filter");
            knowledgeSummary = root.Q<Label>("knowledge-summary");
            pauseButton = root.Q<Button>("pause");

            actorPicker = new DropdownField { name = "actor-dropdown", label = "NPC" };
            root.Q<VisualElement>("actor-picker").Add(actorPicker);
            actorPicker.RegisterValueChangedCallback(change => SetActorId(change.newValue));
            actorField.RegisterValueChangedCallback(change => SetActorId(change.newValue));
            eventFilter.RegisterValueChangedCallback(_ => RefreshNow());
            knowledgeFilter.RegisterValueChangedCallback(_ => RefreshNow());
            root.Q<Button>("select-npc").clicked += SelectCurrentNpc;
            pauseButton.clicked += TogglePause;
            root.Q<Button>("step").clicked += StepOne;
            timeScale = new UnityEngine.UIElements.FloatField("倍率") { name = "time-scale-field" };
            timeScale.RegisterValueChangedCallback(change =>
            {
                if (!EnsureBoundWorld()) return;
                runner.World.TimeScale = Math.Max(0.01, Math.Min(20, change.newValue));
                timeScale.SetValueWithoutNotify((float)runner.World.TimeScale);
            });
            root.Q<VisualElement>("time-scale").Add(timeScale);
            OnSelectionChanged();
            RefreshNow();
        }

        public void SetActorId(string actorId)
        {
            selectedMarker = null;
            hasMarkerSelection = false;
            SetActorIdWithoutRefresh(actorId);
            RefreshNow();
        }

        private void SetActorIdWithoutRefresh(string actorId)
        {
            var normalized = string.IsNullOrWhiteSpace(actorId) ? null : actorId.Trim();
            if (selectedActorId != normalized) latestGraphTrace.Clear();
            selectedActorId = normalized;
            actorField?.SetValueWithoutNotify(selectedActorId ?? "");
            if (actorPicker != null && actorPicker.choices != null &&
                actorPicker.choices.Contains(selectedActorId))
                actorPicker.SetValueWithoutNotify(selectedActorId);
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            BindRunner(null);
            hasWorldBinding = false;
            selectedMarker = null;
            hasMarkerSelection = false;
            OnSelectionChanged();
            RefreshNow();
        }

        private void OnGraphTrace(GraphTraceEvent entry)
        {
            // The event bus has no world identity. Only accept a trace synchronously emitted by
            // the bound world's tick and corroborated by a provider owned by this runner.
            if (entry == null || entry.ActorId != selectedActorId || runner == null || observedWorld == null ||
                !ReferenceEquals(observedWorld, runner.World) || !observedWorld.IsStepping || entry.Tick != observedWorld.TickIndex ||
                string.IsNullOrEmpty(entry.ActorId)) return;
            foreach (var other in UnityEngine.Object.FindObjectsByType<SocialWorldRunner>(FindObjectsInactive.Include))
                if (other != runner && other.World != null && other.World.IsStepping) return;
            foreach (var provider in providers)
            {
                if (!(provider is MonoBehaviour component) || component == null || component.gameObject != runner.gameObject) continue;
                try
                {
                    var snapshots = provider.GetActorRuntimeSnapshots(entry.ActorId);
                    if (snapshots == null) continue;
                    foreach (var snapshot in snapshots)
                        if (MatchesTrace(snapshot, entry)) { latestGraphTrace[entry.ActorId] = entry; return; }
                }
                catch (Exception) { /* A provider that cannot confirm ownership cannot supply a trace. */ }
            }
        }

        private void OnSelectionChanged()
        {
            SelectSceneObject(Selection.activeGameObject);
        }

        internal void SelectSceneObject(GameObject gameObject)
        {
            var marker = gameObject == null ? null :
                gameObject.GetComponentInParent<SocialNpcMarker>();
            if (marker != null)
            {
                BindRunner(marker.GetComponentInParent<SocialWorldRunner>());
                selectedMarker = marker;
                hasMarkerSelection = true;
                SetActorIdWithoutRefresh(marker.NpcInstanceId);
                RefreshNow();
                return;
            }
            var owner = gameObject == null ? null : gameObject.GetComponentInParent<SocialWorldRunner>();
            if (owner == null) return;
            BindRunner(owner);
            selectedMarker = null;
            hasMarkerSelection = false;
            SetActorIdWithoutRefresh(null);
            RefreshNow();
        }

        private void SelectCurrentNpc() { OnSelectionChanged(); }

        internal void TogglePause()
        {
            if (!EnsureBoundWorld()) { RefreshNow(); return; }
            runner.SetPaused(!runner.World.Paused);
            RefreshNow();
        }

        internal void StepOne()
        {
            if (!EnsureBoundWorld()) { RefreshNow(); return; }
            runner.SetPaused(true);
            if (!runner.TryStepOne(out var error))
            {
                selectionStatus.text = "单步失败：" + error;
                return;
            }
            RefreshNow();
        }

        private void UpdateView()
        {
            if (root == null || EditorApplication.timeSinceStartup < nextRefresh) return;
            nextRefresh = EditorApplication.timeSinceStartup + 0.25;
            RefreshNow();
        }

        internal void RefreshNow()
        {
            if (root == null) return;
            if (!hasWorldBinding)
            {
                SocialWorldRunner only = null;
                bool multiple = false;
                foreach (var candidate in UnityEngine.Object.FindObjectsByType<SocialWorldRunner>(FindObjectsInactive.Exclude))
                    if (candidate.World != null)
                    {
                        if (only != null) { multiple = true; break; }
                        only = candidate;
                    }
                if (!multiple && only != null) BindRunner(only);
                else bindingMessage = multiple ? "多个运行世界，请在场景中选择 NPC 或世界对象" : "等待运行中的 SocialWorldRunner";
            }
            if (!EnsureBoundWorld())
            {
                worldStatus.text = bindingMessage ?? "等待所选世界初始化";
                selectionStatus.text = "请在目标世界中选择已登记的 NPC";
                SetControlsEnabled(false);
                ClearSections();
                return;
            }
            SetControlsEnabled(true);
            var world = runner.World;
            worldStatus.text = "世界 " + world.WorldId + " [" + runner.name + "]  Tick " + world.TickIndex +
                "  时间 " + world.Time.ToString("0.0") + " s  NPC " + world.Agents.Count +
                "  会话 " + world.Activities.Sessions.Count +
                "  事件 " + world.Trace.Count +
                (world.Paused ? "  已暂停" : "  运行中");
            pauseButton.text = world.Paused ? "继续" : "暂停";
            timeScale.SetValueWithoutNotify((float)world.TimeScale);
            RefreshActorChoices();
            if (string.IsNullOrEmpty(selectedActorId))
            {
                selectionStatus.text = "请选择或输入 NPC ID";
                ClearSections();
                return;
            }
            var runtime = new List<ActorRuntimeSnapshot>();
            string runtimeError = null;
            foreach (var provider in providers)
            {
                if (!(provider is MonoBehaviour component) || component == null || component.gameObject != runner.gameObject) continue;
                try
                {
                    var snapshots = provider.GetActorRuntimeSnapshots(selectedActorId);
                    if (snapshots == null) continue;
                    foreach (var snapshot in snapshots) if (snapshot != null) runtime.Add(snapshot);
                }
                catch (Exception exception)
                {
                    runtimeError = "图诊断提供者失败：" + exception.Message;
                }
            }
            latestGraphTrace.TryGetValue(selectedActorId, out var graphTrace);
            var view = ActorObserverDataBuilder.Build(world, selectedActorId,
                runtime, graphTrace, eventFilter.value, knowledgeFilter.value);
            if (view == null)
            {
                selectionStatus.text = "未找到 NPC：" + selectedActorId;
                ClearSections();
                return;
            }
            if (runtimeError != null) view.Graphs.Add("失败 " + runtimeError);
            BindActorView(view);
        }

        private void BindRunner(SocialWorldRunner value)
        {
            hasWorldBinding = true;
            if (ReferenceEquals(runner, value) && value != null) return;
            runner = value;
            observedWorld = null;
            providers.Clear();
            latestGraphTrace.Clear();
            bindingMessage = value == null ? "所选 NPC 没有所属运行世界；请重新选择" : null;
            if (actorPicker != null)
            { actorPicker.choices = new List<string>(); actorPicker.SetValueWithoutNotify(""); }
        }

        private bool EnsureBoundWorld()
        {
            if (runner == null || runner.World == null)
            {
                observedWorld = null;
                providers.Clear();
                latestGraphTrace.Clear();
                return false;
            }
            if (hasMarkerSelection && (selectedMarker == null ||
                selectedMarker.GetComponentInParent<SocialWorldRunner>() != runner ||
                !runner.NpcViews.TryGetValue(selectedMarker.NpcInstanceId ?? "", out var registered) || registered != selectedMarker))
            {
                BindRunner(null);
                bindingMessage = "所选 NPC 未登记在其所属世界中；不会切换到其他同 ID 的 NPC";
                return false;
            }
            if (!ReferenceEquals(observedWorld, runner.World))
            {
                observedWorld = runner.World;
                providers.Clear();
                latestGraphTrace.Clear();
                foreach (var component in runner.GetComponents<MonoBehaviour>())
                    if (component is ISocialActorRuntimeDiagnostics provider) providers.Add(provider);
            }
            return true;
        }

        private void SetControlsEnabled(bool enabled)
        {
            pauseButton.SetEnabled(enabled);
            root.Q<Button>("step").SetEnabled(enabled);
            timeScale.SetEnabled(enabled);
            actorPicker.SetEnabled(enabled);
        }

        private static bool MatchesTrace(ActorRuntimeSnapshot snapshot, GraphTraceEvent trace) =>
            snapshot != null && trace != null && !string.IsNullOrEmpty(trace.InstanceId) &&
            snapshot.ActorId == trace.ActorId && snapshot.GraphInstanceId == trace.InstanceId &&
            snapshot.GraphId == trace.DocumentId && snapshot.GraphVersion == trace.Version &&
            snapshot.GraphRevision == trace.Revision;

        internal void BindActorView(ActorObserverData view)
        {
            if (root == null) return;
            if (view == null) { ClearSections(); return; }
            selectionStatus.text = view.DisplayName + "  [" + view.ActorId + "]  " +
                "位置 " + (view.PlaceId ?? "—") + "  目标 " + (view.GoalId ?? "—");
            knowledgeSummary.text = "匹配 " + view.KnowledgeMatchedCount + " / 个人记录总数 " + view.KnowledgeTotalCount +
                "，显示 " + view.KnowledgeDisplayedCount + " 条" +
                (view.KnowledgeMatchedCount > view.KnowledgeDisplayedCount
                    ? "；另有 " + (view.KnowledgeMatchedCount - view.KnowledgeDisplayedCount) + " 条，请缩小筛选范围" : "");
            Fill("identity-rows", view.Identity);
            Fill("needs-rows", view.NeedsAndEmotion);
            Fill("knowledge-rows", view.Knowledge);
            Fill("decision-rows", view.Decisions);
            Fill("graph-rows", view.Graphs);
            Fill("resource-rows", view.ResourcesAndSessions);
            Fill("norm-rows", view.Norms);
            Fill("event-rows", view.Events);
        }

        private void RefreshActorChoices()
        {
            var choices = new List<string>(runner.World.Agents.Keys);
            choices.Sort(StringComparer.Ordinal);
            if (actorPicker.choices != null && actorPicker.choices.Count == choices.Count)
            {
                var same = true;
                for (var i = 0; i < choices.Count; i++)
                    if (choices[i] != actorPicker.choices[i]) { same = false; break; }
                if (same) return;
            }
            actorPicker.choices = choices;
            if (selectedActorId != null && choices.Contains(selectedActorId))
                actorPicker.SetValueWithoutNotify(selectedActorId);
            else if (choices.Count > 0 && string.IsNullOrEmpty(selectedActorId))
                SetActorId(choices[0]);
        }

        private void ClearSections()
        {
            Fill("identity-rows", null);
            Fill("needs-rows", null);
            Fill("knowledge-rows", null);
            if (knowledgeSummary != null) knowledgeSummary.text = "";
            Fill("decision-rows", null);
            Fill("graph-rows", null);
            Fill("resource-rows", null);
            Fill("norm-rows", null);
            Fill("event-rows", null);
        }

        private void Fill(string name, IReadOnlyList<string> lines)
        {
            var container = root.Q<VisualElement>(name);
            if (container == null) return;
            container.Clear();
            if (lines == null || lines.Count == 0) return;
            foreach (var line in lines)
            {
                var label = new Label(line) { enableRichText = false };
                label.AddToClassList("observer-row");
                if (line.StartsWith("失败 ", StringComparison.Ordinal))
                    label.AddToClassList("observer-error");
                if (line.StartsWith("无", StringComparison.Ordinal))
                    label.AddToClassList("observer-empty");
                if (line.StartsWith("来源 ", StringComparison.Ordinal) || line.StartsWith("时间 ", StringComparison.Ordinal) ||
                    line.StartsWith("知识依据 ", StringComparison.Ordinal) || line.StartsWith("决策记录 ", StringComparison.Ordinal))
                    label.AddToClassList("observer-detail");
                container.Add(label);
            }
        }
    }
}
