using UnityEngine;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>Explicit social collective metadata for one world, independent of domain naming.</summary>
    [DisallowMultipleComponent]
    public sealed class SocialCollectiveMarker : MonoBehaviour
    {
        [SerializeField] private string collectiveId;
        [SerializeField] private string displayName;
        [SerializeField] private bool formal;
        [SerializeField] private bool organization;

        public string CollectiveId => collectiveId;
        public string DisplayName => displayName;
        public bool Formal => formal;
        public bool Organization => organization;

        public void Configure(string id, string label, bool isFormal, bool isOrganization)
        {
            collectiveId = id;
            displayName = label;
            formal = isFormal;
            organization = isOrganization;
        }
    }
}
