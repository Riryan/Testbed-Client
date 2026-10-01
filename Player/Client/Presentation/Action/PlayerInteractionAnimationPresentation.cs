using System;
using System.Collections.Generic;
using Game.Shared.Interactions;
using Player.Networking;
using Player.Shared;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Canonical client presentation consumer for PlayerEntity interaction action state.
    ///
    /// It subscribes to the EXISTING PlayerEntityNetwork SnapshotChanged event and consumes
    /// actionState/actionId only. No network message, polling, or presentation asset name is
    /// added to the wire.
    ///
    /// The existing PlayerHumanoid controller stays canonical. This component only asks that
    /// controller to enter authored states/triggers selected by the local presentation catalog.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerInteractionAnimationPresentation : MonoBehaviour
    {
        private const string ResourcesPath = "MMO/Interactions/InteractionAnimationCatalog";

        private static InteractionAnimationCatalog _catalog;
        private static bool _catalogLoaded;
        private static readonly HashSet<byte> MissingPresentationWarnings = new HashSet<byte>();

        private PlayerEntityClient _client;
        private PlayerEntityNetwork _network;
        private Animator _animator;
        private Transform _boundVisual;
        private byte _activePresentationId;
        private InteractionAnimationCatalog.Binding _activeBinding;

        private void Awake()
        {
            _client = GetComponent<PlayerEntityClient>();
            _network = GetComponent<PlayerEntityNetwork>();
        }

        private void OnEnable()
        {
            if (_client == null) _client = GetComponent<PlayerEntityClient>();
            if (_network == null) _network = GetComponent<PlayerEntityNetwork>();

            if (_network != null)
            {
                _network.SnapshotChanged += OnSnapshotChanged;
                PlayerEntitySnapshot snapshot = _network.Snapshot;
                if (snapshot.actionState == (byte)PlayerEntityActionState.Interacting)
                    Present(snapshot.actionId);
            }
        }

        private void OnDisable()
        {
            if (_network != null)
                _network.SnapshotChanged -= OnSnapshotChanged;
            StopPresentation();
        }

        private void OnSnapshotChanged(
            bool initial,
            PlayerEntitySnapshot oldValue,
            PlayerEntitySnapshot value)
        {
            bool oldInteraction =
                oldValue.actionState == (byte)PlayerEntityActionState.Interacting &&
                oldValue.actionId != InteractionPresentationWire.Stop;

            if (value.actionState == (byte)PlayerEntityActionState.Interacting)
            {
                if (value.actionId == InteractionPresentationWire.Stop)
                {
                    StopPresentation();
                    return;
                }

                bool samePresentation =
                    !initial &&
                    oldValue.actionState == value.actionState &&
                    oldValue.actionId == value.actionId;

                if (!samePresentation)
                    Present(value.actionId);
                return;
            }

            // Interaction presentation is persistent on the GameServer while active.
            // If an explicit stop pulse is lost, the next ordinary snapshot transitions
            // Interacting -> None and closes the presentation here.
            if (oldInteraction &&
                value.actionState == (byte)PlayerEntityActionState.None)
            {
                StopPresentation();
            }
        }

        public void Present(byte presentationId)
        {
            if (presentationId == InteractionPresentationWire.Stop)
            {
                StopPresentation();
                return;
            }

            if (!ResolveAnimator())
                return;

            InteractionAnimationCatalog.Binding binding =
                ResolveBinding(presentationId);

            bool played = TryPlayState(
                _animator,
                binding.layerName,
                binding.startState,
                binding.transitionSeconds);

            if (!played)
                played = TrySetTrigger(_animator, binding.startTrigger);

            // Harvest variants deliberately fall back to the established generic Harvest
            // trigger if no exact local mapping exists.
            if (!played && InteractionPresentationWire.IsHarvest(presentationId))
                played = TrySetTrigger(_animator, "Harvest");

            if (!played)
            {
                if (MissingPresentationWarnings.Add(presentationId))
                {
                    Debug.LogWarning(
                        $"[Interaction Animation] No authored Animator state/trigger resolved for " +
                        $"{InteractionPresentationWire.DebugLabel(presentationId)} ({presentationId}). " +
                        $"Edit Resources/MMO/Interactions/InteractionAnimationCatalog.");
                }
                return;
            }

            _activePresentationId = presentationId;
            _activeBinding = binding;
        }

        public void StopPresentation()
        {
            if (_activePresentationId == 0)
                return;

            ResolveAnimator();
            if (_animator != null)
            {
                bool stopped = false;
                if (_activeBinding != null)
                {
                    stopped = TryPlayState(
                        _animator,
                        _activeBinding.layerName,
                        _activeBinding.stopState,
                        _activeBinding.transitionSeconds);

                    if (!stopped)
                        stopped = TrySetTrigger(_animator, _activeBinding.stopTrigger);
                }

                if (!stopped &&
                    InteractionPresentationWire.IsHarvest(_activePresentationId))
                {
                    TrySetTrigger(_animator, "HarvestCancel");
                }
                else if (!stopped)
                {
                    TrySetTrigger(_animator, "InteractionCancel");
                }
            }

            _activePresentationId = 0;
            _activeBinding = null;
        }

        private bool ResolveAnimator()
        {
            Transform visual = _client != null ? _client.PresentationTransform : null;
            if (visual == null)
            {
                _boundVisual = null;
                _animator = null;
                return false;
            }

            if (_boundVisual == visual && _animator != null)
                return true;

            _boundVisual = visual;
            Animator[] animators = visual.GetComponentsInChildren<Animator>(true);
            _animator = null;
            for (int i = 0; i < animators.Length; ++i)
            {
                Animator candidate = animators[i];
                if (candidate == null || candidate.runtimeAnimatorController == null)
                    continue;
                _animator = candidate;
                break;
            }

            return _animator != null;
        }

        private static InteractionAnimationCatalog.Binding ResolveBinding(byte presentationId)
        {
            if (!_catalogLoaded)
            {
                _catalogLoaded = true;
                _catalog = Resources.Load<InteractionAnimationCatalog>(ResourcesPath);
            }

            if (_catalog != null && _catalog.TryGet(presentationId, out InteractionAnimationCatalog.Binding binding))
                return binding;

            return InteractionAnimationCatalog.CreateRuntimeFallback(presentationId);
        }

        private static bool TryPlayState(
            Animator animator,
            string layerName,
            string stateName,
            float transitionSeconds)
        {
            if (animator == null || string.IsNullOrWhiteSpace(stateName))
                return false;

            int layer = ResolveLayer(animator, layerName);
            if (layer < 0)
                return false;

            int shortHash = Animator.StringToHash(stateName);
            if (animator.HasState(layer, shortHash))
            {
                animator.CrossFadeInFixedTime(
                    shortHash,
                    Mathf.Max(0f, transitionSeconds),
                    layer,
                    0f);
                return true;
            }

            string layerActualName = animator.GetLayerName(layer);
            int fullHash = Animator.StringToHash(layerActualName + "." + stateName);
            if (!animator.HasState(layer, fullHash))
                return false;

            animator.CrossFadeInFixedTime(
                fullHash,
                Mathf.Max(0f, transitionSeconds),
                layer,
                0f);
            return true;
        }

        private static int ResolveLayer(Animator animator, string layerName)
        {
            if (animator == null || animator.layerCount <= 0)
                return -1;

            if (string.IsNullOrWhiteSpace(layerName))
                return 0;

            int layer = animator.GetLayerIndex(layerName);
            return layer >= 0 ? layer : -1;
        }

        private static bool TrySetTrigger(Animator animator, string triggerName)
        {
            if (animator == null || string.IsNullOrWhiteSpace(triggerName))
                return false;

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; ++i)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter.type != AnimatorControllerParameterType.Trigger ||
                    !string.Equals(parameter.name, triggerName, StringComparison.Ordinal))
                {
                    continue;
                }

                animator.SetTrigger(parameter.nameHash);
                return true;
            }

            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _catalog = null;
            _catalogLoaded = false;
            MissingPresentationWarnings.Clear();
        }
    }

    /// <summary>
    /// Event-driven installer. PlayerEntityClient already maintains a canonical active-client
    /// registry, so this adds no scene polling or per-frame entity scan.
    /// </summary>
    internal sealed class PlayerInteractionAnimationInstaller : MonoBehaviour
    {
        private void OnEnable()
        {
            PlayerEntityClient.ActiveClientRegistered += Attach;

            IReadOnlyList<PlayerEntityClient> active = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < active.Count; ++i)
                Attach(active[i]);
        }

        private void OnDisable()
        {
            PlayerEntityClient.ActiveClientRegistered -= Attach;
        }

        private static void Attach(PlayerEntityClient client)
        {
            if (client == null ||
                client.GetComponent<PlayerInteractionAnimationPresentation>() != null)
            {
                return;
            }

            client.gameObject.AddComponent<PlayerInteractionAnimationPresentation>();
        }
    }

    internal static class PlayerInteractionAnimationBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (UnityEngine.Object.FindFirstObjectByType<PlayerInteractionAnimationInstaller>(
                    FindObjectsInactive.Include) != null)
            {
                return;
            }

            var go = new GameObject("Player Interaction Animation Presentation");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PlayerInteractionAnimationInstaller>();
        }
    }
}
