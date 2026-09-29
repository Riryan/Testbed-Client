using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using UnityEngine;

namespace Player.Networking
{
    /// <summary>
    /// Server-side admission governance for connected-but-unauthenticated peers.
    /// The exact PlayerSessionHandle is the key so a reused connection id cannot
    /// inherit another generation's timeout or attempt budget.
    /// </summary>
    public sealed partial class PlayerEntityGameManager
    {
        [Header("Authentication Admission")]
        [SerializeField, Range(10f, 300f)] private float authenticationTimeoutSeconds = 60f;
        [SerializeField, Range(1, 20)] private int maxAdmissionAttemptsPerSession = 5;

        private readonly Dictionary<PlayerSessionHandle, PendingAuthenticationAdmission>
            _pendingAuthenticationAdmissions =
                new Dictionary<PlayerSessionHandle, PendingAuthenticationAdmission>();
        private float _authenticationAdmissionSweepCountdown;

        private void ResetAuthenticationAdmissionTracking()
        {
            _pendingAuthenticationAdmissions.Clear();
            _authenticationAdmissionSweepCountdown = 1f;
        }

        private void TrackAuthenticationAdmission(PlayerSessionHandle handle)
        {
            if (!handle.IsValid)
                return;

            long timeoutMs = (long)(Math.Max(10f, authenticationTimeoutSeconds) * 1000f);
            _pendingAuthenticationAdmissions[handle] = new PendingAuthenticationAdmission
            {
                DeadlineTimestampMs = ServerTimestamp + timeoutMs,
                Attempts = 0,
            };
        }

        private bool TryBeginAuthenticationAdmissionAttempt(PlayerSessionHandle handle)
        {
            if (!handle.IsValid ||
                !_pendingAuthenticationAdmissions.TryGetValue(
                    handle,
                    out PendingAuthenticationAdmission pending))
            {
                return false;
            }

            if (pending.Attempts >= Math.Max(1, maxAdmissionAttemptsPerSession))
                return false;

            pending.Attempts++;
            _pendingAuthenticationAdmissions[handle] = pending;
            return true;
        }

        private void CompleteAuthenticationAdmission(PlayerSessionHandle handle) =>
            _pendingAuthenticationAdmissions.Remove(handle);

        private void RemoveAuthenticationAdmission(PlayerSessionHandle handle) =>
            _pendingAuthenticationAdmissions.Remove(handle);

        private void UpdateAuthenticationAdmissionPolicy(float deltaTime)
        {
            if (!IsServer || _pendingAuthenticationAdmissions.Count == 0)
                return;

            _authenticationAdmissionSweepCountdown -= deltaTime;
            if (_authenticationAdmissionSweepCountdown > 0f)
                return;
            _authenticationAdmissionSweepCountdown = 1f;

            long now = ServerTimestamp;
            var expired = new List<PlayerSessionHandle>();
            foreach (KeyValuePair<PlayerSessionHandle, PendingAuthenticationAdmission> pair
                     in _pendingAuthenticationAdmissions)
            {
                if (pair.Value.DeadlineTimestampMs <= now)
                    expired.Add(pair.Key);
            }

            for (int i = 0; i < expired.Count; ++i)
            {
                PlayerSessionHandle handle = expired[i];
                _pendingAuthenticationAdmissions.Remove(handle);

                if (_playerSessionHandles.TryGetValue(
                        handle.Connection.Value,
                        out PlayerSessionHandle current) &&
                    current == handle)
                {
                    Debug.LogWarning(
                        $"[PlayerEntity] Authentication timeout for exact session {handle}. Disconnecting peer.");
                    KickClient(handle.Connection.Value, Array.Empty<byte>()).Forget();
                }
            }
        }

        private struct PendingAuthenticationAdmission
        {
            public long DeadlineTimestampMs;
            public int Attempts;
        }
    }
}
