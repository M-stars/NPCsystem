using System;
using System.Collections.Generic;
using System.IO;
using SocialSimulation.Core;
using UnityEngine;

namespace SocialSimulation.UnityRuntime
{
    public interface ISocialScenario
    {
        void Initialize(SocialWorldRunner runner, SimulationWorld world);
    }

    public interface ISocialScenarioPersistence : ISocialScenario
    {
        string CaptureScenarioState();
        void ValidateScenarioState(SocialWorldRunner runner, SimulationWorld world, string json);
        void RestoreScenarioState(SocialWorldRunner runner, SimulationWorld world, string json);
    }

    /// <summary>Synchronously clears region-bound work before the next checkpoint can be captured.</summary>
    public interface ISocialRegionLifecycle
    {
        void OnRegionUnavailable(string regionId, bool unloaded);
    }

    /// <summary>Scene composition root and the sole simulation clock driver.</summary>
    [DisallowMultipleComponent]
    public sealed class SocialWorldRunner : MonoBehaviour
    {
        [Serializable]
        private sealed class SavedNpcPose
        {
            public string AgentId;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        [Serializable]
        private sealed class SavedTravelOrder
        {
            public string AgentId;
            public string PlaceId;
            public string LocalPointId;
            public string Reason;
            public double Deadline;
        }

        [Serializable]
        private sealed class SavedArrival
        {
            public string AgentId;
            public string PlaceId;
            public string Reason;
            public bool Succeeded;
        }

        [Serializable]
        private sealed class SaveEnvelope
        {
            public int SchemaVersion = 5;
            public List<SavedPackage> Packages = new List<SavedPackage>();
            public List<string> LoadedRegionIds = new List<string>();
            public SimulationSnapshot World;
            public string ScenarioJson;
            public List<SavedScenarioState> ScenarioStates = new List<SavedScenarioState>();
            public List<SavedNpcPose> Poses = new List<SavedNpcPose>();
            public List<SavedTravelOrder> TravelOrders = new List<SavedTravelOrder>();
            public List<SavedArrival> PendingArrivals = new List<SavedArrival>();
            public List<SavedArrival> TravelOutcomes = new List<SavedArrival>();
        }

        [Serializable]
        private sealed class SavedPackage
        {
            public string Id;
            public string Version;
            public string Fingerprint;
        }

        [Serializable]
        private sealed class SavedScenarioState
        {
            public string Key;
            public string Json;
        }

        private sealed class TravelOrder
        {
            public string AgentId;
            public string PlaceId;
            public string LocalPointId;
            public string Reason;
            public int Generation;
            public double Deadline;
            public SocialNpcNavigation Navigation;
        }

        private sealed class Arrival
        {
            public string AgentId;
            public string PlaceId;
            public string Reason;
            public bool Succeeded;
        }

        private sealed class ArrivalSystem : IWorldSystem
        {
            private readonly SocialWorldRunner runner;
            public ArrivalSystem(SocialWorldRunner owner) { runner = owner; }
            private readonly Queue<Arrival> arrivals = new Queue<Arrival>();
            public int Order => 10;
            public void Enqueue(Arrival arrival) { arrivals.Enqueue(arrival); }
            public bool Contains(string agentId, string placeId, string reason)
            {
                foreach (var arrival in arrivals)
                    if (arrival.AgentId == agentId && arrival.PlaceId == placeId && arrival.Reason == reason) return true;
                return false;
            }
            public void Cancel(string agentId)
            {
                var count = arrivals.Count;
                for (var i = 0; i < count; i++)
                {
                    var arrival = arrivals.Dequeue();
                    if (arrival.AgentId != agentId) arrivals.Enqueue(arrival);
                }
            }
            public int PendingAdmissions(string regionId, string exceptAgent)
            {
                var count = 0;
                foreach (var arrival in arrivals)
                    if (arrival.Succeeded && arrival.PlaceId == regionId && arrival.AgentId != exceptAgent &&
                        runner.World.Agents.TryGetValue(arrival.AgentId, out var actor) && actor.PlaceId != regionId) count++;
                return count;
            }
            public void CancelRegion(string regionId)
            {
                foreach (var arrival in arrivals)
                    if (arrival.PlaceId == regionId)
                    {
                        arrival.Succeeded = false;
                        if (runner.npcNavigators.TryGetValue(arrival.AgentId, out var nav) && nav != null) nav.CancelMove();
                    }
            }
            public List<SavedArrival> CapturePending()
            {
                var result = new List<SavedArrival>();
                foreach (var arrival in arrivals)
                    result.Add(new SavedArrival { AgentId = arrival.AgentId, PlaceId = arrival.PlaceId,
                        Reason = arrival.Reason, Succeeded = arrival.Succeeded });
                return result;
            }
            public void RestorePending(IEnumerable<SavedArrival> saved)
            {
                if (saved == null) return;
                foreach (var arrival in saved)
                    arrivals.Enqueue(new Arrival { AgentId = arrival.AgentId, PlaceId = arrival.PlaceId,
                        Reason = arrival.Reason, Succeeded = arrival.Succeeded });
            }
            public void Tick(SimulationWorld world, double deltaSeconds)
            {
                runner.ExpireTravelOrders();
                while (arrivals.Count > 0)
                {
                    var arrival = arrivals.Dequeue();
                    if (!world.Agents.TryGetValue(arrival.AgentId, out var agent)) continue;
                    arrival.Succeeded = arrival.Succeeded && runner.CanEnterRegion(arrival.AgentId, arrival.PlaceId, out _);
                    runner.travelOutcomes[arrival.AgentId] = new SavedArrival { AgentId = arrival.AgentId,
                        PlaceId = arrival.PlaceId, Reason = arrival.Reason, Succeeded = arrival.Succeeded };
                    if (arrival.Succeeded)
                    {
                        agent.PlaceId = arrival.PlaceId;
                        world.Emit("agent.arrived", arrival.AgentId, arrival.AgentId, arrival.Reason, arrival.PlaceId);
                    }
                    else
                    {
                        if (runner.npcNavigators.TryGetValue(arrival.AgentId, out var nav) && nav != null) nav.CancelMove();
                        world.Emit("agent.travel_failed", arrival.AgentId, arrival.AgentId, arrival.Reason, arrival.PlaceId);
                    }
                }
            }
        }

        private sealed class RegionClockSystem : IWorldSystem
        {
            private readonly SocialWorldRunner runner;
            public RegionClockSystem(SocialWorldRunner owner) { runner = owner; }
            public int Order => 5;
            public void Tick(SimulationWorld world, double deltaSeconds)
            {
                foreach (var pair in runner.regionViews)
                {
                    if (pair.Value == null || !world.Places.TryGetValue(pair.Key, out var place) ||
                        !runner.Content.Regions.TryGetValue(place.DefinitionId, out var definition)) continue;
                    var now = RegionAccess.IsOpenAt(definition, world.Time);
                    var before = RegionAccess.IsOpenAt(definition, Math.Max(0, world.Time - deltaSeconds));
                    var crossedClose = RegionAccess.CrossedClosingBoundary(definition, Math.Max(0, world.Time - deltaSeconds), world.Time);
                    if (crossedClose)
                    {
                        runner.ClearRegionOccupations(place.Id);
                        world.Emit("region.hours_closed", "world.regions", place.Id, null, place.Id);
                    }
                    if (now && (!before || crossedClose))
                    {
                        runner.RefreshRegionAvailability(place.Id);
                        world.Emit("region.hours_opened", "world.regions", place.Id, null, place.Id);
                    }
                }
            }
        }

        [SerializeField] private string worldId = "combined.demo";
        [SerializeField] private int worldSeed = 1234;
        [SerializeField, Min(0.1f)] private float simulationSecondsPerRealSecond = 30f;
        [SerializeField, Min(0.1f)] private float tickSeconds = 5f;
        [SerializeField] private bool startPaused;
        [SerializeField] private bool showOverlay = true;

        private readonly Dictionary<string, SocialNpcMarker> npcViews = new Dictionary<string, SocialNpcMarker>(StringComparer.Ordinal);
        private readonly Dictionary<string, SocialNpcNavigation> npcNavigators = new Dictionary<string, SocialNpcNavigation>(StringComparer.Ordinal);
        private readonly Dictionary<string, SocialWorldMarker> regionViews = new Dictionary<string, SocialWorldMarker>(StringComparer.Ordinal);
        private readonly Dictionary<string, SocialInteractionPoint> pointViews = new Dictionary<string, SocialInteractionPoint>(StringComparer.Ordinal);
        private readonly Dictionary<string, TravelOrder> travelOrders = new Dictionary<string, TravelOrder>(StringComparer.Ordinal);
        private readonly Dictionary<string, SavedArrival> travelOutcomes = new Dictionary<string, SavedArrival>(StringComparer.Ordinal);
        private readonly List<string> finishedTravelOrders = new List<string>();
        private readonly List<string> recentEvents = new List<string>();
        private ArrivalSystem arrivalSystem;
        private readonly Dictionary<string, ISocialScenarioPersistence> scenarioPersistences =
            new Dictionary<string, ISocialScenarioPersistence>(StringComparer.Ordinal);
        private bool faulted;
        private string faultMessage;
        private GUIStyle overlayStyle;
        private bool recoveringLoad;
        private static readonly int MovingHash = Animator.StringToHash("Moving");
        private static readonly int RunningHash = Animator.StringToHash("Running");

        public SimulationWorld World { get; private set; }
        public ContentRegistry Content { get; private set; }
        public IReadOnlyDictionary<string, SocialNpcMarker> NpcViews => npcViews;
        public IReadOnlyDictionary<string, SocialWorldMarker> RegionViews => regionViews;
        public IReadOnlyDictionary<string, SocialInteractionPoint> PointViews => pointViews;
        public bool IsFaulted => faulted;
        public string FaultMessage => faultMessage;
        public string SavePath => Path.Combine(Application.persistentDataPath,
            "SocialSimulation." + SafeFileName(worldId) + ".save.json");

        public void ConfigureWorld(string stableWorldId, uint seed = 1234)
        {
            if (World != null) throw new InvalidOperationException("World configuration is locked after initialization.");
            worldId = string.IsNullOrWhiteSpace(stableWorldId) ? throw new ArgumentException("World ID is required.") : stableWorldId;
            worldSeed = seed > int.MaxValue ? int.MaxValue : (int)seed;
        }

        private static string SafeFileName(string id)
        {
            var value = string.IsNullOrWhiteSpace(id) ? "world" : id;
            foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
            return value;
        }

        private void Awake()
        {
            try { InitializeWorld(); }
            catch (Exception exception)
            {
                faulted = true;
                faultMessage = "世界初始化失败：" + exception.Message;
                if (World != null) SetPaused(true);
                Debug.LogException(exception, this);
                enabled = false;
            }
        }

        private void InitializeWorld()
        {
            ISocialContentProvider contentProvider = null;
            foreach (var behaviour in GetComponents<MonoBehaviour>())
                if (behaviour is ISocialContentProvider provider)
                {
                    if (contentProvider != null)
                        throw new InvalidOperationException("A world can have only one content provider.");
                    contentProvider = provider;
                }
            if (contentProvider == null)
                throw new InvalidOperationException("A world requires one registered social content provider.");
            Content = contentProvider.BuildRegistry();
            World = new SimulationWorld(worldId, (uint)Mathf.Max(1, worldSeed))
            {
                TickSeconds = tickSeconds,
                TimeScale = simulationSecondsPerRealSecond,
                Paused = startPaused
            };
            arrivalSystem = new ArrivalSystem(this);
            faulted = false;
            faultMessage = null;
            World.AddSystem(arrivalSystem);
            World.AddSystem(new RegionClockSystem(this));

            foreach (var region in GetComponentsInChildren<SocialWorldMarker>(true))
            {
                if (string.IsNullOrWhiteSpace(region.RegionInstanceId) || regionViews.ContainsKey(region.RegionInstanceId))
                    throw new InvalidOperationException("Invalid or duplicated region instance ID: " + region.RegionInstanceId);
                if (!Content.Regions.TryGetValue(region.RegionDefinitionId ?? string.Empty, out var definition) ||
                    string.IsNullOrWhiteSpace(region.PackageId) ||
                    definition.PackageId != region.PackageId)
                    throw new InvalidOperationException("Region instance is not bound to a registered package: " +
                        region.RegionInstanceId);
                ValidateRegionInstance(region, definition);
                regionViews.Add(region.RegionInstanceId, region);
                World.Places.Add(region.RegionInstanceId, new PlaceState
                {
                    Id = region.RegionInstanceId,
                    DefinitionId = region.RegionDefinitionId,
                    Name = region.DisplayName,
                    Tag = definition.Tag,
                    RegionInstanceId = region.RegionInstanceId,
                    Capacity = definition.Capacity,
                    OwnerCollectiveId = region.OwnerCollectiveId
                });
            }

            foreach (var point in GetComponentsInChildren<SocialInteractionPoint>(true))
            {
                if (string.IsNullOrWhiteSpace(point.StableId) || pointViews.ContainsKey(point.StableId))
                    throw new InvalidOperationException("Invalid or duplicated interaction point ID: " + point.StableId);
                var region = point.GetComponentInParent<SocialWorldMarker>();
                if (region == null) throw new InvalidOperationException("Interaction point lacks a region: " + point.name);
                pointViews.Add(point.StableId, point);
                World.Resources.Slots.Add(point.StableId, new ResourceSlot
                {
                    Id = point.StableId,
                    PlaceId = region.RegionInstanceId,
                    Kind = point.ActionId,
                    Capacity = point.Capacity
                });
            }

            var requiredCollectives = new HashSet<string>(StringComparer.Ordinal);
            foreach (var npc in GetComponentsInChildren<SocialNpcMarker>(true))
            {
                if (string.IsNullOrWhiteSpace(npc.NpcInstanceId) || npcViews.ContainsKey(npc.NpcInstanceId))
                    throw new InvalidOperationException("Invalid or duplicated NPC instance ID: " + npc.NpcInstanceId);
                npcViews.Add(npc.NpcInstanceId, npc);
                var navigator = npc.GetComponent<SocialNpcNavigation>();
                if (navigator == null) throw new InvalidOperationException("NPC lacks navigation adapter: " + npc.NpcInstanceId);
                npcNavigators.Add(npc.NpcInstanceId, navigator);
                var placeId = ClosestPlace(npc.transform.position);
                if (string.IsNullOrWhiteSpace(npc.PackageId) ||
                    !Content.Packages.TryGetValue(npc.PackageId, out var npcPackage) ||
                    npcPackage.Kind != "NPC")
                    throw new InvalidOperationException("NPC instance is not bound to a registered NPC package: " +
                        npc.NpcInstanceId);
                if (npcPackage.Body != null)
                    navigator.ConfigureSpeeds(npcPackage.Body.WalkMetersPerSecond,
                        npcPackage.Body.RunMetersPerSecond);
                var agent = new AgentState
                {
                    Id = npc.NpcInstanceId,
                    DisplayName = npc.DisplayName,
                    TemplateId = npcPackage.Id,
                    PlaceId = placeId,
                    Personality = PersonalityGenerator.Generate(World.Seed, npc.NpcInstanceId,
                        npc.PersonalityArchetype)
                };
                foreach (var capability in npcPackage.Capabilities)
                    agent.CapabilityIds.Add(capability.Id);
                World.AddAgent(agent);
                foreach (var membershipId in npc.MembershipIds)
                    if (!string.IsNullOrWhiteSpace(membershipId)) requiredCollectives.Add(membershipId);
            }

            var collectiveDefinitions = new Dictionary<string, SocialCollectiveMarker>(StringComparer.Ordinal);
            foreach (var definition in GetComponentsInChildren<SocialCollectiveMarker>(true))
            {
                if (string.IsNullOrWhiteSpace(definition.CollectiveId) ||
                    !collectiveDefinitions.TryAdd(definition.CollectiveId, definition))
                    throw new InvalidOperationException("Invalid or duplicate social collective: " +
                        definition.CollectiveId);
            }
            foreach (var id in requiredCollectives)
                if (!collectiveDefinitions.ContainsKey(id))
                    throw new InvalidOperationException("NPC membership has no collective definition: " + id);
            foreach (var pair in collectiveDefinitions)
            {
                var id = pair.Key;
                var definition = pair.Value;
                World.Society.Collectives.Add(id, new CollectiveState
                {
                    Id = id,
                    DefinitionId = id,
                    Name = string.IsNullOrWhiteSpace(definition.DisplayName) ? id : definition.DisplayName,
                    Formal = definition.Formal
                });
                if (definition.Organization)
                    World.Society.Organizations.Add(id, new OrganizationState { Id = id, CollectiveId = id });
            }
            foreach (var place in World.Places.Values)
                if (!string.IsNullOrEmpty(place.OwnerCollectiveId) &&
                    !World.Society.Collectives.ContainsKey(place.OwnerCollectiveId))
                    throw new InvalidOperationException("Region owner has no collective definition: " + place.Id);

            foreach (var npc in npcViews.Values)
            {
                var primary = npc.PrimaryCollectiveId;
                if (!string.IsNullOrEmpty(primary) &&
                    Array.IndexOf(npc.MembershipIds, primary) < 0)
                    throw new InvalidOperationException("NPC primary collective is not a membership: " +
                        npc.NpcInstanceId);
                foreach (var membershipId in npc.MembershipIds)
                {
                    if (string.IsNullOrWhiteSpace(membershipId)) continue;
                    World.Society.Join(npc.NpcInstanceId, membershipId);
                    if (membershipId == primary)
                        World.Society.AssignRole(npc.NpcInstanceId, membershipId, npc.PrimaryRoleId);
                }
                foreach (var binding in npc.MembershipRoles)
                {
                    if (binding == null ||
                        Array.IndexOf(npc.MembershipIds, binding.CollectiveId) < 0 ||
                        string.IsNullOrWhiteSpace(binding.RoleId))
                        throw new InvalidOperationException("NPC has an invalid membership role: " +
                            npc.NpcInstanceId);
                    World.Society.AssignRole(npc.NpcInstanceId, binding.CollectiveId,
                        binding.RoleId);
                }
            }

            foreach (var behaviour in GetComponents<MonoBehaviour>())
                if (behaviour is ISocialScenario scenario)
                {
                    scenario.Initialize(this, World);
                    if (behaviour is ISocialScenarioPersistence persistence)
                    {
                        var key = behaviour.GetType().FullName;
                        if (!scenarioPersistences.TryAdd(key, persistence))
                            throw new InvalidOperationException("Duplicate scenario persistence component: " + key);
                    }
                }
            World.EventPublished += RememberEvent;
            ValidateEntrances();
            foreach (var place in World.Places.Values)
                if (Content.Regions.TryGetValue(place.DefinitionId, out var definition) &&
                    !RegionAccess.IsOpenAt(definition, World.Time))
                    foreach (var slot in World.Resources.Slots.Values)
                        if (slot.PlaceId == place.Id) slot.RegionAvailable = false;
            SetNavigationPaused(World.Paused);
        }

        private static void ValidateRegionInstance(SocialWorldMarker marker, RegionDefinition definition)
        {
            if (definition.Bounds != null && definition.Bounds.Enabled)
            {
                var bounds = definition.Bounds;
                if (!marker.HasBoundary || marker.LocalBounds != new Bounds(
                        new Vector3(bounds.CenterX, bounds.CenterY, bounds.CenterZ),
                        new Vector3(bounds.SizeX, bounds.SizeY, bounds.SizeZ)))
                    throw new InvalidOperationException("Region boundary differs from registered package: " + marker.RegionInstanceId);
            }
            else if (marker.HasBoundary)
                throw new InvalidOperationException("Region has an unregistered boundary: " + marker.RegionInstanceId);
            var entrances = new HashSet<string>(StringComparer.Ordinal);
            var entranceRoot = marker.transform.Find("Entrances");
            foreach (var entrance in marker.GetComponentsInChildren<SocialRegionEntrance>(true))
                if (entrance.transform.parent != entranceRoot || !entrances.Add(entrance.LocalId ?? "") ||
                    !marker.Contains(entrance.transform.position))
                    throw new InvalidOperationException("Invalid region entrance hierarchy or bounds: " + marker.RegionInstanceId);
            if (!entrances.SetEquals(definition.EntranceLocalIds ?? new List<string>()))
                throw new InvalidOperationException("Region entrance IDs differ from registered package: " + marker.RegionInstanceId);
            var zonesRoot = marker.transform.Find("Zones");
            if (zonesRoot == null || zonesRoot.parent != marker.transform)
                throw new InvalidOperationException("Registered region lacks a direct Zones container: " +
                    marker.RegionInstanceId);
            var expectedZones = new Dictionary<string, RegionZoneDefinition>(StringComparer.Ordinal);
            foreach (var zone in definition.Zones) expectedZones.Add(zone.LocalId, zone);
            var expectedPoints = new Dictionary<string, InteractionPointDefinition>(StringComparer.Ordinal);
            foreach (var point in definition.Points) expectedPoints.Add(point.LocalId, point);
            var observedZones = new HashSet<string>(StringComparer.Ordinal);
            var observedPoints = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transform zoneObject in zonesRoot)
            {
                var zone = zoneObject.GetComponent<SocialRegionZone>();
                if (zone == null || !observedZones.Add(zone.LocalId ?? string.Empty) ||
                    !expectedZones.TryGetValue(zone.LocalId ?? string.Empty, out var zoneDefinition) ||
                    zone.Tag != zoneDefinition.Tag || zone.Capacity != zoneDefinition.Capacity)
                    throw new InvalidOperationException("Region zone differs from registered package: " +
                        marker.RegionInstanceId + "/" + zoneObject.name);
                var pointsRoot = zoneObject.Find("InteractionPoints");
                if (pointsRoot == null || pointsRoot.parent != zoneObject)
                    throw new InvalidOperationException("Region zone lacks InteractionPoints: " +
                        marker.RegionInstanceId + "/" + zone.LocalId);
                foreach (Transform pointObject in pointsRoot)
                {
                    var point = pointObject.GetComponent<SocialInteractionPoint>();
                    if (point == null || !observedPoints.Add(point.LocalId ?? string.Empty) ||
                        !expectedPoints.TryGetValue(point.LocalId ?? string.Empty, out var pointDefinition) ||
                        pointDefinition.ZoneLocalId != zone.LocalId ||
                        pointDefinition.Kind != point.ActionId ||
                        pointDefinition.Capacity != point.Capacity || !marker.Contains(point.AnchorPosition))
                        throw new InvalidOperationException("Region point differs from registered package: " +
                            marker.RegionInstanceId + "/" + pointObject.name);
                }
            }
            if (observedZones.Count != expectedZones.Count || observedPoints.Count != expectedPoints.Count ||
                marker.GetComponentsInChildren<SocialRegionZone>(true).Length != observedZones.Count ||
                marker.GetComponentsInChildren<SocialInteractionPoint>(true).Length != observedPoints.Count)
                throw new InvalidOperationException("Region hierarchy differs from registered package: " +
                    marker.RegionInstanceId);
        }

        private string ClosestPlace(Vector3 position)
        {
            string bestId = null;
            var bestDistance = float.PositiveInfinity;
            foreach (var pair in regionViews)
            {
                if (pair.Value == null || !pair.Value.Contains(position)) continue;
                var distance = (pair.Value.transform.position - position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; bestId = pair.Key; }
            }
            return bestId;
        }

        public bool RequestTravel(string agentId, string placeId, string localPointId = null, string reason = null,
            double timeoutSeconds = 1800)
        {
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0) return false;
            if (World != null && double.IsInfinity(World.Time + timeoutSeconds)) return false;
            if (World == null || string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(placeId) ||
                !World.Agents.ContainsKey(agentId) || !World.Places.TryGetValue(placeId, out var place) || !place.Open ||
                !npcViews.TryGetValue(agentId, out var npc) || npc == null ||
                !regionViews.TryGetValue(placeId, out var region) || region == null ||
                !npcNavigators.TryGetValue(agentId, out var navigator) || navigator == null) return false;
            if (!CanEnterRegion(agentId, placeId, out _)) return false;
            if (World.Agents[agentId].PlaceId != placeId)
            {
                var occupancy = 0;
                foreach (var actor in World.Agents.Values) if (actor.PlaceId == placeId) occupancy++;
                foreach (var order in travelOrders.Values)
                    if (order.PlaceId == placeId && order.AgentId != agentId &&
                        World.Agents.TryGetValue(order.AgentId, out var traveller) && traveller.PlaceId != placeId) occupancy++;
                occupancy += arrivalSystem.PendingAdmissions(placeId, agentId);
                if (occupancy >= place.Capacity) return false;
            }
            Vector3 target = region.transform.position;
            var run = false;
            if (!string.IsNullOrEmpty(localPointId))
            {
                if (!pointViews.TryGetValue(placeId + "/" + localPointId, out var point) || point == null) return false;
                target = point.AnchorPosition;
                run = point.ActionId == "locomotion.run";
            }
            CancelTravel(agentId);
            var result = navigator.RequestMove(target, run, out var generation);
            if (result != SocialNavigationStatus.InProgress) return false;
            travelOrders[agentId] = new TravelOrder
            { AgentId = agentId, PlaceId = placeId, LocalPointId = localPointId,
                Reason = reason, Generation = generation, Navigation = navigator, Deadline = World.Time + timeoutSeconds };
            travelOutcomes.Remove(agentId);
            return true;
        }

        /// <summary>Returns the authoritative Tick-committed outcome for an action's unique travel reason.</summary>
        public SocialNavigationStatus GetTravelStatus(string agentId, string placeId, string reason)
        {
            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(reason)) return SocialNavigationStatus.Idle;
            if (travelOrders.TryGetValue(agentId, out var order) && order.PlaceId == placeId && order.Reason == reason)
                return SocialNavigationStatus.InProgress;
            if (arrivalSystem != null && arrivalSystem.Contains(agentId, placeId, reason)) return SocialNavigationStatus.InProgress;
            if (travelOutcomes.TryGetValue(agentId, out var outcome) && outcome.PlaceId == placeId && outcome.Reason == reason)
                return outcome.Succeeded ? SocialNavigationStatus.Succeeded : SocialNavigationStatus.Failed;
            return SocialNavigationStatus.Idle;
        }

        public string SaveToJson()
        {
            if (World == null) throw new InvalidOperationException("World is not initialized.");
            var envelope = new SaveEnvelope
            {
                World = WorldPersistence.Capture(World),
                PendingArrivals = arrivalSystem.CapturePending()
            };
            foreach (var package in Content.Packages.Values)
                envelope.Packages.Add(new SavedPackage { Id = package.Id, Version = package.Version,
                    Fingerprint = Content.GetPackageFingerprint(package.Id) });
            envelope.Packages.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            envelope.LoadedRegionIds.AddRange(regionViews.Keys);
            envelope.LoadedRegionIds.Sort(StringComparer.Ordinal);
            envelope.TravelOutcomes.AddRange(travelOutcomes.Values);
            envelope.TravelOutcomes.Sort((a, b) => StringComparer.Ordinal.Compare(a.AgentId, b.AgentId));
            foreach (var pair in scenarioPersistences)
                envelope.ScenarioStates.Add(new SavedScenarioState
                { Key = pair.Key, Json = pair.Value.CaptureScenarioState() });
            envelope.ScenarioStates.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
            foreach (var npc in npcViews.Values)
                envelope.Poses.Add(new SavedNpcPose { AgentId = npc.NpcInstanceId,
                    Position = npc.transform.position, Rotation = npc.transform.rotation });
            foreach (var order in travelOrders.Values)
                envelope.TravelOrders.Add(new SavedTravelOrder { AgentId = order.AgentId,
                    PlaceId = order.PlaceId, LocalPointId = order.LocalPointId, Reason = order.Reason, Deadline = order.Deadline });
            envelope.Poses.Sort((a, b) => StringComparer.Ordinal.Compare(a.AgentId, b.AgentId));
            envelope.TravelOrders.Sort((a, b) => StringComparer.Ordinal.Compare(a.AgentId, b.AgentId));
            return JsonUtility.ToJson(envelope);
        }

        public void LoadFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Save JSON is empty.");
            var envelope = JsonUtility.FromJson<SaveEnvelope>(json);
            if (envelope == null || envelope.SchemaVersion != 5 ||
                envelope.World == null ||
                envelope.Poses == null || envelope.Poses.Count != npcViews.Count ||
                envelope.ScenarioStates == null || envelope.Packages == null ||
                envelope.TravelOrders == null || envelope.PendingArrivals == null || envelope.LoadedRegionIds == null || envelope.TravelOutcomes == null)
                throw new InvalidOperationException("Save schema is incompatible. Version 5 requires knowledge and agreement history; older saves require explicit migration.");
            var loadedRegions = new HashSet<string>(envelope.LoadedRegionIds, StringComparer.Ordinal);
            if (loadedRegions.Count != envelope.LoadedRegionIds.Count || !loadedRegions.SetEquals(regionViews.Keys))
                throw new InvalidOperationException("Save loaded region set differs from the scene. Reload matching regions before restoring.");
            var packageIds = new HashSet<string>(StringComparer.Ordinal);
            if (envelope.Packages.Count != Content.Packages.Count)
                throw new InvalidOperationException("Save content packages do not match this world.");
            foreach (var package in envelope.Packages)
                if (package == null || string.IsNullOrEmpty(package.Id) || !packageIds.Add(package.Id) ||
                    !Content.Packages.TryGetValue(package.Id, out var registered) || registered.Version != package.Version ||
                    Content.GetPackageFingerprint(package.Id) != package.Fingerprint)
                    throw new InvalidOperationException("Save content version or fingerprint differs: " + package?.Id);
            var restored = WorldPersistence.Restore(envelope.World);
            if (restored.WorldId != World.WorldId || restored.Agents.Count != npcViews.Count)
                throw new InvalidOperationException("Save world does not match this scene.");
            foreach (var pair in regionViews)
                if (pair.Value == null || !restored.Places.TryGetValue(pair.Key, out var place) ||
                    place.DefinitionId != pair.Value.RegionDefinitionId || place.RegionInstanceId != pair.Key)
                    throw new InvalidOperationException("Save region bindings do not match this scene: " + pair.Key);
            var poseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pose in envelope.Poses)
                if (pose == null || string.IsNullOrEmpty(pose.AgentId) || !poseIds.Add(pose.AgentId) ||
                    !npcViews.TryGetValue(pose.AgentId, out var npc) || npc == null ||
                    !restored.Agents.TryGetValue(pose.AgentId, out var actor) || actor.TemplateId != npc.PackageId ||
                    !IsFinite(pose.Position) || !IsValidRotation(pose.Rotation) ||
                    npcNavigators[pose.AgentId] == null || !npcNavigators[pose.AgentId].isActiveAndEnabled ||
                    !npcNavigators[pose.AgentId].CanRestorePose(pose.Position))
                    throw new InvalidOperationException("Save has an unknown NPC pose.");
            var travelIds = new HashSet<string>(StringComparer.Ordinal);
            if (envelope.TravelOrders != null)
                foreach (var order in envelope.TravelOrders)
                    if (order == null || string.IsNullOrEmpty(order.AgentId) || string.IsNullOrEmpty(order.PlaceId) ||
                        !travelIds.Add(order.AgentId) ||
                        double.IsNaN(order.Deadline) || double.IsInfinity(order.Deadline) || order.Deadline < 0 ||
                        !npcViews.ContainsKey(order.AgentId) || !regionViews.ContainsKey(order.PlaceId) ||
                        (!string.IsNullOrEmpty(order.LocalPointId) &&
                         !pointViews.ContainsKey(order.PlaceId + "/" + order.LocalPointId)))
                        throw new InvalidOperationException("Save has an invalid travel order.");
            var arrivalIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arrival in envelope.PendingArrivals)
                if (arrival == null || string.IsNullOrEmpty(arrival.AgentId) || string.IsNullOrEmpty(arrival.PlaceId) ||
                    !restored.Agents.ContainsKey(arrival.AgentId) || !restored.Places.ContainsKey(arrival.PlaceId) ||
                    (arrival.Succeeded && !regionViews.ContainsKey(arrival.PlaceId)) ||
                    !arrivalIds.Add(arrival.AgentId) || travelIds.Contains(arrival.AgentId))
                    throw new InvalidOperationException("Save has an invalid pending arrival.");
            var savedScenarioStates = new Dictionary<string, string>(StringComparer.Ordinal);
            var outcomeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var outcome in envelope.TravelOutcomes)
                if (outcome == null || string.IsNullOrEmpty(outcome.AgentId) || string.IsNullOrEmpty(outcome.PlaceId) ||
                    !outcomeIds.Add(outcome.AgentId) || !restored.Agents.ContainsKey(outcome.AgentId) ||
                    !restored.Places.ContainsKey(outcome.PlaceId))
                    throw new InvalidOperationException("Save has an invalid committed travel outcome.");
            if (envelope.ScenarioStates != null)
                foreach (var saved in envelope.ScenarioStates)
                    if (saved == null || string.IsNullOrEmpty(saved.Key) ||
                        string.IsNullOrEmpty(saved.Json) || !savedScenarioStates.TryAdd(saved.Key, saved.Json))
                        throw new InvalidOperationException("Save contains invalid scenario state.");
            if (savedScenarioStates.Count != scenarioPersistences.Count)
                throw new InvalidOperationException("Save scenario components do not match this scene.");
            foreach (var pair in scenarioPersistences)
            {
                if (!savedScenarioStates.TryGetValue(pair.Key, out var state))
                    throw new InvalidOperationException("Save is missing scenario component: " + pair.Key);
                pair.Value.ValidateScenarioState(this, restored, state);
            }

            var previousJson = recoveringLoad ? null : SaveToJson();
            try
            {
            SetPaused(true);
            foreach (var order in travelOrders.Values) order.Navigation.CancelMove();
            travelOrders.Clear();
            travelOutcomes.Clear();
            foreach (var outcome in envelope.TravelOutcomes) travelOutcomes.Add(outcome.AgentId, outcome);
            World.EventPublished -= RememberEvent;
            foreach (var pose in envelope.Poses)
                if (!npcNavigators[pose.AgentId].RestorePose(pose.Position, pose.Rotation))
                    throw new InvalidOperationException("Could not restore NPC on NavMesh: " + pose.AgentId);
            World = restored;
            faulted = false;
            faultMessage = null;
            arrivalSystem = new ArrivalSystem(this);
            arrivalSystem.RestorePending(envelope.PendingArrivals);
            World.AddSystem(arrivalSystem);
            World.AddSystem(new RegionClockSystem(this));
            SetNavigationPaused(World.Paused);
            if (envelope.TravelOrders != null)
                foreach (var order in envelope.TravelOrders)
                    if (order.Deadline <= World.Time || !RequestTravel(order.AgentId, order.PlaceId, order.LocalPointId, order.Reason,
                            order.Deadline - World.Time))
                        arrivalSystem.Enqueue(new Arrival { AgentId = order.AgentId, PlaceId = order.PlaceId,
                            Reason = order.Reason, Succeeded = false });
            foreach (var pair in scenarioPersistences)
                pair.Value.RestoreScenarioState(this, World, savedScenarioStates[pair.Key]);
            World.EventPublished += RememberEvent;
            recentEvents.Clear();
            var first = Math.Max(0, World.Trace.Count - 8);
            for (var i = first; i < World.Trace.Count; i++) RememberEvent(World.Trace[i]);
            }
            catch (Exception failure)
            {
                if (recoveringLoad) throw;
                recoveringLoad = true;
                try { LoadFromJson(previousJson); }
                catch (Exception rollbackFailure)
                {
                    RecordFault(new InvalidOperationException("Save restore and rollback both failed: " +
                        failure.Message + "; rollback: " + rollbackFailure.Message, rollbackFailure));
                }
                finally { recoveringLoad = false; }
                throw new InvalidOperationException("Could not apply save; recovery of the previous state was attempted.", failure);
            }
        }

        public void SaveGame()
        {
            var json = SaveToJson();
            var temporary = SavePath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(SavePath)) File.Replace(temporary, SavePath, SavePath + ".bak");
                else File.Move(temporary, SavePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Debug.Log("SocialSimulation: 已保存 " + SavePath);
        }

        public void LoadGame()
        {
            LoadFromJson(File.ReadAllText(SavePath));
            Debug.Log("SocialSimulation: 已读取 " + SavePath);
        }

        public void CancelTravel(string agentId)
        {
            if (arrivalSystem != null)
                foreach (var pending in arrivalSystem.CapturePending())
                    if (pending.AgentId == agentId)
                    {
                        pending.Succeeded = false;
                        travelOutcomes[agentId] = pending;
                        break;
                    }
            arrivalSystem?.Cancel(agentId);
            if (!travelOrders.TryGetValue(agentId, out var order)) return;
            travelOutcomes[agentId] = new SavedArrival { AgentId = agentId, PlaceId = order.PlaceId,
                Reason = order.Reason, Succeeded = false };
            if (order.Navigation != null) order.Navigation.CancelMove();
            travelOrders.Remove(agentId);
        }

        private void Update()
        {
            if (World == null || faulted) return;
            finishedTravelOrders.Clear();
            foreach (var pair in npcViews)
                if (pair.Value == null || !World.Agents.ContainsKey(pair.Key)) finishedTravelOrders.Add(pair.Key);
            foreach (var id in finishedTravelOrders) DespawnAgent(id);
            finishedTravelOrders.Clear();
            foreach (var pair in regionViews)
                if (pair.Value == null) finishedTravelOrders.Add(pair.Key);
            foreach (var id in finishedTravelOrders) UnregisterRegion(id);
            try { World.Advance(Time.deltaTime); }
            catch (Exception exception) { RecordFault(exception); return; }
            foreach (var npc in npcViews.Values)
            {
                if (npc.Animator == null) continue;
                var navigation = npcNavigators[npc.NpcInstanceId];
                SocialAnimatorParameters.SetBool(npc.Animator, MovingHash, navigation != null && navigation.IsMoving);
                SocialAnimatorParameters.SetBool(npc.Animator, RunningHash, navigation != null && navigation.IsRunning);
            }
            if (World.Paused) return;
            finishedTravelOrders.Clear();
            foreach (var pair in travelOrders)
            {
                var order = pair.Value;
                var status = order.Navigation == null ? SocialNavigationStatus.Failed : order.Navigation.Poll(order.Generation);
                if (status == SocialNavigationStatus.InProgress) continue;
                arrivalSystem.Enqueue(new Arrival { AgentId = order.AgentId, PlaceId = order.PlaceId,
                    Reason = order.Reason, Succeeded = status == SocialNavigationStatus.Succeeded });
                finishedTravelOrders.Add(pair.Key);
            }
            foreach (var id in finishedTravelOrders) travelOrders.Remove(id);
        }

        private void ExpireTravelOrders()
        {
            finishedTravelOrders.Clear();
            foreach (var pair in travelOrders)
            {
                var order = pair.Value;
                if (World.Time < order.Deadline) continue;
                if (order.Navigation != null) order.Navigation.CancelMove();
                arrivalSystem.Enqueue(new Arrival { AgentId = order.AgentId, PlaceId = order.PlaceId,
                    Reason = order.Reason, Succeeded = false });
                finishedTravelOrders.Add(pair.Key);
            }
            foreach (var id in finishedTravelOrders) travelOrders.Remove(id);
        }

        public void DespawnAgent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return;
            CancelTravel(agentId);
            World?.RemoveAgent(agentId);
            travelOutcomes.Remove(agentId);
            if (npcViews.TryGetValue(agentId, out var view) && view != null) Destroy(view.gameObject);
            npcViews.Remove(agentId);
            npcNavigators.Remove(agentId);
        }

        public bool CanEnterRegion(string agentId, string regionId, out string reason)
        {
            reason = "UnknownRegion";
            if (World == null || string.IsNullOrEmpty(regionId) ||
                !World.Places.TryGetValue(regionId, out var place) ||
                !regionViews.TryGetValue(regionId, out var marker) || marker == null ||
                !Content.Regions.TryGetValue(place.DefinitionId, out var definition)) return false;
            var access = RegionAccess.Evaluate(World, place, definition, agentId, World.Time);
            reason = access.Reason;
            return access.Allowed;
        }

        public void SetRegionOpen(string regionId, bool open)
        {
            if (World == null || !World.Places.TryGetValue(regionId, out var place)) return;
            if (place.Open == open) return;
            if (open && (!regionViews.TryGetValue(regionId, out var view) || view == null))
                throw new InvalidOperationException("Cannot reopen an unloaded region: " + regionId);
            place.Open = open;
            if (!open) ClearRegionOccupations(regionId);
            else RefreshRegionAvailability(regionId);
            World.Emit(open ? "region.opened" : "region.closed", "world.regions", regionId, null, regionId);
        }

        private void ClearRegionOccupations(string regionId, bool unloaded = false)
        {
            var cancelled = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pending in arrivalSystem.CapturePending())
                if (pending.PlaceId == regionId) cancelled[pending.AgentId] = pending.Reason;
            foreach (var order in travelOrders.Values)
                if (order.PlaceId == regionId) cancelled[order.AgentId] = order.Reason;
            foreach (var behaviour in GetComponents<MonoBehaviour>())
                if (behaviour is ISocialRegionLifecycle lifecycle) lifecycle.OnRegionUnavailable(regionId, unloaded);
            arrivalSystem.CancelRegion(regionId);
            foreach (var pair in cancelled)
            {
                CancelTravel(pair.Key);
                arrivalSystem.Enqueue(new Arrival { AgentId = pair.Key, PlaceId = regionId, Reason = pair.Value, Succeeded = false });
            }
            foreach (var slot in World.Resources.Slots.Values)
                if (slot.PlaceId == regionId) World.Resources.SetRegionAvailability(slot.Id, false);
            foreach (var session in World.Activities.Sessions.Values)
                if (session.PlaceId == regionId) World.Activities.Cancel(session.Id, "region.closed");
            foreach (var duty in World.Schedules.Assignments.Values)
                if (duty.PlaceId == regionId && (duty.State == "Accepted" || duty.State == "Due"))
                    World.Schedules.Cancel(duty.Id, "region.closed");
        }

        private void RefreshRegionAvailability(string regionId)
        {
            var place = World.Places[regionId];
            var open = place.Open && Content.Regions.TryGetValue(place.DefinitionId, out var definition) &&
                RegionAccess.IsOpenAt(definition, World.Time);
            foreach (var slot in World.Resources.Slots.Values)
                if (slot.PlaceId == regionId)
                    World.Resources.SetRegionAvailability(slot.Id, open &&
                        pointViews.TryGetValue(slot.Id, out var point) && point != null);
        }

        public void UnregisterRegion(string regionId)
        {
            if (World == null || string.IsNullOrEmpty(regionId) || !regionViews.ContainsKey(regionId)) return;
            World.Places[regionId].Open = false;
            ClearRegionOccupations(regionId, true);
            foreach (var duty in World.Schedules.Assignments.Values)
                if (duty.PlaceId == regionId) World.Schedules.Cancel(duty.Id, "region.unloaded");
            var points = new List<string>();
            foreach (var pair in pointViews)
                if (World.Resources.Slots.TryGetValue(pair.Key, out var slot) && slot.PlaceId == regionId) points.Add(pair.Key);
            foreach (var point in points) pointViews.Remove(point);
            regionViews.Remove(regionId);
            foreach (var actor in World.Agents.Values)
                if (actor.PlaceId == regionId) actor.PlaceId = null;
            foreach (var place in World.Places.Values) place.NeighbourIds.Remove(regionId);
            World.Emit("region.unloaded", "world.regions", regionId, null, regionId);
        }

        private void ValidateEntrances()
        {
            var entrances = new Dictionary<string, SocialRegionEntrance>(StringComparer.Ordinal);
            foreach (var pair in regionViews)
                foreach (var entrance in pair.Value.GetComponentsInChildren<SocialRegionEntrance>(true))
                {
                    var container = pair.Value.transform.Find("Entrances");
                    if (string.IsNullOrWhiteSpace(entrance.LocalId) || entrance.transform.parent != container ||
                        !entrances.TryAdd(pair.Key + "/" + entrance.LocalId, entrance))
                        throw new InvalidOperationException("Invalid or duplicate region entrance: " + pair.Key);
                    if (!UnityEngine.AI.NavMesh.SamplePosition(entrance.transform.position, out _, 1.5f, UnityEngine.AI.NavMesh.AllAreas))
                        throw new InvalidOperationException("Region entrance is outside the NavMesh: " + pair.Key + "/" + entrance.LocalId);
                }
            foreach (var pair in entrances)
            {
                var entrance = pair.Value;
                if (string.IsNullOrEmpty(entrance.TargetRegionInstanceId) && string.IsNullOrEmpty(entrance.TargetEntranceLocalId)) continue;
                if (!entrances.TryGetValue(entrance.TargetRegionInstanceId + "/" + entrance.TargetEntranceLocalId, out var target))
                    throw new InvalidOperationException("Region entrance connection is missing its destination: " + pair.Key);
                var path = new UnityEngine.AI.NavMeshPath();
                if (!UnityEngine.AI.NavMesh.CalculatePath(entrance.transform.position, target.transform.position,
                        UnityEngine.AI.NavMesh.AllAreas, path) || path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete)
                    throw new InvalidOperationException("Region entrance connection has no complete navigation path: " + pair.Key);
                var source = entrance.GetComponentInParent<SocialWorldMarker>().RegionInstanceId;
                if (!World.Places[source].NeighbourIds.Contains(entrance.TargetRegionInstanceId))
                    World.Places[source].NeighbourIds.Add(entrance.TargetRegionInstanceId);
            }
        }

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsValidRotation(Quaternion value)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z) || !IsFinite(value.w)) return false;
            var norm = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            return Mathf.Abs(norm - 1f) < 0.01f;
        }

        public void SetPaused(bool paused)
        {
            if (World == null) return;
            World.Paused = paused;
            SetNavigationPaused(paused);
        }

        public bool TryStepOne(out string error)
        {
            error = null;
            if (World == null || faulted)
            { error = faultMessage ?? "World is not ready."; return false; }
            try { World.StepOne(); return true; }
            catch (Exception exception)
            {
                RecordFault(exception);
                error = faultMessage;
                return false;
            }
        }

        private void RecordFault(Exception exception)
        {
            if (faulted) return;
            faulted = true;
            faultMessage = "模拟已在 Tick " + World.TickIndex + " / " + FormatClock(World.Time) +
                " 暂停：" + exception.Message;
            SetPaused(true);
            Debug.LogException(exception, this);
        }

        private void SetNavigationPaused(bool paused)
        {
            foreach (var nav in npcNavigators.Values) if (nav != null) nav.SetSimulationPaused(paused);
        }

        private void RememberEvent(WorldEvent item)
        {
            if (item == null) return;
            var entry = FormatClock(item.Time) + " " + item.Type + " " + item.SubjectId;
            recentEvents.Add(entry);
            if (recentEvents.Count > 8) recentEvents.RemoveAt(0);
        }

        private static string FormatClock(double elapsedSeconds)
        {
            var seconds = (int)Math.Floor(elapsedSeconds) + 7 * 3600 + 45 * 60;
            return (seconds / 3600).ToString("00") + ":" + ((seconds / 60) % 60).ToString("00");
        }

        private void OnGUI()
        {
            if (!showOverlay || World == null) return;
            if (overlayStyle == null)
            {
                overlayStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13 };
                overlayStyle.normal.textColor = Color.white;
            }
            GUILayout.BeginArea(new Rect(12f, 12f, 390f, 300f), overlayStyle);
            GUILayout.Label("SocialSimulation · " + FormatClock(World.Time));
            GUILayout.Label("NPC " + World.Agents.Count + "  Places " + World.Places.Count +
                            "  Sessions " + World.Activities.Sessions.Count + "  Events " + World.Trace.Count);
            if (faulted) GUILayout.Label(faultMessage);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(World.Paused ? "Resume" : "Pause") && !faulted) SetPaused(!World.Paused);
            if (GUILayout.Button("Step") && !faulted) TryStepOne(out _);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save"))
            {
                try { SaveGame(); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
            if (GUILayout.Button("Load"))
            {
                try { LoadGame(); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("x1")) World.TimeScale = 1;
            if (GUILayout.Button("x30")) World.TimeScale = 30;
            if (GUILayout.Button("x120")) World.TimeScale = 120;
            GUILayout.EndHorizontal();
            foreach (var entry in recentEvents) GUILayout.Label(entry);
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            if (World != null) World.EventPublished -= RememberEvent;
            foreach (var order in travelOrders.Values) if (order.Navigation != null) order.Navigation.CancelMove();
            travelOrders.Clear();
        }
    }
}
