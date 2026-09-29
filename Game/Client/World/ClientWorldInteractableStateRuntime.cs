using System.Collections.Generic;
using Game.Shared.World;
using Game.WorldAuthoring;
using Player.Client;
using Player.Networking;
using UnityEngine;

namespace Game.Client.World
{
    /// <summary>
    /// One client-side bridge for rare authoritative world-interactable state deltas.
    /// It updates visual props and the client prediction blocker cache. It owns no gameplay authority.
    /// </summary>
    public sealed class ClientWorldInteractableStateRuntime : MonoBehaviour
    {
        private static ClientWorldInteractableStateRuntime _instance;
        private static readonly Dictionary<long, WorldInteractableStateMessage> States = new Dictionary<long, WorldInteractableStateMessage>();
        private PlayerEntityGameManager _manager;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
#if UNITY_SERVER
            return;
#else
            if (_instance != null)
                return;
            var go = new GameObject("[Client World Interactable State]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ClientWorldInteractableStateRuntime>();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (_manager == null)
                Bind(UnityEngine.Object.FindFirstObjectByType<PlayerEntityGameManager>());
#endif
        }

        private void OnDestroy()
        {
            Bind(null);
            if (_instance == this)
                _instance = null;
        }

        private void Bind(PlayerEntityGameManager manager)
        {
            if (_manager == manager)
                return;
            if (_manager != null)
                _manager.WorldInteractableStateReceived -= OnStateReceived;
            _manager = manager;
            if (_manager != null)
                _manager.WorldInteractableStateReceived += OnStateReceived;
        }

        public static bool TryGetState(long stableId, out WorldInteractableStateMessage state)
        {
            if (stableId <= 0)
            {
                state = default;
                return false;
            }
            return States.TryGetValue(stableId, out state);
        }

        private static void OnStateReceived(WorldInteractableStateMessage state)
        {
            if (state.stableId <= 0)
                return;

            if (!States.TryGetValue(state.stableId, out WorldInteractableStateMessage current) || state.revision >= current.revision)
                States[state.stableId] = state;

            if (state.dynamicBlockerId > 0)
                SharedWorldClient.TrySetDynamicBlockerEnabled(
                    state.dynamicBlockerId,
                    state.blockerEnabled);

            if ((ServerWorldInteractableKind)state.kind == ServerWorldInteractableKind.Door ||
                (ServerWorldInteractableKind)state.kind == ServerWorldInteractableKind.Gate)
            {
                if (WorldDoorPresentation.TryGet(state.stableId, out WorldDoorPresentation door))
                    door.SetOpen(state.open);
            }
        }
    }
}
