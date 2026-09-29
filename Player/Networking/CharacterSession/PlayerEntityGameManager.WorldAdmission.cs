using System;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using Game.Shared.Sessions;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace Player.Networking
{
    /// <summary>
    /// Gameplay-admission boundary.
    ///
    /// LiteNetLibGameManager remains the canonical PlayerEntity spawn/despawn owner,
    /// but Ready is deferred until the exact Character Session has authenticated,
    /// selected and loaded a character into AwaitingWorldEntry.
    /// </summary>
    public sealed partial class PlayerEntityGameManager
    {
        private bool _clientGameplayReadyArmed;
        private UniTaskCompletionSource<bool> _pendingGameplayReady;

        /// <summary>
        /// LiteNetLibGameManager normally invokes this automatically after the online
        /// scene loads. For the MMO frontend that is too early, so automatic calls are
        /// intentionally ignored until Character Select explicitly arms admission.
        /// </summary>
        public override void SendClientReady()
        {
            if (!_clientGameplayReadyArmed)
                return;

            base.SendClientReady();
        }

        /// <summary>
        /// Server-side fail-closed gate. A modified/older client cannot bypass Character
        /// Session by sending ClientReady directly because Ready is accepted only for the
        /// exact stored generation whose selected runtime is AwaitingWorldEntry.
        /// </summary>
        public override async UniTask<bool> SetPlayerReady(
            uint requestId,
            long connectionId,
            NetDataReader reader)
        {
            if (!TryGetExactCharacterSession(
                    connectionId,
                    out PlayerSessionHandle handle,
                    out PlayerSession session) ||
                !PlayerSessionWorldAdmissionPolicy.CanRequestCanonicalReady(session))
            {
                Debug.LogWarning(
                    $"[PlayerEntity] Refused Ready for connection {connectionId}: " +
                    "an authenticated, loaded Character Session is not awaiting world entry.");
                return false;
            }

            // Canonical LiteNetLib ownership remains unchanged. This is the only call in
            // this admission path that is allowed to cause the normal Ready -> SpawnPlayer flow.
            if (!await base.SetPlayerReady(requestId, connectionId, reader))
                return false;

            // SetPlayerReady can yield while processing extra ready data. Re-resolve the
            // exact generation before binding the newly spawned identity.
            if (!TryGetExactCharacterSession(connectionId, handle, out session) ||
                !PlayerSessionWorldAdmissionPolicy.CanRequestCanonicalReady(session))
            {
                RollBackCanonicalReady(connectionId);
                Debug.LogWarning(
                    $"[PlayerEntity] Rolled back Ready for connection {connectionId}: " +
                    "the exact Character Session changed during world admission.");
                return false;
            }

            if (!TryFindCanonicalPlayerIdentity(connectionId, out LiteNetLibIdentity playerIdentity))
            {
                RollBackCanonicalReady(connectionId);
                Debug.LogWarning(
                    $"[PlayerEntity] Rolled back Ready for connection {connectionId}: " +
                    "canonical PlayerEntity could not be resolved after spawn.");
                return false;
            }

            bool adopted = _characterSessionRuntimeHost.CanonicalBridge.TryAdoptSpawnedPlayer(
                handle,
                playerIdentity,
                out _);

            if (!adopted ||
                !TryGetExactCharacterSession(connectionId, handle, out session) ||
                session.State != PlayerSessionState.InWorld)
            {
                RollBackCanonicalReady(connectionId);
                Debug.LogWarning(
                    $"[PlayerEntity] Rolled back Ready for connection {connectionId}: " +
                    "Character Session could not adopt the canonical PlayerEntity.");
                return false;
            }

            _characterSessionRuntimeHost.ActivatePlayerGameplayRuntime(handle);
            return true;
        }

        /// <summary>
        /// Completes the client-side Enter Character call only after the canonical Ready
        /// request has been acknowledged by the server. A success response therefore means
        /// the server also completed Character Session adoption into InWorld.
        /// </summary>
        private async UniTask<bool> RequestGameplayReadyAsync(int millisecondsTimeout)
        {
            if (!IsClientConnected || _pendingGameplayReady != null)
                return false;

            int timeout = Math.Max(1000, millisecondsTimeout);
            var completion = new UniTaskCompletionSource<bool>();
            _pendingGameplayReady = completion;
            _clientGameplayReadyArmed = true;

            // Call the override rather than base directly so there remains one visible
            // client-side Ready gate in this manager.
            SendClientReady();

            (bool IsTimeout, bool Result) wait = await completion.Task.TimeoutWithoutException(
                TimeSpan.FromMilliseconds(timeout),
                DelayType.Realtime);

            if (ReferenceEquals(_pendingGameplayReady, completion))
                _pendingGameplayReady = null;
            _clientGameplayReadyArmed = false;

            return !wait.IsTimeout && wait.Result;
        }

        protected override void ReadExtraClientReadyResponse(
            AckResponseCode responseCode,
            EmptyMessage response,
            NetDataReader reader)
        {
            base.ReadExtraClientReadyResponse(responseCode, response, reader);
            _pendingGameplayReady?.TrySetResult(responseCode == AckResponseCode.Success);
        }

        public override void OnClientConnected()
        {
            ResetClientGameplayAdmission();
            ResetClientCharacterListCache();
            ResetClientPlayerResources();
            ResetClientPlayerStatusEffects();
            ResetClientPlayerItems();
            ResetClientProgression();
            ResetClientOwnerStateCacheAdmission();
            ResetClientWorldItems();
            ResetClientGameplayActions();
            ResetClientGameplaySettingsRequestAdmission();
            ResetClientChatRate();
            ResetClientStaffRequestAdmission();
            ResetClientSocialEconomy();
            ResetClientServiceStatus();
            base.OnClientConnected();
            RaiseClientConnectionEstablished();
        }

        public override void OnStopClient()
        {
            // Persist durable owner-visible caches before the normal client-state reset.
            PersistClientOwnerStateCaches();
            LocalClientInputGate.Reset();
            ResetClientGameplayAdmission();
            ResetClientCharacterListCache();
            ResetClientPlayerResources();
            ResetClientPlayerStatusEffects();
            ResetClientPlayerItems();
            ResetClientProgression();
            ResetClientOwnerStateCacheAdmission();
            ResetClientWorldItems();
            ResetClientGameplayActions();
            ResetClientGameplaySettingsRequestAdmission();
            ResetClientChatRate();
            ResetClientStaffRequestAdmission();
            ResetClientSocialEconomy();
            ResetClientServiceStatus();
            base.OnStopClient();
            ClientTransportStopped?.Invoke();
        }

        private void ResetClientGameplayAdmission()
        {
            _clientGameplayReadyArmed = false;
            _pendingGameplayReady?.TrySetResult(false);
            _pendingGameplayReady = null;
        }

        private void RollBackCanonicalReady(long connectionId)
        {
            if (!Players.TryGetValue(connectionId, out LiteNetLibPlayer player) ||
                player == null ||
                !player.IsReady)
            {
                return;
            }

            // Base NotReady owns canonical subscription/object cleanup. No direct
            // NetworkDestroy is introduced here.
            base.SetPlayerNotReady(connectionId, null);
        }
    }
}
