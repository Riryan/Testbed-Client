using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Shared.Interactions;
using Game.Shared.WorldItems;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<WorldItemsSnapshotMessage> WorldItemsSnapshotReceived;
        public event Action<WorldItemsSnapshotMessage> WorldItemsChangedReceived;
        public event Action<WorldItemInteractionResponseMessage> WorldItemInteractionResultReceived;

        private WorldItemsSnapshotMessage _latestWorldItems;
        private bool _worldItemsReconciliationPending;
        private bool _worldItemsSnapshotRequestInFlight;
        private bool _worldItemLootRequestInFlight;
        private double _nextWorldItemLootRequestAt;
        private uint _worldItemInteractionSequence;

        public WorldItemsSnapshotMessage LatestWorldItems => _latestWorldItems;

        private void RegisterWorldItemMessages()
        {
            // Requests are owned by the standalone GameServer. Register only the client
            // response contracts here; do not install Unity/Host server handlers.
            Client.RegisterResponseHandler<WorldItemsSnapshotRequestMessage, WorldItemsSnapshotMessage>(WorldItemRequestTypes.Snapshot);
            Client.RegisterResponseHandler<WorldItemLootRequestMessage, WorldItemInteractionResponseMessage>(WorldItemRequestTypes.Loot);

            RegisterClientMessage(WorldItemMessageTypes.Snapshot, HandleWorldItemsSnapshot);
            RegisterClientMessage(WorldItemMessageTypes.Delta, HandleWorldItemDelta);
        }

        public async UniTask<WorldItemsSnapshotMessage> RequestWorldItemsAsync(int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected) return WorldItemsSnapshotMessage.Failed("client is not connected");
            if (_worldItemsSnapshotRequestInFlight)
                return _latestWorldItems.success ? _latestWorldItems : WorldItemsSnapshotMessage.Failed("world item snapshot request is already pending locally");

            _worldItemsSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<WorldItemsSnapshotMessage> response = await ClientSendRequestAsync<WorldItemsSnapshotRequestMessage, WorldItemsSnapshotMessage>(
                    WorldItemRequestTypes.Snapshot,
                    new WorldItemsSnapshotRequestMessage(),
                    millisecondsTimeout);
                WorldItemsSnapshotMessage result = response.IsSuccess ? response.Response : WorldItemsSnapshotMessage.Failed($"request failed: {response.ResponseCode}");
                if (result.success) ApplyWorldItemsSnapshot(result, notify: true);
                return result;
            }
            finally
            {
                _worldItemsSnapshotRequestInFlight = false;
            }
        }

        public async UniTask<WorldItemInteractionResponseMessage> RequestWorldItemLootAsync(long itemInstanceId, int millisecondsTimeout = 10000)
        {
            uint sequence = ++_worldItemInteractionSequence;
            if (sequence == 0) sequence = ++_worldItemInteractionSequence;
            if (!IsClientConnected)
                return new WorldItemInteractionResponseMessage { success = false, sequence = sequence, actionId = (ushort)InteractionActionId.Loot, resultCode = (byte)InteractionResultCode.InvalidState, itemInstanceId = itemInstanceId, detail = "client is not connected" };
            if (itemInstanceId <= 0)
                return new WorldItemInteractionResponseMessage { success = false, sequence = sequence, actionId = (ushort)InteractionActionId.Loot, resultCode = (byte)InteractionResultCode.InvalidTarget, itemInstanceId = itemInstanceId, detail = "world item target is invalid" };
            if (_latestWorldItems.success && !HasCachedWorldItem(itemInstanceId))
                return new WorldItemInteractionResponseMessage { success = false, sequence = sequence, actionId = (ushort)InteractionActionId.Loot, resultCode = (byte)InteractionResultCode.InvalidTarget, itemInstanceId = itemInstanceId, detail = "world item is not in the locally known AOI" };

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_worldItemLootRequestInFlight || now < _nextWorldItemLootRequestAt)
                return new WorldItemInteractionResponseMessage { success = false, sequence = sequence, actionId = (ushort)InteractionActionId.Loot, resultCode = (byte)InteractionResultCode.Rejected, itemInstanceId = itemInstanceId, detail = "world item loot request is locally pending or rate-limited" };

            _worldItemLootRequestInFlight = true;
            _nextWorldItemLootRequestAt = now + 0.10d;
            try
            {
                AsyncResponseData<WorldItemInteractionResponseMessage> response = await ClientSendRequestAsync<WorldItemLootRequestMessage, WorldItemInteractionResponseMessage>(
                    WorldItemRequestTypes.Loot,
                    new WorldItemLootRequestMessage { itemInstanceId = itemInstanceId, sequence = sequence },
                    millisecondsTimeout);
                WorldItemInteractionResponseMessage result = response.IsSuccess
                    ? response.Response
                    : new WorldItemInteractionResponseMessage { success = false, sequence = sequence, actionId = (ushort)InteractionActionId.Loot, resultCode = (byte)InteractionResultCode.Rejected, itemInstanceId = itemInstanceId, detail = $"request failed: {response.ResponseCode}" };
                // Correlated request fields are not repeated in the response payload.
                result.sequence = sequence;
                result.actionId = (ushort)InteractionActionId.Loot;
                result.itemInstanceId = itemInstanceId;
                WorldItemInteractionResultReceived?.Invoke(result);
                return result;
            }
            finally
            {
                _worldItemLootRequestInFlight = false;
            }
        }

        private bool HasCachedWorldItem(long itemInstanceId)
        {
            WorldItemWire[] items = _latestWorldItems.items ?? Array.Empty<WorldItemWire>();
            for (int i = 0; i < items.Length; ++i)
                if (items[i].itemInstanceId == itemInstanceId)
                    return true;
            return false;
        }

        private void HandleWorldItemsSnapshot(MessageHandlerData handler)
        {
            WorldItemsSnapshotMessage snapshot = handler.ReadMessage<WorldItemsSnapshotMessage>();
            if (!snapshot.success) return;
            ApplyWorldItemsSnapshot(snapshot, notify: true);
            WorldItemsSnapshotReceived?.Invoke(snapshot);
        }

        private void HandleWorldItemDelta(MessageHandlerData handler)
        {
            WorldItemDeltaMessage delta = handler.ReadMessage<WorldItemDeltaMessage>();
            if (!_latestWorldItems.success || !SameWorld(_latestWorldItems, delta.mapId, delta.instanceId))
            {
                ReconcileWorldItemsAsync().Forget();
                return;
            }
            if (delta.worldRevision <= _latestWorldItems.revision)
                return;
            if (_latestWorldItems.revision == long.MaxValue || delta.worldRevision != _latestWorldItems.revision + 1 || !ApplyWorldItemDelta(delta))
            {
                ReconcileWorldItemsAsync().Forget();
                return;
            }
            WorldItemsChangedReceived?.Invoke(_latestWorldItems);
        }

        private bool ApplyWorldItemDelta(WorldItemDeltaMessage delta)
        {
            var items = new List<WorldItemWire>(_latestWorldItems.items ?? Array.Empty<WorldItemWire>());
            int index = FindWorldItem(items, delta.itemInstanceId);
            WorldItemChangeKind kind = (WorldItemChangeKind)delta.changeKind;
            if (kind == WorldItemChangeKind.Added)
            {
                if (!delta.hasItem || delta.item.itemInstanceId != delta.itemInstanceId || !SameWorld(delta.item, delta.mapId, delta.instanceId)) return false;
                if (index >= 0) items[index] = delta.item; else items.Add(delta.item);
            }
            else if (kind == WorldItemChangeKind.Removed)
            {
                if (delta.hasItem) return false;
                if (index >= 0) items.RemoveAt(index);
            }
            else return false;

            items.Sort((a, b) => a.itemInstanceId.CompareTo(b.itemInstanceId));
            _latestWorldItems.revision = delta.worldRevision;
            _latestWorldItems.items = items.ToArray();
            _latestWorldItems.success = true;
            _latestWorldItems.error = string.Empty;
            return true;
        }

        private void RefreshWorldItemPresentationFromGameplaySettings()
        {
            if (!_latestWorldItems.success)
                return;

            WorldItemWire[] items = _latestWorldItems.items ?? Array.Empty<WorldItemWire>();
            bool changed = false;
            for (int i = 0; i < items.Length; ++i)
            {
                WorldItemWire item = items[i];
                if (item.itemDataId == 0 ||
                    !PlayerGameplaySettingsRuntime.TryGetItem(item.itemDataId, out GameplayItemReferenceWire definition))
                    continue;

                string definitionId = definition.definitionId ?? string.Empty;
                string displayName = definition.displayName ?? definitionId;
                if (string.Equals(item.definitionId, definitionId, StringComparison.Ordinal) &&
                    string.Equals(item.displayName, displayName, StringComparison.Ordinal))
                    continue;

                item.definitionId = definitionId;
                item.displayName = displayName;
                items[i] = item;
                changed = true;
            }

            if (!changed)
                return;

            _latestWorldItems.items = items;
            WorldItemsChangedReceived?.Invoke(_latestWorldItems);
        }

        private async UniTaskVoid ReconcileWorldItemsAsync()
        {
            if (_worldItemsReconciliationPending || !IsClientConnected) return;
            _worldItemsReconciliationPending = true;
            try { await RequestWorldItemsAsync(); }
            finally { _worldItemsReconciliationPending = false; }
        }

        private void ApplyWorldItemsSnapshot(WorldItemsSnapshotMessage snapshot, bool notify)
        {
            if (!snapshot.success) return;
            bool sameWorld = SameWorld(_latestWorldItems, snapshot.mapId, snapshot.instanceId);
            if (sameWorld && _latestWorldItems.success && snapshot.revision < _latestWorldItems.revision) return;
            _latestWorldItems = snapshot;
            _latestWorldItems.items ??= Array.Empty<WorldItemWire>();
            if (notify) WorldItemsChangedReceived?.Invoke(_latestWorldItems);
        }

        private static int FindWorldItem(List<WorldItemWire> items, long id)
        {
            for (int i = 0; i < items.Count; ++i) if (items[i].itemInstanceId == id) return i;
            return -1;
        }

        private void ResetClientWorldItems()
        {
            _latestWorldItems = default;
            _worldItemsReconciliationPending = false;
            _worldItemsSnapshotRequestInFlight = false;
            _worldItemLootRequestInFlight = false;
            _nextWorldItemLootRequestAt = 0d;
            _worldItemInteractionSequence = 0;
        }

        private static bool SameWorld(WorldItemsSnapshotMessage snapshot, string mapId, string instanceId) =>
            snapshot.success && string.Equals(snapshot.mapId ?? string.Empty, mapId ?? string.Empty, StringComparison.Ordinal) && string.Equals(snapshot.instanceId ?? string.Empty, instanceId ?? string.Empty, StringComparison.Ordinal);

        private static bool SameWorld(WorldItemWire item, string mapId, string instanceId) =>
            string.Equals(item.mapId ?? string.Empty, mapId ?? string.Empty, StringComparison.Ordinal) && string.Equals(item.instanceId ?? string.Empty, instanceId ?? string.Empty, StringComparison.Ordinal);
    }
}
