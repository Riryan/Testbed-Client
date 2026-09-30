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
        [Serializable]
        private struct PresentationTriggerOverride
        {
            [Min(0)] public int presentationId;
            public string startedTrigger;
            public string succeededTrigger;
            public string failedTrigger;
            public string cancelledTrigger;

            public string GetTrigger(PlayerHarvestEventPhase phase)
            {
                return phase switch
                {
                    PlayerHarvestEventPhase.Started => startedTrigger,
                    PlayerHarvestEventPhase.Succeeded => succeededTrigger,
                    PlayerHarvestEventPhase.Failed => failedTrigger,
                    PlayerHarvestEventPhase.Cancelled => cancelledTrigger,
                    _ => string.Empty,
                };
            }
        }

        public static event Action<PlayerHarvestEventMessage> HarvestEventPresented;

        [Header("Fallback Animator Triggers")]
        [SerializeField] private string startedTrigger = "Harvest";
        [SerializeField] private string succeededTrigger = "HarvestComplete";
        [SerializeField] private string failedTrigger = "HarvestFail";
        [SerializeField] private string cancelledTrigger = "HarvestCancel";

        [Header("Harvest Type Overrides")]
        [Tooltip("Optional local presentation mapping. The existing server-supplied presentationId selects a trigger set; no extra network data is sent.")]
        [SerializeField] private PresentationTriggerOverride[] presentationTriggerOverrides = Array.Empty<PresentationTriggerOverride>();

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
            // UI and other local presentation consumers piggyback the same authoritative owner event.
            HarvestEventPresented?.Invoke(message);

            if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) || owner.PresentationTransform == null)
                return;

            Animator animator = owner.PresentationTransform.GetComponentInChildren<Animator>(true);
            if (animator == null)
                return;

            string trigger = ResolveTrigger(message);
            TrySetTrigger(animator, trigger);
        }

        private string ResolveTrigger(PlayerHarvestEventMessage message)
        {
            if (presentationTriggerOverrides != null)
            {
                for (int i = 0; i < presentationTriggerOverrides.Length; ++i)
                {
                    PresentationTriggerOverride entry = presentationTriggerOverrides[i];
                    if (entry.presentationId != message.presentationId)
                        continue;

                    string mapped = entry.GetTrigger(message.Phase);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        return mapped;
                    break;
                }
            }

            return message.Phase switch
            {
                PlayerHarvestEventPhase.Started => startedTrigger,
                PlayerHarvestEventPhase.Succeeded => succeededTrigger,
                PlayerHarvestEventPhase.Failed => failedTrigger,
                PlayerHarvestEventPhase.Cancelled => cancelledTrigger,
                _ => string.Empty,
            };
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
