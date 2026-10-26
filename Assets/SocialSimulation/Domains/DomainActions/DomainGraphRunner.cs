using System;
using System.Collections.Generic;
using SocialSimulation.Actions;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using UnityEngine;

namespace SocialSimulation.Domains.DomainActions
{
    [Serializable]
    public sealed class DomainGraphRunCheckpoint
    {
        public string RunKey;
        public string GraphKey;
        public ActionGraphCheckpoint Graph;
    }

    /// <summary>Runs authored business graphs on the simulation clock and restores active action hosts.</summary>
    public sealed class DomainGraphRunner : IWorldSystem, ISocialActorRuntimeDiagnostics, ISocialGraphHost
    {
        private sealed class Run
        {
            public string GraphKey;
            public ActionGraphHost Host;
        }

        private readonly SimulationWorld world;
        private readonly IReadOnlyDictionary<string, ActionDefinition> definitions;
        private readonly IReadOnlyDictionary<string, IWorldActionAdapter> adapters;
        private readonly IWorldActionPresentation presentation;
        private readonly Action<bool> restartStateChanged;
        private readonly Dictionary<string, CompiledBehaviorPlan> plans =
            new Dictionary<string, CompiledBehaviorPlan>(StringComparer.Ordinal);
        private readonly Dictionary<string, Run> runs = new Dictionary<string, Run>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> actionRegions = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> requiredActions = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, CompiledBehaviorPlan> availablePlans = new Dictionary<string, CompiledBehaviorPlan>(StringComparer.Ordinal);

        public int Order => 25;
        public int ActiveCount => runs.Count;
        public bool Enabled { get; set; } = true;
        public event Action<string, ActionFailureDiagnostic> Failed;

        public DomainGraphRunner(SimulationWorld simulation,
            IReadOnlyDictionary<string, ActionDefinition> actionDefinitions,
            IReadOnlyDictionary<string, IWorldActionAdapter> actionAdapters,
            IWorldActionPresentation actionPresentation = null, Action<bool> onRestartStateChanged = null)
        {
            world = simulation ?? throw new ArgumentNullException(nameof(simulation));
            definitions = actionDefinitions ?? throw new ArgumentNullException(nameof(actionDefinitions));
            adapters = actionAdapters ?? throw new ArgumentNullException(nameof(actionAdapters));
            presentation = actionPresentation;
            restartStateChanged = onRestartStateChanged;
        }

        /// <summary>A missing asset uses the same single-action or sequence graph as the demo generator.</summary>
        public void RegisterGraph(string key, GraphDocument document, string fallbackDocumentId,
            params string[] requiredActionIds)
        {
            if (string.IsNullOrWhiteSpace(key) || plans.ContainsKey(key))
                throw new InvalidOperationException("Domain graph key is empty or duplicated: " + key);
            if (requiredActionIds == null || requiredActionIds.Length == 0)
                throw new ArgumentException("A domain graph needs at least one business action.");
            var temporary = document == null;
            if (temporary) document = CreateDefault(fallbackDocumentId, requiredActionIds);
            try
            {
                var compiled = Compile(document, requiredActionIds);
                foreach (var registered in plans.Values)
                    if (registered.DocumentId == compiled.DocumentId)
                        throw new InvalidOperationException("Domain graph document ID is duplicated: " + compiled.DocumentId);
                plans.Add(key, compiled);
                requiredActions.Add(key, (string[])requiredActionIds.Clone());
                availablePlans[PlanKey(compiled.DocumentId, compiled.Version, compiled.Revision)] = compiled;
            }
            finally
            {
                if (temporary)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(document);
                    else UnityEngine.Object.DestroyImmediate(document);
                }
            }
        }

        public bool Start(string runKey, string graphKey, string actorId)
        {
            if (string.IsNullOrWhiteSpace(runKey) || !plans.TryGetValue(graphKey ?? "", out var plan) ||
                !world.Agents.ContainsKey(actorId ?? "")) return false;
            if (runs.ContainsKey(runKey)) return false;
            runs.Add(runKey, new Run
            {
                GraphKey = graphKey,
                Host = NewHost(new BehaviorInstance(plan, actorId))
            });
            return true;
        }

        public void Tick(SimulationWorld simulation, double deltaSeconds)
        {
            if (!ReferenceEquals(simulation, world))
                throw new InvalidOperationException("Domain graph runner is attached to another world.");
            if (!Enabled || runs.Count == 0) return;
            var keys = new List<string>(runs.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (!runs.TryGetValue(key, out var run)) continue;
                var result = run.Host.Tick(world.TickIndex);
                if (result == GraphStatus.Running) continue;
                runs.Remove(key);
                if (result == GraphStatus.Failure) Failed?.Invoke(key, run.Host.LastFailure);
            }
        }

        public List<DomainGraphRunCheckpoint> Capture()
        {
            var result = new List<DomainGraphRunCheckpoint>();
            foreach (var pair in runs)
                result.Add(new DomainGraphRunCheckpoint
                { RunKey = pair.Key, GraphKey = pair.Value.GraphKey,
                    Graph = pair.Value.Host.CaptureCheckpoint() });
            result.Sort((a, b) => StringComparer.Ordinal.Compare(a.RunKey, b.RunKey));
            return result;
        }

        /// <summary>Call against the candidate restored world during validation, then again on the installed world.</summary>
        public void Restore(IEnumerable<DomainGraphRunCheckpoint> checkpoints)
        {
            if (runs.Count != 0) throw new InvalidOperationException("Domain graph runner must be fresh for restore.");
            if (checkpoints == null) throw new InvalidOperationException("Domain graph checkpoints are missing.");
            foreach (var saved in checkpoints)
            {
                if (saved == null || string.IsNullOrWhiteSpace(saved.RunKey) ||
                    saved.Graph == null || saved.Graph.Behavior == null ||
                    !plans.TryGetValue(saved.GraphKey ?? "", out var current) ||
                    current.DocumentId != saved.Graph.Behavior.GraphId ||
                    !availablePlans.TryGetValue(PlanKey(saved.Graph.Behavior.GraphId,
                        saved.Graph.Behavior.Version, saved.Graph.Behavior.Revision), out var plan) ||
                    !world.Agents.ContainsKey(saved.Graph.ActorId ?? "") ||
                    plan.DocumentId != saved.Graph.Behavior.GraphId ||
                    plan.Version != saved.Graph.Behavior.Version ||
                    plan.Revision != saved.Graph.Behavior.Revision ||
                    runs.ContainsKey(saved.RunKey))
                    throw new InvalidOperationException("Saved domain graph identity is invalid: " + saved?.RunKey);
                var behavior = new BehaviorInstance(plan, saved.Graph.ActorId, saved.Graph.GraphInstanceId);
                var host = NewHost(behavior);
                if (!host.TryRestoreCheckpoint(saved.Graph, world.TickIndex))
                    throw new InvalidOperationException("Domain graph checkpoint rejected: " +
                        saved.RunKey + " " + host.LastRestoreFailure);
                runs.Add(saved.RunKey, new Run { GraphKey = saved.GraphKey, Host = host });
            }
        }

        public void CancelAll()
        {
            foreach (var run in runs.Values) run.Host.Cancel(world.TickIndex);
            runs.Clear();
        }

        public bool Cancel(string runKey)
        {
            if (!runs.TryGetValue(runKey ?? "", out var run)) return false;
            run.Host.Cancel(world.TickIndex);
            runs.Remove(runKey);
            return true;
        }

        /// <summary>Bind the business target of an action, including while its graph is waiting before that action.</summary>
        public void BindActionRegion(string actionId, string regionId)
        {
            if (string.IsNullOrEmpty(actionId) || !definitions.ContainsKey(actionId) ||
                string.IsNullOrEmpty(regionId) || !world.Places.ContainsKey(regionId))
                throw new ArgumentException("Action region binding requires a registered action and region.");
            if (runs.Count != 0) throw new InvalidOperationException("Bind action regions before starting graphs.");
            actionRegions[actionId] = regionId;
        }

        /// <summary>Synchronously drop affected hosts so a paused world can be saved immediately after region removal.</summary>
        public IReadOnlyList<string> CancelRegion(string regionId)
        {
            var affected = new List<string>();
            if (string.IsNullOrEmpty(regionId)) return affected;
            foreach (var pair in runs)
                if (UsesRegion(pair.Value.Host.Behavior.Plan, pair.Value.Host.Behavior.Plan.RootId, regionId))
                    affected.Add(pair.Key);
            affected.Sort(StringComparer.Ordinal);
            foreach (var key in affected) Cancel(key);
            return affected;
        }

        private bool UsesRegion(CompiledBehaviorPlan plan, string nodeId, string regionId)
        {
            var node = plan.Nodes[nodeId];
            if (node.Type == GraphNodeType.Action &&
                actionRegions.TryGetValue(node.Get("actionId"), out var boundRegion) && boundRegion == regionId) return true;
            if (node.Type == GraphNodeType.Subtree && node.Subtree != null &&
                UsesRegion(node.Subtree, node.Subtree.RootId, regionId)) return true;
            foreach (var child in node.Children) if (UsesRegion(plan, child, regionId)) return true;
            return false;
        }

        public IReadOnlyList<ActorRuntimeSnapshot> GetActorRuntimeSnapshots(string actorId)
        {
            var result = new List<ActorRuntimeSnapshot>();
            foreach (var run in runs.Values)
                if (run.Host.Behavior.ActorId == actorId)
                    result.AddRange(run.Host.CaptureRuntimeSnapshots());
            return result;
        }

        public void CopyAvailablePlansTo(DomainGraphRunner target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            foreach (var pair in plans)
                if (target.plans.TryGetValue(pair.Key, out var destination) && destination.DocumentId == pair.Value.DocumentId)
                    target.plans[pair.Key] = pair.Value;
            foreach (var pair in availablePlans) target.availablePlans[pair.Key] = pair.Value;
        }

        public bool TryGetGraphState(string documentId, GraphKind kind, out GraphRuntimeState state)
        {
            state = null;
            if (kind != GraphKind.Behavior) return false;
            foreach (var pair in plans)
            {
                if (pair.Value.DocumentId != documentId) continue;
                var active = new HashSet<string>(StringComparer.Ordinal);
                foreach (var run in runs.Values)
                    if (run.GraphKey == pair.Key)
                    {
                        var plan = run.Host.Behavior.Plan;
                        active.Add(PlanKey(plan.DocumentId, plan.Version, plan.Revision));
                    }
                var versions = new List<string>(active); versions.Sort(StringComparer.Ordinal);
                state = new GraphRuntimeState(documentId, kind, pair.Value.Version, pair.Value.Revision, versions);
                return true;
            }
            return false;
        }

        public bool TryApplyGraph(GraphDocument draft, GraphApplyMode mode, out string error)
        {
            error = null;
            if (draft == null || draft.Kind != GraphKind.Behavior)
            { error = "请选择当前领域的行为图。"; return false; }
            string key = null;
            foreach (var pair in plans) if (pair.Value.DocumentId == draft.DocumentId) { key = pair.Key; break; }
            if (key == null) { error = "当前领域未注册该图 ID。"; return false; }
            if (mode != GraphApplyMode.ForNewInstances && mode != GraphApplyMode.RestartAffected)
            { error = "无效的图应用方式。"; return false; }
            if (mode == GraphApplyMode.RestartAffected && !world.Paused)
            { error = "重启现有行为前请先暂停模拟。"; return false; }
            CompiledBehaviorPlan compiled;
            try { compiled = Compile(draft, requiredActions[key]); }
            catch (Exception exception) { error = exception.Message; return false; }
            plans[key] = compiled;
            availablePlans[PlanKey(compiled.DocumentId, compiled.Version, compiled.Revision)] = compiled;
            if (mode == GraphApplyMode.ForNewInstances) return true;
            var affected = new List<string>();
            foreach (var pair in runs) if (pair.Value.GraphKey == key) affected.Add(pair.Key);
            affected.Sort(StringComparer.Ordinal);
            restartStateChanged?.Invoke(true);
            try
            {
                foreach (var runKey in affected)
                {
                    var old = runs[runKey];
                    var actor = old.Host.Behavior.ActorId;
                    old.Host.Cancel(world.TickIndex);
                    runs[runKey] = new Run { GraphKey = key, Host = NewHost(new BehaviorInstance(compiled, actor)) };
                }
            }
            finally { restartStateChanged?.Invoke(false); }
            return true;
        }

        private CompiledBehaviorPlan Compile(GraphDocument document, IEnumerable<string> required)
        {
            var result = GraphCompiler.Compile(document);
            if (!result.Success || result.BehaviorPlan == null)
                throw new InvalidOperationException("Domain graph failed compilation: " + document.name + " " + string.Join("; ", result.Diagnostics));
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var guaranteed = CountActions(result.BehaviorPlan, result.BehaviorPlan.RootId, counts);
            foreach (var actionId in required)
            {
                if (!counts.TryGetValue(actionId, out var count) || count != 1)
                    throw new InvalidOperationException("Domain graph must contain action exactly once: " + actionId);
                if (!guaranteed.Contains(actionId))
                    throw new InvalidOperationException("Every successful domain graph path must execute action: " + actionId);
            }
            return result.BehaviorPlan;
        }

        private HashSet<string> CountActions(CompiledBehaviorPlan plan, string nodeId, Dictionary<string, int> counts)
        {
            var node = plan.Nodes[nodeId];
            if (node.Type == GraphNodeType.Subtree && node.Subtree != null)
                return CountActions(node.Subtree, node.Subtree.RootId, counts);
            if (node.Type == GraphNodeType.Condition)
                throw new InvalidOperationException("Domain graphs support BlackboardCheck conditions; no external condition executor is registered.");
            var requiredOnSuccess = new HashSet<string>(StringComparer.Ordinal);
            if (node.Type == GraphNodeType.Action)
            {
                var actionId = node.Get("actionId");
                if (!definitions.TryGetValue(actionId, out var definition) || definition == null || !adapters.ContainsKey(definition.ExecutorId))
                    throw new InvalidOperationException("Domain graph has an unbound action: " + actionId);
                counts[actionId] = counts.TryGetValue(actionId, out var old) ? old + 1 : 1;
                requiredOnSuccess.Add(actionId);
            }
            bool alternatives = node.Type == GraphNodeType.Selector || node.Type == GraphNodeType.ReactiveSelector;
            bool first = true;
            foreach (var child in node.Children)
            {
                var childActions = CountActions(plan, child, counts);
                if (!alternatives || first) requiredOnSuccess.UnionWith(childActions);
                else requiredOnSuccess.IntersectWith(childActions);
                first = false;
            }
            return requiredOnSuccess;
        }

        private static string PlanKey(string document, int version, string revision) => document + "@" + version + "#" + revision;

        private ActionGraphHost NewHost(BehaviorInstance behavior)
        { return new ActionGraphHost(world, behavior, definitions, adapters, null, presentation); }

        public static GraphDocument CreateDefault(string documentId, params string[] actionIds)
        {
            if (string.IsNullOrWhiteSpace(documentId))
                throw new ArgumentException("A fallback graph needs a stable document ID.");
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.name = documentId;
            document.DocumentId = documentId;
            document.Kind = GraphKind.Behavior;
            document.Nodes.Add(new GraphNode { Id = "root", Type = GraphNodeType.Root });
            var parent = "root";
            if (actionIds.Length > 1)
            {
                document.Nodes.Add(new GraphNode { Id = "sequence", Type = GraphNodeType.Sequence });
                document.Edges.Add(new GraphEdge { FromNodeId = "root", ToNodeId = "sequence" });
                parent = "sequence";
            }
            for (var i = 0; i < actionIds.Length; i++)
            {
                var node = new GraphNode { Id = "action." + i, Type = GraphNodeType.Action };
                node.Set("actionId", actionIds[i]);
                document.Nodes.Add(node);
                document.Edges.Add(new GraphEdge
                { FromNodeId = parent, ToNodeId = node.Id, Order = i });
            }
            return document;
        }
    }
}
