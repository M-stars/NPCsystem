using System;
using System.Collections.Generic;
using SocialSimulation.Core;

namespace SocialSimulation.Graph
{
    public enum GraphCompatibilityState
    {
        Available, OptionalPresentationMissing, MissingActionDefinition, MissingCapability,
        IncompatibleBody, MissingExecutor, MissingRequiredPresentation, InvalidDefinition
    }

    public sealed class GraphCompatibility
    {
        public readonly GraphCompatibilityState State;
        public readonly string Reason;
        public bool CanUse => State == GraphCompatibilityState.Available ||
            State == GraphCompatibilityState.OptionalPresentationMissing;
        public GraphCompatibility(GraphCompatibilityState state, string reason)
        { State = state; Reason = reason; }
    }

    /// <summary>Checks the selected template against definitions actually supplied by a registered catalog.</summary>
    public static class GraphTemplateCompatibility
    {
        public static GraphCompatibility Evaluate(ActionDefinition action, PackageManifest template,
            Func<string, bool> hasExecutor)
        {
            if (action == null) return Result(GraphCompatibilityState.MissingActionDefinition,
                "仅有动作 ID，缺少已注册 ActionDefinition 与执行器绑定，兼容性未知。");
            if (template == null || template.Kind != "NPC" || template.Body == null ||
                string.IsNullOrWhiteSpace(action.Id) || action.TimeoutSeconds <= 0 ||
                double.IsNaN(action.TimeoutSeconds) || double.IsInfinity(action.TimeoutSeconds))
                return Result(GraphCompatibilityState.InvalidDefinition, "模板或动作定义无效。");
            if (string.IsNullOrWhiteSpace(action.ExecutorId) || hasExecutor == null || !hasExecutor(action.ExecutorId))
                return Result(GraphCompatibilityState.MissingExecutor, "缺少执行器：" + action.ExecutorId);
            if (string.IsNullOrWhiteSpace(action.CapabilityId))
                return Result(GraphCompatibilityState.Available, "定义与执行器已注册；动作未要求身体能力。");
            return EvaluateCapability(action.CapabilityId, template, hasExecutor);
        }

        public static GraphCompatibility EvaluateCapability(string capabilityId, PackageManifest template,
            Func<string, bool> hasExecutor)
        {
            CapabilityDefinition capability = null;
            if (template?.Capabilities != null)
                foreach (var item in template.Capabilities)
                    if (item != null && item.Id == capabilityId) { capability = item; break; }
            if (capability == null)
                return Result(GraphCompatibilityState.MissingCapability, "模板缺少能力：" + capabilityId);
            if (!string.IsNullOrEmpty(capability.BodyConstraint) && capability.BodyConstraint != "Any" &&
                capability.BodyConstraint != template.Body?.Rig)
                return Result(GraphCompatibilityState.IncompatibleBody,
                    "能力需要 " + capability.BodyConstraint + " 骨骼，模板为 " + template.Body?.Rig + "。");
            if (string.IsNullOrWhiteSpace(capability.ExecutorId) || hasExecutor == null || !hasExecutor(capability.ExecutorId))
                return Result(GraphCompatibilityState.MissingExecutor, "能力缺少执行器：" + capability.ExecutorId);
            bool presented = false;
            if (template.AnimationBindings != null)
                foreach (var binding in template.AnimationBindings)
                    if (binding != null && binding.CapabilityId == capabilityId && !string.IsNullOrWhiteSpace(binding.AnimatorState))
                    { presented = true; break; }
            if (!presented)
                return capability.RequiresPresentation
                    ? Result(GraphCompatibilityState.MissingRequiredPresentation, "缺少必需动画映射：" + capabilityId)
                    : Result(GraphCompatibilityState.OptionalPresentationMissing,
                        "可执行；能力未要求动画表现，缺少映射时仅执行逻辑：" + capabilityId);
            return Result(GraphCompatibilityState.Available, "能力、骨骼、执行器和动画映射兼容。");
        }

        public static List<GraphDiagnostic> Validate(CompiledBehaviorPlan plan, PackageManifest template,
            Func<string, ActionDefinition> resolveAction, Func<string, bool> hasExecutor)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (resolveAction == null) throw new ArgumentNullException(nameof(resolveAction));
            var diagnostics = new List<GraphDiagnostic>();
            Visit(plan, template, resolveAction, hasExecutor, diagnostics, new HashSet<CompiledBehaviorPlan>());
            return diagnostics;
        }

        private static void Visit(CompiledBehaviorPlan plan, PackageManifest template,
            Func<string, ActionDefinition> resolveAction, Func<string, bool> hasExecutor,
            List<GraphDiagnostic> diagnostics, HashSet<CompiledBehaviorPlan> visited)
        {
            if (!visited.Add(plan)) return;
            foreach (var node in plan.Nodes.Values)
            {
                if (node.Subtree != null) Visit(node.Subtree, template, resolveAction, hasExecutor, diagnostics, visited);
                if (node.Type != GraphNodeType.Action) continue;
                var id = node.Get("actionId");
                var definition = resolveAction(id);
                var result = Evaluate(definition?.Id == id ? definition : null, template, hasExecutor);
                if (result.State == GraphCompatibilityState.Available) continue;
                diagnostics.Add(new GraphDiagnostic(result.CanUse ? GraphDiagnosticSeverity.Warning : GraphDiagnosticSeverity.Error,
                    "Template" + result.State, node.Id,
                    "模板 " + template.Id + " · 图 " + plan.DocumentId + " · 动作 " + id + "：" + result.Reason));
            }
        }

        private static GraphCompatibility Result(GraphCompatibilityState state, string reason)
        { return new GraphCompatibility(state, reason); }
    }
}
