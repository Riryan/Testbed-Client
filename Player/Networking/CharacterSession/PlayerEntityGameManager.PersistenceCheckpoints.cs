using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using LiteNetLibManager;
using UnityEngine;

namespace Player.Networking
{
    /// <summary>
    /// Periodic durable-location checkpointing. Unity samples the canonical live
    /// PlayerEntity transforms, marks portable runtimes dirty, then submits only dirty
    /// character snapshots to the backend. The backend owns database placement/storage.
    /// </summary>
    public sealed partial class PlayerEntityGameManager
    {
        [Header("Persistence Checkpoints")]
        [SerializeField, Min(1f)] private float characterCheckpointIntervalSeconds = 15f;
        [SerializeField, Range(1, 512)] private int characterCheckpointBatchSize = 256;
        [SerializeField] private bool logCharacterCheckpointBatches;

        [Header("Gameplay Content")]
        [SerializeField] private bool logGameplayContentRefresh;

        private float _characterCheckpointCountdown;
        private bool _characterCheckpointInFlight;
        private CancellationTokenSource _characterCheckpointCancellation;
        private bool _gameplayContentRefreshInFlight;
        private float _gameplayContentRetryCountdown;

        private void StartPersistenceCheckpointing()
        {
            StopPersistenceCheckpointing();
            _characterCheckpointCancellation = new CancellationTokenSource();
            _characterCheckpointCountdown = Math.Max(1f, characterCheckpointIntervalSeconds);
            _characterCheckpointInFlight = false;
            _gameplayContentRefreshInFlight = false;
            _gameplayContentRetryCountdown = 0f;
        }

        private void StopPersistenceCheckpointing()
        {
            if (_characterCheckpointCancellation != null)
            {
                _characterCheckpointCancellation.Cancel();
                _characterCheckpointCancellation.Dispose();
                _characterCheckpointCancellation = null;
            }
            _characterCheckpointInFlight = false;
        }

        protected override void OnServerUpdate(LogicUpdater updater)
        {
            base.OnServerUpdate(updater);
            FlushPendingPlayerResourceDeltas();
            FlushPendingPlayerStatusEffectDeltas();
            FlushPendingPlayerItemChanges();
            FlushPendingPlayerServiceStatuses();
            UpdateAuthenticationAdmissionPolicy(updater.DeltaTimeF);

            if (IsServer &&
                _characterSessionRuntimeHost != null &&
                _characterCheckpointCancellation != null)
            {
                if (_gameplayContentRetryCountdown > 0f)
                    _gameplayContentRetryCountdown -= updater.DeltaTimeF;

                if (!_gameplayContentRefreshInFlight &&
                    _gameplayContentRetryCountdown <= 0f &&
                    _characterSessionRuntimeHost.TryGetPendingGameplayContentRevision(out long announcedRevision))
                {
                    RefreshGameplayContentAsync(
                        announcedRevision,
                        _characterCheckpointCancellation.Token).Forget();
                }
            }

            if (!IsServer ||
                _characterSessionRuntimeHost == null ||
                _characterCheckpointCancellation == null ||
                _characterCheckpointInFlight)
            {
                return;
            }

            _characterCheckpointCountdown -= updater.DeltaTimeF;
            if (_characterCheckpointCountdown > 0f)
                return;

            _characterCheckpointCountdown = Math.Max(1f, characterCheckpointIntervalSeconds);
            FlushCharacterCheckpointsAsync(_characterCheckpointCancellation.Token).Forget();
        }


        private async UniTaskVoid RefreshGameplayContentAsync(
            long announcedRevision,
            CancellationToken cancellationToken)
        {
            _gameplayContentRefreshInFlight = true;
            try
            {
                long before = _characterSessionRuntimeHost.Content.Revision;
                long after = await _characterSessionRuntimeHost.RefreshGameplayContentAsync(
                    announcedRevision,
                    cancellationToken);
                await UniTask.SwitchToMainThread();

                if (after >= announcedRevision)
                {
                    _characterSessionRuntimeHost.AcknowledgeGameplayContentRevision(after);
                    _gameplayContentRetryCountdown = 0f;
                    if (logGameplayContentRefresh && after != before)
                        Debug.Log($"[PlayerEntity] Gameplay content revision {before} -> {after} activated.");
                }
                else
                {
                    // The event remains pending. Retry is event-triggered and bounded; there
                    // is no steady-state content polling when no revision is pending.
                    _gameplayContentRetryCountdown = 5f;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                try
                {
                    await UniTask.SwitchToMainThread();
                    _gameplayContentRetryCountdown = 5f;
                    Debug.LogWarning($"[PlayerEntity] Gameplay content refresh rejected: {ex.Message}");
                }
                catch { }
            }
            finally { _gameplayContentRefreshInFlight = false; }
        }

        /// <summary>
        /// Graceful-shutdown durability barrier. Called on Unity's main thread before
        /// LiteNetLib destroys canonical identities, so final authoritative transforms
        /// can be captured and all supported dirty checkpoints can be committed.
        /// Catastrophic process/machine loss still relies on the periodic checkpoint.
        /// </summary>
        private void FlushAllCharacterCheckpointsForShutdownBlocking()
        {
            if (_characterSessionRuntimeHost == null)
                return;

            var handles = new List<PlayerSessionHandle>(_playerSessionHandles.Values);
            for (int i = 0; i < handles.Count; ++i)
            {
                _characterSessionRuntimeHost.CanonicalBridge
                    .TryCaptureAuthoritativeLocation(handles[i]);
            }

            int batchSize = Math.Max(1, characterCheckpointBatchSize);
            while (_characterSessionRuntimeHost.HasPendingCharacterCheckpoints)
            {
                _characterSessionRuntimeHost.SaveCharacterCheckpointBatchAsync(
                        batchSize,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private async UniTaskVoid FlushCharacterCheckpointsAsync(CancellationToken cancellationToken)
        {
            _characterCheckpointInFlight = true;
            try
            {
                // Capture first on Unity's main thread while the canonical identities exist.
                var handles = new List<PlayerSessionHandle>(_playerSessionHandles.Values);
                for (int i = 0; i < handles.Count; ++i)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _characterSessionRuntimeHost.CanonicalBridge
                        .TryCaptureAuthoritativeLocation(handles[i]);
                }

                int saved = await _characterSessionRuntimeHost.SaveCharacterCheckpointBatchAsync(
                    Math.Max(1, characterCheckpointBatchSize),
                    cancellationToken);

                await UniTask.SwitchToMainThread();
                cancellationToken.ThrowIfCancellationRequested();
                if (logCharacterCheckpointBatches && saved > 0)
                {
                    Debug.Log($"[PlayerEntity] Persisted {saved} dirty character checkpoint(s) through BackendServer.");
                }
            }
            catch (OperationCanceledException)
            {
                // Normal during server shutdown.
            }
            catch (Exception ex)
            {
                try
                {
                    await UniTask.SwitchToMainThread();
                    Debug.LogException(ex);
                }
                catch
                {
                    // Shutdown may already have torn down Unity's player loop.
                }
            }
            finally
            {
                _characterCheckpointInFlight = false;
            }
        }
    }
}
