using System;
using System.Collections.Generic;
using SocialSimulation.Authoring;
using SocialSimulation.Core;
using SocialSimulation.Domains;
using SocialSimulation.Domains.Combined;
using SocialSimulation.Domains.Campus;
using SocialSimulation.Domains.Household;
using SocialSimulation.Domains.WorkAndTrade;
using SocialSimulation.Domains.DomainActions;
using SocialSimulation.Graph;
using SocialSimulation.UnityRuntime;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace SocialSimulation.Editor.Samples
{
    /// <summary>Creates a repeatable, editable three-domain demo without requiring external art.</summary>
    public static class CombinedWorldGenerator
    {
        public const string ScenePath = "Assets/SocialSimulation/Domains/Combined/CombinedSocialWorld.unity";
        public const string IndependentScenePath = "Assets/SocialSimulation/Domains/Combined/IndependentCommunity.unity";
        private const string GeneratedRootName = "SocialSimulation Generated World";
        private const string MaterialFolder = "Assets/SocialSimulation/Domains/Combined/Materials";
        private const string NavMeshPath = "Assets/SocialSimulation/Domains/Combined/CombinedWorldNavMesh.asset";
        private const string CharacterPackageRoot = "Assets/SocialSimulation/Art/KenneyAnimatedProtagonists";
        private const string CharacterModelPath = CharacterPackageRoot + "/Model/characterMedium.fbx";
        private const string CharacterPrefabPath = CharacterPackageRoot + "/Prefab/Character.prefab";
        private const string CharacterPackagePath = CharacterPackageRoot + "/NpcPackage.asset";
        private const string GenericPackagePath = "Assets/SocialSimulation/NpcPackages/character_a/NpcPackage.asset";
        private const string AnimationFolder = CharacterPackageRoot + "/Animations/";
        private const string KayKitFolder = "Assets/SocialSimulation/Art/KayKitCharacterAnimations/";
        private const string SkinFolder = "Assets/SocialSimulation/Art/KenneyAnimatedProtagonists/Textures/";
        private const string AnimatorControllerPath = AnimationFolder + "KenneyDemo.controller";
        private static GameObject characterModel;
        private static GameObject characterPrefab;
        private static Avatar characterAvatar;
        private static RuntimeAnimatorController characterController;
        private static Material[] characterSkins;

        private static readonly Color CampusColor = new Color(0.27f, 0.47f, 0.82f);
        private static readonly Color HouseholdColor = new Color(0.90f, 0.62f, 0.29f);
        private static readonly Color TradeColor = new Color(0.31f, 0.68f, 0.50f);
        private static readonly Color NeutralColor = new Color(0.64f, 0.68f, 0.73f);

        [MenuItem("SocialSimulation/Samples/Generate Combined World")]
        public static void Generate()
        {
            EnsureFolder("Assets/SocialSimulation/Domains/Combined");
            EnsureFolder(MaterialFolder);

            var scene = GetOrCreateScene();
            SceneManager.SetActiveScene(scene);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == GeneratedRootName)
                    UnityEngine.Object.DestroyImmediate(root);
            }

            var generated = new GameObject(GeneratedRootName);
            generated.AddComponent<SocialWorldRunner>();
            var contentProvider = generated.AddComponent<SocialCatalogProvider>();
            var scenario = generated.AddComponent<CombinedDemoScenario>();
            generated.AddComponent<CampusDomainModule>();
            var householdModule = generated.AddComponent<HouseholdDomainModule>();
            var workModule = generated.AddComponent<WorkAndTradeDomainModule>();
            householdModule.Configure("household.home.river", "household.river", "npc.teacher.01",
                "npc.clerk.01", "npc.student.01", "npc.student.02");
            workModule.Configure("trade.shop.corner", "trade.corner_shop", "community.square",
                "community.walking_club", "npc.clerk.01", "npc.clerk.02",
                "npc.worker.01", "npc.student.01");
            householdModule.ConfigureGraphs(EnsureDomainGraph("Household", "Care", "behavior.household.care",
                "household.care"));
            workModule.ConfigureGraphs(
                EnsureDomainGraph("WorkAndTrade", "ShiftAndWage", "behavior.trade.shift", "trade.shift", "trade.pay_wage"),
                EnsureDomainGraph("WorkAndTrade", "Purchase", "behavior.trade.purchase", "trade.purchase"),
                EnsureDomainGraph("WorkAndTrade", "ClubMeetup", "behavior.community.meetup", "community.walking_club.meetup"));
            scenario.Configure(
                EnsureDomainAsset("Campus", "campus", "校园",
                    new[] { "campus.student", "campus.teacher", "campus.leader" },
                    new[] { "classmate", "colleague" }, new[] { "campus.course", "campus.break", "campus.inspect" },
                    new[] { "locomotion.walk", "locomotion.run", "posture.sit", "communication.speak" }),
                EnsureDomainAsset("Household", "household", "家庭",
                    new[] { "household.parent", "household.child" }, new[] { "kinship", "care" },
                    new[] { "household.breakfast", "household.talk" }, new[] { "activity.breakfast", "communication.speak" }),
                EnsureDomainAsset("WorkAndTrade", "work_and_trade", "工作、交易与社团",
                    new[] { "trade.clerk", "trade.worker", "trade.customer", "community.member" },
                    new[] { "employment", "customer", "club.member" },
                    new[] { "trade.shift", "trade.pay_wage", "trade.purchase", "community.walking_club.meetup" },
                    new[] { "trade.exchange", "work.perform", "communication.speak" }, "1.2.0"),
                EnsureCourseBehaviorGraph(), EnsureCorridorRuleGraph());

            var baseMaterial = EnsureMaterial("Ground", new Color(0.79f, 0.81f, 0.75f));
            var campusMaterial = EnsureMaterial("Campus", CampusColor);
            var householdMaterial = EnsureMaterial("Household", HouseholdColor);
            var tradeMaterial = EnsureMaterial("Trade", TradeColor);
            var furnitureMaterial = EnsureMaterial("Furniture", new Color(0.36f, 0.32f, 0.29f));
            var studentMaterial = EnsureMaterial("Student", new Color(0.20f, 0.40f, 0.79f));
            var teacherMaterial = EnsureMaterial("Teacher", new Color(0.53f, 0.29f, 0.71f));
            var otherMaterial = EnsureMaterial("OtherNpc", new Color(0.20f, 0.62f, 0.46f));
            PrepareCharacterVisuals();
            EnsureCharacterPrefab();
            BindNpcAnimationPackage();

            CreateCamera(generated.transform);
            CreateSun(generated.transform);
            CreateWalkableGround(generated.transform, baseMaterial);

            var classroomA = CreateRegion(generated.transform, "campus.classroom.a", "campus.classroom", "教室 A", "campus", new Vector3(-18f, 0f, 12f), new Vector2(10f, 9f), campusMaterial);
            var classroomB = CreateRegion(generated.transform, "campus.classroom.b", "campus.classroom", "教室 B", "campus", new Vector3(-6f, 0f, 12f), new Vector2(10f, 9f), campusMaterial);
            var office = CreateRegion(generated.transform, "campus.office", "campus.office", "办公室", "campus", new Vector3(6f, 0f, 12f), new Vector2(10f, 9f), campusMaterial);
            var corridor = CreateRegion(generated.transform, "campus.corridor", "campus.corridor", "走廊", "campus", new Vector3(-6f, 0f, 2.5f), new Vector2(34f, 5f), campusMaterial);
            var yard = CreateRegion(generated.transform, "campus.yard", "campus.yard", "操场", "campus", new Vector3(14f, 0f, -7f), new Vector2(14f, 13f), campusMaterial);
            var home = CreateRegion(generated.transform, "household.home.river", "household.home", "河畔家庭", "household", new Vector3(-18f, 0f, -13f), new Vector2(10f, 9f), householdMaterial);
            var shop = CreateRegion(generated.transform, "trade.shop.corner", "trade.shop", "社区商店", "work_and_trade", new Vector3(-6f, 0f, -13f), new Vector2(10f, 9f), tradeMaterial);
            var workplace = CreateRegion(generated.transform, "trade.workshop", "trade.workshop", "工作单位", "work_and_trade", new Vector3(6f, 0f, -18f), new Vector2(10f, 7f), tradeMaterial);
            var communitySquare = CreateRegion(generated.transform, "community.square", "community.square",
                "社区广场", "work_and_trade", new Vector3(17f, 0f, -19f), new Vector2(12f, 9f), tradeMaterial);

            FillClassroom(classroomA, 1, furnitureMaterial);
            FillClassroom(classroomB, 2, furnitureMaterial);
            FillOffice(office, furnitureMaterial);
            FillCorridor(corridor);
            FillYard(yard);
            FillHome(home, furnitureMaterial);
            FillShop(shop, furnitureMaterial);
            FillWorkplace(workplace, furnitureMaterial);
            FillYard(communitySquare);
            EnsureRegionPackageInstance(classroomA, "Campus", "Classroom",
                "region.campus.classroom", "ClassroomRegionPackage");
            ReplaceRegionInstance(classroomB,
                "Assets/SocialSimulation/Domains/Campus/Packages/Classroom/RegionPackage.asset");
            EnsureRegionPackageInstance(office, "Campus", "Office",
                "region.campus.office", "OfficeRegionPackage");
            EnsureRegionPackageInstance(corridor, "Campus", "Corridor",
                "region.campus.corridor", "CorridorRegionPackage");
            EnsureRegionPackageInstance(yard, "Campus", "Yard",
                "region.campus.yard", "YardRegionPackage");
            EnsureRegionPackageInstance(home, "Household", "Home",
                "region.household.home", "HomeRegionPackage");
            EnsureRegionPackageInstance(shop, "WorkAndTrade", "Shop",
                "region.trade.shop", "ShopRegionPackage");
            EnsureRegionPackageInstance(workplace, "WorkAndTrade", "Workshop",
                "region.trade.workshop", "WorkshopRegionPackage");
            EnsureRegionPackageInstance(communitySquare, "WorkAndTrade", "Square",
                "region.community.square", "SquareRegionPackage");
            CreateCollectives(generated.transform);
            CreatePopulation(generated.transform, studentMaterial, teacherMaterial, otherMaterial);
            ConnectRegionRing(generated.transform, "campus.classroom.a", "campus.classroom.b", "campus.office",
                "campus.corridor", "campus.yard", "community.square", "trade.workshop", "trade.shop.corner", "household.home.river");

            var surface = generated.GetComponentInChildren<NavMeshSurface>();
            if (surface != null)
            {
                surface.BuildNavMesh();
                if (surface.navMeshData != null)
                {
                    if (AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshPath) != null)
                        AssetDatabase.DeleteAsset(NavMeshPath);
                    AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            var buildScenes = new List<EditorBuildSettingsScene>
            { new EditorBuildSettingsScene(ScenePath, true) };
            foreach (var existingScene in EditorBuildSettings.scenes)
                if (!string.Equals(existingScene.path, ScenePath, StringComparison.Ordinal))
                    buildScenes.Add(existingScene);
            EditorBuildSettings.scenes = buildScenes.ToArray();
            RegisterDemoPackages();
            contentProvider.Configure(AssetDatabase.LoadAssetAtPath<SocialContentCatalogAsset>(
                "Assets/SocialSimulation/Authoring/SocialContentCatalog.asset"));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var other = SceneManager.GetSceneAt(i);
                if (other == scene || !other.isLoaded) continue;
                if (other.isDirty)
                    Debug.LogWarning("SocialSimulation: 另一个已修改场景仍保持打开，可能导致重复 Camera/AudioListener：" + other.path);
                else EditorSceneManager.CloseScene(other, true);
            }
            Selection.activeGameObject = generated;
            Validate(scene);
            Debug.Log("SocialSimulation: 已生成三领域组合演示：" + ScenePath +
                (characterModel == null ? "。角色使用 Capsule 占位体。" : "。角色使用 Humanoid / Generic 骨骼、移动及活动动画。"));
        }

        [MenuItem("SocialSimulation/Samples/Generate Independent Community")]
        public static void GenerateIndependentCommunity()
        {
            Generate();
            var combined = SceneManager.GetSceneByPath(ScenePath);
            EditorSceneManager.SaveScene(combined, IndependentScenePath, true);
            var scene = EditorSceneManager.OpenScene(IndependentScenePath, OpenSceneMode.Single);
            SocialWorldRunner runner = null;
            foreach (var root in scene.GetRootGameObjects())
                if (root.TryGetComponent<SocialWorldRunner>(out var found)) { runner = found; break; }
            if (runner == null) throw new InvalidOperationException("Generated world is missing.");
            runner.ConfigureWorld("community.independent");
            var camera = runner.GetComponentInChildren<Camera>();
            camera.transform.position = new Vector3(0f, 29f, -42f);
            camera.transform.LookAt(new Vector3(0f, 0f, -15f));
            camera.orthographic = true;
            camera.orthographicSize = 18f;
            UnityEngine.Object.DestroyImmediate(runner.GetComponent<CombinedDemoScenario>());
            UnityEngine.Object.DestroyImmediate(runner.GetComponent<CampusDomainModule>());
            foreach (var region in runner.GetComponentsInChildren<SocialWorldMarker>(true))
                if (region.DomainId == "campus") UnityEngine.Object.DestroyImmediate(region.gameObject);
            var keep = new HashSet<string> { "npc.teacher.01", "npc.clerk.01", "npc.clerk.02",
                "npc.student.01", "npc.student.02", "npc.worker.01", "npc.worker.02" };
            foreach (var npc in runner.GetComponentsInChildren<SocialNpcMarker>(true))
            {
                if (!keep.Contains(npc.NpcInstanceId)) { UnityEngine.Object.DestroyImmediate(npc.gameObject); continue; }
                var memberships = new List<string>();
                foreach (var membership in npc.MembershipIds)
                    if (!membership.StartsWith("campus.", StringComparison.Ordinal)) memberships.Add(membership);
                var role = npc.PrimaryRoleId;
                if (role.StartsWith("campus.", StringComparison.Ordinal))
                    role = npc.NpcInstanceId == "npc.teacher.01" ? "household.parent" : "household.child";
                npc.Configure(npc.NpcInstanceId, npc.DisplayName, role, memberships.ToArray());
                if (memberships.Contains("household.river"))
                    npc.BindMembershipRole("household.river", role == "household.child" ? role : "household.parent");
                PrefabUtility.RecordPrefabInstancePropertyModifications(npc);
                EditorUtility.SetDirty(npc);
            }
            foreach (var collective in runner.GetComponentsInChildren<SocialCollectiveMarker>(true))
                if (collective.CollectiveId.StartsWith("campus.", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(collective.gameObject);
            ConnectRegionRing(runner.transform, "household.home.river", "trade.shop.corner", "trade.workshop", "community.square");
            const string catalogPath = "Assets/SocialSimulation/Domains/Combined/IndependentContentCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<SocialContentCatalogAsset>(catalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<SocialContentCatalogAsset>();
                AssetDatabase.CreateAsset(catalog, catalogPath);
            }
            var source = runner.GetComponent<SocialCatalogProvider>().Catalog;
            catalog.NpcPackages.Clear(); catalog.RegionPackages.Clear(); catalog.DomainPackages.Clear();
            catalog.NpcPackages.AddRange(source.NpcPackages);
            foreach (var region in source.RegionPackages)
                if (!region.PackageId.StartsWith("region.campus.", StringComparison.Ordinal)) catalog.RegionPackages.Add(region);
            foreach (var domain in source.DomainPackages)
                if (domain.PackageId != "campus") catalog.DomainPackages.Add(domain);
            var result = catalog.BuildRegistry(new ContentRegistry());
            if (!result.Success) throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
            runner.GetComponent<SocialCatalogProvider>().Configure(catalog);
            var surface = runner.GetComponentInChildren<NavMeshSurface>();
            const string navPath = "Assets/SocialSimulation/Domains/Combined/IndependentNavMesh.asset";
            surface.BuildNavMesh();
            if (AssetDatabase.LoadAssetAtPath<NavMeshData>(navPath) != null) AssetDatabase.DeleteAsset(navPath);
            AssetDatabase.CreateAsset(surface.navMeshData, navPath);
            EditorUtility.SetDirty(catalog);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (!scenes.Exists(entry => entry.path == IndependentScenePath))
                scenes.Add(new EditorBuildSettingsScene(IndependentScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("SocialSimulation: 独立社区已生成，7 个 NPC、4 个地区、家庭与工作交易领域，无校园模块。");
        }

        [MenuItem("SocialSimulation/Samples/Validate Combined World")]
        public static void ValidateOpenScene()
        {
            var scene = SceneManager.GetSceneByPath(ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            Validate(scene);
        }

        private static void Validate(Scene scene)
        {
            var errors = new List<string>();
            var regionIds = new HashSet<string>(StringComparer.Ordinal);
            var pointIds = new HashSet<string>(StringComparer.Ordinal);
            var npcIds = new HashSet<string>(StringComparer.Ordinal);
            var domains = new HashSet<string>(StringComparer.Ordinal);
            var regionCount = 0;
            var pointCount = 0;
            var npcCount = 0;
            var hasNavMesh = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var region in root.GetComponentsInChildren<SocialWorldMarker>(true))
                {
                    regionCount++;
                    domains.Add(region.DomainId);
                    if (string.IsNullOrWhiteSpace(region.RegionInstanceId) || !regionIds.Add(region.RegionInstanceId))
                        errors.Add("重复或空地区实例 ID: " + region.RegionInstanceId);
                    ValidateRegionHierarchy(region, errors);
                }
                foreach (var point in root.GetComponentsInChildren<SocialInteractionPoint>(true))
                {
                    pointCount++;
                    if (string.IsNullOrWhiteSpace(point.StableId) || !pointIds.Add(point.StableId))
                        errors.Add("重复或缺少父地区的交互点 ID: " + point.StableId);
                }
                foreach (var npc in root.GetComponentsInChildren<SocialNpcMarker>(true))
                {
                    npcCount++;
                    if (string.IsNullOrWhiteSpace(npc.NpcInstanceId) || !npcIds.Add(npc.NpcInstanceId))
                        errors.Add("重复或空 NPC 实例 ID: " + npc.NpcInstanceId);
                }
                foreach (var navMeshSurface in root.GetComponentsInChildren<NavMeshSurface>(true))
                    hasNavMesh |= navMeshSurface.navMeshData != null;
            }
            if (!hasNavMesh) errors.Add("未发现已烘焙 NavMeshSurface。");
            if (!domains.Contains("campus") || !domains.Contains("household") || !domains.Contains("work_and_trade"))
                errors.Add("校园、家庭、工作交易三个领域没有同时出现。");
            if (npcCount < 27) errors.Add("预期至少 27 个 NPC，实际 " + npcCount);
            if (errors.Count > 0)
                Debug.LogError("SocialSimulation 组合场景校验失败：\n" + string.Join("\n", errors));
            else
                Debug.Log("SocialSimulation 组合场景校验通过：" + regionCount + " 个地区，" + pointCount + " 个交互点，" + npcCount + " 个 NPC，3 个领域，NavMesh 已烘焙。");
        }

        private static void ValidateRegionHierarchy(SocialWorldMarker region, List<string> errors)
        {
            var zonesRoot = region.transform.Find("Zones");
            if (zonesRoot == null)
            { errors.Add("地区缺少 Zones 容器: " + region.RegionInstanceId); return; }
            var zoneIds = new HashSet<string>(StringComparer.Ordinal);
            var validPoints = new HashSet<SocialInteractionPoint>();
            foreach (Transform zoneRoot in zonesRoot)
            {
                var zone = zoneRoot.GetComponent<SocialRegionZone>();
                if (zone == null || string.IsNullOrWhiteSpace(zone.LocalId) ||
                    !zoneIds.Add(zone.LocalId) || zone.Capacity < 1)
                { errors.Add("功能区缺少组件或 ID 重复: " + region.RegionInstanceId); continue; }
                var pointRoot = zoneRoot.Find("InteractionPoints");
                if (pointRoot == null)
                { errors.Add("功能区缺少 InteractionPoints: " + zone.LocalId); continue; }
                foreach (Transform pointObject in pointRoot)
                {
                    var point = pointObject.GetComponent<SocialInteractionPoint>();
                    if (point == null)
                        errors.Add("InteractionPoints 下对象缺少交互点组件: " + pointObject.name);
                    else validPoints.Add(point);
                }
            }
            if (zoneIds.Count == 0) errors.Add("地区没有功能区: " + region.RegionInstanceId);
            foreach (var point in region.GetComponentsInChildren<SocialInteractionPoint>(true))
                if (!validPoints.Contains(point))
                    errors.Add("交互点未放在功能区 InteractionPoints 直属层级: " + point.StableId);
        }

        private static Scene GetOrCreateScene()
        {
            var alreadyLoaded = SceneManager.GetSceneByPath(ScenePath);
            if (alreadyLoaded.IsValid() && alreadyLoaded.isLoaded)
                return alreadyLoaded;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            return EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = path.Substring(0, path.LastIndexOf('/'));
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
        }

        private static void EnsureRegionPackageInstance(Transform source, string domainFolder,
            string packageFolder, string packageId, string name)
        {
            var original = source.GetComponent<SocialWorldMarker>();
            var instanceId = original.RegionInstanceId;
            var definitionId = original.RegionDefinitionId;
            var label = original.DisplayName;
            var domainId = original.DomainId;
            var parent = source.parent;
            var position = source.position;
            var folder = "Assets/SocialSimulation/Domains/" + domainFolder +
                "/Packages/" + packageFolder;
            EnsureFolder(folder);
            EnsureFolder(folder + "/Prefab");
            var prefabPath = folder + "/Prefab/Region.prefab";
            var packagePath = folder + "/RegionPackage.asset";
            var template = UnityEngine.Object.Instantiate(source.gameObject);
            template.name = name;
            template.transform.SetParent(null);
            template.transform.position = Vector3.zero;
            var templateMarker = template.GetComponent<SocialWorldMarker>();
            templateMarker.Configure("template." + definitionId,
                definitionId, label, domainId);
            templateMarker.BindPackage(packageId);
            var prefab = PrefabUtility.SaveAsPrefabAsset(template, prefabPath);
            UnityEngine.Object.DestroyImmediate(template);
            if (prefab == null) throw new InvalidOperationException("Failed to create region prefab: " + prefabPath);
            var package = AssetDatabase.LoadAssetAtPath<RegionPackageAsset>(packagePath);
            if (package == null)
            {
                package = ScriptableObject.CreateInstance<RegionPackageAsset>();
                AssetDatabase.CreateAsset(package, packagePath);
            }
            var definition = new RegionDefinition
            { Id = definitionId, Tag = definitionId, Capacity = 64,
                Bounds = new RegionBounds { Enabled = original.HasBoundary,
                    CenterX = original.LocalBounds.center.x, CenterY = original.LocalBounds.center.y, CenterZ = original.LocalBounds.center.z,
                    SizeX = original.LocalBounds.size.x, SizeY = original.LocalBounds.size.y, SizeZ = original.LocalBounds.size.z } };
            foreach (var entrance in source.GetComponentsInChildren<SocialRegionEntrance>(true))
                definition.EntranceLocalIds.Add(entrance.LocalId);
            foreach (var zone in source.GetComponentsInChildren<SocialRegionZone>(true))
                definition.Zones.Add(new RegionZoneDefinition
                { LocalId = zone.LocalId, Tag = zone.Tag, Capacity = zone.Capacity });
            foreach (var point in source.GetComponentsInChildren<SocialInteractionPoint>(true))
                definition.Points.Add(new InteractionPointDefinition
                { LocalId = point.LocalId,
                    ZoneLocalId = point.GetComponentInParent<SocialRegionZone>()?.LocalId,
                    Kind = point.ActionId, Capacity = point.Capacity });
            package.Configure(packageId, "1.1.0", prefab);
            package.SetRegions(new[] { definition });
            EditorUtility.SetDirty(package);
            var catalog = AssetDatabase.LoadAssetAtPath<SocialContentCatalogAsset>(
                "Assets/SocialSimulation/Authoring/SocialContentCatalog.asset");
            if (catalog != null && !catalog.RegionPackages.Contains(package))
            { catalog.RegionPackages.Add(package); EditorUtility.SetDirty(catalog); }
            UnityEngine.Object.DestroyImmediate(source.gameObject);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null) throw new InvalidOperationException("Failed to instantiate region prefab: " + prefabPath);
            instance.transform.SetParent(parent, false);
            instance.transform.position = position;
            var instanceMarker = instance.GetComponent<SocialWorldMarker>();
            instanceMarker.Configure(instanceId, definitionId, label, domainId);
            instanceMarker.BindPackage(packageId);
        }

        private static void ReplaceRegionInstance(Transform source, string packagePath)
        {
            var sourceMarker = source.GetComponent<SocialWorldMarker>();
            var package = AssetDatabase.LoadAssetAtPath<RegionPackageAsset>(packagePath);
            if (sourceMarker == null || package == null || package.RegionPrefab == null)
                throw new InvalidOperationException("Cannot instance registered region package: " + packagePath);
            var parent = source.parent;
            var position = source.position;
            var name = source.name;
            var id = sourceMarker.RegionInstanceId;
            var label = sourceMarker.DisplayName;
            var domain = sourceMarker.DomainId;
            UnityEngine.Object.DestroyImmediate(source.gameObject);
            var instance = PrefabUtility.InstantiatePrefab(package.RegionPrefab) as GameObject;
            if (instance == null) throw new InvalidOperationException("Cannot instantiate: " + packagePath);
            instance.name = name;
            instance.transform.SetParent(parent, false);
            instance.transform.position = position;
            var instanceMarker = instance.GetComponent<SocialWorldMarker>();
            instanceMarker.Configure(id, package.Regions[0].Id, label, domain);
            instanceMarker.BindPackage(package.PackageId);
            var text = instance.GetComponentInChildren<TextMesh>(true);
            if (text != null) text.text = label;
        }

        private static void RegisterDemoPackages()
        {
            const string catalogPath = "Assets/SocialSimulation/Authoring/SocialContentCatalog.asset";
            var catalog = AssetDatabase.LoadAssetAtPath<SocialContentCatalogAsset>(catalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<SocialContentCatalogAsset>();
                AssetDatabase.CreateAsset(catalog, catalogPath);
            }
            var npc = AssetDatabase.LoadAssetAtPath<NpcPackageAsset>(CharacterPackagePath);
            if (npc == null) throw new InvalidOperationException("Kenney NPC package is missing or invalid.");
            if (!catalog.NpcPackages.Contains(npc)) catalog.NpcPackages.Add(npc);
            var generic = AssetDatabase.LoadAssetAtPath<NpcPackageAsset>(GenericPackagePath);
            if (generic == null) throw new InvalidOperationException("Generic NPC package is missing.");
            if (!catalog.NpcPackages.Contains(generic)) catalog.NpcPackages.Add(generic);
            foreach (var path in new[]
            {
                "Assets/SocialSimulation/Domains/Campus/Packages/Classroom/RegionPackage.asset",
                "Assets/SocialSimulation/Domains/Campus/Packages/Office/RegionPackage.asset",
                "Assets/SocialSimulation/Domains/Campus/Packages/Corridor/RegionPackage.asset",
                "Assets/SocialSimulation/Domains/Campus/Packages/Yard/RegionPackage.asset",
                "Assets/SocialSimulation/Domains/Household/Packages/Home/RegionPackage.asset",
                "Assets/SocialSimulation/Domains/WorkAndTrade/Packages/Shop/RegionPackage.asset",
                "Assets/SocialSimulation/Domains/WorkAndTrade/Packages/Workshop/RegionPackage.asset",
                "Assets/SocialSimulation/Domains/WorkAndTrade/Packages/Square/RegionPackage.asset"
            })
            {
                var package = AssetDatabase.LoadAssetAtPath<RegionPackageAsset>(path);
                if (package == null) throw new InvalidOperationException("Region package is missing: " + path);
                if (!catalog.RegionPackages.Contains(package)) catalog.RegionPackages.Add(package);
            }
            foreach (var path in new[]
            {
                "Assets/SocialSimulation/Domains/Campus/CampusDomain.asset",
                "Assets/SocialSimulation/Domains/Household/HouseholdDomain.asset",
                "Assets/SocialSimulation/Domains/WorkAndTrade/WorkAndTradeDomain.asset"
            })
            {
                var package = AssetDatabase.LoadAssetAtPath<SocialDomainPackageAsset>(path);
                if (package == null) throw new InvalidOperationException("Domain package is missing: " + path);
                if (!catalog.DomainPackages.Contains(package)) catalog.DomainPackages.Add(package);
            }
            EditorUtility.SetDirty(catalog);
            var result = catalog.BuildRegistry(new ContentRegistry());
            if (!result.Success)
                throw new InvalidOperationException("Content catalog failed validation: " +
                    string.Join("; ", result.Diagnostics));
            Debug.Log("SocialSimulation: 内容目录已验证：" + catalog.NpcPackages.Count + " NPC 包，" +
                catalog.RegionPackages.Count + " 地区包，" + catalog.DomainPackages.Count + " 领域包。");
        }

        private static Material EnsureMaterial(string name, Color color)
        {
            var path = MaterialFolder + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("找不到 URP Lit 或 Standard Shader。");
            material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else material.color = color;
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static SocialDomainPackageAsset EnsureDomainAsset(string folder, string id, string label,
            string[] roles, string[] relations, string[] activities, string[] actions, string version = "1.0.0")
        {
            var pathFolder = "Assets/SocialSimulation/Domains/" + folder;
            EnsureFolder(pathFolder);
            var path = pathFolder + "/" + folder + "Domain.asset";
            var existing = AssetDatabase.LoadAssetAtPath<SocialDomainPackageAsset>(path);
            if (existing != null)
            {
                if (existing.Version != version)
                {
                    existing.Configure(id, version, label, roles, relations, activities, actions);
                    EditorUtility.SetDirty(existing);
                }
                return existing;
            }
            var asset = ScriptableObject.CreateInstance<SocialDomainPackageAsset>();
            asset.Configure(id, version, label, roles, relations, activities, actions);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static void PrepareCharacterVisuals()
        {
            characterModel = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterModelPath);
            characterPrefab = null;
            characterAvatar = null;
            characterController = null;
            characterSkins = null;
            if (characterModel == null) return;
            foreach (var subAsset in AssetDatabase.LoadAllAssetsAtPath(CharacterModelPath))
                if (subAsset is Avatar avatar && avatar.isValid && avatar.isHuman)
                { characterAvatar = avatar; break; }
            if (characterAvatar == null) { characterModel = null; return; }

            characterController = EnsureCharacterController();
            if (characterController == null) { characterModel = null; return; }
            characterSkins = new[]
            {
                EnsureSkinMaterial("SkaterMale", "skaterMaleA.png"),
                EnsureSkinMaterial("SkaterFemale", "skaterFemaleA.png"),
                EnsureSkinMaterial("CyborgFemale", "cyborgFemaleA.png"),
                EnsureSkinMaterial("CriminalMale", "criminalMaleA.png")
            };
        }

        private static Material EnsureSkinMaterial(string name, string png)
        {
            var folder = CharacterPackageRoot + "/Materials";
            EnsureFolder(folder);
            var path = folder + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader == null) throw new InvalidOperationException("找不到角色材质 Shader。");
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(SkinFolder + png);
            if (texture != null && material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != texture)
            { material.SetTexture("_BaseMap", texture); EditorUtility.SetDirty(material); }
            return material;
        }

        private static void EnsureCharacterPrefab()
        {
            if (characterModel == null || characterController == null) return;
            EnsureFolder(CharacterPackageRoot + "/Prefab");
            var root = new GameObject("Character");
            try
            {
                var marker = root.AddComponent<SocialNpcMarker>();
                marker.Configure("template.npc.charactermedium", "Character", "citizen", Array.Empty<string>());
                marker.BindPackage("npc.charactermedium");
                var agent = root.AddComponent<NavMeshAgent>();
                agent.enabled = false;
                agent.radius = 0.3f;
                agent.height = 2f;
                agent.speed = 1.7f;
                agent.acceleration = 5f;
                agent.angularSpeed = 300f;
                root.AddComponent<SocialNpcNavigation>();
                var visual = PrefabUtility.InstantiatePrefab(characterModel) as GameObject;
                if (visual == null) throw new InvalidOperationException("无法实例化 Kenney Humanoid 模型。");
                visual.name = "Visual";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                var renderers = visual.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    visual.transform.localScale = Vector3.one * (1.75f / Mathf.Max(0.01f, bounds.size.y));
                    bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    visual.transform.position += Vector3.up * (root.transform.position.y - bounds.min.y);
                    foreach (var renderer in renderers)
                        renderer.sharedMaterial = characterSkins[0];
                }
                var animator = visual.GetComponent<Animator>() ?? visual.AddComponent<Animator>();
                animator.avatar = characterAvatar;
                animator.runtimeAnimatorController = characterController;
                animator.applyRootMotion = false;
                marker.SetAnimator(animator);
                if (PrefabUtility.SaveAsPrefabAsset(root, CharacterPrefabPath) == null)
                    throw new InvalidOperationException("无法保存标准 NPC Prefab。");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            characterPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPrefabPath);
            if (characterPrefab == null) throw new InvalidOperationException("标准 NPC Prefab 导入失败。");
        }

        private static AnimatorController EnsureCharacterController()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(AnimatorControllerPath);
            var idle = LoadHumanoidClip(KayKitFolder + "Rig_Medium_General.fbx", "Idle_A") ??
                LoadHumanoidClip(AnimationFolder + "idle.fbx", "Root|Idle");
            var walk = LoadHumanoidClip(KayKitFolder + "Rig_Medium_MovementBasic.fbx", "Walking_A");
            var run = LoadHumanoidClip(KayKitFolder + "Rig_Medium_MovementBasic.fbx", "Running_A") ??
                LoadHumanoidClip(AnimationFolder + "run.fbx", "Root|Run");
            var sitDown = LoadHumanoidClip(KayKitFolder + "Rig_Medium_Simulation.fbx", "Sit_Chair_Down");
            var sit = LoadHumanoidClip(KayKitFolder + "Rig_Medium_Simulation.fbx", "Sit_Chair_Idle");
            var sitUp = LoadHumanoidClip(KayKitFolder + "Rig_Medium_Simulation.fbx", "Sit_Chair_StandUp");
            var talk = LoadHumanoidClip(KayKitFolder + "Rig_Medium_Simulation.fbx", "Waving");
            var work = LoadHumanoidClip(KayKitFolder + "Rig_Medium_Tools.fbx", "Working_A");
            var trade = LoadHumanoidClip(KayKitFolder + "Rig_Medium_General.fbx", "Interact");
            var group = LoadHumanoidClip(KayKitFolder + "Rig_Medium_Simulation.fbx", "Cheering");
            var carry = LoadHumanoidClip(KayKitFolder + "Rig_Medium_Tools.fbx", "Holding_A");
            if (idle == null || walk == null || run == null || sitDown == null || sit == null ||
                sitUp == null || talk == null || work == null || trade == null || group == null || carry == null)
                return null;
            var controller = existing ?? AnimatorController.CreateAnimatorControllerAtPath(AnimatorControllerPath);
            EnsureParameter(controller, "Moving", AnimatorControllerParameterType.Bool);
            EnsureParameter(controller, "Running", AnimatorControllerParameterType.Bool);
            EnsureParameter(controller, "ActionActive", AnimatorControllerParameterType.Bool);
            EnsureParameter(controller, "ActionCue", AnimatorControllerParameterType.Int);
            EnsureParameter(controller, "ActionPhase", AnimatorControllerParameterType.Int);
            var machine = controller.layers[0].stateMachine;
            var idleState = EnsureState(machine, "Idle", idle);
            machine.defaultState = idleState;
            var walkState = EnsureState(machine, "Walk", walk);
            var runState = EnsureState(machine, "Run", run);
            var sitDownState = EnsureState(machine, "SitDown", sitDown);
            var sitState = EnsureState(machine, "Sit", sit);
            var sitUpState = EnsureState(machine, "SitUp", sitUp);
            var talkState = EnsureState(machine, "Talk", talk);
            var workState = EnsureState(machine, "Work", work);
            var tradeState = EnsureState(machine, "Trade", trade);
            var groupState = EnsureState(machine, "Group", group);
            var carryState = EnsureState(machine, "Carry", carry);
            foreach (var state in new[] { idleState, walkState, runState, sitDownState, sitState,
                         sitUpState, talkState, workState, tradeState, groupState, carryState })
                foreach (var transition in state.transitions) state.RemoveTransition(transition);
            foreach (var transition in machine.anyStateTransitions)
                if (transition.destinationState == sitDownState || transition.destinationState == talkState ||
                    transition.destinationState == workState || transition.destinationState == tradeState ||
                    transition.destinationState == groupState || transition.destinationState == carryState)
                    machine.RemoveAnyStateTransition(transition);
            var beginWalk = idleState.AddTransition(walkState);
            beginWalk.hasExitTime = false;
            beginWalk.duration = 0.12f;
            beginWalk.AddCondition(AnimatorConditionMode.If, 0, "Moving");
            beginWalk.AddCondition(AnimatorConditionMode.IfNot, 0, "Running");
            AddTransition(walkState, idleState, "Moving", AnimatorConditionMode.IfNot);
            AddTransition(idleState, runState, "Running", AnimatorConditionMode.If);
            AddTransition(walkState, runState, "Running", AnimatorConditionMode.If);
            AddTransition(runState, walkState, "Running", AnimatorConditionMode.IfNot);
            // Enter the seated chain only from non-seated states. AnyState would restart
            // SitDown every time Sit was reached while the same cue remained active.
            foreach (var state in new[] { idleState, walkState, runState, talkState, workState,
                         tradeState, groupState, carryState })
            {
                var transition = state.AddTransition(sitDownState);
                transition.hasExitTime = false;
                transition.duration = 0.12f;
                transition.AddCondition(AnimatorConditionMode.If, 0, "ActionActive");
                transition.AddCondition(AnimatorConditionMode.Equals, 2, "ActionCue");
            }
            AddCueTransition(machine, talkState, 3);
            AddCueTransition(machine, workState, 4);
            AddCueTransition(machine, tradeState, 5);
            AddCueTransition(machine, groupState, 6);
            AddCueTransition(machine, carryState, 7);
            var intoSit = sitDownState.AddTransition(sitState);
            intoSit.hasExitTime = true;
            intoSit.exitTime = 0.95f;
            intoSit.duration = 0.08f;
            var outOfSit = sitState.AddTransition(sitUpState);
            outOfSit.hasExitTime = false;
            outOfSit.duration = 0.08f;
            outOfSit.AddCondition(AnimatorConditionMode.IfNot, 0, "ActionActive");
            var changedCue = sitState.AddTransition(sitUpState);
            changedCue.hasExitTime = false;
            changedCue.duration = 0.08f;
            changedCue.AddCondition(AnimatorConditionMode.NotEqual, 2, "ActionCue");
            var stand = sitUpState.AddTransition(idleState);
            stand.hasExitTime = true;
            stand.exitTime = 0.95f;
            stand.duration = 0.08f;
            foreach (var state in new[] { talkState, workState, tradeState, groupState, carryState })
                AddTransition(state, idleState, "ActionActive", AnimatorConditionMode.IfNot);
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static AnimatorState EnsureState(AnimatorStateMachine machine, string name, AnimationClip clip)
        {
            clip = EnsurePlaybackClip(clip, name != "SitDown" && name != "SitUp");
            foreach (var child in machine.states)
                if (child.state.name == name)
                { child.state.motion = clip; return child.state; }
            var state = machine.AddState(name);
            state.motion = clip;
            return state;
        }

        private static AnimationClip EnsurePlaybackClip(AnimationClip source, bool loop)
        {
            var path = AnimationFolder + source.name.Replace('|', '_') + ".anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                clip = UnityEngine.Object.Instantiate(source);
                clip.name = source.name;
                AssetDatabase.CreateAsset(clip, path);
            }
            else EditorUtility.CopySerialized(source, clip);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            return clip;
        }

        private static void EnsureParameter(AnimatorController controller, string name,
            AnimatorControllerParameterType type)
        {
            foreach (var parameter in controller.parameters)
                if (parameter.name == name)
                {
                    if (parameter.type != type)
                        throw new InvalidOperationException("Animator 参数类型不匹配：" + name);
                    return;
                }
            controller.AddParameter(name, type);
        }

        private static void AddTransition(AnimatorState from, AnimatorState to, string parameter,
            AnimatorConditionMode mode)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.duration = 0.12f;
            transition.AddCondition(mode, 0, parameter);
        }

        private static void AddCueTransition(AnimatorStateMachine machine, AnimatorState state, int cue)
        {
            var transition = machine.AddAnyStateTransition(state);
            transition.hasExitTime = false;
            transition.canTransitionToSelf = false;
            transition.duration = 0.12f;
            transition.AddCondition(AnimatorConditionMode.If, 0, "ActionActive");
            transition.AddCondition(AnimatorConditionMode.Equals, cue, "ActionCue");
        }

        private static void BindNpcAnimationPackage()
        {
            if (characterController == null) return;
            var npc = AssetDatabase.LoadAssetAtPath<NpcPackageAsset>(CharacterPackagePath);
            if (npc == null) return;
            npc.Configure("npc.charactermedium", "1.2.0", characterPrefab, characterAvatar);
            npc.DeclareCapability("locomotion.walk", "navmesh.move", true);
            npc.DeclareCapability("locomotion.run", "navmesh.move", true);
            npc.DeclareCapability("posture.sit", "animator.present", true);
            npc.DeclareCapability("communication.speak", "animator.present", true);
            npc.DeclareCapability("work.perform", "animator.present", true);
            npc.DeclareCapability("trade.exchange", "animator.present", true);
            npc.DeclareCapability("object.carry", "animator.present", true);
            npc.DeclareCapability("activity.group", "animator.present", true);
            npc.BindPresentation(characterController, "locomotion.walk", "Walk", true);
            npc.BindPresentation(characterController, "locomotion.run", "Run", true);
            npc.BindPresentation(characterController, "posture.sit", "Sit", true);
            npc.BindPresentation(characterController, "communication.speak", "Talk", true);
            npc.BindPresentation(characterController, "work.perform", "Work", true);
            npc.BindPresentation(characterController, "trade.exchange", "Trade", false);
            npc.BindPresentation(characterController, "object.carry", "Carry", true);
            npc.BindPresentation(characterController, "activity.group", "Group", true);
            EditorUtility.SetDirty(npc);
        }

        private static AnimationClip LoadHumanoidClip(string path, string clipName)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is AnimationClip clip && clip.name == clipName && clip.humanMotion)
                    return clip;
            return null;
        }

        private static GraphDocument EnsureDomainGraph(string domain, string name, string id, params string[] actions)
        {
            var folder = "Assets/SocialSimulation/Domains/" + domain + "/Graphs";
            var path = folder + "/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<GraphDocument>(path);
            if (existing != null) return existing;
            EnsureFolder(folder);
            var graph = DomainGraphRunner.CreateDefault(id, actions);
            graph.name = name;
            RequireCompiledGraph(graph);
            AssetDatabase.CreateAsset(graph, path);
            return graph;
        }

        private static GraphDocument EnsureCourseBehaviorGraph()
        {
            const string path = "Assets/SocialSimulation/Domains/Combined/Graphs/AttendCourse.asset";
            var existing = AssetDatabase.LoadAssetAtPath<GraphDocument>(path);
            if (existing != null) return existing;
            EnsureFolder("Assets/SocialSimulation/Domains/Combined/Graphs");
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.name = "Attend Course";
            document.Kind = GraphKind.Behavior;
            document.DocumentId = "sample.behavior.attend_course";
            var root = AddNode(document, GraphNodeType.Root, 20, 160);
            var sequence = AddNode(document, GraphNodeType.Sequence, 260, 160);
            AddEdge(document, root, sequence, 0);
            var actions = new[] { "course.reserve_seat", "course.move_to_seat", "course.sit",
                "course.attend", "course.release_seat" };
            for (var i = 0; i < actions.Length; i++)
            {
                var action = AddNode(document, GraphNodeType.Action, 520 + i * 230, 160);
                action.Set("actionId", actions[i]);
                AddEdge(document, sequence, action, i);
            }
            RequireCompiledGraph(document);
            AssetDatabase.CreateAsset(document, path);
            return document;
        }

        private static GraphDocument EnsureCorridorRuleGraph()
        {
            const string path = "Assets/SocialSimulation/Domains/Combined/Graphs/CorridorNoRunning.asset";
            var existing = AssetDatabase.LoadAssetAtPath<GraphDocument>(path);
            if (existing != null) return existing;
            EnsureFolder("Assets/SocialSimulation/Domains/Combined/Graphs");
            var document = ScriptableObject.CreateInstance<GraphDocument>();
            document.name = "Corridor No Running";
            document.Kind = GraphKind.Rule;
            document.DocumentId = "sample.rule.corridor_no_running";
            var eventNode = AddNode(document, GraphNodeType.RuleEvent, 20, 150);
            eventNode.Set("eventType", "action.observed");
            var scope = AddNode(document, GraphNodeType.RuleScope, 280, 150);
            scope.Set("areaId", "campus.corridor");
            scope.Set("roleId", "campus.student");
            scope.Set("actionId", "locomotion.run");
            var condition = AddNode(document, GraphNodeType.RuleCondition, 550, 80);
            condition.Set("requiresEvidence", "true");
            var effect = AddNode(document, GraphNodeType.RuleEffect, 820, 80);
            effect.Set("commandId", "violation.warn");
            effect.Set("target", "actor");
            var norm = AddNode(document, GraphNodeType.RuleNorm, 550, 280);
            norm.Set("normKind", "prohibition");
            norm.Set("actionId", "locomotion.run");
            norm.Set("priority", "10");
            AddEdge(document, eventNode, scope, 0);
            AddEdge(document, scope, condition, 0);
            AddEdge(document, condition, effect, 0);
            AddEdge(document, scope, norm, 1);
            RequireCompiledGraph(document);
            AssetDatabase.CreateAsset(document, path);
            return document;
        }

        private static GraphNode AddNode(GraphDocument document, GraphNodeType type, float x, float y)
        {
            var node = new GraphNode { Id = Guid.NewGuid().ToString("N"), Type = type, Position = new Vector2(x, y) };
            document.Nodes.Add(node);
            return node;
        }

        private static void AddEdge(GraphDocument document, GraphNode from, GraphNode to, int order)
        {
            document.Edges.Add(new GraphEdge { FromNodeId = from.Id, ToNodeId = to.Id, Order = order });
        }

        private static void RequireCompiledGraph(GraphDocument document)
        {
            var result = GraphCompiler.Compile(document);
            if (result.Success) return;
            var messages = new List<string>();
            foreach (var diagnostic in result.Diagnostics) messages.Add(diagnostic.ToString());
            throw new InvalidOperationException("Demo graph is invalid: " + string.Join("; ", messages));
        }

        private static void CreateCamera(Transform root)
        {
            var go = new GameObject("Demo Camera");
            go.transform.SetParent(root, false);
            go.transform.position = new Vector3(0f, 38f, -31f);
            go.transform.rotation = Quaternion.LookRotation(new Vector3(0f, -30f, 30f));
            go.tag = "MainCamera";
            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = 53f;
            camera.clearFlags = CameraClearFlags.Skybox;
            go.AddComponent<AudioListener>();
        }

        private static void CreateSun(Transform root)
        {
            var go = new GameObject("Sun");
            go.transform.SetParent(root, false);
            go.transform.rotation = Quaternion.Euler(48f, -25f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.4f;
        }

        private static void CreateWalkableGround(Transform root, Material material)
        {
            var navRoot = new GameObject("Navigation Ground");
            navRoot.transform.SetParent(root, false);
            var surface = navRoot.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            var floor = CreateCube(navRoot.transform, "Walkable Ground", new Vector3(0f, -0.23f, -3f), new Vector3(50f, 0.3f, 49f), material);
            floor.isStatic = true;
        }

        private static Transform CreateRegion(Transform root, string instanceId, string definitionId, string label, string domain, Vector3 position, Vector2 size, Material material)
        {
            var go = new GameObject(label + " [" + instanceId + "]");
            go.transform.SetParent(root, false);
            go.transform.position = position;
            var marker = go.AddComponent<SocialWorldMarker>();
            marker.Configure(instanceId, definitionId, label, domain);
            marker.ConfigureBounds(new Bounds(new Vector3(0, 2, 0), new Vector3(size.x, 6, size.y)));
            marker.ConfigureOwner(domain == "campus" ? "campus.management" :
                definitionId == "household.home" ? "household.river" :
                definitionId == "trade.shop" ? "trade.corner_shop" :
                definitionId == "trade.workshop" ? "trade.workshop" : null);
            var entrances = new GameObject("Entrances");
            entrances.transform.SetParent(go.transform, false);
            var entry = new GameObject("main");
            entry.transform.SetParent(entrances.transform, false);
            entry.transform.localPosition = new Vector3(0, 0, -size.y / 2 + 0.4f);
            entry.AddComponent<SocialRegionEntrance>().Configure("main");
            var visuals = new GameObject("Visuals");
            visuals.transform.SetParent(go.transform, false);
            var zones = new GameObject("Zones");
            zones.transform.SetParent(go.transform, false);
            var floor = CreateCube(visuals.transform, "Region Floor", new Vector3(0f, -0.055f, 0f), new Vector3(size.x, 0.08f, size.y), material);
            UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());
            CreateLabel(visuals.transform, label, new Vector3(0f, 0.1f, size.y * 0.5f - 0.9f), 0.35f);
            return go.transform;
        }

        private static void ConnectRegionRing(Transform root, params string[] regions)
        {
            var markers = new Dictionary<string, SocialWorldMarker>(StringComparer.Ordinal);
            foreach (var marker in root.GetComponentsInChildren<SocialWorldMarker>(true)) markers.Add(marker.RegionInstanceId, marker);
            for (var i = 0; i < regions.Length; i++)
            {
                var entrance = markers[regions[i]].GetComponentInChildren<SocialRegionEntrance>();
                entrance.Configure("main", regions[(i + 1) % regions.Length], "main");
                PrefabUtility.RecordPrefabInstancePropertyModifications(entrance);
                EditorUtility.SetDirty(entrance);
            }
        }

        private static Transform CreateZone(Transform region, string localId, string tag, int capacity)
        {
            var zones = region.Find("Zones");
            if (zones == null) throw new InvalidOperationException("Region is missing Zones: " + region.name);
            var zone = new GameObject(localId);
            zone.transform.SetParent(zones, false);
            zone.AddComponent<SocialRegionZone>().Configure(localId, tag, capacity);
            var points = new GameObject("InteractionPoints");
            points.transform.SetParent(zone.transform, false);
            return points.transform;
        }

        private static void FillClassroom(Transform region, int number, Material furniture)
        {
            var points = CreateZone(region, "learning", "campus.learning", 32);
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 4; column++)
            {
                var index = row * 4 + column + 1;
                var p = new Vector3(-3.25f + column * 2.1f, 0f, -2.8f + row * 1.8f);
                CreatePoint(points, "seat." + index.ToString("00"), "posture.sit", p, furniture);
            }
            CreatePoint(points, "teacher.podium", "communication.speak", new Vector3(0f, 0f, 2.5f), furniture);
            CreatePoint(points, "observation", "perception.observe", new Vector3(4.2f, 0f, 3.1f), furniture);
        }

        private static void FillOffice(Transform region, Material furniture)
        {
            var points = CreateZone(region, "staff", "campus.staff", 10);
            for (var i = 0; i < 4; i++)
                CreatePoint(points, "desk." + (i + 1).ToString("00"), "work.prepare", new Vector3(-3f + i * 2f, 0f, -1.5f), furniture);
            CreatePoint(points, "briefing", "communication.speak", new Vector3(0f, 0f, 2f), furniture);
        }

        private static void FillCorridor(Transform region)
        {
            var points = CreateZone(region, "passage", "campus.passage", 64);
            var positions = new[] { new Vector3(-14f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(14f, 0f, 0f) };
            for (var i = 0; i < positions.Length; i++)
                CreateAnchor(points, "checkpoint." + (i + 1), "locomotion.walk", positions[i], 2);
        }

        private static void FillYard(Transform region)
        {
            var points = CreateZone(region, "activity", "community.activity", 40);
            for (var i = 0; i < 4; i++)
            {
                var a = i * Mathf.PI * 0.5f;
                CreateAnchor(points, "run." + (i + 1), "locomotion.run", new Vector3(Mathf.Cos(a) * 4f, 0f, Mathf.Sin(a) * 3f), 2);
            }
            CreateAnchor(points, "chat", "communication.speak", new Vector3(0f, 0f, 0f), 4);
        }

        private static void FillHome(Transform region, Material furniture)
        {
            var dining = CreateZone(region, "dining", "household.dining", 8);
            for (var i = 0; i < 4; i++)
                CreatePoint(dining, "table.seat." + (i + 1), "posture.sit", new Vector3(-2.7f + i * 1.8f, 0f, -1.5f), furniture);
            var living = CreateZone(region, "living", "household.living", 8);
            CreateAnchor(living, "family.talk", "communication.speak", new Vector3(0f, 0f, 1.5f), 4);
        }

        private static void FillShop(Transform region, Material furniture)
        {
            var service = CreateZone(region, "service", "trade.service", 8);
            CreatePoint(service, "counter", "trade.exchange", new Vector3(0f, 0f, 1.1f), furniture);
            CreateAnchor(service, "queue", "activity.wait", new Vector3(0f, 0f, -1.2f), 3);
            var stock = CreateZone(region, "stock", "trade.stock", 8);
            CreatePoint(stock, "shelf.01", "object.pickup", new Vector3(-3.5f, 0f, -2f), furniture);
            CreatePoint(stock, "shelf.02", "object.pickup", new Vector3(3.5f, 0f, -2f), furniture);
        }

        private static void FillWorkplace(Transform region, Material furniture)
        {
            var production = CreateZone(region, "production", "trade.production", 8);
            CreatePoint(production, "workstation.01", "work.perform", new Vector3(-2f, 0f, 0f), furniture);
            CreatePoint(production, "workstation.02", "work.perform", new Vector3(2f, 0f, 0f), furniture);
            var meeting = CreateZone(region, "meeting", "trade.meeting", 8);
            CreateAnchor(meeting, "meeting", "communication.speak", new Vector3(0f, 0f, 2f), 4);
        }

        private static void CreatePoint(Transform parent, string localId, string actionId, Vector3 localPosition, Material material)
        {
            var go = CreateCube(parent, localId, localPosition + Vector3.up * 0.3f, new Vector3(0.65f, 0.6f, 0.65f), material);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            var anchor = new GameObject("Approach Anchor");
            anchor.transform.SetParent(go.transform, false);
            anchor.transform.localPosition = Vector3.down * 0.5f;
            go.AddComponent<SocialInteractionPoint>().Configure(localId, actionId, 1, anchor.transform);
        }

        private static void CreateAnchor(Transform parent, string localId, string actionId, Vector3 localPosition, int capacity)
        {
            var go = new GameObject(localId);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.AddComponent<SocialInteractionPoint>().Configure(localId, actionId, capacity);
        }

        private static GameObject CreateCube(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        private static void CreateLabel(Transform parent, string caption, Vector3 localPosition, float size)
        {
            var go = new GameObject("Label: " + caption);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(55f, 0f, 0f);
            var text = go.AddComponent<TextMesh>();
            text.text = caption;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 48;
            text.characterSize = size / 48f;
            text.color = Color.black;
        }

        private static void CreatePopulation(Transform root, Material student, Material teacher, Material other)
        {
            var population = new GameObject("Population");
            population.transform.SetParent(root, false);
            for (var i = 0; i < 20; i++)
            {
                var id = "npc.student." + (i + 1).ToString("00");
                var memberships = i < 2
                    ? new[] { "campus.class.01", "household.river" }
                    : new[] { "campus.class." + (i < 10 ? "01" : "02") };
                CreateNpc(population.transform, id, "学生 " + (i + 1).ToString("00"), "campus.student", memberships,
                    i < 2 ? new Vector3(-19f + i * 1.5f, 0f, -13f) :
                    new Vector3(-19f + (i % 10) * 1.5f, 0f, -0.1f + (i / 10) * 1.4f), student);
            }
            CreateNpc(population.transform, "npc.teacher.01", "教师 01 / 家长", "campus.teacher",
                new[] { "campus.faculty", "household.river" }, new Vector3(-17f, 0f, -13f), teacher);
            CreateNpc(population.transform, "npc.teacher.02", "教师 02", "campus.teacher",
                new[] { "campus.faculty" }, new Vector3(5f, 0f, 10f), teacher);
            CreateNpc(population.transform, "npc.leader.01", "教务负责人", "campus.leader",
                new[] { "campus.management" }, new Vector3(7f, 0f, 10f), teacher);
            CreateNpc(population.transform, "npc.clerk.01", "店员 / 家长", "trade.clerk",
                new[] { "trade.corner_shop", "household.river", "community.walking_club" },
                new Vector3(-16f, 0f, -13f), other);
            CreateNpc(population.transform, "npc.clerk.02", "店员 02", "trade.clerk",
                new[] { "trade.corner_shop" }, new Vector3(-5f, 0f, -14f), other);
            CreateNpc(population.transform, "npc.worker.01", "工人 01", "trade.worker",
                new[] { "trade.workshop", "community.walking_club" }, new Vector3(4f, 0f, -18f), other);
            CreateNpc(population.transform, "npc.worker.02", "工人 02", "trade.worker",
                new[] { "trade.workshop" }, new Vector3(7f, 0f, -18f), other);
        }

        private static void CreateCollectives(Transform root)
        {
            var container = new GameObject("Collectives");
            container.transform.SetParent(root, false);
            AddCollective(container.transform, "campus.class.01", "班级 01", true, false);
            AddCollective(container.transform, "campus.class.02", "班级 02", true, false);
            AddCollective(container.transform, "campus.faculty", "教职员工", true, false);
            AddCollective(container.transform, "campus.management", "校园管理", true, true);
            AddCollective(container.transform, "household.river", "河畔家庭", false, false);
            AddCollective(container.transform, "trade.corner_shop", "社区商店", true, true);
            AddCollective(container.transform, "trade.workshop", "工作单位", true, true);
            AddCollective(container.transform, "community.walking_club", "步行社团", false, false);
        }

        private static void AddCollective(Transform parent, string id, string label,
            bool formal, bool organization)
        {
            var item = new GameObject(id);
            item.transform.SetParent(parent, false);
            item.AddComponent<SocialCollectiveMarker>().Configure(id, label, formal, organization);
        }

        private static void CreateNpc(Transform parent, string id, string label, string role, string[] memberships, Vector3 position, Material material)
        {
            var generic = id == "npc.worker.01" || id == "npc.worker.02";
            var prefab = generic ? AssetDatabase.LoadAssetAtPath<NpcPackageAsset>(GenericPackagePath)?.CharacterPrefab : characterPrefab;
            var go = prefab != null ? PrefabUtility.InstantiatePrefab(prefab) as GameObject :
                new GameObject("Placeholder Character");
            if (go == null) throw new InvalidOperationException("无法实例化标准 NPC Prefab。");
            go.name = label + " [" + id + "]";
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var marker = go.GetComponent<SocialNpcMarker>() ?? go.AddComponent<SocialNpcMarker>();
            marker.Configure(id, label, role, memberships);
            if (Array.IndexOf(memberships, "household.river") >= 0)
                marker.BindMembershipRole("household.river",
                    role == "campus.student" ? "household.child" : "household.parent");
            var agent = go.GetComponent<NavMeshAgent>() ?? go.AddComponent<NavMeshAgent>();
            agent.enabled = false;
            agent.radius = 0.3f;
            agent.height = 2f;
            agent.speed = 1.7f;
            agent.acceleration = 5f;
            agent.angularSpeed = 300f;
            if (go.GetComponent<SocialNpcNavigation>() == null) go.AddComponent<SocialNpcNavigation>();
            if (characterPrefab != null)
            {
                var visual = go.transform.Find("Visual");
                if (visual == null) throw new InvalidOperationException("标准 NPC Prefab 缺少 Visual。");
                var renderers = visual.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0 && !generic)
                {
                    var index = 0;
                    foreach (var letter in id) index = (index * 31 + letter) & 0x7fffffff;
                    index %= characterSkins.Length;
                    foreach (var renderer in renderers) renderer.sharedMaterial = characterSkins[index] ?? material;
                }
                if (marker.Animator == null || marker.Animator.applyRootMotion)
                    throw new InvalidOperationException("标准 NPC Prefab 的 Animator 引用或 Root Motion 无效。");
            }
            else
            {
                var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visual.name = "Placeholder Body";
                visual.transform.SetParent(go.transform, false);
                visual.transform.localPosition = Vector3.up;
                visual.transform.localScale = new Vector3(0.55f, 1f, 0.55f);
                visual.GetComponent<Renderer>().sharedMaterial = material;
                UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());
            }
            CreateLabel(go.transform, label, new Vector3(0f, 2.35f, 0f), 0.48f);
        }
    }
}
