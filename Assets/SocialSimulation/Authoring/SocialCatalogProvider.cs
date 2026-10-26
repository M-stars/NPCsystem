using System;
using SocialSimulation.Core;
using SocialSimulation.UnityRuntime;
using UnityEngine;

namespace SocialSimulation.Authoring
{
    [DisallowMultipleComponent]
    public sealed class SocialCatalogProvider : MonoBehaviour, ISocialContentProvider
    {
        [SerializeField] private SocialContentCatalogAsset catalog;

        public SocialContentCatalogAsset Catalog => catalog;
        public void Configure(SocialContentCatalogAsset value) => catalog = value;

        public ContentRegistry BuildRegistry()
        {
            if (catalog == null)
                throw new InvalidOperationException("Social content catalog is not assigned.");
            var registry = new ContentRegistry();
            var result = catalog.BuildRegistry(registry);
            if (!result.Success)
                throw new InvalidOperationException("Social content catalog failed validation: " +
                    string.Join("; ", result.Diagnostics));
            return registry;
        }
    }
}
