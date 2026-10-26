using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SocialSimulation.Core;

namespace SocialSimulation.Graph
{
    public sealed class StructureNodeRecord
    {
        public readonly string Id, EntityId, Label, TemplateId, LinkedCollectiveId, PlaceId;
        public readonly string Governance, AssetIds, Tag, RegionInstanceId;
        public readonly StructureNodeType Type;
        public readonly bool Formal;
        public readonly int Capacity;

        internal StructureNodeRecord(StructureNode node)
        {
            Id = node.Id; EntityId = node.EntityId; Label = node.Label;
            TemplateId = node.TemplateId; LinkedCollectiveId = node.LinkedCollectiveId;
            PlaceId = node.PlaceId; Governance = node.Governance; AssetIds = node.AssetIds; Tag = node.Tag;
            RegionInstanceId = node.RegionInstanceId; Type = node.Type;
            Formal = node.Formal; Capacity = node.Capacity;
        }
    }

    public sealed class StructureEdgeRecord
    {
        public readonly string Id, FromNodeId, ToNodeId, RelationType, ActionId;
        public readonly string CollectiveId, PlaceScopeId, ObjectScopeId, ParentEdgeId;
        public readonly StructureEdgeType Type;
        public readonly float Liking, Trust;
        public readonly bool CanDelegate;

        internal StructureEdgeRecord(StructureEdge edge)
        {
            Id = edge.Id; FromNodeId = edge.FromNodeId; ToNodeId = edge.ToNodeId;
            Type = edge.Type; RelationType = edge.RelationType; ActionId = edge.ActionId;
            CollectiveId = edge.CollectiveId; PlaceScopeId = edge.PlaceScopeId;
            ObjectScopeId = edge.ObjectScopeId; ParentEdgeId = edge.ParentEdgeId;
            Liking = edge.Liking; Trust = edge.Trust; CanDelegate = edge.CanDelegate;
        }
    }

    public sealed class StructureCompileResult
    {
        public readonly IReadOnlyList<GraphDiagnostic> Diagnostics;
        public readonly SocialStructurePlan Plan;
        public bool Success { get { return Plan != null; } }

        internal StructureCompileResult(List<GraphDiagnostic> diagnostics, SocialStructurePlan plan)
        { Diagnostics = diagnostics.AsReadOnly(); Plan = plan; }
    }

    public sealed class StructureApplyResult
    {
        public readonly IReadOnlyList<GraphDiagnostic> Diagnostics;
        public bool Success { get { return Diagnostics.Count == 0; } }

        internal StructureApplyResult(List<GraphDiagnostic> diagnostics)
        { Diagnostics = diagnostics.AsReadOnly(); }
    }

    public sealed class SocialStructurePlan
    {
        public readonly string DocumentId;
        public readonly int Version;
        public readonly IReadOnlyList<StructureNodeRecord> Nodes;
        public readonly IReadOnlyList<StructureEdgeRecord> Edges;

        internal SocialStructurePlan(string documentId, int version,
            List<StructureNodeRecord> nodes, List<StructureEdgeRecord> edges)
        {
            DocumentId = documentId; Version = version;
            Nodes = nodes.AsReadOnly(); Edges = edges.AsReadOnly();
        }

        /// <summary>Populate a new or compatible world. Runtime membership changes use SocietyService lifecycle methods.</summary>
        public StructureApplyResult ApplyTo(SimulationWorld world)
        {
            if (world == null) throw new ArgumentNullException("world");
            var diagnostics = new List<GraphDiagnostic>();
            var byId = new Dictionary<string, StructureNodeRecord>(StringComparer.Ordinal);
            var plannedPlaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in Nodes)
                if (node.Type == StructureNodeType.Place) plannedPlaces.Add(node.EntityId);
            foreach (var node in Nodes)
            {
                byId.Add(node.Id, node);
                if (node.Type == StructureNodeType.Collective &&
                    world.Society.Collectives.TryGetValue(node.EntityId, out var collective) && !collective.Active)
                    Error(diagnostics, "InactiveCollective", node.Id, "现有集体已解散，不能作为新成员归属。");
                if (node.Type == StructureNodeType.Organization &&
                    world.Society.Organizations.TryGetValue(node.EntityId, out var organization) &&
                    organization.CollectiveId != node.LinkedCollectiveId)
                    Error(diagnostics, "OrganizationConflict", node.Id, "现有组织绑定了不同集体。");
                if (node.Type == StructureNodeType.Organization &&
                    world.Society.Organizations.TryGetValue(node.EntityId, out organization) && !organization.Active)
                    Error(diagnostics, "InactiveOrganization", node.Id, "现有组织已关闭，不能作为新结构导入。");
                if (node.Type == StructureNodeType.Agent && !string.IsNullOrEmpty(node.PlaceId) &&
                    !world.Places.ContainsKey(node.PlaceId) && !plannedPlaces.Contains(node.PlaceId))
                    Error(diagnostics, "MissingInitialPlace", node.Id, "NPC 初始地点既不在结构图，也不在世界中。");
            }
            foreach (var edge in Edges)
            {
                if (edge.Type != StructureEdgeType.Jurisdiction) continue;
                var place = byId[edge.ToNodeId];
                var from = byId[edge.FromNodeId];
                var owner = from.Type == StructureNodeType.Organization ? from.LinkedCollectiveId : from.EntityId;
                if (world.Places.TryGetValue(place.EntityId, out var existing) &&
                    !string.IsNullOrEmpty(existing.OwnerCollectiveId) && existing.OwnerCollectiveId != owner)
                    Error(diagnostics, "JurisdictionConflict", edge.Id, "地点已有不同的所属集体。");
            }
            if (diagnostics.Count > 0) return new StructureApplyResult(diagnostics);

            // A dry run catches failures in Society.Join/AssignRole/Grant before the live world changes.
            // Unity simulation commits on one thread; callers should pause that thread while applying.
            try
            {
                var staging = WorldPersistence.Restore(WorldPersistence.Capture(world));
                ApplyValidated(staging, byId);
            }
            catch (Exception exception)
            {
                Error(diagnostics, "StagingFailure", "", "隔离预检失败，原世界未写入：" + exception.Message);
                return new StructureApplyResult(diagnostics);
            }

            try { ApplyValidated(world, byId); }
            catch (Exception exception)
            {
                Error(diagnostics, "CommitFailure", "",
                    "实际应用异常，世界可能已部分更新；请从存档恢复：" + exception.Message);
            }
            return new StructureApplyResult(diagnostics);
        }

        private void ApplyValidated(SimulationWorld world, Dictionary<string, StructureNodeRecord> byId)
        {

            foreach (var node in Nodes)
                if (node.Type == StructureNodeType.Place && !world.Places.ContainsKey(node.EntityId))
                    world.Places.Add(node.EntityId, new PlaceState { Id = node.EntityId, Name = node.Label,
                        Tag = node.Tag, RegionInstanceId = node.RegionInstanceId, Capacity = node.Capacity });
            foreach (var node in Nodes)
                if (node.Type == StructureNodeType.Collective && !world.Society.Collectives.ContainsKey(node.EntityId))
                    world.Society.Collectives.Add(node.EntityId, new CollectiveState { Id = node.EntityId,
                        Name = node.Label, Formal = node.Formal });
            foreach (var node in Nodes)
                if (node.Type == StructureNodeType.Agent && !world.Agents.ContainsKey(node.EntityId))
                    world.AddAgent(new AgentState { Id = node.EntityId, DisplayName = node.Label,
                        TemplateId = node.TemplateId, PlaceId = node.PlaceId });
            foreach (var node in Nodes)
                if (node.Type == StructureNodeType.Organization && !world.Society.Organizations.ContainsKey(node.EntityId))
                {
                    var organization = new OrganizationState { Id = node.EntityId,
                        CollectiveId = node.LinkedCollectiveId, Governance = node.Governance };
                    if (!string.IsNullOrWhiteSpace(node.AssetIds))
                        foreach (var asset in node.AssetIds.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var assetId = asset.Trim();
                            if (assetId.Length > 0 && !organization.AssetIds.Contains(assetId))
                                organization.AssetIds.Add(assetId);
                        }
                    world.Society.Organizations.Add(node.EntityId, organization);
                }

            foreach (var edge in Edges)
                if (edge.Type == StructureEdgeType.Jurisdiction)
                {
                    var place = byId[edge.ToNodeId];
                    var owner = byId[edge.FromNodeId];
                    world.Places[place.EntityId].OwnerCollectiveId = owner.Type == StructureNodeType.Organization
                        ? owner.LinkedCollectiveId : owner.EntityId;
                }
            foreach (var edge in Edges)
                if (edge.Type == StructureEdgeType.Membership &&
                    byId[edge.ToNodeId].Type == StructureNodeType.Collective)
                    world.Society.Join(byId[edge.FromNodeId].EntityId, byId[edge.ToNodeId].EntityId);
            foreach (var edge in Edges)
                if (edge.Type == StructureEdgeType.Membership &&
                    byId[edge.ToNodeId].Type == StructureNodeType.Role)
                {
                    var agentId = byId[edge.FromNodeId].EntityId;
                    var role = byId[edge.ToNodeId];
                    bool exists = false;
                    foreach (var assignment in world.Society.Roles.Values)
                        if (assignment.Active && assignment.AgentId == agentId &&
                            assignment.CollectiveId == role.LinkedCollectiveId && assignment.RoleId == role.EntityId)
                        { exists = true; break; }
                    if (!exists) world.Society.AssignRole(agentId, role.LinkedCollectiveId, role.EntityId);
                }
            foreach (var edge in Edges)
                if (edge.Type == StructureEdgeType.Relation)
                {
                    string source = byId[edge.FromNodeId].EntityId;
                    string target = byId[edge.ToNodeId].EntityId;
                    string key = Identity.Definition(Identity.Definition(source, target), edge.RelationType);
                    world.Society.Relations.TryGetValue(key, out var existing);
                    float liking = existing == null ? 0 : existing.Liking;
                    float trust = existing == null ? 0 : existing.Trust;
                    if (existing == null || liking != edge.Liking || trust != edge.Trust)
                        world.Society.Relate(source, target, edge.RelationType,
                            edge.Liking - liking, edge.Trust - trust);
                }

            var pending = new List<StructureEdgeRecord>();
            foreach (var edge in Edges) if (edge.Type == StructureEdgeType.Authority) pending.Add(edge);
            var actualGrantIds = new Dictionary<string, string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                bool progressed = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var edge = pending[i];
                    string parentGrantId = null;
                    if (!string.IsNullOrEmpty(edge.ParentEdgeId) &&
                        !actualGrantIds.TryGetValue(edge.ParentEdgeId, out parentGrantId)) continue;
                    string from = byId[edge.FromNodeId].EntityId;
                    string to = byId[edge.ToNodeId].EntityId;
                    AuthorityGrant grant = null;
                    foreach (var candidate in world.Society.Grants.Values)
                        if (candidate.Active && candidate.BeginsAt <= world.Time &&
                            world.Time < candidate.EndsAt &&
                            candidate.GrantorId == from && candidate.GranteeId == to &&
                            candidate.ActionId == edge.ActionId && candidate.CollectiveId == edge.CollectiveId &&
                            candidate.PlaceScopeId == edge.PlaceScopeId && candidate.ObjectScopeId == edge.ObjectScopeId &&
                            candidate.ParentGrantId == parentGrantId && candidate.CanDelegate == edge.CanDelegate)
                        { grant = candidate; break; }
                    if (grant == null)
                        grant = world.Society.Grant(from, to, edge.ActionId, edge.CollectiveId,
                            edge.PlaceScopeId, edge.ObjectScopeId, double.MaxValue, edge.CanDelegate, parentGrantId);
                    actualGrantIds.Add(edge.Id, grant.Id);
                    pending.RemoveAt(i);
                    progressed = true;
                }
                if (!progressed) throw new InvalidOperationException("Authority dependency was not resolved after validation.");
            }
        }

        public SimulationWorld CreateWorld(string worldId, uint seed = 1)
        {
            var world = new SimulationWorld(worldId, seed);
            var result = ApplyTo(world);
            if (!result.Success)
                throw new InvalidOperationException("Social structure could not be applied: " + result.Diagnostics[0]);
            return world;
        }

        private static void Error(List<GraphDiagnostic> diagnostics, string code, string id, string message)
        { diagnostics.Add(new GraphDiagnostic(GraphDiagnosticSeverity.Error, code, id, message)); }
    }

    public static class SocialStructureCompiler
    {
        public static bool CanConnect(StructureNodeType from, StructureNodeType to, StructureEdgeType type)
        {
            switch (type)
            {
                case StructureEdgeType.Membership: return from == StructureNodeType.Agent &&
                    (to == StructureNodeType.Collective || to == StructureNodeType.Role);
                case StructureEdgeType.Relation: return from == StructureNodeType.Agent && to == StructureNodeType.Agent;
                case StructureEdgeType.Authority: return (from == StructureNodeType.Agent ||
                    from == StructureNodeType.Collective) && to == StructureNodeType.Agent;
                case StructureEdgeType.Jurisdiction: return (from == StructureNodeType.Collective ||
                    from == StructureNodeType.Organization) && to == StructureNodeType.Place;
                default: return false;
            }
        }

        public static StructureCompileResult Compile(SocialStructureDocument document)
        {
            var diagnostics = new List<GraphDiagnostic>();
            if (document == null)
            {
                Error(diagnostics, "MissingDocument", "", "社会结构图资产为空。");
                return new StructureCompileResult(diagnostics, null);
            }
            if (string.IsNullOrWhiteSpace(document.DocumentId))
                Error(diagnostics, "MissingDocumentId", "", "社会结构图缺少稳定 ID。");
            if (document.Nodes == null || document.Edges == null)
            {
                Error(diagnostics, "MissingStructureData", "", "社会结构图的节点或连线列表不存在。");
                return new StructureCompileResult(diagnostics, null);
            }
            if (document.Nodes.Count == 0) Error(diagnostics, "EmptyStructure", "", "至少创建一个结构节点。");
            var nodes = new Dictionary<string, StructureNode>(StringComparer.Ordinal);
            var entityIds = new HashSet<string>(StringComparer.Ordinal);
            var pairedOrganizationIds = new HashSet<string>(StringComparer.Ordinal);
            var organizationIds = new HashSet<string>(StringComparer.Ordinal);
            var roleKeys = new HashSet<string>(StringComparer.Ordinal);
            var collectives = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in document.Nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node))
                { Error(diagnostics, "DuplicateNode", node == null ? "" : node.Id, "结构节点 ID 缺失或重复。"); continue; }
                if (!Enum.IsDefined(typeof(StructureNodeType), node.Type))
                    Error(diagnostics, "UnknownNodeType", node.Id, "结构节点类型无效。");
                if (string.IsNullOrWhiteSpace(node.EntityId))
                    Error(diagnostics, "MissingEntity", node.Id, "实体 ID 不能为空。");
                else if (node.Type == StructureNodeType.Role)
                {
                    string roleKey = (node.LinkedCollectiveId ?? "").Length + ":" + node.LinkedCollectiveId + node.EntityId;
                    if (!roleKeys.Add(roleKey))
                        Error(diagnostics, "DuplicateRole", node.Id, "同一集体内的角色 ID 重复。");
                }
                else if (node.Type == StructureNodeType.Organization && node.EntityId == node.LinkedCollectiveId)
                {
                    if (!pairedOrganizationIds.Add(node.EntityId))
                        Error(diagnostics, "DuplicateEntity", node.Id, "同一集体的组织节点重复。");
                }
                else if (!entityIds.Add(node.EntityId))
                    Error(diagnostics, "DuplicateEntity", node.Id, "实体 ID 重复。");
                if (node.Type == StructureNodeType.Organization && !organizationIds.Add(node.EntityId))
                    Error(diagnostics, "DuplicateOrganization", node.Id, "组织 ID 重复。");
                if (node.Type == StructureNodeType.Collective) collectives.Add(node.EntityId);
                if (node.Type == StructureNodeType.Place && node.Capacity < 0)
                    Error(diagnostics, "InvalidCapacity", node.Id, "地点容量不能为负数。");
            }
            foreach (var node in nodes.Values)
                if ((node.Type == StructureNodeType.Organization || node.Type == StructureNodeType.Role) &&
                    !collectives.Contains(node.LinkedCollectiveId ?? ""))
                    Error(diagnostics, "MissingCollective", node.Id, "组织或角色必须绑定图内的集体 ID。");

            var edges = new Dictionary<string, StructureEdge>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var jurisdictionPlaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in document.Edges)
            {
                if (edge == null || string.IsNullOrWhiteSpace(edge.Id) || !edges.TryAdd(edge.Id, edge))
                { Error(diagnostics, "DuplicateEdge", edge == null ? "" : edge.Id, "结构连线 ID 缺失或重复。"); continue; }
                if (!Enum.IsDefined(typeof(StructureEdgeType), edge.Type))
                    Error(diagnostics, "UnknownEdgeType", edge.Id, "结构连线类型无效。");
                StructureNode from, to;
                if (!nodes.TryGetValue(edge.FromNodeId ?? "", out from) ||
                    !nodes.TryGetValue(edge.ToNodeId ?? "", out to))
                { Error(diagnostics, "MissingEndpoint", edge.Id, "连线引用不存在的结构节点。"); continue; }
                if (!CanConnect(from.Type, to.Type, edge.Type) || from.Id == to.Id)
                    Error(diagnostics, "InvalidConnection", edge.Id, "此类型的结构节点不能这样连接。");
                string key = edge.Type + "|" + edge.FromNodeId + "|" + edge.ToNodeId;
                if (edge.Type == StructureEdgeType.Relation) key += "|" + edge.RelationType;
                if (edge.Type == StructureEdgeType.Authority)
                    key += "|" + edge.ActionId + "|" + edge.CollectiveId + "|" +
                        edge.PlaceScopeId + "|" + edge.ObjectScopeId + "|" + edge.ParentEdgeId;
                if (!keys.Add(key)) Error(diagnostics, "DuplicateConnection", edge.Id, "同一类型连线重复。");
                if (edge.Type == StructureEdgeType.Jurisdiction && !jurisdictionPlaces.Add(to.Id))
                    Error(diagnostics, "MultipleJurisdictions", edge.Id, "一个地点只能有一个所属集体。");
                if (edge.Type == StructureEdgeType.Relation &&
                    (string.IsNullOrWhiteSpace(edge.RelationType) || edge.Liking < -1 || edge.Liking > 1 ||
                        edge.Trust < -1 || edge.Trust > 1))
                    Error(diagnostics, "InvalidRelation", edge.Id, "关系类型不能为空，喜欢和信任须在 -1 到 1 之间。");
                if (edge.Type == StructureEdgeType.Authority)
                {
                    if (string.IsNullOrWhiteSpace(edge.ActionId) || !collectives.Contains(edge.CollectiveId ?? ""))
                        Error(diagnostics, "InvalidAuthority", edge.Id, "授权需要动作 ID 和图内集体 ID。");
                    if (from.Type == StructureNodeType.Collective && from.EntityId != edge.CollectiveId)
                        Error(diagnostics, "WrongGrantor", edge.Id, "集体授权必须使用自身集体 ID。");
                    if (from.Type == StructureNodeType.Collective && !string.IsNullOrEmpty(edge.ParentEdgeId))
                        Error(diagnostics, "InvalidBaseGrant", edge.Id, "集体初始授权不能引用父授权边。");
                    if (from.Type == StructureNodeType.Agent && string.IsNullOrEmpty(edge.ParentEdgeId))
                        Error(diagnostics, "MissingParentGrant", edge.Id, "NPC 转授权需要父授权边 ID。");
                }
            }
            foreach (var edge in edges.Values)
            {
                if (!nodes.TryGetValue(edge.FromNodeId ?? "", out var from) ||
                    !nodes.TryGetValue(edge.ToNodeId ?? "", out var to)) continue;
                if (edge.Type == StructureEdgeType.Membership && to.Type == StructureNodeType.Role)
                {
                    bool member = false;
                    foreach (var candidate in edges.Values)
                        if (candidate.Type == StructureEdgeType.Membership && candidate.FromNodeId == from.Id &&
                            nodes.TryGetValue(candidate.ToNodeId ?? "", out var target) &&
                            target.Type == StructureNodeType.Collective && target.EntityId == to.LinkedCollectiveId)
                        { member = true; break; }
                    if (!member) Error(diagnostics, "RoleNeedsMembership", edge.Id, "角色指派需要同一 NPC 到所属集体的成员连线。");
                }
                if (edge.Type != StructureEdgeType.Authority || from.Type != StructureNodeType.Agent) continue;
                StructureEdge parent;
                if (!edges.TryGetValue(edge.ParentEdgeId ?? "", out parent) ||
                    !nodes.TryGetValue(parent.ToNodeId ?? "", out var grantee) ||
                    parent.Type != StructureEdgeType.Authority || !parent.CanDelegate ||
                    grantee.EntityId != from.EntityId || parent.ActionId != edge.ActionId ||
                    parent.CollectiveId != edge.CollectiveId ||
                    (!string.IsNullOrEmpty(parent.PlaceScopeId) && parent.PlaceScopeId != edge.PlaceScopeId) ||
                    (!string.IsNullOrEmpty(parent.ObjectScopeId) && parent.ObjectScopeId != edge.ObjectScopeId))
                    Error(diagnostics, "InvalidDelegation", edge.Id, "转授权必须引用范围兼容且可委托的父授权。");
            }
            foreach (var edge in edges.Values)
                if (edge.Type == StructureEdgeType.Authority)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var current = edge;
                    while (current != null && !string.IsNullOrEmpty(current.ParentEdgeId))
                    {
                        if (!seen.Add(current.Id))
                        { Error(diagnostics, "DelegationCycle", edge.Id, "授权委托不能成环。"); break; }
                        edges.TryGetValue(current.ParentEdgeId, out current);
                    }
                }

            if (diagnostics.Exists(item => item.Severity == GraphDiagnosticSeverity.Error))
                return new StructureCompileResult(diagnostics, null);
            var nodeRecords = new List<StructureNodeRecord>();
            var edgeRecords = new List<StructureEdgeRecord>();
            foreach (var node in document.Nodes) nodeRecords.Add(new StructureNodeRecord(node));
            foreach (var edge in document.Edges) edgeRecords.Add(new StructureEdgeRecord(edge));
            return new StructureCompileResult(diagnostics,
                new SocialStructurePlan(document.DocumentId, document.Version, nodeRecords, edgeRecords));
        }

        private static void Error(List<GraphDiagnostic> diagnostics, string code, string id, string message)
        { diagnostics.Add(new GraphDiagnostic(GraphDiagnosticSeverity.Error, code, id, message)); }
    }
}
