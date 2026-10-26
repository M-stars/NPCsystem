using UnityEngine;

namespace SocialSimulation.UnityRuntime
{
    /// <summary>A stable doorway. Connections belong to scene instances, not shared package templates.</summary>
    [DisallowMultipleComponent]
    public sealed class SocialRegionEntrance : MonoBehaviour
    {
        [SerializeField] private string localId;
        [SerializeField] private string targetRegionInstanceId;
        [SerializeField] private string targetEntranceLocalId;
        public string LocalId => localId;
        public string TargetRegionInstanceId => targetRegionInstanceId;
        public string TargetEntranceLocalId => targetEntranceLocalId;
        public void Configure(string id, string targetRegion = null, string targetEntrance = null)
        { localId = id; targetRegionInstanceId = targetRegion; targetEntranceLocalId = targetEntrance; }
    }
}
