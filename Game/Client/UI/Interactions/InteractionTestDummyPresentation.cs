using System;
using System.Collections;
using Game.Shared.Interactions;
using Game.WorldAuthoring;
using Player.Client;
using Player.Client.Presentation;
using Player.Networking;
using UnityEngine;

namespace Game.Client.UI.Interactions
{
    /// <summary>
    /// Development-only presentation companion for the placeable Interaction Test Dummy.
    ///
    /// The normal ContextInteraction request/response and server WorldInteractable session remain
    /// authoritative. This component only reacts AFTER an existing successful authoritative response
    /// for this baked SceneObject and locally drives the existing semantic interaction presentation
    /// IDs so one client can inspect both the initiator and receiver animations.
    ///
    /// No new network message, request type, polling stream, or authoritative state is introduced.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionTestDummyPresentation : MonoBehaviour
    {
        private const string CatalogResourcesPath = "MMO/Interactions/InteractionAnimationCatalog";

        [SerializeField] private WorldObject worldObject;
        [SerializeField] private Animator receiverAnimator;

        private PlayerEntityGameManager _manager;
        private PlayerInteractionAnimationPresentation _ownerPresentation;
        private InteractionAnimationCatalog _catalog;
        private bool _catalogLoaded;

        private byte _receiverPresentationId;
        private InteractionAnimationCatalog.Binding _receiverBinding;
        private uint _presentationVersion;
        private Coroutine _stopRoutine;

        private void Awake()
        {
            ResolveFixtureReferences();
        }

        private void OnEnable()
        {
            ResolveFixtureReferences();
            BindManager();
        }

        private void Start()
        {
            // NetworkManagerMMO / PlayerEntityGameManager is expected to be active in client scenes.
            // Start gives the normal scene bootstrap one more lifecycle point without adding Update polling.
            BindManager();
        }

        private void OnDisable()
        {
            UnbindManager();
            StopCurrentPresentation();
        }

        private void ResolveFixtureReferences()
        {
            if (worldObject == null)
                worldObject = GetComponent<WorldObject>();

            if (receiverAnimator != null && receiverAnimator.runtimeAnimatorController != null)
                return;

            Animator[] animators = GetComponentsInChildren<Animator>(true);
            receiverAnimator = null;
            for (int i = 0; i < animators.Length; ++i)
            {
                Animator candidate = animators[i];
                if (candidate == null || candidate.runtimeAnimatorController == null)
                    continue;
                receiverAnimator = candidate;
                break;
            }
        }

        private void BindManager()
        {
            if (_manager != null)
                return;

            _manager = FindFirstObjectByType<PlayerEntityGameManager>(FindObjectsInactive.Include);
            if (_manager != null)
                _manager.ContextInteractionResultReceived += OnContextInteractionResult;
        }

        private void UnbindManager()
        {
            if (_manager != null)
                _manager.ContextInteractionResultReceived -= OnContextInteractionResult;
            _manager = null;
        }

        private void OnContextInteractionResult(ContextInteractionResponseMessage message)
        {
            if (!message.success ||
                message.ResultCode != InteractionResultCode.Success ||
                message.target.Kind != InteractionTargetKind.SceneObject ||
                worldObject == null ||
                worldObject.StableId <= 0 ||
                message.target.primaryId != worldObject.StableId)
            {
                return;
            }

            PlayTestPresentation(message.ActionId);
        }

        private void PlayTestPresentation(InteractionActionId actionId)
        {
            unchecked
            {
                _presentationVersion++;
                if (_presentationVersion == 0)
                    _presentationVersion = 1;
            }

            uint version = _presentationVersion;
            StopCurrentPresentation(clearVersion: false);

            byte initiatorPresentation =
                InteractionPresentationWire.EncodeInteraction(actionId, receiver: false);
            byte receiverPresentation =
                InteractionPresentationWire.EncodeInteraction(actionId, receiver: true);

            _ownerPresentation = ResolveOwnerPresentation();
            if (_ownerPresentation != null)
                _ownerPresentation.Present(initiatorPresentation);
            else
                Debug.LogWarning(
                    "[Interaction Test Dummy] Local PlayerInteractionAnimationPresentation was not found; " +
                    "receiver animation will still be attempted.",
                    this);

            PresentReceiver(receiverPresentation);

            float duration = (float)Math.Max(
                0.1d,
                InteractionPresentationWire.DevelopmentDurationSeconds(actionId));

            _stopRoutine = StartCoroutine(StopAfter(version, duration));
        }

        private PlayerInteractionAnimationPresentation ResolveOwnerPresentation()
        {
            if (PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) && owner != null)
            {
                PlayerInteractionAnimationPresentation presentation =
                    owner.GetComponent<PlayerInteractionAnimationPresentation>();
                if (presentation == null)
                    presentation = owner.GetComponentInChildren<PlayerInteractionAnimationPresentation>(true);
                return presentation;
            }

            return null;
        }

        private IEnumerator StopAfter(uint expectedVersion, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);

            if (_presentationVersion == expectedVersion)
                StopCurrentPresentation();

            _stopRoutine = null;
        }

        private void StopCurrentPresentation(bool clearVersion = true)
        {
            if (_stopRoutine != null)
            {
                StopCoroutine(_stopRoutine);
                _stopRoutine = null;
            }

            if (_ownerPresentation != null)
                _ownerPresentation.StopPresentation();
            _ownerPresentation = null;

            StopReceiver();

            if (clearVersion)
            {
                unchecked
                {
                    _presentationVersion++;
                    if (_presentationVersion == 0)
                        _presentationVersion = 1;
                }
            }
        }

        private void PresentReceiver(byte presentationId)
        {
            ResolveFixtureReferences();
            if (receiverAnimator == null)
            {
                Debug.LogWarning(
                    "[Interaction Test Dummy] No receiver Animator with a RuntimeAnimatorController was found.",
                    this);
                return;
            }

            InteractionAnimationCatalog.Binding binding = ResolveBinding(presentationId);
            bool played = TryPlayState(
                receiverAnimator,
                binding.layerName,
                binding.startState,
                binding.transitionSeconds);

            if (!played)
                played = TrySetTrigger(receiverAnimator, binding.startTrigger);

            if (!played)
            {
                Debug.LogWarning(
                    $"[Interaction Test Dummy] Receiver presentation '{InteractionPresentationWire.DebugLabel(presentationId)}' " +
                    "has no matching Animator state/trigger. Update the existing InteractionAnimationCatalog.",
                    this);
                return;
            }

            _receiverPresentationId = presentationId;
            _receiverBinding = binding;
        }

        private void StopReceiver()
        {
            if (_receiverPresentationId == InteractionPresentationWire.Stop ||
                receiverAnimator == null)
            {
                _receiverPresentationId = InteractionPresentationWire.Stop;
                _receiverBinding = null;
                return;
            }

            bool stopped = false;
            if (_receiverBinding != null)
            {
                stopped = TryPlayState(
                    receiverAnimator,
                    _receiverBinding.layerName,
                    _receiverBinding.stopState,
                    _receiverBinding.transitionSeconds);

                if (!stopped)
                    stopped = TrySetTrigger(receiverAnimator, _receiverBinding.stopTrigger);
            }

            if (!stopped)
                TrySetTrigger(receiverAnimator, "InteractionCancel");

            _receiverPresentationId = InteractionPresentationWire.Stop;
            _receiverBinding = null;
        }

        private InteractionAnimationCatalog.Binding ResolveBinding(byte presentationId)
        {
            if (!_catalogLoaded)
            {
                _catalogLoaded = true;
                _catalog = Resources.Load<InteractionAnimationCatalog>(CatalogResourcesPath);
            }

            if (_catalog != null &&
                _catalog.TryGet(
                    presentationId,
                    out InteractionAnimationCatalog.Binding binding))
            {
                return binding;
            }

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
    }
}
