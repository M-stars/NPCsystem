using System;
using System.Collections.Generic;
using System.Linq;
using SocialSimulation.Authoring;
using SocialSimulation.Core;
using SocialSimulation.Graph;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SocialSimulation.Editor.Graph
{
    public sealed partial class GraphWorkbenchWindow
    {
        [SerializeField] private SocialContentCatalogAsset selectedCatalog;
        [SerializeField] private NpcPackageAsset selectedTemplate;
        [SerializeField] private bool compatibleOnly;
        private GraphActionCatalog actionCatalog;
        private PackageManifest templateManifest;

        private void CreateTemplateControls(VisualElement header)
        {
            if (selectedCatalog == null)
            {
                var provider = UnityEngine.Object.FindAnyObjectByType<SocialCatalogProvider>();
                selectedCatalog = provider != null ? provider.Catalog : null;
                if (selectedCatalog == null)
                    selectedCatalog = AssetDatabase.FindAssets("t:SocialContentCatalogAsset")
                        .Select(AssetDatabase.GUIDToAssetPath).OrderBy(path => path, StringComparer.Ordinal)
                        .Select(AssetDatabase.LoadAssetAtPath<SocialContentCatalogAsset>).FirstOrDefault();
            }
            var catalogField = new ObjectField("内容目录") { name = "graphContentCatalog",
                objectType = typeof(SocialContentCatalogAsset), value = selectedCatalog };
            catalogField.RegisterValueChangedCallback(evt =>
            { selectedCatalog = evt.newValue as SocialContentCatalogAsset; RefreshPalette(); ValidateDocument(); });
            header.Add(catalogField);
            var templateField = new ObjectField("NPC 模板") { name = "graphNpcTemplate",
                objectType = typeof(NpcPackageAsset), value = selectedTemplate,
                tooltip = "选择当前目录中注册的 NPC 包；检查能力、身体、执行器依赖与动画映射。" };
            templateField.RegisterValueChangedCallback(evt =>
            { selectedTemplate = evt.newValue as NpcPackageAsset; RefreshPalette(); ValidateDocument(); });
            header.Add(templateField);
            var toggle = new Toggle("仅显示兼容项") { value = compatibleOnly, name = "compatibleActionsOnly" };
            toggle.RegisterValueChangedCallback(evt => { compatibleOnly = evt.newValue; RefreshPalette(); });
            header.Add(toggle);
        }

        private void RefreshActionCatalog()
        {
            actionCatalog = new GraphActionCatalog(selectedCatalog);
            templateManifest = selectedCatalog != null && selectedCatalog.NpcPackages.Contains(selectedTemplate)
                ? actionCatalog.ResolveTemplate(selectedTemplate) : null;
        }

        private List<GraphDiagnostic> TemplateDiagnostics(GraphCompileResult result)
        {
            var entries = new List<GraphDiagnostic>();
            if (selectedTemplate == null || document == null || document.Kind != GraphKind.Behavior) return entries;
            if (actionCatalog == null) RefreshActionCatalog();
            if (templateManifest == null)
            {
                entries.Add(new GraphDiagnostic(GraphDiagnosticSeverity.Error, "UnregisteredTemplate", "",
                    "所选 NPC 模板不在有效注册目录中；检查内容目录及包引用。"));
                return entries;
            }
            if (result.Success && result.BehaviorPlan != null)
                entries.AddRange(GraphTemplateCompatibility.Validate(result.BehaviorPlan, templateManifest,
                    actionCatalog.ResolveAction, actionCatalog.HasExecutor));
            return entries;
        }

        private bool CanAddBinding(GraphNodeType type, string id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            if (actionCatalog == null) RefreshActionCatalog();
            if (selectedTemplate == null) return type != GraphNodeType.Action || actionCatalog.ResolveAction(id) != null;
            var check = type == GraphNodeType.Action
                ? GraphTemplateCompatibility.Evaluate(actionCatalog.ResolveAction(id), templateManifest, actionCatalog.HasExecutor)
                : GraphTemplateCompatibility.EvaluateCapability(id.StartsWith("capability:", StringComparison.Ordinal)
                    ? id.Substring("capability:".Length) : id, templateManifest, actionCatalog.HasExecutor);
            if (templateManifest != null && check.CanUse) return true;
            if (status != null) status.text = "无法添加：" + (templateManifest == null ? "模板未有效注册。" : check.Reason);
            return false;
        }

        private List<PaletteBinding> RegisteredPaletteBindings()
        {
            RefreshActionCatalog();
            var result = new List<PaletteBinding>();
            if (!actionCatalog.IsValid)
            {
                foreach (var diagnostic in actionCatalog.Diagnostics)
                    palette.Add(new Label("目录无效：" + diagnostic) { name = "invalidActionCatalog" });
                return result;
            }
            if (selectedTemplate != null && templateManifest == null)
                palette.Add(new Label("所选模板未注册到此目录。") { name = "unregisteredTemplate" });
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in actionCatalog.Registry.Packages.Values)
            {
                foreach (var action in package.Actions)
                {
                    if (!ids.Add("action:" + action.Id)) continue;
                    var check = templateManifest == null ? null :
                        GraphTemplateCompatibility.Evaluate(action, templateManifest, actionCatalog.HasExecutor);
                    result.Add(new PaletteBinding { Package = package.Id, Id = action.Id,
                        CapabilityId = action.CapabilityId, Type = GraphNodeType.Action, Compatibility = check });
                }
                foreach (var capability in package.Capabilities)
                {
                    if (!ids.Add("capability:" + capability.Id)) continue;
                    var check = templateManifest == null ? null :
                        GraphTemplateCompatibility.EvaluateCapability(capability.Id, templateManifest, actionCatalog.HasExecutor);
                    result.Add(new PaletteBinding { Package = package.Id, Id = "capability:" + capability.Id,
                        CapabilityId = capability.Id, Type = GraphNodeType.Condition, Compatibility = check });
                }
            }
            foreach (var domain in selectedCatalog.DomainPackages)
                foreach (var id in domain.ToManifest().ActionIds)
                {
                    if (string.IsNullOrWhiteSpace(id) || !ids.Add("action:" + id)) continue;
                    result.Add(new PaletteBinding { Package = domain.DisplayName ?? domain.PackageId,
                        Id = id, Type = GraphNodeType.Action,
                        Compatibility = GraphTemplateCompatibility.Evaluate(null, templateManifest, actionCatalog.HasExecutor) });
                }
            return result;
        }
    }
}
