using System;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Combat;
using Game.Server.Application.Sessions;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.World;
using LiteNetLib;
using LiteNetLibManager;
using Player.Shared;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<PlayerCombatDamageEventMessage> PlayerCombatDamageReceived;
        public event Action<PlayerAbilityCastStateMessage> PlayerAbilityCastStateReceived;
        public event Action<PlayerCombatPresentationBatchMessage> CombatPresentationBatchReceived;
        public event Action<PlayerCombatFireCycleBatchMessage> CombatFireCycleBatchReceived;
        public event Action<ushort, byte, FirearmFireMode, float> LocalFireTriggerPredicted;
        public event Action<BasicAttackMode, BasicAttackInputKind, ushort> LocalBasicAttackPredicted;
        public event Action LocalReloadAccepted;
        public event Action<PlayerInteractionResponseMessage> PlayerInteractionResultReceived;
        public event Action<PlayerCombatOwnerStateMessage> PlayerCombatOwnerStateReceived;

        private PlayerCombatOwnerStateMessage _latestCombatOwnerState;
        public PlayerCombatOwnerStateMessage LatestCombatOwnerState => _latestCombatOwnerState;

        private double _nextCombatActionAt;
        private uint _combatActionSequence;
        private bool _localAutomaticPredictionActive;
        private double _localAutomaticPredictionAt;
        private double _localAutomaticShotAccumulator;
        private bool _abilityRequestInFlight;
        private bool _cancelAbilityRequestInFlight;
        private double _nextAbilityRequestAt;
        private uint _activeClientAbilityCastSequence;
        private bool _playerInteractionRequestInFlight;
        private double _nextPlayerInteractionRequestAt;
        private bool _respawnRequestInFlight;
        private double _nextRespawnRequestAt;
        private bool _combatOwnerStateRequestInFlight;
        private bool _reloadRequestInFlight;
        private double _nextReloadRequestAt;

        private void RegisterPlayerGameplayActionMessages()
        {
            RegisterServerMessage(
                PlayerGameplayActionMessageTypes.CombatActionIntent,
                HandleCombatActionIntent);
            RegisterRequestToServer<PlayerBeginAbilityRequestMessage, PlayerAbilityRequestAckMessage>(
                PlayerGameplayActionRequestTypes.BeginAbility,
                HandleBeginAbilityRequest);
            RegisterRequestToServer<PlayerCancelAbilityRequestMessage, PlayerAbilityRequestAckMessage>(
                PlayerGameplayActionRequestTypes.CancelAbility,
                HandleCancelAbilityRequest);
            RegisterRequestToServer<PlayerInteractionRequestMessage, PlayerInteractionResponseMessage>(
                PlayerGameplayActionRequestTypes.Interaction,
                HandleInteractionRequest);
            RegisterRequestToServer<PlayerRespawnRequestMessage, PlayerRespawnResponseMessage>(
                PlayerGameplayActionRequestTypes.Respawn,
                HandleRespawnRequest);
            RegisterRequestToServer<PlayerReloadRequestMessage, PlayerReloadResponseMessage>(
                PlayerGameplayActionRequestTypes.Reload,
                HandleReloadRequest);
            RegisterRequestToServer<PlayerCombatOwnerStateRequestMessage, PlayerCombatOwnerStateMessage>(
                PlayerGameplayActionRequestTypes.CombatOwnerState,
                HandleCombatOwnerStateRequest);

            RegisterClientMessage(PlayerGameplayActionMessageTypes.CombatDamage, HandleCombatDamageEvent);
            RegisterClientMessage(PlayerGameplayActionMessageTypes.AbilityCastState, HandleAbilityCastStateEvent);
            RegisterClientMessage(PlayerGameplayActionMessageTypes.CombatPresentationBatch, HandleCombatPresentationBatch);
            RegisterClientMessage(PlayerGameplayActionMessageTypes.CombatOwnerState, HandleCombatOwnerStateEvent);
            RegisterClientMessage(PlayerGameplayActionMessageTypes.CombatFireCycleBatch, HandleCombatFireCycleBatch);
            RegisterContextInteractionMessages();
        }

        /// <summary>
        /// Sends one compact targetless combat action. This is deliberately one-way:
        /// authoritative effects arrive through combat/resource events, while rare server
        /// rejection/drift is corrected by an owner-state push.
        /// </summary>
        public bool TrySubmitCombatAction(
            BasicAttackInputKind inputKind,
            float aimYawDegrees,
            float aimPitchDegrees,
            out BasicAttackResultCode localResult)
        {
            localResult = BasicAttackResultCode.Applied;
            if (!IsClientConnected)
            {
                localResult = BasicAttackResultCode.RejectedInvalidSource;
                return false;
            }
            if (inputKind != BasicAttackInputKind.Primary &&
                inputKind != BasicAttackInputKind.Light &&
                inputKind != BasicAttackInputKind.Heavy)
            {
                localResult = BasicAttackResultCode.RejectedInvalidInput;
                return false;
            }
            if (TryGetLocallyKnownAlive(out bool alive) && !alive)
            {
                localResult = BasicAttackResultCode.RejectedDead;
                return false;
            }
            if (_activeClientAbilityCastSequence != 0)
            {
                localResult = BasicAttackResultCode.RejectedAlreadyCasting;
                return false;
            }

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < _nextCombatActionAt)
            {
                localResult = BasicAttackResultCode.RejectedRecovery;
                return false;
            }

            if (_latestCombatOwnerState.revision > 0)
            {
                if (_latestCombatOwnerState.Mode == BasicAttackMode.Firearm &&
                    _latestCombatOwnerState.loadedRounds <= 0)
                {
                    localResult = BasicAttackResultCode.RejectedNoAmmo;
                    return false;
                }

                if (_latestCombatOwnerState.Mode == BasicAttackMode.Unarmed)
                {
                    int staminaCost = inputKind == BasicAttackInputKind.Heavy
                        ? _latestCombatOwnerState.unarmedHeavyStaminaCost
                        : _latestCombatOwnerState.unarmedLightStaminaCost;
                    if (staminaCost > 0 &&
                        !CanPredictResourceSpend(CharacterResourceId.Stamina, staminaCost))
                    {
                        localResult = BasicAttackResultCode.RejectedInsufficientResource;
                        return false;
                    }
                }
            }

            float interval = PlayerGameplaySettingsRuntime.BasicAttackInterval;
            if (_latestCombatOwnerState.revision > 0)
                interval = inputKind == BasicAttackInputKind.Heavy
                    ? _latestCombatOwnerState.heavyInterval
                    : _latestCombatOwnerState.primaryInterval;

            _nextCombatActionAt = now +
                (_latestCombatOwnerState.revision > 0 &&
                 _latestCombatOwnerState.Mode == BasicAttackMode.Firearm
                    ? Math.Max(
                        FirearmCadenceTiming.MinimumTriggerInterval,
                        Math.Min(FirearmCadenceTiming.MaximumTriggerInterval, interval))
                    : BasicAttackCadenceTiming.Clamp(interval));

            // Local response is presentation only. The server independently commits action,
            // resource/ammo, contact and effect truth.
            if (_latestCombatOwnerState.revision > 0)
            {
                if (_latestCombatOwnerState.Mode == BasicAttackMode.Firearm)
                {
                    int predictedRounds = _latestCombatOwnerState.FireMode == FirearmFireMode.Burst
                        ? Math.Max(1, (int)_latestCombatOwnerState.firearmRoundsPerTrigger)
                        : 1;
                    _latestCombatOwnerState.loadedRounds =
                        Math.Max(0, _latestCombatOwnerState.loadedRounds - predictedRounds);
                    PlayerCombatOwnerStateReceived?.Invoke(_latestCombatOwnerState);
                    // Primary firearm body animation is canonical PlayerHumanoid presentation,
                    // so it must run even when the optional semantic FX/audio presentation id is 0.
                    LocalFireTriggerPredicted?.Invoke(
                        _latestCombatOwnerState.attackPresentationId,
                        (byte)Math.Min(31, predictedRounds),
                        _latestCombatOwnerState.FireMode,
                        Math.Max(
                            FirearmCadenceTiming.MinimumRoundsPerSecond,
                            _latestCombatOwnerState.firearmRoundsPerSecond));
                    if (_latestCombatOwnerState.FireMode == FirearmFireMode.FullAutomatic)
                    {
                        _localAutomaticPredictionActive = true;
                        _localAutomaticPredictionAt = now;
                        _localAutomaticShotAccumulator = 0d;
                    }
                }
                else
                {
                    if (_latestCombatOwnerState.Mode == BasicAttackMode.Unarmed)
                    {
                        int staminaCost = inputKind == BasicAttackInputKind.Heavy
                            ? _latestCombatOwnerState.unarmedHeavyStaminaCost
                            : _latestCombatOwnerState.unarmedLightStaminaCost;
                        PredictResourceSpend(CharacterResourceId.Stamina, staminaCost);
                    }
                    LocalBasicAttackPredicted?.Invoke(
                        _latestCombatOwnerState.Mode,
                        inputKind,
                        _latestCombatOwnerState.attackPresentationId);
                }
            }

            uint sequence = ++_combatActionSequence;
            if (sequence == 0)
                sequence = ++_combatActionSequence;

            bool precisionAim = _latestCombatOwnerState.revision > 0 &&
                                _latestCombatOwnerState.Mode == BasicAttackMode.Firearm;

            ClientSendPacket(
                0,
                DeliveryMethod.ReliableOrdered,
                PlayerGameplayActionMessageTypes.CombatActionIntent,
                new PlayerCombatActionIntentMessage
                {
                    sequence = sequence,
                    inputKind = (byte)inputKind,
                    hasPrecisionAim = precisionAim,
                    packedAim = precisionAim
                        ? PlayerCombatAimEncoding.Encode(aimYawDegrees, aimPitchDegrees)
                        : (ushort)0,
                });
            return true;
        }

        /// <summary>
        /// Advances owner-local full-auto presentation/ammo prediction from the same authored
        /// rounds-per-second value used by authority. This never applies gameplay effects.
        /// The server sends owner state only for rejection/resync/empty boundaries.
        /// </summary>
        public void UpdateLocalAutomaticFirePrediction(bool fireHeld, double now)
        {
            if (!fireHeld || _latestCombatOwnerState.revision <= 0 ||
                _latestCombatOwnerState.Mode != BasicAttackMode.Firearm ||
                _latestCombatOwnerState.FireMode != FirearmFireMode.FullAutomatic ||
                _latestCombatOwnerState.loadedRounds <= 0)
            {
                ResetLocalAutomaticPrediction();
                return;
            }

            if (!_localAutomaticPredictionActive)
            {
                _localAutomaticPredictionActive = true;
                _localAutomaticPredictionAt = now;
                _localAutomaticShotAccumulator = 0d;
                return;
            }

            double elapsed = Math.Max(0d, Math.Min(0.25d, now - _localAutomaticPredictionAt));
            _localAutomaticPredictionAt = now;
            if (elapsed <= 0d)
                return;

            float rps = Math.Max(
                FirearmCadenceTiming.MinimumRoundsPerSecond,
                _latestCombatOwnerState.firearmRoundsPerSecond);
            _localAutomaticShotAccumulator += rps * elapsed;
            int due = (int)Math.Floor(_localAutomaticShotAccumulator);
            if (due <= 0)
                return;

            due = Math.Min(due, Math.Max(0, _latestCombatOwnerState.loadedRounds));
            if (due <= 0)
            {
                ResetLocalAutomaticPrediction();
                return;
            }

            _localAutomaticShotAccumulator = Math.Max(0d, _localAutomaticShotAccumulator - due);
            _latestCombatOwnerState.loadedRounds = Math.Max(0, _latestCombatOwnerState.loadedRounds - due);
            PlayerCombatOwnerStateReceived?.Invoke(_latestCombatOwnerState);

            LocalFireTriggerPredicted?.Invoke(
                _latestCombatOwnerState.attackPresentationId,
                (byte)Math.Min(31, due),
                FirearmFireMode.FullAutomatic,
                rps);

            if (_latestCombatOwnerState.loadedRounds <= 0)
                ResetLocalAutomaticPrediction();
        }

        private void ResetLocalAutomaticPrediction()
        {
            _localAutomaticPredictionActive = false;
            _localAutomaticPredictionAt = 0d;
            _localAutomaticShotAccumulator = 0d;
        }

        public UniTask<PlayerAbilityRequestAckMessage> RequestBeginAbilityAsync(
            string abilityDefinitionId,
            int rank = 1,
            uint targetObjectId = 0,
            ushort targetGeneration = 0,
            int millisecondsTimeout = 5000) =>
            RequestBeginAbilityAsync(
                abilityDefinitionId,
                rank,
                targetObjectId != 0 && targetGeneration != 0
                    ? CombatTargetReferenceWire.Player(targetObjectId, targetGeneration)
                    : default,
                millisecondsTimeout);

        public async UniTask<PlayerAbilityRequestAckMessage> RequestBeginAbilityAsync(
            string abilityDefinitionId,
            int rank,
            CombatTargetReferenceWire target,
            int millisecondsTimeout = 5000)
        {
            if (!IsClientConnected)
                return PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.InvalidState);
            if (string.IsNullOrWhiteSpace(abilityDefinitionId) ||
                !PlayerGameplaySettingsRuntime.TryGetAbility(abilityDefinitionId, out GameplayAbilityReferenceWire ability))
                return PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.UnknownAbility);
            if (TryGetLocallyKnownAlive(out bool abilityAlive) && !abilityAlive)
                return PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.InvalidState, ability.wireId);

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_abilityRequestInFlight || _activeClientAbilityCastSequence != 0 || now < _nextAbilityRequestAt)
                return PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.OnCooldown, ability.wireId);

            _abilityRequestInFlight = true;
            // Cheap normal-client send suppression. The standalone GameServer remains the
            // authority and independently validates cooldown/recovery and request abuse.
            _nextAbilityRequestAt = now + 0.10d;
            try
            {
                AsyncResponseData<PlayerAbilityRequestAckMessage> response =
                    await ClientSendRequestAsync<PlayerBeginAbilityRequestMessage, PlayerAbilityRequestAckMessage>(
                        PlayerGameplayActionRequestTypes.BeginAbility,
                        new PlayerBeginAbilityRequestMessage
                        {
                            target = target,
                            abilityWireId = ability.wireId,
                            rank = (byte)Math.Max(1, Math.Min(byte.MaxValue, rank)),
                        },
                        millisecondsTimeout);

                PlayerAbilityRequestAckMessage result = response.IsSuccess
                    ? response.Response
                    : PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.InvalidState, ability.wireId);
                return result;
            }
            finally
            {
                _abilityRequestInFlight = false;
            }
        }

        public async UniTask<PlayerCombatOwnerStateMessage> RequestCombatOwnerStateAsync(int millisecondsTimeout = 5000)
        {
            if (!IsClientConnected)
                return PlayerCombatOwnerStateMessage.Failed();
            if (_combatOwnerStateRequestInFlight)
                return _latestCombatOwnerState.revision > 0
                    ? _latestCombatOwnerState
                    : PlayerCombatOwnerStateMessage.Failed();

            _combatOwnerStateRequestInFlight = true;
            try
            {
                AsyncResponseData<PlayerCombatOwnerStateMessage> response =
                    await ClientSendRequestAsync<PlayerCombatOwnerStateRequestMessage, PlayerCombatOwnerStateMessage>(
                        PlayerGameplayActionRequestTypes.CombatOwnerState,
                        new PlayerCombatOwnerStateRequestMessage(),
                        millisecondsTimeout);
                if (!response.IsSuccess)
                    return PlayerCombatOwnerStateMessage.Failed();
                ApplyCombatOwnerState(response.Response);
                return response.Response;
            }
            finally
            {
                _combatOwnerStateRequestInFlight = false;
            }
        }

        /// <summary>
        /// Local gameplay-input entry point for the existing R key. Reuses the existing
        /// reload/respawn request methods and wire messages; no UI dependency or new protocol.
        /// </summary>
        public void RequestReloadOrRespawnFromLocalInput()
        {
            RequestReloadOrRespawnFromLocalInputAsync().Forget();
        }

        private async UniTaskVoid RequestReloadOrRespawnFromLocalInputAsync()
        {
            if (!IsClientConnected)
                return;

            if (TryGetLocallyKnownAlive(out bool alive) && !alive)
            {
                await RequestRespawnAsync();
                return;
            }

            await RequestReloadAsync();
        }

        public async UniTask<PlayerReloadResponseMessage> RequestReloadAsync(
            string preferredAmmoDefinitionId = "",
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.InvalidState);
            if (TryGetLocallyKnownAlive(out bool reloadAlive) && !reloadAlive)
                return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.InvalidState);
            if (_activeClientAbilityCastSequence != 0 || _abilityRequestInFlight)
                return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.InvalidState);

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_reloadRequestInFlight || now < _nextReloadRequestAt)
                return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.InvalidState);

            if (_latestCombatOwnerState.revision > 0)
            {
                if (_latestCombatOwnerState.Mode != BasicAttackMode.Firearm)
                    return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.NotFirearm);
                if (_latestCombatOwnerState.magazineCapacity > 0 &&
                    _latestCombatOwnerState.loadedRounds >= _latestCombatOwnerState.magazineCapacity)
                {
                    return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.MagazineFull);
                }
            }

            _reloadRequestInFlight = true;
            _nextReloadRequestAt = now + 0.25d;
            try
            {
                ushort preferredAmmoDataId = 0;
                if (!string.IsNullOrWhiteSpace(preferredAmmoDefinitionId))
                {
                    if (!PlayerGameplaySettingsRuntime.TryGetItem(
                            preferredAmmoDefinitionId,
                            out GameplayItemReferenceWire preferredAmmo))
                        return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.AmmoUnavailable);
                    preferredAmmoDataId = preferredAmmo.dataId;
                }

                AsyncResponseData<PlayerReloadResponseMessage> response =
                    await ClientSendRequestAsync<PlayerReloadRequestMessage, PlayerReloadResponseMessage>(
                        PlayerGameplayActionRequestTypes.Reload,
                        new PlayerReloadRequestMessage
                        {
                            preferredAmmoDataId = preferredAmmoDataId,
                            preferredAmmoDefinitionId = preferredAmmoDefinitionId ?? string.Empty,
                        },
                        millisecondsTimeout);
                if (!response.IsSuccess)
                    return PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.InvalidState);

                PlayerReloadResponseMessage result = response.Response;
                if (result.success)
                {
                    if (_latestCombatOwnerState.revision <= 0)
                    {
                        ReconcileCombatPredictionAsync().Forget();
                    }
                    else
                    {
                        _latestCombatOwnerState.success = true;
                        _latestCombatOwnerState.resultCode = (byte)CombatReloadResultCode.Success;
                        _latestCombatOwnerState.revision = result.revision;
                        _latestCombatOwnerState.loadedAmmoDataId = result.loadedAmmoDataId;
                        _latestCombatOwnerState.loadedAmmoDefinitionId = result.loadedAmmoDefinitionId ?? string.Empty;
                        _latestCombatOwnerState.loadedRounds = result.loadedRounds;
                        PlayerCombatOwnerStateReceived?.Invoke(_latestCombatOwnerState);
                    }

                    // The response already exists for the authoritative inventory transaction.
                    // Reuse it to start owner-local presentation without another message.
                    LocalReloadAccepted?.Invoke();
                }
                return result;
            }
            finally
            {
                _reloadRequestInFlight = false;
            }
        }

        public async UniTask<PlayerAbilityRequestAckMessage> RequestCancelAbilityAsync(int millisecondsTimeout = 5000)
        {
            if (!IsClientConnected)
                return PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.InvalidState);
            if (_activeClientAbilityCastSequence == 0 || _abilityRequestInFlight || _cancelAbilityRequestInFlight)
                return PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.InvalidState);

            _cancelAbilityRequestInFlight = true;
            try
            {
                AsyncResponseData<PlayerAbilityRequestAckMessage> response =
                    await ClientSendRequestAsync<PlayerCancelAbilityRequestMessage, PlayerAbilityRequestAckMessage>(
                        PlayerGameplayActionRequestTypes.CancelAbility,
                        new PlayerCancelAbilityRequestMessage(),
                        millisecondsTimeout);

                PlayerAbilityRequestAckMessage result = response.IsSuccess
                    ? response.Response
                    : PlayerAbilityRequestAckMessage.Failed(AbilityCastFailure.InvalidState);
                return result;
            }
            finally
            {
                _cancelAbilityRequestInFlight = false;
            }
        }

        public async UniTask<PlayerInteractionResponseMessage> RequestPlayerInteractionAsync(
            uint targetObjectId,
            ushort targetGeneration,
            InteractionCategoryId categoryId,
            InteractionActionId actionId,
            uint sequence,
            int millisecondsTimeout = 5000)
        {
            if (!IsClientConnected)
                return PlayerInteractionResponseMessage.Failed(sequence, categoryId, actionId, InteractionResultCode.InvalidState, "client is not connected");
            if (targetObjectId == 0 || targetGeneration == 0)
                return PlayerInteractionResponseMessage.Failed(sequence, categoryId, actionId, InteractionResultCode.InvalidTarget, "target is not selected");

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_playerInteractionRequestInFlight || now < _nextPlayerInteractionRequestAt)
                return PlayerInteractionResponseMessage.Failed(sequence, categoryId, actionId, InteractionResultCode.Rejected, "interaction is locally pending or rate-limited");

            _playerInteractionRequestInFlight = true;
            _nextPlayerInteractionRequestAt = now + 0.10d;
            try
            {
                AsyncResponseData<PlayerInteractionResponseMessage> response =
                    await ClientSendRequestAsync<PlayerInteractionRequestMessage, PlayerInteractionResponseMessage>(
                        PlayerGameplayActionRequestTypes.Interaction,
                        new PlayerInteractionRequestMessage
                        {
                            target = new PlayerTargetReferenceWire
                            {
                                objectId = targetObjectId,
                                generation = targetGeneration,
                            },
                            categoryId = (ushort)categoryId,
                            actionId = (ushort)actionId,
                            sequence = sequence,
                        },
                        millisecondsTimeout);

                PlayerInteractionResponseMessage result = response.IsSuccess
                    ? response.Response
                    : PlayerInteractionResponseMessage.Failed(
                        sequence,
                        categoryId,
                        actionId,
                        InteractionResultCode.InvalidState,
                        $"interaction request failed: {response.ResponseCode}");
                // sequence + action are request-known and intentionally omitted from the wire.
                result.sequence = sequence;
                result.categoryId = (ushort)categoryId;
                result.actionId = (ushort)actionId;
                PlayerInteractionResultReceived?.Invoke(result);
                return result;
            }
            finally
            {
                _playerInteractionRequestInFlight = false;
            }
        }

        public async UniTask<PlayerRespawnResponseMessage> RequestRespawnAsync(int millisecondsTimeout = 5000)
        {
            if (!IsClientConnected)
                return PlayerRespawnResponseMessage.Failed(
                    PlayerRespawnResultCode.CharacterUnavailable,
                    "client is not connected");
            if (TryGetLocallyKnownAlive(out bool respawnAlive) && respawnAlive)
                return PlayerRespawnResponseMessage.Failed(PlayerRespawnResultCode.NotDead, "character is locally known alive");

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_respawnRequestInFlight || now < _nextRespawnRequestAt)
                return PlayerRespawnResponseMessage.Failed(PlayerRespawnResultCode.CharacterUnavailable, "respawn request is locally pending or rate-limited");

            _respawnRequestInFlight = true;
            _nextRespawnRequestAt = now + 0.50d;
            try
            {
                AsyncResponseData<PlayerRespawnResponseMessage> response =
                    await ClientSendRequestAsync<PlayerRespawnRequestMessage, PlayerRespawnResponseMessage>(
                        PlayerGameplayActionRequestTypes.Respawn,
                        new PlayerRespawnRequestMessage(),
                        millisecondsTimeout);

                return response.IsSuccess
                    ? response.Response
                    : PlayerRespawnResponseMessage.Failed(
                        PlayerRespawnResultCode.CharacterUnavailable,
                        $"respawn request failed: {response.ResponseCode}");
            }
            finally
            {
                _respawnRequestInFlight = false;
            }
        }

        private UniTaskVoid HandleRespawnRequest(
            RequestHandlerData handler,
            PlayerRespawnRequestMessage request,
            RequestProceedResultDelegate<PlayerRespawnResponseMessage> result)
        {
            // The MMO application no longer exposes a Unity-hosted authoritative server.
            // This handler exists only so LiteNetLibManager can register the client-side
            // response contract for request 504. The standalone .NET GameServer owns
            // the real respawn execution path.
            result(
                AckResponseCode.Success,
                PlayerRespawnResponseMessage.Failed(
                    PlayerRespawnResultCode.CharacterUnavailable,
                    "respawn authority is owned by the standalone GameServer"));
            return default;
        }

        private UniTaskVoid HandleReloadRequest(
            RequestHandlerData handler,
            PlayerReloadRequestMessage request,
            RequestProceedResultDelegate<PlayerReloadResponseMessage> result)
        {
            result(AckResponseCode.Success, PlayerReloadResponseMessage.Failed((byte)CombatReloadResultCode.InvalidState));
            return default;
        }

        private UniTaskVoid HandleCombatOwnerStateRequest(
            RequestHandlerData handler,
            PlayerCombatOwnerStateRequestMessage request,
            RequestProceedResultDelegate<PlayerCombatOwnerStateMessage> result)
        {
            result(AckResponseCode.Success, PlayerCombatOwnerStateMessage.Failed());
            return default;
        }

        private void HandleCombatActionIntent(MessageHandlerData handler)
        {
            PlayerCombatActionIntentMessage intent = handler.ReadMessage<PlayerCombatActionIntentMessage>();
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle sourceHandle) ||
                _characterSessionRuntimeHost == null)
                return;

            // Unity-host compatibility path. Production standalone authority performs
            // spatial AOI/LOS contact resolution; this path still commits the targetless
            // action so host tooling cannot reintroduce the legacy target-request contract.
            _characterSessionRuntimeHost.TryPlayerBasicAttack(
                sourceHandle,
                default,
                intent.InputKind);
        }

        private UniTaskVoid HandleBeginAbilityRequest(
            RequestHandlerData handler,
            PlayerBeginAbilityRequestMessage request,
            RequestProceedResultDelegate<PlayerAbilityRequestAckMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle sourceHandle))
            {
                result(AckResponseCode.Success, PlayerAbilityRequestAckMessage.Failed(
                    AbilityCastFailure.InvalidState,
                    request.abilityWireId));
                return default;
            }

            PlayerSessionHandle targetHandle = default;
            if (request.target.IsValid &&
                (!request.target.IsPlayer ||
                 !TryResolveUnityPlayerTarget(
                     new PlayerTargetReferenceWire { objectId = request.target.objectId, generation = request.target.generation },
                     out targetHandle)))
            {
                result(AckResponseCode.Success, PlayerAbilityRequestAckMessage.Failed(
                    AbilityCastFailure.InvalidTarget,
                    request.abilityWireId));
                return default;
            }

            if (_characterSessionRuntimeHost?.Content == null ||
                !_characterSessionRuntimeHost.Content.TryGetAbility(request.abilityWireId, out AbilityDefinition ability))
            {
                result(AckResponseCode.Success, PlayerAbilityRequestAckMessage.Failed(
                    AbilityCastFailure.UnknownAbility,
                    request.abilityWireId));
                return default;
            }

            AbilityCastResult cast = _characterSessionRuntimeHost.BeginPlayerAbility(
                sourceHandle,
                targetHandle,
                ability.definitionId,
                Math.Max(1, (int)request.rank),
                new WorldPosition(0f, 0f, 0f));
            result(AckResponseCode.Success, new PlayerAbilityRequestAckMessage
            {
                success = cast.Success,
                failure = (byte)cast.Failure,
                abilityWireId = request.abilityWireId,
            });
            return default;
        }

        private UniTaskVoid HandleCancelAbilityRequest(
            RequestHandlerData handler,
            PlayerCancelAbilityRequestMessage request,
            RequestProceedResultDelegate<PlayerAbilityRequestAckMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle sourceHandle))
            {
                result(AckResponseCode.Success, PlayerAbilityRequestAckMessage.Failed(
                    AbilityCastFailure.InvalidState));
                return default;
            }

            AbilityCastResult cast = _characterSessionRuntimeHost.CancelPlayerAbility(sourceHandle);
            PlayerAbilityCastStateMessage wire = ToWireGameplay(cast);
            result(AckResponseCode.Success, new PlayerAbilityRequestAckMessage
            {
                success = cast.Success,
                failure = (byte)cast.Failure,
                abilityWireId = wire.abilityWireId,
            });
            return default;
        }

        private UniTaskVoid HandleInteractionRequest(
            RequestHandlerData handler,
            PlayerInteractionRequestMessage request,
            RequestProceedResultDelegate<PlayerInteractionResponseMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle sourceHandle))
            {
                result(AckResponseCode.Success, PlayerInteractionResponseMessage.Failed(
                    request.sequence,
                    request.CategoryId,
                    request.ActionId,
                    InteractionResultCode.InvalidState,
                    "source character is not in world"));
                return default;
            }

            if (!InteractionCategoryCatalog.IsCompatible(InteractionTargetKind.PlayerEntity, request.CategoryId, request.ActionId))
            {
                result(AckResponseCode.Success, PlayerInteractionResponseMessage.Failed(
                    request.sequence,
                    request.CategoryId,
                    request.ActionId,
                    InteractionResultCode.Unsupported,
                    "interaction category/action pair is not supported for player targets"));
                return default;
            }

            if (!TryResolveUnityPlayerTarget(request.target, out PlayerSessionHandle targetHandle))
            {
                result(AckResponseCode.Success, PlayerInteractionResponseMessage.Failed(
                    request.sequence,
                    request.CategoryId,
                    request.ActionId,
                    InteractionResultCode.InvalidTarget,
                    "target is unavailable"));
                return default;
            }

            InteractionResult interaction = _characterSessionRuntimeHost.ExecutePlayerInteraction(
                sourceHandle,
                targetHandle,
                request.ActionId,
                request.sequence);
            result(AckResponseCode.Success, ToWireGameplay(interaction));
            return default;
        }

        private bool TryResolveUnityPlayerTarget(
            PlayerTargetReferenceWire target,
            out PlayerSessionHandle targetHandle)
        {
            targetHandle = default;
            if (!target.IsValid || Assets == null ||
                !Assets.TryGetSpawnedObject(target.objectId, out LiteNetLibIdentity identity) ||
                identity == null)
            {
                return false;
            }

            PlayerEntityNetwork network = identity.GetComponent<PlayerEntityNetwork>();
            if (network == null || network.Snapshot.generation != target.generation)
                return false;

            return TryGetInWorldPlayerSession(identity.ConnectionId, out targetHandle);
        }

        private void HandleCombatDamageEvent(MessageHandlerData handler)
        {
            PlayerCombatDamageEventMessage message = handler.ReadMessage<PlayerCombatDamageEventMessage>();
            PlayerCombatDamageReceived?.Invoke(message);
        }

        private void HandleAbilityCastStateEvent(MessageHandlerData handler)
        {
            PlayerAbilityCastStateMessage message = handler.ReadMessage<PlayerAbilityCastStateMessage>();
            TrackLocalAbilityState(message);
            PlayerAbilityCastStateReceived?.Invoke(message);
        }

        private void HandleCombatPresentationBatch(MessageHandlerData handler)
        {
            PlayerCombatPresentationBatchMessage message = handler.ReadMessage<PlayerCombatPresentationBatchMessage>();
            CombatPresentationBatchReceived?.Invoke(message);
        }

        private void HandleCombatFireCycleBatch(MessageHandlerData handler)
        {
            CombatFireCycleBatchReceived?.Invoke(handler.ReadMessage<PlayerCombatFireCycleBatchMessage>());
        }

        private void HandleCombatOwnerStateEvent(MessageHandlerData handler)
        {
            ApplyCombatOwnerState(handler.ReadMessage<PlayerCombatOwnerStateMessage>());
        }

        private void ApplyCombatOwnerState(PlayerCombatOwnerStateMessage message)
        {
            if (message.revision <= 0 ||
                (_latestCombatOwnerState.revision > 0 && message.revision < _latestCombatOwnerState.revision))
                return;
            _latestCombatOwnerState = message;
            PlayerCombatOwnerStateReceived?.Invoke(message);
        }

        private async UniTaskVoid ReconcileCombatPredictionAsync()
        {
            await RequestCombatOwnerStateAsync();
            if (_latestCombatOwnerState.Mode == BasicAttackMode.Unarmed)
                await RequestPlayerResourcesAsync();
        }

        private void TrackLocalAbilityState(PlayerAbilityCastStateMessage message)
        {
            if (!message.success)
                return;

            if (message.Phase == AbilityPresentationPhase.CastStarted && message.castSequence != 0)
            {
                _activeClientAbilityCastSequence = message.castSequence;
                return;
            }

            if (message.Phase == AbilityPresentationPhase.CastCompleted ||
                message.Phase == AbilityPresentationPhase.CastCancelled)
            {
                if (_activeClientAbilityCastSequence == 0 ||
                    _activeClientAbilityCastSequence == message.castSequence)
                    _activeClientAbilityCastSequence = 0;

                if (message.Phase == AbilityPresentationPhase.CastCompleted &&
                    PlayerGameplaySettingsRuntime.TryGetAbility(message.abilityWireId, out GameplayAbilityReferenceWire ability))
                {
                    double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
                    _nextAbilityRequestAt = Math.Max(_nextAbilityRequestAt, now + Math.Max(0d, ability.cooldownSeconds));
                }
            }
        }

        private bool TryGetLocallyKnownAlive(out bool alive)
        {
            alive = false;
            if (!_latestPlayerResources.success)
                return false;

            CharacterResourceWire[] resources = _latestPlayerResources.resources ?? Array.Empty<CharacterResourceWire>();
            for (int i = 0; i < resources.Length; ++i)
            {
                CharacterResourceWire resource = resources[i];
                if ((CharacterResourceId)resource.id != CharacterResourceId.Health)
                    continue;

                alive = resource.current > resource.minimum;
                return true;
            }
            return false;
        }

        private void ResetClientCombatRequestAdmission()
        {
            _nextCombatActionAt = 0d;
            _combatActionSequence = 0;
            ResetLocalAutomaticPrediction();
            _abilityRequestInFlight = false;
            _cancelAbilityRequestInFlight = false;
            _nextAbilityRequestAt = 0d;
            _activeClientAbilityCastSequence = 0;
            _playerInteractionRequestInFlight = false;
            _nextPlayerInteractionRequestAt = 0d;
            _respawnRequestInFlight = false;
            _nextRespawnRequestAt = 0d;
            _combatOwnerStateRequestInFlight = false;
            _reloadRequestInFlight = false;
            _nextReloadRequestAt = 0d;
            _latestCombatOwnerState = default;
        }

        private PlayerAbilityCastStateMessage ToWireGameplay(AbilityCastResult result)
        {
            ushort wireId = 0;
            if (_characterSessionRuntimeHost?.Content != null &&
                !string.IsNullOrWhiteSpace(result.AbilityDefinitionId) &&
                _characterSessionRuntimeHost.Content.TryGetAbility(result.AbilityDefinitionId, out AbilityDefinition ability))
                wireId = ability.wireId;

            double remaining = result.CompletesAt > 0d
                ? Math.Max(0d, result.CompletesAt - CoreScheduler.ServerTime)
                : 0d;
            uint remainingMs = remaining >= uint.MaxValue / 1000d
                ? uint.MaxValue
                : (uint)Math.Round(remaining * 1000d, MidpointRounding.AwayFromZero);

            return new PlayerAbilityCastStateMessage
            {
                success = result.Success,
                failure = (byte)result.Failure,
                phase = (byte)result.Phase,
                castSequence = unchecked((uint)result.CastId),
                abilityWireId = wireId,
                source = ToUnityTargetReference(result.SourceCharacterId),
                target = ToUnityTargetReference(result.TargetCharacterId),
                remainingMilliseconds = remainingMs,
                totalAffected = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, result.TotalAffected)),
            };
        }

        private static PlayerInteractionResponseMessage ToWireGameplay(InteractionResult result) =>
            new PlayerInteractionResponseMessage
            {
                success = result.Success,
                sequence = result.Sequence,
                actionId = (ushort)result.ActionId,
                resultCode = (byte)result.ResultCode,
                targetCharacterId = result.Target.PrimaryId,
                detail = result.Detail,
            };

        private CombatDamageWire ToWireGameplay(CombatDamageResult result) =>
            new CombatDamageWire
            {
                eventSequence = unchecked((uint)result.eventId),
                source = ToUnityTargetReference(result.sourceCharacterId),
                target = ToUnityTargetReference(result.targetCharacterId),
                damageTypeId = result.damageTypeId,
                amount = result.dealtAmount,
                resultCode = (byte)result.resultCode,
                cause = (byte)result.cause,
                flags = (ushort)result.presentationFlags,
            };

        private PlayerTargetReferenceWire ToUnityTargetReference(long characterId)
        {
            if (characterId <= 0 || Assets == null || _characterSessionRuntimeHost == null)
                return default;

            var spawned = Assets.GetSpawnedObjects();
            while (spawned.MoveNext())
            {
                LiteNetLibIdentity identity = spawned.Current.Value;
                if (identity == null ||
                    !TryGetInWorldPlayerSession(identity.ConnectionId, out PlayerSessionHandle handle) ||
                    !_characterSessionRuntimeHost.SessionService.TryGetSession(handle, out Game.Server.Application.Sessions.PlayerSession session) ||
                    session == null || !session.HasRuntime || session.SelectedCharacterId.Value != characterId)
                    continue;

                PlayerEntityNetwork network = identity.GetComponent<PlayerEntityNetwork>();
                if (network == null)
                    return default;

                return new PlayerTargetReferenceWire
                {
                    objectId = identity.ObjectId,
                    generation = network.Snapshot.generation,
                };
            }

            return default;
        }

        public bool IsLocalPlayerReference(PlayerTargetReferenceWire reference)
        {
            if (!reference.IsValid || Assets == null ||
                !Assets.TryGetSpawnedObject(reference.objectId, out LiteNetLibIdentity identity) ||
                identity == null)
                return false;
            PlayerEntityNetwork network = identity.GetComponent<PlayerEntityNetwork>();
            return network != null && network.Snapshot.generation == reference.generation && identity.IsOwnerClient;
        }

        private static string BasicAttackFailureText(BasicAttackResultCode code)
        {
            switch (code)
            {
                case BasicAttackResultCode.RejectedInvalidTarget: return "target is unavailable";
                case BasicAttackResultCode.RejectedDead: return "source or target is dead";
                case BasicAttackResultCode.RejectedAlreadyCasting: return "cannot basic attack while casting";
                case BasicAttackResultCode.RejectedRecovery: return "basic attack is recovering";
                case BasicAttackResultCode.RejectedOutOfRange: return "target is out of range";
                case BasicAttackResultCode.RejectedDifferentWorld: return "target is in another world";
                case BasicAttackResultCode.RejectedNoDamage: return "attack produced no authoritative damage";
                case BasicAttackResultCode.RejectedInsufficientResource: return "authoritative resource is insufficient";
                case BasicAttackResultCode.RejectedNoAmmo: return "authoritative magazine is empty";
                case BasicAttackResultCode.RejectedInvalidInput: return "attack input is invalid";
                case BasicAttackResultCode.RejectedRateLimited: return "combat request rate limited";
                default: return "basic attack was rejected";
            }
        }

        private void ResetClientGameplayActions()
        {
            ResetClientCombatRequestAdmission();
            ResetClientContextInteractionRequests();
            ResetClientWorldLootRequests();
        }
    }
}
