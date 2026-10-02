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
    ///
    /// PlayerEntityLocomotionCameraController owns the local MMO locomotion/camera
    /// control contract. This component only serializes that intent into the existing
    /// compact MovementCommand. The standalone GameServer remains authoritative.
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
        [Tooltip("Use the baked shared-world support/collision package to suppress movement intent that the legitimate client already knows cannot advance.")]
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

        [Header("Runtime Wire Telemetry")]
        [SerializeField] private uint sentMovementCommands;
        [SerializeField] private uint suppressedUnchangedCommands;
        [SerializeField] private uint suppressedBlockedCommands;

        public uint SentMovementCommands => sentMovementCommands;
        public uint SuppressedUnchangedCommands => suppressedUnchangedCommands;
        public uint SuppressedBlockedCommands => suppressedBlockedCommands;

        /// <summary>
        /// Latest local facing intent. Used by presentation/camera consumers only;
        /// accepted facing remains authoritative on the standalone GameServer.
        /// </summary>
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

            if (_logicUpdater != null)
                _logicUpdater.OnTick += OnClientTick;
        }

        public override void OnNetworkDestroy(byte reasons) => Cleanup();
        public override void OnIdentityDestroy() => Cleanup();

        private void Update()
        {
            if (!enableKeyboardInput || _network == null || !IsOwnerClient || _gameManager == null)
                return;

            // Interaction auto-approach owns locomotion/facing temporarily. It deliberately
            // suppresses combat clicks without introducing a separate input or network path.
            if (_interactionApproach != null && _interactionApproach.IsActive)
                return;

            if (_locomotionCamera == null)
                _locomotionCamera = GetComponent<PlayerEntityLocomotionCameraController>();
            if (_locomotionCamera == null || !_locomotionCamera.CombatCameraActive)
                return;

            // Only intentional local UI capture suppresses combat. Combat no longer depends on
            // ClientUIRoot/PlayerUiInputController being alive or correctly wired.
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
                approachOwnsIntent =
                    _interactionApproach.TryOverrideMovementIntent(
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

            // Precision firearm intent rides the already-scheduled movement stream so full-auto
            // needs no per-round request. Melee/unarmed deliberately carry no aim/pitch state here:
            // their authoritative swing uses server-owned player facing + reach/arc instead.
            bool precisionRangedMode = false;
            bool knownEmpty = false;
            if (_gameManager != null)
            {
                PlayerCombatOwnerStateMessage ownerState = _gameManager.LatestCombatOwnerState;
                precisionRangedMode = ownerState.revision > 0 &&
                                      ownerState.Mode == Game.Shared.Abilities.BasicAttackMode.Firearm;
                knownEmpty = precisionRangedMode && ownerState.loadedRounds <= 0;
            }

            // Auto-approach is an interaction movement intent, not combat-ready intent.
            bool combatReady =
                !approachOwnsIntent &&
                _locomotionCamera != null &&
                _locomotionCamera.CombatCameraActive;

            if (combatReady)
                combatState |= PlayerCombatInputFlags.AimHeld;

            if (precisionRangedMode &&
                combatReady &&
                !LocalClientInputGate.IsGameplayInputBlocked)
            {
                float precisionYaw = facingYaw;
                if (!_locomotionCamera.TryGetPrecisionAim(out precisionYaw, out aimPitchDegrees))
                    aimPitchDegrees = _locomotionCamera.CurrentAimPitchDegrees;

                bool pointerBlocked =
                    EventSystem.current != null &&
                    EventSystem.current.IsPointerOverGameObject();

                if (!pointerBlocked && !knownEmpty && Input.GetMouseButton(0))
                {
                    combatState |= PlayerCombatInputFlags.FireHeld;
                    facingYaw = precisionYaw;
                }
            }

            // A legitimate client already has the immutable client-safe world bake. Use it
            // to avoid asking the server to advance into a static wall or beyond a true
            // support-surface edge. The server still independently simulates every accepted
            // command; this is bandwidth/prediction hygiene, never authority.
            bool jumpRequested = (flags & (byte)PlayerEntityFlags.Jumping) != 0;
            if (useSharedWorldPrediction && !jumpRequested && movement.sqrMagnitude > 0.0001f &&
                PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) &&
                owner.LatestAuthoritativeGrounded)
            {
                float speed = (flags & (byte)PlayerEntityFlags.Sprinting) != 0
                    ? PlayerGameplaySettingsRuntime.SprintSpeed
                    : PlayerGameplaySettingsRuntime.MoveSpeed;
                float magnitude = Mathf.Clamp01(movement.magnitude);
                Vector2 direction = magnitude > 0.0001f ? movement / magnitude : Vector2.zero;
                float previewSeconds = Mathf.Max(1f / 60f, Mathf.Min(0.08f, Time.unscaledDeltaTime));
                Vector3 desiredDelta = new Vector3(direction.x, 0f, direction.y) * speed * magnitude * previewSeconds;

                if (SharedWorldClient.TryResolveGroundedMove(
                        owner.PresentationPosition,
                        desiredDelta,
                        0.35f,
                        0.40f,
                        0.50f,
                        out _,
                        out bool blocked) && blocked)
                {
                    movement = Vector2.zero;
                    suppressedBlockedCommands++;
                }
            }

            _gameManager?.UpdateLocalAutomaticFirePrediction(
                (combatState & PlayerCombatInputFlags.FireHeld) != 0,
                Time.realtimeSinceStartupAsDouble);

            bool fireHeld = (combatState & PlayerCombatInputFlags.FireHeld) != 0;
            byte combatInput = PlayerCombatInputEncoding.Encode(
                combatState,
                fireHeld ? aimPitchDegrees : 0f);
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

            // Start/stop, directional key changes, sprint transitions and jump edges are
            // latency-sensitive and bypass the cadence limiter.
            if (candidate.inputX != _lastSent.inputX ||
                candidate.inputZ != _lastSent.inputZ ||
                candidate.flags != _lastSent.flags ||
                candidate.combatFlags != _lastSent.combatFlags)
            {
                return true;
            }

            // A truly idle client goes silent after sending the stop/neutral transition.
            // Held movement or combat state still needs a bounded lease refresh because the
            // standalone server intentionally expires stale input after a short timeout.
            // Sprint-without-movement and a held jump bit are not authority-significant once
            // their edge/neutral transition has been observed, so they do not keep the wire awake.
            PlayerCombatInputFlags combatState =
                PlayerCombatInputEncoding.DecodeFlags(candidate.combatFlags);
            bool requiresLeaseRefresh =
                candidate.inputX != 0 ||
                candidate.inputZ != 0 ||
                (combatState & PlayerCombatInputFlags.FireHeld) != 0;

            double refresh = Math.Max(0.10, unchangedRefreshSeconds);
            if (requiresLeaseRefresh && now - _lastSentAt >= refresh)
                return true;

            // Use one transmitted yaw step as a hysteresis band around the last sent
            // orientation. This prevents boundary chatter from tiny camera jitter while
            // still matching the actual 8-bit precision visible to remote clients.
            float lastYaw = PlayerEntityQuantization.DequantizeYaw(_lastSent.yaw);
            float yawDelta = Mathf.Abs(Mathf.DeltaAngle(lastYaw, facingYaw));
            if (yawDelta < PlayerEntityQuantization.YawStepDegrees)
                return false;

            // Holstered TPS camera yaw is presentation state and must not wake the movement
            // stream while the player is idle. Once movement/input changes, the current yaw
            // piggybacks the already-required MovementCommand and remains independently
            // server-validated. Weapon-ready combat continues using the normal combat cadence.
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
