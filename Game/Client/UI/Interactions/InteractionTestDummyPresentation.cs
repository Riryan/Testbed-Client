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
    /// The normal ContextInteraction request/response remains authoritative. After success,
    /// the Player approaches the stationary dummy through the EXISTING PlayerEntity movement
    /// command path. No transform teleport is performed. Once final spacing/facing is reached,
    /// paired presentation begins.
    ///
    /// No new network message, request type, polling stream, or authoritative movement path.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionTestDummyPresentation : MonoBehaviour
    {
        private const float DefaultApproachDistance = 1.05f;
        private const float DummyFaceSeconds = 0.18f;

        [SerializeField] private WorldObject worldObject;
        [SerializeField] private Animator receiverAnimator;

        private PlayerEntityGameManager _manager;
        private PlayerInteractionAnimationPresentation _ownerPresentation;
        private PlayerEntityInteractionApproachDriver _approachDriver;
        private PlayerEntityClient _ownerClient;

        private uint _presentationVersion;
        private uint _approachToken;
        private InteractionActionId _pendingAction;

        private Coroutine _dummyFaceRoutine;
        private Coroutine _stopRoutine;
        private Coroutine _receiverLoopRoutine;
        private Coroutine _receiverClearRoutine;

        private bool _receiverActionLatched;
        private int _receiverLayerIndex = -1;
        private float _receiverPreviousLayerWeight;

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

            Animator[] animators =
                GetComponentsInChildren<Animator>(true);

            receiverAnimator = null;
            for (int i = 0; i < animators.Length; ++i)
            {
                Animator candidate = animators[i];
                if (candidate == null ||
                    candidate.runtimeAnimatorController == null)
                {
                    continue;
                }

                receiverAnimator = candidate;
                break;
            }
        }

        private void BindManager()
        {
            if (_manager != null)
                return;

            _manager =
                FindFirstObjectByType<PlayerEntityGameManager>(
                    FindObjectsInactive.Include);

            if (_manager != null)
            {
                _manager.ContextInteractionResultReceived +=
                    OnContextInteractionResult;
            }
        }

        private void UnbindManager()
        {
            if (_manager != null)
            {
                _manager.ContextInteractionResultReceived -=
                    OnContextInteractionResult;
            }

            _manager = null;
        }

        private void OnContextInteractionResult(
            ContextInteractionResponseMessage message)
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

        private void BeginAcceptedApproach(
            InteractionActionId actionId)
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
                // PlayerEntityInput normally installs this on owner start. Keep a narrow
                // self-heal for scenes where the component order differs.
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

            Vector3 targetPosition =
                receiverAnimator != null
                    ? receiverAnimator.transform.position
                    : worldObject.transform.position;

            if (!_approachDriver.Begin(
                    _approachToken,
                    targetPosition,
                    ResolveApproachDistance(actionId)))
            {
                _approachDriver.Finished -= OnApproachFinished;
                _approachDriver = null;
                _ownerClient = null;

                Debug.LogWarning(
                    "[Interaction Test Dummy] Could not begin normal PlayerEntity auto-approach.",
                    this);
            }
        }

        private void OnApproachFinished(
            uint token,
            bool success)
        {
            if (_approachDriver != null)
                _approachDriver.Finished -= OnApproachFinished;

            if (token != _approachToken)
                return;

            if (!success)
            {
                _pendingAction = InteractionActionId.None;
                _ownerClient = null;
                _approachDriver = null;
                return;
            }

            if (_dummyFaceRoutine != null)
                StopCoroutine(_dummyFaceRoutine);

            _dummyFaceRoutine =
                StartCoroutine(
                    FaceDummyThenPlay(
                        token,
                        _pendingAction));
        }

        private IEnumerator FaceDummyThenPlay(
            uint token,
            InteractionActionId actionId)
        {
            ResolveFixtureReferences();

            Transform receiver =
                receiverAnimator != null
                    ? receiverAnimator.transform
                    : null;

            if (receiver != null &&
                _ownerClient != null)
            {
                Vector3 toOwner =
                    _ownerClient.PresentationPosition -
                    receiver.position;
                toOwner.y = 0f;

                if (toOwner.sqrMagnitude > 0.000001f)
                {
                    Quaternion start = receiver.rotation;
                    Quaternion target =
                        Quaternion.Euler(
                            0f,
                            Mathf.Atan2(toOwner.x, toOwner.z) * Mathf.Rad2Deg,
                            0f);

                    float elapsed = 0f;
                    while (elapsed < DummyFaceSeconds)
                    {
                        if (token != _approachToken)
                            yield break;

                        elapsed += Time.unscaledDeltaTime;
                        float t =
                            Mathf.Clamp01(
                                elapsed / Mathf.Max(0.01f, DummyFaceSeconds));

                        // Smoothstep keeps the stationary dummy from visibly snapping.
                        t = t * t * (3f - 2f * t);
                        receiver.rotation =
                            Quaternion.Slerp(start, target, t);
                        yield return null;
                    }

                    receiver.rotation = target;
                }
            }

            _dummyFaceRoutine = null;

            if (token == _approachToken)
                PlayTestPresentation(actionId);
        }

        private void PlayTestPresentation(
            InteractionActionId actionId)
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
                _ownerPresentation.PresentAction(
                    actionId,
                    receiver: false);
            }
            else
            {
                Debug.LogWarning(
                    "[Interaction Test Dummy] Local PlayerInteractionAnimationPresentation " +
                    "was not found; receiver animation will still be attempted.",
                    this);
            }

            PresentReceiver(actionId);

            float duration =
                (float)Math.Max(
                    0.1d,
                    InteractionPresentationWire
                        .DevelopmentDurationSeconds(actionId));

            _stopRoutine =
                StartCoroutine(
                    StopAfter(
                        version,
                        duration));
        }

        private PlayerInteractionAnimationPresentation
            ResolveOwnerPresentation()
        {
            if (_ownerClient == null &&
                !PlayerEntityClient.TryGetOwner(out _ownerClient))
            {
                return null;
            }

            if (_ownerClient == null)
                return null;

            PlayerInteractionAnimationPresentation presentation =
                _ownerClient.GetComponent<
                    PlayerInteractionAnimationPresentation>();

            if (presentation == null)
            {
                presentation =
                    _ownerClient.GetComponentInChildren<
                        PlayerInteractionAnimationPresentation>(
                        true);
            }

            return presentation;
        }

        private IEnumerator StopAfter(
            uint expectedVersion,
            float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);

            if (_presentationVersion == expectedVersion)
                BeginFinishPresentation();

            _stopRoutine = null;
        }

        private void PresentReceiver(
            InteractionActionId actionId)
        {
            ResolveFixtureReferences();

            if (receiverAnimator == null)
            {
                Debug.LogWarning(
                    "[Interaction Test Dummy] No receiver Animator with a RuntimeAnimatorController was found.",
                    this);
                return;
            }

            if (!CanonicalInteractionAnimatorDriver.TryResolveAction(
                    actionId,
                    receiver: true,
                    out int controllerActionId))
            {
                Debug.LogWarning(
                    $"[Interaction Test Dummy] '{actionId}' has no proven PlayerHumanoid " +
                    "CP_ActionId receiver mapping yet. The controller-backed fixture " +
                    "currently supports Feed, Partner Dance, Hug, and Kiss.",
                    this);
                return;
            }

            if (!CanonicalInteractionAnimatorDriver.Begin(
                    receiverAnimator,
                    controllerActionId,
                    out _receiverLayerIndex,
                    out _receiverPreviousLayerWeight,
                    out string failure))
            {
                Debug.LogWarning(
                    $"[Interaction Test Dummy] Could not begin receiver action " +
                    $"{controllerActionId} for '{actionId}': {failure}.",
                    this);
                return;
            }

            _receiverActionLatched = true;
            _receiverLoopRoutine =
                StartCoroutine(
                    AdvanceReceiverToLoop());
        }

        private IEnumerator AdvanceReceiverToLoop()
        {
            yield return new WaitForSecondsRealtime(
                CanonicalInteractionAnimatorDriver
                    .StartPhaseHoldSeconds);

            _receiverLoopRoutine = null;

            if (!_receiverActionLatched ||
                receiverAnimator == null)
            {
                yield break;
            }

            CanonicalInteractionAnimatorDriver.SetPhase(
                receiverAnimator,
                CanonicalInteractionAnimatorDriver.LoopPhase);
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

            if (_receiverActionLatched &&
                receiverAnimator != null)
            {
                CanonicalInteractionAnimatorDriver.SetPhase(
                    receiverAnimator,
                    CanonicalInteractionAnimatorDriver.FinishPhase);

                if (_receiverClearRoutine != null)
                    StopCoroutine(_receiverClearRoutine);

                _receiverClearRoutine =
                    StartCoroutine(
                        ClearReceiverAfterFinish());
            }

            // Keep facing through Finish. ClearReceiverAfterFinish releases the hold.
        }

        private IEnumerator ClearReceiverAfterFinish()
        {
            yield return new WaitForSecondsRealtime(
                CanonicalInteractionAnimatorDriver
                    .FinishPhaseHoldSeconds);

            _receiverClearRoutine = null;

            if (receiverAnimator != null)
            {
                CanonicalInteractionAnimatorDriver.Clear(
                    receiverAnimator,
                    _receiverLayerIndex,
                    _receiverPreviousLayerWeight);
            }

            _receiverLayerIndex = -1;
            _receiverPreviousLayerWeight = 0f;
            _receiverActionLatched = false;

            ReleaseApproachFacing();
        }

        private void CancelApproach()
        {
            if (_dummyFaceRoutine != null)
            {
                StopCoroutine(_dummyFaceRoutine);
                _dummyFaceRoutine = null;
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

            if (_receiverActionLatched &&
                receiverAnimator != null)
            {
                CanonicalInteractionAnimatorDriver.Clear(
                    receiverAnimator,
                    _receiverLayerIndex,
                    _receiverPreviousLayerWeight);
            }

            _receiverLayerIndex = -1;
            _receiverPreviousLayerWeight = 0f;
            _receiverActionLatched = false;

            ReleaseApproachFacing();
        }

        private static float ResolveApproachDistance(
            InteractionActionId actionId)
        {
            // Use the former authored dummy anchor spacing as the non-teleport destination.
            // Per-action authored spacing can replace this later without changing movement wire.
            return DefaultApproachDistance;
        }
    }
}
