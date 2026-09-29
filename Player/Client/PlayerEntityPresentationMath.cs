using System;
using Player.Shared;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Deterministic presentation math shared by the runtime client and EditMode
    /// tests. Keeping packet-order, jitter, interpolation and extrapolation rules
    /// here prevents tests from becoming a second implementation of the algorithm.
    /// </summary>
    internal static class PlayerEntityPresentationMath
    {
        internal static bool ShouldAcceptSnapshot(uint incomingTick, uint latestTick)
        {
            return NetworkSequence.IsNewer(incomingTick, latestTick);
        }

        internal static double UnwrapTick(
            double previousUnwrappedTick,
            uint incomingTick,
            uint previousTick)
        {
            return previousUnwrappedTick +
                NetworkSequence.ForwardDistance(incomingTick, previousTick);
        }

        internal static double UpdateJitterEwma(
            double currentJitterSeconds,
            double arrivalSpanSeconds,
            double serverSpanSeconds,
            double ewmaFactor)
        {
            double variation = Math.Abs(
                Math.Max(0.0, arrivalSpanSeconds) -
                Math.Max(0.0, serverSpanSeconds));

            double factor = Math.Max(0.0, Math.Min(1.0, ewmaFactor));
            return currentJitterSeconds +
                (variation - currentJitterSeconds) * factor;
        }

        internal static double ComputeAdaptiveDelay(
            double currentDelaySeconds,
            float frameDelta,
            double baseDelaySeconds,
            double maximumDelaySeconds,
            double jitterSeconds,
            double jitterMultiplier,
            double extrapolationPenaltySeconds,
            double increaseResponseSeconds,
            double decreaseResponseSeconds,
            out double desiredDelaySeconds)
        {
            double min = Math.Max(0.0, baseDelaySeconds);
            double max = Math.Max(min, maximumDelaySeconds);
            double desired =
                min +
                Math.Max(0.0, jitterSeconds) * Math.Max(0.0, jitterMultiplier) +
                Math.Max(0.0, extrapolationPenaltySeconds);

            desiredDelaySeconds = Math.Max(min, Math.Min(max, desired));

            double current = Math.Max(min, Math.Min(max, currentDelaySeconds));
            double response = desiredDelaySeconds > current
                ? Math.Max(0.02, increaseResponseSeconds)
                : Math.Max(0.25, decreaseResponseSeconds);

            double alpha = 1.0 - Math.Exp(-Math.Max(0f, frameDelta) / response);
            double next = current + (desiredDelaySeconds - current) * alpha;
            return Math.Max(min, Math.Min(max, next));
        }

        internal static bool IsTeleport(
            PlayerEntitySnapshot value,
            Vector3 previousPosition,
            double serverSpanSeconds,
            float teleportDistance,
            float teleportSpeedThreshold)
        {
            if ((((PlayerEntityFlags)value.flags) &
                 PlayerEntityFlags.Teleport) != 0)
            {
                return true;
            }

            float distance = Vector3.Distance(previousPosition, value.Position);
            float impliedSpeed = serverSpanSeconds > 0.0001
                ? distance / (float)serverSpanSeconds
                : float.MaxValue;

            return distance >= Math.Max(0.1f, teleportDistance) &&
                impliedSpeed >= Math.Max(1f, teleportSpeedThreshold);
        }

        internal static bool IsAuthoritativelyStopped(
            PlayerEntitySnapshot snapshot,
            float stoppedMoveSpeedEpsilon)
        {
            PlayerEntityMoveState state =
                (PlayerEntityMoveState)snapshot.moveState;

            if (state == PlayerEntityMoveState.Idle ||
                state == PlayerEntityMoveState.Dead)
            {
                return true;
            }

            return snapshot.MoveSpeed <= Math.Max(0f, stoppedMoveSpeedEpsilon);
        }

        internal static float IntegratedPredictionTime(
            float elapsed,
            float fullPredictionSeconds,
            float coastSeconds)
        {
            float full = Mathf.Max(0f, fullPredictionSeconds);
            float safeElapsed = Mathf.Max(0f, elapsed);

            if (safeElapsed <= full)
                return safeElapsed;

            float coast = Mathf.Max(0f, coastSeconds);
            if (coast <= 0.0001f)
                return full;

            float extra = Mathf.Clamp(safeElapsed - full, 0f, coast);
            float coastIntegral =
                extra - (extra * extra) / (2f * coast);

            return full + coastIntegral;
        }

        internal static float DirectionContinuity(Vector3 a, Vector3 b)
        {
            float aSq = a.sqrMagnitude;
            float bSq = b.sqrMagnitude;

            if (aSq < 0.0001f || bSq < 0.0001f)
                return 0.65f;

            float dot = Vector3.Dot(
                a / Mathf.Sqrt(aSq),
                b / Mathf.Sqrt(bSq));

            return Mathf.InverseLerp(-0.35f, 0.85f, dot);
        }

        internal static Vector3 Hermite(
            Vector3 p0,
            Vector3 p1,
            Vector3 v0,
            Vector3 v1,
            float t,
            float seconds)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return
                h00 * p0 +
                h10 * (v0 * seconds) +
                h01 * p1 +
                h11 * (v1 * seconds);
        }

        internal static float HermiteScalar(
            float p0,
            float p1,
            float v0,
            float v1,
            float t,
            float seconds)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return
                h00 * p0 +
                h10 * (v0 * seconds) +
                h01 * p1 +
                h11 * (v1 * seconds);
        }

        internal static Vector3 BoundHermiteDeviation(
            Vector3 p0,
            Vector3 p1,
            Vector3 linear,
            Vector3 hermite,
            float maximumHermiteDeviation)
        {
            Vector3 deviation = hermite - linear;
            float segmentLength = Vector3.Distance(p0, p1);
            float allowedDeviation = Mathf.Min(
                Mathf.Max(0.05f, maximumHermiteDeviation),
                0.05f + segmentLength * 0.40f);

            if (deviation.sqrMagnitude > allowedDeviation * allowedDeviation)
            {
                hermite = linear +
                    Vector3.ClampMagnitude(deviation, allowedDeviation);
            }

            return hermite;
        }

        internal static bool ShouldHardSnap(
            bool forcePresentationSnap,
            float presentationError,
            float hardSnapDistance)
        {
            return forcePresentationSnap ||
                presentationError >= Math.Max(0f, hardSnapDistance);
        }
    }
}
