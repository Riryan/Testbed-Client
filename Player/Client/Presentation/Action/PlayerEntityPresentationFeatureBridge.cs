using Player.Shared;
using Player.Client;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Binds additive client presentation features to PlayerEntity presentations without
    /// changing PlayerEntity networking or server authority. It survives replacement of the
    /// visual root by the appearance system and rebinds IK/ragdoll to the current humanoid.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(9000)]
    public sealed class PlayerEntityPresentationFeatureBridge : MonoBehaviour
    {
        private PlayerEntityClient _client;
        private Transform _boundVisual;
        private HumanoidIkPresentationController _ik;
        private HumanoidRagdollPresentationController _ragdoll;
        private PlayerEntityCameraStatePresentation _cameraStates;
        private PlayerEntityRecoilSwayPresentation _recoilSway;

        public HumanoidIkPresentationController Ik => _ik;
        public HumanoidRagdollPresentationController Ragdoll => _ragdoll;
        public PlayerEntityCameraStatePresentation CameraStates => _cameraStates;
        public PlayerEntityRecoilSwayPresentation RecoilSway => _recoilSway;

        private void Awake()
        {
            _client = GetComponent<PlayerEntityClient>();
        }

        private void Update()
        {
            if (_client == null)
                return;

            EnsureOwnerModules();
            EnsureVisualModules();
            ApplyAuthoritativePresentationState();
        }

        private void EnsureOwnerModules()
        {
            if (!_client.IsOwnerClient)
                return;

            if (_cameraStates == null)
            {
                _cameraStates = GetComponent<PlayerEntityCameraStatePresentation>();
                if (_cameraStates == null)
                    _cameraStates = gameObject.AddComponent<PlayerEntityCameraStatePresentation>();
            }

            if (_recoilSway == null)
            {
                _recoilSway = GetComponent<PlayerEntityRecoilSwayPresentation>();
                if (_recoilSway == null)
                    _recoilSway = gameObject.AddComponent<PlayerEntityRecoilSwayPresentation>();
            }
        }

        private void EnsureVisualModules()
        {
            Transform visual = _client.PresentationTransform;
            if (visual == _boundVisual)
                return;

            _boundVisual = visual;
            _ik = null;
            _ragdoll = null;

            if (_boundVisual == null)
                return;

            Animator animator = _boundVisual.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                _ik = animator.GetComponent<HumanoidIkPresentationController>();
                if (_ik == null)
                    _ik = animator.gameObject.AddComponent<HumanoidIkPresentationController>();
            }

            _ragdoll = _boundVisual.GetComponent<HumanoidRagdollPresentationController>();
            if (_ragdoll == null)
                _ragdoll = _boundVisual.gameObject.AddComponent<HumanoidRagdollPresentationController>();
        }

        private void ApplyAuthoritativePresentationState()
        {
            if (_client.NetworkBridge == null)
                return;

            PlayerEntitySnapshot snapshot = _client.NetworkBridge.Snapshot;
            bool dead =
                (PlayerEntityMoveState)snapshot.moveState == PlayerEntityMoveState.Dead ||
                (PlayerEntityActionState)snapshot.actionState == PlayerEntityActionState.Dead;

            if (_ragdoll != null && _ragdoll.autoFollowAuthoritativeDeath)
                _ragdoll.SetRagdollActive(dead);
        }
    }

    /// <summary>
    /// Runtime installer keeps this feature bundle additive. No prefab migration is required
    /// for the first integration gate; explicit prefab authoring can replace this bootstrap later.
    /// </summary>
    public static class PlayerEntityPresentationFeatureBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            PlayerEntityClient.ActiveClientRegistered -= OnClientRegistered;
            PlayerEntityClient.ActiveClientRegistered += OnClientRegistered;

            var active = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < active.Count; ++i)
                Attach(active[i]);
        }

        private static void OnClientRegistered(PlayerEntityClient client)
        {
            Attach(client);
        }

        private static void Attach(PlayerEntityClient client)
        {
            if (client == null)
                return;

            PlayerEntityPresentationFeatureBridge bridge = client.GetComponent<PlayerEntityPresentationFeatureBridge>();
            if (bridge == null)
                client.gameObject.AddComponent<PlayerEntityPresentationFeatureBridge>();
        }
    }
}
