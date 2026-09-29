using System.Collections.Generic;
using Game.Client.UI.Interactions;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Interactions;
using Player.Client;
using Player.Client.Presentation;
using Player.Networking;
using UnityEngine;

namespace Game.Client.Presentation.Combat
{
    /// <summary>
    /// Client-only impact feedback driven by the existing authoritative damage result.
    /// Player targets are resolved from the already-present objectId/generation in CombatDamageWire.
    /// Baked combat fixtures intentionally have no target id in that wire today, so owner-local
    /// presentation caches the fixture under the canonical attack ray when the attack is submitted
    /// and only flashes it if the GameServer later confirms positive damage.
    ///
    /// This component never mutates gameplay state and adds no network/protocol traffic.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class ClientDamageImpactFeedback : MonoBehaviour
    {
        private const float DirectCombatContactRadius = 0.85f;
        private const float DirectCombatOriginHeight = 1.10f;
        private const float CandidateGraceSeconds = 1.50f;
        private const int MaximumCastHits = 32;

        private readonly RaycastHit[] _castHits = new RaycastHit[MaximumCastHits];
        private PlayerEntityGameManager _manager;
        private GameObject _latestLocalFixtureCandidate;
        private double _latestLocalFixtureCandidateAt;

        private void OnEnable()
        {
            PlayerCombatPresentationRouter.ParticipantDamageReceived += OnDamageReceived;
            PlayerCombatPresentationRouter.ActionBodyPresented += OnActionBodyPresented;
            RefreshManagerReference();
        }

        private void Update()
        {
            if (_manager == null)
                RefreshManagerReference();

            if (_latestLocalFixtureCandidate != null &&
                Time.realtimeSinceStartupAsDouble - _latestLocalFixtureCandidateAt > CandidateGraceSeconds)
            {
                ClearFixtureCandidate();
            }
        }

        private void OnDisable()
        {
            PlayerCombatPresentationRouter.ParticipantDamageReceived -= OnDamageReceived;
            PlayerCombatPresentationRouter.ActionBodyPresented -= OnActionBodyPresented;
            _manager = null;
            ClearFixtureCandidate();
        }

        private void RefreshManagerReference()
        {
            _manager = FindFirstObjectByType<PlayerEntityGameManager>();
        }

        private void OnActionBodyPresented(
            CombatPresentationCueWire cue,
            PlayerEntityClient source)
        {
            // This hook runs only after PlayerCombatPresentationRouter has already triggered the
            // body animation. Impact feedback therefore cannot block an unarmed/melee/firearm swing.
            if (source == null || !source.IsOwnerClient)
                return;

            if (_manager == null)
                RefreshManagerReference();
            if (_manager == null)
                return;

            CaptureLocalFixtureCandidate(_manager.LatestCombatOwnerState.Mode);
        }

        private void CaptureLocalFixtureCandidate(BasicAttackMode mode)
        {
            // Every local attack replaces the previous candidate, including a miss. That prevents
            // a stale dummy from being flashed by a later confirmed impact on another target.
            ClearFixtureCandidate();

            if (_manager == null ||
                !PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) ||
                owner == null)
            {
                return;
            }

            PlayerCombatOwnerStateMessage state = _manager.LatestCombatOwnerState;
            float range = state.revision > 0 ? CombatRangePolicy.ClientRequestRangeForMode(state.Mode) : 0f;
            if (float.IsNaN(range) || float.IsInfinity(range) || range <= 0f)
                return;

            float yaw = owner.transform.eulerAngles.y;
            float pitch = 0f;
            PlayerEntityLocomotionCameraController camera =
                owner.GetComponent<PlayerEntityLocomotionCameraController>();
            if (camera != null)
            {
                yaw = camera.CurrentFacingYaw;
                pitch = camera.CurrentAimPitchDegrees;
                if (mode == BasicAttackMode.Firearm)
                    camera.TryGetPrecisionAim(out yaw, out pitch);
            }

            if (mode != BasicAttackMode.Firearm)
                pitch = Mathf.Clamp(pitch, -35f, 35f);

            Vector3 direction = BuildAimDirection(yaw, pitch);
            Vector3 origin = owner.PresentationPosition + Vector3.up * DirectCombatOriginHeight;
            float castDistance = Mathf.Max(0.1f, range) + DirectCombatContactRadius;

            int hitCount = Physics.SphereCastNonAlloc(
                origin,
                DirectCombatContactRadius,
                direction,
                _castHits,
                castDistance,
                ~0,
                QueryTriggerInteraction.Collide);

            ClientInteractionTargetMarker bestMarker = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < hitCount; ++i)
            {
                Collider collider = _castHits[i].collider;
                if (collider == null)
                    continue;

                ClientInteractionTargetMarker marker =
                    collider.GetComponentInParent<ClientInteractionTargetMarker>();
                if (marker == null ||
                    marker.TargetKind != InteractionTargetKind.CombatTestTarget ||
                    marker.PrimaryId <= 0)
                {
                    continue;
                }

                float distance = _castHits[i].distance;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestMarker = marker;
                }
            }

            if (bestMarker == null)
                return;

            _latestLocalFixtureCandidate = bestMarker.gameObject;
            _latestLocalFixtureCandidateAt = Time.realtimeSinceStartupAsDouble;
        }

        private void OnDamageReceived(CombatDamageWire damage)
        {
            if (damage.amount <= 0 ||
                (damage.ResultCode != CombatDamageResultCode.Applied &&
                 damage.ResultCode != CombatDamageResultCode.Killed))
            {
                return;
            }

            GameObject visualRoot = ResolvePlayerImpactRoot(damage.target);
            if (visualRoot != null)
            {
                Pulse(visualRoot);
                return;
            }

            // A successful participant damage result with no Player target can currently only be
            // a non-networked combat fixture on this direct basic-attack path. Only the attacking
            // owner receives that event, so require the source to be this local Player before using
            // the attack-time presentation candidate.
            if (!IsLocalPlayerReference(damage.source) ||
                _latestLocalFixtureCandidate == null ||
                Time.realtimeSinceStartupAsDouble - _latestLocalFixtureCandidateAt > CandidateGraceSeconds)
            {
                return;
            }

            Pulse(_latestLocalFixtureCandidate);
        }

        private static GameObject ResolvePlayerImpactRoot(PlayerTargetReferenceWire target)
        {
            if (!target.IsValid)
                return null;

            IReadOnlyList<PlayerEntityClient> active = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < active.Count; ++i)
            {
                PlayerEntityClient client = active[i];
                if (client?.NetworkBridge == null ||
                    client.NetworkBridge.ObjectId != target.objectId ||
                    client.NetworkBridge.Generation != target.generation)
                {
                    continue;
                }

                Transform presentation = client.PresentationTransform;
                return presentation != null ? presentation.gameObject : client.gameObject;
            }

            return null;
        }

        private static bool IsLocalPlayerReference(PlayerTargetReferenceWire reference)
        {
            if (!reference.IsValid || !PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) ||
                owner?.NetworkBridge == null)
            {
                return false;
            }

            return owner.NetworkBridge.ObjectId == reference.objectId &&
                   owner.NetworkBridge.Generation == reference.generation;
        }

        private static Vector3 BuildAimDirection(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitch);
            return new Vector3(
                Mathf.Sin(yaw) * cosPitch,
                Mathf.Sin(pitch),
                Mathf.Cos(yaw) * cosPitch).normalized;
        }

        private static void Pulse(GameObject visualRoot)
        {
            if (visualRoot == null)
                return;

            ClientDamageFlashTarget flash = visualRoot.GetComponent<ClientDamageFlashTarget>();
            if (flash == null)
                flash = visualRoot.AddComponent<ClientDamageFlashTarget>();
            flash.Pulse();
        }

        private void ClearFixtureCandidate()
        {
            _latestLocalFixtureCandidate = null;
            _latestLocalFixtureCandidateAt = 0d;
        }
    }

    /// <summary>
    /// Short presentation-only red tint. Existing MaterialPropertyBlocks are restored exactly;
    /// no shared material is changed or cloned.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class ClientDamageFlashTarget : MonoBehaviour
    {
        private sealed class RendererState
        {
            public Renderer Renderer;
            public MaterialPropertyBlock Original;
            public bool HasBaseColor;
            public bool HasColor;
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly Color DamageFlashColor = new Color(1f, 0.045f, 0.045f, 1f);
        private const double FlashDurationSeconds = 0.10d;

        private readonly List<RendererState> _states = new List<RendererState>(8);
        private double _restoreAt;
        private bool _active;

        public void Pulse()
        {
            if (!_active)
            {
                CaptureAndApply();
                if (_states.Count == 0)
                    return;
                _active = true;
            }

            _restoreAt = Time.realtimeSinceStartupAsDouble + FlashDurationSeconds;
        }

        private void Update()
        {
            if (_active && Time.realtimeSinceStartupAsDouble >= _restoreAt)
                Restore();
        }

        private void OnDisable() => Restore();

        private void CaptureAndApply()
        {
            _states.Clear();
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                if (renderer == null ||
                    (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)))
                {
                    continue;
                }

                bool hasBaseColor = false;
                bool hasColor = false;
                Material[] materials = renderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; ++materialIndex)
                {
                    Material material = materials[materialIndex];
                    if (material == null)
                        continue;
                    hasBaseColor |= material.HasProperty(BaseColorId);
                    hasColor |= material.HasProperty(ColorId);
                }

                if (!hasBaseColor && !hasColor)
                    continue;

                var original = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(original);

                var flashed = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(flashed);
                if (hasBaseColor) flashed.SetColor(BaseColorId, DamageFlashColor);
                if (hasColor) flashed.SetColor(ColorId, DamageFlashColor);
                renderer.SetPropertyBlock(flashed);

                _states.Add(new RendererState
                {
                    Renderer = renderer,
                    Original = original,
                    HasBaseColor = hasBaseColor,
                    HasColor = hasColor,
                });
            }
        }

        private void Restore()
        {
            if (!_active && _states.Count == 0)
                return;

            for (int i = 0; i < _states.Count; ++i)
            {
                RendererState state = _states[i];
                if (state?.Renderer != null)
                    state.Renderer.SetPropertyBlock(state.Original);
            }

            _states.Clear();
            _active = false;
            _restoreAt = 0d;
        }
    }

    internal static class ClientDamageImpactFeedbackBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if UNITY_SERVER
            return;
#else
            if (Object.FindFirstObjectByType<ClientDamageImpactFeedback>() != null)
                return;

            var go = new GameObject("[Client Damage Impact Feedback]");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ClientDamageImpactFeedback>();
#endif
        }
    }
}
