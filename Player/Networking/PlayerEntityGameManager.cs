using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using Game.UnityIntegration;
using LiteNetLib;
using LiteNetLibManager;
using LiteNetLib.Utils;
using Player.Shared;
using UnityEngine;

namespace Player.Networking
{
    /// <summary>
    /// Clean testbed manager using LiteNetLibManager's normal EnterGame/Ready/player-spawn flow.
    /// Command line modes are intentionally simple so headless bot processes can use the same scene.
    /// </summary>
    public sealed partial class PlayerEntityGameManager : LiteNetLibGameManager
    {
        public enum ClientServerTarget
        {
            Local = 0,
            Live = 1,
        }

        [Header("Client Startup")]
        [SerializeField] private ClientServerTarget clientServerTarget = ClientServerTarget.Local;
        [SerializeField] private string localGameServerAddress = "127.0.0.1";
        [SerializeField] private string liveGameServerAddress = "67.162.29.227";
        public bool logCommandLineMode = true;

        [Header("Backend Server")]
        [SerializeField] private string backendInternalBaseUrl = "http://127.0.0.1:8444";
        [SerializeField] private string authenticationServiceBaseUrl = "https://127.0.0.1:8443";
        [SerializeField] private string liveAuthenticationServiceBaseUrl = "https://67.162.29.227:8443";
        [SerializeField] private string authenticationCertificateSha256 = string.Empty;

        public ClientServerTarget SelectedClientServerTarget => clientServerTarget;
        public string SelectedGameServerAddress => ResolveSelectedGameServerAddress();
        public string ConfiguredClientGameServerAddress => ResolveConfiguredClientGameServerAddress();
        public int ConfiguredClientGameServerPort => ResolveConfiguredClientGameServerPort();
        public string BackendInternalBaseUrl => ResolveBackendInternalBaseUrl();
        public string AuthenticationServiceBaseUrl => ResolveAuthenticationServiceBaseUrl();
        public string AuthenticationCertificateSha256 => ResolveAuthenticationCertificateSha256();

        private CharacterSessionRuntimeHost _characterSessionRuntimeHost;
        private readonly Dictionary<long, PlayerSessionHandle> _playerSessionHandles =
            new Dictionary<long, PlayerSessionHandle>();
        private bool _serverStopping;

        public ushort AuthoritativeTickRate { get; private set; } = PlayerEntityProtocol.DefaultTickRate;

        protected override void Start()
        {
            base.Start();
            Application.runInBackground = true;
            string[] args = Environment.GetCommandLineArgs();

            // Apply the Inspector Local/Live preset first. Explicit command-line values
            // remain authoritative for bots, builds, and automation.
            string address = ResolveConfiguredClientGameServerAddress();
            int port = ResolveConfiguredClientGameServerPort();
            networkAddress = address;
            networkPort = port;

            // One startup diagnostic only. This is intentionally not a recurring log; it
            // makes Local/Live endpoint mismatches immediately visible during client testing.
            Debug.Log(
                $"[PlayerEntity] Client endpoint configured: Target={clientServerTarget}, " +
                $"GameServer={address}:{port}, Auth={AuthenticationServiceBaseUrl}");

            // Unity is now the MMO client only. The authoritative runtime is the
            // standalone .NET GameServer, so this application no longer auto-starts
            // a Unity server under UNITY_SERVER or any command-line mode.
            if (HasArg(args, "-mmoBot") || HasArg(args, "-mmoClient"))
            {
                if (logCommandLineMode)
                {
                    Debug.Log(
                        $"[PlayerEntity] Starting {clientServerTarget} client to " +
                        $"{address}:{port} (Auth={AuthenticationServiceBaseUrl})");
                }

                StartClient(address, port);
            }
        }

        /// <summary>
        /// Production MMO architecture does not support combined client/server Host mode.
        /// Keep the generic LiteNetLib capability inside the plugin, but reject it at the
        /// MMO application boundary so future UI/test code cannot accidentally create
        /// authoritative server state inside a player client process.
        /// </summary>
        public override bool StartHost(bool isOfflineConnection = false)
        {
            Debug.LogError("[PlayerEntity] Host mode is disabled. Start a dedicated GameServer and connect with StartClient().");
            return false;
        }

        // REMOVE ME AFTER CLIENT-NETWORKING SPLIT:
        // Everything from the Unity server lifecycle below is retained only as a staged
        // compile bridge while the remaining mixed request-handler partials are converted
        // to client-only registration. No normal MMO startup path reaches this code now.

        public override void OnStartServer()
        {
            // One Character Session composition root per authoritative server run.
            // This is a plain C# host; no second MonoBehaviour/network lifecycle is introduced.
            _serverStopping = false;
            _playerSessionHandles.Clear();
            ClearPendingPlayerResourceDeltas();
            ClearPendingPlayerStatusEffectDeltas();
            ClearPendingPlayerItemChanges();
            ClearPlayerChatState();
            ClearPendingPlayerServiceStatuses();
            ResetAuthenticationAdmissionTracking();
            _characterSessionRuntimeHost = CreateCharacterSessionRuntimeHost();
            Debug.Log($"[PlayerEntity] Backend Server internal API: {BackendInternalBaseUrl}");

            base.OnStartServer();
            _characterSessionRuntimeHost.PlayerResourceChanged += HandleAuthoritativeResourceChanged;
            _characterSessionRuntimeHost.PlayerStatusEffectChanged += HandleAuthoritativeStatusEffectChanged;
            _characterSessionRuntimeHost.PlayerItemsChanged += HandleAuthoritativePlayerItemsChanged;
            _characterSessionRuntimeHost.BackendEventStreamAvailabilityChanged += HandleBackendEventStreamAvailabilityChanged;
            _characterSessionRuntimeHost.StartGameplayScheduling();
            StartPersistenceCheckpointing();
        }

        public override void OnPeerConnected(long connectionId)
        {
            // Preserve LiteNetLib's canonical peer/player registration first.
            base.OnPeerConnected(connectionId);

            if (!IsServer)
                return;

            CharacterSessionRuntimeHost runtimeHost = GetOrCreateCharacterSessionRuntimeHost();

            // LiteNetLib should not deliver a second live connect for the same id. Do not
            // replace the exact generation if it does; replacement is only valid after the
            // prior disconnect callback has removed the old mapping.
            if (_playerSessionHandles.ContainsKey(connectionId))
            {
                Debug.LogWarning(
                    $"[PlayerEntity] Ignoring duplicate Character Session open for connection {connectionId}.");
                return;
            }

            var session = runtimeHost.OpenConnection(connectionId);
            _playerSessionHandles.Add(connectionId, session.Handle);
            TrackAuthenticationAdmission(session.Handle);
        }

        public override void OnPeerDisconnected(
            long connectionId,
            DisconnectReason reason,
            SocketError socketError)
        {
            RemovePlayerChatState(connectionId);
            CharacterSessionRuntimeHost runtimeHost = _characterSessionRuntimeHost;
            PlayerSessionHandle handle = default(PlayerSessionHandle);
            bool finalizeExactSession = false;

            // Begin the exact stored generation before LiteNetLib destroys owned objects.
            // CanonicalPlayerSessionBridge may capture final authoritative transform state
            // here, while the identity still exists.
            if (runtimeHost != null &&
                _playerSessionHandles.TryGetValue(connectionId, out handle))
            {
                runtimeHost.DeactivatePlayerGameplayRuntime(handle);
                finalizeExactSession =
                    runtimeHost.TryBeginDisconnectBeforeCanonicalDestroy(handle);
                RemoveAuthenticationAdmission(handle);

                // Remove only the mapping we captured. The async finalizer below retains the
                // exact PlayerSessionHandle, so a reused connection id cannot redirect it to
                // a replacement generation.
                if (_playerSessionHandles.TryGetValue(
                        connectionId,
                        out PlayerSessionHandle currentHandle) &&
                    currentHandle == handle)
                {
                    _playerSessionHandles.Remove(connectionId);
                }
            }

            // Preserve LiteNetLib's canonical disconnect path unchanged:
            // subscription cleanup -> owned object cleanup -> player removal.
            base.OnPeerDisconnected(connectionId, reason, socketError);

            if (finalizeExactSession)
            {
                if (_serverStopping)
                {
                    // Shutdown is a durability barrier. Do not leave an HTTP save/close
                    // continuation racing disposal of the backend client after base
                    // OnStopServer returns. Blocking is intentional only during shutdown.
                    try
                    {
                        runtimeHost.SaveAndCloseDisconnectedSessionAsync(
                                handle,
                                CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
                else
                {
                    FinalizeCharacterSessionDisconnectAsync(runtimeHost, handle).Forget();
                }
            }
        }

        public override void OnStopServer()
        {
            _serverStopping = true;
            StopPersistenceCheckpointing();

            // Graceful shutdown is a durability boundary. Capture/save while canonical
            // identities still exist, before LiteNetLib destroys them. The synchronous
            // wait is intentional here: server shutdown must not dispose the backend
            // client while final checkpoint HTTP requests are still in flight.
            try
            {
                FlushAllCharacterCheckpointsForShutdownBlocking();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            if (_characterSessionRuntimeHost != null)
            {
                _characterSessionRuntimeHost.PlayerResourceChanged -= HandleAuthoritativeResourceChanged;
                _characterSessionRuntimeHost.PlayerStatusEffectChanged -= HandleAuthoritativeStatusEffectChanged;
                _characterSessionRuntimeHost.PlayerItemsChanged -= HandleAuthoritativePlayerItemsChanged;
                _characterSessionRuntimeHost.BackendEventStreamAvailabilityChanged -= HandleBackendEventStreamAvailabilityChanged;
                _characterSessionRuntimeHost.StopGameplayScheduling();
            }

            // Let LiteNetLib complete canonical shutdown/cleanup. Disconnect callbacks
            // should now find clean location state and can close exact sessions without
            // racing backend disposal.
            base.OnStopServer();

            _playerSessionHandles.Clear();
            ClearPendingPlayerResourceDeltas();
            ClearPendingPlayerStatusEffectDeltas();
            ClearPendingPlayerItemChanges();
            ClearPendingPlayerServiceStatuses();
            ResetAuthenticationAdmissionTracking();
            _characterSessionRuntimeHost?.Dispose();
            _characterSessionRuntimeHost = null;
            _serverStopping = false;
        }

        private CharacterSessionRuntimeHost GetOrCreateCharacterSessionRuntimeHost()
        {
            if (_characterSessionRuntimeHost == null)
                _characterSessionRuntimeHost = CreateCharacterSessionRuntimeHost();
            return _characterSessionRuntimeHost;
        }

        private CharacterSessionRuntimeHost CreateCharacterSessionRuntimeHost()
        {
            string key = LoadBackendGameServerKey();
            return new CharacterSessionRuntimeHost(
                this,
                BackendInternalBaseUrl,
                key);
        }

        private string ResolveBackendInternalBaseUrl()
        {
            string configured = GetArg(
                Environment.GetCommandLineArgs(),
                "-mmoBackendInternal",
                backendInternalBaseUrl);
            return string.IsNullOrWhiteSpace(configured)
                ? "http://127.0.0.1:8444"
                : configured.Trim().TrimEnd('/');
        }

        private string ResolveSelectedGameServerAddress()
        {
            string configured = clientServerTarget == ClientServerTarget.Live
                ? liveGameServerAddress
                : localGameServerAddress;

            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Trim();

            return clientServerTarget == ClientServerTarget.Live
                ? "67.162.29.227"
                : "127.0.0.1";
        }

        private string ResolveConfiguredClientGameServerAddress()
        {
            string configured = GetArg(
                Environment.GetCommandLineArgs(),
                "-mmoAddress",
                ResolveSelectedGameServerAddress());
            return string.IsNullOrWhiteSpace(configured)
                ? ResolveSelectedGameServerAddress()
                : configured.Trim();
        }

        private int ResolveConfiguredClientGameServerPort()
        {
            return Mathf.Clamp(
                GetIntArg(Environment.GetCommandLineArgs(), "-mmoPort", networkPort),
                1,
                ushort.MaxValue);
        }

        private string ResolveAuthenticationServiceBaseUrl()
        {
            string[] args = Environment.GetCommandLineArgs();

            // Explicit auth URL always wins.
            string commandLineAuthUrl = GetArg(args, "-mmoAuthUrl", string.Empty);
            if (!string.IsNullOrWhiteSpace(commandLineAuthUrl))
                return commandLineAuthUrl.Trim().TrimEnd('/');

            // If automation explicitly changes the GameServer host but does not provide
            // -mmoAuthUrl, keep auth on the matching host.
            string commandLineGameServerAddress = GetArg(args, "-mmoAddress", string.Empty);
            if (!string.IsNullOrWhiteSpace(commandLineGameServerAddress))
                return $"https://{commandLineGameServerAddress.Trim()}:8443";

            string configured = clientServerTarget == ClientServerTarget.Live
                ? liveAuthenticationServiceBaseUrl
                : authenticationServiceBaseUrl;

            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Trim().TrimEnd('/');

            return clientServerTarget == ClientServerTarget.Live
                ? "https://67.162.29.227:8443"
                : "https://127.0.0.1:8443";
        }

        private string ResolveAuthenticationCertificateSha256()
        {
            string commandLine = GetArg(
                Environment.GetCommandLineArgs(),
                "-mmoAuthCertSha256",
                string.Empty);
            if (!string.IsNullOrWhiteSpace(commandLine))
                return NormalizeSha256(commandLine);
            if (!string.IsNullOrWhiteSpace(authenticationCertificateSha256))
                return NormalizeSha256(authenticationCertificateSha256);

            foreach (string candidate in BackendDataFileCandidates("backend-cert.sha256"))
            {
                if (!File.Exists(candidate))
                    continue;

                string value = File.ReadAllText(candidate).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    return NormalizeSha256(value);
            }

            return string.Empty;
        }

        private string LoadBackendGameServerKey()
        {
            string explicitPath = GetArg(
                Environment.GetCommandLineArgs(),
                "-mmoBackendKey",
                string.Empty);
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return ReadRequiredBackendKey(Path.GetFullPath(explicitPath));

            foreach (string candidate in BackendDataFileCandidates("game-server-auth.key"))
            {
                if (File.Exists(candidate))
                    return ReadRequiredBackendKey(candidate);
            }

            throw new FileNotFoundException(
                "Backend Server game-server key was not found. Start BackendServer once or pass -mmoBackendKey <path>.");
        }

        private static string ReadRequiredBackendKey(string path)
        {
            string value = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Backend Server game-server key is empty: {path}");
            return value;
        }

        private static IEnumerable<string> BackendDataFileCandidates(string fileName)
        {
            string current = Directory.GetCurrentDirectory();
            yield return Path.GetFullPath(Path.Combine(current, "Server", "Data", fileName));
            yield return Path.GetFullPath(Path.Combine(current, "..", "Data", fileName));
        }

        private static string NormalizeSha256(string value) =>
            (value ?? string.Empty)
                .Replace(":", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .Trim()
                .ToUpperInvariant();

        private async UniTask FinalizeCharacterSessionDisconnectAsync(
            CharacterSessionRuntimeHost runtimeHost,
            PlayerSessionHandle handle)
        {
            try
            {
                bool closed = await runtimeHost.SaveAndCloseDisconnectedSessionAsync(
                    handle,
                    CancellationToken.None);

                if (!closed)
                {
                    Debug.LogWarning(
                        $"[PlayerEntity] Character Session disconnect was not finalized for exact session {handle}.");
                }
            }
            catch (Exception ex)
            {
                // CharacterSessionRuntimeHost intentionally keeps the session open if save
                // fails, so dirty state is not discarded. Surface the failure without
                // changing LiteNetLib's already-completed cleanup semantics.
                Debug.LogException(ex);
            }
        }

        public int ReadyPlayersCount
        {
            get
            {
                int count = 0;
                var players = GetPlayers();
                while (players.MoveNext())
                {
                    if (players.Current.Value != null && players.Current.Value.IsReady)
                        count++;
                }
                return count;
            }
        }

        public int SpawnedPlayerObjectsCount
        {
            get
            {
                int count = 0;
                var players = GetPlayers();
                while (players.MoveNext())
                {
                    if (players.Current.Value != null)
                        count += players.Current.Value.SpawnedObjectsCount;
                }
                return count;
            }
        }

        public override void SerializeEnterGameData(NetDataWriter writer)
        {
            base.SerializeEnterGameData(writer);
            writer.Put(PlayerEntityProtocol.Version);
        }

        public override async UniTask<bool> DeserializeEnterGameData(uint requestId, long connectionId, EnterGameRequestMessage request, NetDataReader reader)
        {
            if (!await base.DeserializeEnterGameData(requestId, connectionId, request, reader))
                return false;
            ushort protocol = reader.GetUShort();
            if (protocol != PlayerEntityProtocol.Version)
            {
                Debug.LogWarning($"[PlayerEntity] Refused connection {connectionId}: PlayerEntity protocol {protocol} != {PlayerEntityProtocol.Version}.");
                return false;
            }
            return true;
        }

        protected override void WriteExtraEnterGameResponse(uint requestId, long connectionId, EnterGameRequestMessage request, AckResponseCode responseCode, NetDataWriter writer)
        {
            base.WriteExtraEnterGameResponse(requestId, connectionId, request, responseCode, writer);
            writer.Put(PlayerEntityProtocol.Version);
            writer.Put((ushort)Mathf.Clamp(updateFps, 1, ushort.MaxValue));
        }

        protected override void ReadExtraEnterGameResponse(AckResponseCode responseCode, EnterGameResponseMessage response, NetDataReader reader)
        {
            base.ReadExtraEnterGameResponse(responseCode, response, reader);
            if (responseCode != AckResponseCode.Success || reader == null)
                return;
            ushort protocol = reader.GetUShort();
            ushort tickRate = reader.GetUShort();
            if (protocol != PlayerEntityProtocol.Version)
            {
                Debug.LogError($"[PlayerEntity] Server PlayerEntity protocol {protocol} != client {PlayerEntityProtocol.Version}. Disconnecting.");
                StopClient();
                return;
            }
            AuthoritativeTickRate = (ushort)Mathf.Clamp(tickRate, 1, ushort.MaxValue);
        }

        private void ApplyExplicitLoadTestAdmission(string[] args)
        {
            if (!HasArg(args, "-mmoLoadTestServer"))
                return;

            int singleAddressCap = Mathf.Clamp(
                GetIntArg(args, "-mmoLoadTestSingleIpCap", 4096),
                32,
                100000);

            int attemptsPerSecond = Mathf.Clamp(
                GetIntArg(args, "-mmoLoadTestConnectRate", 1000),
                32,
                100000);

            serverFoundationSettings.network.maxAttemptsPerAddressPerWindow =
                singleAddressCap;
            serverFoundationSettings.network.maxConnectionAttemptsPerSecond =
                attemptsPerSecond;

            Debug.LogWarning(
                $"[PlayerEntity] LOAD TEST SERVER admission override enabled. " +
                $"Single-address attempts/window={singleAddressCap}, " +
                $"global attempts/s={attemptsPerSecond}. " +
                $"Do not use -mmoLoadTestServer for a public production deployment.");
        }

        private void ApplyPort(string[] args)
        {
            networkPort = GetIntArg(args, "-mmoPort", networkPort);
        }

        public static bool HasArg(string[] args, string key)
        {
            for (int i = 0; i < args.Length; ++i)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static string GetArg(string[] args, string key, string fallback)
        {
            for (int i = 0; i + 1 < args.Length; ++i)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }

        public static int GetIntArg(string[] args, string key, int fallback)
        {
            string raw = GetArg(args, key, null);
            return int.TryParse(raw, out int value) ? value : fallback;
        }
    }
}
