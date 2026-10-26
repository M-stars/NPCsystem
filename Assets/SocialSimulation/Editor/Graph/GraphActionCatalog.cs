using System;
using System.Collections.Generic;
using SocialSimulation.Authoring;
using SocialSimulation.Core;

namespace SocialSimulation.Editor.Graph
{
    /// <summary>A successful atomic content registration is the source of editor action availability.</summary>
    public sealed class GraphActionCatalog
    {
        public readonly ContentRegistry Registry = new ContentRegistry();
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        public bool IsValid { get; private set; }

        public GraphActionCatalog(SocialContentCatalogAsset catalog)
        {
            if (catalog == null)
            { Diagnostics.Add(new Diagnostic("MISSING_CATALOG", "graph", "请指定内容目录。")); return; }
            try
            {
                var result = catalog.BuildRegistry(Registry);
                Diagnostics.AddRange(result.Diagnostics);
                IsValid = result.Success;
            }
            catch (Exception exception)
            { Diagnostics.Add(new Diagnostic("INVALID_CATALOG", catalog.name, exception.Message)); }
        }

        public bool HasExecutor(string id)
        {
            // These are precisely the executors registered by SocialContentCatalogAsset.BuildRegistry.
            return IsValid && (id == "navmesh.move" || id == "simulation.action" || id == "animator.present");
        }

        public ActionDefinition ResolveAction(string id)
        { return IsValid && Registry.Actions.TryGetValue(id ?? "", out var action) ? action : null; }

        public PackageManifest ResolveTemplate(NpcPackageAsset template)
        {
            if (!IsValid || template == null || !Registry.Packages.TryGetValue(template.PackageId ?? "", out var manifest) ||
                manifest.Kind != "NPC" || manifest.Version != template.Version) return null;
            // Identity is also verified by the window against the catalog's actual object references.
            return manifest;
        }
    }
}
