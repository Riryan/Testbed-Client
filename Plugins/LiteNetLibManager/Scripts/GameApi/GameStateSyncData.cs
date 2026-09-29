using System.Collections.Generic;

namespace LiteNetLibManager
{
    public class GameStateSyncData
    {
        public LiteNetLibIdentity Identity;
        public GameStateSyncType StateType = GameStateSyncType.None;
        public byte DestroyReasons = 0;
        public readonly HashSet<LiteNetLibSyncElement> SyncElements = new HashSet<LiteNetLibSyncElement>();

        public void Reset()
        {
            Identity = null;
            StateType = GameStateSyncType.None;
            DestroyReasons = 0;
            SyncElements.Clear();
        }
    }

    internal static class GameStateSyncDataPool
    {
        // Bound retained memory after an unusually large population/visibility spike.
        private const int MaxRetained = 16384;
        private static readonly Stack<GameStateSyncData> Pool = new Stack<GameStateSyncData>();

        public static GameStateSyncData Rent()
        {
            return Pool.Count > 0 ? Pool.Pop() : new GameStateSyncData();
        }

        public static void Return(GameStateSyncData value)
        {
            if (value == null)
                return;
            value.Reset();
            if (Pool.Count < MaxRetained)
                Pool.Push(value);
        }
    }
}
