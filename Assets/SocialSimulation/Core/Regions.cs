using System;
using System.Collections.Generic;

namespace SocialSimulation.Core
{
    /// <summary>Optional axis-aligned bounds in the region prefab's local coordinates, in metres.</summary>
    [Serializable]
    public sealed class RegionBounds
    {
        public bool Enabled;
        public float CenterX;
        public float CenterY;
        public float CenterZ;
        public float SizeX = 1;
        public float SizeY = 1;
        public float SizeZ = 1;

        public bool Contains(float localX, float localY, float localZ)
        {
            if (!RegionDefinitions.Finite(localX) || !RegionDefinitions.Finite(localY) || !RegionDefinitions.Finite(localZ)) return false;
            return !Enabled || (SizeX > 0 && SizeY > 0 && SizeZ > 0 &&
                Math.Abs((double)localX - CenterX) <= SizeX * 0.5 &&
                Math.Abs((double)localY - CenterY) <= SizeY * 0.5 &&
                Math.Abs((double)localZ - CenterZ) <= SizeZ * 0.5);
        }
    }

    /// <summary>A half-open interval [StartSeconds, EndSeconds) in one opening period.</summary>
    [Serializable]
    public sealed class RegionOpeningInterval
    {
        public double StartSeconds;
        public double EndSeconds;
    }

    [Serializable]
    public sealed class RegionRoleRequirement
    {
        public string RoleId;
        // Empty means the concrete place's OwnerCollectiveId.
        public string CollectiveId;
    }

    /// <summary>All configured requirements must hold. Empty rules permit any known actor.</summary>
    [Serializable]
    public sealed class RegionAccessPolicy
    {
        public bool RequireOwnerMembership;
        public List<string> RequiredCollectiveIds = new List<string>();
        public List<RegionRoleRequirement> RequiredRoles = new List<RegionRoleRequirement>();
    }

    public enum RegionAccessFailure
    {
        None, UnknownWorld, UnknownPlace, UnknownActor, InvalidTime, InvalidDefinition,
        DefinitionMismatch, Closed, OutsideOpeningHours, MissingOwnerCollective,
        MissingMembership, MissingRole, CapacityReached
    }

    public sealed class RegionAccessResult
    {
        public bool Allowed => Failure == RegionAccessFailure.None;
        public RegionAccessFailure Failure { get; }
        public string Reason { get; }
        public int Occupancy { get; }
        public int Capacity { get; }
        internal RegionAccessResult(RegionAccessFailure failure, string reason, int occupancy, int capacity)
        { Failure = failure; Reason = reason; Occupancy = occupancy; Capacity = capacity; }
    }

    /// <summary>Pure policy evaluation. It neither moves actors nor reserves admission capacity.</summary>
    public static class RegionAccess
    {
        public static RegionAccessResult Evaluate(SimulationWorld world, PlaceState place,
            RegionDefinition definition, string agentId, double time)
        {
            if (world == null) return Result(RegionAccessFailure.UnknownWorld, "World is missing.");
            if (place == null || string.IsNullOrWhiteSpace(place.Id) || !world.Places.TryGetValue(place.Id, out var current))
                return Result(RegionAccessFailure.UnknownPlace, "Place is not registered in this world.");
            // Read the authoritative state even when the caller supplies a detached PlaceState copy.
            place = current;
            int occupancy = 0;
            foreach (var actor in world.Agents.Values) if (actor.PlaceId == place.Id) occupancy++;
            if (!place.Open) return Result(RegionAccessFailure.Closed, "Place is manually closed.", occupancy, place.Capacity);
            if (!RegionDefinitions.Finite(time) || time < 0)
                return Result(RegionAccessFailure.InvalidTime, "Evaluation time must be finite and nonnegative.", occupancy, place.Capacity);
            if (definition == null || string.IsNullOrWhiteSpace(definition.Id) || definition.Capacity < 1 ||
                RegionDefinitions.PolicyIssue(definition) != null)
                return Result(RegionAccessFailure.InvalidDefinition, "Region definition or access policy is invalid.", occupancy, place.Capacity);
            if (!string.IsNullOrEmpty(place.DefinitionId) && place.DefinitionId != definition.Id)
                return Result(RegionAccessFailure.DefinitionMismatch, "The region definition does not belong to this place.", occupancy, place.Capacity);
            if (string.IsNullOrWhiteSpace(agentId) || !world.Agents.TryGetValue(agentId, out var agent))
                return Result(RegionAccessFailure.UnknownActor, "Actor is not registered in this world.", occupancy, place.Capacity);
            if (!IsOpenAt(definition, time))
                return Result(RegionAccessFailure.OutsideOpeningHours, "Place is outside its periodic opening hours.", occupancy, place.Capacity);

            var access = definition.Access;
            if (access != null && access.RequireOwnerMembership)
            {
                if (!ActiveCollective(world, place.OwnerCollectiveId))
                    return Result(RegionAccessFailure.MissingOwnerCollective, "An active owner collective is required.", occupancy, place.Capacity);
                if (!MemberAt(world, agentId, place.OwnerCollectiveId, time))
                    return Result(RegionAccessFailure.MissingMembership, "Owner membership is required: " + place.OwnerCollectiveId, occupancy, place.Capacity);
            }
            if (access?.RequiredCollectiveIds != null)
                foreach (var collective in access.RequiredCollectiveIds)
                    if (!MemberAt(world, agentId, collective, time))
                        return Result(RegionAccessFailure.MissingMembership, "Membership is required: " + collective, occupancy, place.Capacity);
            if (access?.RequiredRoles != null)
                foreach (var required in access.RequiredRoles)
                {
                    var collective = string.IsNullOrEmpty(required.CollectiveId) ? place.OwnerCollectiveId : required.CollectiveId;
                    if (string.IsNullOrEmpty(required.CollectiveId) && !ActiveCollective(world, collective))
                        return Result(RegionAccessFailure.MissingOwnerCollective, "A scoped role requires an active owner collective.", occupancy, place.Capacity);
                    if (!MemberAt(world, agentId, collective, time))
                        return Result(RegionAccessFailure.MissingMembership, "Role membership is required: " + collective, occupancy, place.Capacity);
                    bool found = false;
                    foreach (var role in world.Society.Roles.Values)
                        if (role.Active && role.AgentId == agentId && role.RoleId == required.RoleId &&
                            role.CollectiveId == collective && time >= role.BeginsAt && time < role.EndsAt &&
                            (string.IsNullOrEmpty(role.ObjectScopeId) || role.ObjectScopeId == place.Id))
                        { found = true; break; }
                    if (!found)
                        return Result(RegionAccessFailure.MissingRole, "Role is required in this place: " + collective + "/" + required.RoleId, occupancy, place.Capacity);
                }
            // An existing occupant does not consume a second admission slot. Closing and access revocation still apply.
            if (agent.PlaceId != place.Id && occupancy >= place.Capacity)
                return Result(RegionAccessFailure.CapacityReached, "Place capacity has been reached.", occupancy, place.Capacity);
            return Result(RegionAccessFailure.None, "Allowed", occupancy, place.Capacity);
        }

        /// <summary>Opening calendar only; the PlaceState.Open override is evaluated separately.</summary>
        public static bool IsOpenAt(RegionDefinition definition, double time)
        {
            if (definition == null || !RegionDefinitions.Finite(time) || time < 0 ||
                RegionDefinitions.CalendarIssue(definition) != null) return false;
            if (definition.OpeningHours == null || definition.OpeningHours.Count == 0) return true;
            var phase = PhaseAt(definition, time);
            foreach (var interval in definition.OpeningHours)
                if (phase >= interval.StartSeconds && phase < interval.EndSeconds) return true;
            return false;
        }

        /// <summary>
        /// True when at least one open-to-closed boundary occurs in (fromTime, toTime].
        /// Work depends only on the configured intervals, never on the number of skipped periods.
        /// Adjacent intervals, including intervals joined across the period boundary, remain open.
        /// </summary>
        public static bool CrossedClosingBoundary(RegionDefinition definition, double fromTime, double toTime)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (!RegionDefinitions.Finite(fromTime) || fromTime < 0)
                throw new ArgumentOutOfRangeException(nameof(fromTime), "Time must be finite and nonnegative.");
            if (!RegionDefinitions.Finite(toTime) || toTime < fromTime)
                throw new ArgumentOutOfRangeException(nameof(toTime), "Time must be finite and not precede fromTime.");
            var issue = RegionDefinitions.CalendarIssue(definition);
            if (issue != null) throw new ArgumentException(issue, nameof(definition));
            if (fromTime == toTime || definition.OpeningHours == null || definition.OpeningHours.Count == 0) return false;

            var period = definition.OpeningPeriodSeconds;
            var elapsed = toTime - fromTime;
            var fromPhase = PhaseAt(definition, fromTime);
            foreach (var interval in definition.OpeningHours)
            {
                var closingPhase = interval.EndSeconds == period ? 0 : interval.EndSeconds;
                bool continuesOpen = false;
                foreach (var next in definition.OpeningHours)
                    if (next.StartSeconds == closingPhase) { continuesOpen = true; break; }
                if (continuesOpen) continue;
                if (elapsed >= period) return true;
                // Equal phase means this boundary already happened at fromTime. The next
                // occurrence is one complete period later and must not be counted twice.
                var distance = closingPhase > fromPhase
                    ? closingPhase - fromPhase : (period - fromPhase) + closingPhase;
                if (elapsed >= distance) return true;
            }
            return false;
        }

        private static double PhaseAt(RegionDefinition definition, double time)
        {
            double period = definition.OpeningPeriodSeconds;
            double phase = time % period;
            double offset = definition.OpeningTimeOffsetSeconds % period;
            // Avoid overflow when valid callers use a very large period or offset.
            if (offset >= 0) phase = phase >= period - offset ? phase - (period - offset) : phase + offset;
            else phase = phase < -offset ? period - (-offset - phase) : phase + offset;
            return phase;
        }

        private static RegionAccessResult Result(RegionAccessFailure failure, string reason, int occupancy = 0, int capacity = 0)
        { return new RegionAccessResult(failure, reason, occupancy, capacity); }

        private static bool ActiveCollective(SimulationWorld world, string collectiveId) =>
            !string.IsNullOrEmpty(collectiveId) && world.Society.Collectives.TryGetValue(collectiveId, out var collective) && collective.Active;

        private static bool MemberAt(SimulationWorld world, string agentId, string collectiveId, double time)
        {
            if (!ActiveCollective(world, collectiveId)) return false;
            foreach (var membership in world.Society.Memberships.Values)
                if (membership.Active && membership.AgentId == agentId && membership.CollectiveId == collectiveId &&
                    time >= membership.BeginsAt && time < membership.EndsAt) return true;
            return false;
        }
    }

    public static class RegionDefinitions
    {
        /// <summary>Returns an independent copy, with every optional container initialized for legacy assets.</summary>
        public static RegionDefinition Clone(RegionDefinition source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var result = new RegionDefinition
            {
                Id = source.Id, PackageId = source.PackageId, Tag = source.Tag, Capacity = source.Capacity,
                OpeningPeriodSeconds = source.OpeningPeriodSeconds, OpeningTimeOffsetSeconds = source.OpeningTimeOffsetSeconds
            };
            if (source.Bounds != null) result.Bounds = new RegionBounds
            {
                Enabled = source.Bounds.Enabled, CenterX = source.Bounds.CenterX, CenterY = source.Bounds.CenterY,
                CenterZ = source.Bounds.CenterZ, SizeX = source.Bounds.SizeX, SizeY = source.Bounds.SizeY, SizeZ = source.Bounds.SizeZ
            };
            if (source.Zones != null)
                foreach (var zone in source.Zones) result.Zones.Add(zone == null ? null : new RegionZoneDefinition
                { LocalId = zone.LocalId, Tag = zone.Tag, Capacity = zone.Capacity });
            if (source.Points != null)
                foreach (var point in source.Points) result.Points.Add(point == null ? null : new InteractionPointDefinition
                { LocalId = point.LocalId, ZoneLocalId = point.ZoneLocalId, Kind = point.Kind, Capacity = point.Capacity });
            if (source.EntranceLocalIds != null) result.EntranceLocalIds.AddRange(source.EntranceLocalIds);
            if (source.OpeningHours != null)
                foreach (var interval in source.OpeningHours) result.OpeningHours.Add(interval == null ? null : new RegionOpeningInterval
                { StartSeconds = interval.StartSeconds, EndSeconds = interval.EndSeconds });
            if (source.Access != null)
            {
                result.Access.RequireOwnerMembership = source.Access.RequireOwnerMembership;
                if (source.Access.RequiredCollectiveIds != null) result.Access.RequiredCollectiveIds.AddRange(source.Access.RequiredCollectiveIds);
                if (source.Access.RequiredRoles != null)
                    foreach (var role in source.Access.RequiredRoles) result.Access.RequiredRoles.Add(role == null ? null : new RegionRoleRequirement
                    { RoleId = role.RoleId, CollectiveId = role.CollectiveId });
            }
            return result;
        }

        public static List<Diagnostic> Validate(RegionDefinition region)
        {
            var result = new List<Diagnostic>();
            if (region == null)
            { result.Add(new Diagnostic("INVALID_REGION", "region", "Region definition is missing.")); return result; }
            if (string.IsNullOrWhiteSpace(region.Id) || string.IsNullOrWhiteSpace(region.Tag) || region.Capacity < 1 ||
                region.Zones == null || region.Zones.Count == 0 || region.Points == null)
                result.Add(new Diagnostic("INVALID_REGION", region.Id, "Region ID, tag, capacity and functional zones are required."));
            var zones = new HashSet<string>(StringComparer.Ordinal);
            if (region.Zones != null)
                foreach (var zone in region.Zones)
                    if (zone == null || !LocalId(zone.LocalId) || string.IsNullOrWhiteSpace(zone.Tag) || zone.Capacity < 1 || !zones.Add(zone.LocalId))
                        result.Add(new Diagnostic("INVALID_REGION_ZONE", region.Id, "Zone local ID, tag or capacity is invalid or duplicated."));
            var points = new HashSet<string>(StringComparer.Ordinal);
            if (region.Points != null)
                foreach (var point in region.Points)
                    if (point == null || !LocalId(point.LocalId) || string.IsNullOrWhiteSpace(point.Kind) || point.Capacity < 1 ||
                        !points.Add(point.LocalId) || !zones.Contains(point.ZoneLocalId ?? ""))
                        result.Add(new Diagnostic("INVALID_REGION_POINT", region.Id, "Point local ID, zone binding or capacity is invalid or duplicated."));
            var entrances = new HashSet<string>(StringComparer.Ordinal);
            if (region.EntranceLocalIds != null)
                foreach (var id in region.EntranceLocalIds)
                    if (!LocalId(id) || !entrances.Add(id))
                        result.Add(new Diagnostic("INVALID_REGION_ENTRANCE", region.Id, "Entrance local IDs must be valid and unique."));
            var issue = PolicyIssue(region);
            if (issue != null) result.Add(new Diagnostic("INVALID_REGION_POLICY", region.Id, issue));
            return result;
        }

        internal static string PolicyIssue(RegionDefinition region)
        {
            var bounds = region.Bounds;
            if (bounds != null && (!Finite(bounds.CenterX) || !Finite(bounds.CenterY) || !Finite(bounds.CenterZ) ||
                !Finite(bounds.SizeX) || !Finite(bounds.SizeY) || !Finite(bounds.SizeZ) ||
                (bounds.Enabled && (bounds.SizeX <= 0 || bounds.SizeY <= 0 || bounds.SizeZ <= 0))))
                return "Region bounds must be finite; enabled bounds require positive sizes.";
            var calendar = CalendarIssue(region);
            if (calendar != null) return calendar;
            var collectives = new HashSet<string>(StringComparer.Ordinal);
            if (region.Access?.RequiredCollectiveIds != null)
                foreach (var id in region.Access.RequiredCollectiveIds)
                    if (string.IsNullOrWhiteSpace(id) || !collectives.Add(id)) return "Required collective IDs must be nonempty and unique.";
            var roles = new HashSet<string>(StringComparer.Ordinal);
            if (region.Access?.RequiredRoles != null)
                foreach (var role in region.Access.RequiredRoles)
                    if (role == null || string.IsNullOrWhiteSpace(role.RoleId) ||
                        (role.CollectiveId != null && role.CollectiveId.Length > 0 && string.IsNullOrWhiteSpace(role.CollectiveId)) ||
                        !roles.Add((role.CollectiveId ?? "").Length + ":" + (role.CollectiveId ?? "") + role.RoleId))
                        return "Required roles must have nonempty IDs and unique collective scopes.";
            return null;
        }

        internal static string CalendarIssue(RegionDefinition region)
        {
            if (!Finite(region.OpeningPeriodSeconds) || region.OpeningPeriodSeconds < 0 || !Finite(region.OpeningTimeOffsetSeconds))
                return "Opening period and time offset must be finite; the period cannot be negative.";
            if (region.OpeningHours == null || region.OpeningHours.Count == 0) return null;
            if (region.OpeningPeriodSeconds <= 0) return "Configured opening intervals require a positive period.";
            for (int i = 0; i < region.OpeningHours.Count; i++)
            {
                var interval = region.OpeningHours[i];
                if (interval == null || !Finite(interval.StartSeconds) || !Finite(interval.EndSeconds) ||
                    interval.StartSeconds < 0 || interval.StartSeconds >= interval.EndSeconds || interval.EndSeconds > region.OpeningPeriodSeconds)
                    return "Opening intervals must satisfy 0 <= start < end <= period; split overnight intervals at the period boundary.";
                for (int j = 0; j < i; j++)
                {
                    var previous = region.OpeningHours[j];
                    if (interval.StartSeconds < previous.EndSeconds && previous.StartSeconds < interval.EndSeconds)
                        return "Opening intervals must not overlap.";
                }
            }
            return null;
        }

        internal static bool Finite(double number) => !double.IsNaN(number) && !double.IsInfinity(number);
        private static bool LocalId(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() &&
            value != "." && value != ".." && value.IndexOf('/') < 0 && value.IndexOf('\\') < 0;
    }
}
