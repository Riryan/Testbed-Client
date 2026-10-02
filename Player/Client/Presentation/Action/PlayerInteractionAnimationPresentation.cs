using System;
using System.Collections;
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
    /// PlayerHumanoid's authored CP_ActionActive / CP_ActionId / CP_ActionPhase contract is
    /// preferred for semantic interactions that are already present in that controller.
    /// The existing InteractionAnimationCatalog remains the fallback for other presentation ids.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerInteractionAnimationPresentation : MonoBehaviour
    {
        private const string ResourcesPath = "MMO/Interactions/InteractionAnimationCatalog";

        private static InteractionAnimationCatalog _catalog;
        private static bool _catalogLoaded;
        private static readonly HashSet<byte> MissingPresentationWarnings = new HashSet<byte>();
        private static readonly HashSet<byte> MissingCanonicalContractWarnings = new HashSet<byte>();

        private PlayerEntityClient _client;
        private PlayerEntityNetwork _network;
        private Animator _animator;
        private Transform _boundVisual;
        private byte _activePresentationId;
        private InteractionAnimationCatalog.Binding _activeBinding;

        private bool _canonicalParameterLatched;
        private int _canonicalLayerIndex = -1;
        private float _canonicalPreviousLayerWeight;
        private Coroutine _canonicalLoopRoutine;
        private Coroutine _canonicalClearRoutine;

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

            ForceClearPresentation();
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

        /// <summary>
        /// Presentation-id entry point used by normal replicated PlayerEntity interaction state.
        /// </summary>
        public void Present(byte presentationId)
        {
            if (presentationId == InteractionPresentationWire.Stop)
            {
                StopPresentation();
                return;
            }

            if (!ResolveAnimator())
                return;

            if (CanonicalInteractionAnimatorDriver.TryResolvePresentation(
                    presentationId,
                    out int controllerActionId))
            {
                PresentCanonicalControllerAction(
                    presentationId,
                    controllerActionId);
                return;
            }

            PresentCatalogFallback(presentationId);
        }

        /// <summary>
        /// Action-aware entry point for the local placeable Interaction Test Dummy.
        /// This avoids throwing away the accepted ContextInteraction action id before
        /// local test presentation is selected.
        /// </summary>
        public void PresentAction(
            InteractionActionId actionId,
            bool receiver)
        {
            if (!ResolveAnimator())
                return;

            byte presentationId =
                InteractionPresentationWire.EncodeInteraction(
                    actionId,
                    receiver);

            if (CanonicalInteractionAnimatorDriver.TryResolveAction(
                    actionId,
                    receiver,
                    out int controllerActionId))
            {
                PresentCanonicalControllerAction(
                    presentationId,
                    controllerActionId);
                return;
            }

            Present(presentationId);
        }

        public void StopPresentation()
        {
            if (_activePresentationId == 0 && !_canonicalParameterLatched)
                return;

            ResolveAnimator();

            if (_canonicalParameterLatched)
            {
                CancelCanonicalLoopRoutine();

                if (_animator != null)
                {
                    CanonicalInteractionAnimatorDriver.SetPhase(
                        _animator,
                        CanonicalInteractionAnimatorDriver.FinishPhase);
                }

                _activePresentationId = 0;
                _activeBinding = null;

                if (_canonicalClearRoutine != null)
                    StopCoroutine(_canonicalClearRoutine);

                _canonicalClearRoutine =
                    StartCoroutine(ClearCanonicalAfterFinish());
                return;
            }

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
                        stopped = TrySetTrigger(
                            _animator,
                            _activeBinding.stopTrigger);
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

        private void PresentCanonicalControllerAction(
            byte presentationId,
            int controllerActionId)
        {
            ForceClearPresentation();

            if (!ResolveAnimator())
                return;

            if (!CanonicalInteractionAnimatorDriver.Begin(
                    _animator,
                    controllerActionId,
                    out _canonicalLayerIndex,
                    out _canonicalPreviousLayerWeight,
                    out string failure))
            {
                if (MissingCanonicalContractWarnings.Add(presentationId))
                {
                    Debug.LogWarning(
                        $"[Interaction Animation] " +
                        $"{InteractionPresentationWire.DebugLabel(presentationId)} " +
                        $"resolved to PlayerHumanoid action {controllerActionId}, but {failure}.",
                        this);
                }

                PresentCatalogFallback(presentationId);
                return;
            }

            _canonicalParameterLatched = true;
            _activePresentationId = presentationId;
            _activeBinding = null;
            _canonicalLoopRoutine =
                StartCoroutine(AdvanceCanonicalToLoop(
                    presentationId));
        }

        private IEnumerator AdvanceCanonicalToLoop(
            byte expectedPresentationId)
        {
            yield return new WaitForSecondsRealtime(
                CanonicalInteractionAnimatorDriver.StartPhaseHoldSeconds);

            _canonicalLoopRoutine = null;

            if (!_canonicalParameterLatched ||
                _activePresentationId != expectedPresentationId ||
                _animator == null)
            {
                yield break;
            }

            CanonicalInteractionAnimatorDriver.SetPhase(
                _animator,
                CanonicalInteractionAnimatorDriver.LoopPhase);
        }

        private IEnumerator ClearCanonicalAfterFinish()
        {
            yield return new WaitForSecondsRealtime(
                CanonicalInteractionAnimatorDriver.FinishPhaseHoldSeconds);

            _canonicalClearRoutine = null;

            if (_animator != null)
            {
                CanonicalInteractionAnimatorDriver.Clear(
                    _animator,
                    _canonicalLayerIndex,
                    _canonicalPreviousLayerWeight);
            }

            _canonicalLayerIndex = -1;
            _canonicalPreviousLayerWeight = 0f;
            _canonicalParameterLatched = false;
        }

        private void PresentCatalogFallback(byte presentationId)
        {
            if (!ResolveAnimator())
                return;

            ForceClearPresentation();

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
                played = TrySetTrigger(
                    _animator,
                    binding.startTrigger);

            // Harvest variants deliberately fall back to the established generic Harvest
            // trigger if no exact local mapping exists.
            if (!played &&
                InteractionPresentationWire.IsHarvest(presentationId))
            {
                played = TrySetTrigger(
                    _animator,
                    "Harvest");
            }

            if (!played)
            {
                if (MissingPresentationWarnings.Add(presentationId))
                {
                    Debug.LogWarning(
                        $"[Interaction Animation] No authored Animator state/trigger resolved for " +
                        $"{InteractionPresentationWire.DebugLabel(presentationId)} ({presentationId}). " +
                        $"PlayerHumanoid parameter mapping is not defined for this semantic id and " +
                        $"the existing InteractionAnimationCatalog fallback also did not resolve.",
                        this);
                }
                return;
            }

            _activePresentationId = presentationId;
            _activeBinding = binding;
        }

        private void ForceClearPresentation()
        {
            CancelCanonicalLoopRoutine();

            if (_canonicalClearRoutine != null)
            {
                StopCoroutine(_canonicalClearRoutine);
                _canonicalClearRoutine = null;
            }

            ResolveAnimator();

            if (_canonicalParameterLatched &&
                _animator != null)
            {
                CanonicalInteractionAnimatorDriver.Clear(
                    _animator,
                    _canonicalLayerIndex,
                    _canonicalPreviousLayerWeight);
            }

            _canonicalLayerIndex = -1;
            _canonicalPreviousLayerWeight = 0f;
            _canonicalParameterLatched = false;
            _activePresentationId = 0;
            _activeBinding = null;
        }

        private void CancelCanonicalLoopRoutine()
        {
            if (_canonicalLoopRoutine == null)
                return;

            StopCoroutine(_canonicalLoopRoutine);
            _canonicalLoopRoutine = null;
        }

        private bool ResolveAnimator()
        {
            Transform visual =
                _client != null
                    ? _client.PresentationTransform
                    : null;

            if (visual == null)
            {
                _boundVisual = null;
                _animator = null;
                return false;
            }

            if (_boundVisual == visual &&
                _animator != null)
            {
                return true;
            }

            _boundVisual = visual;
            Animator[] animators =
                visual.GetComponentsInChildren<Animator>(true);

            _animator = null;
            for (int i = 0; i < animators.Length; ++i)
            {
                Animator candidate = animators[i];
                if (candidate == null ||
                    candidate.runtimeAnimatorController == null)
                {
                    continue;
                }

                _animator = candidate;
                break;
            }

            return _animator != null;
        }

        private static InteractionAnimationCatalog.Binding ResolveBinding(
            byte presentationId)
        {
            if (!_catalogLoaded)
            {
                _catalogLoaded = true;
                _catalog =
                    Resources.Load<InteractionAnimationCatalog>(
                        ResourcesPath);
            }

            if (_catalog != null &&
                _catalog.TryGet(
                    presentationId,
                    out InteractionAnimationCatalog.Binding binding))
            {
                return binding;
            }

            return InteractionAnimationCatalog.CreateRuntimeFallback(
                presentationId);
        }

        private static bool TryPlayState(
            Animator animator,
            string layerName,
            string stateName,
            float transitionSeconds)
        {
            if (animator == null ||
                string.IsNullOrWhiteSpace(stateName))
            {
                return false;
            }

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
            int fullHash =
                Animator.StringToHash(
                    layerActualName + "." + stateName);

            if (!animator.HasState(layer, fullHash))
                return false;

            animator.CrossFadeInFixedTime(
                fullHash,
                Mathf.Max(0f, transitionSeconds),
                layer,
                0f);
            return true;
        }

        private static int ResolveLayer(
            Animator animator,
            string layerName)
        {
            if (animator == null ||
                animator.layerCount <= 0)
            {
                return -1;
            }

            if (string.IsNullOrWhiteSpace(layerName))
                return 0;

            int layer = animator.GetLayerIndex(layerName);
            return layer >= 0 ? layer : -1;
        }

        private static bool TrySetTrigger(
            Animator animator,
            string triggerName)
        {
            if (animator == null ||
                string.IsNullOrWhiteSpace(triggerName))
            {
                return false;
            }

            AnimatorControllerParameter[] parameters =
                animator.parameters;

            for (int i = 0; i < parameters.Length; ++i)
            {
                AnimatorControllerParameter parameter =
                    parameters[i];

                if (parameter.type != AnimatorControllerParameterType.Trigger ||
                    !string.Equals(
                        parameter.name,
                        triggerName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                animator.SetTrigger(parameter.nameHash);
                return true;
            }

            return false;
        }

        [RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _catalog = null;
            _catalogLoaded = false;
            MissingPresentationWarnings.Clear();
            MissingCanonicalContractWarnings.Clear();
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

            IReadOnlyList<PlayerEntityClient> active =
                PlayerEntityClient.ActiveClients;

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

            client.gameObject.AddComponent<
                PlayerInteractionAnimationPresentation>();
        }
    }

    internal static class PlayerInteractionAnimationBootstrap
    {
        [RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (UnityEngine.Object.FindFirstObjectByType<
                    PlayerInteractionAnimationInstaller>(
                    FindObjectsInactive.Include) != null)
            {
                return;
            }

            var go =
                new GameObject(
                    "Player Interaction Animation Presentation");

            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<
                PlayerInteractionAnimationInstaller>();
        }
    }
}
