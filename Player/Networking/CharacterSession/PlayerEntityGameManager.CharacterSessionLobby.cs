using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using Game.Shared.Characters;
using Game.Shared.Identity;
using Game.Shared.Protocol;
using Game.Shared.Sessions;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        private bool _characterListRequestInFlight;
        private bool _hasCharacterListCache;
        private CharacterListResponseMessage _latestCharacterList;

        public bool HasCharacterListCache => _hasCharacterListCache && _latestCharacterList.success;
        public CharacterListResponseMessage LatestCharacterList => _latestCharacterList;

        protected override void RegisterMessages()
        {
            base.RegisterMessages();
            RegisterRequestToServer<CharacterListRequestMessage, CharacterListResponseMessage>(
                CharacterSessionRequestTypes.CharacterList,
                HandleCharacterListRequest);
            RegisterRequestToServer<EnterCharacterRequestMessage, EnterCharacterResponseMessage>(
                CharacterSessionRequestTypes.EnterCharacter,
                HandleEnterCharacterRequest);
            RegisterRequestToServer<AdmissionAuthenticationRequestMessage, AdmissionAuthenticationResponseMessage>(
                CharacterSessionRequestTypes.AuthenticateAdmission,
                HandleAuthenticateAdmissionRequest);
            RegisterRequestToServer<CreateCharacterRequestMessage, CreateCharacterResponseMessage>(
                CharacterSessionRequestTypes.CreateCharacter,
                HandleCreateCharacterRequest);
            // Unity is client-only in production. Register the response codec without
            // introducing a second authoritative delete handler into the staged Unity
            // server bridge; standalone GameServer owns Character.Delete.
            Client.RegisterResponseHandler<DeleteCharacterRequestMessage, DeleteCharacterResponseMessage>(
                CharacterSessionRequestTypes.DeleteCharacter);
            RegisterPlayerItemMessages();
            RegisterWorldItemMessages();
            RegisterPlayerResourceMessages();
            RegisterProgressionMessages();
            RegisterCraftingMessages();
            RegisterPlayerStatusEffectMessages();
            RegisterPlayerGameplayActionMessages();
            RegisterPlayerChatMessages();
            RegisterPlayerServiceStatusMessages();
            RegisterGameplaySettingsMessages();
            RegisterStaffMessages();
            RegisterSocialEconomyMessages();
            RegisterGuildMessages();
        }

        public async UniTask<AdmissionAuthenticationResponseMessage> RequestAuthenticateAdmissionAsync(
            string admissionToken,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return AdmissionAuthenticationResponseMessage.Failed(0, "client is not connected");
            if (string.IsNullOrWhiteSpace(admissionToken))
                return AdmissionAuthenticationResponseMessage.Failed(0, "admission token is missing");

            AsyncResponseData<AdmissionAuthenticationResponseMessage> response =
                await ClientSendRequestAsync<AdmissionAuthenticationRequestMessage, AdmissionAuthenticationResponseMessage>(
                    CharacterSessionRequestTypes.AuthenticateAdmission,
                    new AdmissionAuthenticationRequestMessage
                    {
                        admissionToken = admissionToken,
                    },
                    millisecondsTimeout);

            if (!response.IsSuccess)
            {
                return AdmissionAuthenticationResponseMessage.Failed(
                    0,
                    $"admission authentication request failed: {response.ResponseCode}");
            }

            AdmissionAuthenticationResponseMessage result = response.Response;
            if (result.success)
                ResetClientCharacterListCache();
            return result;
        }

        private async UniTaskVoid HandleAuthenticateAdmissionRequest(
            RequestHandlerData requestHandler,
            AdmissionAuthenticationRequestMessage request,
            RequestProceedResultDelegate<AdmissionAuthenticationResponseMessage> result)
        {
            long connectionId = requestHandler.ConnectionId;
            if (!TryGetExactCharacterSession(
                    connectionId,
                    out PlayerSessionHandle handle,
                    out PlayerSession session))
            {
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)PlayerSessionState.Closed,
                    "character session not found"));
                return;
            }

            // Idempotent response-loss/retry handling. Once this exact session
            // generation already owns an AccountId, a duplicate admission request from
            // the same transport session is success; do not attempt to redeem the
            // one-time token again.
            if (session.HasAccount &&
                session.State != PlayerSessionState.Disconnecting &&
                session.State != PlayerSessionState.Closed)
            {
                CompleteAuthenticationAdmission(handle);
                long rosterRevision = await ResolveCharacterRosterRevisionAsync(handle);
                await UniTask.SwitchToMainThread();
                if (!TryGetExactCharacterSession(connectionId, handle, out session))
                {
                    result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                        (byte)PlayerSessionState.Closed,
                        "character session changed during roster reconciliation"));
                    return;
                }

                result(AckResponseCode.Success, new AdmissionAuthenticationResponseMessage
                {
                    success = true,
                    accountId = session.AccountId.Value,
                    sessionState = (byte)session.State,
                    error = string.Empty,
                    rosterRevision = rosterRevision,
                });
                return;
            }

            if (session.State != PlayerSessionState.Connected)
            {
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)session.State,
                    "session is not ready for authentication"));
                return;
            }

            if (!TryBeginAuthenticationAdmissionAttempt(handle))
            {
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)session.State,
                    "authentication unavailable"));
                KickClient(connectionId, Array.Empty<byte>()).Forget();
                return;
            }

            if (!_characterSessionRuntimeHost.SessionService.BeginAuthentication(handle))
            {
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)session.State,
                    "authentication could not begin"));
                return;
            }

            AccountId accountId;
            try
            {
                accountId = await _characterSessionRuntimeHost.RedeemAdmissionAsync(
                    request.admissionToken,
                    CancellationToken.None);
                await UniTask.SwitchToMainThread();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                _characterSessionRuntimeHost.SessionService.CancelAuthentication(handle);
                TryGetExactCharacterSession(connectionId, handle, out session);
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)(session != null ? session.State : PlayerSessionState.Closed),
                    "authentication unavailable"));
                return;
            }

            if (!accountId.IsValid)
            {
                _characterSessionRuntimeHost.SessionService.CancelAuthentication(handle);
                TryGetExactCharacterSession(connectionId, handle, out session);
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)(session != null ? session.State : PlayerSessionState.Closed),
                    "authentication unavailable"));
                return;
            }

            if (!TryGetExactCharacterSession(connectionId, handle, out session))
            {
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)PlayerSessionState.Closed,
                    "character session changed during authentication"));
                return;
            }

            if (_characterSessionRuntimeHost.Sessions.ContainsAccount(accountId))
            {
                _characterSessionRuntimeHost.SessionService.CancelAuthentication(handle);
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)PlayerSessionState.Connected,
                    "account session already active"));
                return;
            }

            if (!_characterSessionRuntimeHost.SessionService.CompleteAuthentication(
                    handle,
                    accountId) ||
                !TryGetExactCharacterSession(connectionId, handle, out session))
            {
                _characterSessionRuntimeHost.SessionService.CancelAuthentication(handle);
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)(session != null ? session.State : PlayerSessionState.Closed),
                    "authentication session commit failed"));
                return;
            }

            CompleteAuthenticationAdmission(handle);

            long authoritativeRosterRevision = await ResolveCharacterRosterRevisionAsync(handle);
            await UniTask.SwitchToMainThread();
            if (!TryGetExactCharacterSession(connectionId, handle, out session))
            {
                result(AckResponseCode.Success, AdmissionAuthenticationResponseMessage.Failed(
                    (byte)PlayerSessionState.Closed,
                    "character session changed during roster reconciliation"));
                return;
            }

            result(AckResponseCode.Success, new AdmissionAuthenticationResponseMessage
            {
                success = true,
                accountId = accountId.Value,
                sessionState = (byte)session.State,
                error = string.Empty,
                rosterRevision = authoritativeRosterRevision,
            });
        }

        private async UniTask<long> ResolveCharacterRosterRevisionAsync(PlayerSessionHandle handle)
        {
            try
            {
                CharacterListResult list = await _characterSessionRuntimeHost.SessionService
                    .GetCharacterListAsync(handle, CancellationToken.None);

                if (!list.Success)
                    return 0L;

                int count = Math.Min(list.Characters.Length, CharacterListResponseMessage.MaxCharacters);
                var characters = new CharacterSessionCharacterSummary[count];
                for (int i = 0; i < count; ++i)
                {
                    CharacterSummary summary = list.Characters[i];
                    characters[i] = new CharacterSessionCharacterSummary(
                        summary.CharacterId.Value,
                        summary.Name,
                        summary.MapId);
                }

                return CharacterRosterStateRevision.Compute(characters);
            }
            catch
            {
                // Reconciliation is an optimization/safety check, not an admission dependency.
                // Zero makes compatible clients perform one authoritative CharacterList recovery.
                return 0L;
            }
        }

        /// <summary>
        /// Client-facing Character Lobby request. The UI talks to this transport boundary,
        /// never to server/domain services directly.
        /// </summary>
        public UniTask<CharacterListResponseMessage> RequestCharacterListAsync(
            int millisecondsTimeout = 10000) =>
            RequestCharacterListAsync(false, millisecondsTimeout);

        public async UniTask<CharacterListResponseMessage> RequestCharacterListAsync(
            bool forceRefresh,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return CharacterListResponseMessage.Failed(0, "client is not connected");

            if (!forceRefresh && HasCharacterListCache)
                return _latestCharacterList;

            if (_characterListRequestInFlight)
                return HasCharacterListCache
                    ? _latestCharacterList
                    : CharacterListResponseMessage.Failed(0, "character list request is already pending locally");

            _characterListRequestInFlight = true;
            try
            {
                AsyncResponseData<CharacterListResponseMessage> response =
                    await ClientSendRequestAsync<CharacterListRequestMessage, CharacterListResponseMessage>(
                        CharacterSessionRequestTypes.CharacterList,
                        new CharacterListRequestMessage(),
                        millisecondsTimeout);

                if (!response.IsSuccess)
                    return CharacterListResponseMessage.Failed(0, $"character list request failed: {response.ResponseCode}");

                CharacterListResponseMessage result = response.Response;
                if (result.success)
                {
                    _latestCharacterList = result;
                    _hasCharacterListCache = true;
                }
                return result;
            }
            finally
            {
                _characterListRequestInFlight = false;
            }
        }

        private void InvalidateCharacterListCache()
        {
            _latestCharacterList = default;
            _hasCharacterListCache = false;
        }

        private void ResetClientCharacterListCache()
        {
            InvalidateCharacterListCache();
            _characterListRequestInFlight = false;
        }

        public UniTask<CreateCharacterResponseMessage> RequestCreateCharacterAsync(
            string name,
            int millisecondsTimeout = 10000) =>
            RequestCreateCharacterAsync(name, CharacterAppearanceRecipe.CreateDefault(), CharacterPresentationPreferences.CreateDefault(), millisecondsTimeout);

        public async UniTask<CreateCharacterResponseMessage> RequestCreateCharacterAsync(
            string name,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
            {
                return CreateCharacterResponseMessage.Failed(
                    0,
                    (byte)CharacterCreateFailure.SessionNotFound,
                    "client is not connected");
            }

            CharacterAppearanceRecipe appearance = initialAppearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            if (!appearance.IsValid(out _))
                appearance = CharacterAppearanceRecipe.CreateDefault();

            CharacterPresentationPreferences presentation =
                initialPresentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            if (!presentation.IsValid(out _))
                presentation = CharacterPresentationPreferences.CreateDefault();

            AsyncResponseData<CreateCharacterResponseMessage> response =
                await ClientSendRequestAsync<CreateCharacterRequestMessage, CreateCharacterResponseMessage>(
                    CharacterSessionRequestTypes.CreateCharacter,
                    new CreateCharacterRequestMessage
                    {
                        name = name ?? string.Empty,
                        initialAppearance = appearance,
                        initialPresentation = presentation,
                    },
                    millisecondsTimeout);

            if (!response.IsSuccess)
            {
                return CreateCharacterResponseMessage.Failed(
                    0,
                    (byte)CharacterCreateFailure.PersistenceFailed,
                    $"character creation request failed: {response.ResponseCode}");
            }

            CreateCharacterResponseMessage result = response.Response;
            if (result.success)
                InvalidateCharacterListCache();
            return result;
        }

        public async UniTask<DeleteCharacterResponseMessage> RequestDeleteCharacterAsync(
            long characterId,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
            {
                return DeleteCharacterResponseMessage.Failed(
                    characterId,
                    0,
                    (byte)CharacterDeleteFailure.SessionNotFound,
                    "client is not connected");
            }
            if (characterId <= 0)
            {
                return DeleteCharacterResponseMessage.Failed(
                    characterId,
                    0,
                    (byte)CharacterDeleteFailure.InvalidCharacter,
                    "character id is invalid");
            }

            AsyncResponseData<DeleteCharacterResponseMessage> response =
                await ClientSendRequestAsync<DeleteCharacterRequestMessage, DeleteCharacterResponseMessage>(
                    CharacterSessionRequestTypes.DeleteCharacter,
                    new DeleteCharacterRequestMessage { characterId = characterId },
                    millisecondsTimeout);

            if (!response.IsSuccess)
            {
                return DeleteCharacterResponseMessage.Failed(
                    characterId,
                    0,
                    (byte)CharacterDeleteFailure.PersistenceFailed,
                    $"character deletion request failed: {response.ResponseCode}");
            }

            DeleteCharacterResponseMessage result = response.Response;
            if (result.success && HasCharacterListCache)
            {
                CharacterSessionCharacterSummary[] source =
                    _latestCharacterList.characters ?? Array.Empty<CharacterSessionCharacterSummary>();
                int index = Array.FindIndex(source, c => c.characterId == characterId);
                if (index >= 0)
                {
                    var next = new CharacterSessionCharacterSummary[source.Length - 1];
                    if (index > 0)
                        Array.Copy(source, 0, next, 0, index);
                    if (index + 1 < source.Length)
                        Array.Copy(source, index + 1, next, index, source.Length - index - 1);
                    _latestCharacterList.characters = next;
                }
            }
            return result;
        }


        /// <summary>
        /// Unity-hosted bridge for rare PlayerEntity appearance publication. The standalone
        /// GameServer reads the same state directly from PlayerRuntime. Presentation assets
        /// never cross this boundary; only the compact generic recipe does.
        /// </summary>
        public bool TryGetAuthoritativeCharacterAppearance(
            long connectionId,
            out long characterId,
            out string displayName,
            out CharacterAppearanceRecipe appearance,
            out CharacterPresentationPreferences presentation)
        {
            characterId = 0;
            displayName = string.Empty;
            appearance = CharacterAppearanceRecipe.CreateDefault();
            presentation = CharacterPresentationPreferences.CreateDefault();

            if (_characterSessionRuntimeHost == null ||
                !TryGetExactCharacterSession(
                    connectionId,
                    out PlayerSessionHandle handle,
                    out _))
            {
                return false;
            }

            return _characterSessionRuntimeHost.TryGetAuthoritativeCharacterAppearance(
                handle,
                out characterId,
                out displayName,
                out appearance,
                out presentation);
        }

        private async UniTaskVoid HandleCreateCharacterRequest(
            RequestHandlerData requestHandler,
            CreateCharacterRequestMessage request,
            RequestProceedResultDelegate<CreateCharacterResponseMessage> result)
        {
            long connectionId = requestHandler.ConnectionId;
            if (!TryGetExactCharacterSession(
                    connectionId,
                    out PlayerSessionHandle handle,
                    out PlayerSession session))
            {
                result(AckResponseCode.Success, CreateCharacterResponseMessage.Failed(
                    (byte)PlayerSessionState.Closed,
                    (byte)CharacterCreateFailure.SessionNotFound,
                    "character session not found"));
                return;
            }

            if (session.State != PlayerSessionState.CharacterLobby)
            {
                result(AckResponseCode.Success, CreateCharacterResponseMessage.Failed(
                    (byte)session.State,
                    (byte)CharacterCreateFailure.InvalidSessionState,
                    "session is not in character lobby"));
                return;
            }

            try
            {
                CharacterCreateResult created = await _characterSessionRuntimeHost
                    .CreateCharacterAsync(
                        handle,
                        request.name,
                        request.initialAppearance,
                        request.initialPresentation,
                        CancellationToken.None);
                await UniTask.SwitchToMainThread();

                if (!TryGetExactCharacterSession(connectionId, handle, out session))
                {
                    result(AckResponseCode.Success, CreateCharacterResponseMessage.Failed(
                        (byte)PlayerSessionState.Closed,
                        (byte)CharacterCreateFailure.SessionChangedDuringCreate,
                        "character session changed during creation"));
                    return;
                }

                if (!created.Success)
                {
                    result(AckResponseCode.Success, CreateCharacterResponseMessage.Failed(
                        (byte)session.State,
                        (byte)created.Failure,
                        CharacterCreateFailureText(created.Failure)));
                    return;
                }

                result(AckResponseCode.Success, new CreateCharacterResponseMessage
                {
                    success = true,
                    characterId = created.CharacterId.Value,
                    name = created.Name,
                    sessionState = (byte)session.State,
                    failure = (byte)CharacterCreateFailure.None,
                    error = string.Empty,
                });
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                TryGetExactCharacterSession(connectionId, handle, out session);
                result(AckResponseCode.Success, CreateCharacterResponseMessage.Failed(
                    (byte)(session != null ? session.State : PlayerSessionState.Closed),
                    (byte)CharacterCreateFailure.PersistenceFailed,
                    "character creation failed"));
            }
        }

        /// <summary>
        /// Selects/loads a character on the authoritative session, then asks LiteNetLib's
        /// canonical Ready path to admit/spawn the gameplay PlayerEntity. The server-side
        /// Ready gate independently requires this exact session to be AwaitingWorldEntry.
        /// </summary>
        public async UniTask<EnterCharacterResponseMessage> RequestEnterCharacterAsync(
            long characterId,
            int millisecondsTimeout = 30000)
        {
            if (!IsClientConnected)
                return EnterCharacterResponseMessage.Failed(characterId, 0, 0, "client is not connected");
            if (characterId <= 0)
                return EnterCharacterResponseMessage.Failed(characterId, 0, 0, "character id is invalid");

            AsyncResponseData<EnterCharacterResponseMessage> response =
                await ClientSendRequestAsync<EnterCharacterRequestMessage, EnterCharacterResponseMessage>(
                    CharacterSessionRequestTypes.EnterCharacter,
                    new EnterCharacterRequestMessage { characterId = characterId },
                    millisecondsTimeout);

            if (!response.IsSuccess)
            {
                return EnterCharacterResponseMessage.Failed(
                    characterId,
                    0,
                    0,
                    $"enter character request failed: {response.ResponseCode}");
            }

            EnterCharacterResponseMessage enter = response.Response;
            if (!enter.success || enter.worldAdopted)
                return enter;

            if ((PlayerSessionState)enter.sessionState != PlayerSessionState.AwaitingWorldEntry)
                return enter;

            // Warm clients validate the persistent public Settings catalog before Ready.
            // Cold/stale clients deliberately fall through to the existing server-pushed
            // Settings baseline, preserving the canonical admission ordering.
            await PrepareGameplaySettingsCacheForAdmissionAsync(millisecondsTimeout);

            // Restore the existing persistent owner-local caches before Ready, then reuse
            // their existing revision probes. Friends membership, Player Items, and
            // Progression remain non-authoritative client caches: the standalone GameServer
            // independently validates their revisions and sends the authoritative baseline
            // whenever a cache is missing or stale.
            await PrepareOwnerStateCachesForAdmissionAsync(characterId, millisecondsTimeout);

            // Selection/loading completed first. Only now arm the client Ready request.
            // The server independently validates the exact session generation before its
            // canonical SetPlayerReady -> SpawnPlayer path may run.
            bool admitted = await RequestGameplayReadyAsync(millisecondsTimeout);
            if (!admitted)
            {
                return EnterCharacterResponseMessage.Failed(
                    characterId,
                    (byte)PlayerSessionState.AwaitingWorldEntry,
                    (byte)CharacterSelectFailure.InvalidSessionState,
                    "gameplay admission failed; retry Enter");
            }

            enter.worldAdopted = true;
            enter.sessionState = (byte)PlayerSessionState.InWorld;
            enter.error = string.Empty;
            RaiseClientWorldEntered(characterId);

            // The standalone GameServer pushes settings/items/resources/status/combat owner
            // baselines once after Ready. Snapshot requests remain reconciliation-only. Keep
            // the legacy Unity Host path self-contained because it does not run that standalone
            // push routine.
            if (IsServer)
            {
                RequestGameplaySettingsAsync(Math.Min(millisecondsTimeout, 10000)).Forget();
                RequestPlayerItemsAsync(Math.Min(millisecondsTimeout, 10000)).Forget();
                RequestPlayerResourcesAsync(Math.Min(millisecondsTimeout, 10000)).Forget();
                RequestPlayerStatusEffectsAsync(Math.Min(millisecondsTimeout, 10000)).Forget();
                RequestCombatOwnerStateAsync(Math.Min(millisecondsTimeout, 10000)).Forget();
            }
            return enter;
        }

        private async UniTaskVoid HandleCharacterListRequest(
            RequestHandlerData requestHandler,
            CharacterListRequestMessage request,
            RequestProceedResultDelegate<CharacterListResponseMessage> result)
        {
            long connectionId = requestHandler.ConnectionId;

            if (!TryGetExactCharacterSession(connectionId, out PlayerSessionHandle handle, out PlayerSession session))
            {
                result(AckResponseCode.Success, CharacterListResponseMessage.Failed(
                    (byte)PlayerSessionState.Closed,
                    "character session not found"));
                return;
            }

            try
            {
                CharacterListResult list = await _characterSessionRuntimeHost.SessionService
                    .GetCharacterListAsync(handle, CancellationToken.None);
                await UniTask.SwitchToMainThread();

                if (!TryGetExactCharacterSession(connectionId, handle, out session))
                {
                    result(AckResponseCode.Success, CharacterListResponseMessage.Failed(
                        (byte)PlayerSessionState.Closed,
                        "character session changed while loading the character list"));
                    return;
                }

                if (!list.Success)
                {
                    result(AckResponseCode.Success, CharacterListResponseMessage.Failed(
                        (byte)session.State,
                        list.Error));
                    return;
                }

                int count = Math.Min(list.Characters.Length, CharacterListResponseMessage.MaxCharacters);
                var characters = new CharacterSessionCharacterSummary[count];
                for (int i = 0; i < count; ++i)
                {
                    CharacterSummary summary = list.Characters[i];
                    characters[i] = new CharacterSessionCharacterSummary(
                        summary.CharacterId.Value,
                        summary.Name,
                        summary.MapId);
                }

                result(AckResponseCode.Success, new CharacterListResponseMessage
                {
                    success = true,
                    sessionState = (byte)session.State,
                    error = list.Characters.Length > count
                        ? $"showing first {count} characters"
                        : string.Empty,
                    characters = characters,
                });
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                result(AckResponseCode.Success, CharacterListResponseMessage.Failed(
                    (byte)session.State,
                    "character list load failed"));
            }
        }

        private async UniTaskVoid HandleEnterCharacterRequest(
            RequestHandlerData requestHandler,
            EnterCharacterRequestMessage request,
            RequestProceedResultDelegate<EnterCharacterResponseMessage> result)
        {
            long connectionId = requestHandler.ConnectionId;

            if (request.characterId <= 0)
            {
                result(AckResponseCode.Success, EnterCharacterResponseMessage.Failed(
                    request.characterId,
                    (byte)PlayerSessionState.Closed,
                    (byte)CharacterSelectFailure.InvalidSessionState,
                    "character id is invalid"));
                return;
            }

            if (!TryGetExactCharacterSession(
                    connectionId,
                    out PlayerSessionHandle handle,
                    out PlayerSession session))
            {
                result(AckResponseCode.Success, EnterCharacterResponseMessage.Failed(
                    request.characterId,
                    (byte)PlayerSessionState.Closed,
                    (byte)CharacterSelectFailure.SessionNotFound,
                    "character session not found"));
                return;
            }

            CharacterId characterId = new CharacterId(request.characterId);

            try
            {
                // The request is deliberately idempotent across the small selection/Ready
                // timing window. If persistence already loaded this same character and the
                // session is AwaitingWorldEntry, retry only gameplay admission; never load it twice.
                if (session.State == PlayerSessionState.CharacterLobby)
                {
                    CharacterSelectResult selected = await _characterSessionRuntimeHost.SessionService
                        .SelectCharacterAsync(handle, characterId, CancellationToken.None);
                    await UniTask.SwitchToMainThread();

                    if (!selected.Success)
                    {
                        TryGetExactCharacterSession(connectionId, handle, out session);
                        result(AckResponseCode.Success, EnterCharacterResponseMessage.Failed(
                            request.characterId,
                            (byte)(session != null ? session.State : PlayerSessionState.Closed),
                            (byte)selected.Failure,
                            CharacterSelectFailureText(selected.Failure)));
                        return;
                    }
                }

                if (!TryGetExactCharacterSession(connectionId, handle, out session))
                {
                    result(AckResponseCode.Success, EnterCharacterResponseMessage.Failed(
                        request.characterId,
                        (byte)PlayerSessionState.Closed,
                        (byte)CharacterSelectFailure.SessionChangedDuringLoad,
                        "character session changed while loading"));
                    return;
                }

                if (!session.HasSelectedCharacter || session.SelectedCharacterId != characterId)
                {
                    result(AckResponseCode.Success, EnterCharacterResponseMessage.Failed(
                        request.characterId,
                        (byte)session.State,
                        (byte)CharacterSelectFailure.InvalidSessionState,
                        "session is not waiting for this character"));
                    return;
                }

                if (session.State == PlayerSessionState.InWorld)
                {
                    result(AckResponseCode.Success, new EnterCharacterResponseMessage
                    {
                        success = true,
                        worldAdopted = true,
                        characterId = request.characterId,
                        sessionState = (byte)session.State,
                        failure = (byte)CharacterSelectFailure.None,
                        error = string.Empty,
                    });
                    return;
                }

                if (session.State != PlayerSessionState.AwaitingWorldEntry)
                {
                    result(AckResponseCode.Success, EnterCharacterResponseMessage.Failed(
                        request.characterId,
                        (byte)session.State,
                        (byte)CharacterSelectFailure.InvalidSessionState,
                        "character session is not awaiting world entry"));
                    return;
                }

                // Character selection owns persistence and session preparation; it does not
                // own network spawning. The client now sends canonical Ready, and SetPlayerReady
                // performs the independent server admission gate plus post-spawn Character
                // Session adoption.
                result(AckResponseCode.Success, new EnterCharacterResponseMessage
                {
                    success = true,
                    worldAdopted = false,
                    characterId = request.characterId,
                    sessionState = (byte)session.State,
                    failure = (byte)CharacterSelectFailure.None,
                    error = string.Empty,
                });
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                TryGetExactCharacterSession(connectionId, handle, out session);
                result(AckResponseCode.Success, EnterCharacterResponseMessage.Failed(
                    request.characterId,
                    (byte)(session != null ? session.State : PlayerSessionState.Closed),
                    (byte)CharacterSelectFailure.CharacterLoadFailed,
                    "character enter failed"));
            }
        }

        private bool TryGetExactCharacterSession(
            long connectionId,
            out PlayerSessionHandle handle,
            out PlayerSession session)
        {
            handle = default(PlayerSessionHandle);
            session = null;

            if (!IsServer ||
                _characterSessionRuntimeHost == null ||
                !_playerSessionHandles.TryGetValue(connectionId, out handle))
            {
                return false;
            }

            return _characterSessionRuntimeHost.SessionService.TryGetSession(handle, out session);
        }

        private bool TryGetExactCharacterSession(
            long connectionId,
            PlayerSessionHandle expected,
            out PlayerSession session)
        {
            session = null;
            if (!expected.IsValid ||
                !_playerSessionHandles.TryGetValue(connectionId, out PlayerSessionHandle current) ||
                current != expected ||
                _characterSessionRuntimeHost == null)
            {
                return false;
            }

            return _characterSessionRuntimeHost.SessionService.TryGetSession(expected, out session);
        }

        private bool TryFindCanonicalPlayerIdentity(long connectionId, out LiteNetLibIdentity identity)
        {
            identity = null;
            if (!Players.TryGetValue(connectionId, out LiteNetLibPlayer player) || player == null)
                return false;

            var spawned = player.GetSpawnedObjects();
            while (spawned.MoveNext())
            {
                LiteNetLibIdentity candidate = spawned.Current.Value;
                if (candidate == null || candidate.ConnectionId != connectionId)
                    continue;
                if (candidate.GetComponent<PlayerEntityNetwork>() == null)
                    continue;

                identity = candidate;
                return true;
            }

            return false;
        }

        private static string CharacterCreateFailureText(CharacterCreateFailure failure)
        {
            switch (failure)
            {
                case CharacterCreateFailure.SessionNotFound:
                    return "character session not found";
                case CharacterCreateFailure.InvalidSessionState:
                    return "session is not in character lobby";
                case CharacterCreateFailure.InvalidName:
                    return "name must be 1-16 letters with optional single spaces";
                case CharacterCreateFailure.InvalidAppearance:
                    return "character appearance data is invalid";
                case CharacterCreateFailure.InvalidPresentation:
                    return "character movement presentation data is invalid";
                case CharacterCreateFailure.NameAlreadyExists:
                    return "character name already exists";
                case CharacterCreateFailure.CharacterLimitReached:
                    return "character limit reached";
                case CharacterCreateFailure.SessionChangedDuringCreate:
                    return "character session changed during creation";
                default:
                    return "character creation failed";
            }
        }

        private static string CharacterSelectFailureText(CharacterSelectFailure failure)
        {
            switch (failure)
            {
                case CharacterSelectFailure.SessionNotFound:
                    return "character session not found";
                case CharacterSelectFailure.InvalidSessionState:
                    return "character session is not in the character lobby";
                case CharacterSelectFailure.CharacterAlreadyActive:
                    return "character is already active";
                case CharacterSelectFailure.CharacterNotFoundOrNotOwned:
                    return "character was not found for this account";
                case CharacterSelectFailure.CharacterDataInvalid:
                    return "character data is invalid";
                case CharacterSelectFailure.CharacterLoadFailed:
                    return "character load failed";
                case CharacterSelectFailure.SessionChangedDuringLoad:
                    return "character session changed during load";
                default:
                    return "character selection failed";
            }
        }
    }
}
