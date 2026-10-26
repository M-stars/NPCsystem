using System;
using System.Collections.Generic;
using SocialSimulation.Core;
using SocialSimulation.Graph;

namespace SocialSimulation.Actions
{
    /// <summary>Runs one actor's behavior instance and owns its composite resource scope.</summary>
    public sealed class ActionGraphHost : IGraphActionHost
    {
        private sealed class RunningAction
        {
            public ActionInstance Instance;
            public ActionDefinition Definition;
            public IWorldActionAdapter Adapter;
            public IReadOnlyDictionary<string, string> Parameters;
        }

        private readonly SimulationWorld world;
        private readonly BehaviorInstance behavior;
        private readonly Dictionary<string, ActionDefinition> definitions =
            new Dictionary<string, ActionDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, IWorldActionAdapter> adapters =
            new Dictionary<string, IWorldActionAdapter>(StringComparer.Ordinal);
        private readonly Dictionary<string, RunningAction> running =
            new Dictionary<string, RunningAction>(StringComparer.Ordinal);
        private readonly Func<GraphNodeExecutionContext, string, bool> conditionEvaluator;
        private readonly IWorldActionPresentation presentation;
        private ActionScope scope;

        public ActionGraphHost(SimulationWorld world, BehaviorInstance behavior,
            IReadOnlyDictionary<string, ActionDefinition> definitions,
            IReadOnlyDictionary<string, IWorldActionAdapter> adapters,
            Func<GraphNodeExecutionContext, string, bool> conditionEvaluator = null,
            IWorldActionPresentation presentation = null)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            if (adapters == null) throw new ArgumentNullException(nameof(adapters));
            foreach (var pair in definitions)
            {
                if (pair.Value == null) continue;
                this.definitions.Add(pair.Key, new ActionDefinition
                {
                    Id = pair.Value.Id,
                    CapabilityId = pair.Value.CapabilityId,
                    ExecutorId = pair.Value.ExecutorId,
                    TargetKind = pair.Value.TargetKind,
                    ResourceKind = pair.Value.ResourceKind,
                    TimeoutSeconds = pair.Value.TimeoutSeconds,
                    RetryBudget = pair.Value.RetryBudget,
                    Required = pair.Value.Required
                });
            }
            foreach (var pair in adapters) this.adapters.Add(pair.Key, pair.Value);
            this.conditionEvaluator = conditionEvaluator;
            this.presentation = presentation;
        }

        public BehaviorInstance Behavior { get { return behavior; } }
        public ActionScope Scope { get { return scope; } }
        public ActionInstance LastFinishedAction { get; private set; }
        public ActionFailureDiagnostic LastFailure { get; private set; }
        public Exception LastAdapterException { get; private set; }
        public Exception LastPresentationException { get; private set; }
        public string LastRestoreFailure { get; private set; }
        public int ActiveActionCount { get { return running.Count; } }

        public IReadOnlyList<ActorRuntimeSnapshot> CaptureRuntimeSnapshots()
        {
            var result = new List<ActorRuntimeSnapshot>();
            var keys = new List<string>(running.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var action = running[key].Instance;
                result.Add(new ActorRuntimeSnapshot(action.ActorId, action.GraphId,
                    action.GraphVersion, action.GraphRevision, action.GraphInstanceId, action.NodeId,
                    action.ActionId, action.Id, action.Phase, LastFailure));
            }
            if (result.Count == 0)
                result.Add(new ActorRuntimeSnapshot(behavior.ActorId,
                    behavior.Plan.DocumentId, behavior.Plan.Version,
                    behavior.Plan.Revision, behavior.InstanceId, lastFailure: LastFailure));
            return result;
        }

        /// <summary>Capture beside WorldPersistence.Capture at the same simulation Tick.</summary>
        public ActionGraphCheckpoint CaptureCheckpoint()
        {
            var checkpoint = new ActionGraphCheckpoint
            {
                WorldId = world.WorldId,
                WorldTick = world.TickIndex,
                WorldTime = world.Time,
                ActorId = behavior.ActorId,
                GraphInstanceId = behavior.InstanceId,
                ScopeId = scope == null ? null : scope.Id,
                Behavior = behavior.CaptureCheckpoint()
            };
            if (scope != null)
                foreach (var token in world.Resources.Tokens.Values)
                    if (token.Active && token.OwnerScopeId == scope.Id)
                        checkpoint.ScopeTokenIds.Add(token.Id);
            checkpoint.ScopeTokenIds.Sort(StringComparer.Ordinal);
            var keys = new List<string>(running.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var instance = running[key].Instance;
                var saved = new ActionCheckpoint
                {
                    ActionInstanceId = instance.Id,
                    ActorId = instance.ActorId,
                    SessionId = instance.SessionId,
                    GraphInstanceId = instance.GraphInstanceId,
                    GraphId = instance.GraphId,
                    GraphVersion = instance.GraphVersion,
                    GraphRevision = instance.GraphRevision,
                    NodeId = instance.NodeId,
                    ActionId = instance.ActionId,
                    ExecutorId = running[key].Definition.ExecutorId,
                    CapabilityId = running[key].Definition.CapabilityId,
                    TargetKind = running[key].Definition.TargetKind,
                    ResourceKind = running[key].Definition.ResourceKind,
                    TimeoutSeconds = running[key].Definition.TimeoutSeconds,
                    RetryBudget = running[key].Definition.RetryBudget,
                    Required = running[key].Definition.Required,
                    TargetId = instance.TargetId,
                    ScopeId = instance.ScopeId,
                    Phase = instance.Phase,
                    FailureCode = instance.FailureCode,
                    StartedAt = instance.StartedAt,
                    Deadline = instance.Deadline,
                    CancellationGeneration = instance.CancellationGeneration
                };
                foreach (var id in instance.ResourceTokenIds)
                {
                    saved.ResourceTokenIds.Add(id);
                    if (world.Resources.Tokens.TryGetValue(id, out var token) && token.Active)
                        saved.OwnedTokens.Add(new ActionTokenCheckpoint
                        { TokenId = id, OwnerScopeId = token.OwnerScopeId,
                            SlotId = token.SlotId, AgentId = token.AgentId });
                }
                foreach (var effect in instance.CommittedEffects) saved.CommittedEffects.Add(effect);
                saved.ResourceTokenIds.Sort(StringComparer.Ordinal);
                saved.OwnedTokens.Sort((a, b) => StringComparer.Ordinal.Compare(a.TokenId, b.TokenId));
                saved.CommittedEffects.Sort(StringComparer.Ordinal);
                checkpoint.Actions.Add(saved);
            }
            return checkpoint;
        }

        /// <summary>Restore into a fresh host and behavior, after restoring the matching world snapshot.</summary>
        public bool TryRestoreCheckpoint(ActionGraphCheckpoint checkpoint, long tick)
        {
            if (scope != null || running.Count != 0 || behavior.CaptureCheckpoint().Nodes.Count != 0)
                throw new InvalidOperationException("Restore requires a fresh action host and behavior instance.");
            LastRestoreFailure = null;
            if (checkpoint == null || checkpoint.SchemaVersion != 3 ||
                checkpoint.WorldId != world.WorldId || checkpoint.ActorId != behavior.ActorId ||
                checkpoint.WorldTick != world.TickIndex ||
                double.IsNaN(checkpoint.WorldTime) ||
                Math.Abs(checkpoint.WorldTime - world.Time) > 0.000001 ||
                checkpoint.GraphInstanceId != behavior.InstanceId || checkpoint.Behavior == null ||
                checkpoint.Actions == null || checkpoint.ScopeTokenIds == null)
                return RejectCheckpoint(checkpoint, "Checkpoint identity or schema does not match.");
            try
            {
                new BehaviorInstance(behavior.Plan, behavior.ActorId, behavior.InstanceId)
                    .RestoreCheckpoint(checkpoint.Behavior);
            }
            catch (Exception exception)
            {
                return RejectCheckpoint(checkpoint, "Behavior checkpoint is invalid: " + exception.Message);
            }

            var prepared = new Dictionary<string, RunningAction>(StringComparer.Ordinal);
            var savedPhases = new Dictionary<string, ActionPhase>(StringComparer.Ordinal);
            var committed = new HashSet<string>(world.CommittedEffects, StringComparer.Ordinal);
            if ((checkpoint.Actions.Count > 0 || checkpoint.ScopeTokenIds.Count > 0) &&
                string.IsNullOrWhiteSpace(checkpoint.ScopeId))
                return RejectCheckpoint(checkpoint, "Active actions or tokens have no composite scope.");
            var scopeTokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tokenId in checkpoint.ScopeTokenIds)
                if (!scopeTokens.Add(tokenId) ||
                    !ValidToken(tokenId, checkpoint.ScopeId, behavior.ActorId, null))
                    return RejectCheckpoint(checkpoint, "Composite resource token is missing or has a different owner.");
            foreach (var token in world.Resources.Tokens.Values)
                if (token.Active && token.OwnerScopeId == checkpoint.ScopeId &&
                    !scopeTokens.Contains(token.Id))
                    return RejectCheckpoint(checkpoint, "Composite resource token is absent from checkpoint.");
            foreach (var saved in checkpoint.Actions)
            {
                if (!ValidateActionCheckpoint(saved, checkpoint, committed, out var node,
                    out var definition, out var adapter, out var reason))
                    return RejectCheckpoint(checkpoint, reason);
                var key = Key(saved.GraphInstanceId, saved.NodeId);
                if (prepared.ContainsKey(key))
                    return RejectCheckpoint(checkpoint, "Duplicate action node in checkpoint.");
                var instance = RestoredInstance(saved);
                prepared.Add(key, new RunningAction
                { Instance = instance, Definition = definition, Adapter = adapter,
                    Parameters = node.Parameters });
                savedPhases.Add(key, saved.Phase);
            }
            var graphRunning = new HashSet<string>(StringComparer.Ordinal);
            CollectRunningActions(behavior.Plan, checkpoint.Behavior, graphRunning);
            if (!graphRunning.SetEquals(prepared.Keys))
                return RejectCheckpoint(checkpoint, "Graph running nodes and action checkpoints differ.");

            scope = string.IsNullOrEmpty(checkpoint.ScopeId) ? null :
                new ActionScope(world, checkpoint.ScopeId, behavior.ActorId);
            foreach (var pair in prepared) running.Add(pair.Key, pair.Value);
            try
            {
                foreach (var pair in running)
                    if (pair.Value.Adapter is IWorldActionRestoreValidator validator &&
                        !validator.CanRestore(Context(pair.Value, tick)))
                {
                    LastRestoreFailure = "Action scene target is no longer valid.";
                    CloseRun(tick);
                    if (LastFinishedAction != null)
                        LastFailure = new ActionFailureDiagnostic(LastFinishedAction,
                            tick, LastRestoreFailure);
                    return false;
                }
                foreach (var pair in running)
                    if (pair.Value.Adapter is IWorldActionRestoreAdapter restorer)
                        restorer.OnRestored(Context(pair.Value, tick), savedPhases[pair.Key]);
                behavior.RestoreCheckpoint(checkpoint.Behavior);
                foreach (var pair in running)
                    PublishPresentation(pair.Value.Instance,
                        ActionPresentationMoment.Restored, savedPhases[pair.Key]);
                return true;
            }
            catch (Exception exception)
            {
                LastRestoreFailure = "Action adapter or behavior restore failed: " + exception.Message;
                CloseRun(tick);
                if (LastFinishedAction != null)
                    LastFailure = new ActionFailureDiagnostic(LastFinishedAction,
                        tick, LastRestoreFailure);
                return false;
            }
        }

        /// <summary>Use this entry point so all composite resources close when the graph terminates.</summary>
        public GraphStatus Tick(long tick)
        {
            LastFinishedAction = null;
            LastFailure = null;
            LastAdapterException = null;
            if (!world.Agents.ContainsKey(behavior.ActorId))
            {
                Cancel(tick);
                return PreflightFailure(null, null, ActionFailureCode.UnknownActor,
                    "Actor is missing from the simulation world.", tick);
            }
            GraphStatus status;
            try { status = behavior.Tick(this, tick); }
            catch (Exception exception)
            {
                LastAdapterException = exception;
                try { Cancel(tick); }
                catch (Exception cancellationException)
                {
                    LastAdapterException = new AggregateException(exception, cancellationException);
                    CloseRun(tick);
                }
                return PreflightFailure(null, null, ActionFailureCode.GraphRuntimeException,
                    "Graph execution threw: " + exception.Message, tick);
            }
            if (status != GraphStatus.Running)
            {
                var reportedFailure = LastFailure;
                var reportedAction = LastFinishedAction;
                CloseRun(tick);
                if (status == GraphStatus.Failure && reportedFailure != null)
                {
                    LastFailure = reportedFailure;
                    LastFinishedAction = reportedAction;
                }
            }
            return status;
        }

        public void Cancel(long tick)
        {
            LastFinishedAction = null;
            LastFailure = null;
            try { behavior.Cancel(this, tick); }
            finally { CloseRun(tick); }
        }

        public void CloseRun(long tick)
        {
            if (running.Count > 0)
            {
                var keys = new List<string>(running.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var key in keys)
                    if (running.TryGetValue(key, out var entry))
                        Finish(key, entry, ActionPhase.Cancelled, ActionFailureCode.Cancelled, tick);
            }
            if (scope != null)
            {
                scope.Close();
                scope = null;
            }
        }

        public bool TryGetActiveAction(string graphInstanceId, string nodeId, out ActionInstance instance)
        {
            if (running.TryGetValue(Key(graphInstanceId, nodeId), out var entry))
            {
                instance = entry.Instance;
                return true;
            }
            instance = null;
            return false;
        }

        public GraphStatus TickAction(GraphNodeExecutionContext context, string actionId)
        {
            if (!BelongsToBehavior(context) || string.IsNullOrWhiteSpace(actionId))
                return PreflightFailure(context, actionId, ActionFailureCode.InvalidGraphContext,
                    "Graph, node, or action identity does not match this behavior instance.",
                    context == null ? world.TickIndex : context.Tick);
            if (!world.Agents.TryGetValue(context.ActorId, out var agent))
                return PreflightFailure(context, actionId, ActionFailureCode.UnknownActor,
                    "Actor is missing from the simulation world.", context.Tick);
            if (!TryResolveActionNode(context.InstanceId, context.NodeId, out _,
                out var node) || node.Get("actionId") != actionId)
                return PreflightFailure(context, actionId, ActionFailureCode.InvalidGraphContext,
                    "Compiled graph node does not contain the requested action.", context.Tick);
            if (!definitions.TryGetValue(actionId, out var definition))
                return PreflightFailure(context, actionId, ActionFailureCode.UnknownAction,
                    "Action definition is not registered.", context.Tick);
            if (definition.TimeoutSeconds <= 0 || double.IsNaN(definition.TimeoutSeconds) ||
                double.IsInfinity(definition.TimeoutSeconds) || double.IsInfinity(world.Time + definition.TimeoutSeconds) ||
                string.IsNullOrWhiteSpace(definition.ExecutorId))
                return PreflightFailure(context, actionId, ActionFailureCode.InvalidActionDefinition,
                    "Action definition has no valid timeout or executor.", context.Tick);
            if (!adapters.TryGetValue(definition.ExecutorId, out var adapter) || adapter == null)
                return PreflightFailure(context, actionId, ActionFailureCode.MissingAdapter,
                    "Executor adapter is not registered: " + definition.ExecutorId, context.Tick);
            if (!string.IsNullOrEmpty(definition.CapabilityId) &&
                (agent.CapabilityIds == null || !agent.CapabilityIds.Contains(definition.CapabilityId)))
                return PreflightFailure(context, actionId, ActionFailureCode.MissingCapability,
                    "Actor lacks capability: " + definition.CapabilityId, context.Tick);

            var key = Key(context.InstanceId, context.NodeId);
            var target = Get(node.Parameters, "targetId", Get(node.Parameters, "placeId", Get(node.Parameters, "slotId")));
            if (!TargetStillValid(node.Parameters, definition, target))
            {
                if (running.TryGetValue(key, out var invalid))
                    return Finish(key, invalid, ActionPhase.Failed, ActionFailureCode.InvalidTarget, context.Tick);
                return PreflightFailure(context, actionId, ActionFailureCode.InvalidTarget,
                    "Action target is no longer available.", context.Tick);
            }
            if (!running.TryGetValue(key, out var entry))
            {
                if (scope == null) scope = new ActionScope(world, world.NextId(), context.ActorId);
                var parameters = node.Parameters;
                var instance = new ActionInstance(world.NextId(), context.ActorId,
                    Get(parameters, "sessionId"), context.InstanceId, context.GraphId, context.Version,
                    context.Revision, context.NodeId, actionId,
                    Get(parameters, "targetId", Get(parameters, "placeId", Get(parameters, "slotId"))),
                    scope.Id, world.Time, world.Time + definition.TimeoutSeconds);
                entry = new RunningAction { Instance = instance, Definition = definition,
                    Adapter = adapter, Parameters = parameters };
                running.Add(key, entry);
                PublishPresentation(instance, ActionPresentationMoment.Entered);
            }
            else if (entry.Instance.ActionId != actionId)
                return PreflightFailure(context, actionId, ActionFailureCode.InvalidGraphContext,
                    "Running action ID differs from the compiled node.", context.Tick);

            if (world.Time >= entry.Instance.Deadline)
                return Finish(key, entry, ActionPhase.Failed, ActionFailureCode.TimedOut, context.Tick);

            for (int transitions = 0; transitions < 6; transitions++)
            {
                if (entry.Instance.Phase == ActionPhase.Created)
                    AdvancePhase(entry.Instance, ActionPhase.Validating);
                var execution = Context(entry, context.Tick);
                ActionStepResult result;
                try { result = entry.Adapter.Step(execution, entry.Instance.Phase); }
                catch (Exception exception)
                {
                    LastAdapterException = exception;
                    return Finish(key, entry, ActionPhase.Failed, ActionFailureCode.AdapterException, context.Tick);
                }
                if (result == ActionStepResult.Running) return GraphStatus.Running;
                if (result != ActionStepResult.Success)
                {
                    var failure = entry.Instance.FailureCode == ActionFailureCode.None
                        ? DefaultFailure(entry.Instance.Phase) : entry.Instance.FailureCode;
                    return Finish(key, entry, ActionPhase.Failed, failure, context.Tick);
                }
                if (entry.Instance.Phase == ActionPhase.Executing)
                    return Finish(key, entry, ActionPhase.Completed, ActionFailureCode.None, context.Tick);
                AdvancePhase(entry.Instance, NextPhase(entry.Instance.Phase));
            }
            throw new InvalidOperationException("Action phase transition budget exceeded.");
        }

        public bool CheckCondition(GraphNodeExecutionContext context, string conditionId)
        {
            if (!BelongsToBehavior(context) || conditionEvaluator == null ||
                !TryResolveNode(context.InstanceId, context.NodeId, out var plan, out var node) ||
                node.Type != GraphNodeType.Condition || node.Get("conditionId") != conditionId)
                return false;
            return conditionEvaluator(new GraphNodeExecutionContext(plan.DocumentId, plan.Version,
                plan.Revision,
                context.InstanceId, context.ActorId, context.NodeId, context.Tick,
                node.Parameters), conditionId);
        }

        public void CancelAction(GraphNodeExecutionContext context, string actionId)
        {
            if (!BelongsToBehavior(context)) return;
            var key = Key(context.InstanceId, context.NodeId);
            if (running.TryGetValue(key, out var entry) && entry.Instance.ActionId == actionId)
                Finish(key, entry, ActionPhase.Cancelled, ActionFailureCode.Cancelled, context.Tick);
        }

        private GraphStatus PreflightFailure(GraphNodeExecutionContext graphContext,
            string actionId, ActionFailureCode code, string message, long tick)
        {
            var parameters = graphContext == null ? null : graphContext.Parameters;
            var instance = new ActionInstance(world.NextId(), behavior.ActorId,
                Get(parameters, "sessionId"), graphContext == null ? behavior.InstanceId : graphContext.InstanceId,
                graphContext == null ? behavior.Plan.DocumentId : graphContext.GraphId,
                graphContext == null ? behavior.Plan.Version : graphContext.Version,
                graphContext == null ? behavior.Plan.Revision : graphContext.Revision,
                graphContext == null ? "" : graphContext.NodeId, actionId,
                Get(parameters, "targetId", Get(parameters, "placeId", Get(parameters, "slotId"))),
                scope == null ? null : scope.Id, world.Time, world.Time)
            { Phase = ActionPhase.Failed, FailureCode = code, CancellationGeneration = 1 };
            LastFinishedAction = instance;
            LastFailure = new ActionFailureDiagnostic(instance, tick, message);
            return GraphStatus.Failure;
        }

        private bool ValidateActionCheckpoint(ActionCheckpoint saved, ActionGraphCheckpoint graph,
            HashSet<string> committed, out CompiledGraphNode node,
            out ActionDefinition definition, out IWorldActionAdapter adapter, out string reason)
        {
            node = null;
            definition = null;
            adapter = null;
            reason = "Invalid action checkpoint.";
            if (saved == null || string.IsNullOrWhiteSpace(saved.ActionInstanceId) ||
                string.IsNullOrWhiteSpace(saved.GraphInstanceId) || string.IsNullOrWhiteSpace(saved.NodeId) ||
                string.IsNullOrWhiteSpace(saved.ActionId) ||
                saved.ActorId != behavior.ActorId || saved.ScopeId != graph.ScopeId ||
                saved.CancellationGeneration < 0 || saved.Deadline <= saved.StartedAt ||
                saved.StartedAt > world.Time || double.IsNaN(saved.Deadline) ||
                double.IsInfinity(saved.Deadline) || double.IsNaN(saved.StartedAt) ||
                saved.ResourceTokenIds == null || saved.OwnedTokens == null ||
                saved.CommittedEffects == null ||
                saved.Phase < ActionPhase.Created || saved.Phase > ActionPhase.Executing)
                return false;
            if (!TryResolveActionNode(saved.GraphInstanceId, saved.NodeId, out var plan, out node) ||
                plan.DocumentId != saved.GraphId || plan.Version != saved.GraphVersion ||
                plan.Revision != saved.GraphRevision ||
                node.Get("actionId") != saved.ActionId ||
                !definitions.TryGetValue(saved.ActionId, out definition) ||
                string.IsNullOrWhiteSpace(definition.ExecutorId) ||
                definition.ExecutorId != saved.ExecutorId ||
                !SameOptional(definition.CapabilityId, saved.CapabilityId) ||
                !SameOptional(definition.TargetKind, saved.TargetKind) ||
                !SameOptional(definition.ResourceKind, saved.ResourceKind) ||
                definition.TimeoutSeconds != saved.TimeoutSeconds ||
                definition.RetryBudget != saved.RetryBudget ||
                definition.Required != saved.Required ||
                !adapters.TryGetValue(definition.ExecutorId, out adapter) || adapter == null)
            {
                reason = "Action node, graph revision, or adapter no longer matches.";
                return false;
            }
            var target = Get(node.Parameters, "targetId",
                Get(node.Parameters, "placeId", Get(node.Parameters, "slotId")));
            if (!SameOptional(target, saved.TargetId) ||
                !SameOptional(Get(node.Parameters, "sessionId"), saved.SessionId) ||
                !TargetStillValid(node.Parameters, definition, target))
            {
                reason = "Action target is no longer valid.";
                return false;
            }
            var tracked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in saved.ResourceTokenIds)
                if (string.IsNullOrEmpty(id) || !tracked.Add(id)) return false;
            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in saved.OwnedTokens)
            {
                if (token == null || !tracked.Contains(token.TokenId) || !owned.Add(token.TokenId) ||
                    !ValidToken(token.TokenId, token.OwnerScopeId, token.AgentId, token.SlotId) ||
                    token.AgentId != behavior.ActorId)
                {
                    reason = "Action resource token is missing or has a different owner.";
                    return false;
                }
            }
            foreach (var id in tracked)
                if (world.Resources.Tokens.TryGetValue(id, out var token) && token.Active &&
                    !owned.Contains(id))
                {
                    reason = "Active resource token is absent from action checkpoint.";
                    return false;
                }
            var effects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var phase in saved.CommittedEffects)
                if (string.IsNullOrWhiteSpace(phase) || !effects.Add(phase) ||
                    !committed.Contains(saved.ActionInstanceId + "|effect|" + phase))
                {
                    reason = "Action effect keys do not match the world snapshot.";
                    return false;
                }
            return true;
        }

        private bool TryResolveActionNode(string graphInstanceId, string nodeId,
            out CompiledBehaviorPlan plan, out CompiledGraphNode node)
        {
            return TryResolveNode(graphInstanceId, nodeId, out plan, out node) &&
                node.Type == GraphNodeType.Action;
        }

        private bool TryResolveNode(string graphInstanceId, string nodeId,
            out CompiledBehaviorPlan plan, out CompiledGraphNode node)
        {
            plan = behavior.Plan;
            node = null;
            if (string.IsNullOrEmpty(graphInstanceId) || string.IsNullOrEmpty(nodeId)) return false;
            if (graphInstanceId != behavior.InstanceId)
            {
                var prefix = behavior.InstanceId + "/";
                if (!graphInstanceId.StartsWith(prefix, StringComparison.Ordinal)) return false;
                var path = graphInstanceId.Substring(prefix.Length).Split('/');
                foreach (var subtreeId in path)
                {
                    if (string.IsNullOrEmpty(subtreeId) ||
                        !plan.Nodes.TryGetValue(subtreeId, out var subtree) ||
                        subtree.Type != GraphNodeType.Subtree || subtree.Subtree == null)
                        return false;
                    plan = subtree.Subtree;
                }
            }
            return plan.Nodes.TryGetValue(nodeId, out node);
        }

        private bool TargetStillValid(IReadOnlyDictionary<string, string> parameters,
            ActionDefinition definition, string target)
        {
            var place = Get(parameters, "placeId");
            var slot = Get(parameters, "slotId");
            if (!string.IsNullOrEmpty(place) && !world.Places.ContainsKey(place)) return false;
            if (!string.IsNullOrEmpty(slot) &&
                (!world.Resources.Slots.TryGetValue(slot, out var resource) ||
                 !resource.Active || !resource.RegionAvailable || resource.Capacity < 1 ||
                 (!string.IsNullOrEmpty(resource.LongTermAssigneeId) &&
                  resource.LongTermAssigneeId != behavior.ActorId))) return false;
            if (string.IsNullOrEmpty(target)) return true;
            switch ((definition.TargetKind ?? "").ToLowerInvariant())
            {
                case "place": case "region": case "location":
                    return world.Places.ContainsKey(target);
                case "slot": case "resource": case "seat":
                    return world.Resources.Slots.TryGetValue(target, out var targetSlot) &&
                        targetSlot.Active && targetSlot.RegionAvailable && targetSlot.Capacity > 0 &&
                        (string.IsNullOrEmpty(targetSlot.LongTermAssigneeId) ||
                         targetSlot.LongTermAssigneeId == behavior.ActorId);
                case "agent": case "npc":
                    return world.Agents.ContainsKey(target);
                case "task":
                    return world.Tasks.ContainsKey(target);
                default:
                    return true;
            }
        }

        private bool ValidToken(string tokenId, string owner, string actor, string slotId)
        {
            return !string.IsNullOrWhiteSpace(tokenId) && !string.IsNullOrWhiteSpace(owner) &&
                world.Resources.Tokens.TryGetValue(tokenId, out var token) && token.Active &&
                token.OwnerScopeId == owner && token.AgentId == actor &&
                (slotId == null || token.SlotId == slotId) && token.ExpiresAt > world.Time &&
                world.Resources.Slots.TryGetValue(token.SlotId, out var slot) &&
                slot.Active && slot.RegionAvailable && slot.Capacity > 0 &&
                (string.IsNullOrEmpty(slot.LongTermAssigneeId) ||
                 slot.LongTermAssigneeId == actor);
        }

        private static void CollectRunningActions(CompiledBehaviorPlan plan,
            BehaviorCheckpoint checkpoint, HashSet<string> keys)
        {
            foreach (var saved in checkpoint.Nodes)
            {
                var node = plan.Nodes[saved.NodeId];
                if (node.Type == GraphNodeType.Action && saved.Running)
                    keys.Add(Key(checkpoint.InstanceId, saved.NodeId));
                if (saved.Subtree != null)
                    CollectRunningActions(node.Subtree, saved.Subtree, keys);
            }
        }

        private static ActionInstance RestoredInstance(ActionCheckpoint saved)
        {
            var instance = new ActionInstance(saved.ActionInstanceId, saved.ActorId,
                saved.SessionId, saved.GraphInstanceId, saved.GraphId, saved.GraphVersion,
                saved.GraphRevision, saved.NodeId, saved.ActionId, saved.TargetId, saved.ScopeId,
                saved.StartedAt, saved.Deadline)
            {
                // A callback retained by the pre-load scene must never affect the restored run.
                CancellationGeneration = unchecked(saved.CancellationGeneration + 1),
                Phase = ActionPhase.Validating,
                FailureCode = saved.FailureCode
            };
            foreach (var id in saved.ResourceTokenIds) instance.RestoreTokenId(id);
            foreach (var phase in saved.CommittedEffects) instance.MarkEffect(phase);
            return instance;
        }

        private bool RejectCheckpoint(ActionGraphCheckpoint checkpoint, string reason)
        {
            LastRestoreFailure = reason;
            if (checkpoint == null || checkpoint.WorldId != world.WorldId ||
                checkpoint.ActorId != behavior.ActorId ||
                checkpoint.GraphInstanceId != behavior.InstanceId ||
                checkpoint.WorldTick != world.TickIndex ||
                double.IsNaN(checkpoint.WorldTime) ||
                Math.Abs(checkpoint.WorldTime - world.Time) > 0.000001) return false;
            if (checkpoint.Actions != null)
                foreach (var saved in checkpoint.Actions)
                {
                    if (saved == null || saved.ActorId != behavior.ActorId) continue;
                    ReleaseCheckpointTokens(saved.ResourceTokenIds,
                        saved.ActionInstanceId, behavior.ActorId);
                    ReleaseCheckpointTokens(saved.ResourceTokenIds,
                        saved.ScopeId, behavior.ActorId);
                    if (saved.ResourceTokenIds == null || saved.CommittedEffects == null) continue;
                    LastFinishedAction = RestoredInstance(saved);
                    LastFinishedAction.Phase = ActionPhase.Cancelled;
                    LastFinishedAction.FailureCode = ActionFailureCode.Cancelled;
                }
            ReleaseCheckpointTokens(checkpoint.ScopeTokenIds,
                checkpoint.ScopeId, behavior.ActorId);
            if (LastFinishedAction != null)
                LastFailure = new ActionFailureDiagnostic(LastFinishedAction,
                    world.TickIndex, "Checkpoint restore rejected: " + reason);
            else
                PreflightFailure(null, null, ActionFailureCode.RestoreRejected,
                    "Checkpoint restore rejected: " + reason, world.TickIndex);
            return false;
        }

        // A rejected checkpoint has not passed ownership validation. Release only
        // token IDs named by that checkpoint and still held by this actor and scope.
        private void ReleaseCheckpointTokens(IEnumerable<string> tokenIds,
            string scopeId, string actorId)
        {
            if (tokenIds == null || string.IsNullOrEmpty(scopeId)) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in tokenIds)
                if (!string.IsNullOrEmpty(id) &&
                    world.Resources.Tokens.TryGetValue(id, out var token) && token.Active &&
                    token.OwnerScopeId == scopeId && token.AgentId == actorId)
                    ids.Add(id);
            var ordered = new List<string>(ids);
            ordered.Sort(StringComparer.Ordinal);
            foreach (var id in ordered) world.Resources.Release(id, scopeId);
        }

        private GraphStatus Finish(string key, RunningAction entry, ActionPhase finalPhase,
            ActionFailureCode failure, long tick)
        {
            entry.Instance.Phase = finalPhase;
            entry.Instance.FailureCode = failure;
            entry.Instance.CancellationGeneration++;
            var execution = Context(entry, tick);
            try
            {
                entry.Adapter.OnExit(execution, finalPhase == ActionPhase.Completed
                    ? ActionExitReason.Completed : finalPhase == ActionPhase.Cancelled
                        ? ActionExitReason.Cancelled : ActionExitReason.Failed);
            }
            catch (Exception exception) { LastAdapterException = exception; }
            finally
            {
                world.Resources.ReleaseScope(entry.Instance.Id);
                PublishPresentation(entry.Instance, ActionPresentationMoment.Exited,
                    exitReason: finalPhase == ActionPhase.Completed
                        ? ActionExitReason.Completed : finalPhase == ActionPhase.Cancelled
                            ? ActionExitReason.Cancelled : ActionExitReason.Failed);
                running.Remove(key);
                LastFinishedAction = entry.Instance;
                if (finalPhase != ActionPhase.Completed)
                    LastFailure = new ActionFailureDiagnostic(entry.Instance, tick,
                        failure == ActionFailureCode.AdapterException && LastAdapterException != null
                            ? LastAdapterException.Message
                            : failure == ActionFailureCode.TimedOut
                                ? "Action deadline " + entry.Instance.Deadline +
                                  " reached at simulation time " + world.Time + "."
                                : finalPhase == ActionPhase.Cancelled
                                    ? "Action was cancelled."
                                    : "Action failed during " + entry.Instance.Phase + ".");
            }
            return finalPhase == ActionPhase.Completed ? GraphStatus.Success : GraphStatus.Failure;
        }

        private ActionExecutionContext Context(RunningAction entry, long tick)
        {
            return new ActionExecutionContext(world, entry.Instance, entry.Definition, scope,
                entry.Parameters, tick);
        }

        private bool BelongsToBehavior(GraphNodeExecutionContext context)
        {
            return context != null && !string.IsNullOrEmpty(context.InstanceId) &&
                context.ActorId == behavior.ActorId &&
                (context.InstanceId == behavior.InstanceId ||
                 context.InstanceId.StartsWith(behavior.InstanceId + "/", StringComparison.Ordinal)) &&
                TryResolveNode(context.InstanceId, context.NodeId, out var plan, out _) &&
                plan.DocumentId == context.GraphId && plan.Version == context.Version &&
                plan.Revision == context.Revision;
        }

        private static string Key(string graphInstanceId, string nodeId)
        {
            graphInstanceId = graphInstanceId ?? "";
            nodeId = nodeId ?? "";
            return graphInstanceId.Length + ":" + graphInstanceId + nodeId.Length + ":" + nodeId;
        }

        private static string Get(IReadOnlyDictionary<string, string> parameters,
            string key, string fallback = null)
        {
            return parameters != null && parameters.TryGetValue(key, out var value) ? value : fallback;
        }

        private static bool SameOptional(string left, string right)
        {
            return string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
        }

        private void AdvancePhase(ActionInstance instance, ActionPhase next)
        {
            var previous = instance.Phase;
            instance.Phase = next;
            PublishPresentation(instance, ActionPresentationMoment.PhaseChanged, previous);
        }

        private void PublishPresentation(ActionInstance instance,
            ActionPresentationMoment moment, ActionPhase? previousPhase = null,
            ActionExitReason? exitReason = null)
        {
            if (presentation == null) return;
            try
            {
                presentation.OnActionPresentation(new ActionPresentationEvent(
                    instance, moment, previousPhase, exitReason));
            }
            catch (Exception exception) { LastPresentationException = exception; }
        }

        private static ActionPhase NextPhase(ActionPhase phase)
        {
            switch (phase)
            {
                case ActionPhase.Validating: return ActionPhase.Reserving;
                case ActionPhase.Reserving: return ActionPhase.Moving;
                case ActionPhase.Moving: return ActionPhase.Aligning;
                case ActionPhase.Aligning: return ActionPhase.Executing;
                default: throw new InvalidOperationException("Invalid action phase transition: " + phase);
            }
        }

        private static ActionFailureCode DefaultFailure(ActionPhase phase)
        {
            switch (phase)
            {
                case ActionPhase.Validating: return ActionFailureCode.InvalidTarget;
                case ActionPhase.Reserving: return ActionFailureCode.ResourceUnavailable;
                case ActionPhase.Moving: return ActionFailureCode.NavigationFailed;
                case ActionPhase.Aligning: return ActionFailureCode.AlignmentFailed;
                default: return ActionFailureCode.ExecutionFailed;
            }
        }
    }
}
