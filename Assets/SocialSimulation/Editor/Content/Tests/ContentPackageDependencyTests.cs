using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SocialSimulation.Authoring;
using SocialSimulation.Core;
using SocialSimulation.Domains;
using SocialSimulation.Domains.Campus;
using SocialSimulation.Domains.Combined;
using SocialSimulation.Domains.Household;
using SocialSimulation.Domains.WorkAndTrade;
using SocialSimulation.UnityRuntime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SocialSimulation.Editor.Content.Tests
{
    public sealed class ContentPackageDependencyTests
    {
        private readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = owned.Count - 1; i >= 0; i--)
                if (owned[i] != null) Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }
        private SocialContentCatalogAsset Catalog() => Own(ScriptableObject.CreateInstance<SocialContentCatalogAsset>());

        private SocialDomainPackageAsset Domain(string id, string version = "1.0.0")
        {
            var domain = Own(ScriptableObject.CreateInstance<SocialDomainPackageAsset>());
            domain.Configure(id, version, id, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            return domain;
        }

        private ScriptableObject Package(string kind)
        {
            if (kind == "Domain") return Domain("dependency.consumer");
            if (kind == "NPC")
            {
                var source = AssetDatabase.FindAssets("t:NpcPackageAsset").Select(AssetDatabase.GUIDToAssetPath)
                    .Select(AssetDatabase.LoadAssetAtPath<NpcPackageAsset>).First(item => item.Validate().Count == 0);
                return Own(Object.Instantiate(source));
            }
            var region = AssetDatabase.FindAssets("t:RegionPackageAsset").Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<RegionPackageAsset>).First(item => item.Validate().Count == 0);
            return Own(Object.Instantiate(region));
        }

        private static void Configure(ScriptableObject asset, IEnumerable<string> ids,
            IEnumerable<PackageDependencyRequirement> requirements = null)
        {
            if (asset is NpcPackageAsset npc) npc.ConfigureDependencies(ids, requirements);
            else if (asset is RegionPackageAsset region) region.ConfigureDependencies(ids, requirements);
            else ((SocialDomainPackageAsset)asset).ConfigureDependencies(ids, requirements);
        }

        private static void Add(SocialContentCatalogAsset catalog, ScriptableObject asset)
        {
            if (asset is NpcPackageAsset npc) catalog.NpcPackages.Add(npc);
            else if (asset is RegionPackageAsset region) catalog.RegionPackages.Add(region);
            else catalog.DomainPackages.Add((SocialDomainPackageAsset)asset);
        }

        private static PackageManifest Manifest(ScriptableObject asset)
        {
            if (asset is NpcPackageAsset npc) return npc.ToManifest();
            if (asset is RegionPackageAsset region) return region.ToManifest();
            return ((SocialDomainPackageAsset)asset).ToManifest().ToPackageManifest();
        }

        [TestCase("NPC")]
        [TestCase("Region")]
        [TestCase("Domain")]
        public void CatalogHonorsSerializedVersionDependenciesAndRejectsWrongInstalledVersion(string kind)
        {
            var package = Package(kind);
            var requirement = new PackageDependencyRequirement { PackageId = "dependency.base", VersionRange = "[1.2,2)" };
            Configure(package, null, new[] { requirement });
            requirement.VersionRange = "9";
            // Both API input and emitted manifest are independent of the authored definition.
            var emitted = Manifest(package);
            emitted.DependencyRequirements[0].VersionRange = "8";
            var serialized = EditorJsonUtility.ToJson(package);
            var restored = Own((ScriptableObject)ScriptableObject.CreateInstance(package.GetType()));
            EditorJsonUtility.FromJsonOverwrite(serialized, restored);
            var catalog = Catalog(); Add(catalog, restored);
            var dependency = Domain("dependency.base", "1.5.0"); catalog.DomainPackages.Add(dependency);
            var registry = new ContentRegistry();
            var accepted = catalog.BuildRegistry(registry);
            Assert.IsTrue(accepted.Success, string.Join("; ", accepted.Diagnostics));
            Assert.AreEqual(2, registry.Packages.Count);

            dependency.Configure("dependency.base", "2.0.0", "base", null, null, null, null);
            var rejectedRegistry = new ContentRegistry();
            var rejected = catalog.BuildRegistry(rejectedRegistry);
            Assert.IsFalse(rejected.Success);
            Assert.IsTrue(rejected.Diagnostics.Exists(item => item.Code == "DEPENDENCY_VERSION_MISMATCH"), string.Join("; ", rejected.Diagnostics));
            StringAssert.Contains("[1.2,2)", string.Join("; ", rejected.Diagnostics));
            StringAssert.Contains("2.0.0", string.Join("; ", rejected.Diagnostics));
            Assert.IsEmpty(rejectedRegistry.Packages, "A rejected catalog must not commit any packages.");
        }

        [TestCase("NPC")]
        [TestCase("Region")]
        [TestCase("Domain")]
        public void BareDependenciesSurviveAuthoringAndStillRequireRegistration(string kind)
        {
            var package = Package(kind);
            var ids = new List<string> { "dependency.legacy" };
            Configure(package, ids); ids.Clear();
            var emitted = Manifest(package); emitted.Dependencies.Clear();
            var catalog = Catalog(); Add(catalog, package);
            var failed = catalog.BuildRegistry(new ContentRegistry());
            Assert.IsFalse(failed.Success);
            Assert.IsTrue(failed.Diagnostics.Exists(item => item.Code == "MISSING_DEPENDENCY"));
            catalog.DomainPackages.Add(Domain("dependency.legacy", "legacy-release"));
            var registry = new ContentRegistry();
            var accepted = catalog.BuildRegistry(registry);
            Assert.IsTrue(accepted.Success, string.Join("; ", accepted.Diagnostics));
            Assert.AreEqual(1, registry.Packages[Manifest(package).Id].SchemaVersion,
                "Existing existence-only dependencies keep the legacy package contract.");
        }

        [TestCase("NPC")]
        [TestCase("Region")]
        [TestCase("Domain")]
        public void MalformedAuthoredVersionRangeIsRejectedByActualCatalogRegistration(string kind)
        {
            var package = Package(kind);
            Configure(package, null, new[] { new PackageDependencyRequirement { PackageId = "dependency.base", VersionRange = "^1" } });
            var catalog = Catalog(); Add(catalog, package); catalog.DomainPackages.Add(Domain("dependency.base"));
            var registry = new ContentRegistry();
            var result = catalog.BuildRegistry(registry);
            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Diagnostics.Exists(item => item.Code == "DEPENDENCY_REQUIREMENT"), string.Join("; ", result.Diagnostics));
            Assert.IsEmpty(registry.Packages);
        }

        [Test]
        public void EditorRegistrationPreviewUsesFullCatalogAndDoesNotCommitRejectedCandidate()
        {
            var catalog = Catalog(); catalog.DomainPackages.Add(Domain("dependency.base", "1.5"));
            var candidate = Domain("dependency.editor");
            candidate.ConfigureDependencies(null, new[] { new PackageDependencyRequirement
                { PackageId = "dependency.base", VersionRange = "[1,2)" } });
            var accepted = ContentCatalogWindow.PreviewRegistration(catalog, candidate);
            Assert.IsTrue(accepted.Success, string.Join("; ", accepted.Diagnostics));
            Assert.AreEqual(1, catalog.DomainPackages.Count, "Preview does not mutate or save the catalog.");
            candidate.ConfigureDependencies(null, new[] { new PackageDependencyRequirement
                { PackageId = "dependency.base", VersionRange = "[2,3)" } });
            var rejected = ContentCatalogWindow.PreviewRegistration(catalog, candidate);
            Assert.IsFalse(rejected.Success);
            StringAssert.Contains("[2,3)", string.Join("; ", rejected.Diagnostics));
            StringAssert.Contains("1.5", string.Join("; ", rejected.Diagnostics));
            Assert.AreEqual(1, catalog.DomainPackages.Count);
            catalog.DomainPackages.Add(candidate);
            Assert.IsFalse(ContentCatalogWindow.PreviewRegistration(catalog, candidate).Success,
                "An already listed asset must still be rejected when its edited dependency is invalid.");
        }

        [Test]
        public void CombinedScenarioUsesValidatedNpcAndRegionDependenciesAndRejectsDivergentDomainReference()
        {
            var npc = Package("NPC"); var region = Package("Region");
            var campus = Domain("dependency.campus"); var household = Domain("dependency.household"); var trade = Domain("dependency.trade");
            campus.ConfigureDependencies(null, new[] {
                new PackageDependencyRequirement { PackageId = Manifest(npc).Id, VersionRange = "[1,2)" },
                new PackageDependencyRequirement { PackageId = Manifest(region).Id, VersionRange = "[1,2)" } });
            var catalog = Catalog(); Add(catalog, npc); Add(catalog, region);
            catalog.DomainPackages.AddRange(new[] { campus, household, trade });
            var registry = new ContentRegistry();
            var result = catalog.BuildRegistry(registry);
            Assert.IsTrue(result.Success, string.Join("; ", result.Diagnostics));
            var host = Own(new GameObject("PackageDependencyCombinedTest")); host.SetActive(false);
            var runner = host.AddComponent<SocialWorldRunner>();
            var world = new SimulationWorld("dependency.world");
            typeof(SocialWorldRunner).GetProperty("Content").SetValue(runner, registry);
            typeof(SocialWorldRunner).GetProperty("World").SetValue(runner, world);
            host.AddComponent<CampusDomainModule>(); host.AddComponent<HouseholdDomainModule>(); host.AddComponent<WorkAndTradeDomainModule>();
            var combined = host.AddComponent<CombinedDemoScenario>();
            combined.Configure(campus, household, trade, null, null);
            combined.Initialize(runner, world);
            Assert.AreSame(registry, combined.Registry);
            Assert.AreEqual(5, combined.Registry.Packages.Count);
            string before = registry.GetPackageFingerprint(campus.PackageId);
            campus.ConfigureDependencies(null, new[] { new PackageDependencyRequirement
                { PackageId = Manifest(npc).Id, VersionRange = "[9,10)" } });
            Assert.Throws<InvalidOperationException>(() => combined.Initialize(runner, world));
            Assert.AreEqual(before, registry.GetPackageFingerprint(campus.PackageId));
            Assert.AreEqual(5, registry.Packages.Count);
        }
    }
}
