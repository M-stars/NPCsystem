using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using SocialSimulation.Core;

namespace SocialSimulation.Tests
{
    public sealed class PackageDependencyVersionTests
    {
        private static PackageManifest Package(string id, string version = "1.0.0", int schema = 2) =>
            new PackageManifest { Id = id, Version = version, SchemaVersion = schema, Kind = "Domain" };

        private static PackageManifest Consumer(string id = "consumer", string range = "[1.2,2)", string dependency = "base")
        {
            var package = Package(id);
            package.DependencyRequirements.Add(new PackageDependencyRequirement { PackageId = dependency, VersionRange = range });
            return package;
        }

        private static bool HasDiagnostic(RegistrationResult result, string code) => result.Diagnostics.Exists(item => item.Code == code);

        [Test]
        public void LegacyOpaqueVersionAndOriginalFingerprintRemainCompatible()
        {
            var registry = new ContentRegistry();
            var legacy = Package("legacy", "release-A", 1);
            Assert.IsTrue(registry.Register(legacy).Success);
            // Golden from the pre-requirement field encoding: identity/schema/kind, zero dependencies,
            // six null body fields, then zero capabilities, bindings, actions, regions and node IDs.
            const string original = "d284701291deae2c730b239b69da908ef4afa8c4ef3620c09f32157669dbfdbf";
            Assert.AreEqual(original, registry.GetPackageFingerprint("legacy"));
            legacy.DependencyRequirements = null;
            Assert.IsTrue(registry.Register(legacy).AlreadyRegistered);
            Assert.AreEqual(original, registry.GetPackageFingerprint("legacy"));
            var extension = Package("extension", "another-opaque-version", 1);
            extension.Dependencies.Add("legacy");
            Assert.IsTrue(registry.Register(extension).Success);
        }

        [Test]
        public void LegacySchemaCannotSilentlyIgnoreEvenWildcardVersionRequirements()
        {
            var registry = new ContentRegistry(); registry.Register(Package("base", "opaque", 1));
            var consumer = Consumer(range: "*"); consumer.SchemaVersion = 1;
            var rejected = registry.Register(consumer);
            Assert.IsFalse(rejected.Success); Assert.IsTrue(HasDiagnostic(rejected, "SCHEMA_VERSION"));
            Assert.AreEqual(1, registry.Packages.Count);
            consumer.SchemaVersion = 2;
            Assert.IsTrue(registry.Register(consumer).Success, "The explicit wildcard accepts a legacy opaque version.");
        }

        [TestCase("0", true)]
        [TestCase("1", true)]
        [TestCase("1.2", true)]
        [TestCase("1.2.3", true)]
        [TestCase("4294967295.2.3", true)]
        [TestCase("opaque", false)]
        [TestCase("1.2.3.4", false)]
        [TestCase("1.2-beta", false)]
        [TestCase("1.2+build", false)]
        [TestCase("-1.2", false)]
        [TestCase("1..2", false)]
        [TestCase("4294967296", false)]
        [TestCase(" 1.2", false)]
        [TestCase("١.٢", false)]
        public void SchemaTwoVersionFormatIsEnforcedBeforeContentCommit(string version, bool allowed)
        {
            var registry = new ContentRegistry();
            var result = registry.Register(Package("package", version));
            Assert.AreEqual(allowed, result.Success);
            Assert.AreEqual(allowed ? 1 : 0, registry.Packages.Count);
            if (!allowed) Assert.IsTrue(HasDiagnostic(result, "PACKAGE_VERSION"));
        }

        [TestCase("1", "1.0.0", true)]
        [TestCase("1.2", "1.2.0", true)]
        [TestCase("1.2.3", "1.2.3", true)]
        [TestCase("1", "1.1", false)]
        [TestCase("[1.2,2)", "1.1.99", false)]
        [TestCase("[1.2,2)", "1.2.0", true)]
        [TestCase("[1.2,2)", "1.99.99", true)]
        [TestCase("[1.2,2)", "2.0", false)]
        [TestCase("[2,10)", "9", true)]
        [TestCase("[2,10)", "10", false)]
        [TestCase("[1.2,)", "22.7", true)]
        [TestCase("[1.2,)", "1.1", false)]
        [TestCase("*", "release-candidate", true)]
        [TestCase("[1,2)", "release-candidate", false)]
        public void DependencyRegistrationUsesNumericBoundariesAndExplicitWildcard(string range, string actual, bool allowed)
        {
            var registry = new ContentRegistry();
            Assert.IsTrue(registry.Register(Package("base", actual, 1)).Success);
            var result = registry.Register(Consumer(range: range));
            Assert.AreEqual(allowed, result.Success);
            Assert.AreEqual(allowed, registry.Packages.ContainsKey("consumer"));
            if (!allowed)
            {
                Assert.IsTrue(HasDiagnostic(result, "DEPENDENCY_VERSION_MISMATCH"));
                Assert.IsFalse(HasDiagnostic(result, "MISSING_DEPENDENCY"));
            }
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("^1.2")]
        [TestCase("~1.2")]
        [TestCase(">=1.2 <2")]
        [TestCase("[1,2]")]
        [TestCase("(1,2)")]
        [TestCase("[,2)")]
        [TestCase("[2,1)")]
        [TestCase("[1,1.0)")]
        [TestCase("[1,2,3)")]
        public void UnsupportedOrEmptyRangesFailExplicitlyInsteadOfBecomingWildcards(string range)
        {
            var registry = new ContentRegistry(); registry.Register(Package("base"));
            var result = registry.Register(Consumer(range: range));
            Assert.IsFalse(result.Success); Assert.IsTrue(HasDiagnostic(result, "DEPENDENCY_REQUIREMENT"));
            Assert.AreEqual(1, registry.Packages.Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BatchOrdersRequiredCapabilityProviderBeforeItsConsumer(bool reverse)
        {
            var registry = new ContentRegistry(); registry.RegisterExecutor("move");
            var provider = Package("base", "1.2");
            provider.Capabilities.Add(new CapabilityDefinition { Id = "walk", ExecutorId = "move" });
            var consumer = Consumer();
            consumer.Actions.Add(new ActionDefinition { Id = "go", ExecutorId = "move", CapabilityId = "walk" });
            var result = registry.RegisterBatch(reverse ? new[] { consumer, provider } : new[] { provider, consumer });
            Assert.IsTrue(result.Success, string.Join("; ", result.Diagnostics));
            Assert.IsTrue(registry.Actions.ContainsKey("go")); Assert.AreEqual(2, registry.Packages.Count);
        }

        [Test]
        public void VersionMismatchInBatchLeavesAllExistingPackagesAndContentUntouched()
        {
            var registry = new ContentRegistry(); registry.RegisterExecutor("move");
            registry.Register(Package("existing", "opaque", 1));
            var fingerprint = registry.GetPackageFingerprint("existing");
            var provider = Package("base", "1.2");
            provider.Capabilities.Add(new CapabilityDefinition { Id = "walk", ExecutorId = "move" });
            var consumer = Consumer(range: "[2,3)");
            consumer.Actions.Add(new ActionDefinition { Id = "go", ExecutorId = "move", CapabilityId = "walk" });
            var result = registry.RegisterBatch(new[] { Package("unrelated"), consumer, provider });
            Assert.IsFalse(result.Success); Assert.IsTrue(HasDiagnostic(result, "DEPENDENCY_VERSION_MISMATCH"));
            Assert.IsFalse(HasDiagnostic(result, "MISSING_DEPENDENCY"));
            Assert.AreEqual(1, registry.Packages.Count);
            Assert.AreEqual(fingerprint, registry.GetPackageFingerprint("existing"));
            Assert.AreEqual(0, registry.Capabilities.Count); Assert.AreEqual(0, registry.Actions.Count);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void DuplicateDependencyIdsAcrossEitherDeclarationAreRejected(int form)
        {
            var registry = new ContentRegistry(); registry.Register(Package("base"));
            var consumer = Package("consumer");
            if (form != 2) consumer.Dependencies.Add("base");
            if (form == 0) consumer.Dependencies.Add("base");
            else consumer.DependencyRequirements.Add(new PackageDependencyRequirement { PackageId = "base" });
            if (form == 2) consumer.DependencyRequirements.Add(new PackageDependencyRequirement { PackageId = "base", VersionRange = "[1,2)" });
            var result = registry.Register(consumer);
            Assert.IsFalse(result.Success); Assert.IsTrue(HasDiagnostic(result, "DUPLICATE_DEPENDENCY"));
            Assert.AreEqual(1, registry.Packages.Count);
        }

        [Test]
        public void SelfMissingAndCyclicDependenciesCannotPartiallyRegister()
        {
            var registry = new ContentRegistry();
            var self = registry.Register(Consumer("self", "*", "self"));
            Assert.IsFalse(self.Success); Assert.IsTrue(HasDiagnostic(self, "SELF_DEPENDENCY"));
            var missing = registry.Register(Consumer("missing", "*", "not.installed"));
            Assert.IsFalse(missing.Success); Assert.IsTrue(HasDiagnostic(missing, "MISSING_DEPENDENCY"));
            var cyclic = registry.RegisterBatch(new[] { Consumer("a", "*", "b"), Consumer("b", "*", "a") });
            Assert.IsFalse(cyclic.Success); Assert.AreEqual(0, registry.Packages.Count);
            var malformed = Package("null.entry"); malformed.DependencyRequirements.Add(null);
            var invalid = registry.Register(malformed);
            Assert.IsFalse(invalid.Success); Assert.IsTrue(HasDiagnostic(invalid, "DEPENDENCY_REQUIREMENT"));
        }

        [Test]
        public void FingerprintIncludesEveryRequirementAndIsIndependentOfDeclarationOrder()
        {
            var original = Package("consumer");
            original.DependencyRequirements.Add(new PackageDependencyRequirement { PackageId = "a", VersionRange = "[1,2)" });
            original.DependencyRequirements.Add(new PackageDependencyRequirement { PackageId = "b", VersionRange = "1.0" });
            var registry = new ContentRegistry(); registry.RegisterBatch(new[] { Package("a"), Package("b"), original });
            var fingerprint = registry.GetPackageFingerprint("consumer");
            original.DependencyRequirements.Reverse();
            Assert.IsTrue(registry.Register(original).AlreadyRegistered);
            Assert.AreEqual(fingerprint, registry.GetPackageFingerprint("consumer"));
            original.DependencyRequirements[0].VersionRange = "[1,3)";
            var rejected = registry.Register(original);
            Assert.IsFalse(rejected.Success); Assert.IsTrue(HasDiagnostic(rejected, "PACKAGE_CONFLICT"));
            var changed = new ContentRegistry();
            Assert.IsTrue(changed.RegisterBatch(new[] { Package("a"), Package("b"), original }).Success);
            Assert.AreNotEqual(fingerprint, changed.GetPackageFingerprint("consumer"));
        }

        [Test]
        public void CallerAndPublicViewAliasesCannotChangeRegisteredVersionsOrRequirements()
        {
            var registry = new ContentRegistry(); var provider = Package("base", "1.2"); var consumer = Consumer();
            Assert.IsTrue(registry.RegisterBatch(new[] { provider, consumer }).Success);
            var fingerprint = registry.GetPackageFingerprint("consumer");
            provider.Version = "9"; consumer.DependencyRequirements[0].VersionRange = "[9,10)";
            var view = registry.Packages;
            view["base"].Version = "9";
            view["consumer"].DependencyRequirements[0].VersionRange = "*";
            Assert.AreEqual(2, registry.Packages.Count);
            Assert.AreEqual("1.2", registry.Packages["base"].Version);
            Assert.AreEqual("[1.2,2)", registry.Packages["consumer"].DependencyRequirements[0].VersionRange);
            Assert.AreEqual(fingerprint, registry.GetPackageFingerprint("consumer"));
            var attack = registry.Register(Consumer("probe", "[9,10)"));
            Assert.IsFalse(attack.Success); Assert.IsTrue(HasDiagnostic(attack, "DEPENDENCY_VERSION_MISMATCH"));
        }

        [Test]
        public void HeldPackageViewSeesLaterRegistrationWhileItsReadValuesRemainIndependent()
        {
            var registry = new ContentRegistry();
            var view = registry.Packages;
            Assert.AreEqual(0, view.Count);
            Assert.IsTrue(registry.Register(Package("base", "1.2")).Success);
            Assert.AreEqual(1, view.Count); Assert.IsTrue(view.ContainsKey("base"));
            CollectionAssert.Contains(new List<string>(view.Keys), "base");
            Assert.IsTrue(view.TryGetValue("base", out var read));
            read.Version = "9";
            Assert.IsTrue(registry.RegisterBatch(new[] { Consumer(), Package("additional") }).Success);
            Assert.AreEqual(3, view.Count);
            Assert.IsTrue(view.ContainsKey("consumer")); Assert.IsTrue(view.ContainsKey("additional"));
            Assert.AreEqual("1.2", view["base"].Version);
            Assert.IsFalse(view.TryGetValue("missing", out var absent)); Assert.IsNull(absent);
        }

        [Test]
        public void AllPublicValueEnumerationPathsReturnOwnedManifestCopies()
        {
            var registry = new ContentRegistry();
            Assert.IsTrue(registry.RegisterBatch(new[] { Package("base", "1.2"), Consumer() }).Success);
            var view = registry.Packages;
            var fingerprint = registry.GetPackageFingerprint("consumer");
            foreach (var manifest in view.Values)
            {
                manifest.Version = "88";
                manifest.DependencyRequirements.Clear();
            }
            foreach (var pair in view)
            {
                pair.Value.Version = "99";
                pair.Value.Dependencies.Add("invented");
                foreach (var requirement in pair.Value.DependencyRequirements) requirement.VersionRange = "*";
            }
            foreach (KeyValuePair<string, PackageManifest> pair in (System.Collections.IEnumerable)view)
                pair.Value.Version = "100";
            Assert.AreEqual("1.2", view["base"].Version);
            Assert.AreEqual("[1.2,2)", view["consumer"].DependencyRequirements[0].VersionRange);
            Assert.AreEqual(fingerprint, registry.GetPackageFingerprint("consumer"));
            Assert.IsFalse(registry.Register(Consumer("probe", "[88,101)")).Success);
        }

        [Test]
        public void DomainConversionPreservesSchemaAndBothDependencyFormsWithIndependentCopies()
        {
            var domain = new SocialDomainManifest { Id = "domain", Version = "1.2", SchemaVersion = 2 };
            domain.Dependencies.Add("base");
            domain.DependencyRequirements.Add(new PackageDependencyRequirement { PackageId = "other", VersionRange = "[1,2)" });
            var converted = domain.ToPackageManifest();
            Assert.AreEqual("Domain", converted.Kind); Assert.AreEqual("domain", converted.Id);
            Assert.AreEqual(2, converted.SchemaVersion); Assert.AreEqual("1.2", converted.Version);
            domain.Dependencies.Clear(); domain.DependencyRequirements[0].PackageId = "changed";
            Assert.AreEqual("base", converted.Dependencies[0]);
            Assert.AreEqual("other", converted.DependencyRequirements[0].PackageId);
            var registry = new ContentRegistry();
            Assert.IsTrue(registry.RegisterBatch(new[] { converted, Package("base"), Package("other") }).Success);
        }

        [Test]
        public void VersionComparisonAndFingerprintsDoNotDependOnCurrentCulture()
        {
            var prior = CultureInfo.CurrentCulture;
            try
            {
                string fingerprint = null;
                foreach (var culture in new[] { "en-US", "fr-FR", "tr-TR", "ar-SA" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    var registry = new ContentRegistry();
                    Assert.IsTrue(registry.RegisterBatch(new[] { Consumer(range: "[1.2,10)"), Package("base", "9.2") }).Success);
                    var current = registry.GetPackageFingerprint("consumer");
                    if (fingerprint == null) fingerprint = current; else Assert.AreEqual(fingerprint, current);
                    Assert.IsFalse(PackageVersionProtocol.TryValidateVersion("1,2", out _));
                    Assert.IsFalse(PackageVersionProtocol.Matches("[1.2,10)", "10", out _));
                }
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }
    }
}
