using System;
using System.Collections.Generic;
using SocialSimulation.Core;
using SocialSimulation.Domains;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Authoring
{
    [CreateAssetMenu(fileName = "RegionPackage", menuName = "Social Simulation/Region Package")]
    public sealed class RegionPackageAsset : ScriptableObject
    {
        [SerializeField] private string packageId;
        [SerializeField] private string version = "1.0.0";
        [SerializeField, Tooltip("仅要求包存在，不限制版本。不要与下方版本依赖重复声明同一包。")]
        private List<string> dependencies = new List<string>();
        [SerializeField, Tooltip("支持 *、精确版本 1.2.3、区间 [1.2,2) 或 [1.2,)。配置后使用包格式 v2。")]
        private List<PackageDependencyRequirement> dependencyRequirements = new List<PackageDependencyRequirement>();
        [SerializeField] private GameObject regionPrefab;
        [SerializeField] private List<RegionDefinition> regions = new List<RegionDefinition>();

        public string PackageId => packageId;
        public string Version => version;
        public GameObject RegionPrefab => regionPrefab;
        public IReadOnlyList<RegionDefinition> Regions => regions;

        public void Configure(string id, string contentVersion, GameObject prefab)
        { packageId = id; version = contentVersion; regionPrefab = prefab; }

        public void ConfigureDependencies(IEnumerable<string> packageIds,
            IEnumerable<PackageDependencyRequirement> requirements = null)
        {
            var copiedIds = packageIds == null ? new List<string>() : new List<string>(packageIds);
            var copiedRequirements = new List<PackageDependencyRequirement>();
            if (requirements != null)
                foreach (var requirement in requirements) copiedRequirements.Add(requirement?.Copy());
            dependencies = copiedIds;
            dependencyRequirements = copiedRequirements;
        }

        public void SetRegions(IEnumerable<RegionDefinition> definitions)
        {
            if (string.IsNullOrWhiteSpace(packageId) || definitions == null)
                throw new ArgumentException("Configure a package ID and provide region definitions first.");
            var copied = new List<RegionDefinition>();
            var regionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in definitions)
            {
                if (region == null || string.IsNullOrWhiteSpace(region.Id) ||
                    string.IsNullOrWhiteSpace(region.Tag) || !regionIds.Add(region.Id) || region.Capacity < 1 ||
                    region.Zones == null || region.Zones.Count == 0 || region.Points == null)
                    throw new ArgumentException("Invalid or duplicate region definition.");
                var metadataErrors = RegionDefinitions.Validate(region);
                if (metadataErrors.Count != 0) throw new ArgumentException(string.Join("; ", metadataErrors));
                var clone = RegionDefinitions.Clone(region);
                clone.PackageId = packageId;
                clone.Zones.Clear();
                clone.Points.Clear();
                var zoneIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var zone in region.Zones)
                {
                    if (zone == null || string.IsNullOrWhiteSpace(zone.LocalId) ||
                        string.IsNullOrWhiteSpace(zone.Tag) || !zoneIds.Add(zone.LocalId) ||
                        zone.Capacity < 1)
                        throw new ArgumentException("Invalid or duplicate functional zone definition.");
                    clone.Zones.Add(new RegionZoneDefinition { LocalId = zone.LocalId,
                        Tag = zone.Tag, Capacity = zone.Capacity });
                }
                var pointIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var point in region.Points)
                {
                    if (point == null || string.IsNullOrWhiteSpace(point.LocalId) ||
                        string.IsNullOrWhiteSpace(point.Kind) || !pointIds.Add(point.LocalId) || point.Capacity < 1 ||
                        !zoneIds.Contains(point.ZoneLocalId ?? string.Empty))
                        throw new ArgumentException("Invalid or duplicate interaction point definition.");
                    clone.Points.Add(new InteractionPointDefinition { LocalId = point.LocalId,
                        ZoneLocalId = point.ZoneLocalId, Kind = point.Kind, Capacity = point.Capacity });
                }
                copied.Add(clone);
            }
            if (copied.Count != 1) throw new ArgumentException("A region prefab must define exactly one region root.");
            regions = copied;
        }

        public PackageManifest ToManifest()
        {
            var manifest = new PackageManifest { Id = packageId, Version = version, Kind = "Region",
                SchemaVersion = dependencyRequirements != null && dependencyRequirements.Count > 0 ? 2 : 1 };
            if (dependencies != null) manifest.Dependencies.AddRange(dependencies);
            if (dependencyRequirements != null)
                foreach (var requirement in dependencyRequirements) manifest.DependencyRequirements.Add(requirement?.Copy());
            foreach (var source in regions)
            {
                manifest.Regions.Add(RegionDefinitions.Clone(source));
            }
            return manifest;
        }

        public List<Diagnostic> Validate()
        {
            var result = new List<Diagnostic>();
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
                result.Add(new Diagnostic("REGION_ID", name, "包 ID 和版本必填。"));
            if (regionPrefab == null) result.Add(new Diagnostic("REGION_PREFAB", name, "未绑定地区 Prefab。"));
            if (regions == null || regions.Count != 1)
                result.Add(new Diagnostic("REGION_DEFINITION", name, "地区包必须定义唯一的地区根节点。"));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in regions ?? new List<RegionDefinition>())
            {
                if (region == null || string.IsNullOrWhiteSpace(region.Id) || !ids.Add(region.Id))
                { result.Add(new Diagnostic("REGION_DEFINITION", name, "区域 ID 为空或重复。")); continue; }
                if (region.PackageId != packageId)
                    result.Add(new Diagnostic("REGION_PACKAGE_ID", region.Id, "区域定义的包 ID 不匹配。"));
                result.AddRange(RegionDefinitions.Validate(region));
                if (region.Capacity < 1 || string.IsNullOrWhiteSpace(region.Tag))
                    result.Add(new Diagnostic("REGION_CAPACITY", region.Id, "地区标签不能为空且容量必须为正数。"));
                var zones = new HashSet<string>(StringComparer.Ordinal);
                foreach (var zone in region.Zones ?? new List<RegionZoneDefinition>())
                    if (zone == null || string.IsNullOrWhiteSpace(zone.LocalId) ||
                        string.IsNullOrWhiteSpace(zone.Tag) || !zones.Add(zone.LocalId) || zone.Capacity < 1)
                        result.Add(new Diagnostic("REGION_ZONE", region.Id,
                            "功能分区本地 ID、标签或容量无效。"));
                if (zones.Count == 0)
                    result.Add(new Diagnostic("REGION_ZONE", region.Id, "至少需要一个功能分区。"));
                var points = new HashSet<string>(StringComparer.Ordinal);
                foreach (var point in region.Points ?? new List<InteractionPointDefinition>())
                    if (point == null || string.IsNullOrWhiteSpace(point.LocalId) || !points.Add(point.LocalId) ||
                        string.IsNullOrWhiteSpace(point.Kind) || point.Capacity < 1 ||
                        !zones.Contains(point.ZoneLocalId ?? string.Empty))
                        result.Add(new Diagnostic("REGION_POINT", region.Id,
                            "交互点本地 ID、动作、所属分区或容量无效。"));
            }
            if (regionPrefab != null)
                ValidatePrefabHierarchy(result);
            return result;
        }

        private void ValidatePrefabHierarchy(List<Diagnostic> result)
        {
            var root = regionPrefab.transform;
            var marker = regionPrefab.GetComponent<SocialWorldMarker>();
            var allMarkers = regionPrefab.GetComponentsInChildren<SocialWorldMarker>(true);
            if (marker == null || allMarkers.Length != 1)
            {
                result.Add(new Diagnostic("REGION_MARKER", name,
                    "Prefab 根节点必须且只能有一个 SocialWorldMarker。"));
                return;
            }
            if (string.IsNullOrWhiteSpace(marker.RegionInstanceId))
                result.Add(new Diagnostic("REGION_INSTANCE_ID", name, "Prefab 模板实例 ID 为空。"));
            var definition = regions != null && regions.Count == 1 ? regions[0] : null;
            if (definition == null) return;
            if (marker.RegionDefinitionId != definition.Id)
                result.Add(new Diagnostic("REGION_DEFINITION_BINDING", name,
                    "根标记的定义 ID 与包内地区定义不一致。"));
            if (marker.PackageId != packageId)
                result.Add(new Diagnostic("REGION_PACKAGE_BINDING", name,
                    "根标记的包 ID 与地区包资产不一致。"));
            if (marker.RegionInstanceId != "template." + definition.Id)
                result.Add(new Diagnostic("REGION_TEMPLATE_ID", name,
                    "Prefab 标记实例 ID 应为 template.<地区定义 ID>。"));
            var bounds = definition.Bounds;
            if (bounds != null && bounds.Enabled)
            {
                var expected = new Bounds(new Vector3(bounds.CenterX, bounds.CenterY, bounds.CenterZ),
                    new Vector3(bounds.SizeX, bounds.SizeY, bounds.SizeZ));
                if (!marker.HasBoundary || marker.LocalBounds != expected)
                    result.Add(new Diagnostic("REGION_BOUNDARY", name, "Prefab 边界与地区定义不一致。"));
            }
            else if (marker.HasBoundary)
                result.Add(new Diagnostic("REGION_BOUNDARY", name, "Prefab 声明了未注册边界。"));
            var expectedEntrances = new HashSet<string>(definition.EntranceLocalIds ?? new List<string>(), StringComparer.Ordinal);
            var foundEntrances = new HashSet<string>(StringComparer.Ordinal);
            var entranceRoot = root.Find("Entrances");
            foreach (var entrance in regionPrefab.GetComponentsInChildren<SocialRegionEntrance>(true))
            {
                if (entrance.transform.parent != entranceRoot || string.IsNullOrWhiteSpace(entrance.LocalId) ||
                    !expectedEntrances.Contains(entrance.LocalId) || !foundEntrances.Add(entrance.LocalId) ||
                    !marker.Contains(entrance.transform.position))
                    result.Add(new Diagnostic("REGION_ENTRANCE", entrance.name, "入口必须位于直属 Entrances 下，具有已注册的唯一本地 ID，并在地区边界内。"));
                if (!string.IsNullOrEmpty(entrance.TargetRegionInstanceId) || !string.IsNullOrEmpty(entrance.TargetEntranceLocalId))
                    result.Add(new Diagnostic("REGION_ENTRANCE_INSTANCE", entrance.name, "包模板不得引用场景实例入口；连接须在场景实例上配置。"));
            }
            if (!foundEntrances.SetEquals(expectedEntrances))
                result.Add(new Diagnostic("REGION_ENTRANCE", name, "地区入口与定义不一致。"));
            var zonesRoot = root.Find("Zones");
            if (zonesRoot == null || zonesRoot.parent != root)
            {
                result.Add(new Diagnostic("REGION_HIERARCHY", name,
                    "根节点直下必须有 Zones 容器。"));
                return;
            }
            var zoneDefinitions = new Dictionary<string, RegionZoneDefinition>(StringComparer.Ordinal);
            foreach (var zone in definition.Zones ?? new List<RegionZoneDefinition>())
                if (zone != null && !string.IsNullOrWhiteSpace(zone.LocalId) &&
                    !zoneDefinitions.ContainsKey(zone.LocalId)) zoneDefinitions.Add(zone.LocalId, zone);
            var pointDefinitions = new Dictionary<string, InteractionPointDefinition>(StringComparer.Ordinal);
            foreach (var point in definition.Points ?? new List<InteractionPointDefinition>())
                if (point != null && !string.IsNullOrWhiteSpace(point.LocalId) &&
                    !pointDefinitions.ContainsKey(point.LocalId)) pointDefinitions.Add(point.LocalId, point);
            var foundZones = new HashSet<string>(StringComparer.Ordinal);
            var foundPoints = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < zonesRoot.childCount; i++)
            {
                var zoneObject = zonesRoot.GetChild(i);
                var zoneMarker = zoneObject.GetComponent<SocialRegionZone>();
                if (zoneMarker == null || string.IsNullOrWhiteSpace(zoneMarker.LocalId) ||
                    !foundZones.Add(zoneMarker.LocalId) ||
                    !zoneDefinitions.TryGetValue(zoneMarker.LocalId, out var zoneDefinition) ||
                    zoneMarker.Tag != zoneDefinition.Tag || zoneMarker.Capacity != zoneDefinition.Capacity)
                {
                    result.Add(new Diagnostic("REGION_ZONE_BINDING", zoneObject.name,
                        "功能分区标记缺失、重复或与定义不一致。"));
                    continue;
                }
                var pointsRoot = zoneObject.Find("InteractionPoints");
                if (pointsRoot == null || pointsRoot.parent != zoneObject)
                {
                    result.Add(new Diagnostic("REGION_HIERARCHY", zoneObject.name,
                        "功能分区直下必须有 InteractionPoints 容器。"));
                    continue;
                }
                for (int j = 0; j < pointsRoot.childCount; j++)
                {
                    var pointObject = pointsRoot.GetChild(j);
                    var pointMarker = pointObject.GetComponent<SocialInteractionPoint>();
                    if (pointMarker == null || string.IsNullOrWhiteSpace(pointMarker.LocalId) ||
                        !foundPoints.Add(pointMarker.LocalId) ||
                        !pointDefinitions.TryGetValue(pointMarker.LocalId, out var pointDefinition) ||
                        pointDefinition.ZoneLocalId != zoneMarker.LocalId ||
                        pointDefinition.Kind != pointMarker.ActionId ||
                        pointDefinition.Capacity != pointMarker.Capacity || !marker.Contains(pointMarker.AnchorPosition))
                        result.Add(new Diagnostic("REGION_POINT_BINDING", pointObject.name,
                            "交互点标记缺失、重复或与所属功能分区/定义不一致。"));
                }
            }
            if (foundZones.Count != zoneDefinitions.Count || foundPoints.Count != pointDefinitions.Count ||
                regionPrefab.GetComponentsInChildren<SocialRegionZone>(true).Length != foundZones.Count ||
                regionPrefab.GetComponentsInChildren<SocialInteractionPoint>(true).Length != foundPoints.Count)
                result.Add(new Diagnostic("REGION_HIERARCHY", name,
                    "Prefab 与地区定义的功能分区/交互点数量不一致，或标记位于标准容器之外。"));
        }
    }

}
