using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using Game.Shared.Protocol;
using LiteNetLib;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<PlayerItemsResponseMessage> PlayerItemsChangedReceived;

        private readonly struct PendingPlayerItemsChange
        {
            public readonly PlayerSessionHandle Handle;
            public readonly PlayerItemsDeltaMessage Message;

            public PendingPlayerItemsChange(PlayerSessionHandle handle, PlayerItemsDeltaMessage message)
            {
                Handle = handle;
                Message = message;
            }
        }

        private readonly ConcurrentQueue<PendingPlayerItemsChange> _pendingPlayerItemChanges =
            new ConcurrentQueue<PendingPlayerItemsChange>();
        private bool _playerItemsReconciliationPending;
        private bool _playerItemsSnapshotRequestInFlight;
        private bool _playerItemsMutationRequestInFlight;
        private PlayerItemsResponseMessage _latestPlayerItems;
        public PlayerItemsResponseMessage LatestPlayerItems => _latestPlayerItems;
        public bool HasPlayerItemsCache => _latestPlayerItems.inventoryCapacity > 0;
        public bool PlayerItemsSnapshotRequestInFlight => _playerItemsSnapshotRequestInFlight;

        private void RegisterPlayerItemMessages()
        {
            RegisterRequestToServer<PlayerItemsSnapshotRequestMessage, PlayerItemsResponseMessage>(PlayerItemRequestTypes.Snapshot, HandlePlayerItemsSnapshotRequest);
            RegisterRequestToServer<MoveInventoryRequestMessage, PlayerItemMutationResponseMessage>(PlayerItemRequestTypes.MoveInventory, HandleMoveInventoryRequest);
            RegisterRequestToServer<EquipItemRequestMessage, PlayerItemMutationResponseMessage>(PlayerItemRequestTypes.Equip, HandleEquipItemRequest);
            RegisterRequestToServer<UnequipItemRequestMessage, PlayerItemMutationResponseMessage>(PlayerItemRequestTypes.Unequip, HandleUnequipItemRequest);
            RegisterRequestToServer<UseItemRequestMessage, PlayerItemMutationResponseMessage>(PlayerItemRequestTypes.Use, HandleUseItemRequest);

            // Drop is authoritative only on the standalone GameServer. Register the
            // client response contract without installing a Unity/Host server handler.
            Client.RegisterResponseHandler<DropItemRequestMessage, PlayerItemMutationResponseMessage>(PlayerItemRequestTypes.Drop);

            RegisterClientMessage(PlayerItemMessageTypes.Snapshot, HandlePlayerItemsSnapshotPush);
            RegisterClientMessage(PlayerItemMessageTypes.Delta, HandlePlayerItemsDelta);
        }

        public UniTask<PlayerItemsResponseMessage> RequestPlayerItemsAsync(int millisecondsTimeout = 10000) =>
            RequestPlayerItemsAsync(false, millisecondsTimeout);

        public UniTask<PlayerItemsResponseMessage> RequestPlayerItemsAsync(
            bool forceRefresh,
            int millisecondsTimeout = 10000)
        {
            if (!forceRefresh && _latestPlayerItems.inventoryCapacity > 0)
                return UniTask.FromResult(_latestPlayerItems);

            return SendPlayerItemsSnapshotRequestAsync(millisecondsTimeout);
        }

        public UniTask<PlayerItemMutationResponseMessage> RequestMoveInventoryAsync(int fromIndex, int toIndex, int millisecondsTimeout = 10000)
        {
            // Normal-client hygiene only. The standalone GameServer still validates every
            // mutation independently, including requests sent by modified clients.
            if (fromIndex == toIndex)
                return LocalItemFailure(PlayerItemOperationStatus.InvalidSlot, "source and destination are the same");
            if (HasPlayerItemsCache)
            {
                if (!IsCachedInventoryIndexValid(fromIndex) || !IsCachedInventoryIndexValid(toIndex))
                    return LocalItemFailure(PlayerItemOperationStatus.InvalidSlot, "inventory slot is outside the known capacity");
                if (!TryGetCachedInventoryItem(fromIndex, out _))
                    return LocalItemFailure(PlayerItemOperationStatus.ItemUnavailable, "source inventory slot is empty locally");
            }

            return SendPlayerItemMutationRequestAsync(
                PlayerItemRequestTypes.MoveInventory,
                new MoveInventoryRequestMessage { fromIndex = fromIndex, toIndex = toIndex },
                millisecondsTimeout);
        }

        public UniTask<PlayerItemMutationResponseMessage> RequestEquipItemAsync(
            int inventoryIndex,
            string equipmentSlotId,
            int millisecondsTimeout = 10000)
        {
            ushort slotDataId = 0;
            if (!string.IsNullOrWhiteSpace(equipmentSlotId) &&
                PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(
                    equipmentSlotId,
                    out GameplayEquipmentSlotReferenceWire slot))
                slotDataId = slot.dataId;

            if (slotDataId == 0)
                return UniTask.FromResult(PlayerItemMutationResponseMessage.Failed(
                    (byte)PlayerItemOperationStatus.EquipmentSlotInvalid,
                    "equipment slot is not in the client content catalog"));

            if (HasPlayerItemsCache)
            {
                if (!IsCachedInventoryIndexValid(inventoryIndex))
                    return LocalItemFailure(PlayerItemOperationStatus.InvalidSlot, "inventory slot is outside the known capacity");
                if (!TryGetCachedInventoryItem(inventoryIndex, out PlayerItemWire cachedItem))
                    return LocalItemFailure(PlayerItemOperationStatus.ItemUnavailable, "inventory slot is empty locally");
                if (PlayerGameplaySettingsRuntime.TryGetItem(cachedItem.itemDataId, out GameplayItemReferenceWire definition) &&
                    !ContainsDataId(definition.allowedSlotDataIds, slotDataId))
                {
                    return LocalItemFailure(PlayerItemOperationStatus.EquipmentNotAllowed, "item cannot use that equipment slot");
                }
            }

            return SendPlayerItemMutationRequestAsync(
                PlayerItemRequestTypes.Equip,
                new EquipItemRequestMessage
                {
                    inventoryIndex = inventoryIndex,
                    equipmentSlotDataId = slotDataId,
                    equipmentSlotId = equipmentSlotId,
                },
                millisecondsTimeout);
        }

        public UniTask<PlayerItemMutationResponseMessage> RequestUnequipItemAsync(
            string equipmentSlotId,
            int preferredInventoryIndex = -1,
            int millisecondsTimeout = 10000)
        {
            ushort slotDataId = 0;
            if (!string.IsNullOrWhiteSpace(equipmentSlotId) &&
                PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(
                    equipmentSlotId,
                    out GameplayEquipmentSlotReferenceWire slot))
                slotDataId = slot.dataId;

            if (slotDataId == 0)
                return UniTask.FromResult(PlayerItemMutationResponseMessage.Failed(
                    (byte)PlayerItemOperationStatus.EquipmentSlotInvalid,
                    "equipment slot is not in the client content catalog"));

            if (HasPlayerItemsCache)
            {
                if (preferredInventoryIndex >= 0 && !IsCachedInventoryIndexValid(preferredInventoryIndex))
                    return LocalItemFailure(PlayerItemOperationStatus.InvalidSlot, "preferred inventory slot is outside the known capacity");
                if (TryGetCachedEquipmentSlot(equipmentSlotId, out EquipmentSlotWire cachedSlot) && !cachedSlot.hasItem)
                    return LocalItemFailure(PlayerItemOperationStatus.ItemUnavailable, "equipment slot is already empty locally");
            }

            return SendPlayerItemMutationRequestAsync(
                PlayerItemRequestTypes.Unequip,
                new UnequipItemRequestMessage
                {
                    equipmentSlotDataId = slotDataId,
                    equipmentSlotId = equipmentSlotId,
                    preferredInventoryIndex = preferredInventoryIndex,
                },
                millisecondsTimeout);
        }

        public UniTask<PlayerItemMutationResponseMessage> RequestUseItemAsync(int inventoryIndex, int millisecondsTimeout = 10000)
        {
            if (HasPlayerItemsCache)
            {
                if (!IsCachedInventoryIndexValid(inventoryIndex))
                    return LocalItemFailure(PlayerItemOperationStatus.InvalidSlot, "inventory slot is outside the known capacity");
                if (!TryGetCachedInventoryItem(inventoryIndex, out PlayerItemWire cachedItem))
                    return LocalItemFailure(PlayerItemOperationStatus.ItemUnavailable, "inventory slot is empty locally");
                if (PlayerGameplaySettingsRuntime.TryGetItem(cachedItem.itemDataId, out GameplayItemReferenceWire definition) && !definition.canUse)
                    return LocalItemFailure(PlayerItemOperationStatus.ItemNotUsable, "item is not usable");
            }

            return SendPlayerItemMutationRequestAsync(
                PlayerItemRequestTypes.Use,
                new UseItemRequestMessage { inventoryIndex = inventoryIndex },
                millisecondsTimeout);
        }

        public UniTask<PlayerItemMutationResponseMessage> RequestDropItemAsync(int inventoryIndex, int quantity, int millisecondsTimeout = 10000)
        {
            if (quantity < 1)
                return LocalItemFailure(PlayerItemOperationStatus.ItemUnavailable, "drop quantity must be positive");
            if (HasPlayerItemsCache)
            {
                if (!IsCachedInventoryIndexValid(inventoryIndex))
                    return LocalItemFailure(PlayerItemOperationStatus.InvalidSlot, "inventory slot is outside the known capacity");
                if (!TryGetCachedInventoryItem(inventoryIndex, out PlayerItemWire cachedItem))
                    return LocalItemFailure(PlayerItemOperationStatus.ItemUnavailable, "inventory slot is empty locally");
                if (quantity > cachedItem.quantity)
                    return LocalItemFailure(PlayerItemOperationStatus.ItemUnavailable, "drop quantity exceeds the locally known stack");
            }

            return SendPlayerItemMutationRequestAsync(
                PlayerItemRequestTypes.Drop,
                new DropItemRequestMessage { inventoryIndex = inventoryIndex, quantity = quantity },
                millisecondsTimeout);
        }

        private UniTask<PlayerItemMutationResponseMessage> LocalItemFailure(PlayerItemOperationStatus status, string error) =>
            UniTask.FromResult(PlayerItemMutationResponseMessage.Failed((byte)status, error));

        private bool IsCachedInventoryIndexValid(int index) =>
            _latestPlayerItems.inventoryCapacity > 0 && index >= 0 && index < _latestPlayerItems.inventoryCapacity;

        private bool TryGetCachedInventoryItem(int inventoryIndex, out PlayerItemWire item)
        {
            PlayerItemWire[] inventory = _latestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < inventory.Length; ++i)
            {
                if (inventory[i].inventorySlot != inventoryIndex)
                    continue;
                item = inventory[i];
                return item.itemInstanceId > 0;
            }
            item = default;
            return false;
        }

        private bool TryGetCachedEquipmentSlot(string slotId, out EquipmentSlotWire slot)
        {
            EquipmentSlotWire[] equipment = _latestPlayerItems.equipment ?? Array.Empty<EquipmentSlotWire>();
            for (int i = 0; i < equipment.Length; ++i)
            {
                if (!string.Equals(equipment[i].slotId, slotId, StringComparison.Ordinal))
                    continue;
                slot = equipment[i];
                return true;
            }
            slot = default;
            return false;
        }

        private static bool ContainsDataId(ushort[] values, ushort value)
        {
            if (values == null || value == 0)
                return false;
            for (int i = 0; i < values.Length; ++i)
                if (values[i] == value)
                    return true;
            return false;
        }

        private async UniTask<PlayerItemsResponseMessage> SendPlayerItemsSnapshotRequestAsync(int millisecondsTimeout)
        {
            if (!IsClientConnected)
                return PlayerItemsResponseMessage.Failed((byte)PlayerItemOperationStatus.SessionUnavailable, "client is not connected");
            if (_playerItemsSnapshotRequestInFlight)
                return PlayerItemsResponseMessage.Failed((byte)PlayerItemOperationStatus.PersistenceRejected, "player item snapshot request is already pending locally");

            _playerItemsSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<PlayerItemsResponseMessage> response =
                    await ClientSendRequestAsync<PlayerItemsSnapshotRequestMessage, PlayerItemsResponseMessage>(
                        PlayerItemRequestTypes.Snapshot,
                        HasPlayerItemsCache
                            ? new PlayerItemsSnapshotRequestMessage
                            {
                                knownContentRevision = _latestPlayerItems.contentRevision,
                                knownInventoryRevision = _latestPlayerItems.inventoryRevision,
                                knownEquipmentRevision = _latestPlayerItems.equipmentRevision,
                            }
                            : new PlayerItemsSnapshotRequestMessage(),
                        millisecondsTimeout);
                PlayerItemsResponseMessage result = response.IsSuccess
                    ? response.Response
                    : PlayerItemsResponseMessage.Failed((byte)PlayerItemOperationStatus.PersistenceRejected, $"request failed: {response.ResponseCode}");
                if (result.IsNotModified && HasPlayerItemsCache)
                    return _latestPlayerItems;
                if (result.inventoryCapacity > 0)
                    ApplyLatestPlayerItems(result, notify: true);
                return result;
            }
            finally
            {
                _playerItemsSnapshotRequestInFlight = false;
            }
        }

        private async UniTask<PlayerItemMutationResponseMessage> SendPlayerItemMutationRequestAsync<TRequest>(
            ushort requestType,
            TRequest request,
            int millisecondsTimeout)
            where TRequest : struct, LiteNetLib.Utils.INetSerializable
        {
            if (!IsClientConnected)
                return PlayerItemMutationResponseMessage.Failed((byte)PlayerItemOperationStatus.SessionUnavailable, "client is not connected");
            if (_playerItemsMutationRequestInFlight)
                return PlayerItemMutationResponseMessage.Failed((byte)PlayerItemOperationStatus.PersistenceRejected, "player item mutation request is already pending locally");

            _playerItemsMutationRequestInFlight = true;
            try
            {
                AsyncResponseData<PlayerItemMutationResponseMessage> response =
                    await ClientSendRequestAsync<TRequest, PlayerItemMutationResponseMessage>(requestType, request, millisecondsTimeout);
                return response.IsSuccess
                    ? response.Response
                    : PlayerItemMutationResponseMessage.Failed(
                        (byte)PlayerItemOperationStatus.PersistenceRejected,
                        $"request failed: {response.ResponseCode}");
            }
            finally
            {
                _playerItemsMutationRequestInFlight = false;
            }
        }

        private bool TryGetInWorldPlayerSession(long connectionId, out PlayerSessionHandle handle)
        {
            if (!TryGetExactCharacterSession(connectionId, out handle, out _))
                return false;

            // Keep Domain runtime details behind the Unity integration boundary.
            return _characterSessionRuntimeHost.HasInWorldRuntime(handle);
        }

        private UniTaskVoid HandlePlayerItemsSnapshotRequest(RequestHandlerData handler, PlayerItemsSnapshotRequestMessage request, RequestProceedResultDelegate<PlayerItemsResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle))
            {
                result(AckResponseCode.Success, PlayerItemsResponseMessage.Failed((byte)PlayerItemOperationStatus.CharacterUnavailable, "character is not in world"));
                return default;
            }

            PlayerItemsSnapshot snapshot = _characterSessionRuntimeHost.GetPlayerItems(handle);
            if (snapshot == null)
            {
                result(AckResponseCode.Success, PlayerItemsResponseMessage.Failed((byte)PlayerItemOperationStatus.CharacterUnavailable, "player item state is unavailable"));
                return default;
            }

            result(AckResponseCode.Success, ToWire(PlayerItemOperationResult.Succeeded(snapshot)));
            return default;
        }

        private async UniTaskVoid HandleMoveInventoryRequest(RequestHandlerData handler, MoveInventoryRequestMessage request, RequestProceedResultDelegate<PlayerItemMutationResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle))
            { result(AckResponseCode.Success, PlayerItemMutationResponseMessage.Failed((byte)PlayerItemOperationStatus.CharacterUnavailable, "character is not in world")); return; }
            PlayerItemOperationResult operation = await _characterSessionRuntimeHost.MoveInventoryAsync(handle, request.fromIndex, request.toIndex, CancellationToken.None);
            if (!TryGetExactCharacterSession(handler.ConnectionId, handle, out _)) return;
            result(AckResponseCode.Success, ToMutationAck(operation));
        }

        private async UniTaskVoid HandleEquipItemRequest(RequestHandlerData handler, EquipItemRequestMessage request, RequestProceedResultDelegate<PlayerItemMutationResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle))
            { result(AckResponseCode.Success, PlayerItemMutationResponseMessage.Failed((byte)PlayerItemOperationStatus.CharacterUnavailable, "character is not in world")); return; }
            if (_characterSessionRuntimeHost?.Content == null ||
                !_characterSessionRuntimeHost.Content.TryGetEquipmentSlot(
                    request.equipmentSlotDataId,
                    out Game.Shared.Content.EquipmentSlotDefinition slot))
            {
                result(AckResponseCode.Success, PlayerItemMutationResponseMessage.Failed(
                    (byte)PlayerItemOperationStatus.EquipmentSlotInvalid,
                    "equipment slot is invalid"));
                return;
            }

            PlayerItemOperationResult operation = await _characterSessionRuntimeHost.EquipItemAsync(
                handle,
                request.inventoryIndex,
                slot.slotId,
                CancellationToken.None);
            if (!TryGetExactCharacterSession(handler.ConnectionId, handle, out _)) return;
            result(AckResponseCode.Success, ToMutationAck(operation));
        }

        private async UniTaskVoid HandleUnequipItemRequest(RequestHandlerData handler, UnequipItemRequestMessage request, RequestProceedResultDelegate<PlayerItemMutationResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle))
            { result(AckResponseCode.Success, PlayerItemMutationResponseMessage.Failed((byte)PlayerItemOperationStatus.CharacterUnavailable, "character is not in world")); return; }
            if (_characterSessionRuntimeHost?.Content == null ||
                !_characterSessionRuntimeHost.Content.TryGetEquipmentSlot(
                    request.equipmentSlotDataId,
                    out Game.Shared.Content.EquipmentSlotDefinition slot))
            {
                result(AckResponseCode.Success, PlayerItemMutationResponseMessage.Failed(
                    (byte)PlayerItemOperationStatus.EquipmentSlotInvalid,
                    "equipment slot is invalid"));
                return;
            }

            PlayerItemOperationResult operation = await _characterSessionRuntimeHost.UnequipItemAsync(
                handle,
                slot.slotId,
                request.preferredInventoryIndex,
                CancellationToken.None);
            if (!TryGetExactCharacterSession(handler.ConnectionId, handle, out _)) return;
            result(AckResponseCode.Success, ToMutationAck(operation));
        }

        private async UniTaskVoid HandleUseItemRequest(RequestHandlerData handler, UseItemRequestMessage request, RequestProceedResultDelegate<PlayerItemMutationResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle))
            { result(AckResponseCode.Success, PlayerItemMutationResponseMessage.Failed((byte)PlayerItemOperationStatus.CharacterUnavailable, "character is not in world")); return; }
            PlayerItemOperationResult operation = await _characterSessionRuntimeHost.UseItemAsync(handle, request.inventoryIndex, CancellationToken.None);
            if (!TryGetExactCharacterSession(handler.ConnectionId, handle, out _)) return;
            result(AckResponseCode.Success, ToMutationAck(operation));
        }

        private void HandlePlayerItemsSnapshotPush(MessageHandlerData handler)
        {
            PlayerItemsResponseMessage snapshot = handler.ReadMessage<PlayerItemsResponseMessage>();
            if (!snapshot.success || snapshot.IsNotModified || snapshot.inventoryCapacity <= 0)
                return;
            ApplyLatestPlayerItems(snapshot, notify: true);
        }

        private void HandlePlayerItemsDelta(MessageHandlerData handler)
        {
            PlayerItemsDeltaMessage delta = handler.ReadMessage<PlayerItemsDeltaMessage>();

            // Item mutations are pushed as ReliableOrdered revisioned deltas. A client
            // without a baseline, or one that observes a revision gap, performs one
            // snapshot reconciliation and then returns to push-only operation.
            if (_latestPlayerItems.inventoryCapacity <= 0)
            {
                ReconcilePlayerItemsAsync().Forget();
                return;
            }

            if (delta.inventoryRevision < _latestPlayerItems.inventoryRevision ||
                delta.equipmentRevision < _latestPlayerItems.equipmentRevision)
                return;
            if (delta.inventoryRevision == _latestPlayerItems.inventoryRevision &&
                delta.equipmentRevision == _latestPlayerItems.equipmentRevision)
                return;

            bool inventoryRevisionValid =
                delta.inventoryRevision == _latestPlayerItems.inventoryRevision ||
                (_latestPlayerItems.inventoryRevision < long.MaxValue &&
                 delta.inventoryRevision == _latestPlayerItems.inventoryRevision + 1);
            bool equipmentRevisionValid =
                delta.equipmentRevision == _latestPlayerItems.equipmentRevision ||
                (_latestPlayerItems.equipmentRevision < long.MaxValue &&
                 delta.equipmentRevision == _latestPlayerItems.equipmentRevision + 1);
            if (!inventoryRevisionValid || !equipmentRevisionValid || !ApplyDeltaToLatestPlayerItems(delta))
            {
                ReconcilePlayerItemsAsync().Forget();
                return;
            }

            PlayerItemsChangedReceived?.Invoke(_latestPlayerItems);
        }

        private bool ApplyDeltaToLatestPlayerItems(PlayerItemsDeltaMessage delta)
        {
            var inventory = new List<PlayerItemWire>(_latestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>());
            InventorySlotDeltaWire[] inventoryChanges = delta.inventoryChanges ?? Array.Empty<InventorySlotDeltaWire>();
            for (int i = 0; i < inventoryChanges.Length; ++i)
            {
                InventorySlotDeltaWire change = inventoryChanges[i];
                if (change.inventorySlot < 0 || change.inventorySlot >= _latestPlayerItems.inventoryCapacity)
                    return false;
                int index = FindInventorySlot(inventory, change.inventorySlot);
                if (!change.hasItem)
                {
                    if (index >= 0) inventory.RemoveAt(index);
                    continue;
                }
                if (change.item.inventorySlot != change.inventorySlot || change.item.itemInstanceId <= 0)
                    return false;
                if (index >= 0) inventory[index] = change.item;
                else inventory.Add(change.item);
            }
            inventory.Sort((a, b) => a.inventorySlot.CompareTo(b.inventorySlot));

            EquipmentSlotWire[] equipment = _latestPlayerItems.equipment ?? Array.Empty<EquipmentSlotWire>();
            EquipmentSlotWire[] nextEquipment = (EquipmentSlotWire[])equipment.Clone();
            EquipmentSlotDeltaWire[] equipmentChanges = delta.equipmentChanges ?? Array.Empty<EquipmentSlotDeltaWire>();
            for (int i = 0; i < equipmentChanges.Length; ++i)
            {
                EquipmentSlotDeltaWire change = equipmentChanges[i];
                int index = FindEquipmentSlot(nextEquipment, change.slotId);
                if (index < 0)
                    return false;
                EquipmentSlotWire slot = nextEquipment[index];
                slot.hasItem = change.hasItem;
                slot.item = change.hasItem ? change.item : default;
                if (change.hasItem &&
                    (!string.Equals(change.item.equipmentSlotId, change.slotId, StringComparison.Ordinal) ||
                     change.item.itemInstanceId <= 0))
                {
                    return false;
                }
                nextEquipment[index] = slot;
            }

            PlayerItemsDeltaFlags flags = (PlayerItemsDeltaFlags)delta.changeMask;
            if ((flags & PlayerItemsDeltaFlags.ContentRevision) != 0)
                _latestPlayerItems.contentRevision = delta.contentRevision;
            _latestPlayerItems.inventoryRevision = delta.inventoryRevision;
            _latestPlayerItems.equipmentRevision = delta.equipmentRevision;
            if ((flags & PlayerItemsDeltaFlags.InventoryWeight) != 0)
                _latestPlayerItems.inventoryWeight = delta.inventoryWeight;
            if ((flags & PlayerItemsDeltaFlags.CombatStats) != 0)
            {
                _latestPlayerItems.armor = delta.armor;
                _latestPlayerItems.attackPower = delta.attackPower;
            }
            _latestPlayerItems.inventory = inventory.ToArray();
            _latestPlayerItems.equipment = nextEquipment;
            _latestPlayerItems.success = true;
            _latestPlayerItems.status = (byte)PlayerItemOperationStatus.Success;
            _latestPlayerItems.error = string.Empty;
            return true;
        }

        private async UniTaskVoid ReconcilePlayerItemsAsync()
        {
            if (_playerItemsReconciliationPending)
                return;

            _playerItemsReconciliationPending = true;
            try
            {
                await RequestPlayerItemsAsync(forceRefresh: true);
            }
            finally
            {
                _playerItemsReconciliationPending = false;
            }
        }

        private void ApplyLatestPlayerItems(PlayerItemsResponseMessage message, bool notify)
        {
            if (message.inventoryCapacity <= 0)
                return;
            if (_latestPlayerItems.inventoryCapacity > 0 &&
                (message.inventoryRevision < _latestPlayerItems.inventoryRevision ||
                 message.equipmentRevision < _latestPlayerItems.equipmentRevision))
                return;
            if (_latestPlayerItems.inventoryCapacity > 0 &&
                message.inventoryRevision == _latestPlayerItems.inventoryRevision &&
                message.equipmentRevision == _latestPlayerItems.equipmentRevision &&
                message.contentRevision <= _latestPlayerItems.contentRevision)
                return;

            _latestPlayerItems = message;
            if (notify)
                PlayerItemsChangedReceived?.Invoke(message);
        }

        private void RefreshPlayerItemPresentationFromGameplaySettings()
        {
            if (_latestPlayerItems.inventoryCapacity <= 0)
                return;

            bool changed = false;
            PlayerItemWire[] inventory = _latestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < inventory.Length; ++i)
            {
                PlayerItemWire item = inventory[i];
                if (HydratePlayerItemReference(ref item))
                {
                    inventory[i] = item;
                    changed = true;
                }
            }

            EquipmentSlotWire[] equipment = _latestPlayerItems.equipment ?? Array.Empty<EquipmentSlotWire>();
            for (int i = 0; i < equipment.Length; ++i)
            {
                EquipmentSlotWire slot = equipment[i];
                if (slot.slotDataId != 0 &&
                    PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(slot.slotDataId, out GameplayEquipmentSlotReferenceWire slotRef))
                {
                    string nextId = slotRef.slotId ?? string.Empty;
                    string nextName = slotRef.displayName ?? nextId;
                    if (!string.Equals(slot.slotId, nextId, StringComparison.Ordinal) ||
                        !string.Equals(slot.displayName, nextName, StringComparison.Ordinal))
                    {
                        slot.slotId = nextId;
                        slot.displayName = nextName;
                        changed = true;
                    }
                }
                if (slot.hasItem)
                {
                    PlayerItemWire item = slot.item;
                    if (HydratePlayerItemReference(ref item))
                    {
                        slot.item = item;
                        changed = true;
                    }
                }
                equipment[i] = slot;
            }

            if (!changed)
                return;

            _latestPlayerItems.inventory = inventory;
            _latestPlayerItems.equipment = equipment;
            PlayerItemsChangedReceived?.Invoke(_latestPlayerItems);
        }

        private static bool HydratePlayerItemReference(ref PlayerItemWire item)
        {
            if (item.itemDataId == 0 ||
                !PlayerGameplaySettingsRuntime.TryGetItem(item.itemDataId, out GameplayItemReferenceWire definition))
                return false;

            string definitionId = definition.definitionId ?? string.Empty;
            string displayName = definition.displayName ?? definitionId;
            string[] slots = PlayerGameplaySettingsRuntime.ResolveEquipmentSlotNames(definition.allowedSlotDataIds);
            bool changed =
                !string.Equals(item.definitionId, definitionId, StringComparison.Ordinal) ||
                !string.Equals(item.displayName, displayName, StringComparison.Ordinal) ||
                item.maxDurability != definition.maxDurability ||
                item.unitWeight != definition.unitWeight ||
                item.canUse != definition.canUse ||
                item.consumeQuantity != definition.consumeQuantity ||
                !SameStrings(item.allowedEquipmentSlots, slots);

            item.definitionId = definitionId;
            item.displayName = displayName;
            item.maxDurability = definition.maxDurability;
            item.unitWeight = definition.unitWeight;
            item.canUse = definition.canUse;
            item.consumeQuantity = definition.consumeQuantity;
            item.allowedEquipmentSlots = slots;
            return changed;
        }

        private static bool SameStrings(string[] left, string[] right)
        {
            left ??= Array.Empty<string>();
            right ??= Array.Empty<string>();
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; ++i)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private void HandleAuthoritativePlayerItemsChanged(
            PlayerSessionHandle handle,
            PlayerItemsSnapshot previous,
            PlayerItemsSnapshot current)
        {
            if (!handle.IsValid || previous == null || current == null)
                return;
            _pendingPlayerItemChanges.Enqueue(new PendingPlayerItemsChange(
                handle,
                ToDelta(previous, current)));
        }

        private void FlushPendingPlayerItemChanges(int maxPerTick = 256)
        {
            if (!IsServer || maxPerTick <= 0)
                return;
            int sent = 0;
            while (sent < maxPerTick && _pendingPlayerItemChanges.TryDequeue(out PendingPlayerItemsChange pending))
            {
                PlayerSessionHandle handle = pending.Handle;
                if (!handle.IsValid || !TryGetExactCharacterSession(handle.Connection.Value, handle, out _))
                    continue;
                ServerSendPacket(
                    handle.Connection.Value,
                    0,
                    DeliveryMethod.ReliableOrdered,
                    PlayerItemMessageTypes.Delta,
                    pending.Message);
                sent++;
            }
        }

        private static PlayerItemsDeltaMessage ToDelta(PlayerItemsSnapshot previous, PlayerItemsSnapshot current)
        {
            var beforeInventory = new Dictionary<int, PlayerItemView>();
            PlayerItemView[] oldInventory = previous.inventory ?? Array.Empty<PlayerItemView>();
            for (int i = 0; i < oldInventory.Length; ++i)
                if (oldInventory[i] != null && oldInventory[i].inventorySlot >= 0)
                    beforeInventory[oldInventory[i].inventorySlot] = oldInventory[i];

            var afterInventory = new Dictionary<int, PlayerItemView>();
            PlayerItemView[] newInventory = current.inventory ?? Array.Empty<PlayerItemView>();
            for (int i = 0; i < newInventory.Length; ++i)
                if (newInventory[i] != null && newInventory[i].inventorySlot >= 0)
                    afterInventory[newInventory[i].inventorySlot] = newInventory[i];

            var changedSlots = new HashSet<int>(beforeInventory.Keys);
            changedSlots.UnionWith(afterInventory.Keys);
            var orderedSlots = new List<int>(changedSlots);
            orderedSlots.Sort();
            var inventoryChanges = new List<InventorySlotDeltaWire>();
            for (int i = 0; i < orderedSlots.Count; ++i)
            {
                int slot = orderedSlots[i];
                beforeInventory.TryGetValue(slot, out PlayerItemView before);
                afterInventory.TryGetValue(slot, out PlayerItemView after);
                if (SameItemView(before, after))
                    continue;
                inventoryChanges.Add(new InventorySlotDeltaWire
                {
                    inventorySlot = slot,
                    hasItem = after != null,
                    item = after == null ? default : ToWire(after),
                });
            }

            EquipmentSlotView[] oldEquipment = previous.equipment ?? Array.Empty<EquipmentSlotView>();
            EquipmentSlotView[] newEquipment = current.equipment ?? Array.Empty<EquipmentSlotView>();
            var equipmentChanges = new List<EquipmentSlotDeltaWire>();
            for (int i = 0; i < newEquipment.Length; ++i)
            {
                EquipmentSlotView afterSlot = newEquipment[i];
                if (afterSlot == null || string.IsNullOrWhiteSpace(afterSlot.slotId))
                    continue;
                EquipmentSlotView beforeSlot = FindEquipmentSlot(oldEquipment, afterSlot.slotId);
                if (beforeSlot != null && SameItemView(beforeSlot.item, afterSlot.item))
                    continue;
                equipmentChanges.Add(new EquipmentSlotDeltaWire
                {
                    slotDataId = afterSlot.slotDataId,
                    slotId = afterSlot.slotId,
                    hasItem = afterSlot.item != null,
                    item = afterSlot.item == null ? default : ToWire(afterSlot.item),
                });
            }

            PlayerItemsDeltaFlags flags = PlayerItemsDeltaFlags.None;
            if (current.contentRevision != previous.contentRevision) flags |= PlayerItemsDeltaFlags.ContentRevision;
            if (current.inventoryWeight != previous.inventoryWeight) flags |= PlayerItemsDeltaFlags.InventoryWeight;
            if (current.armor != previous.armor || current.attackPower != previous.attackPower) flags |= PlayerItemsDeltaFlags.CombatStats;

            return new PlayerItemsDeltaMessage
            {
                contentRevision = current.contentRevision,
                inventoryRevision = current.inventoryRevision,
                equipmentRevision = current.equipmentRevision,
                changeMask = (byte)flags,
                inventoryWeight = current.inventoryWeight,
                armor = current.armor,
                attackPower = current.attackPower,
                inventoryChanges = inventoryChanges.ToArray(),
                equipmentChanges = equipmentChanges.ToArray(),
            };
        }

        private static bool SameItemView(PlayerItemView a, PlayerItemView b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.inventorySlot == b.inventorySlot &&
                   string.Equals(a.equipmentSlotId ?? string.Empty, b.equipmentSlotId ?? string.Empty, StringComparison.Ordinal) &&
                   a.itemInstanceId == b.itemInstanceId &&
                   string.Equals(a.definitionId, b.definitionId, StringComparison.Ordinal) &&
                   a.quantity == b.quantity &&
                   a.durability == b.durability;
        }

        private static EquipmentSlotView FindEquipmentSlot(EquipmentSlotView[] slots, string slotId)
        {
            for (int i = 0; i < slots.Length; ++i)
                if (slots[i] != null && string.Equals(slots[i].slotId, slotId, StringComparison.Ordinal))
                    return slots[i];
            return null;
        }

        private static int FindInventorySlot(List<PlayerItemWire> items, int slot)
        {
            for (int i = 0; i < items.Count; ++i)
                if (items[i].inventorySlot == slot) return i;
            return -1;
        }

        private static int FindEquipmentSlot(EquipmentSlotWire[] slots, string slotId)
        {
            for (int i = 0; i < slots.Length; ++i)
                if (string.Equals(slots[i].slotId, slotId, StringComparison.Ordinal)) return i;
            return -1;
        }

        private void ClearPendingPlayerItemChanges()
        {
            while (_pendingPlayerItemChanges.TryDequeue(out _)) { }
        }

        private void ResetClientPlayerItems()
        {
            _latestPlayerItems = default;
            _playerItemsReconciliationPending = false;
            _playerItemsSnapshotRequestInFlight = false;
            _playerItemsMutationRequestInFlight = false;
        }

        private static PlayerItemMutationResponseMessage ToMutationAck(PlayerItemOperationResult operation) =>
            new PlayerItemMutationResponseMessage
            {
                success = operation.Success,
                status = (byte)operation.Status,
                error = operation.Error ?? string.Empty,
            };

        private static PlayerItemsResponseMessage ToWire(PlayerItemOperationResult operation)
        {
            PlayerItemsSnapshot snapshot = operation.Snapshot;
            if (snapshot == null) return PlayerItemsResponseMessage.Failed((byte)operation.Status, operation.Error);

            PlayerItemView[] sourceInv = snapshot.inventory ?? Array.Empty<PlayerItemView>();
            var inv = new PlayerItemWire[sourceInv.Length];
            for (int i = 0; i < inv.Length; ++i) inv[i] = ToWire(sourceInv[i]);

            EquipmentSlotView[] sourceEq = snapshot.equipment ?? Array.Empty<EquipmentSlotView>();
            var eq = new EquipmentSlotWire[sourceEq.Length];
            for (int i = 0; i < eq.Length; ++i)
                eq[i] = new EquipmentSlotWire { slotDataId = sourceEq[i].slotDataId, slotId = sourceEq[i].slotId, displayName = sourceEq[i].displayName, order = sourceEq[i].order, hasItem = sourceEq[i].item != null, item = sourceEq[i].item == null ? default : ToWire(sourceEq[i].item) };

            return new PlayerItemsResponseMessage
            {
                success = operation.Success, status = (byte)operation.Status, error = operation.Error,
                contentRevision = snapshot.contentRevision, inventoryRevision = snapshot.inventoryRevision, equipmentRevision = snapshot.equipmentRevision,
                inventoryCapacity = snapshot.inventoryCapacity, inventoryWeight = snapshot.inventoryWeight, armor = snapshot.armor, attackPower = snapshot.attackPower,
                inventory = inv, equipment = eq,
            };
        }

        private static PlayerItemWire ToWire(PlayerItemView item) => new PlayerItemWire
        {
            inventorySlot = item.inventorySlot,
            equipmentSlotDataId = item.equipmentSlotDataId,
            equipmentSlotId = item.equipmentSlotId,
            itemInstanceId = item.itemInstanceId,
            itemDataId = item.itemDataId,
            definitionId = item.definitionId,
            displayName = item.displayName,
            quantity = item.quantity,
            durability = item.durability,
            maxDurability = item.maxDurability,
            unitWeight = item.unitWeight,
            canUse = item.canUse,
            consumeQuantity = item.consumeQuantity,
            allowedEquipmentSlots = item.allowedEquipmentSlots,
        };
    }
}
