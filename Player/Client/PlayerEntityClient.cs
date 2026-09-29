using System;
using System.Collections.Generic;
using LiteNetLibManager;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Player.Client.Presentation;
using Player.Networking;
using Player.Shared;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// High-quality remote PlayerEntity presentation.
    ///
    /// Authority remains entirely server-side. Network snapshots are buffered on the
    /// client and rendered on a continuously advancing server-tick timeline.
    ///
    /// Features:
    /// - adaptive jitter buffer
    /// - continuous render timeline between packets
    /// - cubic Hermite position interpolation
    /// - cubic yaw interpolation with angular-velocity reconstruction
    /// - bounded extrapolation
    /// - graceful velocity decay after the extrapolation horizon
    /// - smooth correction instead of packet-to-packet snapping
    /// - teleport/generation reset handling
    /// - centralized render-frame update system
    /// </summary>
    public sealed class PlayerEntityClient : LiteNetLibBehaviour
    {
        private static readonly List<PlayerEntityClient> ActiveClientInstances = new List<PlayerEntityClient>(64);
        private static readonly int BaseColorShaderId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorShaderId = Shader.PropertyToID("_Color");
        private static readonly Color HiddenPresentationColor = new Color(0.45f, 0.45f, 0.45f, 1f);

        /// <summary>
        /// Event-driven client registry for spawned PlayerEntity presentations. Client UI/
        /// targeting code should use this registry instead of scene-wide object searches.
        /// </summary>
        public static IReadOnlyList<PlayerEntityClient> ActiveClients => ActiveClientInstances;

        public static bool TryGetByNetwork(PlayerEntityNetwork network, out PlayerEntityClient client)
        {
            client = null;
            if (network == null)
                return false;

            for (int i = 0; i < ActiveClientInstances.Count; ++i)
            {
                PlayerEntityClient candidate = ActiveClientInstances[i];
                if (candidate != null && ReferenceEquals(candidate._network, network))
                {
                    client = candidate;
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetByPresentationTransform(Transform hit, out PlayerEntityClient client)
        {
            client = null;
            if (hit == null)
                return false;

            for (int i = 0; i < ActiveClientInstances.Count; ++i)
            {
                PlayerEntityClient candidate = ActiveClientInstances[i];
                Transform root = candidate != null ? candidate.PresentationTransform : null;
                if (root != null && (hit == root || hit.IsChildOf(root)))
                {
                    client = candidate;
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetPopulationPresentation(
            long actorId,
            ushort generation,
            out PlayerEntityClient client)
        {
            client = null;
            if (actorId <= 0 || generation == 0)
                return false;

            for (int i = 0; i < ActiveClientInstances.Count; ++i)
            {
                PlayerEntityClient candidate = ActiveClientInstances[i];
                if (candidate != null &&
                    candidate.IsPopulationPresentation &&
                    candidate.PopulationActorId == actorId &&
                    candidate.PopulationGeneration == generation)
                {
                    client = candidate;
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetOwner(out PlayerEntityClient client)
        {
            client = null;
            for (int i = 0; i < ActiveClientInstances.Count; ++i)
            {
                PlayerEntityClient candidate = ActiveClientInstances[i];
                if (candidate != null && candidate._network != null && candidate._network.IsSpawned && candidate.IsOwnerClient)
                {
                    client = candidate;
                    return true;
                }
            }
            return false;
        }

        public static event Action<PlayerEntityClient> ActiveClientRegistered;
        public static event Action<PlayerEntityClient> ActiveClientUnregistered;

        public event Action<PlayerEntityClient> DisplayNameChanged;

        /// <summary>Current rendered/predicted root position for owner-side input prediction.</summary>
        public Vector3 PresentationPosition => _visual != null ? _visual.position : transform.position;

        /// <summary>Latest server-confirmed grounded state. Used only to decide whether client-side static-world suppression is safe.</summary>
        public bool LatestAuthoritativeGrounded =>
            _count > 0 && ((((PlayerEntityFlags)GetSample(_count - 1).snapshot.flags) & PlayerEntityFlags.Grounded) != 0);

        private const int BufferCapacity = 64;
        private const double JitterEwmaFactor = 1.0 / 16.0;

        private struct BufferedSnapshot
        {
            public PlayerEntitySnapshot snapshot;
            public double unwrappedTick;
            public double arrivalTime;
        }

        [Header("Maximum Smoothness Profile")]
        [Tooltip("Keeps the public tuning fields available, but enforces conservative minimums suitable for very jittery remote players.")]
        public bool maximumSmoothnessProfile = true;

        [Header("Adaptive Interpolation")]
        [Range(0, 8)]
        public uint ownerInterpolationTicks = 1;

        [Range(1, 8)]
        public uint baseInterpolationTicks = 2;

        [Range(2, 16)]
        public uint maximumInterpolationTicks = 10;

        [Range(1f, 6f)]
        public float jitterSafetyMultiplier = 3.0f;

        [Tooltip("How quickly the client may add buffer when the connection becomes unstable.")]
        [Range(0.02f, 1f)]
        public float delayIncreaseResponseSeconds = 0.10f;

        [Tooltip("Buffer is removed slowly so a recovering connection does not visually surge forward.")]
        [Range(0.25f, 10f)]
        public float delayDecreaseResponseSeconds = 3.0f;

        [Header("Interpolation Curve")]
        [Range(0f, 1f)]
        public float hermiteStrength = 0.90f;

        [Range(0.1f, 1.5f)]
        public float hermiteTangentScale = 0.85f;

        [Min(0.05f)]
        public float maximumHermiteDeviation = 0.75f;

        [Min(1f)]
        public float maximumReconstructedSpeed = 20f;

        [Min(90f)]
        public float maximumReconstructedAngularSpeed = 1080f;

        [Header("Loss / Extrapolation")]
        [Tooltip("Full-velocity prediction horizon when snapshots temporarily stop.")]
        [Range(0f, 0.5f)]
        public float maxExtrapolationSeconds = 0.25f;

        [Tooltip("After full extrapolation, velocity is smoothly decayed to zero over this additional period instead of freezing abruptly.")]
        [Range(0f, 0.75f)]
        public float extrapolationCoastSeconds = 0.30f;

        [Tooltip("An extrapolation event temporarily increases interpolation delay so repeated packet gaps are less visible.")]
        [Range(0f, 2f)]
        public float extrapolationPenaltyMultiplier = 1.25f;

        [Header("Presentation Correction")]
        [Range(0.005f, 0.2f)]
        public float positionSmoothTime = 0.035f;

        [Range(0.005f, 0.2f)]
        public float ownerPositionSmoothTime = 0.020f;

        [Range(0.005f, 0.2f)]
        public float rotationSmoothTime = 0.040f;

        [Min(1f)]
        public float maximumCorrectionSpeed = 40f;

        [Min(1f)]
        public float hardSnapDistance = 15f;

        [Header("Teleport Detection")]
        [Min(0.1f)]
        public float teleportDistance = 8f;

        [Tooltip("A large position gap is treated as normal delayed movement unless its implied speed also exceeds this value.")]
        [Min(1f)]
        public float teleportSpeedThreshold = 30f;

        [Header("Authoritative Stop Handling")]
        [Tooltip("Snapshots at or below this movement speed are treated as position-stopped for velocity reconstruction.")]
        [Min(0f)]
        public float stoppedMoveSpeedEpsilon = 0.05f;

        [Tooltip("Maximum incoming tangent, as a multiple of final-segment distance, used by the monotonic stop curve.")]
        [Range(0.5f, 4f)]
        public float stopTangentDistanceMultiplier = 1.5f;

        [Tooltip("Use a slightly slower correction when an authoritative stop arrives after prior extrapolation, avoiding a visible sling-back.")]
        [Range(0.02f, 0.20f)]
        public float stoppedCorrectionSmoothTime = 0.070f;

        [Header("Local Movement Prediction")]
        [Tooltip("Presentation-only prediction for the locally controlled player. The GameServer remains authoritative.")]
        public bool localPrediction = true;

        [Tooltip("How far ahead of the newest authoritative snapshot the local presentation may lead, measured in server ticks.")]
        [Range(0f, 2f)]
        public float localPredictionLeadTicks = 1f;

        [Tooltip("Hard ceiling for continuous owner prediction if snapshots are late. Normal presentation advances every render frame; this only bounds extended packet starvation.")]
        [Range(0.05f, 0.50f)]
        public float localPredictionMaxSeconds = 0.35f;

        [Header("Local Ground Presentation")]
        [Tooltip("Presentation-only one-ray-per-render-frame ground probe for the local owner. This keeps the locally rendered feet on the same visible floor/platform that the authoritative server resolved, without adding network traffic.")]
        public bool localGroundPresentationProbe = true;

        [Tooltip("World layers eligible for the local owner ground presentation probe. The owner hierarchy is filtered out even if its layer is included.")]
        public LayerMask localGroundPresentationLayers = Physics.DefaultRaycastLayers;

        [Tooltip("Probe origin height above the higher of local predicted feet and latest authoritative feet.")]
        [Range(0.10f, 1.50f)]
        public float localGroundProbeUp = 0.75f;

        [Tooltip("Maximum distance below the probe origin considered for local ground presentation.")]
        [Range(0.25f, 3.00f)]
        public float localGroundProbeDown = 1.50f;

        [Tooltip("A scene-physics ground hit must be this close to the server-confirmed feet height. This prevents a low floor or overhead prop from winning when stacked geometry exists.")]
        [Range(0.05f, 1.50f)]
        public float localGroundAuthoritativeTolerance = 0.75f;

        [Header("Locomotion Camera - Exploration")]
        [Tooltip("Client-only. Enables the local exploration follow/free-look camera for the local owner. Remote players ignore these settings.")]
        public bool explorationCameraEnabled = true;

        [Tooltip("Local-space pivot around the rendered humanoid. Raise/lower this to frame the upper body/head correctly.")]
        public Vector3 explorationCameraPivotOffset = new Vector3(0f, 1.55f, 0f);

        [Tooltip("Horizontal/vertical camera-space framing offset. X is shoulder/side offset; Y raises or lowers the camera without moving the pivot.")]
        public Vector2 explorationCameraFramingOffset = Vector2.zero;

        [Min(0.25f)] public float explorationCameraDistance = 7.5f;
        [Min(0.25f)] public float explorationCameraMinimumDistance = 2.5f;
        [Min(0.5f)] public float explorationCameraMaximumDistance = 16f;
        [Min(0.01f)] public float explorationCameraZoomSpeed = 0.8f;

        [Header("Exploration Camera Mouse")]
        [Tooltip("LMB-style free look: orbit the camera without changing authoritative character facing.")]
        public bool explorationCameraFreeLookEnabled = true;
        [Range(0, 2)] public int explorationCameraFreeLookMouseButton = 0;
        [Min(0f)] public float explorationCameraFreeLookDragThreshold = 40f;

        [Tooltip("RMB-style forced look: rotate camera yaw and send that yaw as the local character-facing intent.")]
        [Range(0, 2)] public int explorationCameraOrbitMouseButton = 1;
        [Min(0.1f)] public float explorationCameraHorizontalSensitivity = 3f;
        [Min(0.1f)] public float explorationCameraVerticalSensitivity = 2.25f;
        [Range(-80f, 20f)] public float explorationCameraMinimumPitch = -20f;
        [Range(20f, 85f)] public float explorationCameraMaximumPitch = 72f;
        public float explorationCameraInitialPitch = 18f;
        public bool explorationCameraLockCursorWhileOrbiting = true;

        [Header("MMO Locomotion Input")]
        [Tooltip("Interpret WASD in current character-facing space. The server still receives world-space intent and remains authoritative.")]
        public bool characterRelativeMovement = true;
        [Tooltip("Classic MMO behavior: holding both mouse buttons is an explicit forward movement command while in Exploration mode.")]
        public bool runForwardWithBothMouseButtons = true;

        [Header("Exploration Camera Follow")]
        [Min(0.001f)] public float explorationCameraFollowSmoothTime = 0.045f;
        public bool explorationCameraRecenterBehindWhenMoving = true;
        [Min(1f)] public float explorationCameraRecenterDegreesPerSecond = 300f;

        [Header("Combat Camera Foundation")]
        [Tooltip("Client-only camera/locomotion stance. This does not enter combat or mutate authoritative combat state.")]
        public bool combatCameraEnabled = true;
        [Tooltip("Matches the prior PlayerCharacterControllerMovement Action Combat stance toggle. Target cycling is moved to T by the client UI input router.")]
        public KeyCode combatCameraToggleKey = KeyCode.Tab;
        public bool combatCameraLockCursor = true;
        [Tooltip("When enabled, Combat camera yaw becomes the local character-facing intent. W/S follow facing; A/D strafe.")]
        public bool combatCameraCharacterFollowsYaw = true;
        [Tooltip("Camera-space/character-yaw-space shoulder offset applied to the normal exploration pivot.")]
        public Vector3 combatCameraPivotOffset = new Vector3(0.55f, -0.20f, 0f);
        [Min(0.5f)] public float combatCameraDistance = 3.6f;
        [Min(0.01f)] public float combatCameraBlendTime = 0.22f;

        [Header("Exploration Camera Obstruction")]
        [Tooltip("Disabled by default for the first framing pass. Enable after the camera distance/pivot are correct, then choose only real world-blocking layers.")]
        public bool explorationCameraObstructionEnabled = false;
        public LayerMask explorationCameraViewBlockingLayers = Physics.DefaultRaycastLayers;
        [Min(0f)] public float explorationCameraCollisionRadius = 0.18f;
        [Min(0f)] public float explorationCameraCollisionPadding = 0.08f;
        [Tooltip("Never let an obstruction result collapse the camera closer than this. This prevents overlap hits from putting the camera inside the avatar.")]
        [Min(0.1f)] public float explorationCameraMinimumCollisionDistance = 1.25f;

        [Header("Debug Presentation")]
        public bool createDebugVisual = true;

        private PlayerEntityNetwork _network;
        private PlayerEntityPresentationSystem _presentationSystem;
        private readonly BufferedSnapshot[] _buffer = new BufferedSnapshot[BufferCapacity];

        private int _count;
        private int _head;
        private ushort _generation;

        private double _lastArrivalTime;
        private double _arrivalJitterSeconds;
        private double _dynamicDelaySeconds;
        private double _desiredDelaySeconds;
        private double _extrapolationPenaltySeconds;

        private Transform _visual;
        private HumanoidAnimatorPresentationAdapter _animatorPresentation;
        private string _displayName;
        private bool _hiddenPresentation;
        private MaterialPropertyBlock _hiddenPresentationBlock;
        private HumanoidCombatStance _equipmentCombatStance = HumanoidCombatStance.Unarmed;
        private long _stanceGameplaySettingsRevision = -1;

        private Vector3 _smoothDampVelocity;
        private float _smoothDampYawVelocity;
        private bool _forcePresentationSnap;
        private bool _isExtrapolating;
        private float _extrapolationSeconds;
        private float _lastPresentationError;
        private Vector3 _localPredictedVelocity;
        private float _localPredictedSpeed;
        private byte _localPredictedFlags;
        private bool _localPredictionActive;
        private Vector3 _ownerPredictionPosition;
        private bool _ownerPredictionInitialized;
        private double _ownerPredictionLastTime;
        private Vector3 _renderedWorldVelocity;
        private readonly RaycastHit[] _ownerGroundProbeHits = new RaycastHit[12];
        private float _visualYaw;
        private bool _telemetryRegistered;
        private bool _activeClientRegistered;

        // Owner-local client locomotion/camera behavior is installed alongside this
        // presentation component. It never owns authoritative movement state.
        private PlayerEntityLocomotionCameraController _locomotionCameraController;

        // Client-only combat presentation hysteresis for Population actors. Authoritative combat
        // cadence/state remains server-owned; this only prevents the humanoid from visibly
        // relaxing between deliberately slow attacks that the client has already observed.
        private const double PopulationCombatReadyPresentationHoldSeconds = 5.0;
        private double _populationCombatReadyPresentationUntil;

        public string DisplayName => _displayName;
        public PlayerEntityNetwork NetworkBridge => _network;

        /// <summary>
        /// Humanoid Population deliberately reuses the exact PlayerEntity network prefab and
        /// presentation path. A server-owned/unowned PlayerEntity (ConnectionId &lt; 0) carrying
        /// a canonical actor id in the existing appearance characterId field is a Population
        /// presentation, not a Player target. No extra presentation message is required.
        /// </summary>
        public bool IsPopulationPresentation =>
            _network != null &&
            _network.IsSpawned &&
            _network.ConnectionId < 0 &&
            _network.Appearance.characterId > 0;

        public long PopulationActorId => IsPopulationPresentation
            ? _network.Appearance.characterId
            : 0L;

        public ushort PopulationGeneration => IsPopulationPresentation
            ? _network.Generation
            : (ushort)0;

        /// <summary>
        /// Public presentation-state view for client systems that already depend on Player.Client.
        /// Keeps Player.Shared protocol enums inside the Player assembly boundary instead of
        /// forcing higher-level Game.Client assemblies to reference Player.Shared directly.
        /// This is presentation/preflight state only and is never gameplay authority.
        /// </summary>
        public bool IsDeadPresentation
        {
            get
            {
                if (_network == null || !_network.IsSpawned)
                    return false;

                PlayerEntitySnapshot snapshot = _network.Snapshot;
                return (PlayerEntityMoveState)snapshot.moveState == PlayerEntityMoveState.Dead ||
                       (PlayerEntityActionState)snapshot.actionState == PlayerEntityActionState.Dead;
            }
        }

        public bool IsTargetableRemotePlayer =>
            _network != null &&
            _network.IsSpawned &&
            !IsOwnerClient &&
            !IsPopulationPresentation &&
            _network.ObjectId != 0 &&
            _network.Generation != 0;

        /// <summary>
        /// Current client-only rendered presentation root. This is intentionally separate
        /// from the network identity transform because interpolation owns the presentation.
        /// Client camera/presentation systems may follow it but must never use it as authority.
        /// </summary>
        public Transform PresentationTransform => _visual;
        public float PresentationYaw => _visual != null ? _visual.eulerAngles.y : transform.eulerAngles.y;

        internal bool PresentPrimaryCombatAction(ushort sequence)
        {
            if (_visual == null)
                return false;

            if (IsPopulationPresentation)
                _populationCombatReadyPresentationUntil = Time.realtimeSinceStartupAsDouble + PopulationCombatReadyPresentationHoldSeconds;

            if (_animatorPresentation == null)
            {
                _animatorPresentation = _visual.GetComponent<HumanoidAnimatorPresentationAdapter>();
                if (_animatorPresentation == null && _visual.GetComponentInChildren<Animator>(true) != null)
                    _animatorPresentation = _visual.gameObject.AddComponent<HumanoidAnimatorPresentationAdapter>();
            }

            if (_animatorPresentation == null)
                return false;

            if (_stanceGameplaySettingsRevision != PlayerGameplaySettingsRuntime.Revision && _network != null)
                ResolveEquipmentCombatStance(_network.Appearance.equipmentVisuals);

            HumanoidCombatStance stance = _equipmentCombatStance == HumanoidCombatStance.None
                ? HumanoidCombatStance.Unarmed
                : _equipmentCombatStance;
            return _animatorPresentation.PlayPrimaryCombatAction(stance, sequence);
        }

        internal bool PresentReloadCombatAction(byte sequence = 0)
        {
            if (_visual == null)
                return false;

            if (_animatorPresentation == null)
            {
                _animatorPresentation = _visual.GetComponent<HumanoidAnimatorPresentationAdapter>();
                if (_animatorPresentation == null && _visual.GetComponentInChildren<Animator>(true) != null)
                    _animatorPresentation = _visual.gameObject.AddComponent<HumanoidAnimatorPresentationAdapter>();
            }

            if (_animatorPresentation == null)
                return false;

            if (_stanceGameplaySettingsRevision != PlayerGameplaySettingsRuntime.Revision && _network != null)
                ResolveEquipmentCombatStance(_network.Appearance.equipmentVisuals);

            return _animatorPresentation.PlayReloadCombatAction(_equipmentCombatStance, sequence);
        }

        /// <summary>
        /// Applies owner-local facing prediction to the rendered humanoid only.
        /// Position and accepted facing remain authoritative on the standalone
        /// GameServer; this removes the server/interpolation round trip from local
        /// camera/turn presentation. Remote players still use snapshot yaw.
        /// </summary>
        internal void ApplyOwnerLocalFacing(float yawDegrees)
        {
            if (!IsOwnerClient || _visual == null)
                return;

            _visualYaw = Mathf.Repeat(yawDegrees, 360f);
            _smoothDampYawVelocity = 0f;
            _visual.rotation = Quaternion.Euler(0f, _visualYaw, 0f);
        }

        private float ResolvePresentationYaw(float authoritativeYaw)
        {
            if (IsOwnerClient &&
                _locomotionCameraController != null &&
                _locomotionCameraController.HasFacingState)
            {
                return _locomotionCameraController.CurrentFacingYaw;
            }

            return authoritativeYaw;
        }
        [ContextMenu("Reset Locomotion Camera Tuning")]
        private void ResetExplorationCameraTuning()
        {
            explorationCameraEnabled = true;
            explorationCameraPivotOffset = new Vector3(0f, 1.55f, 0f);
            explorationCameraFramingOffset = Vector2.zero;
            explorationCameraDistance = 7.5f;
            explorationCameraMinimumDistance = 2.5f;
            explorationCameraMaximumDistance = 16f;
            explorationCameraZoomSpeed = 0.8f;
            explorationCameraFreeLookEnabled = true;
            explorationCameraFreeLookMouseButton = 0;
            explorationCameraFreeLookDragThreshold = 40f;
            explorationCameraOrbitMouseButton = 1;
            explorationCameraHorizontalSensitivity = 3f;
            explorationCameraVerticalSensitivity = 2.25f;
            explorationCameraMinimumPitch = -20f;
            explorationCameraMaximumPitch = 72f;
            explorationCameraInitialPitch = 18f;
            explorationCameraLockCursorWhileOrbiting = true;
            characterRelativeMovement = true;
            runForwardWithBothMouseButtons = true;
            explorationCameraFollowSmoothTime = 0.045f;
            explorationCameraRecenterBehindWhenMoving = true;
            explorationCameraRecenterDegreesPerSecond = 300f;
            combatCameraEnabled = true;
            combatCameraToggleKey = KeyCode.Tab;
            combatCameraLockCursor = true;
            combatCameraCharacterFollowsYaw = true;
            combatCameraPivotOffset = new Vector3(0.55f, -0.20f, 0f);
            combatCameraDistance = 3.6f;
            combatCameraBlendTime = 0.22f;
            explorationCameraObstructionEnabled = false;
            explorationCameraViewBlockingLayers = Physics.DefaultRaycastLayers;
            explorationCameraCollisionRadius = 0.18f;
            explorationCameraCollisionPadding = 0.08f;
            explorationCameraMinimumCollisionDistance = 1.25f;
        }

        public int BufferedSnapshots => _count;

        public uint DynamicInterpolationTicks
        {
            get
            {
                double tick = GetTickSeconds();
                if (tick <= 0.0001)
                    return 0;
                return (uint)Math.Max(0, Math.Round(_dynamicDelaySeconds / tick));
            }
        }

        public float InterpolationDelayMilliseconds => (float)(_dynamicDelaySeconds * 1000.0);
        public float EstimatedJitterMilliseconds => (float)(_arrivalJitterSeconds * 1000.0);
        public bool IsExtrapolating => _isExtrapolating;
        public float ExtrapolationMilliseconds => _extrapolationSeconds * 1000f;
        public float PresentationError => _lastPresentationError;

        public override void OnStartOwnerClient()
        {
            // Canonical local camera/locomotion presentation lives on the owner
            // PlayerEntity itself. There is no global orbit-camera bootstrap.
            _locomotionCameraController = GetComponent<PlayerEntityLocomotionCameraController>();
            if (_locomotionCameraController == null)
                _locomotionCameraController = gameObject.AddComponent<PlayerEntityLocomotionCameraController>();

            _locomotionCameraController.enabled = true;
        }

        public override void OnStartClient()
        {
            _network = GetComponent<PlayerEntityNetwork>();
            if (_network == null)
                return;

            RegisterActiveClient();
            _network.SnapshotChanged += OnSnapshotChanged;
            _network.AppearanceChanged += OnAppearanceChanged;

            double tick = GetTickSeconds();
            _dynamicDelaySeconds = GetBaseDelaySeconds(tick);
            _desiredDelaySeconds = _dynamicDelaySeconds;

            if (PlayerEntityGameManager.HasArg(Environment.GetCommandLineArgs(), "-mmoBot"))
                createDebugVisual = false;

            if (createDebugVisual)
                EnsureVisual();

            // LiteNetLib deserializes spawn-baseline sync fields before OnStartClient() is
            // invoked. Subscribing above therefore cannot observe those initial onChange
            // callbacks. Hydrate explicitly from the already-deserialized network state after
            // the presentation target exists, so AOI re-entry starts at the authoritative
            // baseline instead of showing a pooled/stale position until the next delta.
            ApplyInitialNetworkState();

            _presentationSystem = PlayerEntityPresentationSystem.GetOrCreate(Manager);
            _presentationSystem?.Register(this);

            if (!_telemetryRegistered)
            {
                MMORemotePresentationTelemetry.RecordSpawn(IsOwnerClient);
                _telemetryRegistered = true;
            }
        }

        public override void OnNetworkDestroy(byte reasons) => Cleanup();
        public override void OnIdentityDestroy() => Cleanup();

        private void Cleanup()
        {
            if (_telemetryRegistered)
            {
                MMORemotePresentationTelemetry.RecordDestroy(IsOwnerClient);
                _telemetryRegistered = false;
            }

            _presentationSystem?.Unregister(this);
            _presentationSystem = null;

            UnregisterActiveClient();

            if (_network != null)
            {
                _network.SnapshotChanged -= OnSnapshotChanged;
                _network.AppearanceChanged -= OnAppearanceChanged;
            }

            _network = null;
            ResetBuffer();
            _generation = 0;
            _displayName = string.Empty;
            _forcePresentationSnap = true;
            if (_locomotionCameraController != null)
                _locomotionCameraController.enabled = false;
            _locomotionCameraController = null;

            if (_visual != null)
                Destroy(_visual.gameObject);

            _visual = null;
            _animatorPresentation = null;
            DisplayNameChanged = null;
        }


        private void RegisterActiveClient()
        {
            if (_activeClientRegistered)
                return;

            _activeClientRegistered = true;
            if (!ActiveClientInstances.Contains(this))
                ActiveClientInstances.Add(this);
            ActiveClientRegistered?.Invoke(this);
        }

        private void UnregisterActiveClient()
        {
            if (!_activeClientRegistered)
                return;

            _activeClientRegistered = false;
            ActiveClientInstances.Remove(this);
            ActiveClientUnregistered?.Invoke(this);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetActiveClientRegistry()
        {
            ActiveClientInstances.Clear();
            ActiveClientRegistered = null;
            ActiveClientUnregistered = null;
        }

        private void ApplyInitialNetworkState()
        {
            if (_network == null)
                return;

            PlayerEntitySnapshot snapshot = _network.Snapshot;
            if (snapshot.entityId != 0 && snapshot.generation != 0)
                OnSnapshotChanged(true, default, snapshot);

            PlayerEntityAppearance appearance = _network.Appearance;
            if (appearance.generation != 0)
                OnAppearanceChanged(true, default, appearance);
        }

        private void ResetBuffer()
        {
            _count = 0;
            _head = 0;
            _lastArrivalTime = 0.0;
            _arrivalJitterSeconds = 0.0;
            _extrapolationPenaltySeconds = 0.0;
            _isExtrapolating = false;
            _extrapolationSeconds = 0f;
            _smoothDampVelocity = Vector3.zero;
            _smoothDampYawVelocity = 0f;
        }

        private void EnsureVisual()
        {
            if (_visual != null)
                return;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "ClientPresentation";

            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            _visual = go.transform;
            _visual.localScale = new Vector3(0.75f, 1f, 0.75f);
        }

        private void OnAppearanceChanged(
            bool initial,
            PlayerEntityAppearance oldValue,
            PlayerEntityAppearance value)
        {
            if (_generation != 0 && value.generation != _generation)
                return;

            string previousDisplayName = _displayName;
            string resolvedDisplayName = value.displayName ?? string.Empty;

            // Ambient Population source/spawn labels are useful server diagnostics, not
            // player-facing identity. Derive the display name locally from the existing
            // actor id + local appearance/name catalogs. This adds zero naming wire cost.
            if (IsPopulationPresentation)
            {
                if (!PlayerEntityVisualFactoryRegistry.TryResolvePopulationDisplayName(
                        value.characterId,
                        value.appearance,
                        out resolvedDisplayName))
                {
                    resolvedDisplayName = "Person";
                }
            }

            _displayName = resolvedDisplayName;
            if (!string.Equals(previousDisplayName, _displayName, StringComparison.Ordinal))
                DisplayNameChanged?.Invoke(this);

            _network?.NotifyOwnerAppearanceObserved(value);

            // Population death-loot availability is carried by the existing rare appearance
            // envelope, but it is interaction state rather than humanoid visual state. Taking
            // the last loot item toggles only populationInteractionFlags. Reapplying the full
            // appearance/equipment/preferences path here can reinitialize the corpse Animator
            // after its authored death pose has already been pinned, exposing the humanoid bind
            // pose/T-pose. Consume that flag change from the already-updated network cache and
            // leave the established corpse presentation untouched.
            //
            // Population visual definitions are immutable for an actor generation on the current
            // standalone contract. Equipment changes still pass through because equipmentVersion
            // changes; identity/name changes also pass through. No new message or corpse state is
            // introduced by this guard.
            if (!initial &&
                IsPopulationPresentation &&
                oldValue.generation == value.generation &&
                oldValue.characterId == value.characterId &&
                oldValue.equipmentVersion == value.equipmentVersion &&
                string.Equals(oldValue.displayName, value.displayName, StringComparison.Ordinal) &&
                string.Equals(oldValue.guildName, value.guildName, StringComparison.Ordinal) &&
                oldValue.populationInteractionFlags != value.populationInteractionFlags)
            {
                if (_visual != null && !string.IsNullOrEmpty(_displayName))
                    _visual.gameObject.name = $"ClientPresentation {_displayName}";
                return;
            }

            // Appearance is rare, authoritative definition state. Reconstruct/update the
            // client humanoid here rather than adding visual data to movement snapshots or
            // handing runtime ownership back to Character Creator.
            ApplyAuthoritativeAppearance(value.appearance);
            ApplyAuthoritativeEquipment(value.equipmentVisuals);
            ApplyPresentationPreferences(value.presentation);
            if (_hiddenPresentation)
                ApplyHiddenPresentationTint();

            if (_visual != null && !string.IsNullOrEmpty(_displayName))
                _visual.gameObject.name = $"ClientPresentation {_displayName}";
        }

        private void ApplyAuthoritativeAppearance(CharacterAppearanceRecipe appearance)
        {
            if (appearance == null)
            {
                if (_visual == null && createDebugVisual)
                    EnsureVisual();
                return;
            }

            Transform previous = _visual;
            long stableAppearanceId = IsPopulationPresentation
                ? PopulationActorId
                : (_network != null ? _network.Appearance.characterId : 0L);

            if (!PlayerEntityVisualFactoryRegistry.TryCreateOrUpdate(
                    previous,
                    appearance.Clone(),
                    stableAppearanceId,
                    IsPopulationPresentation,
                    out Transform resolved) ||
                resolved == null)
            {
                // Keep an already-valid presentation if a later recipe cannot be resolved.
                // The capsule exists only when there is no presentation to keep and debug
                // fallback is enabled.
                if (_visual == null && createDebugVisual)
                    EnsureVisual();
                return;
            }

            if (resolved == previous)
                return;

            Vector3 position;
            Quaternion rotation;

            if (previous != null)
            {
                // Preserve the exact rendered pose so capsule -> humanoid replacement does
                // not introduce a presentation jump or disturb interpolation continuity.
                position = previous.position;
                rotation = previous.rotation;
            }
            else if (_count > 0)
            {
                BufferedSnapshot latest = GetSample(_count - 1);
                position = latest.snapshot.Position;
                rotation = Quaternion.Euler(0f, latest.snapshot.YawDegrees, 0f);
            }
            else
            {
                position = transform.position;
                rotation = transform.rotation;
            }

            resolved.position = position;
            resolved.rotation = rotation;
            _visual = resolved;
            _animatorPresentation = null;
            _visualYaw = rotation.eulerAngles.y;

            if (previous != null)
            {
                // Destroy is deferred until end-of-frame; hide the fallback immediately so
                // there is never a one-frame duplicate body during replacement.
                previous.gameObject.SetActive(false);
                Destroy(previous.gameObject);
            }
        }

        private void ApplyAuthoritativeEquipment(PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            PlayerEquipmentVisualSelection[] effective =
                equipmentVisuals ?? Array.Empty<PlayerEquipmentVisualSelection>();

            ResolveEquipmentCombatStance(effective);

            if (_visual == null)
                return;

            PlayerEntityVisualFactoryRegistry.TryApplyEquipment(_visual, effective);
        }

        private void ResolveEquipmentCombatStance(PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            _stanceGameplaySettingsRevision = PlayerGameplaySettingsRuntime.Revision;
            if (!PlayerEntityVisualFactoryRegistry.TryResolveCombatStance(
                    equipmentVisuals ?? Array.Empty<PlayerEquipmentVisualSelection>(),
                    out _equipmentCombatStance))
            {
                _equipmentCombatStance = HumanoidCombatStance.Unarmed;
            }
        }

        private void ApplyPresentationPreferences(CharacterPresentationPreferences preferences)
        {
            if (_visual == null)
                return;

            CharacterPresentationPreferences effective =
                preferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault();

            GameObject root = _visual.gameObject;
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; ++i)
            {
                if (behaviours[i] is ICharacterPresentationPreferencesPresenter presenter)
                    presenter.ApplyPresentationPreferences(effective.Clone());
            }

            // The canonical runtime humanoid must always have the snapshot-driven adapter.
            // Character Creator preview presenters are not runtime locomotion owners.
            if (root.GetComponentInChildren<Animator>(true) != null)
            {
                HumanoidAnimatorPresentationAdapter adapter =
                    root.GetComponent<HumanoidAnimatorPresentationAdapter>();
                if (adapter == null)
                    adapter = root.AddComponent<HumanoidAnimatorPresentationAdapter>();

                _animatorPresentation = adapter;
                adapter.ApplyPresentationPreferences(effective.Clone());
            }
        }

        private void OnSnapshotChanged(
            bool initial,
            PlayerEntitySnapshot oldValue,
            PlayerEntitySnapshot value)
        {
            if (value.entityId == 0 || value.generation == 0)
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            double tickSeconds = GetTickSeconds();

            if (_generation != value.generation)
            {
                if (_generation != 0)
                    MMORemotePresentationTelemetry.RecordGenerationReset();

                _generation = value.generation;
                ResetBuffer();
                _forcePresentationSnap = true;
            }

            if (_count > 0)
            {
                BufferedSnapshot latest = GetSample(_count - 1);

                if (!PlayerEntityPresentationMath.ShouldAcceptSnapshot(
                        value.serverTick,
                        latest.snapshot.serverTick))
                {
                    return;
                }

                uint tickDelta = NetworkSequence.ForwardDistance(value.serverTick, latest.snapshot.serverTick);
                double serverSpan = Math.Max(tickSeconds, tickDelta * tickSeconds);
                double arrivalSpan = _lastArrivalTime > 0.0
                    ? Math.Max(0.0, now - _lastArrivalTime)
                    : serverSpan;

                _arrivalJitterSeconds =
                    PlayerEntityPresentationMath.UpdateJitterEwma(
                        _arrivalJitterSeconds,
                        arrivalSpan,
                        serverSpan,
                        JitterEwmaFactor);

                Vector3 previousPosition = latest.snapshot.Position;
                if (PlayerEntityPresentationMath.IsTeleport(
                        value,
                        previousPosition,
                        serverSpan,
                        teleportDistance,
                        teleportSpeedThreshold))
                {
                    ResetBuffer();
                    _forcePresentationSnap = true;
                }
            }

            if (_isExtrapolating && _extrapolationSeconds > 0f)
            {
                double penalty = Math.Min(
                    GetMaximumDelaySeconds(tickSeconds) - GetBaseDelaySeconds(tickSeconds),
                    _extrapolationSeconds * Math.Max(0f, extrapolationPenaltyMultiplier));

                if (penalty > _extrapolationPenaltySeconds)
                    _extrapolationPenaltySeconds = penalty;
            }

            bool authoritativeStop = IsAuthoritativelyStopped(value);
            if (authoritativeStop)
            {
                _isExtrapolating = false;
                _extrapolationSeconds = 0f;
                _extrapolationPenaltySeconds = 0.0;
            }

            _lastArrivalTime = now;
            Push(value, now);

            if (_visual == null && createDebugVisual)
                EnsureVisual();

            SetHiddenPresentation((((PlayerEntityFlags)value.flags) & PlayerEntityFlags.Hidden) != 0);

            PresentSnapshotActionPulse(initial, oldValue, value);

            if (_count == 1 || _forcePresentationSnap)
                SnapPresentation(value);
        }

        private void SetHiddenPresentation(bool hidden)
        {
            if (_hiddenPresentation == hidden)
                return;

            _hiddenPresentation = hidden;
            if (_visual == null)
                return;

            if (hidden)
            {
                ApplyHiddenPresentationTint();
                return;
            }

            RestoreHiddenPresentationTint();

            // Re-apply the rare authoritative appearance/equipment state after restoring
            // material defaults. This preserves skin/hair/eye palette property blocks while
            // removing only the temporary hidden-player grey cue.
            if (_network != null)
            {
                PlayerEntityAppearance appearance = _network.Appearance;
                ApplyAuthoritativeAppearance(appearance.appearance);
                ApplyAuthoritativeEquipment(appearance.equipmentVisuals);
            }
        }

        private void ApplyHiddenPresentationTint()
        {
            if (_visual == null)
                return;
            if (_hiddenPresentationBlock == null)
                _hiddenPresentationBlock = new MaterialPropertyBlock();

            Renderer[] renderers = _visual.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                int materialCount = materials != null ? materials.Length : 0;
                if (materialCount <= 0)
                    materialCount = 1;

                for (int materialIndex = 0; materialIndex < materialCount; ++materialIndex)
                {
                    Material material = materials != null && materialIndex < materials.Length
                        ? materials[materialIndex]
                        : null;
                    if (material == null)
                        continue;

                    _hiddenPresentationBlock.Clear();
                    renderer.GetPropertyBlock(_hiddenPresentationBlock, materialIndex);
                    bool changed = false;
                    if (material.HasProperty(BaseColorShaderId))
                    {
                        _hiddenPresentationBlock.SetColor(BaseColorShaderId, HiddenPresentationColor);
                        changed = true;
                    }
                    if (material.HasProperty(ColorShaderId))
                    {
                        _hiddenPresentationBlock.SetColor(ColorShaderId, HiddenPresentationColor);
                        changed = true;
                    }
                    if (changed)
                        renderer.SetPropertyBlock(_hiddenPresentationBlock, materialIndex);
                }
            }
        }

        private void RestoreHiddenPresentationTint()
        {
            if (_visual == null)
                return;
            if (_hiddenPresentationBlock == null)
                _hiddenPresentationBlock = new MaterialPropertyBlock();

            Renderer[] renderers = _visual.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                int materialCount = materials != null ? materials.Length : 0;
                if (materialCount <= 0)
                    materialCount = 1;

                for (int materialIndex = 0; materialIndex < materialCount; ++materialIndex)
                {
                    Material material = materials != null && materialIndex < materials.Length
                        ? materials[materialIndex]
                        : null;
                    if (material == null)
                        continue;

                    _hiddenPresentationBlock.Clear();
                    renderer.GetPropertyBlock(_hiddenPresentationBlock, materialIndex);
                    bool changed = false;
                    if (material.HasProperty(BaseColorShaderId))
                    {
                        _hiddenPresentationBlock.SetColor(BaseColorShaderId, material.GetColor(BaseColorShaderId));
                        changed = true;
                    }
                    if (material.HasProperty(ColorShaderId))
                    {
                        _hiddenPresentationBlock.SetColor(ColorShaderId, material.GetColor(ColorShaderId));
                        changed = true;
                    }
                    if (changed)
                        renderer.SetPropertyBlock(_hiddenPresentationBlock, materialIndex);
                }
            }
        }

        private void PresentSnapshotActionPulse(
            bool initial,
            PlayerEntitySnapshot oldValue,
            PlayerEntitySnapshot value)
        {
            // Owner presentation starts from the already-existing reliable reload response.
            // Remote observers consume the existing snapshot actionState/actionId bytes.
            if (IsOwnerClient ||
                value.actionState != (byte)PlayerEntityActionState.Reloading ||
                value.actionId == 0)
            {
                return;
            }

            bool samePulse =
                !initial &&
                oldValue.generation == value.generation &&
                oldValue.actionState == value.actionState &&
                oldValue.actionId == value.actionId;
            if (!samePulse)
                PresentReloadCombatAction(value.actionId);
        }

        private void Push(PlayerEntitySnapshot snapshot, double arrivalTime)
        {
            int index = (_head + _count) % BufferCapacity;

            if (_count == BufferCapacity)
            {
                _head = (_head + 1) % BufferCapacity;
                index = (_head + _count - 1) % BufferCapacity;
            }
            else
            {
                _count++;
            }

            double unwrappedTick = snapshot.serverTick;
            if (_count > 1)
            {
                BufferedSnapshot previous = GetSample(_count - 2);
                unwrappedTick = PlayerEntityPresentationMath.UnwrapTick(
                    previous.unwrappedTick,
                    snapshot.serverTick,
                    previous.snapshot.serverTick);
            }

            _buffer[index] = new BufferedSnapshot
            {
                snapshot = snapshot,
                unwrappedTick = unwrappedTick,
                arrivalTime = arrivalTime,
            };
        }

        private void SnapPresentation(PlayerEntitySnapshot snapshot)
        {
            if (_visual == null)
                return;

            Vector3 p = snapshot.Position;
            float yaw = ResolvePresentationYaw(snapshot.YawDegrees);

            _visual.position = p;
            _visual.rotation = Quaternion.Euler(0f, yaw, 0f);
            _visualYaw = yaw;

            _smoothDampVelocity = Vector3.zero;
            _smoothDampYawVelocity = 0f;
            _renderedWorldVelocity = Vector3.zero;
            _ownerPredictionPosition = p;
            _ownerPredictionInitialized = IsOwnerClient;
            _ownerPredictionLastTime = Time.realtimeSinceStartupAsDouble;
            _lastPresentationError = 0f;
            _forcePresentationSnap = false;
        }

        internal void PresentationUpdate(float frameDelta, double now)
        {
            if (_visual == null || _count == 0 || Manager == null)
                return;

            double tickSeconds = GetTickSeconds();
            if (tickSeconds <= 0.0001)
                return;

            UpdateAdaptiveDelay(frameDelta, tickSeconds);

            BufferedSnapshot latest = GetSample(_count - 1);

            // The critical smoothness detail: the render timeline advances every render
            // frame, not only when a packet arrives.
            double ageSinceLatestArrival = Math.Max(0.0, now - latest.arrivalTime);
            double estimatedLatestServerTickNow =
                latest.unwrappedTick + ageSinceLatestArrival / tickSeconds;

            double renderTick =
                estimatedLatestServerTickNow - _dynamicDelaySeconds / tickSeconds;

            TrimOldSamples(renderTick);

            Vector3 targetPosition;
            float targetYaw;
            bool usingOwnerPrediction = false;
            bool usingBufferedInterpolation = false;

            if (TryPredictLocalOwner(latest, now, tickSeconds, out targetPosition, out targetYaw))
            {
                usingOwnerPrediction = true;
                _isExtrapolating = false;
                _extrapolationSeconds = 0f;
            }
            else if (TryInterpolate(renderTick, tickSeconds, out targetPosition, out targetYaw))
            {
                usingBufferedInterpolation = true;
                _localPredictionActive = false;
                _isExtrapolating = false;
                _extrapolationSeconds = 0f;
            }
            else
            {
                _localPredictionActive = false;
                Extrapolate(renderTick, tickSeconds, out targetPosition, out targetYaw);
            }

            // Remote entities render authoritative/interpolated yaw. The owner
            // predicts only visual facing so mouse turning is immediate instead
            // of waiting through network + interpolation latency.
            targetYaw = ResolvePresentationYaw(targetYaw);

            Vector3 current = _visual.position;
            float error = Vector3.Distance(current, targetPosition);
            _lastPresentationError = error;

            if (PlayerEntityPresentationMath.ShouldHardSnap(
                    _forcePresentationSnap,
                    error,
                    GetHardSnapDistance()))
            {
                _visual.position = targetPosition;
                _renderedWorldVelocity = Vector3.zero;
                _visualYaw = targetYaw;
                _visual.rotation = Quaternion.Euler(0f, targetYaw, 0f);
                _smoothDampVelocity = Vector3.zero;
                _smoothDampYawVelocity = 0f;
                _forcePresentationSnap = false;
                UpdateLocomotionPresentation(latest.snapshot, frameDelta, tickSeconds);
                return;
            }

            if (usingOwnerPrediction || (!IsOwnerClient && usingBufferedInterpolation))
            {
                // Both owner prediction and remote buffered interpolation already produce a
                // continuous render-frame position. A second positional SmoothDamp makes the
                // visual root lag behind that timeline, then accelerate to catch up (up to the
                // correction-speed ceiling) and coast after an authoritative stop. That is the
                // source of remote stride-distance mismatch and idle-before-stop sliding.
                // Render the reconstructed timeline directly.
                _visual.position = targetPosition;
                _smoothDampVelocity = Vector3.zero;
            }
            else
            {
                // Extrapolation/recovery is the only path that keeps a correction filter. It
                // is exceptional packet-loss handling, not normal remote locomotion.
                _visual.position = Vector3.SmoothDamp(
                    current,
                    targetPosition,
                    ref _smoothDampVelocity,
                    Mathf.Max(0.005f, positionSmoothTime),
                    Mathf.Max(1f, maximumCorrectionSpeed),
                    frameDelta);
            }

            if (frameDelta > 0.0001f)
            {
                _renderedWorldVelocity = (_visual.position - current) / frameDelta;
                float maximumRenderedSpeed = Mathf.Max(
                    PlayerGameplaySettingsRuntime.SprintSpeed * 1.75f,
                    GetMaximumReconstructedSpeed());
                if (_renderedWorldVelocity.sqrMagnitude > maximumRenderedSpeed * maximumRenderedSpeed)
                    _renderedWorldVelocity = _renderedWorldVelocity.normalized * maximumRenderedSpeed;
            }
            else
            {
                _renderedWorldVelocity = Vector3.zero;
            }

            if (IsOwnerClient &&
                _locomotionCameraController != null &&
                _locomotionCameraController.HasFacingState)
            {
                // Do not smooth owner-local facing through the authoritative
                // snapshot timeline. That extra filter was the source of the
                // rubbery RMB/TPS rotation. The server still owns accepted yaw.
                _visualYaw = targetYaw;
                _smoothDampYawVelocity = 0f;
            }
            else
            {
                _visualYaw = Mathf.SmoothDampAngle(
                    _visualYaw,
                    targetYaw,
                    ref _smoothDampYawVelocity,
                    Mathf.Max(0.005f, rotationSmoothTime),
                    GetMaximumAngularSpeed(),
                    frameDelta);
            }

            _visual.rotation = Quaternion.Euler(0f, _visualYaw, 0f);
            UpdateLocomotionPresentation(latest.snapshot, frameDelta, tickSeconds);
        }

        private void UpdateLocomotionPresentation(
            PlayerEntitySnapshot snapshot,
            float frameDelta,
            double tickSeconds)
        {
            if (_visual == null)
                return;

            if (_animatorPresentation == null)
            {
                _animatorPresentation = _visual.GetComponent<HumanoidAnimatorPresentationAdapter>();
                if (_animatorPresentation == null && _visual.GetComponentInChildren<Animator>(true) != null)
                    _animatorPresentation = _visual.gameObject.AddComponent<HumanoidAnimatorPresentationAdapter>();
            }

            if (_animatorPresentation == null)
                return;

            if (_stanceGameplaySettingsRevision != PlayerGameplaySettingsRuntime.Revision &&
                _network != null)
            {
                ResolveEquipmentCombatStance(_network.Appearance.equipmentVisuals);
            }

            Vector3 worldVelocity = _localPredictionActive && IsOwnerClient
                ? _localPredictedVelocity
                : _renderedWorldVelocity;
            worldVelocity.y = 0f;

            Vector3 localPlanarVelocity =
                Quaternion.Inverse(_visual.rotation) * worldVelocity;

            PlayerEntitySnapshot presentationSnapshot = snapshot;
            if ((PlayerEntityMoveState)snapshot.moveState != PlayerEntityMoveState.Dead)
            {
                float renderedPlanarSpeed = worldVelocity.magnitude;

                // Locomotion presentation must follow the root that is actually being
                // rendered, not the newest packet. Remote interpolation intentionally
                // renders slightly behind the latest snapshot; using latest Idle while
                // the root is still traversing the buffered final segment caused the
                // classic "animation stops, body slides" artifact.
                if (!IsOwnerClient)
                {
                    presentationSnapshot.moveSpeed =
                        PlayerEntityQuantization.QuantizeUnsignedSpeed(renderedPlanarSpeed);

                    PlayerEntityFlags remoteFlags =
                        (PlayerEntityFlags)presentationSnapshot.flags;
                    bool remoteGrounded =
                        (remoteFlags & PlayerEntityFlags.Grounded) != 0;
                    if (remoteGrounded)
                    {
                        presentationSnapshot.moveState = renderedPlanarSpeed > 0.05f
                            ? (byte)PlayerEntityMoveState.Moving
                            : (byte)PlayerEntityMoveState.Idle;
                    }
                }
                else if (_localPredictionActive)
                {
                    presentationSnapshot.moveSpeed =
                        PlayerEntityQuantization.QuantizeUnsignedSpeed(_localPredictedSpeed);

                    PlayerEntityFlags presentationFlags =
                        (PlayerEntityFlags)presentationSnapshot.flags;
                    presentationFlags &=
                        ~(PlayerEntityFlags.Sprinting | PlayerEntityFlags.Running);

                    if ((_localPredictedFlags & (byte)PlayerEntityFlags.Sprinting) != 0 &&
                        _localPredictedSpeed > 0.05f)
                    {
                        presentationFlags |=
                            PlayerEntityFlags.Sprinting | PlayerEntityFlags.Running;
                    }

                    presentationSnapshot.flags = (byte)presentationFlags;

                    if ((presentationFlags & PlayerEntityFlags.Grounded) != 0)
                    {
                        presentationSnapshot.moveState = _localPredictedSpeed > 0.05f
                            ? (byte)PlayerEntityMoveState.Moving
                            : (byte)PlayerEntityMoveState.Idle;
                    }
                }
            }

            if (IsPopulationPresentation &&
                !IsDeadPresentation &&
                Time.realtimeSinceStartupAsDouble < _populationCombatReadyPresentationUntil)
            {
                PlayerEntityFlags populationPresentationFlags =
                    (PlayerEntityFlags)presentationSnapshot.flags;
                populationPresentationFlags |= PlayerEntityFlags.CombatReady;
                presentationSnapshot.flags = (byte)populationPresentationFlags;
            }

            if (IsOwnerClient && _locomotionCameraController != null)
            {
                PlayerEntityFlags localPresentationFlags =
                    (PlayerEntityFlags)presentationSnapshot.flags;
                if (_locomotionCameraController.CombatCameraActive)
                    localPresentationFlags |= PlayerEntityFlags.CombatReady;
                else
                    localPresentationFlags &= ~PlayerEntityFlags.CombatReady;
                presentationSnapshot.flags = (byte)localPresentationFlags;
            }

            _animatorPresentation.ApplyLocomotionSnapshot(
                presentationSnapshot,
                localPlanarVelocity,
                _equipmentCombatStance,
                Mathf.Max(0f, frameDelta));
        }

        private void UpdateAdaptiveDelay(float frameDelta, double tickSeconds)
        {
            if (IsOwnerClient && localPrediction)
            {
                _dynamicDelaySeconds = 0.0;
                _desiredDelaySeconds = 0.0;
                _extrapolationPenaltySeconds = 0.0;
                return;
            }

            double baseDelay = GetBaseDelaySeconds(tickSeconds);
            double maxDelay = GetMaximumDelaySeconds(tickSeconds);

            // Decay extrapolation pressure slowly. This hysteresis is intentional:
            // a connection that just starved the interpolator should keep a little
            // extra safety margin instead of immediately shrinking the buffer.
            _extrapolationPenaltySeconds = Math.Max(
                0.0,
                _extrapolationPenaltySeconds - frameDelta * 0.05);

            _dynamicDelaySeconds =
                PlayerEntityPresentationMath.ComputeAdaptiveDelay(
                    _dynamicDelaySeconds,
                    frameDelta,
                    baseDelay,
                    maxDelay,
                    _arrivalJitterSeconds,
                    GetJitterMultiplier(),
                    _extrapolationPenaltySeconds,
                    delayIncreaseResponseSeconds,
                    delayDecreaseResponseSeconds,
                    out _desiredDelaySeconds);
        }

        private bool TryPredictLocalOwner(
            BufferedSnapshot latest,
            double now,
            double tickSeconds,
            out Vector3 position,
            out float yaw)
        {
            position = default;
            yaw = 0f;
            _localPredictionActive = false;
            _localPredictedVelocity = Vector3.zero;
            _localPredictedSpeed = 0f;
            _localPredictedFlags = 0;

            if (!IsOwnerClient ||
                !localPrediction ||
                _locomotionCameraController == null ||
                !_locomotionCameraController.HasFacingState)
            {
                _ownerPredictionInitialized = false;
                return false;
            }

            PlayerEntitySnapshot snapshot = latest.snapshot;
            if ((PlayerEntityMoveState)snapshot.moveState == PlayerEntityMoveState.Dead)
            {
                _ownerPredictionInitialized = false;
                return false;
            }

            if (!_locomotionCameraController.TryReadMovementIntent(
                    out Vector2 worldInput,
                    out float facingYaw,
                    out byte inputFlags))
            {
                return false;
            }

            bool grounded =
                (((PlayerEntityFlags)snapshot.flags) & PlayerEntityFlags.Grounded) != 0;

            float speed = (inputFlags & (byte)PlayerEntityFlags.Sprinting) != 0
                ? PlayerGameplaySettingsRuntime.SprintSpeed
                : PlayerGameplaySettingsRuntime.MoveSpeed;

            float magnitude = Mathf.Clamp01(worldInput.magnitude);
            Vector2 direction = magnitude > 0.0001f
                ? worldInput / magnitude
                : Vector2.zero;

            Vector3 planarVelocity = new Vector3(
                direction.x * speed * magnitude,
                0f,
                direction.y * speed * magnitude);

            double rawFrameSeconds = _ownerPredictionInitialized
                ? Math.Max(0.0, now - _ownerPredictionLastTime)
                : 0.0;
            float frameSeconds = (float)Math.Min(
                0.05,
                rawFrameSeconds > 0.0 ? rawFrameSeconds : Math.Max(0.0, Time.unscaledDeltaTime));

            bool teleport =
                (((PlayerEntityFlags)snapshot.flags) & PlayerEntityFlags.Teleport) != 0;
            if (!_ownerPredictionInitialized || teleport)
            {
                _ownerPredictionPosition = _visual != null
                    ? _visual.position
                    : snapshot.Position;
                if (teleport || Vector3.Distance(_ownerPredictionPosition, snapshot.Position) > 2f)
                    _ownerPredictionPosition = snapshot.Position;
                _ownerPredictionInitialized = true;
            }

            _ownerPredictionLastTime = now;

            float verticalVelocity = 0f;
            if (grounded)
            {
                Vector3 horizontalDelta = planarVelocity * frameSeconds;
                if (horizontalDelta.sqrMagnitude > 0.0000001f)
                {
                    if (SharedWorldClient.TryResolveGroundedMove(
                            _ownerPredictionPosition,
                            horizontalDelta,
                            0.35f,
                            0.40f,
                            0.50f,
                            out Vector3 sharedResolved,
                            out _))
                    {
                        if (frameSeconds > 0.0001f)
                            verticalVelocity =
                                (sharedResolved.y - _ownerPredictionPosition.y) / frameSeconds;
                        _ownerPredictionPosition = sharedResolved;
                    }
                    else
                    {
                        _ownerPredictionPosition += horizontalDelta;
                    }
                }

                // Project the newest authoritative sample to approximately "now" and
                // fold corrections into the predictor while moving. When the local input
                // has just stopped but the newest packet still describes motion, hold the
                // rendered root still instead of dragging the body/camera toward a stale
                // pre-stop snapshot. Large idle disagreement still snaps for safety.
                double age = Math.Max(0.0, now - latest.arrivalTime);
                float projectedSeconds = (float)Math.Min(
                    Math.Max(0.02f, localPredictionMaxSeconds),
                    age);

                Vector3 authoritativeNow = snapshot.Position;
                if (!IsAuthoritativelyStopped(snapshot) && _count > 1)
                {
                    Vector3 authoritativeVelocity = EstimateVelocity(_count - 1, tickSeconds);
                    authoritativeNow += authoritativeVelocity * projectedSeconds;
                }

                Vector3 predictionError = authoritativeNow - _ownerPredictionPosition;
                predictionError.y = 0f;

                if (magnitude > 0.0001f)
                {
                    float correctionResponse = 0.10f;
                    float correctionAlpha = 1f - Mathf.Exp(-frameSeconds / correctionResponse);
                    Vector3 correction = predictionError * correctionAlpha;
                    correction = Vector3.ClampMagnitude(
                        correction,
                        Mathf.Max(0.02f, PlayerGameplaySettingsRuntime.MoveSpeed * frameSeconds * 0.35f));
                    _ownerPredictionPosition += correction;
                }
                else if (IsAuthoritativelyStopped(snapshot) &&
                         predictionError.sqrMagnitude > 1.0f)
                {
                    // A one-metre idle disagreement is not normal prediction lead. Snap
                    // instead of hiding a genuine correction indefinitely.
                    _ownerPredictionPosition = snapshot.Position;
                }

                // Horizontal owner prediction is driven from the shared bake, but the local
                // scene already contains the exact visible collider that the player must stand
                // on. Resolve Y independently every rendered frame using one non-allocating
                // downward probe, constrained to the latest server-confirmed feet height.
                // This fixes owner-only visual sinking on raised meshes/platforms without
                // changing authority or adding any wire traffic. If scene physics cannot
                // resolve the support, fall back to the same precise baked support surface.
                float beforeGroundResolveY = _ownerPredictionPosition.y;
                if (TryResolveOwnerGroundPresentation(
                        snapshot.Position.y,
                        out float ownerGroundY))
                {
                    _ownerPredictionPosition.y = ownerGroundY;
                    if (frameSeconds > 0.0001f)
                        verticalVelocity =
                            (_ownerPredictionPosition.y - beforeGroundResolveY) / frameSeconds;
                }
            }
            else
            {
                // Airborne motion is still reconstructed from the authoritative sample.
                // Jump prediction can be promoted to the same integrated model later once
                // the shared-world dynamic blocker path is complete.
                double age = Math.Max(0.0, now - latest.arrivalTime);
                double lead = Math.Max(0f, localPredictionLeadTicks) * tickSeconds;
                float predictionSeconds = (float)Math.Min(
                    Math.Max(0.02f, localPredictionMaxSeconds),
                    age + lead);

                _ownerPredictionPosition = snapshot.Position +
                    planarVelocity * predictionSeconds;
                verticalVelocity = snapshot.VerticalSpeed -
                    PlayerGameplaySettingsRuntime.Gravity * predictionSeconds;
                _ownerPredictionPosition.y +=
                    snapshot.VerticalSpeed * predictionSeconds -
                    0.5f * PlayerGameplaySettingsRuntime.Gravity *
                    predictionSeconds * predictionSeconds;
            }

            _localPredictedVelocity = new Vector3(
                planarVelocity.x,
                verticalVelocity,
                planarVelocity.z);
            _localPredictedSpeed = planarVelocity.magnitude;
            _localPredictedFlags = inputFlags;
            _localPredictionActive = true;

            position = _ownerPredictionPosition;
            yaw = facingYaw;
            return true;
        }

        private bool TryResolveOwnerGroundPresentation(
            float authoritativeFeetY,
            out float resolvedY)
        {
            resolvedY = authoritativeFeetY;

            if (localGroundPresentationProbe &&
                TryProbeOwnerSceneGround(authoritativeFeetY, out resolvedY))
            {
                return true;
            }

            // SharedWorld is still the prediction source of truth. This zero-movement
            // query matters when the owner is standing still: TryResolveGroundedMove()
            // only runs when there is a horizontal delta.
            return SharedWorldClient.TryFindSupportHeight(
                _ownerPredictionPosition.x,
                _ownerPredictionPosition.z,
                authoritativeFeetY,
                Mathf.Max(0.10f, localGroundProbeUp),
                Mathf.Max(0.25f, localGroundProbeDown),
                out resolvedY);
        }

        private bool TryProbeOwnerSceneGround(
            float authoritativeFeetY,
            out float groundY)
        {
            groundY = authoritativeFeetY;

            float probeUp = Mathf.Max(0.10f, localGroundProbeUp);
            float probeDown = Mathf.Max(0.25f, localGroundProbeDown);
            float referenceTop = Mathf.Max(_ownerPredictionPosition.y, authoritativeFeetY);
            Vector3 origin = new Vector3(
                _ownerPredictionPosition.x,
                referenceTop + probeUp,
                _ownerPredictionPosition.z);

            // Include the vertical disagreement in the cast length so a client that is
            // already visually embedded can recover in one frame.
            float castDistance =
                probeUp +
                probeDown +
                Mathf.Abs(authoritativeFeetY - _ownerPredictionPosition.y);

            int hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                _ownerGroundProbeHits,
                castDistance,
                localGroundPresentationLayers,
                QueryTriggerInteraction.Ignore);

            if (hitCount <= 0)
                return false;

            const float minimumGroundNormalY = 0.6427876f; // cos(50 degrees), matches standard server motor slope.
            float tolerance = Mathf.Max(0.05f, localGroundAuthoritativeTolerance);
            float bestError = float.PositiveInfinity;
            float bestY = authoritativeFeetY;
            bool found = false;

            for (int i = 0; i < hitCount; ++i)
            {
                RaycastHit hit = _ownerGroundProbeHits[i];
                Collider collider = hit.collider;
                if (collider == null || hit.normal.y < minimumGroundNormalY)
                    continue;

                Transform hitTransform = collider.transform;
                if (hitTransform == transform ||
                    hitTransform.IsChildOf(transform) ||
                    (_visual != null &&
                     (hitTransform == _visual || hitTransform.IsChildOf(_visual))))
                {
                    continue;
                }

                float error = Mathf.Abs(hit.point.y - authoritativeFeetY);
                if (error > tolerance)
                    continue;

                // Match the server-confirmed support height first. If two stacked
                // surfaces are equally close, prefer the higher one so a platform top
                // wins over a floor beneath it.
                if (error > bestError + 0.0001f)
                    continue;
                if (Mathf.Abs(error - bestError) <= 0.0001f &&
                    found &&
                    hit.point.y <= bestY)
                {
                    continue;
                }

                bestError = error;
                bestY = hit.point.y;
                found = true;
            }

            if (!found)
                return false;

            groundY = bestY;
            return true;
        }

        private bool TryInterpolate(
            double renderTick,
            double tickSeconds,
            out Vector3 position,
            out float yaw)
        {
            position = default;
            yaw = 0f;

            if (_count == 0)
                return false;

            BufferedSnapshot oldest = GetSample(0);
            if (renderTick <= oldest.unwrappedTick)
            {
                position = oldest.snapshot.Position;
                yaw = oldest.snapshot.YawDegrees;
                return true;
            }

            for (int i = 1; i < _count; ++i)
            {
                BufferedSnapshot to = GetSample(i);
                if (renderTick > to.unwrappedTick)
                    continue;

                BufferedSnapshot from = GetSample(i - 1);
                double tickDelta = to.unwrappedTick - from.unwrappedTick;

                if (tickDelta == 0)
                {
                    position = to.snapshot.Position;
                    yaw = to.snapshot.YawDegrees;
                    return true;
                }

                double normalized =
                    (renderTick - from.unwrappedTick) / tickDelta;

                float t = Mathf.Clamp01((float)normalized);
                float segmentSeconds = (float)(tickDelta * tickSeconds);

                position = InterpolatePosition(i - 1, i, t, segmentSeconds, tickSeconds);
                yaw = InterpolateYaw(i - 1, i, t, segmentSeconds, tickSeconds);
                return true;
            }

            return false;
        }

        private Vector3 InterpolatePosition(
            int fromIndex,
            int toIndex,
            float t,
            float segmentSeconds,
            double tickSeconds)
        {
            PlayerEntitySnapshot fromSnapshot = GetSample(fromIndex).snapshot;
            PlayerEntitySnapshot toSnapshot = GetSample(toIndex).snapshot;
            Vector3 p0 = fromSnapshot.Position;
            Vector3 p1 = toSnapshot.Position;

            Vector3 linear = Vector3.LerpUnclamped(p0, p1, t);

            if (segmentSeconds <= 0.0001f)
                return linear;

            Vector3 v0 = EstimateVelocity(fromIndex, tickSeconds);

            if (IsAuthoritativelyStopped(toSnapshot))
            {
                Vector3 segment = p1 - p0;
                float distance = segment.magnitude;
                if (distance <= 0.0001f)
                    return p1;

                Vector3 direction = segment / distance;
                float incomingSpeed = Mathf.Max(0f, Vector3.Dot(v0, direction));

                float maxIncomingSpeed =
                    distance * Mathf.Max(0.5f, stopTangentDistanceMultiplier) /
                    Mathf.Max(0.0001f, segmentSeconds);

                incomingSpeed = Mathf.Min(incomingSpeed, maxIncomingSpeed);

                float traveled = PlayerEntityPresentationMath.HermiteScalar(
                    0f,
                    distance,
                    incomingSpeed,
                    0f,
                    t,
                    segmentSeconds);

                traveled = Mathf.Clamp(traveled, 0f, distance);
                return p0 + direction * traveled;
            }

            if (hermiteStrength <= 0f)
                return linear;

            Vector3 v1 = EstimateVelocity(toIndex, tickSeconds);

            float continuity = PlayerEntityPresentationMath.DirectionContinuity(v0, v1);
            float strength = Mathf.Clamp01(hermiteStrength) * continuity;

            if (strength <= 0.001f)
                return linear;

            float tangentScale = GetHermiteTangentScale();
            Vector3 hermite = PlayerEntityPresentationMath.Hermite(
                p0,
                p1,
                v0 * tangentScale,
                v1 * tangentScale,
                t,
                segmentSeconds);

            // Cubic interpolation can overshoot on abrupt reversals. Keep the smooth
            // tangent while bounding its deviation from the authoritative segment.
            hermite = PlayerEntityPresentationMath.BoundHermiteDeviation(
                p0,
                p1,
                linear,
                hermite,
                GetMaximumHermiteDeviation());

            return Vector3.LerpUnclamped(linear, hermite, strength);
        }

        private float InterpolateYaw(
            int fromIndex,
            int toIndex,
            float t,
            float segmentSeconds,
            double tickSeconds)
        {
            float y0 = GetSample(fromIndex).snapshot.YawDegrees;
            float y1 = y0 + Mathf.DeltaAngle(y0, GetSample(toIndex).snapshot.YawDegrees);

            if (segmentSeconds <= 0.0001f)
                return Mathf.Repeat(y1, 360f);

            float w0 = EstimateAngularVelocity(fromIndex, tickSeconds);
            float w1 = EstimateAngularVelocity(toIndex, tickSeconds);

            float tangentScale = GetHermiteTangentScale();
            float value = PlayerEntityPresentationMath.HermiteScalar(
                y0,
                y1,
                w0 * tangentScale,
                w1 * tangentScale,
                t,
                segmentSeconds);

            return Mathf.Repeat(value, 360f);
        }

        private void Extrapolate(
            double renderTick,
            double tickSeconds,
            out Vector3 position,
            out float yaw)
        {
            BufferedSnapshot latest = GetSample(_count - 1);

            double ticksPastLatest =
                Math.Max(0.0, renderTick - latest.unwrappedTick);

            float secondsPastLatest = (float)(ticksPastLatest * tickSeconds);

            _isExtrapolating = secondsPastLatest > 0.0001f;
            _extrapolationSeconds = secondsPastLatest;

            bool authoritativeStop = IsAuthoritativelyStopped(latest.snapshot);
            Vector3 velocity = authoritativeStop
                ? Vector3.zero
                : EstimateVelocity(_count - 1, tickSeconds);

            float angularVelocity = EstimateAngularVelocity(_count - 1, tickSeconds);

            float distanceTime = authoritativeStop
                ? 0f
                : IntegratedPredictionTime(secondsPastLatest);

            position = latest.snapshot.Position + velocity * distanceTime;

            float yawTime = IntegratedPredictionTime(secondsPastLatest);
            yaw = Mathf.Repeat(
                latest.snapshot.YawDegrees + angularVelocity * yawTime,
                360f);
        }

        /// <summary>
        /// Full prediction for maxExtrapolationSeconds, then linearly decays velocity
        /// to zero over extrapolationCoastSeconds. Returns the equivalent integrated
        /// full-velocity time, which can be multiplied by velocity/angular velocity.
        /// </summary>
        private float IntegratedPredictionTime(float elapsed)
        {
            return PlayerEntityPresentationMath.IntegratedPredictionTime(
                elapsed,
                GetMaximumExtrapolationSeconds(),
                GetExtrapolationCoastSeconds());
        }

        private Vector3 EstimateVelocity(int index, double tickSeconds)
        {
            if (_count <= 1)
                return Vector3.zero;

            PlayerEntitySnapshot center = GetSample(index).snapshot;
            if (IsAuthoritativelyStopped(center))
                return Vector3.zero;

            int previous = Math.Max(0, index - 1);
            int next = Math.Min(_count - 1, index + 1);

            if (previous == next)
                return Vector3.zero;

            PlayerEntitySnapshot a = GetSample(previous).snapshot;
            PlayerEntitySnapshot b = GetSample(next).snapshot;

            double ticks = GetSample(next).unwrappedTick - GetSample(previous).unwrappedTick;
            double seconds = ticks * tickSeconds;

            if (seconds <= 0.0001)
                return Vector3.zero;

            Vector3 velocity = (b.Position - a.Position) / (float)seconds;

            float maxSpeed = GetMaximumReconstructedSpeed();
            if (velocity.sqrMagnitude > maxSpeed * maxSpeed)
                velocity = velocity.normalized * maxSpeed;

            return velocity;
        }

        private float EstimateAngularVelocity(int index, double tickSeconds)
        {
            if (_count <= 1)
                return 0f;

            int previous = Math.Max(0, index - 1);
            int next = Math.Min(_count - 1, index + 1);

            if (previous == next)
                return 0f;

            PlayerEntitySnapshot a = GetSample(previous).snapshot;
            PlayerEntitySnapshot b = GetSample(next).snapshot;

            double ticks = GetSample(next).unwrappedTick - GetSample(previous).unwrappedTick;
            double seconds = ticks * tickSeconds;

            if (seconds <= 0.0001)
                return 0f;

            float degrees =
                Mathf.DeltaAngle(a.YawDegrees, b.YawDegrees);

            return Mathf.Clamp(
                degrees / (float)seconds,
                -GetMaximumAngularSpeed(),
                GetMaximumAngularSpeed());
        }

        private void TrimOldSamples(double renderTick)
        {
            // Keep enough history for central-difference velocity reconstruction.
            while (_count > 5)
            {
                BufferedSnapshot second = GetSample(1);
                if (second.unwrappedTick >= renderTick)
                    break;

                _head = (_head + 1) % BufferCapacity;
                _count--;
            }
        }

        private BufferedSnapshot GetSample(int relativeIndex)
        {
            return _buffer[(_head + relativeIndex) % BufferCapacity];
        }

        private bool IsAuthoritativelyStopped(PlayerEntitySnapshot snapshot)
        {
            return PlayerEntityPresentationMath.IsAuthoritativelyStopped(
                snapshot,
                stoppedMoveSpeedEpsilon);
        }

        private double GetTickSeconds()
        {
            // Presentation follows negotiated server timing first.
            if (Manager is PlayerEntityGameManager playerManager)
                return 1.0 / Math.Max(1, (int)playerManager.AuthoritativeTickRate);

            if (Manager != null && Manager.LogicUpdater != null)
                return Math.Max(0.001, Manager.LogicUpdater.DeltaTime);

            return 1.0 / PlayerEntityProtocol.DefaultTickRate;
        }

        private double GetBaseDelaySeconds(double tickSeconds)
        {
            uint ticks;

            if (IsOwnerClient)
            {
                if (localPrediction)
                    return 0.0;

                ticks = maximumSmoothnessProfile
                    ? Math.Max(1u, ownerInterpolationTicks)
                    : ownerInterpolationTicks;
            }
            else
            {
                ticks = maximumSmoothnessProfile
                    ? Math.Max(2u, baseInterpolationTicks)
                    : Math.Max(1u, baseInterpolationTicks);
            }

            return ticks * tickSeconds;
        }

        private double GetMaximumDelaySeconds(double tickSeconds)
        {
            uint ticks = maximumSmoothnessProfile
                ? Math.Max(10u, maximumInterpolationTicks)
                : Math.Max(baseInterpolationTicks, maximumInterpolationTicks);

            return Math.Max(GetBaseDelaySeconds(tickSeconds), ticks * tickSeconds);
        }

        private float GetJitterMultiplier()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(3.0f, jitterSafetyMultiplier)
                : Mathf.Max(1f, jitterSafetyMultiplier);
        }

        private float GetHermiteTangentScale()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(0.75f, hermiteTangentScale)
                : Mathf.Max(0.1f, hermiteTangentScale);
        }

        private float GetMaximumHermiteDeviation()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(0.75f, maximumHermiteDeviation)
                : Mathf.Max(0.05f, maximumHermiteDeviation);
        }

        private float GetMaximumReconstructedSpeed()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(20f, maximumReconstructedSpeed)
                : Mathf.Max(1f, maximumReconstructedSpeed);
        }

        private float GetMaximumAngularSpeed()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(1080f, maximumReconstructedAngularSpeed)
                : Mathf.Max(90f, maximumReconstructedAngularSpeed);
        }

        private float GetMaximumExtrapolationSeconds()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(0.25f, maxExtrapolationSeconds)
                : Mathf.Max(0f, maxExtrapolationSeconds);
        }

        private float GetExtrapolationCoastSeconds()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(0.30f, extrapolationCoastSeconds)
                : Mathf.Max(0f, extrapolationCoastSeconds);
        }

        private float GetHardSnapDistance()
        {
            return maximumSmoothnessProfile
                ? Mathf.Max(15f, hardSnapDistance)
                : Mathf.Max(1f, hardSnapDistance);
        }
    }
}
