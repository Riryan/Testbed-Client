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
    /// Local presentation companion for the placeable Interaction Test Dummy.
    /// Server acceptance remains authoritative. Approach reuses the existing PlayerEntity
    /// movement-command path; animation presentation reuses the canonical action driver/catalog.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionTestDummyPresentation : MonoBehaviour
    {
        private const float DefaultApproachDistance = 1.05f;
        private const float DummyFaceSeconds = 0.18f;

        [SerializeField] private WorldObject worldObject;
        [SerializeField] private Animator receiverAnimator;

        private PlayerEntityGameManager _manager;
        private PlayerEntityClient _ownerClient;
        private PlayerEntityInteractionApproachDriver _approachDriver;
        private PlayerInteractionAnimationPresentation _ownerPresentation;

        private uint _approachToken;
        private uint _presentationVersion;
        private InteractionActionId _pendingAction;

        private Coroutine _faceRoutine;
        private Coroutine _stopRoutine;
        private Coroutine _receiverLoopRoutine;
        private Coroutine _receiverClearRoutine;

        private bool _receiverCanonicalLatched;
        private int _receiverCanonicalLayer = -1;
        private float _receiverCanonicalPreviousWeight;

        private byte _receiverCatalogPresentationId;
        private InteractionAnimationCatalog.Binding _receiverCatalogBinding;
        private int _receiverCatalogLayer = -1;
        private float _receiverCatalogPreviousWeight;

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
            BindManager();
        }

        private void OnDisable()
        {
            UnbindManager();
            CancelApproach();
            ForceClearPresentation();
        }

        private void ResolveFixtureReferences()
        {
            if (worldObject == null)
                worldObject = GetComponent<WorldObject>();

            if (receiverAnimator != null &&
                receiverAnimator.runtimeAnimatorController != null)
            {
                return;
            }

            receiverAnimator = null;
            Animator[] animators = GetComponentsInChildren<Animator>(true);
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

            _manager = FindFirstObjectByType<PlayerEntityGameManager>(
                FindObjectsInactive.Include);
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

            BeginAcceptedApproach(message.ActionId);
        }

        private void BeginAcceptedApproach(InteractionActionId actionId)
        {
            CancelApproach();
            ForceClearPresentation();

            if (!PlayerEntityClient.TryGetOwner(out _ownerClient) ||
                _ownerClient == null)
            {
                Debug.LogWarning(
                    "[Interaction Test Dummy] Local PlayerEntity owner was not found.",
                    this);
                return;
            }

            _approachDriver =
                _ownerClient.GetComponent<PlayerEntityInteractionApproachDriver>();
            if (_approachDriver == null)
            {
                _approachDriver =
                    _ownerClient.gameObject.AddComponent<PlayerEntityInteractionApproachDriver>();
            }

            unchecked
            {
                _approachToken++;
                if (_approachToken == 0)
                    _approachToken = 1;
            }

            _pendingAction = actionId;
            _approachDriver.Finished += OnApproachFinished;

            ResolveFixtureReferences();
            Vector3 targetPosition = receiverAnimator != null
                ? receiverAnimator.transform.position
                : worldObject.transform.position;

            if (!_approachDriver.Begin(
                    _approachToken,
                    targetPosition,
                    DefaultApproachDistance))
            {
                _approachDriver.Finished -= OnApproachFinished;
                _approachDriver = null;
                _ownerClient = null;
                Debug.LogWarning(
                    "[Interaction Test Dummy] Could not begin normal PlayerEntity auto-approach.",
                    this);
                return;
            }

            // Turn the receiver while the player is already walking in. This removes the old
            // approach-complete -> face -> animate dead gap without adding movement traffic.
            if (_faceRoutine != null)
                StopCoroutine(_faceRoutine);
            _faceRoutine = StartCoroutine(FaceDummyDuringApproach(_approachToken));
        }

        private IEnumerator FaceDummyDuringApproach(uint token)
        {
            ResolveFixtureReferences();
            Transform receiver = receiverAnimator != null
                ? receiverAnimator.transform
                : null;

            if (receiver == null || _ownerClient == null)
            {
                _faceRoutine = null;
                yield break;
            }

            Quaternion start = receiver.rotation;
            float elapsed = 0f;

            while (elapsed < DummyFaceSeconds)
            {
                if (token != _approachToken || _ownerClient == null)
                    yield break;

                Vector3 toOwner = _ownerClient.PresentationPosition - receiver.position;
                toOwner.y = 0f;
                elapsed += Time.unscaledDeltaTime;

                if (toOwner.sqrMagnitude > 0.000001f)
                {
                    Quaternion target = Quaternion.Euler(
                        0f,
                        Mathf.Atan2(toOwner.x, toOwner.z) * Mathf.Rad2Deg,
                        0f);
                    float t = Mathf.Clamp01(
                        elapsed / Mathf.Max(0.01f, DummyFaceSeconds));
                    t = t * t * (3f - 2f * t);
                    receiver.rotation = Quaternion.Slerp(start, target, t);
                }

                yield return null;
            }

            _faceRoutine = null;
        }

        private void OnApproachFinished(uint token, bool success)
        {
            if (_approachDriver != null)
                _approachDriver.Finished -= OnApproachFinished;

            if (token != _approachToken)
                return;

            if (_faceRoutine != null)
            {
                StopCoroutine(_faceRoutine);
                _faceRoutine = null;
            }

            if (!success)
            {
                _pendingAction = InteractionActionId.None;
                _ownerClient = null;
                _approachDriver = null;
                return;
            }

            FaceDummyImmediately();
            PlayTestPresentation(_pendingAction);
        }

        private void FaceDummyImmediately()
        {
            ResolveFixtureReferences();
            Transform receiver = receiverAnimator != null
                ? receiverAnimator.transform
                : null;

            if (receiver == null || _ownerClient == null)
                return;

            Vector3 toOwner = _ownerClient.PresentationPosition - receiver.position;
            toOwner.y = 0f;
            if (toOwner.sqrMagnitude <= 0.000001f)
                return;

            receiver.rotation = Quaternion.Euler(
                0f,
                Mathf.Atan2(toOwner.x, toOwner.z) * Mathf.Rad2Deg,
                0f);
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

            _ownerPresentation = ResolveOwnerPresentation();
            if (_ownerPresentation != null)
            {
                _ownerPresentation.PresentAction(actionId, receiver: false);
            }
            else
            {
                Debug.LogWarning(
                    "[Interaction Test Dummy] Local PlayerInteractionAnimationPresentation was not found; receiver animation will still be attempted.",
                    this);
            }

            PresentReceiver(actionId);

            float duration = (float)Math.Max(
                0.1d,
                InteractionPresentationWire.DevelopmentDurationSeconds(actionId));
            _stopRoutine = StartCoroutine(StopAfter(version, duration));
        }

        private PlayerInteractionAnimationPresentation ResolveOwnerPresentation()
        {
            if (_ownerClient == null &&
                !PlayerEntityClient.TryGetOwner(out _ownerClient))
            {
                return null;
            }

            if (_ownerClient == null)
                return null;

            PlayerInteractionAnimationPresentation presentation =
                _ownerClient.GetComponent<PlayerInteractionAnimationPresentation>();
            if (presentation == null)
            {
                presentation = _ownerClient.GetComponentInChildren<
                    PlayerInteractionAnimationPresentation>(true);
            }

            return presentation;
        }

        private void PresentReceiver(InteractionActionId actionId)
        {
            ResolveFixtureReferences();
            if (receiverAnimator == null)
            {
                Debug.LogWarning(
                    "[Interaction Test Dummy] No receiver Animator with a RuntimeAnimatorController was found.",
                    this);
                return;
            }

            string canonicalFailure = string.Empty;
            if (CanonicalInteractionAnimatorDriver.TryResolveAction(
                    actionId,
                    receiver: true,
                    out int controllerActionId) &&
                CanonicalInteractionAnimatorDriver.Begin(
                    receiverAnimator,
                    controllerActionId,
                    out _receiverCanonicalLayer,
                    out _receiverCanonicalPreviousWeight,
                    out canonicalFailure))
            {
                _receiverCanonicalLatched = true;
                _receiverLoopRoutine = StartCoroutine(AdvanceReceiverToLoop());
                return;
            }

            byte presentationId =
                InteractionPresentationWire.EncodeInteraction(actionId, receiver: true);

            if (PlayerInteractionAnimationPresentation.TryPlayCatalogFallback(
                    receiverAnimator,
                    presentationId,
                    out InteractionAnimationCatalog.Binding binding,
                    out int layerIndex,
                    out float previousLayerWeight))
            {
                _receiverCatalogPresentationId = presentationId;
                _receiverCatalogBinding = binding;
                _receiverCatalogLayer = layerIndex;
                _receiverCatalogPreviousWeight = previousLayerWeight;
                return;
            }

            Debug.LogWarning(
                string.IsNullOrWhiteSpace(canonicalFailure)
                    ? $"[Interaction Test Dummy] No authored receiver animation resolved for '{actionId}'."
                    : $"[Interaction Test Dummy] No authored receiver animation resolved for '{actionId}'. Canonical path: {canonicalFailure}.",
                this);
        }

        private IEnumerator AdvanceReceiverToLoop()
        {
            yield return new WaitForSecondsRealtime(
                CanonicalInteractionAnimatorDriver.StartPhaseHoldSeconds);
            _receiverLoopRoutine = null;

            if (_receiverCanonicalLatched && receiverAnimator != null)
            {
                CanonicalInteractionAnimatorDriver.SetPhase(
                    receiverAnimator,
                    CanonicalInteractionAnimatorDriver.LoopPhase);
            }
        }

        private IEnumerator StopAfter(uint expectedVersion, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);

            if (_presentationVersion == expectedVersion)
                BeginFinishPresentation();

            _stopRoutine = null;
        }

        private void BeginFinishPresentation()
        {
            if (_ownerPresentation != null)
                _ownerPresentation.StopPresentation();
            _ownerPresentation = null;

            if (_receiverLoopRoutine != null)
            {
                StopCoroutine(_receiverLoopRoutine);
                _receiverLoopRoutine = null;
            }

            if (_receiverCanonicalLatched && receiverAnimator != null)
            {
                CanonicalInteractionAnimatorDriver.SetPhase(
                    receiverAnimator,
                    CanonicalInteractionAnimatorDriver.FinishPhase);

                if (_receiverClearRoutine != null)
                    StopCoroutine(_receiverClearRoutine);
                _receiverClearRoutine = StartCoroutine(ClearReceiverAfterFinish());
                return;
            }

            if (_receiverCatalogPresentationId != InteractionPresentationWire.Stop)
            {
                PlayerInteractionAnimationPresentation.StopCatalogFallback(
                    receiverAnimator,
                    _receiverCatalogPresentationId,
                    _receiverCatalogBinding);
                ClearReceiverCatalogPresentation();
            }

            ReleaseApproachFacing();
        }

        private IEnumerator ClearReceiverAfterFinish()
        {
            yield return new WaitForSecondsRealtime(
                CanonicalInteractionAnimatorDriver.FinishPhaseHoldSeconds);
            _receiverClearRoutine = null;

            if (receiverAnimator != null)
            {
                CanonicalInteractionAnimatorDriver.Clear(
                    receiverAnimator,
                    _receiverCanonicalLayer,
                    _receiverCanonicalPreviousWeight);
            }

            _receiverCanonicalLayer = -1;
            _receiverCanonicalPreviousWeight = 0f;
            _receiverCanonicalLatched = false;
            ReleaseApproachFacing();
        }

        private void ClearReceiverCatalogPresentation()
        {
            PlayerInteractionAnimationPresentation.RestoreCatalogLayer(
                receiverAnimator,
                _receiverCatalogLayer,
                _receiverCatalogPreviousWeight);

            _receiverCatalogPresentationId = InteractionPresentationWire.Stop;
            _receiverCatalogBinding = null;
            _receiverCatalogLayer = -1;
            _receiverCatalogPreviousWeight = 0f;
        }

        private void CancelApproach()
        {
            if (_faceRoutine != null)
            {
                StopCoroutine(_faceRoutine);
                _faceRoutine = null;
            }

            if (_approachDriver != null)
            {
                _approachDriver.Finished -= OnApproachFinished;
                _approachDriver.Cancel(_approachToken, notify: false);
            }

            _pendingAction = InteractionActionId.None;
            _ownerClient = null;
            _approachDriver = null;
        }

        private void ReleaseApproachFacing()
        {
            if (_approachDriver != null)
            {
                _approachDriver.Finished -= OnApproachFinished;
                _approachDriver.ReleaseFacingHold(_approachToken);
            }

            _pendingAction = InteractionActionId.None;
            _ownerClient = null;
            _approachDriver = null;
        }

        private void ForceClearPresentation()
        {
            if (_stopRoutine != null)
            {
                StopCoroutine(_stopRoutine);
                _stopRoutine = null;
            }
            if (_receiverLoopRoutine != null)
            {
                StopCoroutine(_receiverLoopRoutine);
                _receiverLoopRoutine = null;
            }
            if (_receiverClearRoutine != null)
            {
                StopCoroutine(_receiverClearRoutine);
                _receiverClearRoutine = null;
            }

            if (_ownerPresentation != null)
                _ownerPresentation.StopPresentation();
            _ownerPresentation = null;

            ResolveFixtureReferences();
            if (_receiverCanonicalLatched && receiverAnimator != null)
            {
                CanonicalInteractionAnimatorDriver.Clear(
                    receiverAnimator,
                    _receiverCanonicalLayer,
                    _receiverCanonicalPreviousWeight);
            }

            _receiverCanonicalLayer = -1;
            _receiverCanonicalPreviousWeight = 0f;
            _receiverCanonicalLatched = false;

            if (_receiverCatalogPresentationId != InteractionPresentationWire.Stop)
            {
                PlayerInteractionAnimationPresentation.StopCatalogFallback(
                    receiverAnimator,
                    _receiverCatalogPresentationId,
                    _receiverCatalogBinding);
            }
            ClearReceiverCatalogPresentation();
            ReleaseApproachFacing();
        }
    }
}
