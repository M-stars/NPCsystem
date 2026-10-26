using System;
using System.Collections.Generic;
using System.IO;
using SocialSimulation.Authoring;
using SocialSimulation.Core;
using UnityEditor;
using UnityEditor.Animations;

namespace SocialSimulation.Editor.Content
{
    /// <summary>Checks the on-disk package contract in addition to runtime manifest validation.</summary>
    public static class StandardPackageLayout
    {
        public static List<Diagnostic> Validate(NpcPackageAsset package)
        {
            var result = new List<Diagnostic>();
            if (package == null) return result;
            var root = RootOf(package);
            if (Path.GetFileName(AssetDatabase.GetAssetPath(package)) != "NpcPackage.asset")
                result.Add(new Diagnostic("NPC_PACKAGE_LAYOUT", package.name,
                    "NPC 包资产应位于包根，文件名为 NpcPackage.asset。"));
            RequireFolder(result, root, "Prefab");
            RequireFolder(result, root, "Model");
            RequireFolder(result, root, "Rig");
            RequireFolder(result, root, "Animations");
            RequireFolder(result, root, "Materials");
            RequireFolder(result, root, "Textures");
            RequireAssetIn(result, AssetDatabase.GetAssetPath(package.CharacterPrefab), root + "/Prefab/",
                ".prefab", "NPC_PREFAB_LAYOUT", package.name);
            var avatarPath = AssetDatabase.GetAssetPath(package.RigAvatar) ?? string.Empty;
            var inModel = avatarPath.StartsWith(root + "/Model/", StringComparison.Ordinal);
            var inRig = avatarPath.StartsWith(root + "/Rig/", StringComparison.Ordinal);
            var extension = Path.GetExtension(avatarPath);
            if ((!inModel && !inRig) ||
                !string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase))
                result.Add(new Diagnostic("NPC_RIG_LAYOUT", package.name,
                    "Avatar 必须是 Model/ 或 Rig/ 内的 FBX 子资源或 .asset 骨骼资源。"));
            if (package.AnimatorController != null)
                RequireAssetIn(result, AssetDatabase.GetAssetPath(package.AnimatorController),
                    root + "/Animations/", ".controller", "NPC_CONTROLLER_LAYOUT", package.name);
            if (package.AnimatorController is AnimatorController controller)
                foreach (var binding in package.AnimationBindings ?? Array.Empty<AnimationBindingProfile>())
                    if (binding != null && !HasPlayableState(controller, binding.AnimatorState))
                        result.Add(new Diagnostic("NPC_ANIMATION_STATE", binding.CapabilityId,
                            "Animator Controller 缺少有动作资源的状态 " + binding.AnimatorState));
            return result;
        }

        public static List<Diagnostic> Validate(RegionPackageAsset package)
        {
            var result = new List<Diagnostic>();
            if (package == null) return result;
            var root = RootOf(package);
            if (Path.GetFileName(AssetDatabase.GetAssetPath(package)) != "RegionPackage.asset")
                result.Add(new Diagnostic("REGION_PACKAGE_LAYOUT", package.name,
                    "地区包资产应位于包根，文件名为 RegionPackage.asset。"));
            RequireFolder(result, root, "Prefab");
            RequireAssetIn(result, AssetDatabase.GetAssetPath(package.RegionPrefab), root + "/Prefab/",
                ".prefab", "REGION_PREFAB_LAYOUT", package.name);
            return result;
        }

        private static string RootOf(UnityEngine.Object asset)
        { return (Path.GetDirectoryName(AssetDatabase.GetAssetPath(asset)) ?? string.Empty).Replace('\\', '/'); }

        private static void RequireFolder(List<Diagnostic> diagnostics, string root, string folder)
        {
            if (!AssetDatabase.IsValidFolder(root + "/" + folder))
                diagnostics.Add(new Diagnostic("PACKAGE_FOLDER", root,
                    "缺少标准子目录 " + folder + "。"));
        }

        private static void RequireAssetIn(List<Diagnostic> diagnostics, string path,
            string prefix, string extension, string code, string packageName)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(prefix, StringComparison.Ordinal) ||
                !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic(code, packageName,
                    "资源必须位于 " + prefix + " 且扩展名为 " + extension + "。"));
        }

        private static bool HasPlayableState(AnimatorController controller, string stateName)
        {
            if (string.IsNullOrWhiteSpace(stateName)) return false;
            foreach (var layer in controller.layers)
                if (HasPlayableState(layer.stateMachine, stateName)) return true;
            return false;
        }

        private static bool HasPlayableState(AnimatorStateMachine machine, string stateName)
        {
            foreach (var state in machine.states)
                if (state.state != null && state.state.name == stateName && state.state.motion != null) return true;
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null && HasPlayableState(child.stateMachine, stateName)) return true;
            return false;
        }
    }
}
