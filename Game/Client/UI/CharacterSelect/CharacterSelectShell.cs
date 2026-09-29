using System;
using Cysharp.Threading.Tasks;
using Game.Client.Networking;
using Game.Client.Presentation.Characters;
using Game.Client.UI.CharacterCreation;
using Game.Client.UI.Root;
using Game.Shared.Backend;
using Game.Shared.Authentication;
using Game.Shared.Characters;
using Game.Shared.Sessions;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

#pragma warning disable 0414 // Serialized client-only presentation fields are intentionally unused in UNITY_SERVER builds.

namespace Game.Client.UI.CharacterSelect
{
    /// <summary>
    /// Editable frontend shell for connection -> account login/create -> character
    /// create/select -> world entry. It talks only to Player.Networking and shared
    /// contracts; no server/application types cross into client presentation.
    /// </summary>
    public sealed class CharacterSelectShell : MonoBehaviour
    {
        [Header("Frontend Panels")]
        [SerializeField] private GameObject connectionPanel;
        [SerializeField] private GameObject loginPanel;
        [SerializeField] private GameObject characterPanel;
        [SerializeField] private bool useFrontendConnectionPanel = true;

        [Header("Connection")]
        [SerializeField] private InputField addressInput;
        [SerializeField] private InputField portInput;
        [SerializeField] private Text connectionStatusText;
        [SerializeField] private Button startClientButton;

        [Header("Account")]
        [SerializeField] private InputField accountInput;
        [SerializeField] private InputField passwordInput;
        [SerializeField] private Text loginStatusText;
        [SerializeField] private Button loginButton;
        [SerializeField] private Button createAccountButton;
        [SerializeField] private Button loginDisconnectButton;
        [SerializeField] private Button quitButton;
        [SerializeField] private Toggle rememberAccountToggle;
        [SerializeField] private Text serverAvailabilityText;
        [SerializeField] private Color serverOnlineColor;
        [SerializeField] private Color serverCheckingColor;
        [SerializeField] private Color serverOfflineColor;
        [SerializeField] private bool autoConnectConfiguredServer = true;

        [Header("Character Lobby")]
        [SerializeField] private Text statusText;
        [SerializeField] private InputField characterNameInput;
        [SerializeField] private Button createCharacterButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button enterWorldButton;
        [SerializeField] private Button deleteCharacterButton;
        [SerializeField] private Button disconnectButton;
        [SerializeField] private CharacterSelectSlotView[] slots;
        [SerializeField] private CharacterSelectPreviewController previewController;
        [SerializeField] private bool hideAfterWorldEntry = true;

        private PlayerEntityGameManager _manager;
        [SerializeField] private CharacterCreatorShell _characterCreator;
        private ClientPopupController _popup;
        private Canvas _canvas;
        private GraphicRaycaster _raycaster;
        private MMOTestbedRuntimePanel _testPanel;
        private long _selectedCharacterId;
        private CharacterSessionCharacterSummary[] _lastCharacters = Array.Empty<CharacterSessionCharacterSummary>();
        private bool _requestInFlight;
        private bool _wasConnected;
        private bool _authenticated;
        private bool _worldEntered;
        private bool _defaultsCopiedFromManager;
        private bool _testPanelSuppressed;
        private bool _testPanelWasEnabled;
        private bool _clientConnectionAttemptPending;
        private bool _configuredClientStartPending;
        private System.Threading.CancellationTokenSource _connectionAttemptCancellation;
        private System.Threading.CancellationTokenSource _availabilityMonitorCancellation;
        private bool _availabilityCheckInFlight;
        private bool _serverAvailabilityKnown;
        private bool _serverAvailable;
        private double _nextManualAvailabilityRefreshAt;
        private double _nextManualRosterRecoveryAt;
        private long _authenticatedAccountId;
        private string _authenticatedServerScope = string.Empty;

        private const double ClientConnectionAttemptTimeoutSeconds = 8.0;
        private const double AutomaticAvailabilityIntervalSeconds = 300.0;
        private const double ManualAvailabilityCooldownSeconds = 60.0;
        private const string RememberAccountEnabledKey = "MMO.Frontend.RememberAccount";
        private const string RememberAccountNameKey = "MMO.Frontend.RememberAccountName";

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            _canvas = GetComponent<Canvas>();
            _raycaster = GetComponent<GraphicRaycaster>();
            EnsureAccountConvenienceSurface();
            EnsureDeleteCharacterSurface();
            LoadRememberedAccount();

            startClientButton?.onClick.AddListener(OnStartClientClicked);
            loginButton?.onClick.AddListener(OnLoginClicked);
            createAccountButton?.onClick.AddListener(OnCreateAccountClicked);
            loginDisconnectButton?.onClick.AddListener(OnDisconnectClicked);
            quitButton?.onClick.AddListener(OnQuitClicked);
            rememberAccountToggle?.onValueChanged.AddListener(OnRememberAccountChanged);
            createCharacterButton?.onClick.AddListener(OnCreateCharacterClicked);
            refreshButton?.onClick.AddListener(OnRefreshClicked);
            enterWorldButton?.onClick.AddListener(OnEnterWorldClicked);
            deleteCharacterButton?.onClick.AddListener(OnDeleteCharacterClicked);
            disconnectButton?.onClick.AddListener(OnDisconnectClicked);
            addressInput?.onValueChanged.AddListener(OnFrontendInputChanged);
            portInput?.onValueChanged.AddListener(OnFrontendInputChanged);
            accountInput?.onValueChanged.AddListener(OnFrontendInputChanged);
            passwordInput?.onValueChanged.AddListener(OnFrontendInputChanged);
            characterNameInput?.onValueChanged.AddListener(OnFrontendInputChanged);
            EnsurePreviewPresentationSurface();
            EnsureCharacterSlotIntegrity();

            // Character Creator remains a compatibility fallback until its own authored
            // prefab conversion. Do not construct it during normal frontend startup; only
            // resolve/build it if the player actually chooses Create Character.

            // Player-facing roster refresh/recovery was retired. If an older prefab still
            // has the legacy serialized control, disable it instead of restyling/reusing it.
            // The normal Character Select path is local-cache first; cache reconstruction
            // remains an automatic authenticated recovery path.
            if (refreshButton != null)
            {
                Debug.LogWarning(
                    "[FrontendUI] Legacy Character Select refresh/recovery button is still authored. " +
                    "Remove it from ClientUIRoot.prefab; disabling it at runtime as a compatibility fallback.",
                    refreshButton);
                refreshButton.gameObject.SetActive(false);
            }
            PlayerEntityNetwork.OwnerAppearanceObserved -= OnOwnerAppearanceObserved;
            PlayerEntityNetwork.OwnerAppearanceObserved += OnOwnerAppearanceObserved;
            PlayerEntityNetwork.OwnerPresentationObserved -= OnOwnerPresentationObserved;
            PlayerEntityNetwork.OwnerPresentationObserved += OnOwnerPresentationObserved;
            PlayerEntityNetwork.OwnerEquipmentVisualsObserved -= OnOwnerEquipmentVisualsObserved;
            PlayerEntityNetwork.OwnerEquipmentVisualsObserved += OnOwnerEquipmentVisualsObserved;

            ClearSlots();
            SetConnectionStatus("Ready to connect.");
            SetServerAvailability(false, true);
            SetLoginStatus(autoConnectConfiguredServer ? "Checking server availability..." : "Choose a server, then sign in or create an account.");
            SetCharacterStatus("Waiting for authentication...");
            ApplyFrontendState();
#endif
        }

        private void Start()
        {
#if !UNITY_SERVER
            BindManager();
#endif
        }

        private void OnDisable() => RestoreTestPanel();

        private void OnDestroy()
        {
            CancelConnectionAttempt();
            StopAvailabilityMonitor();
            UnbindManager();
            RestoreTestPanel();
#if !UNITY_SERVER
            PlayerEntityNetwork.OwnerAppearanceObserved -= OnOwnerAppearanceObserved;
            PlayerEntityNetwork.OwnerPresentationObserved -= OnOwnerPresentationObserved;
            PlayerEntityNetwork.OwnerEquipmentVisualsObserved -= OnOwnerEquipmentVisualsObserved;
            if (_characterCreator != null)
            {
                _characterCreator.Cancelled -= OnCharacterCreatorCancelled;
                _characterCreator.CreateRequested -= OnCharacterCreatorCreateRequested;
            }
            startClientButton?.onClick.RemoveListener(OnStartClientClicked);
            loginButton?.onClick.RemoveListener(OnLoginClicked);
            createAccountButton?.onClick.RemoveListener(OnCreateAccountClicked);
            loginDisconnectButton?.onClick.RemoveListener(OnDisconnectClicked);
            quitButton?.onClick.RemoveListener(OnQuitClicked);
            rememberAccountToggle?.onValueChanged.RemoveListener(OnRememberAccountChanged);
            createCharacterButton?.onClick.RemoveListener(OnCreateCharacterClicked);
            refreshButton?.onClick.RemoveListener(OnRefreshClicked);
            enterWorldButton?.onClick.RemoveListener(OnEnterWorldClicked);
            deleteCharacterButton?.onClick.RemoveListener(OnDeleteCharacterClicked);
            disconnectButton?.onClick.RemoveListener(OnDisconnectClicked);
            addressInput?.onValueChanged.RemoveListener(OnFrontendInputChanged);
            portInput?.onValueChanged.RemoveListener(OnFrontendInputChanged);
            accountInput?.onValueChanged.RemoveListener(OnFrontendInputChanged);
            passwordInput?.onValueChanged.RemoveListener(OnFrontendInputChanged);
            characterNameInput?.onValueChanged.RemoveListener(OnFrontendInputChanged);
#endif
        }

        private void BindManager()
        {
            if (_manager == null)
                _manager = FindFirstObjectByType<PlayerEntityGameManager>();
            if (_manager == null)
            {
                SetConnectionStatus("PlayerEntityGameManager was not found in the scene.");
                RefreshInteractableState();
                return;
            }

            _manager.ClientConnectionEstablished -= OnClientConnectionEstablished;
            _manager.ClientConnectionClosed -= OnClientConnectionClosed;
            _manager.ClientTransportStopped -= OnClientTransportStopped;
            _manager.ClientConnectionEstablished += OnClientConnectionEstablished;
            _manager.ClientConnectionClosed += OnClientConnectionClosed;
            _manager.ClientTransportStopped += OnClientTransportStopped;

            CopyManagerDefaultsOnce();
            if (_manager.IsClientConnected)
                OnClientConnectionEstablished();
            else
            {
                ResetFrontendSessionState();
                ApplyFrontendState();
                RefreshInteractableState();
                if (autoConnectConfiguredServer && !_manager.IsNetworkActive)
                    StartConfiguredClient();
            }
        }

        private void UnbindManager()
        {
            if (_manager == null)
                return;
            _manager.ClientConnectionEstablished -= OnClientConnectionEstablished;
            _manager.ClientConnectionClosed -= OnClientConnectionClosed;
            _manager.ClientTransportStopped -= OnClientTransportStopped;
        }

        private void OnClientConnectionEstablished()
        {
            CancelConnectionAttempt();
            StopAvailabilityMonitor();
            _selectedCharacterId = 0;
            _authenticated = false;
            _worldEntered = false;
            ClearSlots();
            SetConnectionStatus("Connected.");
            SetServerAvailability(true, false);
            SetLoginStatus("Server available. Sign in or create an account.");
            SetCharacterStatus("Waiting for authentication...");
            ApplyFrontendState();
            RefreshInteractableState();
        }

        private void OnClientTransportStopped()
        {
            if (_manager != null && _manager.IsClientConnected)
                return;

            bool wasAuthenticated = _authenticated;
            bool failedAttempt = _clientConnectionAttemptPending;
            CancelConnectionAttempt();
            ResetFrontendSessionState();
            if (failedAttempt)
                SetConnectionStatus("Connection failed. Server may not be ready; you can try again.");
            SetLoginStatus(autoConnectConfiguredServer
                ? (wasAuthenticated
                    ? "Disconnected from game server."
                    : (_serverAvailabilityKnown && !_serverAvailable
                        ? "Server unavailable. You can refresh status."
                        : "Server available. Sign in or create an account."))
                : "Choose a server, then sign in or create an account.");
            ApplyFrontendState();
            RefreshInteractableState();
            if (autoConnectConfiguredServer)
                StartAvailabilityMonitor();
        }

        private void OnClientConnectionClosed(ClientDisconnectNotice notice)
        {
            bool wasAuthenticated = _authenticated;
            CancelConnectionAttempt();
            ResetFrontendSessionState();
            SetConnectionStatus(notice.Unexpected && !string.IsNullOrWhiteSpace(notice.Message)
                ? notice.Message
                : "Ready to connect.");
            SetLoginStatus(autoConnectConfiguredServer
                ? (wasAuthenticated
                    ? "Disconnected from game server."
                    : (_serverAvailabilityKnown && !_serverAvailable
                        ? "Server unavailable. You can refresh status."
                        : "Server available. Sign in or create an account."))
                : "Choose a server, then sign in or create an account.");
            SetCharacterStatus("Waiting for authentication...");
            ApplyFrontendState();
            RefreshInteractableState();
            if (autoConnectConfiguredServer)
                StartAvailabilityMonitor();
        }

        private void ResetFrontendSessionState()
        {
            _selectedCharacterId = 0;
            _authenticated = false;
            _worldEntered = false;
            _requestInFlight = false;
            _clientConnectionAttemptPending = false;
            _authenticatedAccountId = 0;
            _authenticatedServerScope = string.Empty;
            _characterCreator?.CompleteAndClose();
            ClearSlots();
        }

        private void OnFrontendInputChanged(string _) => RefreshInteractableState();

        private void CopyManagerDefaultsOnce()
        {
            if (_defaultsCopiedFromManager || _manager == null)
                return;

            string defaultAddress = _manager.ConfiguredClientGameServerAddress;
            int defaultPort = _manager.ConfiguredClientGameServerPort;

            addressInput?.SetTextWithoutNotify(defaultAddress);
            portInput?.SetTextWithoutNotify(defaultPort.ToString());
            _defaultsCopiedFromManager = true;
        }

        private void OnStartClientClicked()
        {
            if (_manager == null || _manager.IsNetworkActive)
                return;
            if (!TryGetConnectionEndpoint(out string address, out int port))
                return;

            SetConnectionStatus($"Connecting to {address}:{port}...");
            if (_manager.StartClient(address, port))
            {
                _clientConnectionAttemptPending = true;
                WaitForConnectionAttemptTimeoutAsync().Forget();
            }
            else
            {
                _clientConnectionAttemptPending = false;
                SetConnectionStatus("Unable to start client connection. You can try again.");
            }

            RefreshInteractableState();
        }

        private async UniTaskVoid WaitForConnectionAttemptTimeoutAsync()
        {
            CancelConnectionAttempt();
            _clientConnectionAttemptPending = true;
            _connectionAttemptCancellation = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken token = _connectionAttemptCancellation.Token;

            try
            {
                await UniTask.Delay(
                    TimeSpan.FromSeconds(ClientConnectionAttemptTimeoutSeconds),
                    DelayType.Realtime,
                    PlayerLoopTiming.Update,
                    token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!this || token.IsCancellationRequested || !_clientConnectionAttemptPending)
                return;

            _clientConnectionAttemptPending = false;
            if (_manager != null && _manager.IsClientConnected)
                return;

            if (_manager != null && _manager.IsClient && !_manager.IsServer)
                _manager.StopClient();

            SetConnectionStatus("Connection timed out. Start the server and try again.");
            SetServerAvailability(false, false);
            SetLoginStatus(autoConnectConfiguredServer ? "Game server offline. Retry when it is available." : "Connect to a server to login or create an account.");
            ApplyFrontendState();
            RefreshInteractableState();
        }

        private void CancelConnectionAttempt()
        {
            _clientConnectionAttemptPending = false;
            if (_connectionAttemptCancellation == null)
                return;
            _connectionAttemptCancellation.Cancel();
            _connectionAttemptCancellation.Dispose();
            _connectionAttemptCancellation = null;
        }

        private bool TryGetConnectionEndpoint(out string address, out int port)
        {
            // Auto-configured Local/Live mode must use the manager as the single source of
            // truth. The hidden/manual address fields can be populated before the manager's
            // Start() runs, so using those fields here can cross-wire Live HTTPS auth to a
            // stale Local GameServer address (or vice versa).
            if (autoConnectConfiguredServer && _manager != null)
            {
                address = _manager.ConfiguredClientGameServerAddress;
                port = _manager.ConfiguredClientGameServerPort;
                return !string.IsNullOrWhiteSpace(address) && port > 0 && port <= 65535;
            }

            address = addressInput != null ? (addressInput.text ?? string.Empty).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(address))
                address = "127.0.0.1";

            string portText = portInput != null ? portInput.text : string.Empty;
            if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
            {
                SetConnectionStatus("Port must be between 1 and 65535.");
                return false;
            }
            return true;
        }

        private void OnLoginClicked() => AccountOperationAsync(false).Forget();
        private static void OnQuitClicked() => Application.Quit();
        private void OnCreateAccountClicked() => AccountOperationAsync(true).Forget();
        private void OnCreateCharacterClicked() => OpenCharacterCreator();
        private void OnRefreshClicked() => RefreshAsync(forceServer: true).Forget();
        private void OnEnterWorldClicked() => EnterSelectedCharacterAsync().Forget();
        private void OnDeleteCharacterClicked()
        {
            if (_requestInFlight || _selectedCharacterId <= 0 ||
                !TryFindCharacterSummary(_selectedCharacterId, out CharacterSessionCharacterSummary summary))
                return;

            if (_popup == null)
                _popup = FindFirstObjectByType<ClientPopupController>(FindObjectsInactive.Include);
            if (_popup == null)
            {
                SetCharacterStatus("Delete confirmation UI is unavailable.");
                return;
            }

            ShowDeleteCharacterConfirmation(summary.characterId, summary.name ?? string.Empty, null, string.Empty);
        }

        private void ShowDeleteCharacterConfirmation(long characterId, string characterName, string validationMessage, string inputValue)
        {
            if (_popup == null)
                return;

            string message = string.IsNullOrWhiteSpace(validationMessage)
                ? $"Type '{characterName}' to confirm deletion. The slot and name are released immediately. " +
                  "Staff can recover the archived character for up to 90 days."
                : validationMessage + "\n\n" +
                  $"Type '{characterName}' exactly to confirm deletion.";

            _popup.Show(
                "DELETE CHARACTER",
                message,
                "DELETE",
                () =>
                {
                    string typed = (_popup?.InputText ?? string.Empty).Trim();
                    if (!string.Equals(typed, characterName, StringComparison.Ordinal))
                    {
                        SetCharacterStatus("Character name did not match. Nothing was deleted.");
                        ShowDeleteCharacterConfirmation(
                            characterId,
                            characterName,
                            "The character name did not match. Nothing was deleted.",
                            typed);
                        return;
                    }

                    DeleteCharacterAsync(characterId, characterName).Forget();
                },
                "CANCEL",
                null,
                null,
                null,
                showInput: true,
                inputValue: inputValue ?? string.Empty);
        }

        private void OnDisconnectClicked()
        {
            if (_manager == null)
                return;

            if (!_authenticated)
            {
                ManualAvailabilityRefresh();
                return;
            }

            CancelConnectionAttempt();
            if (_manager.IsClient)
                _manager.StopClient();
        }

        /// <summary>
        /// Explicit gameplay -> frontend handoff used by ClientUIRoot. The frontend
        /// remains one scene object with a disabled Canvas while in world, so returning
        /// does not require scene reload or a polling loop.
        /// </summary>
        public void ReturnToFrontend(string message = null)
        {
#if UNITY_SERVER
            return;
#else
            CancelConnectionAttempt();
            ResetFrontendSessionState();
            if (!string.IsNullOrWhiteSpace(message))
                SetConnectionStatus(message);
            else
                SetConnectionStatus("Ready to connect.");
            SetLoginStatus("Sign in or create an account.");
            SetCharacterStatus("Waiting for authentication...");
            ApplyFrontendState();
            RefreshInteractableState();
            StartAvailabilityMonitor();
#endif
        }

        private async UniTaskVoid AccountOperationAsync(bool createAccount)
        {
            if (_requestInFlight || _manager == null)
                return;
            if (!TryBuildCredentials(createAccount, out string account, out string password))
                return;

            _requestInFlight = true;

            // The normal startup path keeps the GameServer transport established before HTTPS
            // authentication. After an unexpected disconnect, however, the frontend can be back
            // at Login with no active transport. Reuse the existing bounded connection/EnterGame
            // helper here so the same client process can recover without creating a second
            // reconnect system or changing the validated admission ordering.
            if (!_manager.IsClientConnected)
            {
                SetLoginStatus("Reconnecting to game server...");
                RefreshInteractableState();

                if (!await EnsureGameServerConnectedForAdmissionAsync())
                {
                    await UniTask.SwitchToMainThread();
                    _requestInFlight = false;
                    if (!this)
                        return;

                    SetLoginStatus(
                        "Game server connection is unavailable. Restore the server and try signing in again.");
                    ApplyFrontendState();
                    RefreshInteractableState();
                    StartAvailabilityMonitor();
                    return;
                }
            }

            SetLoginStatus(createAccount ? "Creating account securely..." : "Signing in securely...");
            RefreshInteractableState();

            string authUrl = ResolveAuthenticationUrl();
            Debug.Log(
                $"[FrontendAuth] {(createAccount ? "Create" : "SignIn")} starting. " +
                $"Auth={authUrl}, GameServer={_manager.ConfiguredClientGameServerAddress}:" +
                $"{_manager.ConfiguredClientGameServerPort}");
            CharacterVisualLocalCache.SetServerScope(authUrl);
            var authClient = new BackendAuthenticationClient();
            BackendAuthResponse backendResponse = createAccount
                ? await authClient.CreateAsync(
                    authUrl,
                    _manager.AuthenticationCertificateSha256,
                    account,
                    password)
                : await authClient.LoginAsync(
                    authUrl,
                    _manager.AuthenticationCertificateSha256,
                    account,
                    password);
            await UniTask.SwitchToMainThread();

            // Do not retain password material in the editable UI after the HTTPS attempt.
            password = string.Empty;
            if (passwordInput != null)
                passwordInput.text = string.Empty;

            if (!this)
                return;

            if (backendResponse == null ||
                !backendResponse.success ||
                string.IsNullOrWhiteSpace(backendResponse.admissionToken))
            {
                _requestInFlight = false;
                SetLoginStatus(backendResponse == null || string.IsNullOrWhiteSpace(backendResponse.error)
                    ? (createAccount ? "Account creation unavailable." : "Sign in unavailable.")
                    : backendResponse.error);
                RefreshInteractableState();
                return;
            }

            // Roll back to the previously validated admission ordering: the frontend keeps
            // the GameServer connection established before HTTPS authentication, so LiteNetLib's
            // EnterGame handshake is already complete when the one-time admission token is sent.
            Debug.Log("[FrontendAuth] HTTPS authentication accepted; redeeming admission on existing GameServer connection.");
            SetLoginStatus("Account accepted. Completing sign in...");
            AdmissionAuthenticationResponseMessage response =
                await _manager.RequestAuthenticateAdmissionAsync(backendResponse.admissionToken);
            await UniTask.SwitchToMainThread();

            // The one-time token has no further client use after this request.
            backendResponse.admissionToken = string.Empty;
            _requestInFlight = false;
            if (!this)
                return;

            if (!response.success || response.accountId <= 0)
            {
                Debug.LogWarning(
                    $"[FrontendAuth] GameServer admission rejected: " +
                    $"{(string.IsNullOrWhiteSpace(response.error) ? "unspecified" : response.error)}");
                SetLoginStatus(string.IsNullOrWhiteSpace(response.error)
                    ? "Game-session admission failed."
                    : response.error);
                if (_manager.IsClient)
                    _manager.StopClient();
                RefreshInteractableState();
                return;
            }

            Debug.Log($"[FrontendAuth] GameServer admission accepted for accountId={response.accountId}.");
            PersistRememberedAccount(account);
            _authenticated = true;
            _authenticatedAccountId = response.accountId;
            _authenticatedServerScope = authUrl;
            StopAvailabilityMonitor();

            // New accounts are authoritatively known to have no characters at creation time,
            // so write an empty local roster and never pay a redundant list request. Existing
            // accounts reconcile the persistent cache against the tiny roster fingerprint
            // piggybacked on the already-required admission response. Matching fingerprints
            // produce zero CharacterList traffic; a missing/unsupported/mismatched fingerprint
            // falls back to one authoritative list recovery.
            if (createAccount)
            {
                _lastCharacters = Array.Empty<CharacterSessionCharacterSummary>();
                CharacterRosterLocalCache.Save(_authenticatedServerScope, _authenticatedAccountId, _lastCharacters);
                ApplyCharacterRoster(_lastCharacters, "Account created. Create your first character.");
            }
            else if (CharacterRosterLocalCache.TryLoad(
                         _authenticatedServerScope,
                         _authenticatedAccountId,
                         out CharacterSessionCharacterSummary[] cachedCharacters,
                         out long cachedRosterRevision) &&
                     response.rosterRevision != 0L &&
                     cachedRosterRevision == response.rosterRevision)
            {
                ApplyCharacterRoster(cachedCharacters, cachedCharacters.Length == 0
                    ? "No characters yet. Create one."
                    : "Characters ready from validated local cache.");
            }
            else
            {
                SetCharacterStatus(response.rosterRevision == 0L
                    ? "Character cache validation unavailable. Synchronizing once..."
                    : "Character cache changed. Synchronizing once...");
                ApplyFrontendState();
                RefreshAsync(forceServer: true, bypassManualCooldown: true).Forget();
                return;
            }

            ApplyFrontendState();
            RefreshInteractableState();
        }

        private async UniTask<bool> EnsureGameServerConnectedForAdmissionAsync()
        {
            if (_manager == null)
                return false;
            if (_manager.IsClientConnected)
                return true;

            if (_manager.IsNetworkActive)
                _manager.StopClient();

            if (!TryGetConnectionEndpoint(out string address, out int port))
                return false;

            SetConnectionStatus($"Connecting to {address}:{port}...");
            if (!_manager.StartClient(address, port))
                return false;

            _clientConnectionAttemptPending = true;
            double deadline = Time.realtimeSinceStartupAsDouble + ClientConnectionAttemptTimeoutSeconds;
            while (this && _manager != null &&
                   !_manager.IsClientConnected &&
                   _manager.IsNetworkActive &&
                   Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            bool connected = this && _manager != null && _manager.IsClientConnected;
            if (!connected)
            {
                _clientConnectionAttemptPending = false;
                if (_manager != null && _manager.IsClient)
                    _manager.StopClient();
                return false;
            }

            // A transport connection is not yet an application/protocol-ready MMO session.
            // OnClientConnected immediately starts LiteNetLibManager's EnterGame handshake;
            // wait for its response (ClientConnectionId >= 0) before sending the admission
            // token request. The old always-connected login flow hid this ordering requirement
            // because EnterGame had already completed long before the user pressed Sign In.
            double handshakeDeadline = Time.realtimeSinceStartupAsDouble + ClientConnectionAttemptTimeoutSeconds;
            while (this && _manager != null &&
                   _manager.IsClientConnected &&
                   _manager.ClientConnectionId < 0 &&
                   Time.realtimeSinceStartupAsDouble < handshakeDeadline)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
            }

            bool protocolReady = this && _manager != null &&
                                 _manager.IsClientConnected &&
                                 _manager.ClientConnectionId >= 0;
            _clientConnectionAttemptPending = false;
            if (!protocolReady)
            {
                Debug.LogWarning("[FrontendAuth] GameServer transport connected, but EnterGame protocol handshake did not complete.");
                if (_manager != null && _manager.IsClient)
                    _manager.StopClient();
                return false;
            }

            Debug.Log($"[FrontendAuth] GameServer protocol handshake complete. ConnectionId={_manager.ClientConnectionId}.");
            return true;
        }

        private bool TryBuildCredentials(
            bool createAccount,
            out string account,
            out string password)
        {
            account = accountInput != null ? (accountInput.text ?? string.Empty).Trim() : string.Empty;
            password = passwordInput != null ? passwordInput.text ?? string.Empty : string.Empty;

            if (!AccountCredentialPolicy.IsAllowedAccountName(account))
            {
                SetLoginStatus("Account must be 1-16 letters, digits, or underscore.");
                return false;
            }

            bool passwordAllowed = createAccount
                ? AccountCredentialPolicy.IsAllowedPasswordForCreation(password)
                : AccountCredentialPolicy.IsAllowedPasswordForLogin(password);
            if (!passwordAllowed)
            {
                SetLoginStatus(createAccount
                    ? $"New passwords must be {AccountCredentialPolicy.PasswordMinLength}-{AccountCredentialPolicy.PasswordMaxLength} characters."
                    : "Password is required.");
                return false;
            }

            return true;
        }

        private string ResolveAuthenticationUrl()
        {
            string configured = _manager != null
                ? _manager.AuthenticationServiceBaseUrl
                : "https://127.0.0.1:8443";

            // In Local/Live preset mode the manager owns both endpoints. Never derive the
            // auth host from the hidden/manual address field, because that field may have
            // been populated before PlayerEntityGameManager.Start() applies its preset.
            if (autoConnectConfiguredServer)
                return configured;

            if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri uri))
                return configured;

            string gameAddress = addressInput != null
                ? (addressInput.text ?? string.Empty).Trim()
                : string.Empty;
            bool configuredLoopback = string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            bool gameIsRemote = !string.IsNullOrWhiteSpace(gameAddress) &&
                                !string.Equals(gameAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(gameAddress, "localhost", StringComparison.OrdinalIgnoreCase);

            if (!configuredLoopback || !gameIsRemote)
                return configured;

            var builder = new UriBuilder(uri) { Host = gameAddress };
            return builder.Uri.ToString().TrimEnd('/');
        }

        private void StartAvailabilityMonitor()
        {
            if (!autoConnectConfiguredServer || _authenticated || _availabilityMonitorCancellation != null)
                return;

            _availabilityMonitorCancellation = new System.Threading.CancellationTokenSource();
            AvailabilityMonitorAsync(_availabilityMonitorCancellation.Token).Forget();
        }

        private void StopAvailabilityMonitor()
        {
            if (_availabilityMonitorCancellation == null)
                return;
            _availabilityMonitorCancellation.Cancel();
            _availabilityMonitorCancellation.Dispose();
            _availabilityMonitorCancellation = null;
        }

        private async UniTaskVoid AvailabilityMonitorAsync(System.Threading.CancellationToken token)
        {
            while (this && !token.IsCancellationRequested)
            {
                if (!_authenticated)
                    await CheckServerAvailabilityAsync(false);

                try
                {
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(AutomaticAvailabilityIntervalSeconds),
                        DelayType.Realtime,
                        PlayerLoopTiming.Update,
                        token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void ManualAvailabilityRefresh()
        {
            if (_authenticated || _availabilityCheckInFlight)
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextManualAvailabilityRefreshAt)
            {
                int remaining = Mathf.CeilToInt((float)(_nextManualAvailabilityRefreshAt - now));
                SetLoginStatus($"Server status can be refreshed again in {remaining}s.");
                RefreshInteractableState();
                return;
            }

            _nextManualAvailabilityRefreshAt = now + ManualAvailabilityCooldownSeconds;
            CheckServerAvailabilityAsync(true).Forget();
        }

        private async UniTask CheckServerAvailabilityAsync(bool manual)
        {
            if (_availabilityCheckInFlight || _manager == null || _authenticated)
                return;

            _availabilityCheckInFlight = true;
            SetServerAvailability(false, true);
            if (manual)
                SetLoginStatus("Checking server availability...");
            RefreshInteractableState();

            string authUrl = ResolveAuthenticationUrl();
            var authClient = new BackendAuthenticationClient();
            BackendAuthenticationClient.BackendServiceHealth health =
                await authClient.CheckHealthAsync(
                    authUrl,
                    _manager.AuthenticationCertificateSha256);
            await UniTask.SwitchToMainThread();

            _availabilityCheckInFlight = false;
            if (!this || _authenticated)
                return;

            bool online = health != null && health.IsHealthy && health.gameServerAvailable;
            SetServerAvailability(online, false);
            SetLoginStatus(online
                ? "Server available. Sign in or create an account."
                : (health != null && health.IsHealthy
                    ? "Login service is online, but no game server is currently available."
                    : "Server unavailable. Please try again later."));
            RefreshInteractableState();
        }

        private void StartConfiguredClient()
        {
            if (_manager == null || _manager.IsNetworkActive)
                return;

            // CharacterSelectShell and PlayerEntityGameManager are separate scene objects.
            // Unity does not guarantee Start() ordering between them. Auto-connect can therefore
            // reach LiteNetLibManager.StartClient before LiteNetLibManager.Start has created its
            // Client/Server runtime objects. Defer until the manager has completed initialization
            // instead of treating that startup-order race as an offline server.
            if (_manager.Client == null)
            {
                StartConfiguredClientWhenManagerReadyAsync().Forget();
                return;
            }

            StartConfiguredClientInitialized();
        }

        private async UniTaskVoid StartConfiguredClientWhenManagerReadyAsync()
        {
            if (_configuredClientStartPending)
                return;

            _configuredClientStartPending = true;
            try
            {
                // Normally one frame is enough because every active MonoBehaviour.Start() has
                // completed before the next Update. Keep a short bounded wait so custom script
                // execution order cannot recreate the race.
                double deadline = Time.realtimeSinceStartupAsDouble + 2.0;
                while (this &&
                       _manager != null &&
                       !_manager.IsNetworkActive &&
                       _manager.Client == null &&
                       Time.realtimeSinceStartupAsDouble < deadline)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update);
                }

                if (!this || _manager == null || _manager.IsNetworkActive)
                    return;

                if (_manager.Client == null)
                {
                    SetServerAvailability(false, false);
                    SetLoginStatus("Game client networking did not initialize. Retry or check the PlayerEntityGameManager.");
                    ApplyFrontendState();
                    RefreshInteractableState();
                    return;
                }

                StartConfiguredClientInitialized();
            }
            finally
            {
                _configuredClientStartPending = false;
            }
        }

        private void StartConfiguredClientInitialized()
        {
            if (_manager == null || _manager.IsNetworkActive || _manager.Client == null)
                return;

            string address = _manager.ConfiguredClientGameServerAddress;
            int port = _manager.ConfiguredClientGameServerPort;

            SetServerAvailability(false, true);
            SetLoginStatus("Checking game server...");
            SetConnectionStatus($"Connecting to {address}:{port}...");
            if (_manager.StartClient(address, port))
            {
                _clientConnectionAttemptPending = true;
                WaitForConnectionAttemptTimeoutAsync().Forget();
            }
            else
            {
                _clientConnectionAttemptPending = false;
                SetServerAvailability(false, false);
                SetLoginStatus("Game server offline. Retry when it is available.");
            }
            ApplyFrontendState();
            RefreshInteractableState();
        }

        private void OnRememberAccountChanged(bool remember)
        {
            PlayerPrefs.SetInt(RememberAccountEnabledKey, remember ? 1 : 0);
            if (!remember)
                PlayerPrefs.DeleteKey(RememberAccountNameKey);
            else
                PersistRememberedAccount(accountInput != null ? accountInput.text : string.Empty);
            PlayerPrefs.Save();
        }

        private void LoadRememberedAccount()
        {
            bool remember = PlayerPrefs.GetInt(RememberAccountEnabledKey, 0) != 0;
            if (rememberAccountToggle != null)
                rememberAccountToggle.SetIsOnWithoutNotify(remember);
            if (!remember || accountInput == null)
                return;

            string saved = PlayerPrefs.GetString(RememberAccountNameKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(saved))
                accountInput.SetTextWithoutNotify(saved.Trim());
        }

        private void PersistRememberedAccount(string account)
        {
            if (rememberAccountToggle == null || !rememberAccountToggle.isOn)
                return;
            string normalized = (account ?? string.Empty).Trim();
            if (!AccountCredentialPolicy.IsAllowedAccountName(normalized))
                return;
            PlayerPrefs.SetInt(RememberAccountEnabledKey, 1);
            PlayerPrefs.SetString(RememberAccountNameKey, normalized);
            PlayerPrefs.Save();
        }

        private void SetServerAvailability(bool online, bool connecting)
        {
            if (!connecting)
            {
                _serverAvailabilityKnown = true;
                _serverAvailable = online;
            }

            if (serverAvailabilityText == null)
                return;
            if (online)
            {
                serverAvailabilityText.text = "●  ONLINE";
                serverAvailabilityText.color = serverOnlineColor;
            }
            else if (connecting)
            {
                serverAvailabilityText.text = "●  CHECKING";
                serverAvailabilityText.color = serverCheckingColor;
            }
            else
            {
                serverAvailabilityText.text = "●  OFFLINE";
                serverAvailabilityText.color = serverOfflineColor;
            }
        }

        private void EnsureAccountConvenienceSurface()
        {
            if (loginPanel == null)
                return;

            if (serverAvailabilityText == null)
            {
                Transform existing = loginPanel.transform.Find("Server Availability Text") ??
                                     loginPanel.transform.Find("ServerAvailability");
                serverAvailabilityText = existing != null ? existing.GetComponent<Text>() : null;
            }

            if (rememberAccountToggle == null)
            {
                Transform existing = loginPanel.transform.Find("Remember Account Toggle") ??
                                     loginPanel.transform.Find("RememberAccountToggle");
                rememberAccountToggle = existing != null ? existing.GetComponent<Toggle>() : null;
            }

            if (quitButton == null)
            {
                Transform existing = loginPanel.transform.Find("Quit Button") ?? loginPanel.transform.Find("QuitButton");
                quitButton = existing != null ? existing.GetComponent<Button>() : null;
            }

            if (serverAvailabilityText == null || rememberAccountToggle == null || quitButton == null)
                Debug.LogError("[FrontendUI] Login Panel is missing authored convenience controls. Repair ClientUIRoot.prefab before production use.", this);
        }

        private void OpenCharacterCreator()
        {
            if (_requestInFlight || _manager == null || !_manager.IsClientConnected || !_authenticated)
                return;

            EnsureCharacterCreatorSurface();
            if (_characterCreator == null)
            {
                SetCharacterStatus("Character Creator is unavailable.");
                return;
            }

            _characterCreator.Open();
            ApplyFrontendState();
        }

        private void OnCharacterCreatorCancelled()
        {
            ApplyFrontendState();
            SetCharacterStatus("Character creation cancelled.");
            RefreshInteractableState();
        }

        private void OnCharacterCreatorCreateRequested(
            string name,
            CharacterAppearanceRecipe appearance,
            CharacterPresentationPreferences presentation)
        {
            CreateCharacterFromCreatorAsync(name, appearance, presentation).Forget();
        }

        private async UniTaskVoid CreateCharacterFromCreatorAsync(
            string name,
            CharacterAppearanceRecipe initialAppearance,
            CharacterPresentationPreferences initialPresentation)
        {
            if (_requestInFlight || _manager == null || !_manager.IsClientConnected || !_authenticated)
                return;

            name = CharacterNamePolicy.Normalize(name);
            if (!CharacterNamePolicy.IsAllowed(name))
            {
                _characterCreator?.SetError("Name must be 1-16 letters with optional single spaces.");
                return;
            }

            CharacterAppearanceRecipe appearance =
                initialAppearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault();
            CharacterPresentationPreferences presentation =
                initialPresentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            if (!appearance.IsValid(out _) || !presentation.IsValid(out _))
            {
                _characterCreator?.SetError("Character customization data is invalid.");
                return;
            }

            _requestInFlight = true;
            _characterCreator?.SetBusy(true, $"Creating {name}...");
            SetCharacterStatus($"Creating {name}...");
            RefreshInteractableState();

            CreateCharacterResponseMessage response =
                await _manager.RequestCreateCharacterAsync(name, appearance, presentation);
            await UniTask.SwitchToMainThread();

            _requestInFlight = false;
            if (!this)
                return;

            if (!response.success)
            {
                string error = string.IsNullOrWhiteSpace(response.error)
                    ? $"Character creation failed (code {response.failure})."
                    : response.error;
                _characterCreator?.SetError(error);
                SetCharacterStatus(error);
                RefreshInteractableState();
                return;
            }

            CharacterSelectAppearanceCache.Save(response.characterId, appearance);
            CharacterSelectPresentationCache.Save(response.characterId, presentation);
            _characterCreator?.CompleteAndClose(clearDraft: true);

            // Creation succeeded authoritatively, so update the local roster directly.
            // Do not throw away the known result just to request the whole roster again.
            CharacterSessionCharacterSummary created = new CharacterSessionCharacterSummary(
                response.characterId,
                response.name,
                string.Empty);
            _lastCharacters = CharacterRosterLocalCache.AddOrUpdate(_lastCharacters, created);
            if (_authenticatedAccountId > 0 && !string.IsNullOrWhiteSpace(_authenticatedServerScope))
                CharacterRosterLocalCache.Save(_authenticatedServerScope, _authenticatedAccountId, _lastCharacters);

            ApplyCharacterRoster(_lastCharacters, $"Created {response.name}.", response.characterId);
            ApplyFrontendState();
            RefreshInteractableState();
        }

        private async UniTaskVoid RefreshAsync(
            bool forceServer = false,
            bool bypassManualCooldown = false)
        {
            if (_requestInFlight)
                return;
            if (_manager == null || !_manager.IsClientConnected || !_authenticated)
            {
                SetCharacterStatus("Sign in before synchronizing characters.");
                RefreshInteractableState();
                return;
            }

            // Normal Character Select rendering is cache-first. Login validates that cache
            // against the admission roster fingerprint before this method is reached. A server
            // list request is therefore reconciliation/recovery only, never routine polling.
            if (!forceServer &&
                _authenticatedAccountId > 0 &&
                CharacterRosterLocalCache.TryLoad(
                    _authenticatedServerScope,
                    _authenticatedAccountId,
                    out CharacterSessionCharacterSummary[] cachedCharacters))
            {
                ApplyCharacterRoster(cachedCharacters, cachedCharacters.Length == 0
                    ? "No characters yet. Create one."
                    : "Characters ready from local cache.");
                RefreshInteractableState();
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (forceServer && !bypassManualCooldown && now < _nextManualRosterRecoveryAt)
            {
                int remaining = Mathf.CeilToInt((float)(_nextManualRosterRecoveryAt - now));
                SetCharacterStatus($"Character recovery can be run again in {remaining}s.");
                RefreshInteractableState();
                return;
            }
            if (forceServer && !bypassManualCooldown)
                _nextManualRosterRecoveryAt = now + ManualAvailabilityCooldownSeconds;

            _requestInFlight = true;
            RefreshInteractableState();
            SetCharacterStatus("Synchronizing characters from server...");

            CharacterListResponseMessage response = await _manager.RequestCharacterListAsync(forceRefresh: true);
            await UniTask.SwitchToMainThread();

            _requestInFlight = false;
            if (!this)
                return;

            if (!response.success)
            {
                SetCharacterStatus(BuildLobbyStatus(response.sessionState, response.error));
                RefreshInteractableState();
                return;
            }

            CharacterSessionCharacterSummary[] characters =
                response.characters ?? Array.Empty<CharacterSessionCharacterSummary>();
            if (_authenticatedAccountId > 0 && !string.IsNullOrWhiteSpace(_authenticatedServerScope))
                CharacterRosterLocalCache.Save(_authenticatedServerScope, _authenticatedAccountId, characters);

            ApplyCharacterRoster(characters, characters.Length == 0
                ? "No characters yet. Create one."
                : "Character cache synchronized from server.");
            RefreshInteractableState();
        }

        private void ApplyCharacterRoster(
            CharacterSessionCharacterSummary[] characters,
            string status,
            long preferredCharacterId = 0)
        {
            CharacterSessionCharacterSummary[] nextCharacters =
                characters ?? Array.Empty<CharacterSessionCharacterSummary>();
            _selectedCharacterId = 0;
            ClearSlots();
            _lastCharacters = nextCharacters;

            int visible = Mathf.Min(_lastCharacters.Length, slots == null ? 0 : slots.Length);
            for (int i = 0; i < visible; ++i)
                slots[i]?.Bind(_lastCharacters[i], SelectCharacter);

            if (_lastCharacters.Length == 0)
            {
                previewController?.ClearPreview();
                SetCharacterStatus(string.IsNullOrWhiteSpace(status) ? "No characters yet. Create one." : status);

                // Empty authoritative/local roster flows directly into the existing authored
                // Character Creator. This is presentation state only: creation still uses the
                // existing server-authoritative RequestCreateCharacterAsync path. Cancelling
                // returns to the empty Character Select surface so the player is never trapped.
                if (_authenticated && !_worldEntered && !_requestInFlight &&
                    _manager != null && _manager.IsClientConnected)
                {
                    OpenCharacterCreator();
                }
                return;
            }

            long selection = preferredCharacterId;
            if (selection <= 0 || Array.FindIndex(_lastCharacters, c => c.characterId == selection) < 0)
                selection = _lastCharacters[0].characterId;
            SelectCharacter(selection);
            SetCharacterStatus(string.IsNullOrWhiteSpace(status) ? "Select a character and enter the world." : status);
        }

        private async UniTaskVoid DeleteCharacterAsync(long characterId, string expectedName)
        {
            if (_requestInFlight || _manager == null || !_manager.IsClientConnected || !_authenticated)
                return;
            if (characterId <= 0 || _selectedCharacterId != characterId)
                return;

            _requestInFlight = true;
            RefreshInteractableState();
            SetCharacterStatus($"Deleting {expectedName}...");

            DeleteCharacterResponseMessage response =
                await _manager.RequestDeleteCharacterAsync(characterId);
            await UniTask.SwitchToMainThread();

            _requestInFlight = false;
            if (!this)
                return;

            if (!response.success)
            {
                SetCharacterStatus(string.IsNullOrWhiteSpace(response.error)
                    ? $"Character deletion failed (code {response.failure})."
                    : response.error);
                RefreshInteractableState();
                return;
            }

            _lastCharacters = CharacterRosterLocalCache.Remove(_lastCharacters, characterId);
            if (_authenticatedAccountId > 0 && !string.IsNullOrWhiteSpace(_authenticatedServerScope))
                CharacterRosterLocalCache.Save(_authenticatedServerScope, _authenticatedAccountId, _lastCharacters);

            // Keep the local visual cache. It is presentation-only, costs no server wire,
            // and allows a future restore of the same stable character id to recover its
            // preview immediately on this machine. The authoritative archive lives server-side.
            string deletedName = string.IsNullOrWhiteSpace(response.name) ? expectedName : response.name;
            ApplyCharacterRoster(
                _lastCharacters,
                $"{deletedName} deleted. Slot freed; name released. Recoverable for 90 days.");
            RefreshInteractableState();
        }

        private async UniTaskVoid EnterSelectedCharacterAsync()
        {
            if (_requestInFlight || _selectedCharacterId <= 0)
                return;
            if (_manager == null || !_manager.IsClientConnected || !_authenticated)
            {
                SetCharacterStatus("Authenticate before entering the world.");
                RefreshInteractableState();
                return;
            }

            _requestInFlight = true;
            RefreshInteractableState();
            SetCharacterStatus("Loading character and entering world...");

            EnterCharacterResponseMessage response =
                await _manager.RequestEnterCharacterAsync(_selectedCharacterId);
            await UniTask.SwitchToMainThread();

            _requestInFlight = false;
            if (!this)
                return;

            if (!response.success || !response.worldAdopted)
            {
                SetCharacterStatus(BuildEnterStatus(response));
                RefreshInteractableState();
                return;
            }

            _worldEntered = true;
            previewController?.ClearPreview();
            SetCharacterStatus("Character entered the world.");
            RefreshInteractableState();
            if (hideAfterWorldEntry)
                SetPresentationVisible(false);
        }

        private void SelectCharacter(long characterId)
        {
            _selectedCharacterId = characterId;
            if (slots != null)
            {
                for (int i = 0; i < slots.Length; ++i)
                {
                    CharacterSelectSlotView slot = slots[i];
                    if (slot != null)
                        slot.SetSelected(slot.CharacterId == characterId);
                }
            }

            if (TryFindCharacterSummary(characterId, out CharacterSessionCharacterSummary summary))
                previewController?.ShowCharacter(summary.characterId, summary.name);
            else
                previewController?.ClearPreview();

            RefreshInteractableState();
        }

        private bool TryFindCharacterSummary(long characterId, out CharacterSessionCharacterSummary summary)
        {
            CharacterSessionCharacterSummary[] characters = _lastCharacters ?? Array.Empty<CharacterSessionCharacterSummary>();
            for (int i = 0; i < characters.Length; ++i)
            {
                if (characters[i].characterId != characterId)
                    continue;
                summary = characters[i];
                return true;
            }

            summary = default;
            return false;
        }

        private void OnOwnerAppearanceObserved(long characterId, CharacterAppearanceRecipe appearance)
        {
            CharacterSelectAppearanceCache.Save(characterId, appearance);
            if (_worldEntered)
                return;

            if (_selectedCharacterId == characterId &&
                TryFindCharacterSummary(characterId, out CharacterSessionCharacterSummary summary))
            {
                previewController?.ShowCharacter(summary.characterId, summary.name);
            }
        }

        private void OnOwnerPresentationObserved(
            long characterId,
            CharacterPresentationPreferences presentation)
        {
            CharacterSelectPresentationCache.Save(characterId, presentation);
            if (_worldEntered)
                return;

            if (_selectedCharacterId == characterId &&
                TryFindCharacterSummary(characterId, out CharacterSessionCharacterSummary summary))
            {
                previewController?.ShowCharacter(summary.characterId, summary.name);
            }
        }

        private void OnOwnerEquipmentVisualsObserved(
            long characterId,
            uint equipmentVersion,
            PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            CharacterVisualLocalCache.SaveEquipment(
                characterId,
                equipmentVersion,
                equipmentVisuals);

            if (_worldEntered)
                return;

            if (_selectedCharacterId == characterId &&
                TryFindCharacterSummary(characterId, out CharacterSessionCharacterSummary summary))
            {
                previewController?.ShowCharacter(summary.characterId, summary.name);
            }
        }

        private void ClearSlots()
        {
            _lastCharacters = Array.Empty<CharacterSessionCharacterSummary>();
            previewController?.ClearPreview();
            if (slots == null)
                return;
            for (int i = 0; i < slots.Length; ++i)
                slots[i]?.Clear();
        }

        private void EnsureCharacterSlotIntegrity()
        {
            if (characterPanel == null)
                return;

            CharacterSelectSlotView[] authoredSlots =
                characterPanel.GetComponentsInChildren<CharacterSelectSlotView>(true);
            for (int i = 0; i < authoredSlots.Length; ++i)
            {
                CharacterSelectSlotView candidate = authoredSlots[i];
                if (candidate == null)
                    continue;
                if (slots != null && Array.IndexOf(slots, candidate) >= 0)
                    continue;

                Debug.LogWarning(
                    $"[FrontendUI] Disabling orphan Character Select slot '{candidate.gameObject.name}'. " +
                    "Rebuild Authored Frontend to remove the stale slot from ClientUIRoot.prefab.",
                    candidate);
                candidate.gameObject.SetActive(false);
            }
        }

        private void EnsureDeleteCharacterSurface()
        {
            if (characterPanel == null || deleteCharacterButton != null)
                return;

            Transform existing = characterPanel.transform.Find("Delete Character Button") ??
                                 characterPanel.transform.Find("DeleteCharacterButton");
            deleteCharacterButton = existing != null ? existing.GetComponent<Button>() : null;
            if (deleteCharacterButton == null)
                Debug.LogError("[FrontendUI] Character Select is missing the authored Delete Character Button.", this);
        }

        private void EnsureCharacterCreatorSurface()
        {
            if (characterPanel == null)
                return;

            if (_characterCreator == null)
                _characterCreator = GetComponentInChildren<CharacterCreatorShell>(true);
            if (_characterCreator == null)
            {
                Debug.LogError(
                    "[FrontendUI] ClientUIRoot.prefab is missing the authored Character Creator. " +
                    "Restore the canonical authored frontend on ClientUIRoot.prefab.",
                    this);
                if (createCharacterButton != null)
                    createCharacterButton.interactable = false;
                return;
            }

            _characterCreator.Initialize();
            _characterCreator.Cancelled -= OnCharacterCreatorCancelled;
            _characterCreator.CreateRequested -= OnCharacterCreatorCreateRequested;
            _characterCreator.Cancelled += OnCharacterCreatorCancelled;
            _characterCreator.CreateRequested += OnCharacterCreatorCreateRequested;
        }

        private void EnsurePreviewPresentationSurface()
        {
            if (characterPanel == null)
                return;

            if (previewController == null)
                previewController = characterPanel.GetComponentInChildren<CharacterSelectPreviewController>(true);
            if (previewController != null)
                return;

            // The current canonical ClientUIRoot can be upgraded from an older authored
            // Character Select that predates the preview surface. Keep the client usable
            // without requiring an Editor repair pass or any server/network changes.
            WarnRuntimeUiFallback("Character Select Preview");
            previewController = CreateRuntimePreviewSurface(characterPanel.transform);
            ApplyRuntimeCharacterSelectPreviewLayout();
        }

        private CharacterSelectPreviewController CreateRuntimePreviewSurface(Transform parent)
        {
            int uiLayer = parent != null ? parent.gameObject.layer : gameObject.layer;

            GameObject frame = new GameObject(
                "Character Preview Frame",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            frame.layer = uiLayer;
            frame.transform.SetParent(parent, false);
            Image frameImage = frame.GetComponent<Image>();
            frameImage.color = new Color(0.055f, 0.06f, 0.08f, 1f);
            frameImage.raycastTarget = false;
            SetRuntimeRect(frame.GetComponent<RectTransform>(), new Vector2(330f, 35f), new Vector2(610f, 590f));

            GameObject previewObject = new GameObject(
                "Character Preview Image",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(RawImage));
            previewObject.layer = uiLayer;
            previewObject.transform.SetParent(frame.transform, false);
            RawImage rawImage = previewObject.GetComponent<RawImage>();
            rawImage.color = Color.white;
            rawImage.raycastTarget = true;
            RectTransform previewRect = rawImage.rectTransform;
            previewRect.anchorMin = Vector2.zero;
            previewRect.anchorMax = Vector2.one;
            previewRect.pivot = new Vector2(0.5f, 0.5f);
            previewRect.offsetMin = new Vector2(8f, 46f);
            previewRect.offsetMax = new Vector2(-8f, -8f);

            Font font = statusText != null ? statusText.font : null;
            if (font == null)
            {
                Text template = characterPanel.GetComponentInChildren<Text>(true);
                if (template != null)
                    font = template.font;
            }

            Text previewName = CreateRuntimePreviewText(
                "Preview Name Text",
                frame.transform,
                font,
                string.Empty,
                20,
                TextAnchor.MiddleLeft,
                new Vector2(0f, 271f),
                new Vector2(560f, 34f),
                uiLayer);

            Text hint = CreateRuntimePreviewText(
                "Rotate Hint Text",
                frame.transform,
                font,
                "Drag preview to rotate  •  Scroll to zoom",
                14,
                TextAnchor.MiddleRight,
                new Vector2(0f, -273f),
                new Vector2(560f, 30f),
                uiLayer);
            hint.color = new Color(0.62f, 0.66f, 0.74f, 1f);

            CharacterSelectPreviewController controller = frame.AddComponent<CharacterSelectPreviewController>();
            controller.ConfigureRuntimeSurface(rawImage, previewName);

            CharacterSelectPreviewDragSurface drag = previewObject.AddComponent<CharacterSelectPreviewDragSurface>();
            drag.controller = controller;
            return controller;
        }

        private static Text CreateRuntimePreviewText(
            string name,
            Transform parent,
            Font font,
            string value,
            int fontSize,
            TextAnchor alignment,
            Vector2 position,
            Vector2 size,
            int uiLayer)
        {
            GameObject go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));
            go.layer = uiLayer;
            go.transform.SetParent(parent, false);

            Text text = go.GetComponent<Text>();
            text.font = font;
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            SetRuntimeRect(text.rectTransform, position, size);
            return text;
        }

        private void ApplyRuntimeCharacterSelectPreviewLayout()
        {
            RectTransform panelRect = characterPanel != null ? characterPanel.GetComponent<RectTransform>() : null;
            if (panelRect == null)
                return;

            SetRuntimeRect(panelRect, Vector2.zero, new Vector2(1380f, 780f));

            Transform title = characterPanel.transform.Find("Title");
            if (title != null)
                SetRuntimeRect(title.GetComponent<RectTransform>(), new Vector2(-385f, 325f), new Vector2(560f, 50f));

            Transform subtitle = characterPanel.transform.Find("Subtitle");
            if (subtitle != null)
                SetRuntimeRect(subtitle.GetComponent<RectTransform>(), new Vector2(-385f, 286f), new Vector2(560f, 30f));

            if (statusText != null)
                SetRuntimeRect(statusText.rectTransform, new Vector2(-385f, 245f), new Vector2(560f, 34f));

            // Character naming now belongs to Character Creator. Older roots can still
            // contain the retired inline name box; hide it when repairing the preview.
            if (characterNameInput != null)
                characterNameInput.gameObject.SetActive(false);

            if (createCharacterButton != null)
                SetRuntimeRect(createCharacterButton.GetComponent<RectTransform>(), new Vector2(-385f, 185f), new Vector2(560f, 50f));

            if (slots != null)
            {
                for (int i = 0; i < slots.Length; ++i)
                {
                    CharacterSelectSlotView slot = slots[i];
                    if (slot == null)
                        continue;

                    float y = 112f - (i * 58f);
                    SetRuntimeRect(slot.GetComponent<RectTransform>(), new Vector2(-385f, y), new Vector2(560f, 52f));
                }
            }

            if (previewController != null)
                SetRuntimeRect(previewController.GetComponent<RectTransform>(), new Vector2(330f, 35f), new Vector2(610f, 590f));

            if (deleteCharacterButton != null)
                SetRuntimeRect(deleteCharacterButton.GetComponent<RectTransform>(), new Vector2(-475f, -335f), new Vector2(220f, 50f));
            if (enterWorldButton != null)
                SetRuntimeRect(enterWorldButton.GetComponent<RectTransform>(), new Vector2(-175f, -335f), new Vector2(270f, 50f));
            if (disconnectButton != null)
                SetRuntimeRect(disconnectButton.GetComponent<RectTransform>(), new Vector2(485f, -335f), new Vector2(220f, 50f));
        }

        private static void SetRuntimeRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            if (rect == null)
                return;

            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private void WarnRuntimeUiFallback(string surface)
        {
            Debug.LogWarning(
                $"[FrontendUI] ClientUIRoot.prefab is missing authored '{surface}'. " +
                "Using runtime compatibility fallback. Repair the authored prefab so this path is not used in production.",
                this);
        }

        private void ApplyFrontendState()
        {
#if UNITY_SERVER
            return;
#else
            bool connected = _manager != null && _manager.IsClientConnected;
            if (_worldEntered && hideAfterWorldEntry)
            {
                SetPresentationVisible(false);
                return;
            }

            bool showConnection = !autoConnectConfiguredServer && useFrontendConnectionPanel && !connected;
            bool showLogin = !_authenticated && (autoConnectConfiguredServer || connected || !useFrontendConnectionPanel);
            bool creatorOpen = _characterCreator != null && _characterCreator.IsOpen;
            bool showCharacters = connected && _authenticated && !creatorOpen;
            if ((!connected || !_authenticated) && creatorOpen)
                _characterCreator.CompleteAndClose();
            bool visible = showConnection || showLogin || showCharacters || creatorOpen;

            if (connectionPanel != null)
                connectionPanel.SetActive(showConnection);
            if (loginPanel != null)
                loginPanel.SetActive(showLogin);
            if (characterPanel != null)
                characterPanel.SetActive(showCharacters);

            SetPresentationVisible(visible);
#endif
        }

        private void RefreshInteractableState()
        {
            bool networkActive = _manager != null && _manager.IsNetworkActive;
            bool connected = _manager != null && _manager.IsClientConnected;

            if (startClientButton != null)
                startClientButton.interactable = !networkActive && !_requestInFlight;
            if (addressInput != null)
                addressInput.interactable = !networkActive && !_requestInFlight;
            if (portInput != null)
                portInput.interactable = !networkActive && !_requestInFlight;

            string account = accountInput != null
                ? (accountInput.text ?? string.Empty).Trim()
                : string.Empty;
            string password = passwordInput != null
                ? passwordInput.text ?? string.Empty
                : string.Empty;
            bool accountValid = AccountCredentialPolicy.IsAllowedAccountName(account);
            bool loginCredentialsValid = accountValid &&
                AccountCredentialPolicy.IsAllowedPasswordForLogin(password);
            bool createCredentialsValid = accountValid &&
                AccountCredentialPolicy.IsAllowedPasswordForCreation(password);
            // Normal startup still presents Login after the GameServer transport is connected.
            // After an unexpected disconnect, auto-configured mode may legitimately be back at
            // Login with no transport. Allow the account action to reuse the existing bounded
            // EnsureGameServerConnectedForAdmissionAsync path before HTTPS authentication.
            // Manual endpoint mode keeps its explicit Connect button requirement.
            bool canPrepareAdmissionConnection =
                connected ||
                (autoConnectConfiguredServer &&
                 !networkActive &&
                 _manager != null &&
                 _manager.Client != null);
            bool loginSurfaceReady =
                canPrepareAdmissionConnection &&
                !_authenticated &&
                !_requestInFlight;
            if (loginButton != null)
                loginButton.interactable = loginSurfaceReady && loginCredentialsValid;
            if (createAccountButton != null)
                createAccountButton.interactable = loginSurfaceReady && createCredentialsValid;
            if (loginDisconnectButton != null)
            {
                // This is a status refresh while unauthenticated, not a gameplay reconnect.
                // Manual health requests are locally rate-limited to one per 60 seconds.
                loginDisconnectButton.interactable = !_authenticated && !_requestInFlight && !_availabilityCheckInFlight;
                Text label = loginDisconnectButton.GetComponentInChildren<Text>();
                if (label != null)
                    label.text = "REFRESH STATUS";
            }

            if (characterNameInput != null)
                characterNameInput.interactable = false;
            if (createCharacterButton != null)
                createCharacterButton.interactable = connected && _authenticated && !_requestInFlight;
            if (refreshButton != null)
                refreshButton.interactable = connected && _authenticated && !_requestInFlight;
            if (enterWorldButton != null)
                enterWorldButton.interactable = connected && _authenticated && !_requestInFlight && _selectedCharacterId > 0;
            if (deleteCharacterButton != null)
                deleteCharacterButton.interactable = connected && _authenticated && !_requestInFlight && _selectedCharacterId > 0;
            if (disconnectButton != null)
                disconnectButton.interactable = connected && !_requestInFlight;
        }

        private void SetPresentationVisible(bool visible)
        {
            if (_canvas == null)
                _canvas = GetComponent<Canvas>();
            if (_raycaster == null)
                _raycaster = GetComponent<GraphicRaycaster>();

            if (_canvas != null)
                _canvas.enabled = visible;
            if (_raycaster != null)
                _raycaster.enabled = visible;

            if (visible)
                SuppressTestPanel();
            else
                RestoreTestPanel();
        }

        private void SuppressTestPanel()
        {
            if (_testPanelSuppressed)
                return;
            if (_testPanel == null)
                _testPanel = FindFirstObjectByType<MMOTestbedRuntimePanel>();
            if (_testPanel == null)
                return;

            _testPanelWasEnabled = _testPanel.enabled;
            if (_testPanelWasEnabled)
                _testPanel.enabled = false;
            _testPanelSuppressed = true;
        }

        private void RestoreTestPanel()
        {
            if (!_testPanelSuppressed)
                return;
            if (_testPanel != null && _testPanelWasEnabled)
                _testPanel.enabled = true;
            _testPanelSuppressed = false;
            _testPanelWasEnabled = false;
        }

        private void SetConnectionStatus(string message)
        {
            if (connectionStatusText != null)
                connectionStatusText.text = string.IsNullOrWhiteSpace(message) ? " " : message;
        }

        private void SetLoginStatus(string message)
        {
            if (loginStatusText != null)
                loginStatusText.text = string.IsNullOrWhiteSpace(message) ? " " : message;
        }

        private void SetCharacterStatus(string message)
        {
            if (statusText != null)
                statusText.text = string.IsNullOrWhiteSpace(message) ? " " : message;
        }

        private static string BuildLobbyStatus(byte stateValue, string error)
        {
            PlayerSessionState state = (PlayerSessionState)stateValue;
            switch (state)
            {
                case PlayerSessionState.Connected:
                case PlayerSessionState.Authenticating:
                    return "Authentication has not completed.";
                case PlayerSessionState.CharacterLobby:
                    return string.IsNullOrWhiteSpace(error) ? "Character Lobby request failed." : error;
                case PlayerSessionState.LoadingCharacter:
                case PlayerSessionState.AwaitingWorldEntry:
                case PlayerSessionState.InWorld:
                    return "A character session is already active.";
                case PlayerSessionState.Disconnecting:
                case PlayerSessionState.Closed:
                    return "Character session is closing.";
                default:
                    return string.IsNullOrWhiteSpace(error) ? "Character Lobby is unavailable." : error;
            }
        }

        private static string BuildEnterStatus(EnterCharacterResponseMessage response) =>
            string.IsNullOrWhiteSpace(response.error)
                ? $"Character entry failed (code {response.failure})."
                : response.error;

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only authored-UI binding hook used by the standalone UI authoring tool.
        /// This keeps CharacterSelectShell as the one runtime owner of auto-connect, login,
        /// create-account, admission, and frontend state while avoiding runtime reflection
        /// or duplicate authentication code.
        /// </summary>
        public void ConfigureStandaloneLoginForEditor(
            GameObject authoredLoginPanel,
            GameObject authoredCharacterPanel,
            InputField authoredAccountInput,
            InputField authoredPasswordInput,
            Text authoredLoginStatus,
            Button authoredLoginButton,
            Button authoredCreateAccountButton,
            Button authoredRefreshStatusButton,
            Button authoredQuitButton,
            Toggle authoredRememberAccountToggle,
            Text authoredServerAvailabilityText,
            Text authoredConnectionStatusText,
            Text authoredCharacterStatusText,
            Button authoredDeleteCharacterButton,
            CharacterSelectPreviewController authoredPreviewController,
            Color authoredServerOnlineColor,
            Color authoredServerCheckingColor,
            Color authoredServerOfflineColor)
        {
            connectionPanel = null;
            loginPanel = authoredLoginPanel;
            characterPanel = authoredCharacterPanel;
            useFrontendConnectionPanel = false;

            addressInput = null;
            portInput = null;
            connectionStatusText = authoredConnectionStatusText;
            startClientButton = null;

            accountInput = authoredAccountInput;
            passwordInput = authoredPasswordInput;
            loginStatusText = authoredLoginStatus;
            loginButton = authoredLoginButton;
            createAccountButton = authoredCreateAccountButton;
            loginDisconnectButton = authoredRefreshStatusButton;
            quitButton = authoredQuitButton;
            rememberAccountToggle = authoredRememberAccountToggle;
            serverAvailabilityText = authoredServerAvailabilityText;
            serverOnlineColor = authoredServerOnlineColor;
            serverCheckingColor = authoredServerCheckingColor;
            serverOfflineColor = authoredServerOfflineColor;
            autoConnectConfiguredServer = true;

            statusText = authoredCharacterStatusText;
            characterNameInput = null;
            createCharacterButton = null;
            refreshButton = null;
            enterWorldButton = null;
            deleteCharacterButton = authoredDeleteCharacterButton;
            disconnectButton = null;
            slots = Array.Empty<CharacterSelectSlotView>();
            previewController = authoredPreviewController;
            hideAfterWorldEntry = true;
        }


        /// <summary>
        /// Editor-only binding hook for the authored standalone Character Select surface.
        /// The existing CharacterSelectShell remains the sole runtime owner of roster cache,
        /// character creation, deletion, selection, world entry, and frontend state.
        /// </summary>
        public void ConfigureStandaloneCharacterLobbyForEditor(
            GameObject authoredCharacterPanel,
            Text authoredCharacterStatusText,
            Button authoredCreateCharacterButton,
            Button authoredEnterWorldButton,
            Button authoredDeleteCharacterButton,
            Button authoredDisconnectButton,
            CharacterSelectSlotView[] authoredSlots,
            CharacterSelectPreviewController authoredPreviewController)
        {
            characterPanel = authoredCharacterPanel;
            statusText = authoredCharacterStatusText;
            characterNameInput = null;
            createCharacterButton = authoredCreateCharacterButton;
            refreshButton = null;
            enterWorldButton = authoredEnterWorldButton;
            deleteCharacterButton = authoredDeleteCharacterButton;
            disconnectButton = authoredDisconnectButton;
            slots = authoredSlots ?? Array.Empty<CharacterSelectSlotView>();
            previewController = authoredPreviewController;
            hideAfterWorldEntry = true;
        }
#endif
    }
}

#pragma warning restore 0414
