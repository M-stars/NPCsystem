using System;
using System.Collections.Generic;
using UnityEngine;

namespace SocialSimulation.Graph
{
    public enum GraphKind { Behavior, Rule }

    public enum GraphNodeType
    {
        Root, Sequence, Selector, ReactiveSelector, Condition, Action, Wait,
        Timeout, Cooldown, Retry, Subtree,
        RuleEvent, RuleScope, RuleCondition, RuleNorm, RuleEffect,
        BlackboardSet, BlackboardCheck
    }

    [Serializable]
    public sealed class GraphParameter
    {
        public string Key;
        public string Value;

        public GraphParameter(string key, string value) { Key = key; Value = value; }
    }

    [Serializable]
    public sealed class GraphNode
    {
        public string Id;
        public GraphNodeType Type;
        public Vector2 Position;
        public List<GraphParameter> Parameters = new List<GraphParameter>();
        public GraphDocument Subgraph;

        public string Get(string key, string fallback = "")
        {
            if (Parameters == null) return fallback;
            for (int i = 0; i < Parameters.Count; i++)
                if (Parameters[i] != null && Parameters[i].Key == key)
                    return Parameters[i].Value ?? fallback;
            return fallback;
        }

        public void Set(string key, string value)
        {
            if (Parameters == null) Parameters = new List<GraphParameter>();
            for (int i = 0; i < Parameters.Count; i++)
            {
                if (Parameters[i] == null || Parameters[i].Key != key) continue;
                Parameters[i].Value = value;
                return;
            }
            Parameters.Add(new GraphParameter(key, value));
        }

        public GraphNode Clone(string newId, Vector2 position)
        {
            var clone = new GraphNode { Id = newId, Type = Type, Position = position, Subgraph = Subgraph };
            if (Parameters != null)
                foreach (var parameter in Parameters)
                    if (parameter != null) clone.Parameters.Add(new GraphParameter(parameter.Key, parameter.Value));
            return clone;
        }
    }

    [Serializable]
    public sealed class GraphEdge
    {
        public string FromNodeId;
        public string FromPort = "out";
        public string ToNodeId;
        public string ToPort = "in";
        public int Order;

        public GraphEdge Clone(string fromId, string toId)
        {
            return new GraphEdge { FromNodeId = fromId, FromPort = FromPort,
                ToNodeId = toId, ToPort = ToPort, Order = Order };
        }
    }

    [CreateAssetMenu(fileName = "NewSocialGraph", menuName = "Social Simulation/Graph Document")]
    public sealed class GraphDocument : ScriptableObject
    {
        [SerializeField] private GraphKind kind;
        [SerializeField] private string documentId;
        [SerializeField] private int schemaVersion = 1;
        [SerializeField] private int version = 1;
        [SerializeField] private List<GraphNode> nodes = new List<GraphNode>();
        [SerializeField] private List<GraphEdge> edges = new List<GraphEdge>();
        [SerializeField] private List<BlackboardVariable> variables = new List<BlackboardVariable>();
        [SerializeField] private List<GraphGroup> groups = new List<GraphGroup>();
        [SerializeField] private List<GraphNote> notes = new List<GraphNote>();

        public GraphKind Kind { get { return kind; } set { kind = value; } }
        public string DocumentId { get { return documentId; } set { documentId = value; } }
        public int SchemaVersion { get { return schemaVersion; } }
        public int Version { get { return version; } }
        public List<GraphNode> Nodes { get { return nodes; } }
        public List<GraphEdge> Edges { get { return edges; } }
        public List<GraphGroup> Groups { get { return groups ?? (groups = new List<GraphGroup>()); } }
        public List<GraphNote> Notes { get { return notes ?? (notes = new List<GraphNote>()); } }
        public List<BlackboardVariable> Variables
        { get { return variables ?? (variables = new List<BlackboardVariable>()); } }

        public void IncrementVersion() { version++; }

        public GraphNode FindNode(string id)
        {
            return nodes.Find(node => node != null && node.Id == id);
        }
    }

    // Editor layout metadata is deliberately excluded from execution plans and their revision hashes.
    [Serializable]
    public sealed class GraphGroup
    {
        public string Id;
        public string Title = "分组";
        public Rect Position;
        public List<string> MemberIds = new List<string>();
    }

    [Serializable]
    public sealed class GraphNote
    {
        public string Id;
        public string Title = "注释";
        public string Contents = "";
        public Rect Position;
        public int Theme;
        public int FontSize;
    }
}
