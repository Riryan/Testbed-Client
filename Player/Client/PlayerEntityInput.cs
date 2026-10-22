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
    /// Thin local PlayerEntity movement serializer.
    /// PlayerEntityLocomotionCameraController owns locomotion and camera input;
    /// this component sends compact intent. The GameServer remains authoritative.
    /// </summary>
    public sealed class PlayerEntityInput : LiteNetLibBehaviour
    {
        public bool enableKeyboardInput = true;

        [Header("Movement Wire Cadence")]
        [Tooltip("Maximum rate for continuously changing yaw/direction. Start/stop/sprint/jump changes still send immediately.")]
        [Range(0.02f, 0.20f)]
        public float continuousUpdateIntervalSeconds = 0.05f;

        [Tooltip("Safety refresh for an unchanged held input state. This protects against a lost sequenced UDP command without resending every simulation tick.")]
        [Range(0.10f, 0.50f)]
        public float unchangedRefreshSeconds = 0.20f;

        [Header("Client-Safe World Prediction")]
        [Tooltip("Legacy inspector option retained for prefab compatibility. Authoritative-safe local movement rejection now always runs; it cannot be disabled.")]
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
                                      ownerState.Mode == Game.Shared.Abilities.BasicAttackMode.Firearm;
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

            // All legitimate movement must be checked against the client-safe world.
            // Loading/missing data is NOT permission to move: send an existing neutral
            // MovementCommand until the bake is ready. Never skip this gate because of
            // the legacy prediction inspector toggle.
            bool hasOwner = PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner);
            bool bakedWorldReady = SharedWorldClient.IsLoaded;
            bool requestedMovement = movement.sqrMagnitude > 0.0001f;
            bool jumpRequested = (flags & (byte)PlayerEntityFlags.Jumping) != 0;
            bool blockedByClient = false;

            if (!bakedWorldReady)
            {
                // The query also kicks off the existing asynchronous bake load.
                // Passing zero delta avoids any speculative movement while loading.
                SharedWorldClient.TryResolveGroundedMove(
                    hasOwner ? owner.PresentationPosition : transform.position,
                    Vector3.zero, 0.35f, 0.40f, 0.50f, out _, out _);
            }

            if (!bakedWorldReady || !hasOwner)
            {
                // World entry, scene transition, missing bake or owner not ready.
                // Do not submit horizontal movement OR jump until locally validated.
                blockedByClient = requestedMovement || jumpRequested;
                movement = Vector2.zero;
                flags = (byte)(flags & ~(byte)PlayerEntityFlags.Jumping);
            }
            else if (requestedMovement && !jumpRequested && owner.LatestAuthoritativeGrounded)
            {
                float speed = (flags & (byte)PlayerEntityFlags.Sprinting) != 0
                    ? PlayerGameplaySettingsRuntime.SprintSpeed
                    : PlayerGameplaySettingsRuntime.MoveSpeed;
                float magnitude = Mathf.Clamp01(movement.magnitude);
                Vector2 direction = magnitude > 0.0001f ? movement / magnitude : Vector2.zero;
                // Full 20Hz server step plus modest input/arrival phase skew.
                const float PreviewSeconds = 0.075f;
                Vector3 desiredDelta = new Vector3(direction.x, 0f, direction.y) *
                                       speed * magnitude * PreviewSeconds;

                if (!SharedWorldClient.TryResolveGroundedMove(
                        owner.PresentationPosition, desiredDelta,
                        0.35f, 0.40f, 0.50f,
                        out _, out bool blocked) || blocked)
                {
                    movement = Vector2.zero;
                    blockedByClient = true;
                }
            }

            if (blockedByClient)
                suppressedBlockedCommands++;
            _blockedMovementHeld = blockedByClient;

            _gameManager?.UpdateLocalAutomaticFirePrediction(
                (combatState & PlayerCombatInputFlags.FireHeld) != 0,
                Time.realtimeSinceStartupAsDouble);

            bool fireHeld = (combatState & PlayerCombatInputFlags.FireHeld) != 0;
            byte combatInput = PlayerCombatInputEncoding.Encode(
                combatState, fireHeld ? aimPitchDegrees : 0f);
            var command = new MovementCommand
            {
                sequence = 0,
                inputX = PlayerEntityQuantization.QuantizeInput(movement.x),
                inputZ = PlayerEntityQuantization.QuantizeInput(movement.y),
                yaw = PlayerEntityQuantization.QuantizeYaw(facingYaw),
                flags = flags,
                combatFlags = combatInput,
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

            // A zero input generated by local collision suppression is an immediate
            // STOP transition. It bypasses continuous-input cadence throttling.
            if (candidate.inputX != _lastSent.inputX ||
                candidate.inputZ != _lastSent.inputZ ||
                candidate.flags != _lastSent.flags ||
                candidate.combatFlags != _lastSent.combatFlags)
                return true;

            PlayerCombatInputFlags combatState =
                PlayerCombatInputEncoding.DecodeFlags(candidate.combatFlags);
            bool requiresLeaseRefresh =
                candidate.inputX != 0 ||
                candidate.inputZ != 0 ||
                (combatState & PlayerCombatInputFlags.FireHeld) != 0;

            double refresh = Math.Max(0.10, unchangedRefreshSeconds);
            // A neutral STOP can be lost on sequenced UDP. While the physical input
            // remains blocked, resend the existing neutral intent at the established
            // refresh cadence until it is no longer held. Never retransmit invalid motion.
            if ((requiresLeaseRefresh || _blockedMovementHeld) &&
                now - _lastSentAt >= refresh)
                return true;

            float lastYaw = PlayerEntityQuantization.DequantizeYaw(_lastSent.yaw);
            float yawDelta = Mathf.Abs(Mathf.DeltaAngle(lastYaw, facingYaw));
            if (yawDelta < PlayerEntityQuantization.YawStepDegrees)
                return false;

            bool holsteredTpsIdle =
                _locomotionCamera != null &&
                _locomotionCamera.TpsCameraPresentationActive &&
                !_locomotionCamera.CombatCameraActive &&
                candidate.inputX == 0 &&
                candidate.inputZ == 0 &&
                combatState == PlayerCombatInputFlags.None;
            if (holsteredTpsIdle)
                return false;

            return now - _lastContinuousSentAt >= Math.Max(0.02, continuousUpdateIntervalSeconds);
        }

        public void SetEnabled(bool value) => enableKeyboardInput = value;
    }
}
