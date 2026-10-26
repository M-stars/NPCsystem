using System;
using System.Collections.Generic;
using SocialSimulation.Core;
using SocialSimulation.Domains;
using SocialSimulation.UnityRuntime;
using UnityEngine;
using UnityEngine.AI;

namespace SocialSimulation.Authoring
{
    [CreateAssetMenu(fileName = "NpcPackage", menuName = "Social Simulation/NPC Package")]
    public sealed class NpcPackageAsset : ScriptableObject
    {
        [SerializeField] private string packageId;
        [SerializeField] private string version = "1.0.0";
        [SerializeField, Tooltip("仅要求包存在，不限制版本。不要与下方版本依赖重复声明同一包。")]
        private List<string> dependencies = new List<string>();
        [SerializeField, Tooltip("支持 *、精确版本 1.2.3、区间 [1.2,2) 或 [1.2,)。配置后使用包格式 v2。")]
        private List<PackageDependencyRequirement> dependencyRequirements = new List<PackageDependencyRequirement>();
        [SerializeField] private GameObject characterPrefab;
        [SerializeField] private Avatar humanoidAvatar;
        [SerializeField] private RuntimeAnimatorController animatorController;
        [SerializeField] private BodyProfile body = new BodyProfile();
        [SerializeField] private List<CapabilityDefinition> capabilities = new List<CapabilityDefinition>();
        [SerializeField] private List<AnimationBindingProfile> animationBindings = new List<AnimationBindingProfile>();
        [SerializeField] private List<ActionDefinition> actions = new List<ActionDefinition>();

        public string PackageId => packageId;
        public string Version => version;
        public GameObject CharacterPrefab => characterPrefab;
        /// <summary>The rig Avatar, for either Humanoid or Generic animation.</summary>
        public Avatar RigAvatar => humanoidAvatar;
        // Kept for existing content and editor integrations.
        public Avatar HumanoidAvatar => humanoidAvatar;
        public RuntimeAnimatorController AnimatorController => animatorController;
        public BodyProfile Body => body;
        public IReadOnlyList<CapabilityDefinition> Capabilities => capabilities;
        public IReadOnlyList<AnimationBindingProfile> AnimationBindings => animationBindings;

        public void ConfigureDependencies(IEnumerable<string> packageIds,
            IEnumerable<PackageDependencyRequirement> requirements = null)
        {
            var copiedIds = packageIds == null ? new List<string>() : new List<string>(packageIds);
            var copiedRequirements = new List<PackageDependencyRequirement>();
            if (requirements != null)
                foreach (var requirement in requirements) copiedRequirements.Add(requirement?.Copy());
            dependencies = copiedIds;
            dependencyRequirements = copiedRequirements;
        }

        public void Configure(string id, string contentVersion, GameObject prefab, Avatar avatar)
        { Configure(id, contentVersion, prefab, avatar, body?.Rig ?? "Humanoid"); }

        public void Configure(string id, string contentVersion, GameObject prefab, Avatar avatar, string rig)
        {
            if (rig != "Humanoid" && rig != "Generic")
                throw new ArgumentException("Rig must be Humanoid or Generic.", nameof(rig));
            packageId = id;
            version = contentVersion;
            characterPrefab = prefab;
            humanoidAvatar = avatar;
            if (body == null) body = new BodyProfile();
            body.Rig = rig;
            body.Id = id + ":body." + rig.ToLowerInvariant();
        }

        public void DeclareCapability(string id, string executorId, bool requiresPresentation = false,
            string animatorState = null, string bodyConstraint = "Any")
        {
            if (bodyConstraint != "Any" && bodyConstraint != "Humanoid" && bodyConstraint != "Generic")
                throw new ArgumentException("Body constraint must be Any, Humanoid or Generic.", nameof(bodyConstraint));
            if (capabilities == null) capabilities = new List<CapabilityDefinition>();
            if (animationBindings == null) animationBindings = new List<AnimationBindingProfile>();
            for (int i = capabilities.Count - 1; i >= 0; i--)
                if (capabilities[i] != null && capabilities[i].Id == id) capabilities.RemoveAt(i);
            capabilities.Add(new CapabilityDefinition { Id = id, ExecutorId = executorId,
                RequiresPresentation = requiresPresentation, BodyConstraint = bodyConstraint });
            for (int i = animationBindings.Count - 1; i >= 0; i--)
                if (animationBindings[i] != null && animationBindings[i].CapabilityId == id)
                    animationBindings.RemoveAt(i);
            if (!string.IsNullOrWhiteSpace(animatorState))
                animationBindings.Add(new AnimationBindingProfile { Id = packageId + ":" + id,
                    CapabilityId = id, AnimatorState = animatorState });
        }

        public void BindPresentation(RuntimeAnimatorController controller, string capabilityId,
            string animatorState, bool loop, bool rootMotion = false)
        {
            if (controller == null || string.IsNullOrWhiteSpace(capabilityId) || string.IsNullOrWhiteSpace(animatorState))
                throw new ArgumentException("Controller, capability ID, and Animator state are required.");
            animatorController = controller;
            if (animationBindings == null) animationBindings = new List<AnimationBindingProfile>();
            for (int i = animationBindings.Count - 1; i >= 0; i--)
                if (animationBindings[i] != null && animationBindings[i].CapabilityId == capabilityId)
                    animationBindings.RemoveAt(i);
            animationBindings.Add(new AnimationBindingProfile { Id = packageId + ":" + capabilityId,
                CapabilityId = capabilityId, AnimatorState = animatorState,
                Loop = loop, RootMotion = rootMotion });
        }

        public PackageManifest ToManifest()
        {
            var manifest = new PackageManifest { Id = packageId, Version = version, Kind = "NPC", Body = body,
                SchemaVersion = dependencyRequirements != null && dependencyRequirements.Count > 0 ? 2 : 1 };
            if (dependencies != null) manifest.Dependencies.AddRange(dependencies);
            if (dependencyRequirements != null)
                foreach (var requirement in dependencyRequirements) manifest.DependencyRequirements.Add(requirement?.Copy());
            if (capabilities != null) foreach (var capability in capabilities) manifest.Capabilities.Add(capability);
            if (animationBindings != null) foreach (var binding in animationBindings) manifest.AnimationBindings.Add(binding);
            if (actions != null) foreach (var action in actions) manifest.Actions.Add(action);
            return manifest;
        }

        public List<Diagnostic> Validate()
        {
            var result = new List<Diagnostic>();
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
                result.Add(new Diagnostic("NPC_ID", name, "包 ID 和版本必填。"));
            var rig = body?.Rig;
            if (rig != "Humanoid" && rig != "Generic")
                result.Add(new Diagnostic("NPC_RIG", name, "身体骨骼类型必须为 Humanoid 或 Generic。"));
            if (characterPrefab == null) result.Add(new Diagnostic("NPC_PREFAB", name, "未绑定角色 Prefab 或 FBX。"));
            else
            {
                var marker = characterPrefab.GetComponent<SocialNpcMarker>();
                if (marker == null || characterPrefab.GetComponentsInChildren<SocialNpcMarker>(true).Length != 1)
                    result.Add(new Diagnostic("NPC_HIERARCHY", name,
                        "角色 Prefab 根节点必须且只能有一个 SocialNpcMarker。"));
                else if (marker.NpcInstanceId != "template." + packageId)
                    result.Add(new Diagnostic("NPC_TEMPLATE_ID", name,
                        "Prefab 标记实例 ID 应为 template.<包 ID>，场景实例再分配唯一 ID。"));
                if (marker != null && marker.PackageId != packageId)
                    result.Add(new Diagnostic("NPC_PACKAGE_BINDING", name,
                        "角色 Prefab 标记的包 ID 与 NPC 包资产不一致。"));
                if (marker != null && marker.PersonalityArchetype != null)
                    try { PersonalityGenerator.Validate(marker.PersonalityArchetype); }
                    catch (ArgumentException error)
                    {
                        result.Add(new Diagnostic("NPC_PERSONALITY", name,
                            "人格原型配置无效：" + error.Message));
                    }
                if (characterPrefab.GetComponent<NavMeshAgent>() == null ||
                    characterPrefab.GetComponent<SocialNpcNavigation>() == null)
                    result.Add(new Diagnostic("NPC_NAVIGATION", name,
                        "角色 Prefab 根节点需要 NavMeshAgent 和 SocialNpcNavigation。"));
                else if (characterPrefab.GetComponent<NavMeshAgent>().enabled)
                    result.Add(new Diagnostic("NPC_NAVIGATION_STARTUP", name,
                        "Prefab 的 NavMeshAgent 应默认禁用，由 SocialNpcNavigation 在场景导航数据就绪后启用。"));
                else if (!characterPrefab.GetComponent<SocialNpcNavigation>().enabled)
                    result.Add(new Diagnostic("NPC_NAVIGATION_STARTUP", name,
                        "Prefab 的 SocialNpcNavigation 必须默认启用，以激活导航并管理角色启停。"));
                var visualRoot = characterPrefab.transform.Find("Visual");
                if (visualRoot == null || visualRoot.parent != characterPrefab.transform)
                    result.Add(new Diagnostic("NPC_VISUAL_ROOT", name,
                        "角色 Prefab 根节点直下需要 Visual 子节点。"));
                var visualAnimator = visualRoot == null ? null : visualRoot.GetComponentInChildren<Animator>(true);
                if (visualAnimator == null || marker == null || marker.Animator != visualAnimator)
                    result.Add(new Diagnostic("NPC_ANIMATOR", name,
                        "Visual 子树需要 Animator，根标记必须引用同一个 Animator。"));
                else
                {
                    if (visualAnimator.avatar != humanoidAvatar)
                        result.Add(new Diagnostic("NPC_AVATAR_BINDING", name,
                            "Visual Animator 的 Avatar 与包声明不一致。"));
                    if (visualAnimator.applyRootMotion)
                        result.Add(new Diagnostic("NPC_ROOT_MOTION", name,
                            "NavMeshAgent 驱动的标准角色须关闭 Animator Root Motion。"));
                    if (animationBindings != null && animationBindings.Count > 0 &&
                        visualAnimator.runtimeAnimatorController != animatorController)
                        result.Add(new Diagnostic("NPC_CONTROLLER_BINDING", name,
                            "Visual Animator Controller 与包声明不一致。"));
                }
            }
            if (humanoidAvatar == null || !humanoidAvatar.isValid ||
                (rig == "Humanoid" && !humanoidAvatar.isHuman) ||
                (rig == "Generic" && humanoidAvatar.isHuman))
                result.Add(new Diagnostic("NPC_AVATAR", name,
                    "需要与 BodyProfile.Rig 一致的有效 Avatar；请检查模型导入设置。"));
            if (body == null || body.HeightMeters <= 0 || body.RadiusMeters <= 0 ||
                body.WalkMetersPerSecond <= 0 || body.RunMetersPerSecond <= 0)
                result.Add(new Diagnostic("NPC_BODY", name, "身高、半径和行走速度必须大于零。"));
            if (capabilities == null || animationBindings == null || actions == null)
                result.Add(new Diagnostic("NPC_LISTS", name, "能力、动画映射和动作列表不能缺失。"));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in capabilities ?? new List<CapabilityDefinition>())
            {
                if (capability == null || string.IsNullOrWhiteSpace(capability.Id) || !ids.Add(capability.Id))
                { result.Add(new Diagnostic("NPC_CAPABILITY", name, "能力 ID 为空或重复。")); continue; }
                if (string.IsNullOrWhiteSpace(capability.ExecutorId))
                    result.Add(new Diagnostic("NPC_EXECUTOR", capability.Id, "未绑定执行器。"));
                if (capability.BodyConstraint != "Any" && capability.BodyConstraint != rig)
                    result.Add(new Diagnostic("NPC_CAPABILITY_RIG", capability.Id,
                        "能力骨骼约束与 NPC 身体骨骼类型不一致。"));
                if (capability.RequiresPresentation && !HasAnimationBinding(capability.Id))
                    result.Add(new Diagnostic("NPC_ANIMATION", capability.Id, "必需表现没有动画映射。"));
            }
            foreach (var binding in animationBindings ?? new List<AnimationBindingProfile>())
                if (binding == null || !ids.Contains(binding.CapabilityId) || string.IsNullOrWhiteSpace(binding.AnimatorState))
                    result.Add(new Diagnostic("NPC_ANIMATION", name, "动画映射引用无效能力或缺少 Animator 状态。"));
            if (animationBindings != null && animationBindings.Count > 0 && animatorController == null)
                result.Add(new Diagnostic("NPC_CONTROLLER", name, "已声明动画映射，但未指定 Animator Controller。"));
            if (capabilities == null || capabilities.Count == 0)
                result.Add(new Diagnostic("NPC_CAPABILITY", name, "请显式声明基础能力。"));
            return result;
        }

        private bool HasAnimationBinding(string id)
        {
            foreach (var binding in animationBindings ?? new List<AnimationBindingProfile>())
                if (binding != null && binding.CapabilityId == id && !string.IsNullOrWhiteSpace(binding.AnimatorState)) return true;
            return false;
        }
    }

}
