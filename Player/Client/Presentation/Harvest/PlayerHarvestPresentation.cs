using System;
using Player.Networking;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Owner Harvest event relay.
    ///
    /// Server-authoritative Started/terminal HarvestEvent messages still drive the progress UI
    /// and movement-release hook. Actual humanoid animation now uses the existing replicated
    /// PlayerEntitySnapshot actionState/actionId path so owner and nearby observers share one
    /// presentation resolver without duplicate owner triggers.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(9500)]
    public sealed class PlayerHarvestPresentation : MonoBehaviour
    {
        public static event Action<PlayerHarvestEventMessage> HarvestEventPresented;

        private PlayerEntityGameManager _manager;

        private void OnEnable() => BindManager();

        private void Update()
        {
            if (_manager == null)
                BindManager();
        }

        private void OnDisable() => UnbindManager();

        private void BindManager()
        {
            PlayerEntityGameManager next =
                FindFirstObjectByType<PlayerEntityGameManager>();

            if (ReferenceEquals(next, _manager))
                return;

            UnbindManager();
            _manager = next;
            if (_manager != null)
                _manager.HarvestEventReceived += OnHarvestEvent;
        }

        private void UnbindManager()
        {
            if (_manager != null)
                _manager.HarvestEventReceived -= OnHarvestEvent;
            _manager = null;
        }

        private static void OnHarvestEvent(PlayerHarvestEventMessage message)
        {
            HarvestEventPresented?.Invoke(message);
        }
    }

    internal static class PlayerHarvestPresentationBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (UnityEngine.Object.FindFirstObjectByType<PlayerHarvestPresentation>(
                    FindObjectsInactive.Include) != null)
            {
                return;
            }

            var go = new GameObject("Client Harvest Presentation");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PlayerHarvestPresentation>();
        }
    }
}
