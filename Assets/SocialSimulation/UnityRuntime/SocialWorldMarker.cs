using UnityEngine;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>Stable, authored identity for a region instance in a scene.</summary>
    [DisallowMultipleComponent]
    public sealed class SocialWorldMarker : MonoBehaviour
    {
        [SerializeField] private string regionInstanceId;
        [SerializeField] private string regionDefinitionId;
        [SerializeField] private string displayName;
        [SerializeField] private string domainId;
        [SerializeField] private string packageId;
        [SerializeField] private string ownerCollectiveId;
        [SerializeField] private bool hasBoundary;
        [SerializeField] private Bounds localBounds = new Bounds(Vector3.zero, new Vector3(10, 5, 10));

        public string RegionInstanceId => regionInstanceId;
        public string RegionDefinitionId => regionDefinitionId;
        public string DisplayName => displayName;
        public string DomainId => domainId;
        public string PackageId => packageId;
        public string OwnerCollectiveId => ownerCollectiveId;
        public bool HasBoundary => hasBoundary;
        public Bounds LocalBounds => localBounds;
        public void ConfigureOwner(string collectiveId) => ownerCollectiveId = collectiveId;
        public void ConfigureBounds(Bounds bounds, bool enabled = true)
        { localBounds = bounds; hasBoundary = enabled; }
        public bool Contains(Vector3 point) => !hasBoundary || localBounds.Contains(transform.InverseTransformPoint(point));

        public void Configure(string instanceId, string definitionId, string label, string domain)
        {
            regionInstanceId = instanceId;
            regionDefinitionId = definitionId;
            displayName = label;
            domainId = domain;
        }

        public void BindPackage(string id) => packageId = id;
    }
}
