using System.Collections.Generic;
using SocialSimulation.UnityRuntime;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace SocialSimulation.Editor.Samples
{
    /// <summary>
    /// Upgrades a generated social world scene with real-looking Kenney (CC0) furniture and
    /// buildings while keeping every logical component (SocialWorldMarker / Zone /
    /// SocialInteractionPoint / Navigation Ground) untouched. Furniture is attached as visual
    /// children of the existing interaction points; large furniture away from anchors is carved
    /// out of the NavMesh; a reachability audit verifies every anchor stays navigable after the bake.
    /// </summary>
    public static class RealisticWorldEnhancer
    {
        private const string FurnitureModels = "Assets/SocialSimulation/Art/KenneyFurnitureKit/Models";
        private const string CityModels = "Assets/SocialSimulation/Art/KenneyCityKit/Models";
        private const string CityTexture = "Assets/SocialSimulation/Art/KenneyCityKit/Textures/colormap.png";
        private const string MaterialFolder = "Assets/SocialSimulation/Art/KenneyMaterials";
        private const string EnvironmentRootName = "Realistic Environment";

        private static readonly Dictionary<string, GameObject> ModelCache = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, Material> SolidMaterialCache = new Dictionary<string, Material>();
        private static Material cityAtlasMaterial;

        [MenuItem("SocialSimulation/Samples/Enhance Realistic World")]
        public static void Enhance()
        {
            var world = GameObject.Find("SocialSimulation Generated World");
            if (world == null) throw new System.InvalidOperationException("当前场景没有生成的社交世界根节点，请先生成或打开真实场景副本。");
            EnsureFolder(MaterialFolder);
            var scene = EditorSceneManager.GetActiveScene();

            var environment = GameObject.Find(EnvironmentRootName);
            if (environment != null) Object.DestroyImmediate(environment);
            // Buildings are parented to the environment root, but a previous buggy run may have
            // left them as scene roots — clear those too so re-runs never stack duplicates.
            foreach (var root in scene.GetRootGameObjects())
                if (root.name.StartsWith("Building: ")) Object.DestroyImmediate(root);
            environment = new GameObject(EnvironmentRootName);

            RestorePointFurniture(world.transform);
            ReplaceFurniture(world.transform);
            BuildSurroundings(environment.transform);
            ConfigureSun(world.transform);
            BakeNavMesh(world.transform);
            AuditReachability(world.transform);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[RealisticWorldEnhancer] 场景已增强并保存：{scene.path}");
        }

        // ---------------------------------------------------------------- furniture

        private static void RestorePointFurniture(Transform world)
        {
            // Make re-runs idempotent: drop fitted visuals and restore the debug cubes.
            var points = world.GetComponentsInChildren<SocialInteractionPoint>(true);
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var cube = primitive.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(primitive);
            foreach (var point in points)
            {
                for (var i = point.transform.childCount - 1; i >= 0; i--)
                {
                    var child = point.transform.GetChild(i);
                    if (child.name.StartsWith("Furniture: ")) Object.DestroyImmediate(child.gameObject);
                }
                var renderer = point.GetComponent<MeshRenderer>();
                var filter = point.GetComponent<MeshFilter>();
                if (renderer != null) renderer.enabled = true;
                if (filter != null) filter.sharedMesh = cube;
            }
            foreach (var marker in Object.FindObjectsByType<SocialWorldMarker>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                for (var i = marker.transform.childCount - 1; i >= 0; i--)
                {
                    var child = marker.transform.GetChild(i);
                    if (child.name.StartsWith("Decor: ")) Object.DestroyImmediate(child.gameObject);
                }
        }

        private static void ReplaceFurniture(Transform world)
        {
            var markers = Object.FindObjectsByType<SocialWorldMarker>(FindObjectsSortMode.None);
            var fitted = new List<BoxCollider>();
            foreach (var marker in markers)
            {
                if (marker.GetComponentInParent<SocialNpcMarker>() != null) continue;
                var zones = marker.transform.Find("Zones");
                if (zones == null) continue;
                foreach (var zone in zones.GetComponentsInChildren<SocialRegionZone>(true))
                {
                    var points = zone.transform.Find("InteractionPoints");
                    if (points == null) continue;
                    foreach (var point in points.GetComponentsInChildren<SocialInteractionPoint>(true))
                        FitPointFurniture(marker, point, fitted);
                }
                DecorateRegionEdges(marker);
            }
        }

        private static void FitPointFurniture(SocialWorldMarker marker, SocialInteractionPoint point, List<BoxCollider> fitted)
        {
            // Decide the visual model from region + action. Returns placement parameters.
            string model; float target = 1f; float rotY; Vector3 offset; bool carve; bool byHeight;
            if (!ResolveModel(marker.RegionDefinitionId, point.ActionId, point.LocalId, out model, out target, out rotY, out offset, out carve, out byHeight)) return;

            HideCubeVisual(point.transform);
            var visual = InstantiateFitted(model, point.transform, offset, rotY, target,
                fitBy: byHeight ? FitAxis.Height : FitAxis.Largest);
            if (visual == null) return;
            visual.name = "Furniture: " + model;

            if (carve)
            {
                var collider = visual.AddComponent<BoxCollider>();
                var bounds = ComputeBounds(visual);
                collider.center = visual.transform.InverseTransformPoint(bounds.center);
                collider.size = new Vector3(Mathf.Min(bounds.size.x, 2.4f), bounds.size.y, Mathf.Min(bounds.size.z, 2.4f));
                fitted.Add(collider);
                MarkNotWalkable(visual);
            }
            else
            {
                MarkIgnoreFromBuild(visual);
            }
        }

        private static bool ResolveModel(string region, string actionId, string localId,
            out string model, out float target, out float rotY, out Vector3 offset, out bool carve, out bool byHeight)
        {
            model = null; target = 1f; rotY = 0f; offset = Vector3.zero; carve = false; byHeight = false;
            switch (region)
            {
                case "campus.classroom":
                    if (actionId == "posture.sit") { model = "chair"; target = 0.85f; rotY = 0f; offset = new Vector3(0, 0, 0.22f); byHeight = true; }
                    else if (actionId == "communication.speak") { model = "desk"; target = 0.78f; rotY = 0f; offset = new Vector3(0, 0, 0.95f); carve = true; byHeight = true; }
                    else if (actionId == "perception.observe") { model = "plantSmall1"; target = 0.7f; offset = new Vector3(0, 0, 0.85f); carve = true; byHeight = true; }
                    break;
                case "campus.office":
                    if (actionId == "work.prepare") { model = "desk"; target = 0.78f; rotY = 0f; offset = new Vector3(0, 0, 0.95f); carve = true; byHeight = true; }
                    else if (actionId == "communication.speak") { model = "tableCoffee"; target = 0.42f; offset = new Vector3(0, 0, 1.1f); carve = true; byHeight = true; }
                    break;
                case "campus.corridor":
                    if (actionId == "locomotion.walk") { model = "bench"; target = 0.85f; rotY = 90f; offset = new Vector3(0, 0, 0.82f); carve = true; byHeight = true; }
                    break;
                case "campus.yard":
                    if (actionId == "communication.speak") { model = "bench"; target = 0.85f; rotY = 0f; offset = new Vector3(0, 0, 0.82f); carve = true; byHeight = true; }
                    break;
                case "household.home":
                    if (actionId == "posture.sit") { model = "chairCushion"; target = 0.85f; rotY = 0f; offset = new Vector3(0, 0, 0.22f); byHeight = true; }
                    else if (actionId == "communication.speak")
                    {
                        // Living room: sofa behind the talk anchor, coffee table to the side.
                        model = localId == "family.talk" ? "loungeSofa" : "tableCoffee";
                        target = localId == "family.talk" ? 0.82f : 0.42f;
                        byHeight = true;
                        rotY = 180f;
                        offset = localId == "family.talk" ? new Vector3(0, 0, 1.2f) : new Vector3(1.15f, 0, -0.95f);
                        carve = true;
                    }
                    break;
                case "trade.shop":
                    if (actionId == "trade.exchange") { model = "kitchenBar"; target = 0.95f; rotY = 180f; offset = new Vector3(0, 0, 0.95f); carve = true; byHeight = true; }
                    else if (actionId == "activity.wait") { model = "rugRectangle"; target = 1.4f; offset = new Vector3(0, 0, 0f); }
                    else if (actionId == "object.pickup") { model = "bookcaseClosed"; target = 2.0f; rotY = 180f; offset = new Vector3(0, 0, 0.92f); carve = true; byHeight = true; }
                    break;
                case "trade.workshop":
                    if (actionId == "work.perform") { model = "desk"; target = 0.78f; rotY = 180f; offset = new Vector3(0, 0, 0.95f); carve = true; byHeight = true; }
                    else if (actionId == "communication.speak") { model = "bench"; target = 0.85f; rotY = 90f; offset = new Vector3(0, 0, 0.82f); carve = true; byHeight = true; }
                    break;
                case "community.square":
                    if (actionId == "communication.speak") { model = "bench"; target = 0.85f; rotY = 0f; offset = new Vector3(0, 0, 0.82f); carve = true; byHeight = true; }
                    break;
            }
            return model != null;
        }

        private static void DecorateRegionEdges(SocialWorldMarker marker)
        {
            var visuals = marker.transform.Find("Visuals");
            if (visuals == null) return;
            var bounds = marker.LocalBounds;
            var half = new Vector3(bounds.extents.x, 0f, bounds.extents.z);
            switch (marker.RegionDefinitionId)
            {
                case "household.home":
                    PlaceDecor("televisionModern", marker.transform, new Vector3(0f, 0f, half.z - 0.45f), 180f, 0.65f, byHeight: true);
                    PlaceDecor("lampSquareFloor", marker.transform, new Vector3(-half.x + 0.5f, 0f, half.z - 0.5f), 0f, 1.6f, carve: true, byHeight: true);
                    PlaceDecor("table", marker.transform, new Vector3(0f, 0f, -2.15f), 0f, 0.75f, byHeight: true);
                    break;
                case "campus.office":
                    PlaceDecor("bookcaseOpen", marker.transform, new Vector3(0f, 0f, half.z - 0.4f), 180f, 2.2f, carve: true, byHeight: true);
                    break;
                case "community.square":
                    PlaceDecor("detail-parasol-a", marker.transform, new Vector3(-half.x + 1.2f, 0f, -half.z + 1.2f), 0f, 1.4f, carve: true);
                    PlaceDecor("detail-parasol-b", marker.transform, new Vector3(half.x - 1.2f, 0f, half.z - 1.2f), 0f, 1.4f, carve: true);
                    break;
                case "trade.shop":
                    PlaceDecor("plantSmall2", marker.transform, new Vector3(-half.x + 0.6f, 0f, -half.z + 0.6f), 0f, 0.8f, carve: true);
                    break;
            }
        }

        private static GameObject PlaceDecor(string model, Transform parent, Vector3 localPosition, float rotY, float target, bool carve = false, bool byHeight = false)
        {
            var visual = InstantiateFitted(model, parent, localPosition, rotY, target,
                fitBy: byHeight ? FitAxis.Height : FitAxis.Largest);
            if (visual == null) return null;
            visual.name = "Decor: " + model;
            if (carve)
            {
                var collider = visual.AddComponent<BoxCollider>();
                var bounds = ComputeBounds(visual);
                collider.center = visual.transform.InverseTransformPoint(bounds.center);
                // Parasols and other wide canopies must not carve a huge footprint.
                collider.size = new Vector3(Mathf.Min(bounds.size.x, 0.8f), bounds.size.y, Mathf.Min(bounds.size.z, 0.8f));
                MarkNotWalkable(visual);
            }
            else
            {
                MarkIgnoreFromBuild(visual);
            }
            return visual;
        }

        // ---------------------------------------------------------------- surroundings

        private static void BuildSurroundings(Transform environment)
        {
            var grass = CreateBox(environment, "Suburban Ground", new Vector3(0f, -0.245f, -3f), new Vector3(150f, 0.32f, 140f),
                new Color(0.42f, 0.58f, 0.34f));
            grass.isStatic = true;

            // Street ring just outside the compound ground (ground spans x[-25,25] z[-27.5,21.5]).
            var asphalt = new Color(0.30f, 0.31f, 0.33f);
            CreateBox(environment, "Street N", new Vector3(0f, -0.11f, 24.5f), new Vector3(90f, 0.06f, 6f), asphalt).isStatic = true;
            CreateBox(environment, "Street S", new Vector3(0f, -0.11f, -31f), new Vector3(90f, 0.06f, 6f), asphalt).isStatic = true;
            CreateBox(environment, "Street W", new Vector3(-28f, -0.11f, -3.25f), new Vector3(6f, 0.06f, 64f), asphalt).isStatic = true;
            CreateBox(environment, "Street E", new Vector3(28f, -0.11f, -3.25f), new Vector3(6f, 0.06f, 64f), asphalt).isStatic = true;

            string[] north = { "building-a", "building-c", "building-e", "building-g" };
            string[] south = { "building-b", "building-d", "building-f", "building-h" };
            string[] west = { "building-i", "building-k", "building-m" };
            string[] east = { "building-j", "building-l", "building-n" };
            var xs = new[] { -33f, -19f, -5f, 9f, 23f, 37f };
            var zs = new[] { -26f, -12f, 2f, 16f };
            PlaceBuildingRow(north, new[] { xs[0], xs[1], xs[2], xs[3], xs[4], xs[5] }, 31.5f, 180f, 10f, parent: environment.transform);
            PlaceBuildingRow(south, new[] { xs[1], xs[2], xs[3], xs[4] }, -38f, 0f, 10f, parent: environment.transform);
            PlaceBuildingRow(west, new[] { zs[3], zs[2], zs[1] }, -37f, -90f, 10f, true, environment.transform);
            PlaceBuildingRow(east, new[] { zs[3], zs[2], zs[1] }, 37f, 90f, 10f, true, environment.transform);
            PlaceBuildingRow(new[] { "low-detail-building-a", "low-detail-building-c", "low-detail-building-e" },
                new[] { xs[1], xs[3], xs[5] }, 42f, 180f, 12f, parent: environment.transform);
        }

        private static void PlaceBuildingRow(string[] models, float[] coords, float line, float rotY, float targetHeight, bool alongZ = false, Transform parent = null)
        {
            for (var i = 0; i < models.Length && i < coords.Length; i++)
            {
                var position = alongZ ? new Vector3(line, 0f, coords[i]) : new Vector3(coords[i], 0f, line);
                PlaceBuilding(models[i], position, rotY, targetHeight, parent);
            }
        }

        private static void PlaceBuilding(string model, Vector3 position, float rotY, float targetHeight, Transform parent)
        {
            var visual = InstantiateFitted(model, parent, position, rotY, targetHeight, fitBy: FitAxis.Height);
            if (visual == null) return;
            visual.name = "Building: " + model;
            visual.isStatic = true;
            var collider = visual.AddComponent<BoxCollider>();
            var bounds = ComputeBounds(visual);
            collider.center = visual.transform.InverseTransformPoint(bounds.center);
            collider.size = bounds.size;
            MarkNotWalkable(visual);
        }

        private static GameObject CreateBox(Transform parent, string name, Vector3 position, Vector3 size, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.transform.localScale = size;
            var material = GetOrCreateSolidMaterial(color);
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        // ---------------------------------------------------------------- model plumbing

        private enum FitAxis { Largest, Height }

        private static GameObject InstantiateFitted(string model, Transform parent, Vector3 localPosition, float rotY, float target, FitAxis fitBy = FitAxis.Largest)
        {
            var prefab = LoadModel(model);
            if (prefab == null)
            {
                Debug.LogWarning($"[RealisticWorldEnhancer] 缺少模型：{model}");
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);

            ApplyKitMaterial(go, model);

            // Two-pass fitting: renderer bounds can lag behind a transform change, so measure,
            // scale, then correct against the refreshed measurement.
            var bounds = ComputeBounds(go);
            var reference = fitBy == FitAxis.Height ? bounds.size.y : Mathf.Max(bounds.size.x, bounds.size.z);
            var scale = Mathf.Max(0.001f, target / Mathf.Max(0.001f, reference));
            go.transform.localScale = go.transform.localScale * scale;

            bounds = ComputeBounds(go);
            reference = fitBy == FitAxis.Height ? bounds.size.y : Mathf.Max(bounds.size.x, bounds.size.z);
            if (reference > 0.0001f && !Mathf.Approximately(reference, target))
            {
                scale = Mathf.Max(0.001f, target / reference);
                go.transform.localScale = go.transform.localScale * scale;
            }

            bounds = ComputeBounds(go);
            var delta = go.transform.position - bounds.center;
            delta.y = go.transform.position.y - bounds.min.y;
            go.transform.position += delta;
            return go;
        }

        private static GameObject LoadModel(string model)
        {
            if (ModelCache.TryGetValue(model, out var cached) && cached != null) return cached;
            var path = $"{FurnitureModels}/{model}.fbx";
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                path = $"{CityModels}/{model}.fbx";
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            ModelCache[model] = asset;
            return asset;
        }

        private static void ApplyKitMaterial(GameObject go, string model)
        {
            var isCity = model.StartsWith("building") || model.StartsWith("detail-") || model.StartsWith("low-detail");
            var renderers = go.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                if (isCity)
                {
                    renderer.sharedMaterial = GetCityAtlasMaterial();
                    continue;
                }
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null) continue;
                    var color = source.HasProperty("_Color") ? source.color : Color.white;
                    materials[i] = GetOrCreateSolidMaterial(color);
                }
                renderer.sharedMaterials = materials;
            }
        }

        private static Material GetCityAtlasMaterial()
        {
            if (cityAtlasMaterial != null) return cityAtlasMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            cityAtlasMaterial = new Material(shader) { name = "Kenney_City_Atlas" };
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(CityTexture);
            if (texture != null) cityAtlasMaterial.mainTexture = texture;
            cityAtlasMaterial.SetFloat("_Smoothness", 0.25f);
            AssetDatabase.CreateAsset(cityAtlasMaterial, $"{MaterialFolder}/Kenney_City_Atlas.mat");
            return cityAtlasMaterial;
        }

        private static Material GetOrCreateSolidMaterial(Color color)
        {
            var key = ColorUtility.ToHtmlStringRGB(color);
            if (SolidMaterialCache.TryGetValue(key, out var cached) && cached != null) return cached;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = new Material(shader) { name = $"Kenney_Solid_{key}" };
            material.color = color;
            material.SetFloat("_Smoothness", 0.3f);
            var path = $"{MaterialFolder}/Kenney_Solid_{key}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) == null) AssetDatabase.CreateAsset(material, path);
            else material = AssetDatabase.LoadAssetAtPath<Material>(path);
            SolidMaterialCache[key] = material;
            return material;
        }

        private static void HideCubeVisual(Transform point)
        {
            var renderer = point.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.enabled = false;
            var filter = point.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = null;
        }

        private static Bounds ComputeBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static void MarkNotWalkable(GameObject go)
        {
            var modifier = go.GetComponent<NavMeshModifier>() ?? go.AddComponent<NavMeshModifier>();
            modifier.ignoreFromBuild = false;
            modifier.overrideArea = true;
            modifier.area = NavMesh.GetAreaFromName("Not Walkable");
        }

        private static void MarkIgnoreFromBuild(GameObject go)
        {
            var modifier = go.GetComponent<NavMeshModifier>() ?? go.AddComponent<NavMeshModifier>();
            modifier.overrideArea = false;
            modifier.ignoreFromBuild = true;
        }

        // ---------------------------------------------------------------- navmesh + audit

        private static void BakeNavMesh(Transform world)
        {
            var surface = world.GetComponentInChildren<NavMeshSurface>(true);
            if (surface == null) throw new System.InvalidOperationException("场景中找不到 NavMeshSurface。");
            surface.collectObjects = CollectObjects.All;
            // PhysicsCollides collection fragments the bake on this Unity version; RenderMeshes
            // produces a contiguous walkable surface for the same scene.
            surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
            // NPC agents query with the default human agent type; data baked under any other
            // registered agent type is invisible to their NavMeshQueryFilter.
            surface.agentTypeID = 0;
            surface.BuildNavMesh();
            Debug.Log($"[RealisticWorldEnhancer] NavMesh 已按全场景渲染网格重烘焙（agentTypeID={surface.agentTypeID}）。");
        }

        private static void AuditReachability(Transform world)
        {
            var points = world.GetComponentsInChildren<SocialInteractionPoint>(true);
            var failures = new List<string>();
            // Query with the same explicit filter the runtime NPC agents use, not the area-mask
            // overload — the overload sees navmesh data across agent types and would mask a
            // surface.agentTypeID mismatch.
            var filter = new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };
            foreach (var point in points)
            {
                var anchor = point.AnchorPosition;
                if (!NavMesh.SamplePosition(anchor, out var hit, 0.9f, filter))
                {
                    failures.Add($"{point.StableId} anchor 不在 NavMesh 附近 ({anchor:F2})");
                    continue;
                }
                var entrance = point.GetComponentInParent<SocialWorldMarker>()?.GetComponentInChildren<SocialRegionEntrance>(true);
                var start = entrance != null ? entrance.transform.position : anchor;
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(start, hit.position, filter, path) || path.status != NavMeshPathStatus.PathComplete)
                    failures.Add($"{point.StableId} 从入口 {start:F2} 无法通行到锚点 ({anchor:F2})");
            }
            if (failures.Count > 0)
                Debug.LogWarning($"[RealisticWorldEnhancer] 可达性审计发现 {failures.Count} 个问题：\n" + string.Join("\n", failures));
            else
                Debug.Log($"[RealisticWorldEnhancer] 可达性审计通过：{points.Length} 个交互锚点全部可达。");
        }

        private static void ConfigureSun(Transform world)
        {
            var sun = world.Find("Sun")?.GetComponent<Light>();
            if (sun == null) return;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.8f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.intensity = 1.35f;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            var leaf = System.IO.Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent)) return;
            EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
