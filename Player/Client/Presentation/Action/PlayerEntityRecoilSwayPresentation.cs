using Player.Client;
using Player.Networking;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only recoil and weapon-sway presentation. Gameplay spread, accuracy,
    /// ammunition, attack cadence, hit validation, and damage remain server-owned.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10200)]
    public sealed class PlayerEntityRecoilSwayPresentation : MonoBehaviour
    {
        [Header("Camera Recoil")]
        public bool cameraRecoilEnabled = true;
        [Min(0.01f)] public float recoilReturnSharpness = 14f;
        [Min(0.01f)] public float recoilFollowSharpness = 24f;
        [Min(0f)] public float maximumPitchRecoil = 18f;
        [Min(0f)] public float maximumYawRecoil = 10f;
        [Min(0f)] public float maximumRollRecoil = 6f;

        [Header("Weapon Sway")]
        public bool weaponSwayEnabled = true;
        [Tooltip("Optional visual-only weapon root. Bind this from the equipment/weapon presentation layer when one exists.")]
        public Transform weaponPresentationRoot;
        public Vector2 swayDegreesPerMouseUnit = new Vector2(0.45f, 0.35f);
        [Min(0f)] public float maximumSwayDegrees = 4.5f;
        [Min(0.01f)] public float swaySharpness = 14f;
        [Min(0f)] public float weaponKickDistance = 0.06f;
        [Min(0.01f)] public float weaponKickReturnSharpness = 18f;

        private PlayerEntityClient _client;
        private Camera _camera;
        private Vector3 _recoilTarget;
        private Vector3 _recoilCurrent;
        private float _weaponKickTarget;
        private float _weaponKickCurrent;
        private Vector2 _swayCurrent;

        private Transform _boundWeapon;
        private Vector3 _weaponBaseLocalPosition;
        private Quaternion _weaponBaseLocalRotation;

        private void Awake()
        {
            _client = GetComponent<PlayerEntityClient>();
        }

        private void OnDisable()
        {
            RestoreWeaponPose();
            _camera = null;
            _recoilTarget = Vector3.zero;
            _recoilCurrent = Vector3.zero;
            _weaponKickTarget = 0f;
            _weaponKickCurrent = 0f;
            _swayCurrent = Vector2.zero;
        }

        private void LateUpdate()
        {
            if (_client == null || !_client.IsOwnerClient)
                return;

            float dt = Mathf.Max(0f, Time.unscaledDeltaTime);
            UpdateRecoil(dt);
            UpdateWeaponSway(dt);

            if (cameraRecoilEnabled && TryResolveGameplayCamera())
            {
                _camera.transform.rotation *= Quaternion.Euler(
                    -_recoilCurrent.x,
                    _recoilCurrent.y,
                    _recoilCurrent.z);
            }
        }

        /// <summary>
        /// Adds visual recoil only. Call this after the server-authorized/local attack
        /// presentation event is accepted, not as the source of attack authority.
        /// </summary>
        [ContextMenu("Preview Recoil/Test Kick")]
        private void PreviewTestKick()
        {
            AddRecoil(2.4f, 0.45f, 0.2f, 1f);
        }

        public void AddRecoil(float pitchDegrees, float yawDegrees, float rollDegrees = 0f, float weaponKick01 = 1f)
        {
            _recoilTarget.x = Mathf.Clamp(
                _recoilTarget.x + Mathf.Max(0f, pitchDegrees),
                0f,
                Mathf.Max(0f, maximumPitchRecoil));
            _recoilTarget.y = Mathf.Clamp(
                _recoilTarget.y + yawDegrees,
                -Mathf.Max(0f, maximumYawRecoil),
                Mathf.Max(0f, maximumYawRecoil));
            _recoilTarget.z = Mathf.Clamp(
                _recoilTarget.z + rollDegrees,
                -Mathf.Max(0f, maximumRollRecoil),
                Mathf.Max(0f, maximumRollRecoil));

            _weaponKickTarget = Mathf.Max(
                _weaponKickTarget,
                Mathf.Clamp01(weaponKick01) * Mathf.Max(0f, weaponKickDistance));
        }

        public void BindWeaponPresentationRoot(Transform root)
        {
            RestoreWeaponPose();
            weaponPresentationRoot = root;
            CaptureWeaponPoseIfNeeded();
        }

        public void ClearWeaponPresentationRoot()
        {
            RestoreWeaponPose();
            weaponPresentationRoot = null;
            _boundWeapon = null;
        }

        private void UpdateRecoil(float dt)
        {
            float returnT = 1f - Mathf.Exp(-Mathf.Max(0.01f, recoilReturnSharpness) * dt);
            _recoilTarget = Vector3.Lerp(_recoilTarget, Vector3.zero, returnT);

            float followT = 1f - Mathf.Exp(-Mathf.Max(0.01f, recoilFollowSharpness) * dt);
            _recoilCurrent = Vector3.Lerp(_recoilCurrent, _recoilTarget, followT);

            float kickReturnT = 1f - Mathf.Exp(-Mathf.Max(0.01f, weaponKickReturnSharpness) * dt);
            _weaponKickTarget = Mathf.Lerp(_weaponKickTarget, 0f, kickReturnT);
            _weaponKickCurrent = Mathf.Lerp(_weaponKickCurrent, _weaponKickTarget, followT);
        }

        private void UpdateWeaponSway(float dt)
        {
            if (!weaponSwayEnabled || weaponPresentationRoot == null)
            {
                RestoreWeaponPose();
                return;
            }

            CaptureWeaponPoseIfNeeded();
            if (_boundWeapon == null)
                return;

            bool uiOwnsMouse = LocalClientInputGate.IsGameplayInputBlocked ||
                               LocalClientInputGate.IsPointerUiActive;
            float mouseX = uiOwnsMouse ? 0f : UnityEngine.Input.GetAxis("Mouse X");
            float mouseY = uiOwnsMouse ? 0f : UnityEngine.Input.GetAxis("Mouse Y");

            Vector2 targetSway = new Vector2(
                Mathf.Clamp(-mouseY * swayDegreesPerMouseUnit.y, -maximumSwayDegrees, maximumSwayDegrees),
                Mathf.Clamp(mouseX * swayDegreesPerMouseUnit.x, -maximumSwayDegrees, maximumSwayDegrees));

            float t = 1f - Mathf.Exp(-Mathf.Max(0.01f, swaySharpness) * dt);
            _swayCurrent = Vector2.Lerp(_swayCurrent, targetSway, t);

            _boundWeapon.localPosition = _weaponBaseLocalPosition + Vector3.back * _weaponKickCurrent;
            _boundWeapon.localRotation = _weaponBaseLocalRotation * Quaternion.Euler(
                _swayCurrent.x,
                _swayCurrent.y,
                0f);
        }

        private void CaptureWeaponPoseIfNeeded()
        {
            if (weaponPresentationRoot == null)
                return;

            if (_boundWeapon == weaponPresentationRoot)
                return;

            _boundWeapon = weaponPresentationRoot;
            _weaponBaseLocalPosition = _boundWeapon.localPosition;
            _weaponBaseLocalRotation = _boundWeapon.localRotation;
        }

        private void RestoreWeaponPose()
        {
            if (_boundWeapon == null)
                return;

            _boundWeapon.localPosition = _weaponBaseLocalPosition;
            _boundWeapon.localRotation = _weaponBaseLocalRotation;
        }

        private bool TryResolveGameplayCamera()
        {
            if (_camera != null && _camera.isActiveAndEnabled && _camera.targetTexture == null)
                return true;

            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled && main.targetTexture == null)
            {
                _camera = main;
                return true;
            }

            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (int i = 0; i < cameras.Length; ++i)
            {
                Camera candidate = cameras[i];
                if (candidate == null || !candidate.isActiveAndEnabled || candidate.targetTexture != null)
                    continue;

                string lowerName = candidate.name.ToLowerInvariant();
                if (lowerName.Contains("preview") ||
                    lowerName.Contains("portrait") ||
                    lowerName.Contains("creator") ||
                    lowerName.Contains("minimap") ||
                    lowerName.Contains("ui camera"))
                {
                    continue;
                }

                _camera = candidate;
                return true;
            }

            return false;
        }
    }
}
