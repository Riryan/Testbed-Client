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
    /// Canonical client presentation consumer for replicated PlayerEntity interaction state.
    /// Reuses the existing compact actionState/actionId fields. No extra presentation traffic.
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
        private Transform _boundVisual;
        private Animator _animator;

        private byte _activePresentationId;
        private InteractionAnimationCatalog.Binding _activeBinding;
        private int _catalogLayerIndex = -1;
        private float _catalogPreviousLayerWeight;

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
            if (_client == null)
                _client = GetComponent<PlayerEntityClient>();
            if (_network == null)
                _network = GetComponent<PlayerEntityNetwork>();

            if (_network == null)
                return;

            _network.SnapshotChanged += OnSnapshotChanged;
            PlayerEntitySnapshot snapshot = _network.Snapshot;
            if (snapshot.actionState == (byte)PlayerEntityActionState.Interacting &&
                snapshot.actionId != InteractionPresentationWire.Stop)
            {
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

            if (CanonicalInteractionAnimatorDriver.TryResolvePresentation(
                    presentationId,
                    out int controllerActionId))
            {
                PresentCanonicalControllerAction(presentationId, controllerActionId);
                return;
            }

            PresentCatalogFallback(presentationId);
        }

        public void PresentAction(
            InteractionActionId actionId,
            bool receiver)
        {
            if (!ResolveAnimator())
                return;

            byte presentationId =
                InteractionPresentationWire.EncodeInteraction(actionId, receiver);

            if (CanonicalInteractionAnimatorDriver.TryResolveAction(
                    actionId,
                    receiver,
                    out int controllerActionId))
            {
                PresentCanonicalControllerAction(presentationId, controllerActionId);
                return;
            }

            PresentCatalogFallback(presentationId);
        }

        public void StopPresentation()
        {
            if (_activePresentationId == 0 &&
                !_canonicalParameterLatched)
            {
                return;
            }

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
                StopCatalogFallback(
                    _animator,
                    _activePresentationId,
                    _activeBinding);
            }

            RestoreCatalogLayer(
                _animator,
                _catalogLayerIndex,
                _catalogPreviousLayerWeight);

            _catalogLayerIndex = -1;
            _catalogPreviousLayerWeight = 0f;
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
                        $"[Interaction Animation] {InteractionPresentationWire.DebugLabel(presentationId)} " +
                        $"resolved to PlayerHumanoid action {controllerActionId}, but {failure}. " +
                        "Falling back to the existing local interaction animation catalog.",
                        this);
                }

                PresentCatalogFallback(presentationId);
                return;
            }

            _canonicalParameterLatched = true;
            _activePresentationId = presentationId;
            _activeBinding = null;
            _canonicalLoopRoutine =
                StartCoroutine(AdvanceCanonicalToLoop(presentationId));
        }

        private IEnumerator AdvanceCanonicalToLoop(byte expectedPresentationId)
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

            if (!TryPlayCatalogFallback(
                    _animator,
                    presentationId,
                    out InteractionAnimationCatalog.Binding binding,
                    out int layerIndex,
                    out float previousLayerWeight))
            {
                if (MissingPresentationWarnings.Add(presentationId))
                {
                    Debug.LogWarning(
                        $"[Interaction Animation] No authored Animator state/trigger resolved for " +
                        $"{InteractionPresentationWire.DebugLabel(presentationId)} ({presentationId}).",
                        this);
                }
                return;
            }

            _activePresentationId = presentationId;
            _activeBinding = binding;
            _catalogLayerIndex = layerIndex;
            _catalogPreviousLayerWeight = previousLayerWeight;
        }

        /// <summary>
        /// Shared local-only fallback for normal players and development interaction receivers.
        /// A blank catalog layer means search the authored controller instead of assuming Base Layer.
        /// </summary>
        public static bool TryPlayCatalogFallback(
            Animator animator,
            byte presentationId,
            out InteractionAnimationCatalog.Binding binding,
            out int layerIndex,
            out float previousLayerWeight)
        {
            binding = ResolveBinding(presentationId);
            layerIndex = -1;
            previousLayerWeight = 0f;

            bool played = TryPlayState(
                animator,
                binding.layerName,
                binding.startState,
                binding.transitionSeconds,
                out layerIndex,
                out previousLayerWeight);

            if (!played)
            {
                layerIndex = -1;
                previousLayerWeight = 0f;
                played = TrySetTrigger(animator, binding.startTrigger);
            }

            if (!played &&
                InteractionPresentationWire.IsHarvest(presentationId))
            {
                played = TrySetTrigger(animator, "Harvest");
            }

            return played;
        }

        public static void StopCatalogFallback(
            Animator animator,
            byte presentationId,
            InteractionAnimationCatalog.Binding binding)
        {
            if (animator == null)
                return;

            bool stopped = false;
            if (binding != null)
            {
                stopped = TryPlayState(
                    animator,
                    binding.layerName,
                    binding.stopState,
                    binding.transitionSeconds,
                    out _,
                    out _);

                if (!stopped)
                    stopped = TrySetTrigger(animator, binding.stopTrigger);
            }

            if (!stopped &&
                InteractionPresentationWire.IsHarvest(presentationId))
            {
                TrySetTrigger(animator, "HarvestCancel");
            }
            else if (!stopped)
            {
                TrySetTrigger(animator, "InteractionCancel");
            }
        }

        public static void RestoreCatalogLayer(
            Animator animator,
            int layerIndex,
            float previousLayerWeight)
        {
            if (animator == null ||
                layerIndex <= 0 ||
                layerIndex >= animator.layerCount)
            {
                return;
            }

            animator.SetLayerWeight(
                layerIndex,
                Mathf.Clamp01(previousLayerWeight));
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

            RestoreCatalogLayer(
                _animator,
                _catalogLayerIndex,
                _catalogPreviousLayerWeight);

            _catalogLayerIndex = -1;
            _catalogPreviousLayerWeight = 0f;
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
            Transform visual = _client != null
                ? _client.PresentationTransform
                : null;

            if (visual == null)
            {
                _boundVisual = null;
                _animator = null;
                return false;
            }

            if (_boundVisual == visual &&
                _animator != null &&
                _animator.runtimeAnimatorController != null)
            {
                return true;
            }

            _boundVisual = visual;
            Animator[] animators = visual.GetComponentsInChildren<Animator>(true);
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

        private static InteractionAnimationCatalog.Binding ResolveBinding(byte presentationId)
        {
            if (!_catalogLoaded)
            {
                _catalogLoaded = true;
                _catalog = Resources.Load<InteractionAnimationCatalog>(ResourcesPath);
            }

            if (_catalog != null &&
                _catalog.TryGet(presentationId, out InteractionAnimationCatalog.Binding binding))
            {
                return binding;
            }

            return InteractionAnimationCatalog.CreateRuntimeFallback(presentationId);
        }

        private static bool TryPlayState(
            Animator animator,
            string layerName,
            string stateName,
            float transitionSeconds,
            out int playedLayerIndex,
            out float previousLayerWeight)
        {
            playedLayerIndex = -1;
            previousLayerWeight = 0f;

            if (animator == null ||
                string.IsNullOrWhiteSpace(stateName) ||
                animator.layerCount <= 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(layerName))
            {
                return TryPlayStateOnLayer(
                    animator,
                    animator.GetLayerIndex(layerName),
                    stateName,
                    transitionSeconds,
                    out playedLayerIndex,
                    out previousLayerWeight);
            }

            for (int layer = 0; layer < animator.layerCount; ++layer)
            {
                if (TryPlayStateOnLayer(
                        animator,
                        layer,
                        stateName,
                        transitionSeconds,
                        out playedLayerIndex,
                        out previousLayerWeight))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryPlayStateOnLayer(
            Animator animator,
            int layer,
            string stateName,
            float transitionSeconds,
            out int playedLayerIndex,
            out float previousLayerWeight)
        {
            playedLayerIndex = -1;
            previousLayerWeight = 0f;

            if (animator == null ||
                layer < 0 ||
                layer >= animator.layerCount ||
                string.IsNullOrWhiteSpace(stateName))
            {
                return false;
            }

            int shortHash = Animator.StringToHash(stateName);
            int stateHash = 0;

            if (animator.HasState(layer, shortHash))
            {
                stateHash = shortHash;
            }
            else
            {
                int fullHash = Animator.StringToHash(
                    animator.GetLayerName(layer) + "." + stateName);
                if (animator.HasState(layer, fullHash))
                    stateHash = fullHash;
            }

            if (stateHash == 0)
                return false;

            previousLayerWeight = animator.GetLayerWeight(layer);
            playedLayerIndex = layer;

            if (layer > 0 && previousLayerWeight < 0.999f)
                animator.SetLayerWeight(layer, 1f);

            animator.CrossFadeInFixedTime(
                stateHash,
                Mathf.Max(0f, transitionSeconds),
                layer,
                0f);
            return true;
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
            MissingCanonicalContractWarnings.Clear();
        }
    }

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

            GameObject go = new GameObject("Player Interaction Animation Presentation");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<PlayerInteractionAnimationInstaller>();
        }
    }
}
