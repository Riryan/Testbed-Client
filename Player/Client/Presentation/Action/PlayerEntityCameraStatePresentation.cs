using Player.Client;
using Player.Networking;
using Player.Shared;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Additive client-only camera-state layer placed after the canonical
    /// PlayerEntityLocomotionCameraController. It never moves the authoritative actor.
    ///
    /// Normal/Fire/Aim/Scope are requestable presentation modes. Authoritative Dead and
    /// Mounted move states override the requested mode automatically when observed.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10100)]
    public sealed class PlayerEntityCameraStatePresentation : MonoBehaviour
    {
        [Header("Profiles")]
        public PlayerCameraPresentationProfile normal = PlayerCameraPresentationProfile.NormalDefault();
        public PlayerCameraPresentationProfile fire = PlayerCameraPresentationProfile.FireDefault();
        public PlayerCameraPresentationProfile aim = PlayerCameraPresentationProfile.AimDefault();
        public PlayerCameraPresentationProfile scope = PlayerCameraPresentationProfile.ScopeDefault();
        public PlayerCameraPresentationProfile vehicle = PlayerCameraPresentationProfile.VehicleDefault();
        public PlayerCameraPresentationProfile dead = PlayerCameraPresentationProfile.DeadDefault();

        [Header("Automatic State Overrides")]
        [Tooltip("Authoritative Dead snapshots force the Dead camera state.")]
        public bool followAuthoritativeDeath = true;

        [Tooltip("Authoritative Mounted snapshots force the Vehicle camera state.")]
        public bool followAuthoritativeMounted = true;

        [Header("Fire Pulse")]
        [Tooltip("Default duration for a transient Fire camera pulse before returning to the requested state.")]
        [Min(0.01f)]
        public float defaultFirePulseSeconds = 0.12f;

        private PlayerEntityClient _client;
        private PlayerEntityNetwork _network;
        private PlayerEntityLocomotionCameraController _locomotionCamera;
        private Camera _camera;

        private PlayerCameraPresentationState _requestedState = PlayerCameraPresentationState.Normal;
        private PlayerCameraPresentationState _resolvedState = PlayerCameraPresentationState.Normal;
        private PlayerCameraPresentationState _stateBeforeFire = PlayerCameraPresentationState.Normal;
        private float _fireUntil;
        private float _baselineFieldOfView;
        private bool _baselineCaptured;
        private Vector3 _currentLocalOffset;
        private float _currentFieldOfView;
        private bool _ownsCombatFacing;
        private bool _combatFacingBeforeOverride;

        public PlayerCameraPresentationState RequestedState => _requestedState;
        public PlayerCameraPresentationState ResolvedState => _resolvedState;

        private void Awake()
        {
            _client = GetComponent<PlayerEntityClient>();
            _network = GetComponent<PlayerEntityNetwork>();
            _locomotionCamera = GetComponent<PlayerEntityLocomotionCameraController>();
        }

        private void OnEnable()
        {
            _requestedState = PlayerCameraPresentationState.Normal;
            _resolvedState = PlayerCameraPresentationState.Normal;
            _stateBeforeFire = PlayerCameraPresentationState.Normal;
            _fireUntil = 0f;
            _baselineCaptured = false;
            _camera = null;
            _currentLocalOffset = Vector3.zero;
            _ownsCombatFacing = false;
            _combatFacingBeforeOverride = false;
        }

        private void OnDisable()
        {
            if (_baselineCaptured && _camera != null)
                _camera.fieldOfView = _baselineFieldOfView;

            if (_locomotionCamera != null && _ownsCombatFacing)
                _locomotionCamera.SetCombatCameraActive(_combatFacingBeforeOverride);

            _ownsCombatFacing = false;

            _camera = null;
            _baselineCaptured = false;
        }

        private void Update()
        {
            if (_client == null || !_client.IsOwnerClient)
                return;

            if (_locomotionCamera == null)
                _locomotionCamera = GetComponent<PlayerEntityLocomotionCameraController>();
            if (_network == null)
                _network = GetComponent<PlayerEntityNetwork>();

            PlayerCameraPresentationState next = ResolveState();
            if (_resolvedState != next)
                _resolvedState = next;

            PlayerCameraPresentationProfile profile = GetProfile(_resolvedState);
            ApplyCombatFacingOverride(profile != null && profile.useCombatFacing);
        }

        private void LateUpdate()
        {
            if (_client == null || !_client.IsOwnerClient)
                return;

            if (!TryResolveGameplayCamera())
                return;

            PlayerCameraPresentationProfile profile = GetProfile(_resolvedState) ?? normal;
            if (profile == null)
                return;

            if (!_baselineCaptured)
            {
                _baselineFieldOfView = _camera.fieldOfView;
                _currentFieldOfView = _baselineFieldOfView;
                _baselineCaptured = true;
            }

            float blendSeconds = Mathf.Max(0.01f, profile.blendSeconds);
            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / blendSeconds);

            _currentLocalOffset = Vector3.Lerp(
                _currentLocalOffset,
                profile.cameraLocalOffset,
                t);

            float targetFov = profile.fieldOfView > 0f
                ? profile.fieldOfView
                : _baselineFieldOfView;
            _currentFieldOfView = Mathf.Lerp(_currentFieldOfView, targetFov, t);

            Transform cameraTransform = _camera.transform;
            cameraTransform.position +=
                cameraTransform.right * _currentLocalOffset.x +
                cameraTransform.up * _currentLocalOffset.y +
                cameraTransform.forward * _currentLocalOffset.z;

            _camera.fieldOfView = _currentFieldOfView;
        }

        public void SetState(PlayerCameraPresentationState state)
        {
            if (state == PlayerCameraPresentationState.Dead ||
                state == PlayerCameraPresentationState.Vehicle)
            {
                // Those states are normally driven by authoritative movement state.
                // The API still accepts them so preview/test tooling can exercise profiles.
                _requestedState = state;
                return;
            }

            _requestedState = state;
        }

        [ContextMenu("Preview Camera/Normal")]
        private void PreviewNormal() => SetState(PlayerCameraPresentationState.Normal);

        [ContextMenu("Preview Camera/Fire")]
        private void PreviewFire() => PulseFire(2f);

        [ContextMenu("Preview Camera/Aim")]
        private void PreviewAim() => SetState(PlayerCameraPresentationState.Aim);

        [ContextMenu("Preview Camera/Scope")]
        private void PreviewScope() => SetState(PlayerCameraPresentationState.Scope);

        [ContextMenu("Preview Camera/Vehicle")]
        private void PreviewVehicle() => SetState(PlayerCameraPresentationState.Vehicle);

        [ContextMenu("Preview Camera/Dead")]
        private void PreviewDead() => SetState(PlayerCameraPresentationState.Dead);

        public void SetNormal() => SetState(PlayerCameraPresentationState.Normal);
        public void SetAimActive(bool active) => SetState(active ? PlayerCameraPresentationState.Aim : PlayerCameraPresentationState.Normal);
        public void SetScopeActive(bool active) => SetState(active ? PlayerCameraPresentationState.Scope : PlayerCameraPresentationState.Aim);
        public void SetVehiclePreview(bool active) => SetState(active ? PlayerCameraPresentationState.Vehicle : PlayerCameraPresentationState.Normal);
        public void SetDeadPreview(bool active) => SetState(active ? PlayerCameraPresentationState.Dead : PlayerCameraPresentationState.Normal);

        public void PulseFire()
        {
            PulseFire(defaultFirePulseSeconds);
        }

        public void PulseFire(float durationSeconds)
        {
            if (_requestedState != PlayerCameraPresentationState.Fire)
                _stateBeforeFire = _requestedState;
            _requestedState = PlayerCameraPresentationState.Fire;
            _fireUntil = Time.unscaledTime + Mathf.Max(0.01f, durationSeconds);
        }

        private PlayerCameraPresentationState ResolveState()
        {
            if (_network != null)
            {
                PlayerEntitySnapshot snapshot = _network.Snapshot;
                PlayerEntityMoveState moveState = (PlayerEntityMoveState)snapshot.moveState;
                PlayerEntityActionState actionState = (PlayerEntityActionState)snapshot.actionState;

                if (followAuthoritativeDeath &&
                    (moveState == PlayerEntityMoveState.Dead || actionState == PlayerEntityActionState.Dead))
                {
                    return PlayerCameraPresentationState.Dead;
                }

                if (followAuthoritativeMounted && moveState == PlayerEntityMoveState.Mounted)
                    return PlayerCameraPresentationState.Vehicle;
            }

            if (_requestedState == PlayerCameraPresentationState.Fire &&
                _fireUntil > 0f &&
                Time.unscaledTime >= _fireUntil)
            {
                _requestedState = _stateBeforeFire;
                _fireUntil = 0f;
            }

            return _requestedState;
        }


        private void ApplyCombatFacingOverride(bool shouldUseCombatFacing)
        {
            if (_locomotionCamera == null)
                return;

            if (shouldUseCombatFacing)
            {
                if (!_ownsCombatFacing)
                {
                    _combatFacingBeforeOverride = _locomotionCamera.CombatCameraActive;
                    _ownsCombatFacing = true;
                }

                _locomotionCamera.SetCombatCameraActive(true);
                return;
            }

            if (!_ownsCombatFacing)
                return;

            _locomotionCamera.SetCombatCameraActive(_combatFacingBeforeOverride);
            _ownsCombatFacing = false;
        }

        private PlayerCameraPresentationProfile GetProfile(PlayerCameraPresentationState state)
        {
            switch (state)
            {
                case PlayerCameraPresentationState.Fire: return fire;
                case PlayerCameraPresentationState.Aim: return aim;
                case PlayerCameraPresentationState.Scope: return scope;
                case PlayerCameraPresentationState.Vehicle: return vehicle;
                case PlayerCameraPresentationState.Dead: return dead;
                default: return normal;
            }
        }

        private bool TryResolveGameplayCamera()
        {
            if (IsUsableGameplayCamera(_camera))
                return true;

            Camera main = Camera.main;
            if (IsUsableGameplayCamera(main))
            {
                CaptureCamera(main);
                return true;
            }

            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; ++i)
            {
                if (!IsUsableGameplayCamera(cameras[i]))
                    continue;

                CaptureCamera(cameras[i]);
                return true;
            }

            return false;
        }

        private void CaptureCamera(Camera camera)
        {
            if (_camera == camera)
                return;

            _camera = camera;
            _baselineFieldOfView = _camera.fieldOfView;
            _currentFieldOfView = _baselineFieldOfView;
            _baselineCaptured = true;
        }

        private static bool IsUsableGameplayCamera(Camera candidate)
        {
            if (candidate == null || !candidate.isActiveAndEnabled || candidate.targetTexture != null)
                return false;

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
    }
}
