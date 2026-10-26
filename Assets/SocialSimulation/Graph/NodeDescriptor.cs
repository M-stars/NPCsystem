using System;
using System.Collections.Generic;

namespace SocialSimulation.Graph
{
    public sealed class GraphParameterDescriptor
    {
        public readonly string Key;
        public readonly string Label;
        public readonly string DefaultValue;

        public GraphParameterDescriptor(string key, string label, string defaultValue = "")
        { Key = key; Label = label; DefaultValue = defaultValue; }
    }

    public sealed class NodeDescriptor
    {
        public readonly GraphNodeType Type;
        public readonly GraphKind Kind;
        public readonly string Title;
        public readonly string Category;
        public readonly bool HasInput;
        public readonly bool HasOutput;
        public readonly int MinChildren;
        public readonly int MaxChildren;
        public readonly GraphParameterDescriptor[] Parameters;

        private NodeDescriptor(GraphNodeType type, GraphKind kind, string title, string category,
            bool input, bool output, int min, int max, params GraphParameterDescriptor[] parameters)
        {
            Type = type; Kind = kind; Title = title; Category = category;
            HasInput = input; HasOutput = output; MinChildren = min; MaxChildren = max;
            Parameters = parameters;
        }

        private static readonly NodeDescriptor[] AllDescriptors =
        {
            new NodeDescriptor(GraphNodeType.Root, GraphKind.Behavior, "入口", "控制", false, true, 1, 1),
            new NodeDescriptor(GraphNodeType.Sequence, GraphKind.Behavior, "顺序", "控制", true, true, 1, int.MaxValue),
            new NodeDescriptor(GraphNodeType.Selector, GraphKind.Behavior, "选择", "控制", true, true, 1, int.MaxValue),
            new NodeDescriptor(GraphNodeType.ReactiveSelector, GraphKind.Behavior, "响应选择", "控制", true, true, 1, int.MaxValue),
            new NodeDescriptor(GraphNodeType.Condition, GraphKind.Behavior, "条件", "判断", true, false, 0, 0,
                new GraphParameterDescriptor("conditionId", "条件 ID")),
            new NodeDescriptor(GraphNodeType.Action, GraphKind.Behavior, "行动", "行动", true, false, 0, 0,
                new GraphParameterDescriptor("actionId", "行动 ID")),
            new NodeDescriptor(GraphNodeType.Wait, GraphKind.Behavior, "等待", "行动", true, false, 0, 0,
                new GraphParameterDescriptor("ticks", "等待 Tick", "1")),
            new NodeDescriptor(GraphNodeType.Timeout, GraphKind.Behavior, "超时", "控制", true, true, 1, 1,
                new GraphParameterDescriptor("ticks", "超时 Tick", "30")),
            new NodeDescriptor(GraphNodeType.Cooldown, GraphKind.Behavior, "冷却", "控制", true, true, 1, 1,
                new GraphParameterDescriptor("ticks", "冷却 Tick", "10")),
            new NodeDescriptor(GraphNodeType.Retry, GraphKind.Behavior, "有限重试", "控制", true, true, 1, 1,
                new GraphParameterDescriptor("attempts", "最多次数", "2")),
            new NodeDescriptor(GraphNodeType.Subtree, GraphKind.Behavior, "子行为图", "复用", true, false, 0, 0),
            new NodeDescriptor(GraphNodeType.BlackboardSet, GraphKind.Behavior, "设置黑板变量", "黑板", true, false, 0, 0,
                new GraphParameterDescriptor("variableId", "变量 ID"),
                new GraphParameterDescriptor("value", "写入值")),
            new NodeDescriptor(GraphNodeType.BlackboardCheck, GraphKind.Behavior, "检查黑板变量", "黑板", true, false, 0, 0,
                new GraphParameterDescriptor("variableId", "变量 ID"),
                new GraphParameterDescriptor("comparison", "比较 (eq/ne/gt/gte/lt/lte)", "eq"),
                new GraphParameterDescriptor("expected", "期望值")),
            new NodeDescriptor(GraphNodeType.RuleEvent, GraphKind.Rule, "事件", "触发", false, true, 1, int.MaxValue,
                new GraphParameterDescriptor("eventType", "事件类型")),
            new NodeDescriptor(GraphNodeType.RuleScope, GraphKind.Rule, "适用范围", "范围", true, true, 1, int.MaxValue,
                new GraphParameterDescriptor("areaId", "地区 ID（空为任意）"),
                new GraphParameterDescriptor("roleId", "角色 ID（空为任意）"),
                new GraphParameterDescriptor("actionId", "动作 ID（空为任意）"),
                new GraphParameterDescriptor("fromSeconds", "起始仿真秒（含；空为不限）"),
                new GraphParameterDescriptor("untilSeconds", "截止仿真秒（不含；空为不限）")),
            new NodeDescriptor(GraphNodeType.RuleCondition, GraphKind.Rule, "条件", "判断", true, true, 1, int.MaxValue,
                new GraphParameterDescriptor("field", "事件字段（空为仅检查证据）"),
                new GraphParameterDescriptor("valueType", "值类型", "string"),
                new GraphParameterDescriptor("comparison", "比较方式", "equals"),
                new GraphParameterDescriptor("equals", "期望值"),
                new GraphParameterDescriptor("unit", "数值单位（空为无量纲）"),
                new GraphParameterDescriptor("requiresEvidence", "需要目击证据 true/false", "true"),
                new GraphParameterDescriptor("combine", "与前条件 and/or", "and"),
                new GraphParameterDescriptor("negate", "取反 true/false", "false")),
            new NodeDescriptor(GraphNodeType.RuleNorm, GraphKind.Rule, "规范", "声明", true, false, 0, 0,
                new GraphParameterDescriptor("normKind", "obligation/prohibition", "prohibition"),
                new GraphParameterDescriptor("actionId", "目标动作 ID"),
                new GraphParameterDescriptor("priority", "优先级", "0")),
            new NodeDescriptor(GraphNodeType.RuleEffect, GraphKind.Rule, "效果命令", "效果", true, false, 0, 0,
                new GraphParameterDescriptor("commandId", "命令 ID"),
                new GraphParameterDescriptor("target", "目标（actor/witness）", "actor"))
        };

        public static IEnumerable<NodeDescriptor> For(GraphKind kind)
        {
            foreach (var descriptor in AllDescriptors)
                if (descriptor.Kind == kind) yield return descriptor;
        }

        public static NodeDescriptor Get(GraphNodeType type)
        {
            foreach (var descriptor in AllDescriptors)
                if (descriptor.Type == type) return descriptor;
            throw new ArgumentOutOfRangeException("type", type, "Unknown node type.");
        }
    }
}
