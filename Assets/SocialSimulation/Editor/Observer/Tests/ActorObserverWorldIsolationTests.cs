using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace SocialSimulation.Editor.Observer.Tests
{
    public sealed class ActorObserverWorldIsolationTests
    {
        private const string Actor = "same.actor";

        private sealed class WorldFixture : IDisposable
        {
            public readonly GameObject Root;
            public readonly SocialWorldRunner Runner;
            public readonly SocialNpcMarker Marker;
            public readonly ObserverWorldProvider Provider;
            public SimulationWorld World;

            public WorldFixture(string id)
            {
                Root = new GameObject(id);
                Runner = Root.AddComponent<SocialWorldRunner>();
                Runner.enabled = false;
                Provider = Root.AddComponent<ObserverWorldProvider>();
                Provider.Node = "snapshot." + id;
                var actor = new GameObject("NPC"); actor.transform.SetParent(Root.transform);
                Marker = actor.AddComponent<SocialNpcMarker>(); Marker.Configure(Actor, id + " actor", "test");
                var markers = (Dictionary<string, SocialNpcMarker>)typeof(SocialWorldRunner)
                    .GetField("npcViews", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Runner);
                markers.Add(Actor, Marker);
                ReplaceWorld(id);
            }

            public void ReplaceWorld(string id)
            {
                World = new SimulationWorld(id);
                World.Places.Add("hall", new PlaceState { Id = "hall", Capacity = 10 });
                World.AddAgent(new AgentState { Id = Actor, PlaceId = "hall", DisplayName = id });
                World.Knowledge.RegisterChannel(new KnowledgeChannel { Id = "notice" });
                World.Knowledge.DeliverSystemNotice(Actor, "knowledge." + id, "world.statement", "hall", id,
                    Actor, "notice");
                typeof(SocialWorldRunner).GetProperty("World").SetValue(Runner, World);
            }

            public void Dispose() { if (Root != null) Object.DestroyImmediate(Root); }
        }

        private sealed class TraceSystem : IWorldSystem
        {
            public int Order => 1;
            public string Node;
            public string Instance = "same.instance";
            public GraphStatus Status = GraphStatus.Running;
            public void Tick(SimulationWorld world, double deltaSeconds) => GraphTraceHub.Publish(
                new GraphTraceEvent("same.graph", 1, "same.revision", Instance, Actor, Node,
                    world.TickIndex, Status, Status == GraphStatus.Failure ? "owned.terminal.failure" : null));
        }

        private sealed class NestedStepSystem : IWorldSystem
        {
            public int Order => 0;
            public SimulationWorld Other;
            public void Tick(SimulationWorld world, double deltaSeconds) => Other.StepOne();
        }

        private static string Section(ActorObserverWindow window, string name)
        {
            var labels = window.rootVisualElement.Q<VisualElement>(name).Query<Label>().ToList();
            var lines = new List<string>(); foreach (var label in labels) lines.Add(label.text);
            return string.Join("\n", lines);
        }

        [Test]
        public void SameActorIdSelectionControlsAndDisplaysOnlyTheSelectedOwnersWorld()
        {
            var previous = Selection.activeObject;
            using (var a = new WorldFixture("world.A"))
            using (var b = new WorldFixture("world.B"))
            {
                var window = ActorObserverWindow.ShowWindow();
                try
                {
                    window.CreateGUI();
                    var visual = new GameObject("Mesh child"); visual.transform.SetParent(b.Marker.transform);
                    Selection.activeGameObject = visual;
                    window.SelectSceneObject(visual);
                    StringAssert.Contains("world.B", window.rootVisualElement.Q<Label>("world-status").text);
                    StringAssert.Contains("knowledge.world.B", Section(window, "knowledge-rows"));
                    StringAssert.DoesNotContain("knowledge.world.A", Section(window, "knowledge-rows"));
                    StringAssert.Contains("snapshot.world.B", Section(window, "graph-rows"));
                    StringAssert.DoesNotContain("snapshot.world.A", Section(window, "graph-rows"));
                    window.TogglePause(); Assert.IsTrue(b.World.Paused); Assert.IsFalse(a.World.Paused);
                    window.StepOne(); Assert.AreEqual(1, b.World.TickIndex); Assert.AreEqual(0, a.World.TickIndex);
                    window.rootVisualElement.Q<UnityEngine.UIElements.FloatField>("time-scale-field").value = 7;
                    Assert.AreEqual(7, b.World.TimeScale); Assert.AreEqual(1, a.World.TimeScale);
                    window.SelectSceneObject(a.Marker.gameObject);
                    window.TogglePause(); window.StepOne();
                    Assert.IsTrue(a.World.Paused); Assert.AreEqual(1, a.World.TickIndex); Assert.AreEqual(1, b.World.TickIndex);
                    StringAssert.Contains("knowledge.world.A", Section(window, "knowledge-rows"));
                    StringAssert.DoesNotContain("knowledge.world.B", Section(window, "knowledge-rows"));
                    StringAssert.Contains("snapshot.world.A", Section(window, "graph-rows"));
                }
                finally { window.Close(); Selection.activeObject = previous; }
            }
        }

        [Test]
        public void UnregisteredAndOrphanMarkersNeverFallBackToAnotherSameIdActor()
        {
            var previous = Selection.activeObject;
            using (var a = new WorldFixture("world.A"))
            using (var b = new WorldFixture("world.B"))
            {
                var orphan = new GameObject("Orphan");
                var marker = orphan.AddComponent<SocialNpcMarker>(); marker.Configure(Actor, "orphan", "test");
                var window = ActorObserverWindow.ShowWindow();
                try
                {
                    window.CreateGUI(); window.SelectSceneObject(b.Marker.gameObject);
                    orphan.transform.SetParent(a.Root.transform);
                    window.SelectSceneObject(orphan);
                    Assert.IsFalse(window.rootVisualElement.Q<Button>("pause").enabledSelf);
                    Assert.IsFalse(window.rootVisualElement.Q<Button>("step").enabledSelf);
                    Assert.IsFalse(window.rootVisualElement.Q<UnityEngine.UIElements.FloatField>("time-scale-field").enabledSelf);
                    Assert.AreEqual("", Section(window, "knowledge-rows"));
                    window.StepOne(); window.TogglePause();
                    Assert.AreEqual(0, a.World.TickIndex); Assert.AreEqual(0, b.World.TickIndex);
                    Assert.IsFalse(a.World.Paused); Assert.IsFalse(b.World.Paused);
                    orphan.transform.SetParent(null);
                    window.SelectSceneObject(orphan); window.SetActorId(Actor);
                    window.RefreshNow();
                    Assert.IsFalse(window.rootVisualElement.Q<Button>("pause").enabledSelf);
                    Assert.AreEqual("", Section(window, "graph-rows"));
                }
                finally { window.Close(); Object.DestroyImmediate(orphan); Selection.activeObject = previous; }
            }
        }

        [Test]
        public void MissingBoundRunnerCannotSwitchToRemainingWorldAndMultipleWorldsNeedSelection()
        {
            var previous = Selection.activeObject;
            Selection.activeObject = null;
            using (var a = new WorldFixture("world.A"))
            using (var b = new WorldFixture("world.B"))
            {
                var window = ActorObserverWindow.ShowWindow();
                try
                {
                    window.CreateGUI();
                    Assert.IsFalse(window.rootVisualElement.Q<Button>("pause").enabledSelf);
                    window.SelectSceneObject(b.Marker.gameObject);
                    Object.DestroyImmediate(b.Root);
                    window.RefreshNow(); window.StepOne(); window.TogglePause();
                    Assert.AreEqual(0, a.World.TickIndex); Assert.IsFalse(a.World.Paused);
                    Assert.IsFalse(window.rootVisualElement.Q<Button>("pause").enabledSelf);
                    Assert.AreEqual("", Section(window, "knowledge-rows"));
                }
                finally { window.Close(); Selection.activeObject = previous; }
            }
        }

        [Test]
        public void GlobalTracesRequireCurrentOwnerTickAndMatchingInstanceAndAreClearedAcrossLoad()
        {
            var previous = Selection.activeObject;
            using (var a = new WorldFixture("world.A"))
            using (var b = new WorldFixture("world.B"))
            {
                a.World.AddSystem(new TraceSystem { Node = "trace.A" });
                var bTrace = new TraceSystem { Node = "trace.B" }; b.World.AddSystem(bTrace);
                var window = ActorObserverWindow.ShowWindow();
                try
                {
                    window.CreateGUI(); window.SelectSceneObject(b.Marker.gameObject);
                    a.World.StepOne(); window.RefreshNow();
                    StringAssert.DoesNotContain("trace.A", Section(window, "graph-rows"));
                    b.World.StepOne(); window.RefreshNow();
                    StringAssert.Contains("trace.B", Section(window, "graph-rows"));
                    bTrace.Node = "trace.wrong.instance"; bTrace.Instance = "unowned.instance";
                    b.World.StepOne(); window.RefreshNow();
                    StringAssert.DoesNotContain("trace.wrong.instance", Section(window, "graph-rows"));
                    b.ReplaceWorld("world.B.loaded");
                    window.RefreshNow();
                    StringAssert.DoesNotContain("trace.B", Section(window, "graph-rows"));
                    GraphTraceHub.Publish(new GraphTraceEvent("same.graph", 1, "same.revision", "same.instance", Actor,
                        "trace.old.after.load", 1, GraphStatus.Running, null));
                    window.RefreshNow();
                    StringAssert.DoesNotContain("trace.old.after.load", Section(window, "graph-rows"));
                    b.World.AddSystem(new TraceSystem { Node = "trace.loaded" }); b.World.StepOne(); window.RefreshNow();
                    StringAssert.Contains("trace.loaded", Section(window, "graph-rows"));
                    StringAssert.Contains("knowledge.world.B.loaded", Section(window, "knowledge-rows"));
                }
                finally { window.Close(); Selection.activeObject = previous; }
            }
        }

        [Test]
        public void ConfirmedTerminalFailureRemainsVisibleAfterItsActiveGraphIsRemoved()
        {
            var previous = Selection.activeObject;
            using (var fixture = new WorldFixture("terminal.world"))
            {
                fixture.World.AddSystem(new TraceSystem { Node = "terminal.node", Status = GraphStatus.Failure });
                var window = ActorObserverWindow.ShowWindow();
                try
                {
                    window.CreateGUI(); window.SelectSceneObject(fixture.Marker.gameObject);
                    fixture.World.StepOne();
                    fixture.Provider.HasSnapshot = false;
                    window.RefreshNow();
                    StringAssert.Contains("owned.terminal.failure", Section(window, "graph-rows"));
                    StringAssert.Contains("terminal.node", Section(window, "graph-rows"));
                    fixture.ReplaceWorld("terminal.world.restored"); window.RefreshNow();
                    StringAssert.DoesNotContain("owned.terminal.failure", Section(window, "graph-rows"));
                }
                finally { window.Close(); Selection.activeObject = previous; }
            }
        }

        [Test]
        public void ChildProvidersAndAmbiguousNestedWorldTracesDoNotEnterOwnersDiagnostics()
        {
            var previous = Selection.activeObject;
            using (var a = new WorldFixture("world.A"))
            using (var b = new WorldFixture("world.B"))
            {
                var foreign = new GameObject("Child provider"); foreign.transform.SetParent(b.Root.transform);
                foreign.AddComponent<ObserverWorldProvider>().Node = "unregistered.child.provider";
                a.World.AddSystem(new NestedStepSystem { Other = b.World });
                b.World.AddSystem(new TraceSystem { Node = "ambiguous.nested.trace" });
                var window = ActorObserverWindow.ShowWindow();
                try
                {
                    window.CreateGUI(); window.SelectSceneObject(b.Marker.gameObject);
                    a.World.StepOne(); window.RefreshNow();
                    StringAssert.Contains("snapshot.world.B", Section(window, "graph-rows"));
                    StringAssert.DoesNotContain("unregistered.child.provider", Section(window, "graph-rows"));
                    StringAssert.DoesNotContain("ambiguous.nested.trace", Section(window, "graph-rows"));
                }
                finally { window.Close(); Selection.activeObject = previous; }
            }
        }
    }

    public sealed class ObserverWorldProvider : MonoBehaviour, ISocialActorRuntimeDiagnostics
    {
        public string Node;
        public bool HasSnapshot = true;
        public IReadOnlyList<ActorRuntimeSnapshot> GetActorRuntimeSnapshots(string actorId) =>
            !HasSnapshot ? Array.Empty<ActorRuntimeSnapshot>() : new[] { new ActorRuntimeSnapshot(actorId, "same.graph", 1, "same.revision", "same.instance", Node,
                "test.action", "same.action", ActionPhase.Executing) };
    }
}
