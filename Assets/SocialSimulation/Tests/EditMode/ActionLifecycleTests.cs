using System;
using System.Collections.Generic;
using NUnit.Framework;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using UnityEngine;

namespace SocialSimulation.Tests
{
    public sealed class ActionLifecycleTests
    {
        private sealed class TestAdapter : IWorldActionAdapter
        {
            public string SlotId;
            public bool UseCompositeScope;
            public bool MoveComplete;
            public bool TransferToSession;
            public string SessionId;
            public bool CommitMoveEffect;
            public bool ThrowOnExit;
            public ActionExecutionContext PendingContext;
            public ResourceToken Token;
            public int EffectCount;
            public int ExitCount;
            public readonly List<ActionInstance> Finished = new List<ActionInstance>();

            public ActionStepResult Step(ActionExecutionContext context, ActionPhase phase)
            {
                switch (phase)
                {
                    case ActionPhase.Validating:
                        return ActionStepResult.Success;
                    case ActionPhase.Reserving:
                        if (string.IsNullOrEmpty(SlotId)) return ActionStepResult.Success;
                        if (Token == null || !Token.Active)
                            Token = UseCompositeScope
                                ? context.TryReserveForScope(SlotId, 30)
                                : context.TryReserveLocal(SlotId, 30);
                        return Token == null ? context.Fail(ActionFailureCode.ResourceUnavailable) : ActionStepResult.Success;
                    case ActionPhase.Moving:
                        PendingContext = context;
                        if (CommitMoveEffect)
                            context.TryCommitEffect("move", () => EffectCount++);
                        return MoveComplete ? ActionStepResult.Success : ActionStepResult.Running;
                    case ActionPhase.Aligning:
                        return ActionStepResult.Success;
                    case ActionPhase.Executing:
                        context.TryCommitEffect("complete", () => EffectCount++);
                        context.TryCommitEffect("complete", () => EffectCount++);
                        if (TransferToSession && !context.TryTransferToSession(Token.Id, SessionId))
                            return context.Fail(ActionFailureCode.ExecutionFailed);
                        return ActionStepResult.Success;
                    default:
                        return context.Fail(ActionFailureCode.ExecutionFailed);
                }
            }

            public void OnExit(ActionExecutionContext context, ActionExitReason reason)
            {
                ExitCount++;
                Finished.Add(context.Instance);
                if (ThrowOnExit) throw new InvalidOperationException("Exit hook failure");
            }
        }

        private sealed class RecordingPresentation : IWorldActionPresentation
        {
            public readonly List<ActionPresentationEvent> Entries =
                new List<ActionPresentationEvent>();
            public bool Throw;

            public void OnActionPresentation(ActionPresentationEvent entry)
            {
                Entries.Add(entry);
                if (Throw) throw new InvalidOperationException("Visual callback failure");
            }
        }

        private sealed class DomainServiceAdapter : IWorldActionAdapter
        {
            private readonly string slotId;
            private readonly Action<ActionExecutionContext> commit;
            public ResourceToken Token;
            public int ExitCount;

            public DomainServiceAdapter(string slotId, Action<ActionExecutionContext> commit)
            {
                this.slotId = slotId;
                this.commit = commit;
            }

            public ActionStepResult Step(ActionExecutionContext context, ActionPhase phase)
            {
                switch (phase)
                {
                    case ActionPhase.Validating:
                        return context.World.Agents.ContainsKey(context.Instance.ActorId)
                            ? ActionStepResult.Success : context.Fail(ActionFailureCode.UnknownActor);
                    case ActionPhase.Reserving:
                        Token = context.TryReserveLocal(slotId, 30);
                        return Token == null ? context.Fail(ActionFailureCode.ResourceUnavailable)
                            : ActionStepResult.Success;
                    case ActionPhase.Moving:
                    case ActionPhase.Aligning:
                        return ActionStepResult.Success;
                    case ActionPhase.Executing:
                        context.TryCommitEffect("business", () => commit(context));
                        return ActionStepResult.Success;
                    default:
                        return context.Fail(ActionFailureCode.ExecutionFailed);
                }
            }

            public void OnExit(ActionExecutionContext context, ActionExitReason reason)
            {
                ExitCount++;
            }
        }

        private static SimulationWorld NewWorld()
        {
            var world = new SimulationWorld("actions-test") { TickSeconds = 1 };
            world.AddAgent(new AgentState { Id = "agent", DisplayName = "Agent", PlaceId = "room" });
            world.Resources.Slots.Add("seat", new ResourceSlot
            { Id = "seat", PlaceId = "room", Kind = "seat", Capacity = 1 });
            return world;
        }

        private static GraphDocument NewGraph(bool waitAfterAction = false)
        {
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.DocumentId = "graph.actions.test";
            document.Kind = GraphKind.Behavior;
            document.Nodes.Add(new GraphNode { Id = "root", Type = GraphNodeType.Root });
            if (waitAfterAction)
            {
                document.Nodes.Add(new GraphNode { Id = "sequence", Type = GraphNodeType.Sequence });
                document.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "sequence" });
            }
            var action = new GraphNode { Id = "action-node", Type = GraphNodeType.Action };
            action.Set("actionId", "test.action");
            action.Set("targetId", "seat");
            document.Nodes.Add(action);
            document.Edges.Add(new GraphEdge
            { FromNodeId = waitAfterAction ? "sequence" : "root", ToNodeId = "action-node", Order = 0 });
            if (waitAfterAction)
            {
                var wait = new GraphNode { Id = "wait-node", Type = GraphNodeType.Wait };
                wait.Set("ticks", "1");
                document.Nodes.Add(wait);
                document.Edges.Add(new GraphEdge
                { FromNodeId = "sequence", ToNodeId = "wait-node", Order = 1 });
            }
            return document;
        }

        private static ActionGraphHost NewHost(SimulationWorld world, GraphDocument document,
            TestAdapter adapter, double timeoutSeconds = 10,
            IWorldActionPresentation presentation = null)
        {
            var compiled = GraphCompiler.Compile(document);
            Assert.IsTrue(compiled.Success, string.Join("; ", compiled.Diagnostics));
            var behavior = new BehaviorInstance(compiled.BehaviorPlan, "agent", "behavior-1");
            return new ActionGraphHost(world, behavior,
                new Dictionary<string, ActionDefinition>
                {
                    ["test.action"] = new ActionDefinition
                    { Id = "test.action", ExecutorId = "test.executor", TargetKind = "slot",
                        TimeoutSeconds = timeoutSeconds }
                },
                new Dictionary<string, IWorldActionAdapter> { ["test.executor"] = adapter },
                presentation: presentation);
        }

        [Test]
        public void PresentationReceivesEnterPhaseCancelAndRestoreWithoutControllingLogic()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var visual = new RecordingPresentation { Throw = true };
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter, presentation: visual);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                Assert.IsNotNull(host.LastPresentationException);
                Assert.AreEqual(ActionPresentationMoment.Entered, visual.Entries[0].Moment);
                Assert.IsTrue(visual.Entries.Exists(e => e.Moment ==
                    ActionPresentationMoment.PhaseChanged && e.Phase == ActionPhase.Moving));
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var resumedVisual = new RecordingPresentation();
                var resumed = NewHost(restoredWorld, graph,
                    new TestAdapter { SlotId = "seat", MoveComplete = false },
                    presentation: resumedVisual);
                Assert.IsTrue(resumed.TryRestoreCheckpoint(savedAction,
                    restoredWorld.TickIndex), resumed.LastRestoreFailure);
                Assert.AreEqual(ActionPresentationMoment.Restored,
                    resumedVisual.Entries[0].Moment);
                Assert.AreEqual(ActionPhase.Moving,
                    resumedVisual.Entries[0].PreviousPhase);
                resumed.Cancel(restoredWorld.TickIndex);
                Assert.AreEqual(ActionPresentationMoment.Exited,
                    resumedVisual.Entries[resumedVisual.Entries.Count - 1].Moment);
                Assert.AreEqual(ActionExitReason.Cancelled,
                    resumedVisual.Entries[resumedVisual.Entries.Count - 1].ExitReason);
                Assert.IsFalse(restoredWorld.Resources.Tokens.ContainsKey(
                    savedAction.Actions[0].ResourceTokenIds[0]));
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void SameActionLifecycleServesCareWagesPurchaseAndClubActivity()
        {
            var world = NewWorld();
            world.AddAgent(new AgentState { Id = "dependent", PlaceId = "room" });
            world.Agents["agent"].PlaceId = "square";
            var careTask = world.CreateTask("agent", "family", "family.care", "dependent", 100);
            world.Economy.Seed("employer", "credit", 10);
            world.Economy.Seed("shop", "snack", 3);
            var club = world.Activities.Create("club.meetup", "agent", "square",
                0, 100, 20, 1, 2);
            var effects = new Dictionary<string, Action<ActionExecutionContext>>
            {
                ["family.care"] = context =>
                {
                    careTask.Status = "Completed";
                    context.World.Emit("family.care.completed", "agent", "dependent", careTask.Id);
                },
                ["work.wage"] = context =>
                {
                    if (!context.World.Economy.Transfer(context.Instance.Id,
                        "employer", "agent", "credit", 6))
                        throw new InvalidOperationException("Wage transfer failed.");
                },
                ["shop.purchase"] = context =>
                {
                    if (!context.World.Economy.Exchange(context.Instance.Id,
                        "agent", "shop", "snack", 1, "credit", 2))
                        throw new InvalidOperationException("Purchase failed.");
                },
                ["club.join"] = context =>
                {
                    if (!context.World.Activities.Start(club.Id) ||
                        !context.World.Activities.Complete(club.Id, "meetup"))
                        throw new InvalidOperationException("Club activity failed.");
                }
            };
            var slots = new[] { "family-bed", "work-counter", "shop-queue", "club-place" };
            var actionIds = new[] { "family.care", "work.wage", "shop.purchase", "club.join" };
            var graphs = new List<GraphDocument>();
            try
            {
                for (var i = 0; i < actionIds.Length; i++)
                {
                    var actionId = actionIds[i];
                    var slotId = slots[i];
                    world.Resources.Slots.Add(slotId, new ResourceSlot
                    { Id = slotId, PlaceId = "square", Capacity = 1 });
                    var graph = ScriptableObject.CreateInstance<GraphDocument>();
                    graphs.Add(graph);
                    graph.DocumentId = "graph." + actionId;
                    graph.Kind = GraphKind.Behavior;
                    graph.Nodes.Add(new GraphNode { Id = "root", Type = GraphNodeType.Root });
                    var node = new GraphNode { Id = "action", Type = GraphNodeType.Action };
                    node.Set("actionId", actionId);
                    node.Set("slotId", slotId);
                    graph.Nodes.Add(node);
                    graph.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "action" });
                    var compiled = GraphCompiler.Compile(graph);
                    Assert.IsTrue(compiled.Success, string.Join("; ", compiled.Diagnostics));
                    var adapter = new DomainServiceAdapter(slotId, effects[actionId]);
                    var host = new ActionGraphHost(world,
                        new BehaviorInstance(compiled.BehaviorPlan, "agent", "run-" + i),
                        new Dictionary<string, ActionDefinition>
                        {
                            [actionId] = new ActionDefinition
                            { Id = actionId, ExecutorId = "domain.service", TimeoutSeconds = 10,
                                TargetKind = "slot" }
                        },
                        new Dictionary<string, IWorldActionAdapter>
                        { ["domain.service"] = adapter });
                    Assert.AreEqual(GraphStatus.Success, host.Tick(world.TickIndex), actionId);
                    Assert.AreEqual(1, adapter.ExitCount, actionId);
                    Assert.IsFalse(adapter.Token.Active, actionId + " left a resource token active.");
                    Assert.AreEqual(0, host.ActiveActionCount);
                }
                Assert.AreEqual("Completed", careTask.Status);
                Assert.AreEqual(4, world.Economy.Balance("agent", "credit"));
                Assert.AreEqual(1, world.Economy.Balance("agent", "snack"));
                Assert.AreEqual("Completed", club.State);
                Assert.AreEqual(0, world.Resources.Tokens.Count);
                Assert.AreEqual(3, world.Economy.Ledger.Count);
            }
            finally
            {
                foreach (var graph in graphs) UnityEngine.Object.DestroyImmediate(graph);
            }
        }

        [Test]
        public void SameActionIdAtDifferentNodesCreatesDistinctInstancesAndEffects()
        {
            var world = NewWorld();
            var graph = ScriptableObject.CreateInstance<GraphDocument>();
            try
            {
                graph.DocumentId = "graph.actions.repeated";
                graph.Kind = GraphKind.Behavior;
                graph.Nodes.Add(new GraphNode { Id = "root", Type = GraphNodeType.Root });
                graph.Nodes.Add(new GraphNode { Id = "sequence", Type = GraphNodeType.Sequence });
                graph.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "sequence" });
                for (int i = 0; i < 2; i++)
                {
                    var node = new GraphNode { Id = "action-" + i, Type = GraphNodeType.Action };
                    node.Set("actionId", "test.action");
                    graph.Nodes.Add(node);
                    graph.Edges.Add(new GraphEdge
                    { FromNodeId = "sequence", ToNodeId = node.Id, Order = i });
                }
                var adapter = new TestAdapter { MoveComplete = true };
                var host = NewHost(world, graph, adapter);

                Assert.AreEqual(GraphStatus.Success, host.Tick(0));
                Assert.AreEqual(2, adapter.ExitCount);
                Assert.AreEqual(2, adapter.EffectCount);
                Assert.AreNotEqual(adapter.Finished[0].Id, adapter.Finished[1].Id);
                Assert.AreNotEqual(adapter.Finished[0].NodeId, adapter.Finished[1].NodeId);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void RunningActionRestoresWithTokenAndInvalidatesOldCallbackGeneration()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var original = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, original);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var oldGeneration = original.PendingContext.Instance.CancellationGeneration;
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                Assert.AreEqual(ActionPhase.Moving, savedAction.Actions[0].Phase);
                Assert.AreEqual(1, savedAction.Actions[0].ResourceTokenIds.Count);

                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var resumed = new TestAdapter { SlotId = "seat", MoveComplete = true };
                var restoredHost = NewHost(restoredWorld, graph, resumed);
                Assert.IsTrue(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex),
                    restoredHost.LastRestoreFailure);
                Assert.IsTrue(restoredHost.TryGetActiveAction("behavior-1", "action-node", out var instance));
                Assert.AreEqual(oldGeneration + 1, instance.CancellationGeneration);
                Assert.AreEqual(ActionPhase.Validating, instance.Phase);
                Assert.AreEqual(GraphStatus.Success, restoredHost.Tick(restoredWorld.TickIndex));
                Assert.AreEqual(1, resumed.EffectCount);
                Assert.IsFalse(resumed.Token.Active);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void LegacyRecursiveCheckpointIsRejectedAndReleasesOwnedToken()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var host = NewHost(world, graph, new TestAdapter { SlotId = "seat", MoveComplete = false });
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                Assert.AreEqual(3, savedAction.SchemaVersion);
                savedAction.SchemaVersion = 2;
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var restoredHost = NewHost(restoredWorld, graph, new TestAdapter());
                Assert.IsFalse(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex));
                StringAssert.Contains("schema", restoredHost.LastRestoreFailure);
                Assert.AreEqual(0, restoredWorld.Resources.Tokens.Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void InvalidTargetAtRestoreCancelsActionAndReleasesOwnedToken()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                restoredWorld.Resources.Slots["seat"].Active = false;
                var restoredHost = NewHost(restoredWorld, graph, new TestAdapter());

                Assert.IsFalse(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex));
                Assert.AreEqual(ActionPhase.Cancelled, restoredHost.LastFinishedAction.Phase);
                Assert.IsFalse(restoredWorld.Resources.Tokens.TryGetValue(
                    savedAction.Actions[0].ResourceTokenIds[0], out var token) && token.Active);
                StringAssert.Contains("target", restoredHost.LastRestoreFailure);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void CompositeScopeAndGraphWaitResumeTogether()
        {
            var world = NewWorld();
            var graph = NewGraph(true);
            try
            {
                var adapter = new TestAdapter
                { SlotId = "seat", UseCompositeScope = true, MoveComplete = true };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                Assert.AreEqual(0, savedAction.Actions.Count);
                Assert.AreEqual(1, savedAction.ScopeTokenIds.Count);
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var restoredHost = NewHost(restoredWorld, graph, new TestAdapter());

                Assert.IsTrue(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex),
                    restoredHost.LastRestoreFailure);
                restoredWorld.StepOne();
                Assert.AreEqual(GraphStatus.Success, restoredHost.Tick(restoredWorld.TickIndex));
                Assert.IsFalse(restoredWorld.Resources.Tokens.TryGetValue(
                    savedAction.ScopeTokenIds[0], out var token) && token.Active);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void RestoreRejectsChangedTokenOwnerWithoutReleasingForeignScope()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var tokenId = savedAction.Actions[0].ResourceTokenIds[0];
                restoredWorld.Resources.Tokens[tokenId].OwnerScopeId = "foreign-scope";
                var restoredHost = NewHost(restoredWorld, graph, new TestAdapter());

                Assert.IsFalse(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex));
                StringAssert.Contains("token", restoredHost.LastRestoreFailure);
                Assert.IsTrue(restoredWorld.Resources.Tokens[tokenId].Active);
                Assert.AreEqual("foreign-scope", restoredWorld.Resources.Tokens[tokenId].OwnerScopeId);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void RejectedCheckpointCannotReleaseAnotherActorsScope()
        {
            var world = NewWorld();
            world.AddAgent(new AgentState { Id = "other", DisplayName = "Other", PlaceId = "room" });
            world.Resources.Slots.Add("other-seat", new ResourceSlot
            { Id = "other-seat", PlaceId = "room", Kind = "seat", Capacity = 1 });
            world.Resources.Slots.Add("unrelated-seat", new ResourceSlot
            { Id = "unrelated-seat", PlaceId = "room", Kind = "seat", Capacity = 1 });
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var otherToken = world.Resources.TryAcquire("other-seat", "other-scope", "other", 30);
                Assert.IsNotNull(otherToken);
                var unrelatedToken = world.Resources.TryAcquire(
                    "unrelated-seat", "other-scope", "agent", 30);
                Assert.IsNotNull(unrelatedToken);
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var restoredHost = NewHost(restoredWorld, graph, new TestAdapter());

                // This ID has never belonged to the checkpoint actor.
                savedAction.ScopeId = "other-scope";
                Assert.IsFalse(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex));
                Assert.IsTrue(restoredWorld.Resources.Tokens.TryGetValue(otherToken.Id, out var foreign));
                Assert.IsTrue(foreign.Active);
                Assert.AreEqual("other", foreign.AgentId);
                Assert.IsTrue(restoredWorld.Resources.Tokens.TryGetValue(
                    unrelatedToken.Id, out var sameActor));
                Assert.IsTrue(sameActor.Active,
                    "Another action's token must survive even when it belongs to the same actor.");
                Assert.IsFalse(restoredWorld.Resources.Tokens.ContainsKey(adapter.Token.Id),
                    "The rejected actor's local reservation must still be released.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void RestorePreservesCommittedEffectKeys()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter
                { MoveComplete = false, CommitMoveEffect = true };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                Assert.AreEqual(1, adapter.EffectCount);
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                CollectionAssert.Contains(savedAction.Actions[0].CommittedEffects, "move");
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var resumed = new TestAdapter
                { MoveComplete = true, CommitMoveEffect = true };
                var restoredHost = NewHost(restoredWorld, graph, resumed);

                Assert.IsTrue(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex),
                    restoredHost.LastRestoreFailure);
                Assert.AreEqual(GraphStatus.Success, restoredHost.Tick(restoredWorld.TickIndex));
                Assert.AreEqual(1, resumed.EffectCount,
                    "Only the new completion effect may run after restoring the committed move effect.");
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void JsonRoundTripRestoresOptionalNullFieldsAsEmptyStrings()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                graph.FindNode("action-node").Parameters.RemoveAll(p => p.Key == "targetId");
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = JsonUtility.FromJson<ActionGraphCheckpoint>(
                    JsonUtility.ToJson(host.CaptureCheckpoint()));
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var restoredHost = NewHost(restoredWorld, graph,
                    new TestAdapter { SlotId = "seat", MoveComplete = true });

                Assert.IsTrue(restoredHost.TryRestoreCheckpoint(savedAction, restoredWorld.TickIndex),
                    restoredHost.LastRestoreFailure);
                Assert.AreEqual(GraphStatus.Success, restoredHost.Tick(restoredWorld.TickIndex));
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void RestoreRejectsActionRevisionThatDiffersFromCompiledSubgraph()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var host = NewHost(world, graph,
                    new TestAdapter { SlotId = "seat", MoveComplete = false });
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                var savedWorld = WorldPersistence.Capture(world);
                var savedAction = host.CaptureCheckpoint();
                Assert.IsFalse(string.IsNullOrEmpty(savedAction.Actions[0].GraphRevision));
                savedAction.Actions[0].GraphRevision = "different-revision";
                var restoredWorld = WorldPersistence.Restore(savedWorld);
                var restoredHost = NewHost(restoredWorld, graph, new TestAdapter());

                Assert.IsFalse(restoredHost.TryRestoreCheckpoint(savedAction,
                    restoredWorld.TickIndex));
                StringAssert.Contains("revision", restoredHost.LastRestoreFailure.ToLowerInvariant());
                Assert.IsFalse(restoredWorld.Resources.Tokens.ContainsKey(
                    savedAction.Actions[0].ResourceTokenIds[0]));
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void MissingExecutorProducesSpecificFailureDiagnostic()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var compiled = GraphCompiler.Compile(graph);
                Assert.IsTrue(compiled.Success);
                var host = new ActionGraphHost(world,
                    new BehaviorInstance(compiled.BehaviorPlan, "agent", "behavior-1"),
                    new Dictionary<string, ActionDefinition>
                    {
                        ["test.action"] = new ActionDefinition
                        { Id = "test.action", ExecutorId = "missing", TimeoutSeconds = 10 }
                    },
                    new Dictionary<string, IWorldActionAdapter>());

                Assert.AreEqual(GraphStatus.Failure, host.Tick(0));
                Assert.AreEqual(ActionFailureCode.MissingAdapter, host.LastFinishedAction.FailureCode);
                Assert.AreEqual(ActionFailureCode.MissingAdapter, host.LastFailure.Code);
                StringAssert.Contains("missing", host.LastFailure.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void MissingActorCancelsRunningActionAndReportsUnknownActor()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                world.Agents.Remove("agent");

                Assert.AreEqual(GraphStatus.Failure, host.Tick(0));
                Assert.IsFalse(adapter.Token.Active);
                Assert.AreEqual(ActionFailureCode.UnknownActor, host.LastFinishedAction.FailureCode);
                Assert.AreEqual(ActionFailureCode.UnknownActor, host.LastFailure.Code);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void ExitHookExceptionStillReleasesToken()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter
                { SlotId = "seat", MoveComplete = false, ThrowOnExit = true };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));

                host.Cancel(0);
                Assert.IsFalse(adapter.Token.Active);
                Assert.AreEqual(ActionPhase.Cancelled, host.LastFinishedAction.Phase);
                Assert.AreEqual("Exit hook failure", host.LastAdapterException.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void GraphCallbackExceptionCancelsCompositeResource()
        {
            var world = NewWorld();
            var graph = ScriptableObject.CreateInstance<GraphDocument>();
            try
            {
                graph.DocumentId = "graph.actions.condition_failure";
                graph.Kind = GraphKind.Behavior;
                graph.Nodes.Add(new GraphNode { Id = "root", Type = GraphNodeType.Root });
                graph.Nodes.Add(new GraphNode { Id = "sequence", Type = GraphNodeType.Sequence });
                var action = new GraphNode { Id = "reserve", Type = GraphNodeType.Action };
                action.Set("actionId", "test.action");
                action.Set("targetId", "seat");
                graph.Nodes.Add(action);
                var condition = new GraphNode { Id = "condition", Type = GraphNodeType.Condition };
                condition.Set("conditionId", "test.condition");
                graph.Nodes.Add(condition);
                graph.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "sequence" });
                graph.Edges.Add(new GraphEdge
                { FromNodeId = "sequence", ToNodeId = "reserve", Order = 0 });
                graph.Edges.Add(new GraphEdge
                { FromNodeId = "sequence", ToNodeId = "condition", Order = 1 });
                var compiled = GraphCompiler.Compile(graph);
                Assert.IsTrue(compiled.Success);
                var adapter = new TestAdapter
                { SlotId = "seat", UseCompositeScope = true, MoveComplete = true };
                var host = new ActionGraphHost(world,
                    new BehaviorInstance(compiled.BehaviorPlan, "agent", "behavior-1"),
                    new Dictionary<string, ActionDefinition>
                    {
                        ["test.action"] = new ActionDefinition
                        { Id = "test.action", ExecutorId = "test.executor", TimeoutSeconds = 10 }
                    },
                    new Dictionary<string, IWorldActionAdapter> { ["test.executor"] = adapter },
                    (context, id) => throw new InvalidOperationException("Condition callback failure"));

                Assert.AreEqual(GraphStatus.Failure, host.Tick(0));
                Assert.IsFalse(adapter.Token.Active);
                Assert.AreEqual(ActionFailureCode.GraphRuntimeException, host.LastFailure.Code);
                StringAssert.Contains("Condition callback failure", host.LastFailure.Message);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void CompositeSeatSurvivesLeafAndReleasesWhenGraphFinishes()
        {
            var world = NewWorld();
            var graph = NewGraph(true);
            try
            {
                var adapter = new TestAdapter
                { SlotId = "seat", UseCompositeScope = true, MoveComplete = true };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                Assert.IsTrue(adapter.Token.Active);
                Assert.AreEqual(host.Scope.Id, adapter.Token.OwnerScopeId);
                Assert.AreEqual(1, adapter.EffectCount);
                Assert.AreEqual(0, host.ActiveActionCount);
                world.StepOne();
                Assert.AreEqual(GraphStatus.Success, host.Tick(world.TickIndex));
                Assert.IsFalse(adapter.Token.Active);
                Assert.IsTrue(host.Scope == null);
                Assert.AreEqual(1, adapter.EffectCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void CancelInvalidatesLateCallbackAndReleasesLocalToken()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                Assert.IsTrue(adapter.Token.Active);
                var pending = adapter.PendingContext;
                var generation = pending.Instance.CancellationGeneration;
                host.Cancel(0);
                Assert.IsFalse(adapter.Token.Active);
                Assert.AreEqual(ActionPhase.Cancelled, host.LastFinishedAction.Phase);
                Assert.AreEqual(ActionFailureCode.Cancelled, host.LastFinishedAction.FailureCode);
                Assert.IsFalse(pending.TryCommitEffect("late", generation, () => adapter.EffectCount++));
                Assert.AreEqual(0, adapter.EffectCount);
                Assert.AreEqual(1, adapter.ExitCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void TimeoutUsesSimulationClockAndCleansUp()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter, 2);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                world.StepOne();
                Assert.AreEqual(GraphStatus.Running, host.Tick(world.TickIndex));
                world.StepOne();
                Assert.AreEqual(GraphStatus.Failure, host.Tick(world.TickIndex));
                Assert.AreEqual(ActionFailureCode.TimedOut, host.LastFinishedAction.FailureCode);
                Assert.IsFalse(adapter.Token.Active);
                Assert.AreEqual(1, adapter.ExitCount);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void DestroyedResourceTargetFailsBeforeAdapterCanReportSuccess()
        {
            var world = NewWorld(); var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                world.Resources.RemoveSlot("seat");
                adapter.MoveComplete = true;
                Assert.AreEqual(GraphStatus.Failure, host.Tick(1));
                Assert.AreEqual(ActionFailureCode.InvalidTarget, host.LastFinishedAction.FailureCode);
                Assert.AreEqual(0, adapter.EffectCount);
                Assert.AreEqual(0, world.Resources.Tokens.Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void ClosingTargetRegionFailsActionWithoutPermanentlyDisablingItsSlot()
        {
            var world = NewWorld(); var graph = NewGraph();
            try
            {
                var adapter = new TestAdapter { SlotId = "seat", MoveComplete = false };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Running, host.Tick(0));
                world.Resources.SetRegionAvailability("seat", false);
                Assert.IsTrue(world.Resources.Slots["seat"].Active);
                adapter.MoveComplete = true;
                Assert.AreEqual(GraphStatus.Failure, host.Tick(1));
                Assert.AreEqual(ActionFailureCode.InvalidTarget, host.LastFinishedAction.FailureCode);
                Assert.AreEqual(0, adapter.EffectCount);
                Assert.AreEqual(0, world.Resources.Tokens.Count);
                world.Resources.SetRegionAvailability("seat", true);
                Assert.IsNotNull(world.Resources.TryAcquire("seat", "new-action", "agent", 10));
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void NonFiniteActionTimeoutIsRejectedBeforeReservingResources()
        {
            var world = NewWorld(); var graph = NewGraph();
            try
            {
                var host = NewHost(world, graph, new TestAdapter { SlotId = "seat" }, double.NaN);
                Assert.AreEqual(GraphStatus.Failure, host.Tick(0));
                Assert.AreEqual(ActionFailureCode.InvalidActionDefinition, host.LastFinishedAction.FailureCode);
                Assert.AreEqual(0, world.Resources.Tokens.Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }

        [Test]
        public void SessionTransferOutlivesActionAndScopeCleanup()
        {
            var world = NewWorld();
            var graph = NewGraph();
            try
            {
                var session = world.Activities.Create("meeting", "agent", "room", 0, 10, 60, 1, 2);
                var adapter = new TestAdapter
                { SlotId = "seat", UseCompositeScope = true, MoveComplete = true, TransferToSession = true, SessionId = session.Id };
                var host = NewHost(world, graph, adapter);
                Assert.AreEqual(GraphStatus.Success, host.Tick(0));
                Assert.IsTrue(adapter.Token.Active);
                Assert.AreEqual(session.Id, adapter.Token.OwnerScopeId);
                Assert.AreEqual(adapter.Token.Id, session.Participants[0].ResourceTokenId);
                Assert.IsNull(host.Scope);
                world.Activities.Cancel(session.Id, "meeting cancelled");
                Assert.IsFalse(adapter.Token.Active);
            }
            finally { UnityEngine.Object.DestroyImmediate(graph); }
        }
    }
}
