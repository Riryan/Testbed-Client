using System.Collections.Generic;

namespace LiteNetLibManager
{
    public class LiteNetLibSyncingStates
    {
        private readonly Dictionary<byte, Dictionary<uint, GameStateSyncData>> _states = new Dictionary<byte, Dictionary<uint, GameStateSyncData>>();
        public Dictionary<byte, Dictionary<uint, GameStateSyncData>> States => _states;

        private static void ReleaseCollection(Dictionary<uint, GameStateSyncData> collection)
        {
            foreach (GameStateSyncData data in collection.Values)
                GameStateSyncDataPool.Return(data);
            collection.Clear();
        }

        public void Clear()
        {
            foreach (Dictionary<uint, GameStateSyncData> collection in _states.Values)
                ReleaseCollection(collection);
            _states.Clear();
        }

        public void Clear(byte channelId)
        {
            if (_states.TryGetValue(channelId, out Dictionary<uint, GameStateSyncData> collection))
                ReleaseCollection(collection);
        }

        public Dictionary<uint, GameStateSyncData> PrepareSyncStateCollection(byte channelId)
        {
            if (!_states.TryGetValue(channelId, out var collectionByObjectId))
            {
                collectionByObjectId = new Dictionary<uint, GameStateSyncData>();
                _states[channelId] = collectionByObjectId;
            }
            return collectionByObjectId;
        }

        public GameStateSyncData PrepareSyncStateData(byte channelId, uint objectId)
        {
            var collectionByObjectId = PrepareSyncStateCollection(channelId);
            if (!collectionByObjectId.TryGetValue(objectId, out var syncData))
            {
                syncData = GameStateSyncDataPool.Rent();
                collectionByObjectId[objectId] = syncData;
            }
            return syncData;
        }

        public void AppendSpawnSyncState(LiteNetLibIdentity identity)
        {
            byte channelId = identity.SyncChannelId;
            uint objectId = identity.ObjectId;
            var syncData = PrepareSyncStateData(channelId, objectId);
            syncData.Identity = identity;
            syncData.StateType = GameStateSyncType.Spawn;
            syncData.DestroyReasons = 0;
            syncData.SyncElements.Clear();
        }

        public void AppendDestroySyncState(LiteNetLibIdentity identity, byte reasons)
        {
            byte channelId = identity.SyncChannelId;
            uint objectId = identity.ObjectId;
            var syncData = PrepareSyncStateData(channelId, objectId);
            syncData.Identity = identity;
            syncData.StateType = GameStateSyncType.Destroy;
            syncData.DestroyReasons = reasons;
            syncData.SyncElements.Clear();
        }

        public void AppendDataSyncState(LiteNetLibSyncElement syncElement)
        {
            if (syncElement.Identity == null)
            {
                Logging.LogError("Unable to append base-line data sync state, sync element's identity is null");
                return;
            }
            byte channelId = syncElement.SyncChannelId;
            uint objectId = syncElement.ObjectId;
            var syncData = PrepareSyncStateData(channelId, objectId);
            if (syncData.StateType == GameStateSyncType.Spawn || syncData.StateType == GameStateSyncType.Destroy)
                return;
            syncData.Identity = syncElement.Identity;
            syncData.StateType = GameStateSyncType.Data;
            syncData.DestroyReasons = 0;
            syncData.SyncElements.Add(syncElement);
        }

        public void RemoveSyncState(LiteNetLibIdentity identity)
        {
            byte channelId = identity.SyncChannelId;
            uint objectId = identity.ObjectId;
            if (_states.TryGetValue(channelId, out Dictionary<uint, GameStateSyncData> collection) &&
                collection.TryGetValue(objectId, out GameStateSyncData data))
            {
                collection.Remove(objectId);
                GameStateSyncDataPool.Return(data);
            }
        }
    }
}
