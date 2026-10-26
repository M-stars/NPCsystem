using UnityEngine;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>A semantic interaction point; occupancy is owned by the simulation, not this component.</summary>
    [DisallowMultipleComponent]
    public sealed class SocialInteractionPoint : MonoBehaviour
    {
        [SerializeField] private string localId;
        [SerializeField] private string actionId;
        [SerializeField, Min(1)] private int capacity = 1;
        [SerializeField] private Transform anchor;

        public string LocalId => localId;
        public string ActionId => actionId;
        public int Capacity => capacity;
        public Vector3 AnchorPosition => anchor != null ? anchor.position : transform.position;

        public string StableId
        {
            get
            {
                var region = GetComponentInParent<SocialWorldMarker>();
                return region == null ? string.Empty : region.RegionInstanceId + "/" + localId;
            }
        }

        public void Configure(string id, string action, int slotCapacity, Transform pointAnchor = null)
        {
            localId = id;
            actionId = action;
            capacity = Mathf.Max(1, slotCapacity);
            anchor = pointAnchor;
        }
    }
}
