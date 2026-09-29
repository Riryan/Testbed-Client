using System;
using System.Collections.Concurrent;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using LiteNetLib;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<PlayerResourcesResponseMessage> PlayerResourcesSnapshotReceived;
        public event Action<PlayerResourceDeltaMessage> PlayerResourceDeltaReceived;

        private readonly struct PendingPlayerResourceDelta
        {
            public readonly PlayerSessionHandle Handle;
            public readonly PlayerResourceDeltaMessage Message;

            public PendingPlayerResourceDelta(PlayerSessionHandle handle, PlayerResourceDeltaMessage message)
            {
                Handle = handle;
                Message = message;
            }
        }

        private readonly ConcurrentQueue<PendingPlayerResourceDelta> _pendingPlayerResourceDeltas =
            new ConcurrentQueue<PendingPlayerResourceDelta>();
        private bool _resourceReconciliationPending;
        private bool _resourceSnapshotRequestInFlight;
        private PlayerResourcesResponseMessage _latestPlayerResources;
        public PlayerResourcesResponseMessage LatestPlayerResources => _latestPlayerResources;

        private void RegisterPlayerResourceMessages()
        {
            RegisterRequestToServer<PlayerResourcesSnapshotRequestMessage, PlayerResourcesResponseMessage>(
                PlayerResourceRequestTypes.Snapshot,
                HandlePlayerResourcesSnapshotRequest);
            RegisterClientMessage(PlayerResourceMessageTypes.Snapshot, HandlePlayerResourcesSnapshotPush);
            RegisterClientMessage(PlayerResourceMessageTypes.Delta, HandlePlayerResourceDelta);
        }

        public async UniTask<PlayerResourcesResponseMessage> RequestPlayerResourcesAsync(int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return PlayerResourcesResponseMessage.Failed((byte)CharacterResourceOperationStatus.SessionUnavailable, "client is not connected");
            if (_resourceSnapshotRequestInFlight)
                return _latestPlayerResources.success
                    ? _latestPlayerResources
                    : PlayerResourcesResponseMessage.Failed((byte)CharacterResourceOperationStatus.SessionUnavailable, "resource snapshot request is already pending locally");

            _resourceSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<PlayerResourcesResponseMessage> response =
                    await ClientSendRequestAsync<PlayerResourcesSnapshotRequestMessage, PlayerResourcesResponseMessage>(
                        PlayerResourceRequestTypes.Snapshot,
                        new PlayerResourcesSnapshotRequestMessage(),
                        millisecondsTimeout);

                PlayerResourcesResponseMessage result = response.IsSuccess
                    ? response.Response
                    : PlayerResourcesResponseMessage.Failed(
                        (byte)CharacterResourceOperationStatus.SessionUnavailable,
                        $"resource snapshot request failed: {response.ResponseCode}");

                if (result.success &&
                    (!_latestPlayerResources.success ||
                     result.resourceRevision >= _latestPlayerResources.resourceRevision))
                {
                    _latestPlayerResources = result;
                    PlayerResourcesSnapshotReceived?.Invoke(result);
                }
                return result;
            }
            finally
            {
                _resourceSnapshotRequestInFlight = false;
            }
        }

        private UniTaskVoid HandlePlayerResourcesSnapshotRequest(
            RequestHandlerData handler,
            PlayerResourcesSnapshotRequestMessage request,
            RequestProceedResultDelegate<PlayerResourcesResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle))
            {
                result(AckResponseCode.Success, PlayerResourcesResponseMessage.Failed(
                    (byte)CharacterResourceOperationStatus.CharacterUnavailable,
                    "character is not in world"));
                return default;
            }

            CharacterResourcesSnapshot snapshot = _characterSessionRuntimeHost.GetPlayerResources(handle);
            if (snapshot == null)
            {
                result(AckResponseCode.Success, PlayerResourcesResponseMessage.Failed(
                    (byte)CharacterResourceOperationStatus.CharacterUnavailable,
                    "character resource state is unavailable"));
                return default;
            }

            result(AckResponseCode.Success, ToWire(snapshot));
            return default;
        }

        private void HandlePlayerResourcesSnapshotPush(MessageHandlerData handler)
        {
            PlayerResourcesResponseMessage snapshot = handler.ReadMessage<PlayerResourcesResponseMessage>();
            if (!snapshot.success)
                return;
            if (_latestPlayerResources.success && snapshot.resourceRevision < _latestPlayerResources.resourceRevision)
                return;
            _latestPlayerResources = snapshot;
            _resourceReconciliationPending = false;
            PlayerResourcesSnapshotReceived?.Invoke(snapshot);
        }

        private void HandlePlayerResourceDelta(MessageHandlerData handler)
        {
            PlayerResourceDeltaMessage delta = handler.ReadMessage<PlayerResourceDeltaMessage>();

            // ReliableOrdered delivery should be contiguous, but revisions make the
            // recovery rule explicit. Never apply an older/duplicate delta. A gap triggers
            // one snapshot reconciliation request; there is no periodic resource polling.
            if (!_latestPlayerResources.success)
            {
                ReconcilePlayerResourcesAsync().Forget();
                return;
            }
            if (delta.resourceRevision <= _latestPlayerResources.resourceRevision)
                return;
            if (delta.resourceRevision != _latestPlayerResources.resourceRevision + 1)
            {
                ReconcilePlayerResourcesAsync().Forget();
                return;
            }

            // previous/minimum are intentionally omitted from the wire. Rehydrate them
            // from the revisioned owner cache before applying/notifying presentation.
            CharacterResourceWire[] cachedResources = _latestPlayerResources.resources ?? Array.Empty<CharacterResourceWire>();
            bool foundCachedResource = false;
            for (int i = 0; i < cachedResources.Length; ++i)
            {
                if (cachedResources[i].id != delta.id)
                    continue;
                delta.previous = cachedResources[i].current;
                delta.minimum = cachedResources[i].minimum;
                foundCachedResource = true;
                break;
            }
            if (!foundCachedResource)
            {
                ReconcilePlayerResourcesAsync().Forget();
                return;
            }

            ApplyDeltaToLatestSnapshot(delta);
            PlayerResourceDeltaReceived?.Invoke(delta);
        }

        private void ApplyDeltaToLatestSnapshot(PlayerResourceDeltaMessage delta)
        {
            CharacterResourceWire[] resources = _latestPlayerResources.resources ?? Array.Empty<CharacterResourceWire>();
            for (int i = 0; i < resources.Length; ++i)
            {
                if (resources[i].id != delta.id)
                    continue;
                resources[i].current = delta.current;
                resources[i].minimum = delta.minimum;
                resources[i].maximum = delta.maximum;
                _latestPlayerResources.resources = resources;
                _latestPlayerResources.resourceRevision = delta.resourceRevision;
                _latestPlayerResources.success = true;
                return;
            }

            // A delta for a resource absent from the baseline means content/state shape
            // changed. Reconcile once rather than guessing a partial definition.
            ReconcilePlayerResourcesAsync().Forget();
        }

        private bool CanPredictResourceSpend(CharacterResourceId id, int amount)
        {
            if (amount <= 0)
                return true;
            CharacterResourceWire[] resources = _latestPlayerResources.resources ?? Array.Empty<CharacterResourceWire>();
            for (int i = 0; i < resources.Length; ++i)
                if (resources[i].id == (ushort)id)
                    return resources[i].current - amount >= resources[i].minimum;
            return true;
        }

        private void PredictResourceSpend(CharacterResourceId id, int amount)
        {
            if (amount <= 0 || !_latestPlayerResources.success)
                return;
            CharacterResourceWire[] resources = _latestPlayerResources.resources ?? Array.Empty<CharacterResourceWire>();
            for (int i = 0; i < resources.Length; ++i)
            {
                if (resources[i].id != (ushort)id)
                    continue;
                resources[i].current = Math.Max(resources[i].minimum, resources[i].current - amount);
                _latestPlayerResources.resources = resources;
                PlayerResourcesSnapshotReceived?.Invoke(_latestPlayerResources);
                return;
            }
        }

        private async UniTaskVoid ReconcilePlayerResourcesAsync()
        {
            if (_resourceReconciliationPending)
                return;

            _resourceReconciliationPending = true;
            try
            {
                await RequestPlayerResourcesAsync();
            }
            finally
            {
                _resourceReconciliationPending = false;
            }
        }

        private void HandleAuthoritativeResourceChanged(
            PlayerSessionHandle handle,
            CharacterResourceChangeView change)
        {
            if (!handle.IsValid)
                return;

            // Resource events can originate after backend/content work completes on a
            // worker thread. Queue only actual changes; the authoritative server tick
            // flushes queued deltas so LiteNetLib is never called from that worker.
            _pendingPlayerResourceDeltas.Enqueue(new PendingPlayerResourceDelta(
                handle,
                new PlayerResourceDeltaMessage
                {
                    resourceRevision = change.resourceRevision,
                    id = (ushort)change.id,
                    previous = change.previous,
                    current = change.current,
                    minimum = change.minimum,
                    maximum = change.maximum,
                    reason = (byte)change.reason,
                }));
        }

        private void FlushPendingPlayerResourceDeltas(int maxPerTick = 512)
        {
            if (!IsServer || maxPerTick <= 0)
                return;

            int sent = 0;
            while (sent < maxPerTick &&
                   _pendingPlayerResourceDeltas.TryDequeue(out PendingPlayerResourceDelta pending))
            {
                PlayerSessionHandle handle = pending.Handle;
                if (!handle.IsValid ||
                    !TryGetExactCharacterSession(handle.Connection.Value, handle, out _))
                    continue;

                ServerSendPacket(
                    handle.Connection.Value,
                    0,
                    DeliveryMethod.ReliableOrdered,
                    PlayerResourceMessageTypes.Delta,
                    pending.Message);
                sent++;
            }
        }

        private static PlayerResourcesResponseMessage ToWire(CharacterResourcesSnapshot snapshot)
        {
            CharacterResourceView[] source = snapshot.resources ?? Array.Empty<CharacterResourceView>();
            var resources = new CharacterResourceWire[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                resources[i] = new CharacterResourceWire
                {
                    id = (ushort)source[i].id,
                    displayName = source[i].displayName,
                    current = source[i].current,
                    minimum = source[i].minimum,
                    maximum = source[i].maximum,
                    replication = (byte)source[i].replication,
                };
            }

            return new PlayerResourcesResponseMessage
            {
                success = true,
                status = (byte)CharacterResourceOperationStatus.Success,
                error = string.Empty,
                contentRevision = snapshot.contentRevision,
                resourceRevision = snapshot.resourceRevision,
                resources = resources,
            };
        }

        private void ClearPendingPlayerResourceDeltas()
        {
            while (_pendingPlayerResourceDeltas.TryDequeue(out _)) { }
        }

        private void ResetClientPlayerResources()
        {
            _latestPlayerResources = default;
            _resourceReconciliationPending = false;
            _resourceSnapshotRequestInFlight = false;
        }
    }
}
