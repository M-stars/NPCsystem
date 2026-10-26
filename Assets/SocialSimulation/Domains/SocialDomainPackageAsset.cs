using System.Collections.Generic;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.Domains
{
    [CreateAssetMenu(menuName = "Social Simulation/Domain Package")]
    public sealed class SocialDomainPackageAsset : ScriptableObject
    {
        [SerializeField] private string packageId;
        [SerializeField] private string version = "1.0.0";
        [SerializeField, Tooltip("仅要求包存在，不限制版本。不要与下方版本依赖重复声明同一包。")]
        private List<string> dependencies = new List<string>();
        [SerializeField, Tooltip("支持 *、精确版本 1.2.3、区间 [1.2,2) 或 [1.2,)。配置后使用包格式 v2。")]
        private List<PackageDependencyRequirement> dependencyRequirements = new List<PackageDependencyRequirement>();
        [SerializeField] private string displayName;
        [SerializeField] private string[] roleIds = new string[0];
        [SerializeField] private string[] relationTypeIds = new string[0];
        [SerializeField] private string[] activityTypeIds = new string[0];
        [SerializeField] private string[] actionIds = new string[0];

        public string PackageId => packageId;
        public string Version => version;
        public string DisplayName => displayName;

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

        public void Configure(string id, string packageVersion, string label,
            string[] roles, string[] relations, string[] activities, string[] actions)
        {
            packageId = id;
            version = packageVersion;
            displayName = label;
            roleIds = roles ?? new string[0];
            relationTypeIds = relations ?? new string[0];
            activityTypeIds = activities ?? new string[0];
            actionIds = actions ?? new string[0];
        }

        public SocialDomainManifest ToManifest()
        {
            var manifest = new SocialDomainManifest
            {
                Id = packageId,
                Version = version,
                SchemaVersion = dependencyRequirements != null && dependencyRequirements.Count > 0 ? 2 : 1,
                RoleIds = new List<string>(roleIds),
                RelationTypeIds = new List<string>(relationTypeIds),
                ActivityTypeIds = new List<string>(activityTypeIds),
                ActionIds = new List<string>(actionIds)
            };
            if (dependencies != null) manifest.Dependencies.AddRange(dependencies);
            if (dependencyRequirements != null)
                foreach (var requirement in dependencyRequirements) manifest.DependencyRequirements.Add(requirement?.Copy());
            return manifest;
        }
    }
}
