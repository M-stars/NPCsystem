using System;
using System.Collections.Generic;
using System.Text;

namespace SocialSimulation.Core
{
    [Serializable]
    public sealed class PackageDependencyRequirement
    {
        public string PackageId;
        public string VersionRange = "*";
        public PackageDependencyRequirement Copy() => new PackageDependencyRequirement
        { PackageId = PackageId, VersionRange = VersionRange };
    }

    [Serializable]
    public sealed class PackageManifest
    {
        public string Id;
        public string Version;
        public int SchemaVersion = 1;
        public string Kind; // NPC, Region, or Domain
        public List<string> Dependencies = new List<string>();
        public List<PackageDependencyRequirement> DependencyRequirements = new List<PackageDependencyRequirement>();
        public BodyProfile Body;
        public List<CapabilityDefinition> Capabilities = new List<CapabilityDefinition>();
        public List<AnimationBindingProfile> AnimationBindings = new List<AnimationBindingProfile>();
        public List<ActionDefinition> Actions = new List<ActionDefinition>();
        public List<RegionDefinition> Regions = new List<RegionDefinition>();
        public List<string> NodeTypeIds = new List<string>();
    }

    [Serializable]
    public sealed class CapabilityDefinition
    {
        public string Id;
        public string ExecutorId;
        public bool RequiresPresentation;
        public string BodyConstraint;
    }

    [Serializable]
    public sealed class ActionDefinition
    {
        public string Id;
        public string CapabilityId;
        public string ExecutorId;
        public string TargetKind;
        public string ResourceKind;
        public double TimeoutSeconds = 30;
        public int RetryBudget;
        public bool Required = true;
    }

    [Serializable]
    public sealed class BodyProfile
    {
        public string Id;
        public string Rig = "Humanoid";
        public float HeightMeters = 1.7f;
        public float RadiusMeters = 0.3f;
        public float WalkMetersPerSecond = 1.4f;
        public float RunMetersPerSecond = 3.0f;
    }

    [Serializable]
    public sealed class AnimationBindingProfile
    {
        public string Id;
        public string CapabilityId;
        public string AnimatorState;
        public bool Loop;
        public bool RootMotion;
        public double ExpectedSeconds;
    }

    [Serializable]
    public sealed class NpcTemplate
    {
        public string Id;
        public string PackageId;
        public string BodyProfileId;
        public List<string> CapabilityIds = new List<string>();
        public PersonalityProfile Personality = new PersonalityProfile();
    }

    [Serializable]
    public sealed class RegionDefinition
    {
        public string Id;
        public string PackageId;
        public string Tag;
        public int Capacity;
        public List<RegionZoneDefinition> Zones = new List<RegionZoneDefinition>();
        public List<InteractionPointDefinition> Points = new List<InteractionPointDefinition>();
        public RegionBounds Bounds = new RegionBounds();
        public List<string> EntranceLocalIds = new List<string>();
        public double OpeningPeriodSeconds = 86400;
        public double OpeningTimeOffsetSeconds;
        public List<RegionOpeningInterval> OpeningHours = new List<RegionOpeningInterval>();
        public RegionAccessPolicy Access = new RegionAccessPolicy();
    }

    [Serializable]
    public sealed class RegionZoneDefinition
    {
        public string LocalId;
        public string Tag;
        public int Capacity = 1;
    }

    [Serializable]
    public sealed class InteractionPointDefinition
    {
        public string LocalId;
        public string ZoneLocalId;
        public string Kind;
        public int Capacity = 1;
    }

    [Serializable]
    public sealed class SocialDomainManifest
    {
        public string Id;
        public string Version;
        public int SchemaVersion = 1;
        public List<string> Dependencies = new List<string>();
        public List<PackageDependencyRequirement> DependencyRequirements = new List<PackageDependencyRequirement>();
        public List<string> RoleIds = new List<string>();
        public List<string> RelationTypeIds = new List<string>();
        public List<string> ActivityTypeIds = new List<string>();
        public List<string> ActionIds = new List<string>();

        public PackageManifest ToPackageManifest()
        {
            var manifest = new PackageManifest { Id = Id, Version = Version, SchemaVersion = SchemaVersion, Kind = "Domain" };
            if (Dependencies != null) manifest.Dependencies.AddRange(Dependencies);
            if (DependencyRequirements != null)
                foreach (var requirement in DependencyRequirements) manifest.DependencyRequirements.Add(requirement?.Copy());
            return manifest;
        }
    }

    public sealed class RegistrationResult
    {
        public bool Success;
        public bool AlreadyRegistered;
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
    }

    /// <summary>Validates the entire package before committing any content.</summary>
    public sealed class ContentRegistry
    {
        private sealed class PackageView : IReadOnlyDictionary<string, PackageManifest>
        {
            private readonly Dictionary<string, PackageManifest> source;
            public PackageView(Dictionary<string, PackageManifest> source) { this.source = source; }
            public int Count => source.Count;
            public IEnumerable<string> Keys => source.Keys;
            public IEnumerable<PackageManifest> Values
            {
                get { foreach (var manifest in source.Values) yield return CloneManifest(manifest); }
            }
            public PackageManifest this[string key] => CloneManifest(source[key]);
            public bool ContainsKey(string key) => source.ContainsKey(key);
            public bool TryGetValue(string key, out PackageManifest value)
            {
                bool found = source.TryGetValue(key, out var manifest);
                value = found ? CloneManifest(manifest) : null;
                return found;
            }
            public IEnumerator<KeyValuePair<string, PackageManifest>> GetEnumerator()
            {
                foreach (var pair in source)
                    yield return new KeyValuePair<string, PackageManifest>(pair.Key, CloneManifest(pair.Value));
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private readonly Dictionary<string, PackageManifest> packages = new Dictionary<string, PackageManifest>(StringComparer.Ordinal);
        private readonly Dictionary<string, CapabilityDefinition> capabilities = new Dictionary<string, CapabilityDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, ActionDefinition> actions = new Dictionary<string, ActionDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, RegionDefinition> regions = new Dictionary<string, RegionDefinition>(StringComparer.Ordinal);
        private readonly HashSet<string> executors = new HashSet<string>(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, PackageManifest> publicPackages;

        public ContentRegistry() { publicPackages = new PackageView(packages); }

        public IReadOnlyDictionary<string, PackageManifest> Packages => publicPackages;
        public IReadOnlyDictionary<string, ActionDefinition> Actions { get { return actions; } }
        public IReadOnlyDictionary<string, CapabilityDefinition> Capabilities { get { return capabilities; } }
        public IReadOnlyDictionary<string, RegionDefinition> Regions { get { return regions; } }

        public void RegisterExecutor(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Executor ID is required.");
            executors.Add(id);
        }

        public RegistrationResult Register(PackageManifest manifest)
        {
            var result = new RegistrationResult();
            if (manifest == null) { result.Diagnostics.Add(new Diagnostic("NULL_PACKAGE", "manifest", "Package is null.")); return result; }
            if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version))
                result.Diagnostics.Add(new Diagnostic("INVALID_ID", "manifest", "ID and version are required."));
            if (manifest.SchemaVersion != 1 && manifest.SchemaVersion != 2)
                result.Diagnostics.Add(new Diagnostic("SCHEMA_VERSION", manifest.Id, "Unsupported schema version."));
            if (manifest.SchemaVersion == 1 && manifest.DependencyRequirements != null && manifest.DependencyRequirements.Count > 0)
                result.Diagnostics.Add(new Diagnostic("SCHEMA_VERSION", manifest.Id, "Version requirements need package schema 2."));
            if (manifest.SchemaVersion == 2 && !PackageVersionProtocol.TryValidateVersion(manifest.Version, out var versionError))
                result.Diagnostics.Add(new Diagnostic("PACKAGE_VERSION", manifest.Id, versionError));
            if (string.IsNullOrWhiteSpace(manifest.Kind) || (manifest.Kind != "NPC" && manifest.Kind != "Region" && manifest.Kind != "Domain"))
                result.Diagnostics.Add(new Diagnostic("PACKAGE_KIND", manifest.Id, "Expected NPC, Region, or Domain."));
            if (packages.TryGetValue(manifest.Id ?? "", out var old))
            {
                if (old.Version == manifest.Version && Fingerprint(old) == Fingerprint(manifest) && result.Diagnostics.Count == 0)
                { result.Success = true; result.AlreadyRegistered = true; return result; }
                result.Diagnostics.Add(new Diagnostic("PACKAGE_CONFLICT", manifest.Id, "Another version is already registered."));
            }
            ValidateDependencies(manifest, result);

            var pendingCapabilities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in manifest.Capabilities ?? new List<CapabilityDefinition>())
            {
                if (capability == null || string.IsNullOrWhiteSpace(capability.Id))
                { result.Diagnostics.Add(new Diagnostic("INVALID_CAPABILITY", manifest.Id, "Capability ID is required.")); continue; }
                if (!pendingCapabilities.Add(capability.Id))
                    result.Diagnostics.Add(new Diagnostic("DUPLICATE_CAPABILITY", capability.Id,
                        "Capability ID occurs twice in one package."));
                else if (capabilities.TryGetValue(capability.Id, out var existingCapability) &&
                    !SameCapability(existingCapability, capability))
                    result.Diagnostics.Add(new Diagnostic("CAPABILITY_CONFLICT", capability.Id,
                        "The same semantic capability has an incompatible definition."));
                if (string.IsNullOrWhiteSpace(capability.ExecutorId) || !executors.Contains(capability.ExecutorId))
                    result.Diagnostics.Add(new Diagnostic("MISSING_EXECUTOR", capability.Id, capability.ExecutorId ?? "null"));
                if (!string.IsNullOrEmpty(capability.BodyConstraint) &&
                    capability.BodyConstraint != "Any" && capability.BodyConstraint != "Humanoid" &&
                    capability.BodyConstraint != "Generic")
                    result.Diagnostics.Add(new Diagnostic("INVALID_BODY_CONSTRAINT", capability.Id,
                        "Body constraint must be Any, Humanoid or Generic."));
                if (manifest.Body != null && !string.IsNullOrEmpty(capability.BodyConstraint) &&
                    capability.BodyConstraint != "Any" && capability.BodyConstraint != manifest.Body.Rig)
                    result.Diagnostics.Add(new Diagnostic("CAPABILITY_BODY_MISMATCH", capability.Id,
                        "Capability body constraint does not match package rig."));
            }
            var pendingActions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in manifest.Actions ?? new List<ActionDefinition>())
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Id))
                { result.Diagnostics.Add(new Diagnostic("INVALID_ACTION", manifest.Id, "Action ID is required.")); continue; }
                if (!pendingActions.Add(action.Id))
                    result.Diagnostics.Add(new Diagnostic("DUPLICATE_ACTION", action.Id,
                        "Action ID occurs twice in one package."));
                else if (actions.TryGetValue(action.Id, out var existingAction) &&
                    !SameAction(existingAction, action))
                    result.Diagnostics.Add(new Diagnostic("ACTION_CONFLICT", action.Id,
                        "The same semantic action has an incompatible definition."));
                if (!string.IsNullOrEmpty(action.CapabilityId) && !pendingCapabilities.Contains(action.CapabilityId) && !capabilities.ContainsKey(action.CapabilityId))
                    result.Diagnostics.Add(new Diagnostic("MISSING_CAPABILITY", action.Id, action.CapabilityId));
                if (string.IsNullOrWhiteSpace(action.ExecutorId) || !executors.Contains(action.ExecutorId))
                    result.Diagnostics.Add(new Diagnostic("MISSING_EXECUTOR", action.Id, action.ExecutorId ?? "null"));
                if (action.TimeoutSeconds <= 0 || double.IsNaN(action.TimeoutSeconds) ||
                    double.IsInfinity(action.TimeoutSeconds))
                    result.Diagnostics.Add(new Diagnostic("INVALID_TIMEOUT", action.Id, "Timeout must be finite and positive."));
            }
            if (manifest.Body != null && (string.IsNullOrWhiteSpace(manifest.Body.Id) ||
                manifest.Body.Rig != "Humanoid" && manifest.Body.Rig != "Generic" ||
                !PositiveFinite(manifest.Body.HeightMeters) || !PositiveFinite(manifest.Body.RadiusMeters) ||
                !PositiveFinite(manifest.Body.WalkMetersPerSecond) ||
                !PositiveFinite(manifest.Body.RunMetersPerSecond)))
                result.Diagnostics.Add(new Diagnostic("INVALID_BODY", manifest.Id, "Body profile is incomplete."));
            var animationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in manifest.AnimationBindings ?? new List<AnimationBindingProfile>())
            {
                if (binding == null || string.IsNullOrWhiteSpace(binding.Id) ||
                    !animationIds.Add(binding.Id) || string.IsNullOrWhiteSpace(binding.AnimatorState) ||
                    (!pendingCapabilities.Contains(binding.CapabilityId ?? string.Empty) &&
                     !capabilities.ContainsKey(binding.CapabilityId ?? string.Empty)))
                    result.Diagnostics.Add(new Diagnostic("INVALID_ANIMATION_BINDING", manifest.Id,
                        "Animation binding must have a unique ID, state and registered capability."));
            }
            if (manifest.Kind == "Region" && (manifest.Regions == null || manifest.Regions.Count != 1))
                result.Diagnostics.Add(new Diagnostic("REGION_DEFINITIONS_REQUIRED", manifest.Id,
                    "A region package must register exactly one region definition."));
            var pendingRegions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in manifest.Regions ?? new List<RegionDefinition>())
            {
                if (region == null || string.IsNullOrWhiteSpace(region.Id) ||
                    !pendingRegions.Add(region.Id) || regions.ContainsKey(region.Id) ||
                    region.PackageId != manifest.Id)
                { result.Diagnostics.Add(new Diagnostic("INVALID_REGION", manifest.Id,
                    "Region ID or package binding is invalid or duplicated.")); continue; }
                result.Diagnostics.AddRange(RegionDefinitions.Validate(region));
            }
            if (result.Diagnostics.Count != 0) return result;
            var stored = CloneManifest(manifest);
            packages.Add(stored.Id, stored);
            foreach (var capability in stored.Capabilities)
                if (!capabilities.ContainsKey(capability.Id)) capabilities.Add(capability.Id, capability);
            foreach (var action in stored.Actions)
                if (!actions.ContainsKey(action.Id)) actions.Add(action.Id, action);
            foreach (var region in stored.Regions) regions.Add(region.Id, region);
            result.Success = true;
            return result;
        }

        /// <summary>Checks the entire batch against current content before changing the registry.</summary>
        public RegistrationResult RegisterBatch(IEnumerable<PackageManifest> manifests)
        {
            var result = new RegistrationResult { Success = true };
            if (manifests == null)
            {
                result.Success = false;
                result.Diagnostics.Add(new Diagnostic("NULL_BATCH", "manifests", "Package batch is null."));
                return result;
            }
            var trial = new ContentRegistry();
            foreach (var id in executors) trial.executors.Add(id);
            foreach (var pair in packages) trial.packages.Add(pair.Key, pair.Value);
            foreach (var pair in capabilities) trial.capabilities.Add(pair.Key, pair.Value);
            foreach (var pair in actions) trial.actions.Add(pair.Key, pair.Value);
            foreach (var pair in regions) trial.regions.Add(pair.Key, pair.Value);
            var remaining = new List<PackageManifest>(manifests);
            while (remaining.Count > 0)
            {
                var progressed = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var manifest = remaining[i];
                    if (manifest != null && !DependenciesAvailable(manifest, trial.packages)) continue;
                    var checkedPackage = trial.Register(manifest);
                    result.Diagnostics.AddRange(checkedPackage.Diagnostics);
                    remaining.RemoveAt(i);
                    progressed = true;
                }
                if (progressed) continue;
                // Nothing else can be registered: report missing or cyclic dependencies.
                foreach (var manifest in remaining)
                    result.Diagnostics.AddRange(trial.Register(manifest).Diagnostics);
                break;
            }
            if (result.Diagnostics.Count != 0) { result.Success = false; return result; }
            packages.Clear(); capabilities.Clear(); actions.Clear(); regions.Clear();
            foreach (var pair in trial.packages) packages.Add(pair.Key, pair.Value);
            foreach (var pair in trial.capabilities) capabilities.Add(pair.Key, pair.Value);
            foreach (var pair in trial.actions) actions.Add(pair.Key, pair.Value);
            foreach (var pair in trial.regions) regions.Add(pair.Key, pair.Value);
            return result;
        }

        private static bool DependenciesAvailable(PackageManifest manifest,
            Dictionary<string, PackageManifest> available)
        {
            if (manifest.Dependencies != null)
                foreach (var dependency in manifest.Dependencies)
                    if (!available.ContainsKey(dependency ?? string.Empty)) return false;
            if (manifest.DependencyRequirements != null)
                foreach (var requirement in manifest.DependencyRequirements)
                    if (requirement != null && !available.ContainsKey(requirement.PackageId ?? string.Empty)) return false;
            return true;
        }

        private void ValidateDependencies(PackageManifest manifest, RegistrationResult result)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (manifest.Dependencies != null)
                foreach (var dependency in manifest.Dependencies)
                    ValidateDependencyId(manifest.Id, dependency, declared, result);
            if (manifest.DependencyRequirements == null) return;
            foreach (var requirement in manifest.DependencyRequirements)
            {
                if (requirement == null)
                { result.Diagnostics.Add(new Diagnostic("DEPENDENCY_REQUIREMENT", manifest.Id, "Dependency requirement is null.")); continue; }
                bool available = ValidateDependencyId(manifest.Id, requirement.PackageId, declared, result);
                if (!PackageVersionProtocol.TryValidateRange(requirement.VersionRange, out var rangeError))
                {
                    result.Diagnostics.Add(new Diagnostic("DEPENDENCY_REQUIREMENT", manifest.Id,
                        requirement.PackageId + ": " + rangeError));
                    continue;
                }
                if (available && !PackageVersionProtocol.Matches(requirement.VersionRange, packages[requirement.PackageId].Version, out var mismatch))
                    result.Diagnostics.Add(new Diagnostic("DEPENDENCY_VERSION_MISMATCH", manifest.Id,
                        "Dependency " + requirement.PackageId + " requires " + requirement.VersionRange +
                        "; registered " + packages[requirement.PackageId].Version + ". " + mismatch));
            }
        }

        private bool ValidateDependencyId(string packageId, string dependencyId, HashSet<string> declared, RegistrationResult result)
        {
            if (string.IsNullOrWhiteSpace(dependencyId))
            { result.Diagnostics.Add(new Diagnostic("MISSING_DEPENDENCY", packageId, "Dependency ID is missing.")); return false; }
            if (!declared.Add(dependencyId))
            { result.Diagnostics.Add(new Diagnostic("DUPLICATE_DEPENDENCY", packageId, dependencyId)); return false; }
            if (dependencyId == packageId)
            { result.Diagnostics.Add(new Diagnostic("SELF_DEPENDENCY", packageId, dependencyId)); return false; }
            if (!packages.ContainsKey(dependencyId))
            { result.Diagnostics.Add(new Diagnostic("MISSING_DEPENDENCY", packageId, dependencyId)); return false; }
            return true;
        }

        private static bool SameCapability(CapabilityDefinition first, CapabilityDefinition second)
        {
            return first.Id == second.Id && first.ExecutorId == second.ExecutorId &&
                first.RequiresPresentation == second.RequiresPresentation &&
                first.BodyConstraint == second.BodyConstraint;
        }

        private static bool PositiveFinite(float value)
        { return value > 0 && !float.IsNaN(value) && !float.IsInfinity(value); }

        private static bool SameAction(ActionDefinition first, ActionDefinition second)
        {
            return first.Id == second.Id && first.CapabilityId == second.CapabilityId &&
                first.ExecutorId == second.ExecutorId && first.TargetKind == second.TargetKind &&
                first.ResourceKind == second.ResourceKind &&
                first.TimeoutSeconds == second.TimeoutSeconds && first.RetryBudget == second.RetryBudget &&
                first.Required == second.Required;
        }

        private static PackageManifest CloneManifest(PackageManifest source)
        {
            var copy = new PackageManifest { Id = source.Id, Version = source.Version,
                SchemaVersion = source.SchemaVersion, Kind = source.Kind };
            if (source.Dependencies != null) copy.Dependencies.AddRange(source.Dependencies);
            if (source.DependencyRequirements != null)
                foreach (var requirement in source.DependencyRequirements) copy.DependencyRequirements.Add(requirement?.Copy());
            if (source.NodeTypeIds != null) copy.NodeTypeIds.AddRange(source.NodeTypeIds);
            if (source.Body != null) copy.Body = new BodyProfile
            { Id = source.Body.Id, Rig = source.Body.Rig, HeightMeters = source.Body.HeightMeters,
                RadiusMeters = source.Body.RadiusMeters, WalkMetersPerSecond = source.Body.WalkMetersPerSecond,
                RunMetersPerSecond = source.Body.RunMetersPerSecond };
            if (source.Capabilities != null)
                foreach (var item in source.Capabilities) copy.Capabilities.Add(new CapabilityDefinition
                { Id = item.Id, ExecutorId = item.ExecutorId, RequiresPresentation = item.RequiresPresentation, BodyConstraint = item.BodyConstraint });
            if (source.AnimationBindings != null)
                foreach (var item in source.AnimationBindings) copy.AnimationBindings.Add(new AnimationBindingProfile
                { Id = item.Id, CapabilityId = item.CapabilityId, AnimatorState = item.AnimatorState,
                    Loop = item.Loop, RootMotion = item.RootMotion, ExpectedSeconds = item.ExpectedSeconds });
            if (source.Actions != null)
                foreach (var item in source.Actions) copy.Actions.Add(new ActionDefinition
                { Id = item.Id, CapabilityId = item.CapabilityId, ExecutorId = item.ExecutorId,
                    TargetKind = item.TargetKind, ResourceKind = item.ResourceKind, TimeoutSeconds = item.TimeoutSeconds,
                    RetryBudget = item.RetryBudget, Required = item.Required });
            if (source.Regions != null)
                foreach (var item in source.Regions)
                    copy.Regions.Add(RegionDefinitions.Clone(item));
            return copy;
        }

        /// <summary>Stable SHA-256 of a registered package's complete Core manifest, including its version.</summary>
        public string GetPackageFingerprint(string packageId)
        {
            if (!packages.TryGetValue(packageId ?? string.Empty, out var manifest))
                throw new KeyNotFoundException("Package is not registered: " + packageId);
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(Fingerprint(manifest)));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes) result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static string Fingerprint(PackageManifest manifest)
        {
            var text = new StringBuilder();
            AppendField(text, manifest.Id); AppendField(text, manifest.Version);
            AppendField(text, manifest.SchemaVersion.ToString()); AppendField(text, manifest.Kind);
            var dependencies = manifest.Dependencies == null ? new List<string>() : new List<string>(manifest.Dependencies);
            dependencies.Sort(StringComparer.Ordinal);
            AppendField(text, dependencies.Count.ToString());
            foreach (var item in dependencies) AppendField(text, item);
            AppendField(text, manifest.Body?.Id); AppendField(text, manifest.Body?.Rig);
            AppendField(text, manifest.Body?.HeightMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            AppendField(text, manifest.Body?.RadiusMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            AppendField(text, manifest.Body?.WalkMetersPerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            AppendField(text, manifest.Body?.RunMetersPerSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            var caps = manifest.Capabilities == null ? new List<CapabilityDefinition>() :
                new List<CapabilityDefinition>(manifest.Capabilities);
            caps.Sort((a, b) => StringComparer.Ordinal.Compare(a?.Id, b?.Id));
            AppendField(text, caps.Count.ToString());
            foreach (var item in caps)
            {
                AppendField(text, item?.Id); AppendField(text, item?.ExecutorId);
                AppendField(text, item?.RequiresPresentation.ToString()); AppendField(text, item?.BodyConstraint);
            }
            var bindings = manifest.AnimationBindings == null ? new List<AnimationBindingProfile>() :
                new List<AnimationBindingProfile>(manifest.AnimationBindings);
            bindings.Sort((a, b) => StringComparer.Ordinal.Compare(a?.Id, b?.Id));
            AppendField(text, bindings.Count.ToString());
            foreach (var binding in bindings)
            {
                AppendField(text, binding?.Id); AppendField(text, binding?.CapabilityId);
                AppendField(text, binding?.AnimatorState); AppendField(text, binding?.Loop.ToString());
                AppendField(text, binding?.RootMotion.ToString());
                AppendField(text, binding?.ExpectedSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            var actionItems = manifest.Actions == null ? new List<ActionDefinition>() :
                new List<ActionDefinition>(manifest.Actions);
            actionItems.Sort((a, b) => StringComparer.Ordinal.Compare(a?.Id, b?.Id));
            AppendField(text, actionItems.Count.ToString());
            foreach (var item in actionItems)
            {
                AppendField(text, item?.Id); AppendField(text, item?.CapabilityId);
                AppendField(text, item?.ExecutorId); AppendField(text, item?.TargetKind);
                AppendField(text, item?.ResourceKind); AppendField(text, item?.TimeoutSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                AppendField(text, item?.RetryBudget.ToString()); AppendField(text, item?.Required.ToString());
            }
            var regionItems = manifest.Regions == null ? new List<RegionDefinition>() :
                new List<RegionDefinition>(manifest.Regions);
            regionItems.Sort((a, b) => StringComparer.Ordinal.Compare(a?.Id, b?.Id));
            AppendField(text, regionItems.Count.ToString());
            foreach (var region in regionItems)
            {
                AppendField(text, region?.Id); AppendField(text, region?.PackageId);
                AppendField(text, region?.Tag); AppendField(text, region?.Capacity.ToString());
                AppendRegionPolicyFingerprint(text, region == null ? null : RegionDefinitions.Clone(region));
                var zoneItems = region?.Zones == null ? new List<RegionZoneDefinition>() :
                    new List<RegionZoneDefinition>(region.Zones);
                zoneItems.Sort((a, b) => StringComparer.Ordinal.Compare(a?.LocalId, b?.LocalId));
                AppendField(text, zoneItems.Count.ToString());
                foreach (var zone in zoneItems)
                { AppendField(text, zone?.LocalId); AppendField(text, zone?.Tag); AppendField(text, zone?.Capacity.ToString()); }
                var pointItems = region?.Points == null ? new List<InteractionPointDefinition>() :
                    new List<InteractionPointDefinition>(region.Points);
                pointItems.Sort((a, b) => StringComparer.Ordinal.Compare(a?.LocalId, b?.LocalId));
                AppendField(text, pointItems.Count.ToString());
                foreach (var point in pointItems)
                {
                    AppendField(text, point?.LocalId); AppendField(text, point?.ZoneLocalId);
                    AppendField(text, point?.Kind); AppendField(text, point?.Capacity.ToString());
                }
            }
            var nodes = manifest.NodeTypeIds == null ? new List<string>() : new List<string>(manifest.NodeTypeIds);
            nodes.Sort(StringComparer.Ordinal);
            AppendField(text, nodes.Count.ToString());
            foreach (var item in nodes) AppendField(text, item);
            // Empty requirements preserve the schema 1 fingerprint byte for byte.
            if (manifest.DependencyRequirements != null && manifest.DependencyRequirements.Count > 0)
            {
                var requirements = new List<PackageDependencyRequirement>(manifest.DependencyRequirements);
                requirements.Sort((a, b) =>
                {
                    int package = StringComparer.Ordinal.Compare(a?.PackageId, b?.PackageId);
                    return package != 0 ? package : StringComparer.Ordinal.Compare(a?.VersionRange, b?.VersionRange);
                });
                AppendField(text, "dependency-requirements-v2");
                AppendField(text, requirements.Count.ToString());
                foreach (var requirement in requirements)
                { AppendField(text, requirement?.PackageId); AppendField(text, requirement?.VersionRange); }
            }
            return text.ToString();
        }

        private static void AppendField(StringBuilder builder, string value)
        { builder.Append(value == null ? -1 : value.Length).Append(':').Append(value); }

        private static void AppendRegionPolicyFingerprint(StringBuilder text, RegionDefinition region)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            AppendField(text, region?.Bounds.Enabled.ToString());
            AppendField(text, region?.Bounds.CenterX.ToString("R", invariant));
            AppendField(text, region?.Bounds.CenterY.ToString("R", invariant));
            AppendField(text, region?.Bounds.CenterZ.ToString("R", invariant));
            AppendField(text, region?.Bounds.SizeX.ToString("R", invariant));
            AppendField(text, region?.Bounds.SizeY.ToString("R", invariant));
            AppendField(text, region?.Bounds.SizeZ.ToString("R", invariant));
            AppendField(text, region?.OpeningPeriodSeconds.ToString("R", invariant));
            AppendField(text, region?.OpeningTimeOffsetSeconds.ToString("R", invariant));
            AppendField(text, region?.Access.RequireOwnerMembership.ToString());
            var entrances = region == null ? new List<string>() : new List<string>(region.EntranceLocalIds);
            entrances.Sort(StringComparer.Ordinal); AppendField(text, entrances.Count.ToString());
            foreach (var entrance in entrances) AppendField(text, entrance);
            var collectives = region == null ? new List<string>() : new List<string>(region.Access.RequiredCollectiveIds);
            collectives.Sort(StringComparer.Ordinal); AppendField(text, collectives.Count.ToString());
            foreach (var collective in collectives) AppendField(text, collective);
            var hours = region == null ? new List<RegionOpeningInterval>() : new List<RegionOpeningInterval>(region.OpeningHours);
            hours.Sort((a, b) => { var start = Nullable.Compare(a?.StartSeconds, b?.StartSeconds);
                return start != 0 ? start : Nullable.Compare(a?.EndSeconds, b?.EndSeconds); });
            AppendField(text, hours.Count.ToString());
            foreach (var interval in hours)
            { AppendField(text, interval?.StartSeconds.ToString("R", invariant)); AppendField(text, interval?.EndSeconds.ToString("R", invariant)); }
            var roles = region == null ? new List<RegionRoleRequirement>() : new List<RegionRoleRequirement>(region.Access.RequiredRoles);
            roles.Sort((a, b) => { var scope = StringComparer.Ordinal.Compare(a?.CollectiveId ?? "", b?.CollectiveId ?? "");
                return scope != 0 ? scope : StringComparer.Ordinal.Compare(a?.RoleId, b?.RoleId); });
            AppendField(text, roles.Count.ToString());
            foreach (var role in roles)
            { AppendField(text, role?.RoleId); AppendField(text, role?.CollectiveId ?? ""); }
        }
    }
}
