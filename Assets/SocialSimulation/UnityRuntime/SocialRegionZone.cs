using UnityEngine;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>A functional area inside one region package.</summary>
    [DisallowMultipleComponent]
    public sealed class SocialRegionZone : MonoBehaviour
    {
        [SerializeField] private string localId;
        [UnityEngine.Serialization.FormerlySerializedAs("tag")]
        [SerializeField] private string semanticTag;
        [SerializeField, Min(1)] private int capacity = 1;

        public string LocalId => localId;
        public string Tag => semanticTag;
        public int Capacity => capacity;

        public void Configure(string id, string semanticTag, int maximumOccupants)
        {
            localId = id;
            this.semanticTag = semanticTag;
            capacity = Mathf.Max(1, maximumOccupants);
        }
    }
}
