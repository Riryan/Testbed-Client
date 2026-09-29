using System;
using Player.Networking;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only harvesting feedback. The server owns timing/outcomes; this component only
    /// maps compact HarvestEvent messages onto optional Animator triggers and local UI hooks.
    /// Missing Animator parameters are a supported no-op so no controller migration is required.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(9500)]
    public sealed class PlayerHarvestPresentation : MonoBehaviour
    {
        public static event Action<PlayerHarvestEventMessage> HarvestEventPresented;

        [SerializeField] private string startedTrigger = "Harvest";
        [SerializeField] private string succeededTrigger = "HarvestComplete";
        [SerializeField] private string failedTrigger = "HarvestFail";
        [SerializeField] private string cancelledTrigger = "HarvestCancel";

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
            PlayerEntityGameManager next = FindFirstObjectByType<PlayerEntityGameManager>();
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

        private void OnHarvestEvent(PlayerHarvestEventMessage message)
        {
            HarvestEventPresented?.Invoke(message);
            if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) || owner.PresentationTransform == null)
                return;

            Animator animator = owner.PresentationTransform.GetComponentInChildren<Animator>(true);
            if (animator == null)
                return;

            string trigger = message.Phase switch
            {
                PlayerHarvestEventPhase.Started => startedTrigger,
                PlayerHarvestEventPhase.Succeeded => succeededTrigger,
                PlayerHarvestEventPhase.Failed => failedTrigger,
                PlayerHarvestEventPhase.Cancelled => cancelledTrigger,
                _ => string.Empty,
            };
            TrySetTrigger(animator, trigger);
        }

        private static void TrySetTrigger(Animator animator, string parameterName)
        {
            if (animator == null || string.IsNullOrWhiteSpace(parameterName))
                return;
            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; ++i)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter.type == AnimatorControllerParameterType.Trigger &&
                    string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                {
                    animator.SetTrigger(parameter.nameHash);
                    return;
                }
            }
        }
    }

    internal static class PlayerHarvestPresentationBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (UnityEngine.Object.FindFirstObjectByType<PlayerHarvestPresentation>() != null)
                return;
            var go = new GameObject("Client Harvest Presentation");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PlayerHarvestPresentation>();
        }
    }
}
