using System;
using System.Collections.Generic;
using UnityEngine;

namespace SocialSimulation.Graph
{
    public enum GraphStatus { Running, Success, Failure }

    public sealed class GraphTraceEvent
    {
        public readonly string DocumentId;
        public readonly int Version;
        public readonly string Revision;
        public readonly string InstanceId;
        public readonly string ActorId;
        public readonly string NodeId;
        public readonly long Tick;
        public readonly GraphStatus Status;
        public readonly string Reason;

        public GraphTraceEvent(string documentId, int version, string instanceId, string actorId,
            string nodeId, long tick, GraphStatus status, string reason)
            : this(documentId, version, null, instanceId, actorId, nodeId, tick, status, reason) { }

        public GraphTraceEvent(string documentId, int version, string revision, string instanceId, string actorId,
            string nodeId, long tick, GraphStatus status, string reason)
        {
            DocumentId = documentId; Version = version; InstanceId = instanceId; ActorId = actorId;
            Revision = revision;
            NodeId = nodeId; Tick = tick; Status = status; Reason = reason;
        }
    }

    public sealed class GraphNodeExecutionContext
    {
        public readonly string GraphId;
        public readonly int Version;
        public readonly string Revision;
        public readonly string InstanceId;
        public readonly string ActorId;
        public readonly string NodeId;
        public readonly long Tick;
        public readonly IReadOnlyDictionary<string, string> Parameters;

        public GraphNodeExecutionContext(string graphId, int version, string instanceId,
            string actorId, string nodeId, long tick, IReadOnlyDictionary<string, string> parameters)
            : this(graphId, version, null, instanceId, actorId, nodeId, tick, parameters) { }

        public GraphNodeExecutionContext(string graphId, int version, string revision, string instanceId,
            string actorId, string nodeId, long tick, IReadOnlyDictionary<string, string> parameters)
        {
            GraphId = graphId; Version = version; InstanceId = instanceId;
            Revision = revision;
            ActorId = actorId; NodeId = nodeId; Tick = tick; Parameters = parameters;
        }
    }

    public static class GraphTraceHub
    {
        public static event Action<GraphTraceEvent> Published;

        public static void Publish(GraphTraceEvent entry)
        {
            var handler = Published;
            if (handler != null) handler(entry);
        }
    }

    public interface IGraphActionHost
    {
        GraphStatus TickAction(GraphNodeExecutionContext context, string actionId);
        /// <summary>Conditions must be read-only; reactive branch selection may evaluate them before cancellation.</summary>
        bool CheckCondition(GraphNodeExecutionContext context, string conditionId);
        void CancelAction(GraphNodeExecutionContext context, string actionId);
    }

    /// <summary>Optional read-only action guard used before preempting a lower-priority branch.</summary>
    public interface IGraphActionReadinessHost
    {
        /// <summary>
        /// Check capability and stable preconditions without starting an action or reserving resources.
        /// Do not reject solely because the branch being preempted currently holds the required resource.
        /// Hosts without this interface treat an action as eligible and may still fail during TickAction.
        /// </summary>
        bool CanStartAction(GraphNodeExecutionContext context, string actionId);
    }

    [Serializable]
    public sealed class BehaviorCheckpoint
    {
        public int SchemaVersion = 2;
        public string GraphId;
        public int Version;
        public string Revision;
        public string InstanceId;
        public string ActorId;
        public List<BehaviorNodeCheckpoint> Nodes = new List<BehaviorNodeCheckpoint>();
        public List<BlackboardValueCheckpoint> Variables = new List<BlackboardValueCheckpoint>();
    }

    [Serializable]
    public sealed class BehaviorNodeCheckpoint
    {
        public string NodeId;
        public int ChildIndex;
        public int Attempts;
        public long StartTick;
        public long LastSuccessTick;
        public bool Running;
        public bool HasSubtree;
        public string SubtreeJson;

        // JsonUtility traverses recursive field types even when the leaf value is null.
        // Each nested behavior is encoded separately to keep Unity's serialization depth bounded.
        public BehaviorCheckpoint Subtree
        {
            get { return string.IsNullOrEmpty(SubtreeJson) ? null : JsonUtility.FromJson<BehaviorCheckpoint>(SubtreeJson); }
            set
            {
                HasSubtree = value != null;
                SubtreeJson = value == null ? null : JsonUtility.ToJson(value);
            }
        }
    }

    /// <summary>Mutable progress belongs to one actor and one compiled plan, never to a graph asset.</summary>
    public sealed class BehaviorInstance
    {
        private enum BranchReadiness { Success, Failure, Ready }

        private sealed class NodeState
        {
            public int ChildIndex;
            public int Attempts;
            public long StartTick = -1;
            public long LastSuccessTick = long.MinValue;
            public bool Running;
            public BehaviorInstance Subtree;
        }

        private readonly CompiledBehaviorPlan plan;
        private readonly string actorId;
        private readonly string instanceId;
        private readonly GraphBlackboard blackboard;
        private readonly Dictionary<string, NodeState> states = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        public event Action<GraphTraceEvent> Trace;

        public BehaviorInstance(CompiledBehaviorPlan plan, string actorId, string instanceId = null)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentException("An actor ID is required.", "actorId");
            this.plan = plan;
            this.actorId = actorId;
            this.instanceId = string.IsNullOrEmpty(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
            blackboard = new GraphBlackboard(plan.Blackboard);
        }

        public string ActorId { get { return actorId; } }
        public string InstanceId { get { return instanceId; } }
        public CompiledBehaviorPlan Plan { get { return plan; } }
        public GraphBlackboard Blackboard { get { return blackboard; } }

        public BehaviorCheckpoint CaptureCheckpoint()
        {
            var checkpoint = new BehaviorCheckpoint { GraphId = plan.DocumentId, Version = plan.Version,
                Revision = plan.Revision, InstanceId = instanceId, ActorId = actorId,
                Variables = blackboard.CaptureCheckpoint() };
            foreach (var pair in states)
            {
                var state = pair.Value;
                checkpoint.Nodes.Add(new BehaviorNodeCheckpoint { NodeId = pair.Key,
                    ChildIndex = state.ChildIndex, Attempts = state.Attempts,
                    StartTick = state.StartTick, LastSuccessTick = state.LastSuccessTick,
                    Running = state.Running,
                    HasSubtree = state.Subtree != null,
                    Subtree = state.Subtree == null ? null : state.Subtree.CaptureCheckpoint() });
            }
            checkpoint.Nodes.Sort((a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
            return checkpoint;
        }

        /// <summary>Restore into a fresh instance after the host has restored matching actions and resource tokens.</summary>
        public void RestoreCheckpoint(BehaviorCheckpoint checkpoint)
        {
            if (checkpoint == null) throw new ArgumentNullException("checkpoint");
            if (checkpoint.SchemaVersion != 2)
                throw new InvalidOperationException("Unsupported behavior checkpoint schema. Expected version 2.");
            if (states.Count != 0) throw new InvalidOperationException("Restore requires a fresh behavior instance.");
            if (checkpoint.GraphId != plan.DocumentId || checkpoint.Version != plan.Version ||
                checkpoint.Revision != plan.Revision ||
                checkpoint.ActorId != actorId || checkpoint.InstanceId != instanceId)
                throw new InvalidOperationException("Behavior checkpoint does not match graph version, actor, or instance.");
            if (checkpoint.Nodes == null) throw new InvalidOperationException("Behavior checkpoint has no node state list.");
            var pendingBlackboard = new GraphBlackboard(plan.Blackboard);
            pendingBlackboard.RestoreCheckpoint(checkpoint.Variables);
            var pendingStates = new Dictionary<string, NodeState>(StringComparer.Ordinal);
            foreach (var saved in checkpoint.Nodes)
            {
                CompiledGraphNode node;
                if (saved == null || string.IsNullOrEmpty(saved.NodeId) || !plan.Nodes.TryGetValue(saved.NodeId, out node) ||
                    pendingStates.ContainsKey(saved.NodeId) || saved.ChildIndex < 0 ||
                    saved.ChildIndex >= Math.Max(1, node.Children.Count) ||
                    saved.Attempts < 0 || saved.StartTick < -1 ||
                    (saved.Running && node.Type != GraphNodeType.Action) ||
                    (node.Type == GraphNodeType.Retry && saved.Attempts >= ParsePositive(node.Get("attempts"))) ||
                    (node.Type != GraphNodeType.Retry && saved.Attempts != 0))
                    throw new InvalidOperationException("Behavior checkpoint contains an invalid node state.");
                var state = new NodeState { ChildIndex = saved.ChildIndex, Attempts = saved.Attempts,
                    StartTick = saved.StartTick, LastSuccessTick = saved.LastSuccessTick, Running = saved.Running };
                if (saved.HasSubtree != !string.IsNullOrEmpty(saved.SubtreeJson))
                    throw new InvalidOperationException("Checkpoint subtree flag and payload disagree.");
                if (saved.HasSubtree)
                {
                    var savedSubtree = saved.Subtree;
                    if (savedSubtree == null)
                        throw new InvalidOperationException("Checkpoint subtree payload is missing.");
                    if (node.Type != GraphNodeType.Subtree || node.Subtree == null)
                        throw new InvalidOperationException("Checkpoint subtree does not match the compiled node.");
                    state.Subtree = new BehaviorInstance(node.Subtree, actorId, instanceId + "/" + node.Id);
                    state.Subtree.RestoreCheckpoint(savedSubtree);
                    state.Subtree.Trace += ForwardTrace;
                }
                pendingStates.Add(saved.NodeId, state);
            }
            // An invalid record must not leave a partially restored instance behind.
            blackboard.RestoreCheckpoint(checkpoint.Variables);
            foreach (var pair in pendingStates) states.Add(pair.Key, pair.Value);
        }

        public GraphStatus Tick(IGraphActionHost host, long tick)
        {
            if (host == null) throw new ArgumentNullException("host");
            var result = TickNode(plan.RootId, host, tick);
            if (result != GraphStatus.Running)
            {
                ResetTransient();
                blackboard.ClearScope(BlackboardScope.Goal);
                blackboard.ClearScope(BlackboardScope.Action);
            }
            return result;
        }

        public void Cancel(IGraphActionHost host, long tick)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (plan.RootId != null) CancelNode(plan.RootId, host, tick);
            ResetTransient();
            blackboard.ClearScope(BlackboardScope.Goal);
            blackboard.ClearScope(BlackboardScope.Action);
        }

        private GraphStatus TickNode(string id, IGraphActionHost host, long tick)
        {
            var node = plan.Nodes[id];
            var state = State(id);
            GraphStatus result;
            string reason = "";
            switch (node.Type)
            {
                case GraphNodeType.Root:
                    result = TickNode(node.Children[0], host, tick);
                    break;
                case GraphNodeType.Sequence:
                    result = TickSequence(node, state, host, tick);
                    break;
                case GraphNodeType.Selector:
                    result = TickSelector(node, state, host, tick, false);
                    break;
                case GraphNodeType.ReactiveSelector:
                    result = TickSelector(node, state, host, tick, true);
                    break;
                case GraphNodeType.Condition:
                    result = host.CheckCondition(Context(node, tick), node.Get("conditionId"))
                        ? GraphStatus.Success : GraphStatus.Failure;
                    break;
                case GraphNodeType.Action:
                    result = host.TickAction(Context(node, tick), node.Get("actionId"));
                    state.Running = result == GraphStatus.Running;
                    break;
                case GraphNodeType.BlackboardSet:
                    result = blackboard.TrySetRaw(node.Get("variableId"), node.Get("value"))
                        ? GraphStatus.Success : GraphStatus.Failure;
                    break;
                case GraphNodeType.BlackboardCheck:
                    result = blackboard.Check(node.Get("variableId"),
                        node.Get("comparison", "eq"), node.Get("expected"))
                        ? GraphStatus.Success : GraphStatus.Failure;
                    break;
                case GraphNodeType.Wait:
                    if (state.StartTick < 0) state.StartTick = tick;
                    result = tick - state.StartTick >= ParsePositive(node.Get("ticks"))
                        ? GraphStatus.Success : GraphStatus.Running;
                    if (result != GraphStatus.Running) state.StartTick = -1;
                    break;
                case GraphNodeType.Timeout:
                    if (state.StartTick < 0) state.StartTick = tick;
                    if (tick - state.StartTick >= ParsePositive(node.Get("ticks")))
                    {
                        CancelNode(node.Children[0], host, tick);
                        state.StartTick = -1;
                        result = GraphStatus.Failure;
                        reason = "Timeout";
                    }
                    else
                    {
                        result = TickNode(node.Children[0], host, tick);
                        if (result != GraphStatus.Running) state.StartTick = -1;
                    }
                    break;
                case GraphNodeType.Cooldown:
                    var duration = ParsePositive(node.Get("ticks"));
                    if (state.LastSuccessTick != long.MinValue && tick - state.LastSuccessTick < duration)
                    {
                        result = GraphStatus.Failure;
                        reason = "Cooldown";
                    }
                    else
                    {
                        result = TickNode(node.Children[0], host, tick);
                        if (result == GraphStatus.Success) state.LastSuccessTick = tick;
                    }
                    break;
                case GraphNodeType.Retry:
                    result = TickNode(node.Children[0], host, tick);
                    if (result == GraphStatus.Failure)
                    {
                        state.Attempts++;
                        if (state.Attempts < ParsePositive(node.Get("attempts")))
                        {
                            ClearBranch(node.Children[0]);
                            result = GraphStatus.Running;
                            reason = "RetryPending";
                        }
                        else state.Attempts = 0;
                    }
                    else if (result == GraphStatus.Success) state.Attempts = 0;
                    break;
                case GraphNodeType.Subtree:
                    if (state.Subtree == null)
                    {
                        state.Subtree = new BehaviorInstance(node.Subtree, actorId, instanceId + "/" + node.Id);
                        state.Subtree.Trace += ForwardTrace;
                    }
                    result = state.Subtree.Tick(host, tick);
                    break;
                default:
                    result = GraphStatus.Failure;
                    reason = "UnsupportedNode";
                    break;
            }
            Emit(id, tick, result, reason);
            return result;
        }

        private GraphStatus TickSequence(CompiledGraphNode node, NodeState state, IGraphActionHost host, long tick)
        {
            while (state.ChildIndex < node.Children.Count)
            {
                var result = TickNode(node.Children[state.ChildIndex], host, tick);
                if (result == GraphStatus.Running) return result;
                if (result == GraphStatus.Failure) { state.ChildIndex = 0; return result; }
                state.ChildIndex++;
            }
            state.ChildIndex = 0;
            return GraphStatus.Success;
        }

        private GraphStatus TickSelector(CompiledGraphNode node, NodeState state,
            IGraphActionHost host, long tick, bool reactive)
        {
            int previous = state.ChildIndex;
            int start = reactive ? 0 : previous;
            for (int i = start; i < node.Children.Count; i++)
            {
                if (reactive && i < previous)
                {
                    // Never start a competing action while the old branch still owns its resources.
                    // Inspect only the read-only prefix; effects and waits are execution boundaries.
                    if (ProbeBranch(node.Children[i], host, tick) == BranchReadiness.Failure)
                    {
                        Emit(node.Children[i], tick, GraphStatus.Failure, "ReactiveGuardRejected");
                        continue;
                    }
                    CancelNode(node.Children[previous], host, tick);
                    previous = -1;
                }
                var result = TickNode(node.Children[i], host, tick);
                if (result == GraphStatus.Failure) continue;
                if (reactive && previous != i && previous >= 0 && previous < node.Children.Count)
                    CancelNode(node.Children[previous], host, tick);
                state.ChildIndex = result == GraphStatus.Running ? i : 0;
                return result;
            }
            state.ChildIndex = 0;
            return GraphStatus.Failure;
        }

        private BranchReadiness ProbeBranch(string id, IGraphActionHost host, long tick)
        {
            var node = plan.Nodes[id];
            states.TryGetValue(id, out var state);
            switch (node.Type)
            {
                case GraphNodeType.Root:
                    return ProbeBranch(node.Children[0], host, tick);
                case GraphNodeType.Condition:
                    return host.CheckCondition(Context(node, tick), node.Get("conditionId"))
                        ? BranchReadiness.Success : BranchReadiness.Failure;
                case GraphNodeType.BlackboardCheck:
                    return blackboard.Check(node.Get("variableId"), node.Get("comparison", "eq"), node.Get("expected"))
                        ? BranchReadiness.Success : BranchReadiness.Failure;
                case GraphNodeType.Sequence:
                    for (int i = state == null ? 0 : state.ChildIndex; i < node.Children.Count; i++)
                    {
                        var result = ProbeBranch(node.Children[i], host, tick);
                        if (result != BranchReadiness.Success) return result;
                    }
                    return BranchReadiness.Success;
                case GraphNodeType.Selector:
                case GraphNodeType.ReactiveSelector:
                    int start = node.Type == GraphNodeType.ReactiveSelector || state == null ? 0 : state.ChildIndex;
                    for (int i = start; i < node.Children.Count; i++)
                    {
                        var result = ProbeBranch(node.Children[i], host, tick);
                        if (result != BranchReadiness.Failure) return result;
                    }
                    return BranchReadiness.Failure;
                case GraphNodeType.Timeout:
                    if (state != null && state.StartTick >= 0 && tick - state.StartTick >= ParsePositive(node.Get("ticks")))
                        return BranchReadiness.Failure;
                    return ProbeBranch(node.Children[0], host, tick);
                case GraphNodeType.Cooldown:
                    if (state != null && state.LastSuccessTick != long.MinValue &&
                        tick - state.LastSuccessTick < ParsePositive(node.Get("ticks")))
                        return BranchReadiness.Failure;
                    return ProbeBranch(node.Children[0], host, tick);
                case GraphNodeType.Retry:
                    var child = ProbeBranch(node.Children[0], host, tick);
                    if (child == BranchReadiness.Failure &&
                        (state == null ? 0 : state.Attempts) + 1 < ParsePositive(node.Get("attempts")))
                        return BranchReadiness.Ready;
                    return child;
                case GraphNodeType.Subtree:
                    var subtree = state == null ? null : state.Subtree;
                    if (subtree == null)
                        subtree = new BehaviorInstance(node.Subtree, actorId, instanceId + "/" + node.Id);
                    return subtree.ProbeBranch(subtree.plan.RootId, host, tick);
                case GraphNodeType.Action:
                    var readiness = host as IGraphActionReadinessHost;
                    return readiness != null && !readiness.CanStartAction(Context(node, tick), node.Get("actionId"))
                        ? BranchReadiness.Failure : BranchReadiness.Ready;
                // Mutations and asynchronous work cannot be speculatively executed. A guard which
                // protects them must precede them in the graph, or be provided by the action host.
                default:
                    return BranchReadiness.Ready;
            }
        }

        private void CancelNode(string id, IGraphActionHost host, long tick)
        {
            var node = plan.Nodes[id];
            var state = State(id);
            if (node.Type == GraphNodeType.Action && state.Running)
                host.CancelAction(Context(node, tick), node.Get("actionId"));
            if (node.Type == GraphNodeType.Subtree && state.Subtree != null)
                state.Subtree.Cancel(host, tick);
            foreach (var child in node.Children) CancelNode(child, host, tick);
            state.Running = false;
            state.StartTick = -1;
            state.ChildIndex = 0;
            state.Attempts = 0;
            Emit(id, tick, GraphStatus.Failure, "Cancelled");
        }

        private void ClearBranch(string id)
        {
            var node = plan.Nodes[id];
            var state = State(id);
            state.ChildIndex = 0;
            state.Attempts = 0;
            state.StartTick = -1;
            state.Running = false;
            if (state.Subtree != null) state.Subtree.ResetTransient();
            foreach (var child in node.Children) ClearBranch(child);
        }

        private void ResetTransient()
        {
            foreach (var state in states.Values)
            {
                state.ChildIndex = 0;
                state.Attempts = 0;
                state.StartTick = -1;
                state.Running = false;
                if (state.Subtree != null) state.Subtree.ResetTransient();
            }
        }

        private NodeState State(string id)
        {
            NodeState state;
            if (!states.TryGetValue(id, out state))
            {
                state = new NodeState();
                states.Add(id, state);
            }
            return state;
        }

        private void Emit(string id, long tick, GraphStatus status, string reason)
        {
            var entry = new GraphTraceEvent(plan.DocumentId, plan.Version, plan.Revision,
                instanceId, actorId, id, tick, status, reason);
            GraphTraceHub.Publish(entry);
            var trace = Trace;
            if (trace != null) trace(entry);
        }

        private void ForwardTrace(GraphTraceEvent entry)
        {
            var trace = Trace;
            if (trace != null) trace(entry);
        }

        private static int ParsePositive(string value) { return int.Parse(value); }

        private GraphNodeExecutionContext Context(CompiledGraphNode node, long tick)
        {
            return new GraphNodeExecutionContext(plan.DocumentId, plan.Version, plan.Revision, instanceId,
                actorId, node.Id, tick, node.Parameters);
        }
    }
}
