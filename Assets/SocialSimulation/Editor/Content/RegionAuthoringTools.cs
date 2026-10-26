using System;
using System.Collections.Generic;
using SocialSimulation.Authoring;
using SocialSimulation.Core;
using SocialSimulation.UnityRuntime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocialSimulation.Editor.Content
{
    public static class RegionAuthoringTools
    {
        [MenuItem("Tools/Social Simulation/Create Standard Region Package From Selection")]
        private static void CreateStandardPackage()
        {
            var source = Selection.activeGameObject;
            if (source == null || source.GetComponent<SocialWorldMarker>() == null)
            { EditorUtility.DisplayDialog("地区包", "请选择根节点带 SocialWorldMarker 的地区对象。", "确定"); return; }
            var zonesRoot = source.transform.Find("Zones");
            if (zonesRoot == null)
            { EditorUtility.DisplayDialog("地区包", "地区根节点直下需要 Zones 容器。", "确定"); return; }
            const string parent = "Assets/SocialSimulation/RegionPackages";
            var folder = string.Empty;
            GameObject temporary = null;
            try
            {
                if (!AssetDatabase.IsValidFolder(parent))
                    AssetDatabase.CreateFolder("Assets/SocialSimulation", "RegionPackages");
                var stem = new System.Text.StringBuilder();
                foreach (var character in source.name.ToLowerInvariant())
                    stem.Append((character >= 'a' && character <= 'z') ||
                        (character >= '0' && character <= '9') ? character : '_');
                var baseName = stem.Length == 0 ? "region" : stem.ToString().Trim('_');
                if (baseName.Length == 0) baseName = "region";
                var folderName = baseName;
                for (var suffix = 2; AssetDatabase.IsValidFolder(parent + "/" + folderName); suffix++)
                    folderName = baseName + "_" + suffix;
                AssetDatabase.CreateFolder(parent, folderName);
                folder = parent + "/" + folderName;
                AssetDatabase.CreateFolder(folder, "Prefab");
                var definitionId = "region." + folderName;
                var packageId = "package.region." + folderName;
                temporary = UnityEngine.Object.Instantiate(source);
                temporary.name = "Region";
                temporary.GetComponent<SocialWorldMarker>().Configure(
                    "template." + definitionId, definitionId, source.name,
                    source.GetComponent<SocialWorldMarker>().DomainId);
                temporary.GetComponent<SocialWorldMarker>().BindPackage(packageId);
                var region = new RegionDefinition { Id = definitionId, Tag = definitionId };
                var boundary = temporary.GetComponent<SocialWorldMarker>();
                region.Bounds = new RegionBounds { Enabled = boundary.HasBoundary,
                    CenterX = boundary.LocalBounds.center.x, CenterY = boundary.LocalBounds.center.y, CenterZ = boundary.LocalBounds.center.z,
                    SizeX = boundary.LocalBounds.size.x, SizeY = boundary.LocalBounds.size.y, SizeZ = boundary.LocalBounds.size.z };
                boundary.ConfigureOwner(null);
                foreach (var entrance in temporary.GetComponentsInChildren<SocialRegionEntrance>(true))
                {
                    region.EntranceLocalIds.Add(entrance.LocalId);
                    entrance.Configure(entrance.LocalId);
                }
                var copiedZones = temporary.transform.Find("Zones");
                for (int i = 0; i < copiedZones.childCount; i++)
                {
                    var zoneObject = copiedZones.GetChild(i);
                    var zone = zoneObject.GetComponent<SocialRegionZone>();
                    if (zone == null) throw new InvalidOperationException("Zones 子节点缺少 SocialRegionZone。" + zoneObject.name);
                    region.Zones.Add(new RegionZoneDefinition
                    { LocalId = zone.LocalId, Tag = zone.Tag, Capacity = zone.Capacity });
                    region.Capacity += zone.Capacity;
                    var pointsRoot = zoneObject.Find("InteractionPoints");
                    if (pointsRoot == null) throw new InvalidOperationException("功能区缺少 InteractionPoints。" + zoneObject.name);
                    for (int j = 0; j < pointsRoot.childCount; j++)
                    {
                        var point = pointsRoot.GetChild(j).GetComponent<SocialInteractionPoint>();
                        if (point == null) throw new InvalidOperationException("交互点对象缺少 SocialInteractionPoint。" + pointsRoot.GetChild(j).name);
                        region.Points.Add(new InteractionPointDefinition
                        { LocalId = point.LocalId, ZoneLocalId = zone.LocalId,
                            Kind = point.ActionId, Capacity = point.Capacity });
                    }
                }
                var prefab = PrefabUtility.SaveAsPrefabAsset(temporary, folder + "/Prefab/Region.prefab");
                if (prefab == null) throw new InvalidOperationException("地区 Prefab 保存失败。");
                var package = ScriptableObject.CreateInstance<RegionPackageAsset>();
                package.Configure(packageId, "1.0.0", prefab);
                package.SetRegions(new[] { region });
                var errors = package.Validate();
                if (errors.Count > 0)
                    throw new InvalidOperationException(string.Join("; ", errors));
                AssetDatabase.CreateAsset(package, folder + "/RegionPackage.asset");
                errors = package.Validate();
                errors.AddRange(StandardPackageLayout.Validate(package));
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
                AssetDatabase.SaveAssets();
                Selection.activeObject = package;
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.DeleteAsset(folder);
                EditorUtility.DisplayDialog("地区包创建失败", exception.Message, "确定");
            }
            finally { if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary); }
        }

        [MenuItem("Tools/Social Simulation/Instantiate Selected Region Package")]
        private static void InstantiateSelected()
        {
            var package = Selection.activeObject as RegionPackageAsset;
            if (package == null || package.RegionPrefab == null)
            { EditorUtility.DisplayDialog("地区包", "请先选中已配置 Prefab 的地区包资产。", "确定"); return; }
            var diagnostics = package.Validate();
            diagnostics.AddRange(StandardPackageLayout.Validate(package));
            if (diagnostics.Count != 0)
            {
                EditorUtility.DisplayDialog("地区包校验失败", string.Join("\n", diagnostics), "确定");
                return;
            }
            var root = PrefabUtility.InstantiatePrefab(package.RegionPrefab) as GameObject;
            if (root == null) throw new InvalidOperationException("Could not instantiate region prefab.");
            Undo.RegisterCreatedObjectUndo(root, "Instantiate region package");
            var used = new HashSet<string>(StringComparer.Ordinal);
            var markers = root.GetComponentsInChildren<SocialWorldMarker>(true);
            for (int i = 0; i < markers.Length; i++)
            {
                var marker = markers[i];
                var local = string.IsNullOrWhiteSpace(marker.RegionDefinitionId) ? "region." + i : marker.RegionDefinitionId;
                var stableId = package.PackageId + "/" + Guid.NewGuid().ToString("N") + "/" + local;
                if (!used.Add(stableId)) throw new InvalidOperationException("Duplicate instance ID generated.");
                Undo.RecordObject(marker, "Assign region instance ID");
                marker.Configure(stableId, local, marker.DisplayName, marker.DomainId);
                marker.BindPackage(package.PackageId);
                PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
                EditorUtility.SetDirty(marker);
            }
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(root.scene);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.Active)]
        private static void DrawRegion(SocialWorldMarker marker, GizmoType type)
        {
            if (marker == null) return;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.65f);
            var previousMatrix = Gizmos.matrix;
            Gizmos.matrix = marker.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(marker.HasBoundary ? marker.LocalBounds.center : Vector3.zero,
                marker.HasBoundary ? marker.LocalBounds.size : new Vector3(4f, 0.2f, 4f));
            Gizmos.matrix = previousMatrix;
            Handles.color = Color.cyan;
            Handles.Label(marker.transform.position + Vector3.up * 0.5f,
                marker.DisplayName + " [" + marker.RegionInstanceId + "]");
            foreach (var entrance in marker.GetComponentsInChildren<SocialRegionEntrance>(true))
            {
                Handles.Label(entrance.transform.position + Vector3.up * 0.5f,
                    "入口 " + entrance.LocalId + " → " + entrance.TargetRegionInstanceId + "/" + entrance.TargetEntranceLocalId);
                Gizmos.DrawWireSphere(entrance.transform.position, 0.35f);
            }
            foreach (var point in marker.GetComponentsInChildren<SocialInteractionPoint>(true))
            {
                Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.85f);
                Gizmos.DrawWireSphere(point.AnchorPosition, 0.25f);
                Handles.Label(point.AnchorPosition + Vector3.up * 0.3f,
                    point.LocalId + " ×" + point.Capacity);
            }
        }
    }
}
