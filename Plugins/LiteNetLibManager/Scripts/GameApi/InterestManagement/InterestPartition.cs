using UnityEngine;

namespace LiteNetLibManager
{
    /// <summary>
    /// Inspector-configurable logical map/instance membership.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InterestPartition :
        MonoBehaviour,
        IMMOInterestPartitionProvider
    {
        [SerializeField] private int mapId;
        [SerializeField] private int instanceId;

        public int InterestMapId => mapId;
        public int InterestInstanceId => instanceId;

        public void SetPartition(int newMapId, int newInstanceId)
        {
            mapId = newMapId;
            instanceId = newInstanceId;
        }
    }
}
