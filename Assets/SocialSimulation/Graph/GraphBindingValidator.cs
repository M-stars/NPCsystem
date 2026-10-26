using System;
using System.Collections.Generic;
using SocialSimulation.Core;

namespace SocialSimulation.Graph
{
    /// <summary>The target world supplies its actual action definitions, executors and capabilities.</summary>
    public interface IRuntimeGraphBindings
    {
        ActionDefinition ResolveAction(string actionId);
        bool HasExecutor(string executorId);
        bool HasCapability(string actorId, string capabilityId);
        bool CanEvaluateCondition(string conditionId);
        bool CanExecuteRuleCommand(string commandId);
    }

    public sealed class GraphBindingResult
    {
        public readonly IReadOnlyList<GraphDiagnostic> Diagnostics;
        public bool Success { get; private set; }

        internal GraphBindingResult(List<GraphDiagnostic> diagnostics)
        {
            Diagnostics = diagnostics.AsReadOnly();
            Success = diagnostics.Count == 0;
        }
    }

    /// <summary>Run after structural compilation and before publishing a plan to a particular host.</summary>
    public static class GraphBindingValidator
    {
        public static GraphBindingResult ValidateBehavior(CompiledBehaviorPlan plan,
            IRuntimeGraphBindings bindings, IEnumerable<string> actorIds = null)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (bindings == null) throw new ArgumentNullException("bindings");
            var diagnostics = new List<GraphDiagnostic>();
            var actors = actorIds == null ? Array.Empty<string>() : new List<string>(actorIds).ToArray();
            var visited = new HashSet<CompiledBehaviorPlan>();
            ValidatePlan(plan, bindings, actors, visited, diagnostics);
            return new GraphBindingResult(diagnostics);
        }

        public static GraphBindingResult ValidateRule(CompiledRulePlan plan, IRuntimeGraphBindings bindings)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (bindings == null) throw new ArgumentNullException("bindings");
            var diagnostics = new List<GraphDiagnostic>();
            foreach (var node in plan.Nodes.Values)
                if (node.Type == GraphNodeType.RuleEffect &&
                    !bindings.CanExecuteRuleCommand(node.Get("commandId")))
                    Error(diagnostics, "MissingRuleCommand", node.Id,
                        "运行宿主未绑定规则命令 " + node.Get("commandId") + "。");
            return new GraphBindingResult(diagnostics);
        }

        private static void ValidatePlan(CompiledBehaviorPlan plan, IRuntimeGraphBindings bindings,
            string[] actors, HashSet<CompiledBehaviorPlan> visited, List<GraphDiagnostic> diagnostics)
        {
            if (!visited.Add(plan)) return;
            foreach (var node in plan.Nodes.Values)
            {
                if (node.Type == GraphNodeType.Subtree && node.Subtree != null)
                    ValidatePlan(node.Subtree, bindings, actors, visited, diagnostics);
                if (node.Type == GraphNodeType.Condition &&
                    !bindings.CanEvaluateCondition(node.Get("conditionId")))
                    Error(diagnostics, "MissingConditionBinding", node.Id,
                        "运行宿主未绑定条件 " + node.Get("conditionId") + "。");
                if (node.Type != GraphNodeType.Action) continue;
                var actionId = node.Get("actionId");
                var definition = bindings.ResolveAction(actionId);
                if (definition == null || definition.Id != actionId)
                {
                    Error(diagnostics, "MissingActionBinding", node.Id,
                        "运行宿主未注册行动 " + actionId + "。");
                    continue;
                }
                if (definition.TimeoutSeconds <= 0 || string.IsNullOrWhiteSpace(definition.ExecutorId) ||
                    !bindings.HasExecutor(definition.ExecutorId))
                    Error(diagnostics, "MissingExecutor", node.Id,
                        "行动 " + actionId + " 缺少有效超时或执行器 " + definition.ExecutorId + "。");
                if (string.IsNullOrEmpty(definition.CapabilityId)) continue;
                foreach (var actorId in actors)
                    if (!bindings.HasCapability(actorId, definition.CapabilityId))
                        Error(diagnostics, "MissingCapability", node.Id,
                            "NPC " + actorId + " 缺少行动 " + actionId + " 所需能力 " +
                            definition.CapabilityId + "。");
            }
        }

        private static void Error(List<GraphDiagnostic> diagnostics, string code, string nodeId, string message)
        { diagnostics.Add(new GraphDiagnostic(GraphDiagnosticSeverity.Error, code, nodeId, message)); }
    }
}
