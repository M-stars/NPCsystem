using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using SocialSimulation.Authoring;
using SocialSimulation.Core;
using SocialSimulation.Domains;
using SocialSimulation.UnityRuntime;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UIElements;

[assembly: InternalsVisibleTo("SocialSimulation.Content.Tests")]

namespace SocialSimulation.Editor.Content
{
    /// <summary>Scans installed content, validates semantic declarations, and commits an asset catalog.</summary>
    public sealed class ContentCatalogWindow : EditorWindow
    {
        private enum RigImportMode { Auto, Humanoid, Generic }

        private const string CatalogPath = "Assets/SocialSimulation/Authoring/SocialContentCatalog.asset";
        private const string StylePath = "Assets/SocialSimulation/Editor/Content/ContentCatalogWindow.uss";
        private SocialContentCatalogAsset catalog;
        private ScrollView list;
        private Label summary;

        [MenuItem("Tools/Social Simulation/Content Catalog")]
        public static void Open()
        {
            var window = GetWindow<ContentCatalogWindow>("Social Content");
            window.minSize = new Vector2(700, 480);
            window.Show();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (style != null) rootVisualElement.styleSheets.Add(style);
            rootVisualElement.AddToClassList("catalog-root");
            rootVisualElement.Add(new Label("内容包注册与验证") { name = "title" });
            rootVisualElement.Q<Label>("title").AddToClassList("catalog-title");

            catalog = AssetDatabase.LoadAssetAtPath<SocialContentCatalogAsset>(CatalogPath);
            var field = new ObjectField("当前目录") { objectType = typeof(SocialContentCatalogAsset), value = catalog };
            field.RegisterValueChangedCallback(change => { catalog = change.newValue as SocialContentCatalogAsset; Refresh(); });
            rootVisualElement.Add(field);
            var toolbar = new VisualElement(); toolbar.AddToClassList("catalog-toolbar");
            toolbar.Add(new Button(CreateCatalog) { text = "创建目录" });
            toolbar.Add(new Button(Refresh) { text = "扫描资源" });
            toolbar.Add(new Button(ValidateCatalog) { text = "校验已注册内容" });
            toolbar.Add(new Button(ShowNpcImportMenu) { text = "从选中 FBX 创建标准 NPC 包" });
            rootVisualElement.Add(toolbar);
            rootVisualElement.Add(new Label("包的 Inspector 可配置 Dependencies（仅要求存在）和 Dependency Requirements（版本依赖）。" +
                "支持 *、1.2.3、[1.2,2)、[1.2,)；同一依赖不要重复填写。版本不符时注册诊断会显示要求及实际版本。")
                { name = "dependency-help", enableRichText = false });
            list = new ScrollView { name = "packageList" };
            list.style.flexGrow = 1;
            rootVisualElement.Add(list);
            summary = new Label(); summary.AddToClassList("catalog-summary"); rootVisualElement.Add(summary);
            Refresh();
        }

        private void CreateCatalog()
        {
            catalog = AssetDatabase.LoadAssetAtPath<SocialContentCatalogAsset>(CatalogPath);
            if (catalog == null)
            {
                var folder = Path.GetDirectoryName(CatalogPath)?.Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(folder)) { summary.text = "找不到目录：" + folder; return; }
                catalog = CreateInstance<SocialContentCatalogAsset>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                AssetDatabase.SaveAssets();
            }
            var field = rootVisualElement.Q<ObjectField>();
            if (field != null) field.SetValueWithoutNotify(catalog);
            Refresh();
        }

        private void Refresh()
        {
            if (list == null) return;
            list.Clear();
            var npcs = FindAssets<NpcPackageAsset>();
            var regions = FindAssets<RegionPackageAsset>();
            var domains = FindAssets<SocialDomainPackageAsset>();
            AddHeader("NPC 标准包 · " + npcs.Count);
            foreach (var package in npcs) AddPackage(package, NpcDiagnostics(package), () => AddNpc(package),
                catalog != null && catalog.NpcPackages.Contains(package));
            AddHeader("地区标准包 · " + regions.Count);
            foreach (var package in regions) AddPackage(package, RegionDiagnostics(package), () => AddRegion(package),
                catalog != null && catalog.RegionPackages.Contains(package));
            AddHeader("社会领域包 · " + domains.Count);
            foreach (var package in domains)
            {
                var errors = new List<Diagnostic>();
                if (string.IsNullOrWhiteSpace(package.PackageId)) errors.Add(new Diagnostic("DOMAIN_ID", package.name, "领域包 ID 为空。"));
                AddPackage(package, errors, () => AddDomain(package), catalog != null && catalog.DomainPackages.Contains(package));
            }
            summary.text = catalog == null ? "请先创建目录，再逐项注册通过校验的包。" :
                "注册后运行“校验已注册内容”检查版本、依赖、能力执行器与重复 ID。";
        }

        private static List<T> FindAssets<T>() where T : UnityEngine.Object
        {
            var result = new List<T>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + typeof(T).Name))
            {
                var item = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
                if (item != null) result.Add(item);
            }
            result.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
            return result;
        }

        private void AddHeader(string title)
        { var label = new Label(title); label.AddToClassList("catalog-section"); list.Add(label); }

        private void AddPackage(UnityEngine.Object asset, List<Diagnostic> diagnostics, Action register, bool installed)
        {
            var row = new VisualElement(); row.AddToClassList("catalog-row");
            var status = diagnostics.Count == 0 ? "结构通过" : "错误 " + diagnostics.Count;
            string packageId = null, version = null;
            if (asset is NpcPackageAsset npc) { packageId = npc.PackageId; version = npc.Version; }
            else if (asset is RegionPackageAsset region) { packageId = region.PackageId; version = region.Version; }
            else if (asset is SocialDomainPackageAsset domain) { packageId = domain.PackageId; version = domain.Version; }
            var label = new Label((packageId ?? asset.name) + "  v" + (version ?? "—") + "  ·  " + status)
                { enableRichText = false };
            label.AddToClassList(diagnostics.Count == 0 ? "catalog-good" : "catalog-error");
            label.tooltip = diagnostics.Count == 0 ? AssetDatabase.GetAssetPath(asset) : string.Join("\n", diagnostics);
            row.Add(label);
            row.Add(new Button(() => Selection.activeObject = asset)
                { text = "编辑依赖 / Inspector", tooltip = "选中此包，在 Inspector 中编辑依赖后重新校验。" });
            var button = new Button(register) { text = installed ? "已注册" : "注册" };
            button.SetEnabled(!installed && diagnostics.Count == 0 && catalog != null);
            row.Add(button);
            list.Add(row);
        }

        private void AddNpc(NpcPackageAsset asset)
        { TryRegister(asset); }
        private void AddRegion(RegionPackageAsset asset)
        { TryRegister(asset); }
        private void AddDomain(SocialDomainPackageAsset asset)
        { TryRegister(asset); }

        private void TryRegister(UnityEngine.Object candidate)
        {
            if (catalog == null || candidate == null) return;
            var result = PreviewRegistration(catalog, candidate);
            if (!result.Success)
            {
                summary.text = "注册失败：" + string.Join("；", result.Diagnostics);
                return;
            }
            if (result.AlreadyRegistered) return;
            Undo.RecordObject(catalog, "Register content package");
            if (candidate is NpcPackageAsset npcPackage) catalog.NpcPackages.Add(npcPackage);
            else if (candidate is RegionPackageAsset regionPackage) catalog.RegionPackages.Add(regionPackage);
            else if (candidate is SocialDomainPackageAsset domainPackage) catalog.DomainPackages.Add(domainPackage);
            SaveCatalog();
            summary.text = "注册成功：" + candidate.name + "；全部已注册包联合校验通过。";
        }

        internal static RegistrationResult PreviewRegistration(SocialContentCatalogAsset content, UnityEngine.Object candidate)
        {
            var result = new RegistrationResult();
            if (content == null || candidate == null)
            {
                result.Diagnostics.Add(new Diagnostic("CATALOG_INPUT", "catalog", "内容目录和待注册包不能为空。"));
                return result;
            }
            var preview = CreateInstance<SocialContentCatalogAsset>();
            var diagnostics = result.Diagnostics;
            try
            {
                preview.NpcPackages.AddRange(content.NpcPackages);
                preview.RegionPackages.AddRange(content.RegionPackages);
                preview.DomainPackages.AddRange(content.DomainPackages);
                if (candidate is NpcPackageAsset npc && !preview.NpcPackages.Contains(npc))
                    preview.NpcPackages.Add(npc);
                else if (candidate is RegionPackageAsset region && !preview.RegionPackages.Contains(region))
                    preview.RegionPackages.Add(region);
                else if (candidate is SocialDomainPackageAsset domain && !preview.DomainPackages.Contains(domain))
                    preview.DomainPackages.Add(domain);
                else if (candidate is NpcPackageAsset || candidate is RegionPackageAsset || candidate is SocialDomainPackageAsset)
                    result.AlreadyRegistered = true;
                else
                { diagnostics.Add(new Diagnostic("PACKAGE_KIND", candidate.name, "需要 NPC、地区或领域包资产。")); return result; }
                diagnostics.AddRange(CollectLayoutErrors(preview));
                if (diagnostics.Count == 0)
                {
                    var registration = preview.BuildRegistry(new ContentRegistry());
                    diagnostics.AddRange(registration.Diagnostics);
                    if (!registration.Success && registration.Diagnostics.Count == 0)
                        diagnostics.Add(new Diagnostic("CATALOG_REJECTED", candidate.name,
                            "联合注册失败，未返回具体诊断。"));
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic("CATALOG_EXCEPTION", candidate.name, exception.Message));
            }
            finally { DestroyImmediate(preview); }
            result.Success = diagnostics.Count == 0;
            return result;
        }

        private void SaveCatalog()
        { EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssets(); Refresh(); }

        private void ValidateCatalog()
        {
            if (catalog == null) { summary.text = "请先创建目录。"; return; }
            var layoutErrors = CollectLayoutErrors(catalog);
            if (layoutErrors.Count != 0)
            { summary.text = "目录布局校验失败：" + string.Join("；", layoutErrors); return; }
            var result = catalog.BuildRegistry(new ContentRegistry());
            summary.text = result.Success ? "校验通过：所有已注册包与依赖可一起加载。" :
                "校验失败：" + string.Join("；", result.Diagnostics);
        }

        private static List<Diagnostic> CollectLayoutErrors(SocialContentCatalogAsset content)
        {
            var errors = new List<Diagnostic>();
            foreach (var npc in content.NpcPackages)
                if (npc != null) errors.AddRange(StandardPackageLayout.Validate(npc));
            foreach (var region in content.RegionPackages)
                if (region != null) errors.AddRange(StandardPackageLayout.Validate(region));
            return errors;
        }

        private static List<Diagnostic> NpcDiagnostics(NpcPackageAsset asset)
        {
            var diagnostics = asset.Validate();
            diagnostics.AddRange(StandardPackageLayout.Validate(asset));
            return diagnostics;
        }

        private static List<Diagnostic> RegionDiagnostics(RegionPackageAsset asset)
        {
            var diagnostics = asset.Validate();
            diagnostics.AddRange(StandardPackageLayout.Validate(asset));
            return diagnostics;
        }

        private void ShowNpcImportMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("自动识别骨骼"), false,
                () => CreateNpcFromSelectedModel(RigImportMode.Auto));
            menu.AddItem(new GUIContent("Humanoid 骨骼"), false,
                () => CreateNpcFromSelectedModel(RigImportMode.Humanoid));
            menu.AddItem(new GUIContent("Generic 骨骼"), false,
                () => CreateNpcFromSelectedModel(RigImportMode.Generic));
            menu.ShowAsContext();
        }

        private void CreateNpcFromSelectedModel(RigImportMode mode)
        {
            var selected = Selection.activeObject;
            var sourcePath = AssetDatabase.GetAssetPath(selected);
            if (string.IsNullOrEmpty(sourcePath) || !sourcePath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            { summary.text = "请先在 Project 窗口选中 FBX 模型。"; return; }
            const string parent = "Assets/SocialSimulation/NpcPackages";
            var folder = string.Empty;
            try
            {
                EnsureFolder("Assets/SocialSimulation", "NpcPackages");
                var baseName = Path.GetFileNameWithoutExtension(sourcePath).ToLowerInvariant();
                var safeName = new System.Text.StringBuilder();
                foreach (var character in baseName)
                    safeName.Append((character >= 'a' && character <= 'z') ||
                        (character >= '0' && character <= '9') ? character : '_');
                var stem = safeName.ToString().Trim('_');
                if (stem.Length == 0) stem = "character";
                var folderName = stem;
                for (var suffix = 2; AssetDatabase.IsValidFolder(parent + "/" + folderName); suffix++)
                    folderName = stem + "_" + suffix;
                AssetDatabase.CreateFolder(parent, folderName);
                folder = parent + "/" + folderName;
                foreach (var child in new[] { "Prefab", "Model", "Rig", "Animations", "Materials", "Textures" })
                    EnsureFolder(folder, child);
                var modelPath = folder + "/Model/Character.fbx";
                if (!AssetDatabase.CopyAsset(sourcePath, modelPath))
                    throw new InvalidOperationException("无法复制选中的 FBX 到标准 Model 目录。");
                var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
                if (importer == null) throw new InvalidOperationException("复制后的 FBX 没有 ModelImporter。");
                var sourceImporter = AssetImporter.GetAtPath(sourcePath) as ModelImporter;
                var selectedRig = mode == RigImportMode.Generic ? "Generic" : "Humanoid";
                if (mode == RigImportMode.Auto && sourceImporter != null &&
                    sourceImporter.animationType == ModelImporterAnimationType.Generic)
                    selectedRig = "Generic";
                var avatar = ImportAvatar(importer, modelPath, selectedRig);
                if (avatar == null && mode == RigImportMode.Auto)
                {
                    selectedRig = selectedRig == "Humanoid" ? "Generic" : "Humanoid";
                    avatar = ImportAvatar(importer, modelPath, selectedRig);
                }
                if (avatar == null)
                    throw new InvalidOperationException("骨骼无法生成有效 " + selectedRig + " Avatar。");
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                var root = new GameObject("Character");
                GameObject prefab;
                try
                {
                    var id = "npc." + folderName;
                    var marker = root.AddComponent<SocialNpcMarker>();
                    marker.Configure("template." + id, model.name, string.Empty);
                    marker.BindPackage(id);
                    var navAgent = root.AddComponent<NavMeshAgent>();
                    navAgent.enabled = false;
                    navAgent.radius = 0.3f; navAgent.height = 1.7f; navAgent.speed = 1.4f;
                    root.AddComponent<SocialNpcNavigation>();
                    var visualRoot = new GameObject("Visual");
                    visualRoot.transform.SetParent(root.transform, false);
                    var visual = PrefabUtility.InstantiatePrefab(model) as GameObject;
                    if (visual == null) throw new InvalidOperationException("无法实例化 FBX 模型。");
                    visual.transform.SetParent(visualRoot.transform, false);
                    visual.transform.localPosition = Vector3.zero;
                    visual.transform.localRotation = Quaternion.identity;
                    var renderers = visual.GetComponentsInChildren<Renderer>(true);
                    if (renderers.Length == 0)
                        throw new InvalidOperationException("模型没有可见的 Renderer。");
                    var bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    visual.transform.localScale *= 1.7f / Mathf.Max(0.01f, bounds.size.y);
                    bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    visual.transform.position -= Vector3.up * bounds.min.y;
                    var animator = visual.GetComponentInChildren<Animator>(true) ?? visual.AddComponent<Animator>();
                    animator.avatar = avatar;
                    animator.applyRootMotion = false;
                    marker.SetAnimator(animator);
                    prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Prefab/Character.prefab");
                }
                finally { DestroyImmediate(root); }
                if (prefab == null) throw new InvalidOperationException("无法写入标准角色 Prefab。");
                var package = CreateInstance<NpcPackageAsset>();
                package.Configure("npc." + folderName, "1.0.0", prefab, avatar, selectedRig);
                package.DeclareCapability("locomotion.walk", "navmesh.move");
                package.DeclareCapability("locomotion.run", "navmesh.move");
                AssetDatabase.CreateAsset(package, folder + "/NpcPackage.asset");
                File.WriteAllText(folder + "/Source.md",
                    "# Source\n\nOriginal FBX: `" + sourcePath + "`\n\nAdd the source license and animation/controller bindings before publication.\n");
                AssetDatabase.ImportAsset(folder + "/Source.md");
                AssetDatabase.SaveAssets();
                Selection.activeObject = package;
                summary.text = "已创建 " + selectedRig + " 标准 NPC 包。请补充动作、Controller、授权记录，然后校验注册。";
                Refresh();
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.DeleteAsset(folder);
                summary.text = "NPC 标准包创建失败，已清理新目录：" + exception.Message;
            }
        }

        private static Avatar ImportAvatar(ModelImporter importer, string modelPath, string rig)
        {
            importer.animationType = rig == "Humanoid" ?
                ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();
            foreach (var subasset in AssetDatabase.LoadAllAssetsAtPath(modelPath))
                if (subasset is Avatar candidate && candidate.isValid &&
                    candidate.isHuman == (rig == "Humanoid")) return candidate;
            return null;
        }

        private static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
