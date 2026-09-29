using System;
using System.Collections.Concurrent;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using Game.Shared.Protocol;
using Game.Shared.Content;
using Game.Shared.StatusEffects;
using LiteNetLib;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<PlayerStatusEffectsResponseMessage> PlayerStatusEffectsSnapshotReceived;
        public event Action<PlayerStatusEffectDeltaMessage> PlayerStatusEffectDeltaReceived;

        private readonly struct PendingPlayerStatusEffectDelta
        {
            public readonly PlayerSessionHandle Handle;
            public readonly PlayerStatusEffectDeltaMessage Message;

            public PendingPlayerStatusEffectDelta(PlayerSessionHandle handle, PlayerStatusEffectDeltaMessage message)
            {
                Handle = handle;
                Message = message;
            }
        }

        private readonly ConcurrentQueue<PendingPlayerStatusEffectDelta> _pendingPlayerStatusEffectDeltas =
            new ConcurrentQueue<PendingPlayerStatusEffectDelta>();
        private bool _statusEffectReconciliationPending;
        private bool _statusEffectSnapshotRequestInFlight;
        private PlayerStatusEffectsResponseMessage _latestPlayerStatusEffects;
        public PlayerStatusEffectsResponseMessage LatestPlayerStatusEffects => _latestPlayerStatusEffects;

        private void RegisterPlayerStatusEffectMessages()
        {
            RegisterRequestToServer<PlayerStatusEffectsSnapshotRequestMessage, PlayerStatusEffectsResponseMessage>(
                PlayerStatusEffectRequestTypes.Snapshot,
                HandlePlayerStatusEffectsSnapshotRequest);
            RegisterClientMessage(PlayerStatusEffectMessageTypes.Snapshot, HandlePlayerStatusEffectsSnapshotPush);
            RegisterClientMessage(PlayerStatusEffectMessageTypes.Delta, HandlePlayerStatusEffectDelta);
        }

        public async UniTask<PlayerStatusEffectsResponseMessage> RequestPlayerStatusEffectsAsync(int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return PlayerStatusEffectsResponseMessage.Failed((byte)StatusEffectOperationStatus.SessionUnavailable, "client is not connected");
            if (_statusEffectSnapshotRequestInFlight)
                return _latestPlayerStatusEffects.success
                    ? _latestPlayerStatusEffects
                    : PlayerStatusEffectsResponseMessage.Failed((byte)StatusEffectOperationStatus.SessionUnavailable, "status effect snapshot request is already pending locally");

            _statusEffectSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<PlayerStatusEffectsResponseMessage> response =
                    await ClientSendRequestAsync<PlayerStatusEffectsSnapshotRequestMessage, PlayerStatusEffectsResponseMessage>(
                        PlayerStatusEffectRequestTypes.Snapshot,
                        new PlayerStatusEffectsSnapshotRequestMessage(),
                        millisecondsTimeout);

                PlayerStatusEffectsResponseMessage result = response.IsSuccess
                    ? response.Response
                    : PlayerStatusEffectsResponseMessage.Failed(
                        (byte)StatusEffectOperationStatus.SessionUnavailable,
                        $"status effect snapshot request failed: {response.ResponseCode}");

                if (result.success &&
                    (!_latestPlayerStatusEffects.success ||
                     result.statusRevision >= _latestPlayerStatusEffects.statusRevision))
                {
                    _latestPlayerStatusEffects = result;
                    PlayerStatusEffectsSnapshotReceived?.Invoke(result);
                }
                return result;
            }
            finally
            {
                _statusEffectSnapshotRequestInFlight = false;
            }
        }

        private UniTaskVoid HandlePlayerStatusEffectsSnapshotRequest(
            RequestHandlerData handler,
            PlayerStatusEffectsSnapshotRequestMessage request,
            RequestProceedResultDelegate<PlayerStatusEffectsResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle))
            {
                result(AckResponseCode.Success, PlayerStatusEffectsResponseMessage.Failed(
                    (byte)StatusEffectOperationStatus.CharacterUnavailable,
                    "character is not in world"));
                return default;
            }

            StatusEffectsSnapshot snapshot = _characterSessionRuntimeHost.GetPlayerStatusEffects(handle);
            if (snapshot == null)
            {
                result(AckResponseCode.Success, PlayerStatusEffectsResponseMessage.Failed(
                    (byte)StatusEffectOperationStatus.CharacterUnavailable,
                    "character status state is unavailable"));
                return default;
            }

            result(AckResponseCode.Success, ToWire(snapshot));
            return default;
        }

        private void HandlePlayerStatusEffectsSnapshotPush(MessageHandlerData handler)
        {
            PlayerStatusEffectsResponseMessage snapshot = handler.ReadMessage<PlayerStatusEffectsResponseMessage>();
            if (!snapshot.success)
                return;
            if (_latestPlayerStatusEffects.success && snapshot.statusRevision < _latestPlayerStatusEffects.statusRevision)
                return;
            _latestPlayerStatusEffects = snapshot;
            _statusEffectReconciliationPending = false;
            PlayerStatusEffectsSnapshotReceived?.Invoke(snapshot);
        }

        private void HandlePlayerStatusEffectDelta(MessageHandlerData handler)
        {
            PlayerStatusEffectDeltaMessage delta = handler.ReadMessage<PlayerStatusEffectDeltaMessage>();

            // Statuses are reliable ordered numeric deltas. Display metadata lives in
            // the gameplay-settings/catalog snapshot, so the hot status path carries no strings.
            if (!_latestPlayerStatusEffects.success ||
                delta.Kind == StatusEffectChangeKind.Reconciled ||
                delta.statusWireId == 0)
            {
                ReconcilePlayerStatusEffectsAsync().Forget();
                return;
            }

            if (!PlayerGameplaySettingsRuntime.TryGetStatus(delta.statusWireId, out _))
            {
                ReconcileGameplaySettingsAsync().Forget();
                ReconcilePlayerStatusEffectsAsync().Forget();
                return;
            }

            if (delta.statusRevision <= _latestPlayerStatusEffects.statusRevision)
                return;
            if (delta.statusRevision != _latestPlayerStatusEffects.statusRevision + 1)
            {
                ReconcilePlayerStatusEffectsAsync().Forget();
                return;
            }

            if (!ApplyDeltaToLatestStatusSnapshot(delta))
            {
                ReconcilePlayerStatusEffectsAsync().Forget();
                return;
            }

            PlayerStatusEffectDeltaReceived?.Invoke(delta);
        }

        private bool ApplyDeltaToLatestStatusSnapshot(PlayerStatusEffectDeltaMessage delta)
        {
            StatusEffectWire[] effects = _latestPlayerStatusEffects.effects ?? Array.Empty<StatusEffectWire>();
            int index = FindStatusEffect(effects, delta.statusWireId);

            switch (delta.Kind)
            {
                case StatusEffectChangeKind.Removed:
                case StatusEffectChangeKind.Expired:
                case StatusEffectChangeKind.ClearedOnDeath:
                    if (index >= 0)
                    {
                        var compact = new StatusEffectWire[effects.Length - 1];
                        if (index > 0) Array.Copy(effects, 0, compact, 0, index);
                        if (index + 1 < effects.Length)
                            Array.Copy(effects, index + 1, compact, index, effects.Length - index - 1);
                        effects = compact;
                    }
                    break;

                case StatusEffectChangeKind.Applied:
                case StatusEffectChangeKind.Refreshed:
                case StatusEffectChangeKind.StacksChanged:
                case StatusEffectChangeKind.Replaced:
                    if (!PlayerGameplaySettingsRuntime.TryGetStatus(delta.statusWireId, out GameplayStatusReferenceWire status))
                        return false;

                    var next = new StatusEffectWire
                    {
                        wireId = delta.statusWireId,
                        definitionId = status.definitionId,
                        displayName = status.displayName,
                        classification = status.classification,
                        stacks = delta.stacks,
                        // Presentation-only local deadline. Gameplay authority never consumes this value.
                        endTime = delta.remainingMilliseconds == 0
                            ? 0d
                            : UnityEngine.Time.realtimeSinceStartupAsDouble + delta.remainingMilliseconds / 1000d,
                        presentationId = status.presentationId,
                    };
                    if (index >= 0)
                    {
                        effects[index] = next;
                    }
                    else
                    {
                        var expanded = new StatusEffectWire[effects.Length + 1];
                        if (effects.Length > 0) Array.Copy(effects, expanded, effects.Length);
                        expanded[effects.Length] = next;
                        Array.Sort(expanded, CompareStatusEffects);
                        effects = expanded;
                    }
                    break;

                default:
                    return false;
            }

            _latestPlayerStatusEffects.effects = effects;
            _latestPlayerStatusEffects.statusRevision = delta.statusRevision;
            _latestPlayerStatusEffects.success = true;
            return true;
        }

        private async UniTaskVoid ReconcilePlayerStatusEffectsAsync()
        {
            if (_statusEffectReconciliationPending)
                return;

            _statusEffectReconciliationPending = true;
            try
            {
                await RequestPlayerStatusEffectsAsync();
            }
            finally
            {
                _statusEffectReconciliationPending = false;
            }
        }

        private void HandleAuthoritativeStatusEffectChanged(
            PlayerSessionHandle handle,
            StatusEffectChangeView change)
        {
            if (!handle.IsValid)
                return;

            ushort statusWireId = 0;
            if (_characterSessionRuntimeHost?.Content != null &&
                !string.IsNullOrWhiteSpace(change.definitionId) &&
                _characterSessionRuntimeHost.Content.TryGetStatusEffect(change.definitionId, out StatusEffectDefinition definition))
                statusWireId = definition.wireId;

            double remaining = change.endTime > 0d
                ? Math.Max(0d, change.endTime - CoreScheduler.ServerTime)
                : 0d;
            uint remainingMs = remaining >= uint.MaxValue / 1000d
                ? uint.MaxValue
                : (uint)Math.Round(remaining * 1000d, MidpointRounding.AwayFromZero);

            _pendingPlayerStatusEffectDeltas.Enqueue(new PendingPlayerStatusEffectDelta(
                handle,
                new PlayerStatusEffectDeltaMessage
                {
                    statusRevision = change.statusRevision,
                    kind = (byte)change.kind,
                    statusWireId = statusWireId,
                    stacks = change.stacks,
                    remainingMilliseconds = remainingMs,
                    reason = (byte)change.reason,
                }));
        }

        private void FlushPendingPlayerStatusEffectDeltas(int maxPerTick = 512)
        {
            if (!IsServer || maxPerTick <= 0)
                return;

            int sent = 0;
            while (sent < maxPerTick &&
                   _pendingPlayerStatusEffectDeltas.TryDequeue(out PendingPlayerStatusEffectDelta pending))
            {
                PlayerSessionHandle handle = pending.Handle;
                if (!handle.IsValid ||
                    !TryGetExactCharacterSession(handle.Connection.Value, handle, out _))
                    continue;

                ServerSendPacket(
                    handle.Connection.Value,
                    0,
                    DeliveryMethod.ReliableOrdered,
                    PlayerStatusEffectMessageTypes.Delta,
                    pending.Message);
                sent++;
            }
        }

        private PlayerStatusEffectsResponseMessage ToWire(StatusEffectsSnapshot snapshot)
        {
            StatusEffectView[] source = snapshot.effects ?? Array.Empty<StatusEffectView>();
            var effects = new StatusEffectWire[source.Length];
            for (int i = 0; i < source.Length; ++i)
            {
                ushort wireId = 0;
                if (_characterSessionRuntimeHost?.Content != null &&
                    _characterSessionRuntimeHost.Content.TryGetStatusEffect(source[i].definitionId, out StatusEffectDefinition definition))
                    wireId = definition.wireId;

                effects[i] = new StatusEffectWire
                {
                    wireId = wireId,
                    definitionId = source[i].definitionId,
                    displayName = source[i].displayName,
                    classification = source[i].classification,
                    stacks = source[i].stacks,
                    endTime = source[i].endTime,
                    presentationId = source[i].presentationId,
                };
            }

            return new PlayerStatusEffectsResponseMessage
            {
                success = true,
                status = (byte)StatusEffectOperationStatus.Success,
                error = string.Empty,
                contentRevision = snapshot.contentRevision,
                statusRevision = snapshot.statusRevision,
                effects = effects,
            };
        }

        private static int FindStatusEffect(StatusEffectWire[] effects, ushort wireId)
        {
            for (int i = 0; i < effects.Length; ++i)
                if (effects[i].wireId == wireId)
                    return i;
            return -1;
        }

        private static int CompareStatusEffects(StatusEffectWire a, StatusEffectWire b) =>
            a.wireId.CompareTo(b.wireId);

        private void ClearPendingPlayerStatusEffectDeltas()
        {
            while (_pendingPlayerStatusEffectDeltas.TryDequeue(out _)) { }
        }

        private void ResetClientPlayerStatusEffects()
        {
            _latestPlayerStatusEffects = default;
            _statusEffectReconciliationPending = false;
            _statusEffectSnapshotRequestInFlight = false;
        }
    }
}
