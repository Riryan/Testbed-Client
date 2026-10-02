using System;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Owner-local interaction approach intent.
    ///
    /// This component DOES NOT move transforms authoritatively and DOES NOT send network
    /// messages. PlayerEntityInput asks it for a temporary movement/facing override and
    /// serializes that through the existing MovementCommand path. The standalone GameServer
    /// remains the sole movement authority.
    ///
    /// It exists so accepted actor interactions can visibly walk into final spacing instead
    /// of teleporting/snapping to an interaction anchor.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(11000)]
    public sealed class PlayerEntityInteractionApproachDriver : MonoBehaviour
    {
        private enum ApproachState : byte
        {
            None = 0,
            Walking = 1,
            FacingHold = 2,
        }

        private const float MinimumStopDistance = 0.25f;
        private const float ArrivalDistanceTolerance = 0.06f;
        private const float ArrivalFacingToleranceDegrees = 5f;
        private const float TurnDegreesPerSecond = 540f;
        private const float DefaultTimeoutSeconds = 5f;
        private const float StuckTimeoutSeconds = 1.25f;
        private const float ProgressEpsilon = 0.025f;

        private PlayerEntityClient _client;
        private ApproachState _state;
        private Vector3 _targetWorldPosition;
        private float _stopDistance;
        private float _displayYaw;
        private float _startedAt;
        private float _lastProgressAt;
        private float _bestDistance;
        private uint _token;

        // The owner presentation predictor reads normal camera locomotion intent directly,
        // while this component overrides the actual MovementCommand sent to the server.
        // During auto-approach those two inputs differ. Temporarily follow authoritative
        // snapshots instead of maintaining a second, stale speculative position.
        private bool _predictionModeCaptured;
        private bool _previousLocalPrediction;

        /// <summary>
        /// Raised once when Walking reaches final spacing/facing or is cancelled.
        /// success=true leaves a FacingHold active until ReleaseFacingHold is called.
        /// </summary>
        public event Action<uint, bool> Finished;

        public bool IsActive => _state != ApproachState.None;
        public bool IsWalking => _state == ApproachState.Walking;
        public bool IsHoldingFacing => _state == ApproachState.FacingHold;
        public uint ActiveToken => _token;

        private void Awake()
        {
            _client = GetComponent<PlayerEntityClient>();
        }

        private void OnDisable()
        {
            Cancel(0, notify: false);
        }

        /// <summary>
        /// Begins a short visible walk toward a stationary interaction partner.
        /// The target transform itself is not moved.
        /// </summary>
        public bool Begin(
            uint token,
            Vector3 targetWorldPosition,
            float stopDistance)
        {
            if (token == 0)
                return false;

            if (_client == null)
                _client = GetComponent<PlayerEntityClient>();

            if (_client == null || !_client.IsOwnerClient)
                return false;

            Cancel(0, notify: false);

            CaptureAndSuppressOwnerPrediction();

            _token = token;
            _targetWorldPosition = targetWorldPosition;
            _stopDistance = Mathf.Max(MinimumStopDistance, stopDistance);

            Vector3 current = _client.PresentationPosition;
            Vector3 planar = _targetWorldPosition - current;
            planar.y = 0f;

            _displayYaw = transform.eulerAngles.y;
            if (planar.sqrMagnitude > 0.000001f)
                _displayYaw = Mathf.Repeat(Mathf.Atan2(planar.x, planar.z) * Mathf.Rad2Deg, 360f);

            _bestDistance = planar.magnitude;
            _startedAt = Time.unscaledTime;
            _lastProgressAt = _startedAt;
            _state = ApproachState.Walking;
            return true;
        }

        /// <summary>
        /// PlayerEntityInput calls this from its existing client tick. Returning true replaces
        /// ordinary keyboard movement for this tick, but the existing serializer, cadence,
        /// shared-world prediction and server validation remain unchanged.
        /// </summary>
        public bool TryOverrideMovementIntent(
            out Vector2 worldInput,
            out float facingYaw,
            out byte flags)
        {
            worldInput = Vector2.zero;
            facingYaw = _displayYaw;
            flags = 0;

            if (_state == ApproachState.None ||
                _client == null ||
                !_client.IsOwnerClient)
            {
                return false;
            }

            // Manual locomotion cancels only the approach walk. Once the paired animation
            // has started, FacingHold intentionally owns facing until presentation ends.
            if (_state == ApproachState.Walking && HasManualMovementInput())
            {
                Cancel(_token, notify: true);
                return false;
            }

            Vector3 current = _client.PresentationPosition;
            Vector3 planar = _targetWorldPosition - current;
            planar.y = 0f;
            float distance = planar.magnitude;

            if (planar.sqrMagnitude > 0.000001f)
            {
                float desiredYaw =
                    Mathf.Repeat(
                        Mathf.Atan2(planar.x, planar.z) * Mathf.Rad2Deg,
                        360f);

                float turnStep =
                    Mathf.Max(1f, TurnDegreesPerSecond) *
                    Mathf.Max(1f / 120f, Time.unscaledDeltaTime);

                _displayYaw =
                    Mathf.MoveTowardsAngle(
                        _displayYaw,
                        desiredYaw,
                        turnStep);
            }

            facingYaw = Mathf.Repeat(_displayYaw, 360f);

            if (_state == ApproachState.FacingHold)
            {
                worldInput = Vector2.zero;
                return true;
            }

            float now = Time.unscaledTime;
            if (now - _startedAt >= DefaultTimeoutSeconds)
            {
                Cancel(_token, notify: true);
                return true;
            }

            if (distance + ProgressEpsilon < _bestDistance)
            {
                _bestDistance = distance;
                _lastProgressAt = now;
            }
            else if (distance > _stopDistance + ArrivalDistanceTolerance &&
                     now - _lastProgressAt >= StuckTimeoutSeconds)
            {
                // Existing shared-world prediction or authoritative collision may have
                // prevented progress. Cancel instead of ever teleporting through it.
                Cancel(_token, notify: true);
                return true;
            }

            float remaining =
                distance - _stopDistance;

            if (remaining > ArrivalDistanceTolerance)
            {
                if (distance > 0.0001f)
                {
                    Vector2 direction =
                        new Vector2(planar.x, planar.z) /
                        distance;

                    // Ease down over the final ~0.7m so this reads as an approach rather
                    // than a full-speed collision into the partner.
                    float magnitude =
                        Mathf.Clamp(
                            remaining / 0.70f,
                            0.28f,
                            1f);

                    worldInput = direction * magnitude;
                }

                return true;
            }

            worldInput = Vector2.zero;

            float desiredFinalYaw = facingYaw;
            if (planar.sqrMagnitude > 0.000001f)
            {
                desiredFinalYaw =
                    Mathf.Repeat(
                        Mathf.Atan2(planar.x, planar.z) * Mathf.Rad2Deg,
                        360f);
            }

            float facingError =
                Mathf.Abs(
                    Mathf.DeltaAngle(
                        facingYaw,
                        desiredFinalYaw));

            if (facingError > ArrivalFacingToleranceDegrees)
                return true;

            // Keep the participant facing the partner during the paired animation.
            uint completedToken = _token;
            _state = ApproachState.FacingHold;
            Finished?.Invoke(completedToken, true);
            return true;
        }

        /// <summary>
        /// Keeps the local rendered owner aligned with the same facing carried by the existing
        /// MovementCommand. This is presentation prediction only; server yaw remains authoritative.
        /// </summary>
        private void LateUpdate()
        {
            if (_state == ApproachState.None ||
                _client == null ||
                !_client.IsOwnerClient)
            {
                return;
            }

            _client.ApplyOwnerLocalFacing(_displayYaw);
        }

        public void ReleaseFacingHold(uint token)
        {
            if (_state == ApproachState.None)
                return;

            if (token != 0 && token != _token)
                return;

            _state = ApproachState.None;
            _token = 0;
            RestoreOwnerPrediction();
        }

        public void Cancel(uint token = 0, bool notify = true)
        {
            if (_state == ApproachState.None)
                return;

            if (token != 0 && token != _token)
                return;

            uint cancelledToken = _token;
            _state = ApproachState.None;
            _token = 0;
            RestoreOwnerPrediction();

            if (notify && cancelledToken != 0)
                Finished?.Invoke(cancelledToken, false);
        }

        private void CaptureAndSuppressOwnerPrediction()
        {
            if (_client == null || !_client.IsOwnerClient)
                return;

            _previousLocalPrediction = _client.localPrediction;
            _predictionModeCaptured = true;

            // PlayerEntityInput continues to send the normal authoritative MovementCommand
            // path. Only the separate render-side speculative position is disabled.
            _client.localPrediction = false;
        }

        private void RestoreOwnerPrediction()
        {
            if (!_predictionModeCaptured)
                return;

            _predictionModeCaptured = false;

            if (_client != null)
                _client.localPrediction = _previousLocalPrediction;
        }

        private static bool HasManualMovementInput()
        {
            return
                PlayerControlConfig.GetKey(PlayerControlAction.MoveForward) ||
                PlayerControlConfig.GetKey(PlayerControlAction.MoveBackward) ||
                PlayerControlConfig.GetKey(PlayerControlAction.MoveLeft) ||
                PlayerControlConfig.GetKey(PlayerControlAction.MoveRight) ||
                PlayerControlConfig.GetKey(PlayerControlAction.Jump);
        }
    }
}
