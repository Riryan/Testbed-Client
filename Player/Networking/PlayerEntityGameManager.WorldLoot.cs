using System;
using Cysharp.Threading.Tasks;
using Game.Shared.Interactions;
using LiteNetLibManager;
using UnityEngine;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<WorldLootResponseMessage> WorldLootSnapshotReceived;

        private bool _worldLootOpenRequestInFlight;
        private bool _worldLootTakeRequestInFlight;

        // Client-safe public-position hint for owner-only loot snapshots whose authoritative
        // loot source is runtime-only (for example a Population corpse). The hint comes from
        // the interaction target already visible to the client; the GameServer still validates
        // the real source position/range independently for every Take / Take All request.
        private bool _pendingWorldLootSourcePositionValid;
        private Vector3 _pendingWorldLootSourcePosition;
        private float _pendingWorldLootSourcePositionExpiresAt;
        private long _resolvedWorldLootSourceStableId;
        private Vector3 _resolvedWorldLootSourcePosition;

        public void HintWorldLootSourcePosition(Vector3 worldPosition)
        {
            _pendingWorldLootSourcePosition = worldPosition;
            _pendingWorldLootSourcePositionExpiresAt = Time.realtimeSinceStartup + 3f;
            _pendingWorldLootSourcePositionValid = true;
        }

        public bool TryGetWorldLootSourcePosition(long stableId, out Vector3 worldPosition)
        {
            if (stableId > 0 && stableId == _resolvedWorldLootSourceStableId)
            {
                worldPosition = _resolvedWorldLootSourcePosition;
                return true;
            }

            worldPosition = default;
            return false;
        }

        private void BindPendingWorldLootSourcePosition(long stableId)
        {
            if (stableId <= 0 || !_pendingWorldLootSourcePositionValid)
                return;

            bool fresh = Time.realtimeSinceStartup <= _pendingWorldLootSourcePositionExpiresAt;
            _pendingWorldLootSourcePositionValid = false;
            if (!fresh)
                return;

            _resolvedWorldLootSourceStableId = stableId;
            _resolvedWorldLootSourcePosition = _pendingWorldLootSourcePosition;
        }

        private void RegisterWorldLootMessages()
        {
            // SceneObject loot is authoritative only on the standalone GameServer.
            // Unity registers response contracts only.
            Client.RegisterResponseHandler<WorldLootOpenRequestMessage, WorldLootResponseMessage>(
                PlayerGameplayActionRequestTypes.WorldLootOpen);
            Client.RegisterResponseHandler<WorldLootTakeRequestMessage, WorldLootTakeResponseMessage>(
                PlayerGameplayActionRequestTypes.WorldLootTake);
            Client.RegisterResponseHandler<WorldLootTakeAllRequestMessage, WorldLootResponseMessage>(
                PlayerGameplayActionRequestTypes.WorldLootTakeAll);
            RegisterClientMessage(WorldLootMessageTypes.Snapshot, HandleWorldLootSnapshot);
        }

        public async UniTask<WorldLootResponseMessage> RequestWorldLootOpenAsync(
            long stableId,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return WorldLootResponseMessage.Failed(
                    stableId,
                    InteractionResultCode.InvalidState,
                    "client is not connected");
            if (stableId <= 0)
                return WorldLootResponseMessage.Failed(
                    stableId,
                    InteractionResultCode.InvalidTarget,
                    "world object target is invalid");
            if (_worldLootOpenRequestInFlight)
                return WorldLootResponseMessage.Failed(stableId, InteractionResultCode.Rejected, "loot open request is already pending locally");

            _worldLootOpenRequestInFlight = true;
            try
            {
                AsyncResponseData<WorldLootResponseMessage> response =
                    await ClientSendRequestAsync<WorldLootOpenRequestMessage, WorldLootResponseMessage>(
                        PlayerGameplayActionRequestTypes.WorldLootOpen,
                        new WorldLootOpenRequestMessage { stableId = stableId },
                        millisecondsTimeout);

                return response.IsSuccess
                    ? response.Response
                    : WorldLootResponseMessage.Failed(
                        stableId,
                        InteractionResultCode.Rejected,
                        $"loot request failed: {response.ResponseCode}");
            }
            finally
            {
                _worldLootOpenRequestInFlight = false;
            }
        }

        public async UniTask<WorldLootTakeResponseMessage> RequestWorldLootTakeAsync(
            long stableId,
            long lootRevision,
            int entryIndex,
            int quantity,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return WorldLootTakeResponseMessage.Failed(
                    stableId,
                    InteractionResultCode.InvalidState,
                    "client is not connected");
            if (stableId <= 0 || lootRevision <= 0 || entryIndex < 0 || quantity < 1)
                return WorldLootTakeResponseMessage.Failed(
                    stableId,
                    InteractionResultCode.InvalidTarget,
                    "loot request is invalid");
            if (_worldLootTakeRequestInFlight)
                return WorldLootTakeResponseMessage.Failed(stableId, InteractionResultCode.Rejected, "loot take request is already pending locally");

            _worldLootTakeRequestInFlight = true;
            try
            {
                AsyncResponseData<WorldLootTakeResponseMessage> response =
                    await ClientSendRequestAsync<WorldLootTakeRequestMessage, WorldLootTakeResponseMessage>(
                        PlayerGameplayActionRequestTypes.WorldLootTake,
                        new WorldLootTakeRequestMessage
                        {
                            stableId = stableId,
                            lootRevision = lootRevision,
                            entryIndex = entryIndex,
                            quantity = quantity,
                        },
                        millisecondsTimeout);

                if (!response.IsSuccess)
                    return WorldLootTakeResponseMessage.Failed(
                        stableId,
                        InteractionResultCode.Rejected,
                        $"loot take request failed: {response.ResponseCode}");

                WorldLootTakeResponseMessage result = response.Response;
                // stableId + entryIndex are request-known and intentionally omitted from the wire.
                result.stableId = stableId;
                result.entryIndex = entryIndex;
                return result;
            }
            finally
            {
                _worldLootTakeRequestInFlight = false;
            }
        }


        public async UniTask<WorldLootResponseMessage> RequestWorldLootTakeAllAsync(
            long stableId,
            long lootRevision,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected) return WorldLootResponseMessage.Failed(stableId, InteractionResultCode.InvalidState, "client is not connected");
            if (stableId <= 0 || lootRevision <= 0) return WorldLootResponseMessage.Failed(stableId, InteractionResultCode.InvalidTarget, "loot TakeAll request is invalid");
            if (_worldLootTakeRequestInFlight) return WorldLootResponseMessage.Failed(stableId, InteractionResultCode.Rejected, "loot take request is already pending locally");

            _worldLootTakeRequestInFlight = true;
            try
            {
                AsyncResponseData<WorldLootResponseMessage> response =
                    await ClientSendRequestAsync<WorldLootTakeAllRequestMessage, WorldLootResponseMessage>(
                        PlayerGameplayActionRequestTypes.WorldLootTakeAll,
                        new WorldLootTakeAllRequestMessage { stableId = stableId, lootRevision = lootRevision },
                        millisecondsTimeout);
                return response.IsSuccess
                    ? response.Response
                    : WorldLootResponseMessage.Failed(stableId, InteractionResultCode.Rejected, $"loot TakeAll request failed: {response.ResponseCode}");
            }
            finally { _worldLootTakeRequestInFlight = false; }
        }

        private void HandleWorldLootSnapshot(MessageHandlerData handler)
        {
            WorldLootResponseMessage snapshot = handler.ReadMessage<WorldLootResponseMessage>();
            if (!snapshot.success)
                return;
            BindPendingWorldLootSourcePosition(snapshot.stableId);
            WorldLootSnapshotReceived?.Invoke(snapshot);
        }

        private void ResetClientWorldLootRequests()
        {
            _worldLootOpenRequestInFlight = false;
            _worldLootTakeRequestInFlight = false;
            _pendingWorldLootSourcePositionValid = false;
            _pendingWorldLootSourcePositionExpiresAt = 0f;
            _resolvedWorldLootSourceStableId = 0;
            _resolvedWorldLootSourcePosition = default;
        }
    }
}
