using System;
using Game.Shared.Characters;
using Game.Shared.Combat;
using Player.Shared;
using Player.Networking;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only adapter for the canonical PlayerHumanoid controller contract.
    /// Shared/server code never references Animator parameter names. Runtime movement
    /// state comes from the compact PlayerEntity snapshot and is converted to local
    /// presentation parameters here for both local and remote players.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumanoidAnimatorPresentationAdapter : MonoBehaviour,
        ICharacterPresentationPreferencesPresenter,
        ICharacterCreatorLocomotionPreview
    {
        private const string WeaponPoseOneHandLayerName = "Weapon Pose One Hand";
        private const string WeaponPoseTwoHandLayerName = "Weapon Pose Two Hand";
        private const string WeaponActionLayerName = "Weapon Actions";
        private const string DeathLayerName = "Client Presentation Death";
        private const string DeathState01 = "CP Death 01";
        private const string DeathState02 = "CP Death 02";
        private const string DeathState03 = "CP Death 03";
        private const string DeathState04 = "CP Death 04";
        private const float DefaultCombatActionHoldSeconds = 0.95f;
        private const float DefaultReloadActionHoldSeconds = 2.25f;

        [SerializeField] private Animator animator;

        [Header("Appearance / Preview")]
        [SerializeField] private string movementStyleParameter = "BodyAnimationStyle";

        [Header("Runtime Locomotion")]
        [SerializeField] private string moveXParameter = "CP_MoveX";
        [SerializeField] private string moveZParameter = "CP_MoveZ";
        [SerializeField] private string speedParameter = "CP_Speed";
        [SerializeField] private string gaitParameter = "CP_Gait";
        [SerializeField] private string gaitFloatParameter = "CP_GaitFloat";
        [SerializeField] private string movingParameter = "CP_Moving";
        [SerializeField] private string crouchedParameter = "CP_Crouched";
        [SerializeField] private string groundedParameter = "CP_Grounded";
        [SerializeField] private string verticalSpeedParameter = "CP_VerticalSpeed";
        [SerializeField] private string airbornePhaseParameter = "CP_AirbornePhase";
        [SerializeField] private string landingGaitParameter = "CP_LandingGait";
        [SerializeField] private string landStrengthParameter = "CP_LandStrength";

        [Header("Runtime Combat Stance")]
        [Tooltip("Existing canonical weapon stance integer. Values align with the prior WeaponAnimatorContract.DefaultStance ids.")]
        [SerializeField] private string weaponStanceParameter = "WeaponStance";
        [SerializeField] private string weaponDrawStateParameter = "WeaponDrawState";
        [SerializeField] private string weaponActionParameter = "WeaponAction";
        [SerializeField] private string weaponActionTriggerParameter = "WeaponActionTrigger";
        [SerializeField] private string aimingParameter = "IsAiming";

        [Header("Runtime Death Presentation")]
        [Tooltip("Existing canonical PlayerHumanoid death flag. The authored death states are selected directly on the Client Presentation Death layer.")]
        [SerializeField] private string deadParameter = "DEAD";

        [Tooltip("Short client-only landing presentation hold before returning CP_AirbornePhase to Grounded.")]
        [Range(0.02f, 0.5f)] [SerializeField] private float landingHoldSeconds = 0.12f;

        private int _movementStyleHash;
        private int _moveXHash;
        private int _moveZHash;
        private int _speedHash;
        private int _gaitHash;
        private int _gaitFloatHash;
        private int _movingHash;
        private int _crouchedHash;
        private int _groundedHash;
        private int _verticalSpeedHash;
        private int _airbornePhaseHash;
        private int _landingGaitHash;
        private int _landStrengthHash;
        private int _weaponStanceHash;
        private int _weaponDrawStateHash;
        private int _weaponActionHash;
        private int _weaponActionTriggerHash;
        private int _aimingHash;
        private int _deadHash;

        private bool _hasMovementStyle;
        private bool _hasMoveX;
        private bool _hasMoveZ;
        private bool _hasSpeed;
        private bool _hasGait;
        private bool _hasGaitFloat;
        private bool _hasMoving;
        private bool _hasCrouched;
        private bool _hasGrounded;
        private bool _hasVerticalSpeed;
        private bool _hasAirbornePhase;
        private bool _hasLandingGait;
        private bool _hasLandStrength;
        private bool _hasWeaponStance;
        private bool _hasWeaponDrawState;
        private bool _hasWeaponAction;
        private bool _hasWeaponActionTrigger;
        private bool _hasAiming;
        private bool _hasDead;
        private int _weaponPoseOneHandLayerIndex = -1;
        private int _weaponPoseTwoHandLayerIndex = -1;
        private int _weaponActionLayerIndex = -1;
        private int _deathLayerIndex = -1;

        private int _resolvedAnimatorInstanceId;
        private bool _wasGrounded = true;
        private bool _airborneObserved;
        private float _mostNegativeVerticalSpeed;
        private float _landingUntil;
        private int _landingGait;
        private int _landingStrength;
        private float _weaponActionLayerHoldUntil;
        private bool _deathPresentationActive;
        private int _deathStateHash;

        private enum AirbornePhase
        {
            Grounded = 0,
            Jumping = 1,
            Falling = 2,
            Landing = 3,
        }

        private enum GaitTier
        {
            Walk = 0,
            Run = 1,
            Sprint = 2,
        }

        private void Awake() => ResolveAnimatorAndParameters();

        public void ApplyPresentationPreferences(CharacterPresentationPreferences preferences)
        {
            ResolveAnimatorAndParameters();
            if (animator == null || !_hasMovementStyle)
                return;

            CharacterPresentationPreferences value =
                preferences?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            animator.SetFloat(_movementStyleHash, value.movementStyle / 255f);
        }

        /// <summary>
        /// Drives the canonical PlayerHumanoid locomotion layer from the same authoritative
        /// snapshot contract for both owner and remote players. Direction is presentation-only
        /// and is reconstructed locally from the interpolated world trajectory.
        /// </summary>
        public void ApplyLocomotionSnapshot(
            PlayerEntitySnapshot snapshot,
            Vector3 localPlanarVelocity,
            HumanoidCombatStance equippedCombatStance,
            float frameDelta)
        {
            ResolveAnimatorAndParameters();
            if (animator == null)
                return;

            PlayerEntityFlags flags = (PlayerEntityFlags)snapshot.flags;
            PlayerEntityMoveState moveState = (PlayerEntityMoveState)snapshot.moveState;

            bool dead = moveState == PlayerEntityMoveState.Dead ||
                        (PlayerEntityActionState)snapshot.actionState == PlayerEntityActionState.Dead;
            bool grounded = dead || (flags & PlayerEntityFlags.Grounded) != 0;
            bool crouched = !dead &&
                            (moveState == PlayerEntityMoveState.Crouched ||
                             (flags & PlayerEntityFlags.Crouching) != 0);
            bool moving = !dead && !crouched &&
                          (moveState == PlayerEntityMoveState.Moving || snapshot.MoveSpeed > 0.05f);

            int gait = ResolveGait(flags, moving);
            Vector2 direction = ResolveDirection(localPlanarVelocity, moving);
            // Normalize against the shared authoritative sprint ceiling so animation
            // timing/blend remains unchanged when gameplay movement speed is retuned.
            // The current 15% distance reduction therefore reduces world travel, not the
            // apparent animation cadence.
            float normalizedSpeed = moving
                ? Mathf.Clamp01(snapshot.MoveSpeed / Mathf.Max(0.1f, PlayerGameplaySettingsRuntime.SprintSpeed))
                : 0f;
            float verticalSpeed = snapshot.VerticalSpeed;

            AirbornePhase airbornePhase = ResolveAirbornePhase(
                grounded,
                verticalSpeed,
                moving,
                gait);

            bool presentationGrounded =
                airbornePhase == AirbornePhase.Grounded ||
                airbornePhase == AirbornePhase.Landing;

            SetFloat(_moveXHash, _hasMoveX, direction.x);
            SetFloat(_moveZHash, _hasMoveZ, direction.y);
            SetFloat(_speedHash, _hasSpeed, normalizedSpeed);
            SetInteger(_gaitHash, _hasGait, gait);
            SetFloat(_gaitFloatHash, _hasGaitFloat, gait);
            SetBool(_movingHash, _hasMoving, moving);
            SetBool(_crouchedHash, _hasCrouched, crouched);
            SetBool(_groundedHash, _hasGrounded, presentationGrounded);
            SetFloat(_verticalSpeedHash, _hasVerticalSpeed, verticalSpeed);
            SetInteger(_airbornePhaseHash, _hasAirbornePhase, (int)airbornePhase);
            SetInteger(_landingGaitHash, _hasLandingGait, _landingGait);
            SetInteger(_landStrengthHash, _hasLandStrength, _landingStrength);

            bool combatReady = !dead && (flags & PlayerEntityFlags.CombatReady) != 0;
            HumanoidCombatStance combatStance = !combatReady
                ? HumanoidCombatStance.None
                : equippedCombatStance == HumanoidCombatStance.None
                    ? HumanoidCombatStance.Unarmed
                    : equippedCombatStance;
            SetInteger(_weaponStanceHash, _hasWeaponStance, (int)combatStance);
            SetInteger(_weaponDrawStateHash, _hasWeaponDrawState, combatReady ? 2 : 0); // Drawn / Holstered
            SetBool(_aimingHash, _hasAiming, combatReady && IsAimingStance(combatStance));
            ApplyCombatReadyPose(combatReady, combatStance);
            UpdateCombatActionLayer(!dead);
            ApplyDeathPresentation(snapshot, dead);
        }

        /// <summary>
        /// Plays one already-authored canonical PlayerHumanoid primary combat action. This is
        /// client presentation only; authoritative attack acceptance, ammo, stamina, contact and
        /// damage remain owned by the standalone GameServer.
        /// </summary>
        public bool PlayPrimaryCombatAction(HumanoidCombatStance stance, ushort sequence = 0)
        {
            ResolveAnimatorAndParameters();
            if (animator == null || animator.runtimeAnimatorController == null ||
                stance == HumanoidCombatStance.None || _weaponActionLayerIndex < 0)
                return false;

            if (!TryResolvePrimaryActionState(stance, sequence, out string relativePath, out string stateName))
                return false;

            int fullPathHash = Animator.StringToHash(WeaponActionLayerName + "." + relativePath);
            int shortNameHash = Animator.StringToHash(stateName);
            int stateHash = animator.HasState(_weaponActionLayerIndex, fullPathHash)
                ? fullPathHash
                : animator.HasState(_weaponActionLayerIndex, shortNameHash)
                    ? shortNameHash
                    : 0;
            if (stateHash == 0)
                return false;

            // Mirror the established PlayerHumanoid weapon-action contract. Directly selecting
            // the authored state makes the visual response deterministic even if an Animator
            // transition was not generated for a particular family.
            SetInteger(_weaponStanceHash, _hasWeaponStance, (int)stance);
            SetInteger(_weaponDrawStateHash, _hasWeaponDrawState, 2); // Drawn
            SetInteger(_weaponActionHash, _hasWeaponAction, 1); // Primary
            if (_hasWeaponActionTrigger)
                animator.SetTrigger(_weaponActionTriggerHash);

            SetLayerWeight(_weaponActionLayerIndex, 1f);
            animator.CrossFadeInFixedTime(stateHash, 0.02f, _weaponActionLayerIndex, 0f);
            _weaponActionLayerHoldUntil = Mathf.Max(
                _weaponActionLayerHoldUntil,
                Time.unscaledTime + DefaultCombatActionHoldSeconds);
            return true;
        }

        /// <summary>
        /// Plays one already-authored firearm reload state. Reload acceptance and magazine
        /// mutation remain server-authoritative; this method only drives the local Animator.
        /// </summary>
        public bool PlayReloadCombatAction(HumanoidCombatStance stance, byte sequence = 0)
        {
            _ = sequence;
            ResolveAnimatorAndParameters();
            if (animator == null || animator.runtimeAnimatorController == null ||
                stance == HumanoidCombatStance.None || _weaponActionLayerIndex < 0)
            {
                return false;
            }

            if (!TryResolveReloadActionState(stance, out string relativePath, out string stateName))
                return false;

            int fullPathHash = Animator.StringToHash(WeaponActionLayerName + "." + relativePath);
            int shortNameHash = Animator.StringToHash(stateName);
            int stateHash = animator.HasState(_weaponActionLayerIndex, fullPathHash)
                ? fullPathHash
                : animator.HasState(_weaponActionLayerIndex, shortNameHash)
                    ? shortNameHash
                    : 0;
            if (stateHash == 0)
                return false;

            SetInteger(_weaponStanceHash, _hasWeaponStance, (int)stance);
            SetInteger(_weaponDrawStateHash, _hasWeaponDrawState, 2); // Drawn

            // Do not invent a new WeaponAction integer contract just for reload. The canonical
            // state is selected directly on the existing Weapon Actions layer.
            SetLayerWeight(_weaponActionLayerIndex, 1f);
            animator.CrossFadeInFixedTime(stateHash, 0.04f, _weaponActionLayerIndex, 0f);
            _weaponActionLayerHoldUntil = Mathf.Max(
                _weaponActionLayerHoldUntil,
                Time.unscaledTime + DefaultReloadActionHoldSeconds);
            return true;
        }

        public void PreviewIdle()
        {
            ResolveAnimatorAndParameters();
            if (animator == null)
                return;
            SetBool(_movingHash, _hasMoving, false);
            SetInteger(_gaitHash, _hasGait, (int)GaitTier.Walk);
            SetFloat(_gaitFloatHash, _hasGaitFloat, (int)GaitTier.Walk);
            SetBool(_groundedHash, _hasGrounded, true);
            SetInteger(_airbornePhaseHash, _hasAirbornePhase, (int)AirbornePhase.Grounded);
        }

        public void PreviewWalk()
        {
            ResolveAnimatorAndParameters();
            if (animator == null)
                return;
            SetInteger(_gaitHash, _hasGait, (int)GaitTier.Walk);
            SetFloat(_gaitFloatHash, _hasGaitFloat, (int)GaitTier.Walk);
            SetBool(_movingHash, _hasMoving, true);
            SetBool(_groundedHash, _hasGrounded, true);
            SetInteger(_airbornePhaseHash, _hasAirbornePhase, (int)AirbornePhase.Grounded);
        }

        public void PreviewRun()
        {
            ResolveAnimatorAndParameters();
            if (animator == null)
                return;
            SetInteger(_gaitHash, _hasGait, (int)GaitTier.Run);
            SetFloat(_gaitFloatHash, _hasGaitFloat, (int)GaitTier.Run);
            SetBool(_movingHash, _hasMoving, true);
            SetBool(_groundedHash, _hasGrounded, true);
            SetInteger(_airbornePhaseHash, _hasAirbornePhase, (int)AirbornePhase.Grounded);
        }

        private int ResolveGait(PlayerEntityFlags flags, bool moving)
        {
            if (!moving)
                return (int)GaitTier.Walk;
            if ((flags & PlayerEntityFlags.Sprinting) != 0)
                return (int)GaitTier.Sprint;
            if ((flags & PlayerEntityFlags.Running) != 0)
                return (int)GaitTier.Run;
            return (int)GaitTier.Walk;
        }

        private static Vector2 ResolveDirection(Vector3 localPlanarVelocity, bool moving)
        {
            Vector2 direction = new Vector2(localPlanarVelocity.x, localPlanarVelocity.z);
            if (direction.sqrMagnitude > 0.0001f)
                return direction.normalized;

            // On the first movement frame there may not yet be enough buffered position
            // history to reconstruct direction. Forward is the safest deterministic fallback.
            return moving ? Vector2.up : Vector2.zero;
        }

        private AirbornePhase ResolveAirbornePhase(
            bool grounded,
            float verticalSpeed,
            bool moving,
            int gait)
        {
            float now = Time.unscaledTime;

            if (!grounded)
            {
                _landingUntil = 0f;
                _airborneObserved = true;
                _mostNegativeVerticalSpeed = Mathf.Min(_mostNegativeVerticalSpeed, verticalSpeed);
                _wasGrounded = false;
                return verticalSpeed > 0.05f ? AirbornePhase.Jumping : AirbornePhase.Falling;
            }

            if (!_wasGrounded && _airborneObserved)
            {
                _landingUntil = now + Mathf.Max(0.02f, landingHoldSeconds);
                _landingGait = ResolveLandingGait(moving, gait);
                _landingStrength = ResolveLandingStrength(_mostNegativeVerticalSpeed);
                _airborneObserved = false;
                _mostNegativeVerticalSpeed = 0f;
            }

            _wasGrounded = true;
            if (_landingUntil > now)
                return AirbornePhase.Landing;

            _landingUntil = 0f;
            return AirbornePhase.Grounded;
        }

        private static int ResolveLandingGait(bool moving, int gait)
        {
            if (!moving)
                return 0; // Idle
            if (gait >= (int)GaitTier.Sprint)
                return 3; // Sprint
            if (gait >= (int)GaitTier.Run)
                return 2; // Run
            return 1;     // Walk
        }

        private static int ResolveLandingStrength(float mostNegativeVerticalSpeed)
        {
            float impact = Mathf.Abs(Mathf.Min(0f, mostNegativeVerticalSpeed));
            if (impact >= 8f)
                return 2; // Hard
            if (impact >= 4f)
                return 1; // Medium
            return 0;     // Soft
        }

        private void ResolveAnimatorAndParameters()
        {
            if (animator == null)
                animator = GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                _resolvedAnimatorInstanceId = 0;
                return;
            }

            int instanceId = animator.GetInstanceID();
            if (_resolvedAnimatorInstanceId == instanceId)
                return;

            _resolvedAnimatorInstanceId = instanceId;
            _movementStyleHash = Animator.StringToHash(movementStyleParameter ?? string.Empty);
            _moveXHash = Animator.StringToHash(moveXParameter ?? string.Empty);
            _moveZHash = Animator.StringToHash(moveZParameter ?? string.Empty);
            _speedHash = Animator.StringToHash(speedParameter ?? string.Empty);
            _gaitHash = Animator.StringToHash(gaitParameter ?? string.Empty);
            _gaitFloatHash = Animator.StringToHash(gaitFloatParameter ?? string.Empty);
            _movingHash = Animator.StringToHash(movingParameter ?? string.Empty);
            _crouchedHash = Animator.StringToHash(crouchedParameter ?? string.Empty);
            _groundedHash = Animator.StringToHash(groundedParameter ?? string.Empty);
            _verticalSpeedHash = Animator.StringToHash(verticalSpeedParameter ?? string.Empty);
            _airbornePhaseHash = Animator.StringToHash(airbornePhaseParameter ?? string.Empty);
            _landingGaitHash = Animator.StringToHash(landingGaitParameter ?? string.Empty);
            _landStrengthHash = Animator.StringToHash(landStrengthParameter ?? string.Empty);
            _weaponStanceHash = Animator.StringToHash(weaponStanceParameter ?? string.Empty);
            _weaponDrawStateHash = Animator.StringToHash(weaponDrawStateParameter ?? string.Empty);
            _weaponActionHash = Animator.StringToHash(weaponActionParameter ?? string.Empty);
            _weaponActionTriggerHash = Animator.StringToHash(weaponActionTriggerParameter ?? string.Empty);
            _aimingHash = Animator.StringToHash(aimingParameter ?? string.Empty);
            _deadHash = Animator.StringToHash(deadParameter ?? string.Empty);

            _hasMovementStyle = HasParameter(movementStyleParameter, AnimatorControllerParameterType.Float);
            _hasMoveX = HasParameter(moveXParameter, AnimatorControllerParameterType.Float);
            _hasMoveZ = HasParameter(moveZParameter, AnimatorControllerParameterType.Float);
            _hasSpeed = HasParameter(speedParameter, AnimatorControllerParameterType.Float);
            _hasGait = HasParameter(gaitParameter, AnimatorControllerParameterType.Int);
            _hasGaitFloat = HasParameter(gaitFloatParameter, AnimatorControllerParameterType.Float);
            _hasMoving = HasParameter(movingParameter, AnimatorControllerParameterType.Bool);
            _hasCrouched = HasParameter(crouchedParameter, AnimatorControllerParameterType.Bool);
            _hasGrounded = HasParameter(groundedParameter, AnimatorControllerParameterType.Bool);
            _hasVerticalSpeed = HasParameter(verticalSpeedParameter, AnimatorControllerParameterType.Float);
            _hasAirbornePhase = HasParameter(airbornePhaseParameter, AnimatorControllerParameterType.Int);
            _hasLandingGait = HasParameter(landingGaitParameter, AnimatorControllerParameterType.Int);
            _hasLandStrength = HasParameter(landStrengthParameter, AnimatorControllerParameterType.Int);
            _hasWeaponStance = HasParameter(weaponStanceParameter, AnimatorControllerParameterType.Int);
            _hasWeaponDrawState = HasParameter(weaponDrawStateParameter, AnimatorControllerParameterType.Int);
            _hasWeaponAction = HasParameter(weaponActionParameter, AnimatorControllerParameterType.Int);
            _hasWeaponActionTrigger = HasParameter(weaponActionTriggerParameter, AnimatorControllerParameterType.Trigger);
            _hasAiming = HasParameter(aimingParameter, AnimatorControllerParameterType.Bool);
            _hasDead = HasParameter(deadParameter, AnimatorControllerParameterType.Bool);
            _weaponPoseOneHandLayerIndex = animator.GetLayerIndex(WeaponPoseOneHandLayerName);
            _weaponPoseTwoHandLayerIndex = animator.GetLayerIndex(WeaponPoseTwoHandLayerName);
            _weaponActionLayerIndex = animator.GetLayerIndex(WeaponActionLayerName);
            _deathLayerIndex = animator.GetLayerIndex(DeathLayerName);
            _deathPresentationActive = false;
            _deathStateHash = 0;
        }

        private void ApplyDeathPresentation(PlayerEntitySnapshot snapshot, bool dead)
        {
            SetBool(_deadHash, _hasDead, dead);

            if (_deathLayerIndex < 0 || animator == null || animator.runtimeAnimatorController == null)
            {
                _deathPresentationActive = false;
                _deathStateHash = 0;
                return;
            }

            if (!dead)
            {
                if (_deathPresentationActive || animator.GetLayerWeight(_deathLayerIndex) > 0f)
                    SetLayerWeight(_deathLayerIndex, 0f);

                _deathPresentationActive = false;
                _deathStateHash = 0;
                return;
            }

            // Death is authoritative state, not a local hit reaction. Suppress competing
            // upper-body/action presentation before selecting the full-body authored death.
            SetLayerWeight(_weaponPoseOneHandLayerIndex, 0f);
            SetLayerWeight(_weaponPoseTwoHandLayerIndex, 0f);
            SetLayerWeight(_weaponActionLayerIndex, 0f);
            _weaponActionLayerHoldUntil = 0f;
            SetInteger(_weaponActionHash, _hasWeaponAction, 0);

            SetLayerWeight(_deathLayerIndex, 1f);
            if (_deathPresentationActive)
            {
                // Keep the already-selected authored death state alive for the full
                // authoritative corpse lifetime. The controller's one-shot death state
                // may transition to an empty/default state after the clip finishes; if
                // that happens, or once the clip reaches its end, pin the same state near
                // its final frame instead of exposing the humanoid bind/T-pose.
                AnimatorStateInfo deathState = animator.GetCurrentAnimatorStateInfo(_deathLayerIndex);
                bool selectedStateActive =
                    deathState.fullPathHash == _deathStateHash ||
                    deathState.shortNameHash == _deathStateHash;

                if (!selectedStateActive || deathState.normalizedTime >= 0.98f)
                {
                    animator.Play(_deathStateHash, _deathLayerIndex, 0.98f);
                    animator.Update(0f);
                }
                return;
            }

            string stateName = ResolveDeathStateName(snapshot.entityId, snapshot.generation);
            int fullPathHash = Animator.StringToHash(DeathLayerName + "." + stateName);
            int shortNameHash = Animator.StringToHash(stateName);
            int stateHash = animator.HasState(_deathLayerIndex, fullPathHash)
                ? fullPathHash
                : animator.HasState(_deathLayerIndex, shortNameHash)
                    ? shortNameHash
                    : 0;

            if (stateHash == 0)
                return;

            _deathStateHash = stateHash;
            _deathPresentationActive = true;
            animator.CrossFadeInFixedTime(_deathStateHash, 0.05f, _deathLayerIndex, 0f);
        }

        private static string ResolveDeathStateName(uint entityId, ushort generation)
        {
            // Presentation-only deterministic variation. No extra network field is required,
            // and every observer chooses the same authored death for the same actor generation.
            uint selector = entityId ^ ((uint)generation * 397u);
            switch (selector & 3u)
            {
                case 1u: return DeathState02;
                case 2u: return DeathState03;
                case 3u: return DeathState04;
                default: return DeathState01;
            }
        }

        private void UpdateCombatActionLayer(bool presentationEnabled)
        {
            if (!presentationEnabled)
                _weaponActionLayerHoldUntil = 0f;

            bool active = presentationEnabled && Time.unscaledTime < _weaponActionLayerHoldUntil;
            SetLayerWeight(_weaponActionLayerIndex, active ? 1f : 0f);
            if (!active)
                SetInteger(_weaponActionHash, _hasWeaponAction, 0); // None
        }

        private void ApplyCombatReadyPose(bool combatReady, HumanoidCombatStance stance)
        {
            if (animator == null || animator.runtimeAnimatorController == null ||
                !_hasWeaponStance || !_hasWeaponDrawState ||
                !combatReady || stance == HumanoidCombatStance.None)
            {
                SetLayerWeight(_weaponPoseOneHandLayerIndex, 0f);
                SetLayerWeight(_weaponPoseTwoHandLayerIndex, 0f);
                return;
            }

            if (!TryResolveReadyPose(stance, out string layerName, out string relativePath))
            {
                SetLayerWeight(_weaponPoseOneHandLayerIndex, 0f);
                SetLayerWeight(_weaponPoseTwoHandLayerIndex, 0f);
                return;
            }

            int layerIndex = string.Equals(layerName, WeaponPoseOneHandLayerName, StringComparison.Ordinal)
                ? _weaponPoseOneHandLayerIndex
                : _weaponPoseTwoHandLayerIndex;
            if (layerIndex < 0)
                return;

            SetLayerWeight(
                _weaponPoseOneHandLayerIndex,
                layerIndex == _weaponPoseOneHandLayerIndex ? 1f : 0f);
            SetLayerWeight(
                _weaponPoseTwoHandLayerIndex,
                layerIndex == _weaponPoseTwoHandLayerIndex ? 1f : 0f);

            int fullPathHash = Animator.StringToHash(layerName + "." + relativePath);
            if (!animator.HasState(layerIndex, fullPathHash))
                return;

            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layerIndex);
            AnimatorStateInfo next = animator.IsInTransition(layerIndex)
                ? animator.GetNextAnimatorStateInfo(layerIndex)
                : default(AnimatorStateInfo);
            if (current.fullPathHash == fullPathHash || next.fullPathHash == fullPathHash)
                return;

            // Ready stance selection is client presentation only. The locomotion Base layer
            // remains in CP Idle/Walk/Run while this masked pose layer supplies the combat pose.
            animator.CrossFadeInFixedTime(fullPathHash, 0.08f, layerIndex, 0f);
        }

        private void SetLayerWeight(int layerIndex, float weight)
        {
            if (animator == null || layerIndex < 0)
                return;
            if (!Mathf.Approximately(animator.GetLayerWeight(layerIndex), weight))
                animator.SetLayerWeight(layerIndex, weight);
        }

        private static bool TryResolveReadyPose(
            HumanoidCombatStance stance,
            out string layerName,
            out string relativePath)
        {
            layerName = WeaponPoseTwoHandLayerName;
            switch (stance)
            {
                case HumanoidCombatStance.Pistol:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Pistol_Ready";
                    return true;
                case HumanoidCombatStance.Rifle:
                    relativePath = "Rifle_Ready";
                    return true;
                case HumanoidCombatStance.AssaultRifle:
                    relativePath = "Assault Rifle_Ready";
                    return true;
                case HumanoidCombatStance.DualPistol:
                    relativePath = "Dual Pistol_Ready";
                    return true;

                // Stance 5 is the retained legacy Bat id, but the canonical controller never
                // authored a Bat_Ready state. Use the existing two-hand blunt pose instead.
                case HumanoidCombatStance.Bat:
                    relativePath = "Melee.Two Hand Blunt.Two Hand Blunt_Ready";
                    return true;

                case HumanoidCombatStance.SmallBlade:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Blades.Small Blade.Small Blade_Ready";
                    return true;
                case HumanoidCombatStance.OneHandBlade:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Blades.One Hand Blade.One Hand Blade_Ready";
                    return true;
                case HumanoidCombatStance.TwoHandBlade:
                    relativePath = "Melee.Two Hand Blade.Two Hand Blade_Ready";
                    return true;
                case HumanoidCombatStance.SmallBlunt:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Blunt & Axes.Small Blunt.Small Blunt_Ready";
                    return true;
                case HumanoidCombatStance.OneHandBlunt:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Blunt & Axes.One Hand Blunt.One Hand Blunt_Ready";
                    return true;
                case HumanoidCombatStance.TwoHandBlunt:
                    relativePath = "Melee.Two Hand Blunt.Two Hand Blunt_Ready";
                    return true;
                case HumanoidCombatStance.FistWeapon:
                    relativePath = "Melee.Fist Weapon.Fist Weapon_Ready";
                    return true;
                case HumanoidCombatStance.OneHandAxe:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Blunt & Axes.One Hand Axe.One Hand Axe_Ready";
                    return true;
                case HumanoidCombatStance.TwoHandAxe:
                    relativePath = "Melee.Two Hand Axe.Two Hand Axe_Ready";
                    return true;
                case HumanoidCombatStance.Polearm:
                    relativePath = "Long Weapons.Polearm.Polearm_Ready";
                    return true;
                case HumanoidCombatStance.Staff:
                    relativePath = "Long Weapons.Staff.Staff_Ready";
                    return true;
                case HumanoidCombatStance.Shotgun:
                    relativePath = "Additional Firearms.Shotgun.Shotgun_Ready";
                    return true;
                case HumanoidCombatStance.SubmachineGun:
                    relativePath = "Additional Firearms.Submachine Gun.Submachine Gun_Ready";
                    return true;
                case HumanoidCombatStance.Bow:
                    relativePath = "Projectile.Bow.Bow_Ready";
                    return true;
                case HumanoidCombatStance.Crossbow:
                    relativePath = "Projectile.Crossbow.Crossbow_Ready";
                    return true;
                case HumanoidCombatStance.Shield:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Fist & Special.Shield.Shield_Ready";
                    return true;
                case HumanoidCombatStance.HeavyWeapon:
                    relativePath = "Heavy & Special.Heavy Weapon.Heavy Weapon_Ready";
                    return true;
                case HumanoidCombatStance.DualWieldMelee:
                    relativePath = "Melee.Dual Wield Melee.Dual Wield Melee_Ready";
                    return true;
                case HumanoidCombatStance.ImprovisedOneHand:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Fist & Special.Improvised One Hand.Improvised One Hand_Ready";
                    return true;
                case HumanoidCombatStance.ImprovisedTwoHand:
                    relativePath = "Heavy & Special.Improvised Two Hand.Improvised Two Hand_Ready";
                    return true;
                case HumanoidCombatStance.Stake:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Fist & Special.Stake.Stake_Ready";
                    return true;
                case HumanoidCombatStance.NaturalWeapon:
                    relativePath = "Melee.Natural Weapon.Natural Weapon_Ready";
                    return true;
                case HumanoidCombatStance.Unarmed:
                    relativePath = "Melee.Unarmed.Unarmed_Ready";
                    return true;
                case HumanoidCombatStance.Thrown:
                    layerName = WeaponPoseOneHandLayerName;
                    relativePath = "Fist & Special.Thrown.Thrown_Ready";
                    return true;
                default:
                    relativePath = null;
                    return false;
            }
        }

        internal static bool TryResolveReloadActionState(
            HumanoidCombatStance stance,
            out string relativePath,
            out string stateName)
        {
            string familyName;
            string actionGroup = null;
            switch (stance)
            {
                // Original firearm families are top-level states on Weapon Actions.
                case HumanoidCombatStance.Pistol:
                    familyName = "Pistol";
                    break;
                case HumanoidCombatStance.Rifle:
                    familyName = "Rifle";
                    break;
                case HumanoidCombatStance.AssaultRifle:
                    familyName = "Assault Rifle";
                    break;
                case HumanoidCombatStance.DualPistol:
                    familyName = "Dual Pistol";
                    break;

                // Expansion firearm families are nested exactly like their Primary states.
                case HumanoidCombatStance.Shotgun:
                    familyName = "Shotgun";
                    actionGroup = "Firearm Expansion";
                    break;
                case HumanoidCombatStance.SubmachineGun:
                    familyName = "Submachine Gun";
                    actionGroup = "Firearm Expansion";
                    break;
                case HumanoidCombatStance.HeavyWeapon:
                    familyName = "Heavy Weapon";
                    actionGroup = "Defense & Special";
                    break;
                default:
                    relativePath = null;
                    stateName = null;
                    return false;
            }

            stateName = familyName + "_Reload";
            relativePath = string.IsNullOrEmpty(actionGroup)
                ? stateName
                : actionGroup + "." + familyName + "." + stateName;
            return true;
        }

        internal static bool TryResolvePrimaryActionState(
            HumanoidCombatStance stance,
            out string relativePath,
            out string stateName)
        {
            return TryResolvePrimaryActionState(stance, 0, out relativePath, out stateName);
        }

        internal static bool TryResolvePrimaryActionState(
            HumanoidCombatStance stance,
            ushort sequence,
            out string relativePath,
            out string stateName)
        {
            // The canonical controller has no generic Unarmed_Primary state. It has six
            // authored punch variants under Melee Actions/Unarmed. Use the already-carried
            // presentation sequence to pick one deterministically without adding wire data.
            if (stance == HumanoidCombatStance.Unarmed)
            {
                switch (sequence % 6)
                {
                    case 1:
                        stateName = "Unarmed_Primary_Right_B";
                        break;
                    case 2:
                        stateName = "Unarmed_Primary_Left";
                        break;
                    case 3:
                        stateName = "Unarmed_Primary_Right_C";
                        break;
                    case 4:
                        stateName = "Unarmed_Primary_Left_B";
                        break;
                    case 5:
                        stateName = "Unarmed_Primary_Left_C";
                        break;
                    default:
                        stateName = "Unarmed_Primary_Right";
                        break;
                }

                relativePath = "Melee Actions.Unarmed." + stateName;
                return true;
            }

            string familyName;
            string actionGroup = null;
            switch (stance)
            {
                // The original four firearm families are top-level states on Weapon Actions.
                case HumanoidCombatStance.Pistol:
                    familyName = "Pistol";
                    break;
                case HumanoidCombatStance.Rifle:
                    familyName = "Rifle";
                    break;
                case HumanoidCombatStance.AssaultRifle:
                    familyName = "Assault Rifle";
                    break;
                case HumanoidCombatStance.DualPistol:
                    familyName = "Dual Pistol";
                    break;

                case HumanoidCombatStance.SmallBlade:
                    familyName = "Small Blade";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.OneHandBlade:
                    familyName = "One Hand Blade";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.TwoHandBlade:
                    familyName = "Two Hand Blade";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.SmallBlunt:
                    familyName = "Small Blunt";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.OneHandBlunt:
                    familyName = "One Hand Blunt";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.TwoHandBlunt:
                    familyName = "Two Hand Blunt";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.FistWeapon:
                    familyName = "Fist Weapon";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.OneHandAxe:
                    familyName = "One Hand Axe";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.TwoHandAxe:
                    familyName = "Two Hand Axe";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.Polearm:
                    familyName = "Polearm";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.Staff:
                    familyName = "Staff";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.Shotgun:
                    familyName = "Shotgun";
                    actionGroup = "Firearm Expansion";
                    break;
                case HumanoidCombatStance.SubmachineGun:
                    familyName = "Submachine Gun";
                    actionGroup = "Firearm Expansion";
                    break;
                case HumanoidCombatStance.Bow:
                    familyName = "Bow";
                    actionGroup = "Projectile Actions";
                    break;
                case HumanoidCombatStance.Crossbow:
                    familyName = "Crossbow";
                    actionGroup = "Projectile Actions";
                    break;
                case HumanoidCombatStance.Shield:
                    familyName = "Shield";
                    actionGroup = "Defense & Special";
                    break;
                case HumanoidCombatStance.HeavyWeapon:
                    familyName = "Heavy Weapon";
                    actionGroup = "Defense & Special";
                    break;
                case HumanoidCombatStance.DualWieldMelee:
                    familyName = "Dual Wield Melee";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.ImprovisedOneHand:
                    familyName = "Improvised One Hand";
                    actionGroup = "Defense & Special";
                    break;
                case HumanoidCombatStance.ImprovisedTwoHand:
                    familyName = "Improvised Two Hand";
                    actionGroup = "Defense & Special";
                    break;
                case HumanoidCombatStance.Stake:
                    familyName = "Stake";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.NaturalWeapon:
                    familyName = "Natural Weapon";
                    actionGroup = "Melee Actions";
                    break;
                case HumanoidCombatStance.Thrown:
                    familyName = "Thrown";
                    actionGroup = "Projectile Actions";
                    break;
                default:
                    relativePath = null;
                    stateName = null;
                    return false;
            }

            stateName = familyName + "_Primary";
            relativePath = string.IsNullOrEmpty(actionGroup)
                ? stateName
                : actionGroup + "." + familyName + "." + stateName;
            return true;
        }

        private static bool IsAimingStance(HumanoidCombatStance stance)
        {
            switch (stance)
            {
                case HumanoidCombatStance.Pistol:
                case HumanoidCombatStance.Rifle:
                case HumanoidCombatStance.AssaultRifle:
                case HumanoidCombatStance.DualPistol:
                case HumanoidCombatStance.Shotgun:
                case HumanoidCombatStance.SubmachineGun:
                case HumanoidCombatStance.Bow:
                case HumanoidCombatStance.Crossbow:
                case HumanoidCombatStance.HeavyWeapon:
                case HumanoidCombatStance.Thrown:
                    return true;
                default:
                    return false;
            }
        }

        private bool HasParameter(string parameterName, AnimatorControllerParameterType expectedType)
        {
            if (animator == null || string.IsNullOrWhiteSpace(parameterName))
                return false;

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; ++i)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter.type == expectedType &&
                    string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private void SetFloat(int hash, bool available, float value)
        {
            if (available)
                animator.SetFloat(hash, value);
        }

        private void SetInteger(int hash, bool available, int value)
        {
            if (available)
                animator.SetInteger(hash, value);
        }

        private void SetBool(int hash, bool available, bool value)
        {
            if (available)
                animator.SetBool(hash, value);
        }
    }
}
