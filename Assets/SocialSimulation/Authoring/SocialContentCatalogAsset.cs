using System;
using System.Collections.Generic;
using SocialSimulation.Core;
using SocialSimulation.Domains;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Authoring
{
    [CreateAssetMenu(fileName = "SocialContentCatalog", menuName = "Social Simulation/Content Catalog")]
    public sealed class SocialContentCatalogAsset : ScriptableObject
    {
        [SerializeField] private List<NpcPackageAsset> npcPackages = new List<NpcPackageAsset>();
        [SerializeField] private List<RegionPackageAsset> regionPackages = new List<RegionPackageAsset>();
        [SerializeField] private List<SocialDomainPackageAsset> domainPackages = new List<SocialDomainPackageAsset>();

        public List<NpcPackageAsset> NpcPackages => npcPackages;
        public List<RegionPackageAsset> RegionPackages => regionPackages;
        public List<SocialDomainPackageAsset> DomainPackages => domainPackages;

        public RegistrationResult BuildRegistry(ContentRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.RegisterExecutor("navmesh.move");
            registry.RegisterExecutor("simulation.action");
            registry.RegisterExecutor("animator.present");
            var result = new RegistrationResult { Success = true };
            var packageIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var npc in npcPackages)
            {
                if (npc == null) { result.Diagnostics.Add(new Diagnostic("MISSING_NPC", name, "NPC 包引用失效。")); continue; }
                CheckPackageId(result, packageIds, npc.PackageId, npc.name);
                result.Diagnostics.AddRange(npc.Validate());
            }
            foreach (var region in regionPackages)
            {
                if (region == null) { result.Diagnostics.Add(new Diagnostic("MISSING_REGION", name, "地区包引用失效。")); continue; }
                CheckPackageId(result, packageIds, region.PackageId, region.name);
                result.Diagnostics.AddRange(region.Validate());
            }
            var regionInstanceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var region in regionPackages)
            {
                if (region == null || region.RegionPrefab == null) continue;
                foreach (var marker in region.RegionPrefab.GetComponentsInChildren<SocialWorldMarker>(true))
                {
                    if (string.IsNullOrWhiteSpace(marker.RegionInstanceId)) continue;
                    if (regionInstanceOwners.TryGetValue(marker.RegionInstanceId, out var owner) &&
                        owner != region.PackageId)
                        result.Diagnostics.Add(new Diagnostic("DUPLICATE_REGION_INSTANCE_ID", marker.RegionInstanceId,
                            "地区 Prefab 模板 ID 同时用于包 " + owner + " 和 " + region.PackageId + "。"));
                    else regionInstanceOwners[marker.RegionInstanceId] = region.PackageId;
                }
            }
            foreach (var domain in domainPackages)
            {
                if (domain == null) { result.Diagnostics.Add(new Diagnostic("MISSING_DOMAIN", name, "领域包引用失效。")); continue; }
                CheckPackageId(result, packageIds, domain.PackageId, domain.name);
                if (string.IsNullOrWhiteSpace(domain.Version))
                    result.Diagnostics.Add(new Diagnostic("DOMAIN_VERSION", domain.name, "领域包版本不能为空。"));
            }
            if (result.Diagnostics.Count != 0) { result.Success = false; return result; }
            var manifests = new List<PackageManifest>();
            foreach (var npc in npcPackages) manifests.Add(npc.ToManifest());
            foreach (var region in regionPackages) manifests.Add(region.ToManifest());
            foreach (var domain in domainPackages) manifests.Add(domain.ToManifest().ToPackageManifest());
            var committed = registry.RegisterBatch(manifests);
            result.Diagnostics.AddRange(committed.Diagnostics);
            result.Success = committed.Success;
            return result;
        }

        private static void CheckPackageId(RegistrationResult result, HashSet<string> ids,
            string packageId, string assetName)
        {
            if (string.IsNullOrWhiteSpace(packageId))
                result.Diagnostics.Add(new Diagnostic("PACKAGE_ID", assetName, "包 ID 不能为空。"));
            else if (!ids.Add(packageId))
                result.Diagnostics.Add(new Diagnostic("DUPLICATE_PACKAGE_ID", packageId,
                    "内容目录中同一包 ID 只能注册一次。"));
        }
    }
}
