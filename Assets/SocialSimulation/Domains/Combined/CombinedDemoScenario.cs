using System;
using System.Collections.Generic;
using SocialSimulation.Core;
using SocialSimulation.Domains.Campus;
using SocialSimulation.Domains.Household;
using SocialSimulation.Domains.WorkAndTrade;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Domains.Combined
{
    /// <summary>Sample composition root. Each domain can also run without this component.</summary>
    [DisallowMultipleComponent]
    public sealed class CombinedDemoScenario : MonoBehaviour, ISocialScenario
    {
        [SerializeField] private SocialDomainPackageAsset campusPackage;
        [SerializeField] private SocialDomainPackageAsset householdPackage;
        [SerializeField] private SocialDomainPackageAsset workAndTradePackage;
        [SerializeField] private CampusDomainModule campus;
        [SerializeField] private HouseholdDomainModule household;
        [SerializeField] private WorkAndTradeDomainModule workAndTrade;

        public ContentRegistry Registry { get; private set; }
        public bool PurchaseCompleted => workAndTrade != null && workAndTrade.StudentPurchaseCompleted;
        public bool FamilyCareCompleted => household != null && household.CareCompleted;
        public bool WorkWagePaid => workAndTrade != null && workAndTrade.WagePaid;
        public bool ClerkPurchaseCompleted => workAndTrade != null && workAndTrade.ClerkPurchaseCompleted;
        public bool ClubCompleted => workAndTrade != null && workAndTrade.ClubCompleted;
        public string CurrentBehaviorGraphId => campus?.CurrentBehaviorGraphId;
        public int CurrentBehaviorGraphVersion => campus?.CurrentBehaviorGraphVersion ?? 0;
        public string CurrentRuleGraphId => campus?.CurrentRuleGraphId;
        public int CurrentRuleGraphVersion => campus?.CurrentRuleGraphVersion ?? 0;
        public IReadOnlyList<string> ActiveBehaviorGraphVersions => campus?.ActiveBehaviorGraphVersions ??
            (IReadOnlyList<string>)Array.Empty<string>();

        public void Configure(SocialDomainPackageAsset campusAsset, SocialDomainPackageAsset householdAsset,
            SocialDomainPackageAsset workAsset, GraphDocument behavior, GraphDocument rule)
        {
            campusPackage = campusAsset;
            householdPackage = householdAsset;
            workAndTradePackage = workAsset;
            campus = GetComponent<CampusDomainModule>();
            household = GetComponent<HouseholdDomainModule>();
            workAndTrade = GetComponent<WorkAndTradeDomainModule>();
            campus?.Configure(behavior, rule);
        }

        public void Initialize(SocialWorldRunner runner, SimulationWorld world)
        {
            if (campus == null) campus = GetComponent<CampusDomainModule>();
            if (household == null) household = GetComponent<HouseholdDomainModule>();
            if (workAndTrade == null) workAndTrade = GetComponent<WorkAndTradeDomainModule>();
            if (campus == null || household == null || workAndTrade == null)
                throw new InvalidOperationException("Combined sample requires all three domain modules.");
            if (runner == null || runner.Content == null || !ReferenceEquals(runner.World, world))
                throw new InvalidOperationException("Combined sample requires its initialized world's validated content registry.");
            Registry = runner.Content;
            RegisterDomain(campusPackage);
            RegisterDomain(householdPackage);
            RegisterDomain(workAndTradePackage);
        }

        private void RegisterDomain(SocialDomainPackageAsset asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.PackageId))
                throw new InvalidOperationException("Combined sample is missing a domain package asset.");
            if (!Registry.Packages.ContainsKey(asset.PackageId))
                throw new InvalidOperationException("Combined domain package is absent from the world's content catalog: " + asset.PackageId);
            // Re-registration must be idempotent against the complete validated world catalog,
            // including NPC/region dependencies and version requirements on every domain reference.
            var result = Registry.Register(asset.ToManifest().ToPackageManifest());
            if (!result.Success || !result.AlreadyRegistered)
                throw new InvalidOperationException("Domain package registration failed: " + asset.PackageId + " " +
                    string.Join("; ", result.Diagnostics));
        }

        public bool TryApplyBehaviorGraph(GraphDocument draft, DemoGraphApplyMode mode, out string error)
        {
            if (campus == null)
            { error = "校园模块尚未挂载。"; return false; }
            return campus.TryApplyBehaviorGraph(draft, mode, out error);
        }

        public bool TryApplyRuleGraph(GraphDocument draft, out string error)
        {
            if (campus == null)
            { error = "校园模块尚未挂载。"; return false; }
            return campus.TryApplyRuleGraph(draft, out error);
        }
    }
}
