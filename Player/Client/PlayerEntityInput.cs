using System;
using Game.Shared.Abilities;
using LiteNetLibManager;
using Player.Networking;
using Player.Shared;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Player.Client
{
    /// <summary>
    /// Owner movement serializer. Sends the current locally resolved visual feet
    /// position in the existing RPC, matching the standalone GameServer contract.
    /// The server independently checks reported movement for legality.
    /// </summary>
    public sealed class PlayerEntityInput : LiteNetLibBehaviour
    {
        public bool enableKeyboardInput = true;

        [Header("Movement Wire Cadence")]
        [Tooltip("Maximum rate for continuously changing yaw/direction. Start/stop/sprint/jump changes still send immediately.")]
        [Range(0.02f, 0.20f)]
        public float continuousUpdateIntervalSeconds = 0.05f;

        [Tooltip("Safety refresh for an unchanged held input state.")]
        [Range(0.10f, 0.50f)]
        public float unchangedRefreshSeconds = 0.20f;

        [Header("Client-Safe World Prediction")]
        [Tooltip("Legacy prefab field. Local movement legality checks remain enabled.")]
        public bool useSharedWorldPrediction = true;

        private PlayerEntityNetwork _network;
        private PlayerEntityGameManager _gameManager;
        private PlayerEntityLocomotionCameraController _locomotionCamera;
        private PlayerEntityInteractionApproachDriver _interactionApproach;
        private LogicUpdater _logicUpdater;
        private uint _sequence;
        private bool _hasLastSent;
        private MovementCommand _lastSent;
        private double _lastSentAt;
        private double _lastContinuousSentAt;
        private bool _blockedMovementHeld;

        [Header("Runtime Wire Telemetry")]
        [SerializeField] private uint sentMovementCommands;
        [SerializeField] private uint suppressedUnchangedCommands;
        [SerializeField] private uint suppressedBlockedCommands;

        public uint SentMovementCommands => sentMovementCommands;
        public uint SuppressedUnchangedCommands => suppressedUnchangedCommands;
        public uint SuppressedBlockedCommands => suppressedBlockedCommands;

        public float CurrentFacingYaw => _locomotionCamera != null
            ? _locomotionCamera.CurrentFacingYaw
            : transform.eulerAngles.y;

        public override void OnStartOwnerClient()
        {
            _network = GetComponent<PlayerEntityNetwork>();
            _gameManager = FindFirstObjectByType<PlayerEntityGameManager>();
            _locomotionCamera = GetComponent<PlayerEntityLocomotionCameraController>();
            _interactionApproach = GetComponent<PlayerEntityInteractionApproachDriver>();
            if (_interactionApproach == null)
                _interactionApproach = gameObject.AddComponent<PlayerEntityInteractionApproachDriver>();

            _logicUpdater = Manager?.LogicUpdater;
            sentMovementCommands = 0;
            suppressedUnchangedCommands = 0;
            suppressedBlockedCommands = 0;
            _blockedMovementHeld = false;
            _hasLastSent = false;
            if (_logicUpdater != null)
                _logicUpdater.OnTick += OnClientTick;
        }

        public override void OnNetworkDestroy(byte reasons) => Cleanup();
        public override void OnIdentityDestroy() => Cleanup();

        private void Update()
        {
            if (!enableKeyboardInput || _network == null || !IsOwnerClient || _gameManager == null)
                return;

            if (_interactionApproach != null && _interactionApproach.IsActive)
                return;

            if (_locomotionCamera == null)
                _locomotionCamera = GetComponent<PlayerEntityLocomotionCameraController>();
            if (_locomotionCamera == null || !_locomotionCamera.CombatCameraActive)
                return;

            if (LocalClientInputGate.IsGameplayInputBlocked)
                return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            BasicAttackInputKind inputKind;
            if (Input.GetMouseButtonDown(0))
                inputKind = BasicAttackInputKind.Primary;
            else if (Input.GetKeyDown(KeyCode.Alpha1))
                inputKind = BasicAttackInputKind.Light;
            else if (Input.GetKeyDown(KeyCode.Alpha2))
                inputKind = BasicAttackInputKind.Heavy;
            else
                return;

            float yaw = _locomotionCamera.CurrentFacingYaw;
            float pitch = _locomotionCamera.CurrentAimPitchDegrees;
            PlayerCombatOwnerStateMessage ownerState = _gameManager.LatestCombatOwnerState;
            if (ownerState.revision > 0 && ownerState.Mode == BasicAttackMode.Firearm)
                _locomotionCamera.TryGetPrecisionAim(out yaw, out pitch);

            _gameManager.TrySubmitCombatAction(inputKind, yaw, pitch, out _);
        }

        private void Cleanup()
        {
            if (_logicUpdater != null)
                _logicUpdater.OnTick -= OnClientTick;
            if (_interactionApproach != null)
                _interactionApproach.Cancel(0, notify: false);
            _logicUpdater = null;
            _network = null;
            _gameManager = null;
            _locomotionCamera = null;
            _interactionApproach = null;
            _hasLastSent = false;
            _blockedMovementHeld = false;
        }

        private void OnClientTick(LogicUpdater updater)
        {
            if (!enableKeyboardInput || _network == null || !IsOwnerClient)
                return;

            if (_locomotionCamera == null)
                _locomotionCamera = GetComponent<PlayerEntityLocomotionCameraController>();

            Vector2 movement = Vector2.zero;
            float facingYaw = transform.eulerAngles.y;
            byte flags = 0;
            PlayerCombatInputFlags combatState = PlayerCombatInputFlags.None;
            float aimPitchDegrees = 0f;

            if (_locomotionCamera != null)
                _locomotionCamera.TryReadMovementIntent(out movement, out facingYaw, out flags);

            bool approachOwnsIntent = false;
            if (_interactionApproach != null)
            {
                approachOwnsIntent = _interactionApproach.TryOverrideMovementIntent(
                    out Vector2 approachMovement,
                    out float approachFacingYaw,
                    out byte approachFlags);
                if (approachOwnsIntent)
                {
                    movement = approachMovement;
                    facingYaw = approachFacingYaw;
                    flags = approachFlags;
                }
            }

            bool precisionRangedMode = false;
            bool knownEmpty = false;
            if (_gameManager != null)
            {
                PlayerCombatOwnerStateMessage ownerState = _gameManager.LatestCombatOwnerState;
                precisionRangedMode = ownerState.revision > 0 &&
                                      ownerState.Mode == BasicAttackMode.Firearm;
                knownEmpty = precisionRangedMode && ownerState.loadedRounds <= 0;
            }

            bool combatReady = !approachOwnsIntent &&
                               _locomotionCamera != null &&
                               _locomotionCamera.CombatCameraActive;
            if (combatReady)
                combatState |= PlayerCombatInputFlags.AimHeld;

            if (precisionRangedMode && combatReady &&
                !LocalClientInputGate.IsGameplayInputBlocked)
            {
                float precisionYaw = facingYaw;
                if (!_locomotionCamera.TryGetPrecisionAim(out precisionYaw, out aimPitchDegrees))
                    aimPitchDegrees = _locomotionCamera.CurrentAimPitchDegrees;

                bool pointerBlocked = EventSystem.current != null &&
                                      EventSystem.current.IsPointerOverGameObject();
                if (!pointerBlocked && !knownEmpty && Input.GetMouseButton(0))
                {
                    combatState |= PlayerCombatInputFlags.FireHeld;
                    facingYaw = precisionYaw;
                }
            }

            bool hasOwner = PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner);
            bool bakedWorldReady = SharedWorldClient.IsLoaded;
            bool requestedMovement = movement.sqrMagnitude > 0.0001f;
            bool jumpRequested = (flags & (byte)PlayerEntityFlags.Jumping) != 0;
            bool blockedByClient = false;

            if (!bakedWorldReady)
            {
                SharedWorldClient.TryResolveGroundedMove(
                    hasOwner ? owner.PresentationPosition : transform.position,
                    Vector3.zero, 0.35f, 0.40f, 0.50f, out _, out _);
            }

            if (!bakedWorldReady || !hasOwner)
            {
                blockedByClient = requestedMovement || jumpRequested;
                movement = Vector2.zero;
                flags = (byte)(flags & ~(byte)PlayerEntityFlags.Jumping);
            }
            // The canonical owner movement predictor already resolves grounded collision.
            // Do not run a second 75ms look-ahead against the visually interpolated
            // feet pose here: it can start partly inside an obstacle and reject
            // even a valid direction AWAY from the obstacle. This component only
            // serializes the resulting client position and intent. The GameServer
            // independently validates each submitted position against baked world.

            if (blockedByClient)
                suppressedBlockedCommands++;
            _blockedMovementHeld = blockedByClient;

            _gameManager?.UpdateLocalAutomaticFirePrediction(
                (combatState & PlayerCombatInputFlags.FireHeld) != 0,
                Time.realtimeSinceStartupAsDouble);

            bool fireHeld = (combatState & PlayerCombatInputFlags.FireHeld) != 0;
            byte combatInput = PlayerCombatInputEncoding.Encode(
                combatState, fireHeld ? aimPitchDegrees : 0f);

            // Serialize the current owner client-resolved feet pose. Do not
            // independently advance a second motor in the input serializer.
            Vector3 feetPosition = hasOwner ? owner.PresentationPosition : transform.position;
            if (!PlayerEntityQuantization.IsFinite(feetPosition))
                return;

            var command = new MovementCommand
            {
                sequence = 0,
                inputX = PlayerEntityQuantization.QuantizeInput(movement.x),
                inputZ = PlayerEntityQuantization.QuantizeInput(movement.y),
                yaw = PlayerEntityQuantization.QuantizeYaw(facingYaw),
                flags = flags,
                combatFlags = combatInput,
                positionX = PlayerEntityQuantization.QuantizePosition(feetPosition.x),
                positionY = PlayerEntityQuantization.QuantizePosition(feetPosition.y),
                positionZ = PlayerEntityQuantization.QuantizePosition(feetPosition.z),
            };

            double now = Time.realtimeSinceStartupAsDouble;
            if (!ShouldSend(command, facingYaw, now))
            {
                suppressedUnchangedCommands++;
                return;
            }

            command.sequence = ++_sequence;
            if (!_network.TrySendMovement(command))
                return;

            _lastSent = command;
            _hasLastSent = true;
            _lastSentAt = now;
            _lastContinuousSentAt = now;
            sentMovementCommands++;
        }

        private bool ShouldSend(in MovementCommand candidate, float facingYaw, double now)
        {
            if (!_hasLastSent)
                return true;

            // Changed position is movement data, not merely a different visual pose.
            // Avoid broadcasting tiny quantized changes while idle, but never
            // suppress actual locally integrated motion when direction is held.
            if (candidate.inputX != _lastSent.inputX ||
                candidate.inputZ != _lastSent.inputZ ||
                candidate.flags != _lastSent.flags ||
                candidate.combatFlags != _lastSent.combatFlags)
                return true;

            PlayerCombatInputFlags combatState =
                PlayerCombatInputEncoding.DecodeFlags(candidate.combatFlags);
            bool requiresLeaseRefresh =
                candidate.inputX != 0 || candidate.inputZ != 0 ||
                (combatState & PlayerCombatInputFlags.FireHeld) != 0;

            double refresh = Math.Max(0.10, unchangedRefreshSeconds);
            if ((requiresLeaseRefresh || _blockedMovementHeld) &&
                now - _lastSentAt >= refresh)
                return true;

            // Position corrections or gravity movement must be transmitted even
            // without a horizontal input; cap these at the existing wire cadence.
            bool positionChanged = candidate.positionX != _lastSent.positionX ||
                                   candidate.positionY != _lastSent.positionY ||
                                   candidate.positionZ != _lastSent.positionZ;
            if (positionChanged && now - _lastContinuousSentAt >=
                Math.Max(0.02, continuousUpdateIntervalSeconds))
                return true;

            float lastYaw = PlayerEntityQuantization.DequantizeYaw(_lastSent.yaw);
            float yawDelta = Mathf.Abs(Mathf.DeltaAngle(lastYaw, facingYaw));
            if (yawDelta < PlayerEntityQuantization.YawStepDegrees)
                return false;

            bool holsteredTpsIdle =
                _locomotionCamera != null &&
                _locomotionCamera.TpsCameraPresentationActive &&
                !_locomotionCamera.CombatCameraActive &&
                candidate.inputX == 0 && candidate.inputZ == 0 &&
                combatState == PlayerCombatInputFlags.None;
            if (holsteredTpsIdle)
                return false;

            return now - _lastContinuousSentAt >=
                Math.Max(0.02, continuousUpdateIntervalSeconds);
        }

        public void SetEnabled(bool value) => enableKeyboardInput = value;
    }
}
