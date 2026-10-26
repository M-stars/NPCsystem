using System;
using System.Collections.Generic;
using UnityEngine;

namespace SocialSimulation.Graph
{
    public enum StructureNodeType { Agent, Collective, Organization, Role, Place }
    public enum StructureEdgeType { Membership, Relation, Authority, Jurisdiction }

    [Serializable]
    public sealed class StructureNode
    {
        public string Id;
        public string EntityId;
        public StructureNodeType Type;
        public Vector2 Position;
        public string Label;
        public string TemplateId;
        public string LinkedCollectiveId;
        public string PlaceId;
        public string Governance = "LeaderAuthorized";
        public string AssetIds;
        public string Tag;
        public string RegionInstanceId;
        public bool Formal;
        public int Capacity = 1;

        public StructureNode Clone(string newId, Vector2 position)
        {
            return new StructureNode { Id = newId, EntityId = EntityId, Type = Type,
                Position = position, Label = Label, TemplateId = TemplateId,
                LinkedCollectiveId = LinkedCollectiveId, PlaceId = PlaceId,
                Governance = Governance, AssetIds = AssetIds, Tag = Tag, RegionInstanceId = RegionInstanceId,
                Formal = Formal, Capacity = Capacity };
        }
    }

    [Serializable]
    public sealed class StructureEdge
    {
        public string Id;
        public string FromNodeId;
        public string ToNodeId;
        public StructureEdgeType Type;
        public string RelationType = "friend";
        public float Liking;
        public float Trust;
        public string ActionId;
        public string CollectiveId;
        public string PlaceScopeId;
        public string ObjectScopeId;
        public string ParentEdgeId;
        public bool CanDelegate;

        public StructureEdge Clone(string newId, string fromId, string toId)
        {
            return new StructureEdge { Id = newId, FromNodeId = fromId, ToNodeId = toId,
                Type = Type, RelationType = RelationType, Liking = Liking, Trust = Trust,
                ActionId = ActionId, CollectiveId = CollectiveId, PlaceScopeId = PlaceScopeId,
                ObjectScopeId = ObjectScopeId, ParentEdgeId = ParentEdgeId, CanDelegate = CanDelegate };
        }
    }

    [CreateAssetMenu(fileName = "NewSocialStructure", menuName = "Social Simulation/Social Structure")]
    public sealed class SocialStructureDocument : ScriptableObject
    {
        [SerializeField] private string documentId;
        [SerializeField] private int version = 1;
        [SerializeField] private List<StructureNode> nodes = new List<StructureNode>();
        [SerializeField] private List<StructureEdge> edges = new List<StructureEdge>();

        public string DocumentId { get { return documentId; } set { documentId = value; } }
        public int Version { get { return version; } }
        public List<StructureNode> Nodes { get { return nodes; } }
        public List<StructureEdge> Edges { get { return edges; } }
        public void IncrementVersion() { version++; }
        public StructureNode FindNode(string id) { return nodes.Find(node => node != null && node.Id == id); }
        public StructureEdge FindEdge(string id) { return edges.Find(edge => edge != null && edge.Id == id); }
    }
}
