using System;
using System.Collections.Generic;
using NUnit.Framework;
using SocialSimulation.Core;

namespace SocialSimulation.Tests
{
    public sealed class RegionPolicyTests
    {
        private static RegionDefinition Definition()
        {
            var region = new RegionDefinition { Id = "shop", PackageId = "region.shop", Tag = "trade.shop", Capacity = 2 };
            region.Zones.Add(new RegionZoneDefinition { LocalId = "public", Tag = "public", Capacity = 2 });
            region.Points.Add(new InteractionPointDefinition { LocalId = "counter", ZoneLocalId = "public", Kind = "trade.purchase" });
            return region;
        }

        private static SimulationWorld World(int capacity = 2)
        {
            var world = new SimulationWorld("regions", 19);
            world.Places.Add("shop.1", new PlaceState
            { Id = "shop.1", DefinitionId = "shop", Capacity = capacity, OwnerCollectiveId = "owner" });
            world.Places.Add("shop.2", new PlaceState
            { Id = "shop.2", DefinitionId = "shop", Capacity = capacity, OwnerCollectiveId = "owner" });
            world.Society.Collectives.Add("owner", new CollectiveState { Id = "owner" });
            world.Society.Collectives.Add("another", new CollectiveState { Id = "another" });
            world.AddAgent(new AgentState { Id = "a", TemplateId = "human" });
            world.AddAgent(new AgentState { Id = "b", TemplateId = "human" });
            return world;
        }

        [Test]
        public void LegacyDefaultsAllowEntryWhileCapacityDoesNotDoubleCountResidents()
        {
            var world = World(1); var definition = Definition(); var place = world.Places["shop.1"];
            definition.Bounds = null; definition.Access = null; definition.EntranceLocalIds = null; definition.OpeningHours = null;
            Assert.IsEmpty(RegionDefinitions.Validate(definition));
            Assert.IsTrue(RegionAccess.Evaluate(world, place, definition, "a", 0).Allowed);
            world.Agents["a"].PlaceId = place.Id;
            var full = RegionAccess.Evaluate(world, place, definition, "b", 0);
            Assert.AreEqual(RegionAccessFailure.CapacityReached, full.Failure);
            Assert.AreEqual(1, full.Occupancy);
            Assert.IsTrue(RegionAccess.Evaluate(world, place, definition, "a", 0).Allowed);
            place.Open = false;
            Assert.AreEqual(RegionAccessFailure.Closed, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
            Assert.AreEqual(RegionAccessFailure.Closed, RegionAccess.Evaluate(world, place, definition, "unknown", double.NaN).Failure);
        }

        [Test]
        public void PeriodicOpeningUsesHalfOpenIntervalsAndExplicitOvernightSegments()
        {
            var definition = Definition();
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 22 * 3600, EndSeconds = 86400 });
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 2 * 3600 });
            Assert.IsEmpty(RegionDefinitions.Validate(definition));
            Assert.IsTrue(RegionAccess.IsOpenAt(definition, 22 * 3600));
            Assert.IsFalse(RegionAccess.IsOpenAt(definition, 2 * 3600));
            Assert.IsTrue(RegionAccess.IsOpenAt(definition, 86400));
            Assert.IsFalse(RegionAccess.IsOpenAt(definition, 86400 + 3 * 3600));
            definition.OpeningTimeOffsetSeconds = 23 * 3600;
            Assert.IsTrue(RegionAccess.IsOpenAt(definition, 0));
            Assert.IsFalse(RegionAccess.IsOpenAt(definition, 3 * 3600));
            definition.OpeningTimeOffsetSeconds = -3600;
            Assert.IsTrue(RegionAccess.IsOpenAt(definition, 0));
            Assert.IsFalse(RegionAccess.IsOpenAt(definition, 3 * 3600));
            Assert.IsFalse(RegionAccess.IsOpenAt(definition, double.NaN));
            Assert.IsFalse(RegionAccess.IsOpenAt(definition, -1));
        }

        [Test]
        public void ClosingBoundaryDetectsEntireSkippedCyclesAndIncludesOnlyTheEndingBoundary()
        {
            var definition = Definition(); definition.OpeningPeriodSeconds = 60;
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 10 });
            Assert.IsTrue(RegionAccess.IsOpenAt(definition, 0));
            Assert.IsTrue(RegionAccess.IsOpenAt(definition, 60));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 0, 60));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 0, 6000000000000));
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 0, 9));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 9, 10));
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 10, 10));
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 10, 69));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 10, 70));
        }

        [Test]
        public void ClosingBoundarySkipsAdjacentAndOvernightContinuousOpenIntervals()
        {
            var definition = Definition(); definition.OpeningPeriodSeconds = 60;
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 50, EndSeconds = 60 });
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 10 });
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 10, EndSeconds = 20 });
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 59, 61));
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 69, 71));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 79, 80));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 15, 75));
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 20, EndSeconds = 50 });
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 0, 1e20));
            definition.OpeningHours.Clear();
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 60 });
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 0, 1e20));
            definition.OpeningHours.Clear();
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 0, 1e20));
        }

        [Test]
        public void ClosingBoundaryAppliesPositiveNegativeAndLargeClockOffsetsWithoutOverflow()
        {
            var definition = Definition(); definition.OpeningPeriodSeconds = 60;
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 10 });
            definition.OpeningTimeOffsetSeconds = 5;
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 0, 4));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 4, 5));
            definition.OpeningTimeOffsetSeconds = -5;
            Assert.IsFalse(RegionAccess.CrossedClosingBoundary(definition, 0, 14));
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 14, 15));
            definition.OpeningTimeOffsetSeconds = 6000000000005;
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 4, 5));
            definition.OpeningPeriodSeconds = double.MaxValue;
            definition.OpeningTimeOffsetSeconds = double.MaxValue / 2;
            definition.OpeningHours[0].EndSeconds = double.MaxValue / 4;
            Assert.IsTrue(RegionAccess.CrossedClosingBoundary(definition, 0, double.MaxValue));
        }

        [Test]
        public void ClosingBoundaryRejectsInvalidTimeAndCalendarInsteadOfHidingConfigurationErrors()
        {
            var definition = Definition();
            Assert.Throws<ArgumentNullException>(() => RegionAccess.CrossedClosingBoundary(null, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RegionAccess.CrossedClosingBoundary(definition, double.NaN, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RegionAccess.CrossedClosingBoundary(definition, 0, double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => RegionAccess.CrossedClosingBoundary(definition, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RegionAccess.CrossedClosingBoundary(definition, 2, 1));
            definition.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 10, EndSeconds = 5 });
            Assert.Throws<ArgumentException>(() => RegionAccess.CrossedClosingBoundary(definition, 0, 1));
        }

        [Test]
        public void OwnerMembershipAndRoleRequireActiveMembershipCorrectScopeAndValidTime()
        {
            var world = World(); var definition = Definition(); var place = world.Places["shop.1"];
            definition.Access.RequireOwnerMembership = true;
            definition.Access.RequiredRoles.Add(new RegionRoleRequirement { RoleId = "staff" });
            Assert.AreEqual(RegionAccessFailure.MissingMembership, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
            var membership = world.Society.Join("a", "owner");
            Assert.AreEqual(RegionAccessFailure.MissingRole, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
            var role = world.Society.AssignRole("a", "owner", "staff", "shop.2");
            Assert.AreEqual(RegionAccessFailure.MissingRole, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
            role.ObjectScopeId = "shop.1"; role.BeginsAt = 10; role.EndsAt = 20;
            Assert.AreEqual(RegionAccessFailure.MissingRole, RegionAccess.Evaluate(world, place, definition, "a", 9).Failure);
            Assert.IsTrue(RegionAccess.Evaluate(world, place, definition, "a", 10).Allowed);
            Assert.AreEqual(RegionAccessFailure.MissingRole, RegionAccess.Evaluate(world, place, definition, "a", 20).Failure);
            membership.EndsAt = 15;
            Assert.AreEqual(RegionAccessFailure.MissingMembership, RegionAccess.Evaluate(world, place, definition, "a", 15).Failure);
            membership.EndsAt = double.MaxValue;
            world.Society.Leave("a", "owner");
            Assert.AreEqual(RegionAccessFailure.MissingMembership, RegionAccess.Evaluate(world, place, definition, "a", 12).Failure);
        }

        [Test]
        public void ExplicitRequirementsAreCombinedAndCannotUseRolesFromAnotherCollective()
        {
            var world = World(); var definition = Definition(); var place = world.Places["shop.1"];
            definition.Access.RequiredCollectiveIds.Add("owner");
            definition.Access.RequiredCollectiveIds.Add("another");
            definition.Access.RequiredRoles.Add(new RegionRoleRequirement { RoleId = "staff", CollectiveId = "owner" });
            world.Society.Join("a", "another");
            world.Society.AssignRole("a", "another", "staff");
            Assert.AreEqual(RegionAccessFailure.MissingMembership, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
            world.Society.Join("a", "owner");
            Assert.AreEqual(RegionAccessFailure.MissingRole, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
            world.Society.AssignRole("a", "owner", "staff");
            Assert.IsTrue(RegionAccess.Evaluate(world, place, definition, "a", 0).Allowed);
            world.Society.Collectives["another"].Active = false;
            Assert.AreEqual(RegionAccessFailure.MissingMembership, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
        }

        [Test]
        public void InvalidMetadataRejectsRegistrationWithoutChangingExistingRegistry()
        {
            var invalid = new Action<RegionDefinition>[]
            {
                d => d.Bounds.CenterX = float.NaN,
                d => { d.Bounds.Enabled = true; d.Bounds.SizeX = 0; },
                d => d.EntranceLocalIds.Add("../entry"),
                d => { d.EntranceLocalIds.Add("main"); d.EntranceLocalIds.Add("main"); },
                d => d.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 23 * 3600, EndSeconds = 3600 }),
                d => { d.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 1, EndSeconds = 5 });
                    d.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 4, EndSeconds = 8 }); },
                d => d.OpeningPeriodSeconds = double.PositiveInfinity,
                d => d.Access.RequiredRoles.Add(new RegionRoleRequirement()),
                d => { d.Access.RequiredCollectiveIds.Add("owner"); d.Access.RequiredCollectiveIds.Add("owner"); }
            };
            foreach (var mutate in invalid)
            {
                var registry = new ContentRegistry();
                Assert.IsTrue(registry.Register(new PackageManifest { Id = "base", Version = "1", Kind = "Domain" }).Success);
                var definition = Definition(); mutate(definition);
                Assert.IsNotEmpty(RegionDefinitions.Validate(definition));
                Assert.IsFalse(registry.Register(Package(definition)).Success);
                Assert.AreEqual(1, registry.Packages.Count);
                Assert.AreEqual(0, registry.Regions.Count);
            }
        }

        [Test]
        public void DeepCopyAndFingerprintIncludeEveryRegionPolicyField()
        {
            var source = Definition();
            source.Bounds.Enabled = true; source.Bounds.SizeX = 8;
            source.EntranceLocalIds.Add("main");
            source.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 100 });
            source.Access.RequiredCollectiveIds.Add("owner");
            source.Access.RequiredRoles.Add(new RegionRoleRequirement { RoleId = "staff", CollectiveId = "owner" });
            var registry = new ContentRegistry(); Assert.IsTrue(registry.Register(Package(source)).Success);
            var baseline = registry.GetPackageFingerprint(source.PackageId);
            var mutations = new Action<RegionDefinition>[]
            {
                d => d.Bounds.CenterZ = 1,
                d => d.Bounds.SizeX = 9,
                d => d.Bounds.Enabled = false,
                d => d.EntranceLocalIds.Add("rear"),
                d => d.OpeningHours[0].EndSeconds = 200,
                d => d.OpeningPeriodSeconds = 3600,
                d => d.OpeningTimeOffsetSeconds = 10,
                d => d.Access.RequireOwnerMembership = true,
                d => d.Access.RequiredCollectiveIds[0] = "another",
                d => d.Access.RequiredRoles[0].RoleId = "manager",
                d => d.Access.RequiredRoles[0].CollectiveId = "another"
            };
            foreach (var mutate in mutations)
            {
                var copy = RegionDefinitions.Clone(source); mutate(copy);
                var changed = new ContentRegistry(); Assert.IsTrue(changed.Register(Package(copy)).Success);
                Assert.AreNotEqual(baseline, changed.GetPackageFingerprint(copy.PackageId));
                Assert.IsFalse(registry.Register(Package(copy)).Success, "Same-version semantic changes must be rejected.");
            }
            source.Bounds.CenterZ = 12; source.OpeningHours[0].EndSeconds = 300;
            source.Access.RequiredRoles[0].RoleId = "mutated";
            Assert.AreEqual(baseline, registry.GetPackageFingerprint(source.PackageId));
            Assert.AreEqual("staff", registry.Regions["shop"].Access.RequiredRoles[0].RoleId);
            var legacy = Definition(); legacy.Bounds = null; legacy.Access = null;
            legacy.EntranceLocalIds = null; legacy.OpeningHours = null;
            var normalized = RegionDefinitions.Clone(legacy);
            Assert.IsNotNull(normalized.Bounds); Assert.IsFalse(normalized.Bounds.Enabled);
            Assert.IsNotNull(normalized.Access.RequiredRoles); Assert.IsNotNull(normalized.Access.RequiredCollectiveIds);
            Assert.IsNotNull(normalized.EntranceLocalIds); Assert.IsNotNull(normalized.OpeningHours);
        }

        [Test]
        public void CalendarAndSetOrderingDoesNotChangePackageFingerprint()
        {
            var source = Definition(); source.EntranceLocalIds.AddRange(new[] { "front", "rear" });
            source.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 0, EndSeconds = 100 });
            source.OpeningHours.Add(new RegionOpeningInterval { StartSeconds = 100, EndSeconds = 200 });
            source.Access.RequiredCollectiveIds.AddRange(new[] { "owner", "another" });
            source.Access.RequiredRoles.Add(new RegionRoleRequirement { RoleId = "staff", CollectiveId = "owner" });
            source.Access.RequiredRoles.Add(new RegionRoleRequirement { RoleId = "guest", CollectiveId = "another" });
            var reordered = RegionDefinitions.Clone(source); reordered.EntranceLocalIds.Reverse(); reordered.OpeningHours.Reverse();
            reordered.Access.RequiredCollectiveIds.Reverse(); reordered.Access.RequiredRoles.Reverse();
            var registry = new ContentRegistry(); Assert.IsTrue(registry.Register(Package(source)).Success);
            Assert.IsTrue(registry.Register(Package(reordered)).AlreadyRegistered);
        }

        [Test]
        public void EvaluationUsesAuthoritativePlaceAndExplainsBindingFailures()
        {
            var world = World(); var definition = Definition(); var place = world.Places["shop.1"];
            place.Open = false;
            var forged = new PlaceState { Id = place.Id, Open = true, Capacity = 100 };
            Assert.AreEqual(RegionAccessFailure.Closed, RegionAccess.Evaluate(world, forged, definition, "a", 0).Failure);
            place.Open = true;
            Assert.AreEqual(RegionAccessFailure.UnknownActor, RegionAccess.Evaluate(world, place, definition, "missing", 0).Failure);
            Assert.AreEqual(RegionAccessFailure.InvalidTime, RegionAccess.Evaluate(world, place, definition, "a", double.PositiveInfinity).Failure);
            definition.Id = "other";
            Assert.AreEqual(RegionAccessFailure.DefinitionMismatch, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
            definition.Id = "shop"; definition.Access.RequireOwnerMembership = true; place.OwnerCollectiveId = null;
            Assert.AreEqual(RegionAccessFailure.MissingOwnerCollective, RegionAccess.Evaluate(world, place, definition, "a", 0).Failure);
        }

        [Test]
        public void OptionalLocalBoundsIncludeTheirFacesAndRejectNonfiniteCoordinates()
        {
            var bounds = new RegionBounds { Enabled = true, CenterX = 2, SizeX = 4, SizeY = 2, SizeZ = 6 };
            Assert.IsTrue(bounds.Contains(0, 1, -3));
            Assert.IsFalse(bounds.Contains(-0.01f, 0, 0));
            Assert.IsFalse(bounds.Contains(float.NaN, 0, 0));
            bounds.Enabled = false;
            Assert.IsTrue(bounds.Contains(1000, 0, 0));
        }

        private static PackageManifest Package(RegionDefinition definition) => new PackageManifest
        { Id = definition.PackageId, Version = "1", Kind = "Region", Regions = new List<RegionDefinition> { definition } };
    }
}
