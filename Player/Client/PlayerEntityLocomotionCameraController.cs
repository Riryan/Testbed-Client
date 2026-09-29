using Player.Networking;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Local PlayerEntity locomotion/camera presentation controller.
    ///
    /// This intentionally mirrors the useful client-side behavior of the old
    /// uMMORPG PlayerCharacterControllerMovement camera contract without restoring
    /// Unity-side movement authority. It owns only local camera state and facing
    /// intent; PlayerEntityInput serializes the existing compact MovementCommand,
    /// and the standalone GameServer remains authoritative.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class PlayerEntityLocomotionCameraController : MonoBehaviour
    {
        private readonly RaycastHit[] _collisionHits = new RaycastHit[16];
        private readonly RaycastHit[] _precisionAimHits = new RaycastHit[32];

        private const float DirectCombatOriginHeight = 1.10f;

        private PlayerEntityClient _client;
        private PlayerEntityGameManager _gameManager;
        private Camera _camera;

        private Vector3 _smoothedPivot;
        private Vector3 _pivotVelocity;
        private float _cameraYaw;
        private float _facingYaw;
        private float _cameraPitch;
        private float _explorationDistance;
        private float _combatBlend;
        private float _nextCameraResolveTime;

        private bool _initialized;
        private bool _freeLookDragging;
        private Vector2 _freeLookDragStart;
        private bool _cursorOwned;
        private bool _combatCameraActive;
        private bool _interactionActionCameraActive;
        private bool _warnedMissingCamera;

        [Header("Action Mode Crosshair")]
        [Tooltip("Client-only center crosshair. Visible only while Action/Combat camera mode owns gameplay input.")]
        public bool showActionModeCrosshair = true;
        [Range(2f, 16f)] public float actionModeCrosshairArmLength = 6f;
        [Range(1f, 6f)] public float actionModeCrosshairThickness = 2f;
        [Range(0f, 12f)] public float actionModeCrosshairGap = 3f;

        // Weapon-ready/combat state only. Interaction Action focus deliberately does
        // not set this flag so combat aim/fire wire behavior remains dormant.
        public bool CombatCameraActive => _combatCameraActive;
        public bool InteractionActionCameraActive => _interactionActionCameraActive;
        public bool TpsCameraPresentationActive => _client != null && _client.combatCameraEnabled;
        public bool HasFacingState => _initialized;
        public float CurrentFacingYaw => Mathf.Repeat(_facingYaw, 360f);
        public float CurrentAimPitchDegrees
        {
            get
            {
                // Positive means aiming upward on the wire. Prefer the final camera forward
                // vector because combat-camera offsets can differ from the stored orbit pitch.
                if (IsUsableGameplayCamera(_camera))
                {
                    float vertical = Mathf.Clamp(_camera.transform.forward.y, -1f, 1f);
                    return Mathf.Asin(vertical) * Mathf.Rad2Deg;
                }
                return Mathf.Clamp(-_cameraPitch,
                    Player.Shared.PlayerCombatInputEncoding.MinimumAimPitchDegrees,
                    Player.Shared.PlayerCombatInputEncoding.MaximumAimPitchDegrees);
            }
        }

        private void Awake()
        {
            _client = GetComponent<PlayerEntityClient>();
            _gameManager = FindFirstObjectByType<PlayerEntityGameManager>();
        }

        private void OnEnable()
        {
            _initialized = false;
            _combatCameraActive = false;
            _interactionActionCameraActive = false;
            _combatBlend = 0f;
            _freeLookDragging = false;
        }

        private void OnDisable()
        {
            ReleaseCursor();
            _camera = null;
            _initialized = false;
            _combatCameraActive = false;
            _interactionActionCameraActive = false;
            _combatBlend = 0f;
            _freeLookDragging = false;
        }

        private void OnGUI()
        {
            if (!showActionModeCrosshair ||
                !_combatCameraActive ||
                _client == null ||
                !_client.IsOwnerClient ||
                LocalClientInputGate.IsGameplayInputBlocked ||
                LocalClientInputGate.IsPointerUiActive ||
                Event.current == null ||
                Event.current.type != EventType.Repaint)
            {
                return;
            }

            if (_gameManager == null)
                _gameManager = FindFirstObjectByType<PlayerEntityGameManager>();

            PlayerCombatOwnerStateMessage ownerState = _gameManager != null
                ? _gameManager.LatestCombatOwnerState
                : default;
            if (ownerState.revision <= 0 || ownerState.Mode != Game.Shared.Abilities.BasicAttackMode.Firearm)
                return;

            float thickness = Mathf.Max(1f, actionModeCrosshairThickness);
            float arm = Mathf.Max(thickness, actionModeCrosshairArmLength);
            float gap = Mathf.Max(0f, actionModeCrosshairGap);
            float centerX = Screen.width * 0.5f;
            float centerY = Screen.height * 0.5f;

            Color previous = GUI.color;
            GUI.color = Color.white;
            Texture2D pixel = Texture2D.whiteTexture;

            GUI.DrawTexture(new Rect(centerX - gap - arm, centerY - thickness * 0.5f, arm, thickness), pixel);
            GUI.DrawTexture(new Rect(centerX + gap, centerY - thickness * 0.5f, arm, thickness), pixel);
            GUI.DrawTexture(new Rect(centerX - thickness * 0.5f, centerY - gap - arm, thickness, arm), pixel);
            GUI.DrawTexture(new Rect(centerX - thickness * 0.5f, centerY + gap, thickness, arm), pixel);

            GUI.color = previous;
        }

        private void LateUpdate()
        {
            if (_client == null || !_client.IsOwnerClient || !_client.explorationCameraEnabled)
            {
                ReleaseCursor();
                return;
            }

            Transform target = _client.PresentationTransform;
            if (target == null || !target.gameObject.activeInHierarchy)
                return;

            if (!TryResolveGameplayCamera())
                return;

            if (!_initialized)
                Initialize(target);

            bool gameplayInputBlocked = LocalClientInputGate.IsGameplayInputBlocked;
            bool pointerUiActive = LocalClientInputGate.IsPointerUiActive;

            // Ordinary pointer UI pauses mouse-look/zoom and unlocks the cursor, but it does
            // not own gameplay input. Tab/action state, movement serialization, and combat
            // remain on their normal player-input paths.
            UpdateCameraMode(gameplayInputBlocked);
            bool cameraInputBlocked = gameplayInputBlocked || pointerUiActive;

            // The normal gameplay camera uses the established TPS/shoulder presentation.
            // Combat and Interaction focus only change authoritative/action semantics; they no
            // longer switch the player into a completely different camera framing.
            bool tpsPresentationActive = TpsCameraPresentationActive;
            bool drivesFacing = tpsPresentationActive
                ? UpdateTpsLook(cameraInputBlocked, _client.combatCameraCharacterFollowsYaw)
                : UpdateExplorationLook(target, cameraInputBlocked);

            UpdateZoom(cameraInputBlocked);
            if (drivesFacing)
            {
                _facingYaw = _cameraYaw;

                // Local facing is presentation-predicted immediately, matching the
                // old uMMORPG local-control feel. This only rotates the rendered
                // humanoid; the standalone GameServer still accepts/rejects the
                // yaw carried by MovementCommand as authority.
                _client.ApplyOwnerLocalFacing(_facingYaw);
            }

            UpdateCameraPose(target);
        }

        public void ForceCameraRebind()
        {
            _camera = null;
            _initialized = false;
            _nextCameraResolveTime = 0f;
        }

        /// <summary>
        /// Resolves precision-ranged aim by converging the authoritative player-origin shot
        /// onto the world point under the center crosshair. This avoids copying the camera
        /// forward vector sideways onto the player, which creates a visible parallax miss in
        /// third person. No target identity is trusted or sent; the standalone server still
        /// performs authoritative range/contact/LOS resolution from the player origin.
        /// </summary>
        public bool TryGetPrecisionAim(out float yawDegrees, out float pitchDegrees)
        {
            yawDegrees = CurrentFacingYaw;
            pitchDegrees = CurrentAimPitchDegrees;

            if (_client == null || !_client.IsOwnerClient || !TryResolveGameplayCamera())
                return false;

            float rayDistance = Mathf.Max(10f, Game.Shared.Abilities.CombatRangePolicy.ClientTargetRaycastDistance);
            Ray cameraRay = _camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 aimPoint = cameraRay.GetPoint(rayDistance);

            int hitCount = Physics.RaycastNonAlloc(
                cameraRay,
                _precisionAimHits,
                rayDistance,
                ~0,
                QueryTriggerInteraction.Collide);

            Transform presentationRoot = _client.PresentationTransform;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < hitCount; ++i)
            {
                Collider collider = _precisionAimHits[i].collider;
                if (collider == null || IsLocalPlayerCollider(collider.transform, presentationRoot))
                    continue;

                float distance = _precisionAimHits[i].distance;
                if (distance <= 0.001f || distance >= nearest)
                    continue;

                nearest = distance;
                aimPoint = _precisionAimHits[i].point;
            }

            Vector3 origin = _client.PresentationPosition + Vector3.up * DirectCombatOriginHeight;
            Vector3 delta = aimPoint - origin;
            if (delta.sqrMagnitude <= 0.000001f)
                return false;

            Vector3 direction = delta.normalized;
            yawDegrees = Mathf.Repeat(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 360f);
            pitchDegrees = Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            pitchDegrees = Mathf.Clamp(
                pitchDegrees,
                Player.Shared.PlayerCombatInputEncoding.MinimumAimPitchDegrees,
                Player.Shared.PlayerCombatInputEncoding.MaximumAimPitchDegrees);
            return true;
        }

        public void SetCombatCameraActive(bool active)
        {
            if (!_client.combatCameraEnabled)
                active = false;

            if (_combatCameraActive == active)
                return;

            bool wasTpsPresentationActive = TpsCameraPresentationActive;
            _combatCameraActive = active;
            _freeLookDragging = false;

            // Entering weapon-ready from normal exploration preserves the established
            // combat behavior of beginning behind current character facing. If the player
            // is already in Interaction Action TPS focus, preserve that camera direction
            // instead of snapping it when Tab draws the weapon.
            if (active && !wasTpsPresentationActive)
                _cameraYaw = _facingYaw;

            if (!TpsCameraPresentationActive)
                ReleaseCursor();
        }

        /// <summary>
        /// Enables the same local TPS camera/look presentation used by weapon-ready combat
        /// without enabling combat authority state. This is intentionally presentation-only:
        /// PlayerEntityInput still keys AimHeld/FireHeld and precision combat yaw exclusively
        /// from CombatCameraActive.
        /// </summary>
        public void SetInteractionActionCameraActive(bool active)
        {
            if (!_client.combatCameraEnabled)
                active = false;

            if (_interactionActionCameraActive == active)
                return;

            bool wasTpsPresentationActive = TpsCameraPresentationActive;
            _interactionActionCameraActive = active;
            _freeLookDragging = false;

            if (active && !wasTpsPresentationActive)
                _cameraYaw = _facingYaw;

            if (!TpsCameraPresentationActive)
                ReleaseCursor();
        }

        private bool TryResolveGameplayCamera()
        {
            if (IsUsableGameplayCamera(_camera))
                return true;

            if (Time.unscaledTime < _nextCameraResolveTime)
                return false;

            _nextCameraResolveTime = Time.unscaledTime + 0.5f;
            _camera = ResolveGameplayCamera();
            if (_camera != null)
            {
                _warnedMissingCamera = false;
                return true;
            }

            if (!_warnedMissingCamera)
            {
                Debug.LogWarning(
                    "[PlayerEntityCamera] No usable gameplay camera was found. " +
                    "Expected one active camera with no RenderTexture target, preferably tagged MainCamera.");
                _warnedMissingCamera = true;
            }

            return false;
        }

        private static Camera ResolveGameplayCamera()
        {
            Camera main = Camera.main;
            if (IsUsableGameplayCamera(main))
                return main;

            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            Camera fallback = null;
            for (int i = 0; i < cameras.Length; ++i)
            {
                Camera candidate = cameras[i];
                if (!IsUsableGameplayCamera(candidate))
                    continue;

                if (candidate.CompareTag("MainCamera"))
                    return candidate;

                if (fallback == null)
                    fallback = candidate;
            }

            return fallback;
        }

        private static bool IsUsableGameplayCamera(Camera candidate)
        {
            if (candidate == null ||
                !candidate.isActiveAndEnabled ||
                candidate.targetTexture != null)
            {
                return false;
            }

            string lowerName = candidate.name.ToLowerInvariant();
            return !lowerName.Contains("preview") &&
                   !lowerName.Contains("portrait") &&
                   !lowerName.Contains("thumbnail") &&
                   !lowerName.Contains("characterselect") &&
                   !lowerName.Contains("character select") &&
                   !lowerName.Contains("creator") &&
                   !lowerName.Contains("minimap") &&
                   !lowerName.Contains("mini map") &&
                   !lowerName.Contains("ui camera") &&
                   !lowerName.Contains("uicamera");
        }

        private void Initialize(Transform target)
        {
            _facingYaw = target.eulerAngles.y;
            _cameraYaw = _facingYaw;

            float minPitch = Mathf.Min(
                _client.explorationCameraMinimumPitch,
                _client.explorationCameraMaximumPitch);
            float maxPitch = Mathf.Max(
                _client.explorationCameraMinimumPitch,
                _client.explorationCameraMaximumPitch);
            _cameraPitch = Mathf.Clamp(
                _client.explorationCameraInitialPitch,
                minPitch,
                maxPitch);

            _explorationDistance = ClampExplorationDistance(_client.explorationCameraDistance);
            _client.explorationCameraDistance = _explorationDistance;
            _smoothedPivot = GetPivot(target);
            _pivotVelocity = Vector3.zero;
            _combatBlend = 0f;
            _initialized = true;
        }

        private void UpdateCameraMode(bool inputBlocked)
        {
            if (!_client.combatCameraEnabled)
            {
                SetCombatCameraActive(false);
                return;
            }

            if (!inputBlocked && PlayerControlConfig.GetKeyDown(PlayerControlAction.ToggleCombatStance))
                SetCombatCameraActive(!_combatCameraActive);
        }

        private bool UpdateExplorationLook(Transform target, bool inputBlocked)
        {
            if (inputBlocked)
            {
                _freeLookDragging = false;

                // Mouse-driven gameplay windows explicitly request an unlocked cursor.
                // Other input captures (notably Interaction focus/menu) do not steal cursor
                // ownership from the current gameplay camera state.
                if (LocalClientInputGate.IsPointerUiActive)
                    ReleaseCursor();
                else
                    AcquireCursor(true);
                return false;
            }

            float sensitivityMultiplier = PlayerGameplayUiConfig.MouseSensitivity;
            float verticalDirection = PlayerGameplayUiConfig.InvertY ? -1f : 1f;
            float mouseX = UnityEngine.Input.GetAxis("Mouse X") *
                           _client.explorationCameraHorizontalSensitivity *
                           sensitivityMultiplier;
            float mouseY = UnityEngine.Input.GetAxis("Mouse Y") *
                           _client.explorationCameraVerticalSensitivity *
                           sensitivityMultiplier *
                           verticalDirection;

            int freeLookButton = Mathf.Clamp(_client.explorationCameraFreeLookMouseButton, 0, 2);
            int rotateButton = Mathf.Clamp(_client.explorationCameraOrbitMouseButton, 0, 2);
            bool freeLookHeld = _client.explorationCameraFreeLookEnabled &&
                                UnityEngine.Input.GetMouseButton(freeLookButton);
            bool rotateHeld = UnityEngine.Input.GetMouseButton(rotateButton);
            bool bothPrimaryButtons = UnityEngine.Input.GetMouseButton(0) &&
                                      UnityEngine.Input.GetMouseButton(1);

            // Classic uMMORPG behavior: LMB by itself is free-look, while RMB
            // (including both primary buttons) rotates character-facing intent.
            if (freeLookHeld && !rotateHeld && !bothPrimaryButtons)
            {
                if (UnityEngine.Input.GetMouseButtonDown(freeLookButton))
                {
                    _freeLookDragStart = UnityEngine.Input.mousePosition;
                    _freeLookDragging = false;
                }

                if (!_freeLookDragging)
                {
                    float threshold = Mathf.Max(0f, _client.explorationCameraFreeLookDragThreshold);
                    Vector2 delta = (Vector2)UnityEngine.Input.mousePosition - _freeLookDragStart;
                    if (delta.sqrMagnitude >= threshold * threshold)
                        _freeLookDragging = true;
                }

                if (_freeLookDragging)
                {
                    ApplyLookDelta(mouseX, mouseY);
                    AcquireCursor(_client.explorationCameraLockCursorWhileOrbiting);
                    return false;
                }
            }
            else
            {
                _freeLookDragging = false;
            }

            if (rotateHeld)
            {
                ApplyLookDelta(mouseX, mouseY);
                AcquireCursor(_client.explorationCameraLockCursorWhileOrbiting);
                return true;
            }

            ReleaseCursor();

            // As in the old controller, starting movement exits idle free-look and
            // returns the camera behind current character-facing intent.
            if (_client.explorationCameraRecenterBehindWhenMoving && HasMovementInput())
            {
                _cameraYaw = Mathf.MoveTowardsAngle(
                    _cameraYaw,
                    _facingYaw,
                    Mathf.Max(1f, _client.explorationCameraRecenterDegreesPerSecond) *
                    Time.unscaledDeltaTime);
            }

            return false;
        }

        private bool UpdateTpsLook(bool inputBlocked, bool drivesCharacterFacing)
        {
            if (!TpsCameraPresentationActive)
            {
                ReleaseCursor();
                return false;
            }

            if (inputBlocked)
            {
                if (LocalClientInputGate.IsPointerUiActive)
                    ReleaseCursor();
                else
                    AcquireCursor(_client.combatCameraLockCursor);
                return false;
            }

            float sensitivityMultiplier = PlayerGameplayUiConfig.MouseSensitivity;
            float verticalDirection = PlayerGameplayUiConfig.InvertY ? -1f : 1f;
            ApplyLookDelta(
                UnityEngine.Input.GetAxis("Mouse X") * _client.explorationCameraHorizontalSensitivity * sensitivityMultiplier,
                UnityEngine.Input.GetAxis("Mouse Y") * _client.explorationCameraVerticalSensitivity * sensitivityMultiplier * verticalDirection);
            AcquireCursor(_client.combatCameraLockCursor);

            // Action mode deliberately reuses the exact local TPS facing/control behavior
            // of weapon-ready mode. Bandwidth suppression belongs in PlayerEntityInput:
            // holstered Action mode may update local facing every frame without waking the
            // movement stream for camera-only idle yaw changes.
            return drivesCharacterFacing;
        }

        private void ApplyLookDelta(float yawDelta, float pitchDelta)
        {
            _cameraYaw = Mathf.Repeat(_cameraYaw + yawDelta, 360f);
            _cameraPitch -= pitchDelta;

            float minPitch = Mathf.Min(
                _client.explorationCameraMinimumPitch,
                _client.explorationCameraMaximumPitch);
            float maxPitch = Mathf.Max(
                _client.explorationCameraMinimumPitch,
                _client.explorationCameraMaximumPitch);
            _cameraPitch = Mathf.Clamp(_cameraPitch, minPitch, maxPitch);
        }

        private void UpdateZoom(bool inputBlocked)
        {
            // Action-focus TPS and weapon-ready TPS share the same camera presentation.
            // Preserve the established TPS behavior of holding its authored distance.
            if (inputBlocked || TpsCameraPresentationActive)
                return;

            float scroll = UnityEngine.Input.mouseScrollDelta.y;
            if (Mathf.Approximately(scroll, 0f))
                return;

            _explorationDistance = ClampExplorationDistance(
                _explorationDistance - scroll * _client.explorationCameraZoomSpeed);
            _client.explorationCameraDistance = _explorationDistance;
        }

        private void UpdateCameraPose(Transform target)
        {
            // The PlayerEntity presentation position is already interpolated.
            // Smoothing the camera pivot a second time creates visible follow lag
            // whenever the player changes direction. uMMORPG parented its camera
            // directly to the locally controlled player for this same reason.
            // Follow the rendered position exactly and keep smoothing only in the
            // authoritative PlayerEntity presentation layer.
            _smoothedPivot = GetPivot(target);
            _pivotVelocity = Vector3.zero;

            _explorationDistance = ClampExplorationDistance(_client.explorationCameraDistance);
            Quaternion orbitRotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);

            Vector2 framing = _client.explorationCameraFramingOffset;
            Vector3 explorationPosition = _smoothedPivot + orbitRotation * new Vector3(
                framing.x,
                framing.y,
                -_explorationDistance);

            Quaternion yawRotation = Quaternion.Euler(0f, _cameraYaw, 0f);
            Vector3 tpsPivot = _smoothedPivot + yawRotation * _client.combatCameraPivotOffset;
            float tpsDistance = Mathf.Max(0.5f, _client.combatCameraDistance);
            Vector3 tpsPosition = tpsPivot + orbitRotation * new Vector3(0f, 0f, -tpsDistance);

            // Normal gameplay, Interaction Action focus, and weapon-ready combat all share
            // the same local TPS framing. Combat authority still keys exclusively from
            // CombatCameraActive; this is presentation only.
            float desiredBlend = TpsCameraPresentationActive ? 1f : 0f;
            float blendTime = Mathf.Max(0.01f, _client.combatCameraBlendTime);
            _combatBlend = Mathf.MoveTowards(
                _combatBlend,
                desiredBlend,
                Time.unscaledDeltaTime / blendTime);
            float blend = _combatBlend * _combatBlend * (3f - 2f * _combatBlend);

            Vector3 lookTarget = Vector3.Lerp(_smoothedPivot, tpsPivot, blend);
            Vector3 desiredPosition = Vector3.Lerp(explorationPosition, tpsPosition, blend);
            Vector3 finalPosition = ResolveObstruction(lookTarget, desiredPosition, target);

            Vector3 lookDirection = lookTarget - finalPosition;
            Quaternion rotation = lookDirection.sqrMagnitude > 0.000001f
                ? Quaternion.LookRotation(lookDirection.normalized, Vector3.up)
                : orbitRotation;

            _camera.transform.SetPositionAndRotation(finalPosition, rotation);
        }

        private Vector3 GetPivot(Transform target)
        {
            return target.TransformPoint(_client.explorationCameraPivotOffset);
        }

        private float ClampExplorationDistance(float value)
        {
            float minimum = Mathf.Max(
                0.25f,
                Mathf.Min(
                    _client.explorationCameraMinimumDistance,
                    _client.explorationCameraMaximumDistance));
            float maximum = Mathf.Max(
                minimum,
                Mathf.Max(
                    _client.explorationCameraMinimumDistance,
                    _client.explorationCameraMaximumDistance));
            return Mathf.Clamp(value, minimum, maximum);
        }

        private Vector3 ResolveObstruction(
            Vector3 pivot,
            Vector3 desiredPosition,
            Transform presentationRoot)
        {
            if (!_client.explorationCameraObstructionEnabled ||
                _client.explorationCameraViewBlockingLayers.value == 0)
            {
                return desiredPosition;
            }

            Vector3 delta = desiredPosition - pivot;
            float distance = delta.magnitude;
            if (distance <= 0.001f)
                return desiredPosition;

            Vector3 direction = delta / distance;
            float radius = Mathf.Max(0f, _client.explorationCameraCollisionRadius);
            int hitCount = radius > 0.001f
                ? Physics.SphereCastNonAlloc(
                    pivot,
                    radius,
                    direction,
                    _collisionHits,
                    distance,
                    _client.explorationCameraViewBlockingLayers,
                    QueryTriggerInteraction.Ignore)
                : Physics.RaycastNonAlloc(
                    pivot,
                    direction,
                    _collisionHits,
                    distance,
                    _client.explorationCameraViewBlockingLayers,
                    QueryTriggerInteraction.Ignore);

            float nearest = distance;
            for (int i = 0; i < hitCount; ++i)
            {
                Collider collider = _collisionHits[i].collider;
                if (collider == null || IsLocalPlayerCollider(collider.transform, presentationRoot))
                    continue;

                float hitDistance = _collisionHits[i].distance;
                if (hitDistance > 0.001f && hitDistance < nearest)
                    nearest = hitDistance;
            }

            if (nearest >= distance)
                return desiredPosition;

            float minimumCollisionDistance = Mathf.Max(
                0.1f,
                _client.explorationCameraMinimumCollisionDistance);
            float safeDistance = Mathf.Max(
                minimumCollisionDistance,
                nearest - Mathf.Max(0f, _client.explorationCameraCollisionPadding));
            safeDistance = Mathf.Min(safeDistance, distance);
            return pivot + direction * safeDistance;
        }

        private bool IsLocalPlayerCollider(Transform hit, Transform presentationRoot)
        {
            if (hit == null)
                return false;

            if (presentationRoot != null &&
                (hit == presentationRoot || hit.IsChildOf(presentationRoot)))
            {
                return true;
            }

            Transform networkRoot = transform;
            return hit == networkRoot || hit.IsChildOf(networkRoot);
        }

        /// <summary>
        /// Reads the canonical local MMO locomotion intent. Camera and locomotion share
        /// the same facing state, matching the useful old uMMORPG control contract.
        /// PlayerEntityInput remains only the network serializer.
        /// </summary>
        public bool TryReadMovementIntent(out Vector2 worldInput, out float facingYaw, out byte flags)
        {
            worldInput = Vector2.zero;
            facingYaw = CurrentFacingYaw;
            flags = 0;

            if (_client == null || !_client.IsOwnerClient)
                return false;

            bool blocked = LocalClientInputGate.IsGameplayInputBlocked;
            Vector2 localInput = blocked ? Vector2.zero : ReadDigitalMovement();

            if (!blocked &&
                _client.runForwardWithBothMouseButtons &&
                !TpsCameraPresentationActive &&
                UnityEngine.Input.GetMouseButton(0) &&
                UnityEngine.Input.GetMouseButton(1))
            {
                localInput = Vector2.up;
            }

            // Normal gameplay, Action focus, and weapon-ready mode share the same TPS
            // movement/facing basis. PlayerEntityInput suppresses camera-only idle yaw while
            // holstered, so this presentation does not create an idle movement stream.
            worldInput = _client.characterRelativeMovement
                ? CharacterLocalToWorld(localInput, facingYaw)
                : localInput;

            if (!blocked && PlayerControlConfig.GetKey(PlayerControlAction.Sprint))
                flags |= (byte)Player.Shared.PlayerEntityFlags.Sprinting;
            if (!blocked && PlayerControlConfig.GetKey(PlayerControlAction.Crouch))
                flags |= (byte)Player.Shared.PlayerEntityFlags.Crouching;
            if (!blocked && PlayerControlConfig.GetKey(PlayerControlAction.Jump))
                flags |= (byte)Player.Shared.PlayerEntityFlags.Jumping;

            return true;
        }

        private static Vector2 ReadDigitalMovement()
        {
            float x = 0f;
            float z = 0f;

            if (PlayerControlConfig.GetKey(PlayerControlAction.MoveLeft)) x -= 1f;
            if (PlayerControlConfig.GetKey(PlayerControlAction.MoveRight)) x += 1f;
            if (PlayerControlConfig.GetKey(PlayerControlAction.MoveBackward)) z -= 1f;
            if (PlayerControlConfig.GetKey(PlayerControlAction.MoveForward)) z += 1f;

            return Vector2.ClampMagnitude(new Vector2(x, z), 1f);
        }

        private static Vector2 CharacterLocalToWorld(Vector2 localInput, float yawDegrees)
        {
            if (localInput.sqrMagnitude <= 0.000001f)
                return Vector2.zero;

            float radians = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians);
            float cos = Mathf.Cos(radians);
            Vector2 right = new Vector2(cos, -sin);
            Vector2 forward = new Vector2(sin, cos);
            return Vector2.ClampMagnitude(right * localInput.x + forward * localInput.y, 1f);
        }

        private bool HasMovementInput()
        {
            if (PlayerControlConfig.GetKey(PlayerControlAction.MoveForward) ||
                PlayerControlConfig.GetKey(PlayerControlAction.MoveBackward) ||
                PlayerControlConfig.GetKey(PlayerControlAction.MoveLeft) ||
                PlayerControlConfig.GetKey(PlayerControlAction.MoveRight))
            {
                return true;
            }

            return _client.runForwardWithBothMouseButtons &&
                   UnityEngine.Input.GetMouseButton(0) &&
                   UnityEngine.Input.GetMouseButton(1);
        }

        private void AcquireCursor(bool shouldLock)
        {
            if (!shouldLock)
            {
                ReleaseCursor();
                return;
            }

            if (_cursorOwned)
                return;

            _cursorOwned = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void ReleaseCursor()
        {
            if (!_cursorOwned)
                return;

            _cursorOwned = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
