using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only humanoid IK presentation for weapon hands, look/spine aiming, and
    /// optional foot placement. Requires an Animator layer with IK Pass enabled.
    /// No IK result is authoritative gameplay state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumanoidIkPresentationController : MonoBehaviour
    {
        [Header("Weapon Hand IK")]
        public bool weaponIkEnabled = true;
        public Transform leftHandTarget;
        public Transform rightHandTarget;
        [Range(0f, 1f)] public float leftHandPositionWeight = 1f;
        [Range(0f, 1f)] public float leftHandRotationWeight = 1f;
        [Range(0f, 1f)] public float rightHandPositionWeight = 0f;
        [Range(0f, 1f)] public float rightHandRotationWeight = 0f;

        [Header("Look / Spine Aim")]
        public bool lookIkEnabled = true;
        public Transform lookTarget;
        [Range(0f, 1f)] public float lookWeight = 0.75f;
        [Range(0f, 1f)] public float bodyWeight = 0.35f;
        [Range(0f, 1f)] public float headWeight = 0.75f;
        [Range(0f, 1f)] public float eyesWeight = 0f;
        [Range(0f, 1f)] public float clampWeight = 0.45f;

        [Header("Foot IK")]
        public bool footIkEnabled = true;
        public LayerMask footGroundLayers = Physics.DefaultRaycastLayers;
        [Min(0.05f)] public float footRayStartHeight = 0.45f;
        [Min(0.05f)] public float footRayDistance = 1.0f;
        [Min(0f)] public float footSoleOffset = 0.03f;
        [Range(0f, 1f)] public float footPositionWeight = 0.85f;
        [Range(0f, 1f)] public float footRotationWeight = 0.75f;
        [Min(0.01f)] public float footBlendSharpness = 16f;

        private readonly RaycastHit[] _footHits = new RaycastHit[12];
        private Animator _animator;
        private float _leftFootBlend;
        private float _rightFootBlend;

        public Animator Animator => _animator;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        private void OnEnable()
        {
            if (_animator == null)
                _animator = GetComponent<Animator>();
        }

        public void BindWeaponIkTargets(Transform leftHand, Transform rightHand, Transform aimLookTarget = null)
        {
            leftHandTarget = leftHand;
            rightHandTarget = rightHand;
            if (aimLookTarget != null)
                lookTarget = aimLookTarget;
        }

        public void ClearWeaponIkTargets()
        {
            leftHandTarget = null;
            rightHandTarget = null;
            lookTarget = null;
        }

        public void SetLookTarget(Transform target)
        {
            lookTarget = target;
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (_animator == null || !_animator.isActiveAndEnabled || !_animator.isHuman)
                return;

            ApplyWeaponHands();
            ApplyLook();
            ApplyFeet();
        }

        private void ApplyWeaponHands()
        {
            float leftPosition = weaponIkEnabled && leftHandTarget != null
                ? Mathf.Clamp01(leftHandPositionWeight)
                : 0f;
            float leftRotation = weaponIkEnabled && leftHandTarget != null
                ? Mathf.Clamp01(leftHandRotationWeight)
                : 0f;
            float rightPosition = weaponIkEnabled && rightHandTarget != null
                ? Mathf.Clamp01(rightHandPositionWeight)
                : 0f;
            float rightRotation = weaponIkEnabled && rightHandTarget != null
                ? Mathf.Clamp01(rightHandRotationWeight)
                : 0f;

            _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, leftPosition);
            _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, leftRotation);
            _animator.SetIKPositionWeight(AvatarIKGoal.RightHand, rightPosition);
            _animator.SetIKRotationWeight(AvatarIKGoal.RightHand, rightRotation);

            if (leftPosition > 0f)
                _animator.SetIKPosition(AvatarIKGoal.LeftHand, leftHandTarget.position);
            if (leftRotation > 0f)
                _animator.SetIKRotation(AvatarIKGoal.LeftHand, leftHandTarget.rotation);
            if (rightPosition > 0f)
                _animator.SetIKPosition(AvatarIKGoal.RightHand, rightHandTarget.position);
            if (rightRotation > 0f)
                _animator.SetIKRotation(AvatarIKGoal.RightHand, rightHandTarget.rotation);
        }

        private void ApplyLook()
        {
            bool active = lookIkEnabled && lookTarget != null;
            float weight = active ? Mathf.Clamp01(lookWeight) : 0f;
            _animator.SetLookAtWeight(
                weight,
                Mathf.Clamp01(bodyWeight),
                Mathf.Clamp01(headWeight),
                Mathf.Clamp01(eyesWeight),
                Mathf.Clamp01(clampWeight));

            if (active)
                _animator.SetLookAtPosition(lookTarget.position);
        }

        private void ApplyFeet()
        {
            if (!footIkEnabled || footGroundLayers.value == 0)
            {
                _leftFootBlend = 0f;
                _rightFootBlend = 0f;
                SetFootWeights(AvatarIKGoal.LeftFoot, 0f);
                SetFootWeights(AvatarIKGoal.RightFoot, 0f);
                return;
            }

            ApplyFoot(AvatarIKGoal.LeftFoot, HumanBodyBones.LeftFoot, ref _leftFootBlend);
            ApplyFoot(AvatarIKGoal.RightFoot, HumanBodyBones.RightFoot, ref _rightFootBlend);
        }

        private void ApplyFoot(AvatarIKGoal goal, HumanBodyBones bone, ref float blend)
        {
            Transform foot = _animator.GetBoneTransform(bone);
            if (foot == null)
            {
                blend = 0f;
                SetFootWeights(goal, 0f);
                return;
            }

            Vector3 origin = foot.position + Vector3.up * Mathf.Max(0.05f, footRayStartHeight);
            float distance = Mathf.Max(0.05f, footRayStartHeight + footRayDistance);
            int hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                _footHits,
                distance,
                footGroundLayers,
                QueryTriggerInteraction.Ignore);

            bool found = false;
            RaycastHit bestHit = default;
            float nearest = float.MaxValue;
            for (int i = 0; i < hitCount; ++i)
            {
                Transform hitTransform = _footHits[i].collider != null
                    ? _footHits[i].collider.transform
                    : null;
                if (hitTransform == null || hitTransform == transform || hitTransform.IsChildOf(transform))
                    continue;

                if (_footHits[i].distance < nearest)
                {
                    nearest = _footHits[i].distance;
                    bestHit = _footHits[i];
                    found = true;
                }
            }

            float targetBlend = found ? 1f : 0f;
            float t = 1f - Mathf.Exp(-Mathf.Max(0.01f, footBlendSharpness) * Time.deltaTime);
            blend = Mathf.Lerp(blend, targetBlend, t);
            SetFootWeights(goal, blend);

            if (!found || blend <= 0.001f)
                return;

            Vector3 targetPosition = bestHit.point + bestHit.normal * Mathf.Max(0f, footSoleOffset);
            Quaternion targetRotation = Quaternion.FromToRotation(Vector3.up, bestHit.normal) * foot.rotation;
            _animator.SetIKPosition(goal, targetPosition);
            _animator.SetIKRotation(goal, targetRotation);
        }

        private void SetFootWeights(AvatarIKGoal goal, float blend)
        {
            _animator.SetIKPositionWeight(goal, Mathf.Clamp01(footPositionWeight) * blend);
            _animator.SetIKRotationWeight(goal, Mathf.Clamp01(footRotationWeight) * blend);
        }
    }
}
