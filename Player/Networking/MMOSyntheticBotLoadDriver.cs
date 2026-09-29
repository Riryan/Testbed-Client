using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Shared;
using UnityEngine;

namespace Player.Networking
{
    /// <summary>
    /// One Unity process can host hundreds of real LiteNetLib client connections.
    ///
    /// Each synthetic connection performs the same server-facing PlayerEntity path:
    /// connection -> EnterGame -> protocol negotiation -> Ready -> real server-side
    /// PlayerEntity spawn -> ping/pong -> sequenced movement RPC -> AOI/replication.
    ///
    /// It deliberately does not instantiate replicated Unity presentation objects.
    /// That keeps the load-generator machine cheap while retaining the real server
    /// networking, authority, AOI, replication and governance paths.
    /// </summary>
    public sealed class MMOSyntheticBotLoadDriver : MonoBehaviour
    {
        private const string ArgCount = "-mmoSyntheticBots";
        private const string ArgAddress = "-mmoAddress";
        private const string ArgPort = "-mmoPort";
        private const string ArgConnectKey = "-mmoConnectKey";
        private const string ArgRamp = "-mmoSyntheticRamp";
        private const string ArgSeed = "-mmoSyntheticSeed";
        private const string ArgRadius = "-mmoSyntheticRouteRadius";
        private const string ArgSprint = "-mmoSyntheticSprintPercent";
        private const string ArgDuration = "-mmoSyntheticDuration";
        private const string ArgNoMovement = "-mmoSyntheticNoMovement";

        private const int MaxSyntheticConnections = 5000;
        private const int MaxConnectionStartsPerFrame = 64;
        private const double SummaryIntervalSeconds = 5.0;

        private readonly List<SyntheticConnection> _connections =
            new List<SyntheticConnection>(256);

        private PlayerEntityGameManager _manager;
        private string _address;
        private int _port;
        private string _connectKey;
        private int _targetCount;
        private int _rampPerSecond;
        private int _seedBase;
        private float _routeRadius;
        private float _sprintProbability;
        private bool _movementEnabled;
        private double _durationSeconds;

        private int _movementRpcElementId;
        private int _snapshotElementId;
        private int _appearanceElementId;
        private uint _packetVersion;

        private double _startedAt;
        private double _nextConnectionAt;
        private double _nextSummaryAt;
        private long _lastSummaryTx;
        private long _lastSummaryRx;
        private long _lastSummaryMovement;

        private StreamWriter _metricsWriter;
        private string _metricsPath;

        public int TargetCount => _targetCount;
        public int StartedCount => _connections.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
#if !UNITY_SERVER
            string[] args = Environment.GetCommandLineArgs();
            if (!PlayerEntityGameManager.HasArg(args, ArgCount))
                return;

            PlayerEntityGameManager manager =
                UnityEngine.Object.FindFirstObjectByType<PlayerEntityGameManager>(
                    FindObjectsInactive.Include);

            if (manager == null)
            {
                Debug.LogError(
                    "[MMOSyntheticLoad] MMOPlayerEntityGameManager was not found. " +
                    "Use the generated PlayerEntity test scene.");
                return;
            }

            if (manager.GetComponent<MMOSyntheticBotLoadDriver>() == null)
                manager.gameObject.AddComponent<MMOSyntheticBotLoadDriver>();
#endif
        }

        private void Start()
        {
#if UNITY_SERVER
            enabled = false;
#else
            _manager = GetComponent<PlayerEntityGameManager>();
            if (_manager == null)
            {
                Debug.LogError("[MMOSyntheticLoad] Manager component is missing.");
                enabled = false;
                return;
            }

            string[] args = Environment.GetCommandLineArgs();

            _targetCount = Mathf.Clamp(
                PlayerEntityGameManager.GetIntArg(args, ArgCount, 0),
                1,
                MaxSyntheticConnections);

            _address = PlayerEntityGameManager.GetArg(
                args,
                ArgAddress,
                _manager.networkAddress);

            _port = Mathf.Clamp(
                PlayerEntityGameManager.GetIntArg(
                    args,
                    ArgPort,
                    _manager.networkPort),
                1,
                65535);

            _connectKey = PlayerEntityGameManager.GetArg(
                args,
                ArgConnectKey,
                ResolveConnectKey());

            _rampPerSecond = Mathf.Clamp(
                PlayerEntityGameManager.GetIntArg(args, ArgRamp, 20),
                1,
                1000);

            _seedBase = PlayerEntityGameManager.GetIntArg(
                args,
                ArgSeed,
                100000);

            _routeRadius = Mathf.Clamp(
                GetFloatArg(args, ArgRadius, 20f),
                3f,
                250f);

            _sprintProbability = Mathf.Clamp01(
                GetFloatArg(args, ArgSprint, 25f) / 100f);

            _durationSeconds = Math.Max(
                0.0,
                GetDoubleArg(args, ArgDuration, 0.0));

            _movementEnabled =
                !PlayerEntityGameManager.HasArg(args, ArgNoMovement);

            _packetVersion = _manager.PacketVersion();

            if (!ResolvePlayerEntityProtocolLayout())
            {
                enabled = false;
                return;
            }

            Application.runInBackground = true;
            OpenMetricsLog();

            _startedAt = Time.realtimeSinceStartupAsDouble;
            _nextConnectionAt = _startedAt;
            _nextSummaryAt = _startedAt + SummaryIntervalSeconds;

            Debug.Log(
                $"[MMOSyntheticLoad] Starting {_targetCount} real LiteNetLib connections " +
                $"to {_address}:{_port} from ONE Unity process. " +
                $"Ramp={_rampPerSecond}/s, movement={_movementEnabled}, " +
                $"routeRadius={_routeRadius:F1}m, sprint={_sprintProbability * 100f:F0}%.");

            Debug.Log(
                "[MMOSyntheticLoad] IMPORTANT: for >20 connections from one public/LAN IP, " +
                "start the testbed server with -mmoLoadTestServer. " +
                "That flag only relaxes single-source connection admission for explicit load tests.");
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            double now = Time.realtimeSinceStartupAsDouble;

            StartDueConnections(now);

            for (int i = 0; i < _connections.Count; ++i)
                _connections[i].Update(now);

            if (now >= _nextSummaryAt)
            {
                LogSummary(now);
                _nextSummaryAt = now + SummaryIntervalSeconds;
            }

            if (_durationSeconds > 0.0 &&
                now - _startedAt >= _durationSeconds)
            {
                Debug.Log(
                    $"[MMOSyntheticLoad] Duration {_durationSeconds:F0}s complete. Stopping.");
                StopAll();
                Application.Quit(0);
            }
#endif
        }

        private void OnDestroy()
        {
            StopAll();
        }

        private void OnApplicationQuit()
        {
            StopAll();
        }

        private void OnGUI()
        {
#if !UNITY_SERVER
            if (Application.isBatchMode)
                return;

            AggregateMetrics metrics = CaptureMetrics();

            GUI.depth = -900;
            GUILayout.BeginArea(
                new Rect(Screen.width - 430f, 12f, 418f, 330f),
                GUI.skin.box);

            GUILayout.Label("Synthetic Connection Load");
            GUILayout.Label($"Target / Started: {_targetCount} / {_connections.Count}");
            GUILayout.Label(
                $"Connected / Entered / Ready / Active: " +
                $"{metrics.connected} / {metrics.entered} / {metrics.ready} / {metrics.active}");
            GUILayout.Label(
                $"Failed / Disconnected / Protocol errors: " +
                $"{metrics.failed} / {metrics.disconnected} / {metrics.protocolErrors}");
            GUILayout.Label(
                $"Observed spawn/destroy: {metrics.observedSpawns:n0} / {metrics.observedDestroys:n0}");
            GUILayout.Label(
                $"Movement RPCs: {metrics.movementCommands:n0}");
            GUILayout.Label(
                $"TX / RX: {metrics.txBytes / (1024.0 * 1024.0):F2} / " +
                $"{metrics.rxBytes / (1024.0 * 1024.0):F2} MB");
            GUILayout.Label($"Avg transport RTT: {metrics.averageRttMs:F1} ms");
            GUILayout.Label($"Server: {_address}:{_port}");
            GUILayout.Label("This process has no replicated presentation GameObjects.");

            if (GUILayout.Button("Stop Synthetic Load"))
            {
                StopAll();
                enabled = false;
            }

            GUILayout.EndArea();
#endif
        }

        private void StartDueConnections(double now)
        {
            if (_connections.Count >= _targetCount)
                return;

            double interval = 1.0 / _rampPerSecond;
            int startsThisFrame = 0;

            while (_connections.Count < _targetCount &&
                   now >= _nextConnectionAt &&
                   startsThisFrame < MaxConnectionStartsPerFrame)
            {
                int index = _connections.Count;
                SyntheticConnection connection =
                    new SyntheticConnection(
                        index,
                        _seedBase + index,
                        _address,
                        _port,
                        _connectKey,
                        _packetVersion,
                        _movementRpcElementId,
                        _snapshotElementId,
                        _appearanceElementId,
                        _routeRadius,
                        _sprintProbability,
                        _movementEnabled);

                _connections.Add(connection);
                connection.Start(now);

                _nextConnectionAt += interval;
                startsThisFrame++;
            }

            // A long hitch must not cause thousands of socket starts in one frame.
            if (startsThisFrame >= MaxConnectionStartsPerFrame &&
                _nextConnectionAt < now)
            {
                _nextConnectionAt = now + interval;
            }
        }

        private bool ResolvePlayerEntityProtocolLayout()
        {
            LiteNetLibAssets assets = _manager.Assets;
            if (assets == null || assets.playerPrefab == null)
            {
                Debug.LogError(
                    "[MMOSyntheticLoad] LiteNetLibAssets.playerPrefab is NULL. " +
                    "Run Tools -> MMO Testbed -> Repair PlayerEntity Test Scene.");
                return false;
            }

            PlayerEntityNetwork network =
                assets.playerPrefab.GetComponent<PlayerEntityNetwork>();

            if (network == null)
            {
                Debug.LogError(
                    "[MMOSyntheticLoad] Player prefab has no MMOPlayerEntityNetwork.");
                return false;
            }

            byte behaviourIndex = network.BehaviourIndex;
            string typeName = typeof(PlayerEntityNetwork).FullName;

            _movementRpcElementId = StableHash(
                $"{typeName}_{behaviourIndex}_ServerReceiveMovement");

            _snapshotElementId = StableHash(
                $"{typeName}_{behaviourIndex}__snapshot");

            _appearanceElementId = StableHash(
                $"{typeName}_{behaviourIndex}__appearance");

            Debug.Log(
                $"[MMOSyntheticLoad] Protocol layout: behaviourIndex={behaviourIndex}, " +
                $"movementRpc={_movementRpcElementId}, snapshot={_snapshotElementId}, " +
                $"appearance={_appearanceElementId}.");

            return true;
        }

        private string ResolveConnectKey()
        {
            LiteNetLibTransportFactory factory =
                _manager.GetComponent<LiteNetLibTransportFactory>();

            if (factory != null && !string.IsNullOrWhiteSpace(factory.connectKey))
                return factory.connectKey;

            return "SampleConnectKey";
        }

        private AggregateMetrics CaptureMetrics()
        {
            AggregateMetrics result = default;
            long rttTotal = 0;
            int rttCount = 0;

            for (int i = 0; i < _connections.Count; ++i)
            {
                SyntheticConnection bot = _connections[i];

                if (bot.IsConnected)
                    result.connected++;
                if (bot.HasEnteredGame)
                    result.entered++;
                if (bot.IsReady)
                    result.ready++;
                if (bot.IsActive)
                    result.active++;
                if (bot.HasFailed)
                    result.failed++;
                if (bot.WasDisconnected)
                    result.disconnected++;

                result.protocolErrors += bot.ProtocolErrors;
                result.observedSpawns += bot.ObservedSpawns;
                result.observedDestroys += bot.ObservedDestroys;
                result.movementCommands += bot.MovementCommandsSent;
                result.txBytes += bot.TxBytes;
                result.rxBytes += bot.RxBytes;

                int rtt = bot.TransportRttMs;
                if (rtt >= 0)
                {
                    rttTotal += rtt;
                    rttCount++;
                }
            }

            result.averageRttMs =
                rttCount > 0 ? rttTotal / (float)rttCount : 0f;

            return result;
        }

        private void LogSummary(double now)
        {
            AggregateMetrics metrics = CaptureMetrics();
            double elapsed = Math.Max(0.001, now - (_nextSummaryAt - SummaryIntervalSeconds));

            long txDelta = metrics.txBytes - _lastSummaryTx;
            long rxDelta = metrics.rxBytes - _lastSummaryRx;
            long movementDelta =
                metrics.movementCommands - _lastSummaryMovement;

            _lastSummaryTx = metrics.txBytes;
            _lastSummaryRx = metrics.rxBytes;
            _lastSummaryMovement = metrics.movementCommands;

            string summary =
                $"target={_targetCount} started={_connections.Count} " +
                $"connected={metrics.connected} entered={metrics.entered} ready={metrics.ready} " +
                $"active={metrics.active} failed={metrics.failed} disconnected={metrics.disconnected} " +
                $"rpc/s={movementDelta / elapsed:F0} " +
                $"tx={txDelta / elapsed / 1024.0:F1}KB/s " +
                $"rx={rxDelta / elapsed / 1024.0:F1}KB/s " +
                $"avgRTT={metrics.averageRttMs:F1}ms " +
                $"spawn/destroy={metrics.observedSpawns}/{metrics.observedDestroys} " +
                $"protocolErrors={metrics.protocolErrors}";

            Debug.Log($"[MMOSyntheticLoad] {summary}");
            WriteMetricsLine(summary);

            if (_targetCount > 20 &&
                metrics.connected <= 20 &&
                _connections.Count >= Math.Min(_targetCount, 40))
            {
                Debug.LogWarning(
                    "[MMOSyntheticLoad] Connection count is near the normal single-IP " +
                    "admission limit. Start the server with -mmoLoadTestServer for a " +
                    "single-machine synthetic load test.");
            }
        }

        private void OpenMetricsLog()
        {
            try
            {
                _metricsPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "SyntheticBots.log");

                _metricsWriter = new StreamWriter(
                    _metricsPath,
                    false,
                    new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };

                _metricsWriter.WriteLine(
                    $"# MMO Synthetic Bot Metrics | UTC {DateTime.UtcNow:O}");
                _metricsWriter.WriteLine(
                    $"# target={_targetCount} server={_address}:{_port} ramp={_rampPerSecond}/s movement={_movementEnabled}");
                _metricsWriter.WriteLine(
                    "# Created only while -mmoSyntheticBots is active.");

                Debug.Log(
                    $"[MMOSyntheticLoad] Bot-only metrics log: {_metricsPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[MMOSyntheticLoad] Unable to open bot metrics log: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                _metricsWriter = null;
            }
        }

        private void WriteMetricsLine(string summary)
        {
            if (_metricsWriter == null)
                return;

            try
            {
                _metricsWriter.WriteLine(
                    $"{DateTime.UtcNow:O} {summary}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[MMOSyntheticLoad] Metrics write failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                try
                {
                    _metricsWriter.Dispose();
                }
                catch
                {
                }
                _metricsWriter = null;
            }
        }

        private void CloseMetricsLog()
        {
            if (_metricsWriter == null)
                return;

            try
            {
                _metricsWriter.Flush();
                _metricsWriter.Dispose();
            }
            catch
            {
            }

            _metricsWriter = null;
        }

        private void StopAll()
        {
            for (int i = 0; i < _connections.Count; ++i)
                _connections[i]?.Stop();

            _connections.Clear();
            CloseMetricsLog();
        }

        private static int StableHash(string id)
        {
            unchecked
            {
                int hash1 = 5381;
                int hash2 = hash1;

                for (int i = 0; i < id.Length && id[i] != '\0'; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ id[i];

                    if (i == id.Length - 1 || id[i + 1] == '\0')
                        break;

                    hash2 = ((hash2 << 5) + hash2) ^ id[i + 1];
                }

                return hash1 + (hash2 * 1566083941);
            }
        }

        private static float GetFloatArg(
            string[] args,
            string key,
            float fallback)
        {
            string raw = PlayerEntityGameManager.GetArg(
                args,
                key,
                null);

            return float.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value)
                ? value
                : fallback;
        }

        private static double GetDoubleArg(
            string[] args,
            string key,
            double fallback)
        {
            string raw = PlayerEntityGameManager.GetArg(
                args,
                key,
                null);

            return double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value)
                ? value
                : fallback;
        }

        private struct AggregateMetrics
        {
            public int connected;
            public int entered;
            public int ready;
            public int active;
            public int failed;
            public int disconnected;
            public long protocolErrors;
            public long observedSpawns;
            public long observedDestroys;
            public long movementCommands;
            public long txBytes;
            public long rxBytes;
            public float averageRttMs;
        }

        private sealed class SyntheticConnection : INetEventListener
        {
            private enum State : byte
            {
                Created,
                Connecting,
                Connected,
                Entered,
                Ready,
                Active,
                Failed,
                Disconnected,
                Stopped,
            }

            private const uint EnterRequestId = 1;
            private const uint ReadyRequestId = 2;
            private const double PingIntervalSeconds = 1.0;
            private const double ConnectTimeoutSeconds = 15.0;

            private readonly int _index;
            private readonly string _address;
            private readonly int _port;
            private readonly string _connectKey;
            private readonly uint _packetVersion;
            private readonly int _movementRpcElementId;
            private readonly int _snapshotElementId;
            private readonly int _appearanceElementId;
            private readonly float _routeRadius;
            private readonly bool _movementEnabled;

            private readonly NetDataWriter _writer =
                new NetDataWriter(true, 256);

            private readonly RttCalculator _rtt =
                new RttCalculator();

            private NetManager _netManager;
            private NetPeer _peer;
            private State _state;

            private long _serverConnectionId = -1;
            private uint _ownedObjectId;
            private ushort _serverTickRate =
                PlayerEntityProtocol.DefaultTickRate;

            private uint _movementSequence;
            private double _startedAt;
            private double _lastManualUpdateAt;
            private double _nextPingAt;
            private double _nextMovementAt;
            private float _phase;
            private float _directionSign;
            private bool _sprint;

            public long TxBytes { get; private set; }
            public long RxBytes { get; private set; }
            public long MovementCommandsSent { get; private set; }
            public long ObservedSpawns { get; private set; }
            public long ObservedDestroys { get; private set; }
            public long ProtocolErrors { get; private set; }

            public bool IsConnected =>
                _peer != null &&
                _peer.ConnectionState == ConnectionState.Connected;

            public bool HasEnteredGame =>
                _state >= State.Entered &&
                _state < State.Failed;

            public bool IsReady =>
                _state >= State.Ready &&
                _state < State.Failed;

            public bool IsActive =>
                _state == State.Active &&
                _ownedObjectId != 0;

            public bool HasFailed => _state == State.Failed;
            public bool WasDisconnected => _state == State.Disconnected;

            public int TransportRttMs =>
                IsConnected ? _peer.RoundTripTime : -1;

            public SyntheticConnection(
                int index,
                int seed,
                string address,
                int port,
                string connectKey,
                uint packetVersion,
                int movementRpcElementId,
                int snapshotElementId,
                int appearanceElementId,
                float routeRadius,
                float sprintProbability,
                bool movementEnabled)
            {
                _index = index;
                _address = address;
                _port = port;
                _connectKey = connectKey;
                _packetVersion = packetVersion;
                _movementRpcElementId = movementRpcElementId;
                _snapshotElementId = snapshotElementId;
                _appearanceElementId = appearanceElementId;
                _routeRadius = routeRadius;
                _movementEnabled = movementEnabled;

                uint rng = unchecked((uint)(seed == 0 ? index + 1 : seed));
                _phase = Next01(ref rng) * Mathf.PI * 2f;
                _directionSign = Next01(ref rng) < 0.5f ? -1f : 1f;
                _sprint = Next01(ref rng) < sprintProbability;
            }

            public void Start(double now)
            {
                if (_state != State.Created)
                    return;

                _startedAt = now;
                _state = State.Connecting;

                try
                {
                    _netManager = new NetManager(this)
                    {
                        ChannelsCount = 16,
                    };

                    if (!_netManager.StartInManualMode(0))
                    {
                        Fail("LiteNetLib NetManager.StartInManualMode() returned false.");
                        return;
                    }

                    _lastManualUpdateAt = now;

                    _peer = _netManager.Connect(
                        _address,
                        _port,
                        _connectKey);

                    if (_peer == null)
                    {
                        Fail("LiteNetLib Connect() returned null.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Fail($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            public void Update(double now)
            {
                if (_state == State.Stopped ||
                    _state == State.Failed ||
                    _state == State.Disconnected)
                {
                    return;
                }

                try
                {
                    if (_netManager != null)
                    {
                        _netManager.PollEvents();

                        float elapsedMilliseconds = (float)Math.Min(
                            250.0,
                            Math.Max(
                                0.1,
                                (now - _lastManualUpdateAt) * 1000.0));

                        _netManager.ManualUpdate(elapsedMilliseconds);
                        _lastManualUpdateAt = now;
                    }
                }
                catch (Exception ex)
                {
                    ProtocolErrors++;
                    Fail($"Manual network pump failed: {ex.Message}");
                    return;
                }

                if (_state == State.Connecting &&
                    now - _startedAt >= ConnectTimeoutSeconds)
                {
                    Fail("Connection timed out.");
                    return;
                }

                if (!IsConnected)
                    return;

                if (now >= _nextPingAt)
                {
                    SendPing();
                    _nextPingAt = now + PingIntervalSeconds;
                }

                if (_movementEnabled &&
                    IsActive &&
                    now >= _nextMovementAt)
                {
                    SendMovement(now);
                    double tickInterval =
                        1.0 / Math.Max(1, (int)_serverTickRate);

                    // Keep phase offsets naturally staggered even after a long frame.
                    _nextMovementAt = Math.Max(
                        _nextMovementAt + tickInterval,
                        now + tickInterval * 0.25);
                }
            }

            public void Stop()
            {
                if (_state == State.Stopped)
                    return;

                _state = State.Stopped;

                try
                {
                    _netManager?.Stop();
                }
                catch
                {
                }

                _peer = null;
                _netManager = null;
            }

            private void SendEnterGame()
            {
                _writer.Reset();
                _writer.PutPackedUShort(GameMsgTypes.Request);
                _writer.PutPackedUShort(GameReqTypes.EnterGame);
                _writer.PutPackedUInt(EnterRequestId);
                _writer.Put(new EnterGameRequestMessage
                {
                    packetVersion = _packetVersion,
                });
                _writer.Put(PlayerEntityProtocol.Version);

                Send(0, DeliveryMethod.ReliableUnordered);
            }

            private void SendReady()
            {
                _writer.Reset();
                _writer.PutPackedUShort(GameMsgTypes.Request);
                _writer.PutPackedUShort(GameReqTypes.ClientReady);
                _writer.PutPackedUInt(ReadyRequestId);

                // EmptyMessage has no payload; MMO testbed SerializeClientReadyData()
                // currently adds no custom data.
                Send(0, DeliveryMethod.ReliableUnordered);
            }

            private void SendPing()
            {
                _writer.Reset();
                _writer.PutPackedUShort(GameMsgTypes.Ping);
                _writer.Put(_rtt.GetPingMessage());
                Send(0, DeliveryMethod.ReliableUnordered);
            }

            private void SendPong(PingMessage ping)
            {
                _writer.Reset();
                _writer.PutPackedUShort(GameMsgTypes.Pong);
                _writer.Put(_rtt.GetPongMessage(ping));
                Send(0, DeliveryMethod.ReliableUnordered);
            }

            private void SendMovement(double now)
            {
                // A continuously rotating desired heading integrates into a bounded
                // circular route without requiring local transform/GameObject state.
                float walkSpeed = _sprint
                    ? PlayerMovementDefaults.SprintSpeed
                    : PlayerMovementDefaults.WalkSpeed;
                float angularSpeed =
                    walkSpeed / Mathf.Max(3f, _routeRadius);

                float angle =
                    _phase +
                    (float)((now - _startedAt) * angularSpeed) *
                    _directionSign;

                // Periodically stop for ~0.6s so moving->idle snapshot transitions are
                // exercised under load too.
                double cycle =
                    (now - _startedAt + (_index % 17) * 0.37) % 13.0;

                bool stopping = cycle < 0.6;

                Vector2 input = stopping
                    ? Vector2.zero
                    : new Vector2(
                        Mathf.Sin(angle),
                        Mathf.Cos(angle));

                byte flags =
                    !stopping && _sprint
                        ? (byte)PlayerEntityFlags.Sprinting
                        : (byte)0;

                MovementCommand command =
                    new MovementCommand
                    {
                        sequence = ++_movementSequence,
                        inputX = PlayerEntityQuantization.QuantizeInput(input.x),
                        inputZ = PlayerEntityQuantization.QuantizeInput(input.y),
                        yaw = PlayerEntityQuantization.QuantizeYaw(
                            angle * Mathf.Rad2Deg),
                        flags = flags,
                    };

                _writer.Reset();
                _writer.PutPackedUShort(GameMsgTypes.RPC);
                _writer.Put((byte)RPCReceivers.Server);
                _writer.PutPackedUInt(_ownedObjectId);
                _writer.PutPackedInt(_movementRpcElementId);
                _writer.Put(command);

                if (Send(1, DeliveryMethod.Sequenced))
                    MovementCommandsSent++;
            }

            private bool Send(
                byte channel,
                DeliveryMethod deliveryMethod)
            {
                if (!IsConnected)
                    return false;

                try
                {
                    int bytes = _writer.Length;
                    _peer.Send(
                        _writer,
                        channel,
                        deliveryMethod);

                    TxBytes += bytes;
                    return true;
                }
                catch (Exception ex)
                {
                    ProtocolErrors++;
                    Fail($"Send failed: {ex.Message}");
                    return false;
                }
            }

            private void HandlePacket(NetPacketReader reader)
            {
                int receivedBytes = reader.AvailableBytes;
                RxBytes += Math.Max(0, receivedBytes);

                try
                {
                    ushort messageType = reader.GetPackedUShort();

                    switch (messageType)
                    {
                        case GameMsgTypes.Response:
                            HandleResponse(reader);
                            break;

                        case GameMsgTypes.SyncBaseLine:
                            ConsumeBaseline(reader);
                            break;

                        case GameMsgTypes.SyncDelta:
                            ConsumeDelta(reader);
                            break;

                        case GameMsgTypes.Ping:
                            SendPong(reader.Get<PingMessage>());
                            break;

                        case GameMsgTypes.Pong:
                            _rtt.OnPong(reader.Get<PongMessage>());
                            break;

                        case GameMsgTypes.Disconnect:
                            _state = State.Disconnected;
                            break;

                        case GameMsgTypes.ServerError:
                        case GameMsgTypes.ServerSceneChange:
                        case GameMsgTypes.ServerSetObjectOwner:
                        case GameMsgTypes.RPC:
                            // These are legitimate server messages. Synthetic load bots
                            // intentionally do not create presentation-side objects.
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ProtocolErrors++;

                    if (ProtocolErrors <= 3)
                    {
                        Debug.LogWarning(
                            $"[MMOSyntheticLoad/Bot {_index}] Packet parse error: " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            private void HandleResponse(NetDataReader reader)
            {
                uint requestId = reader.GetPackedUInt();
                AckResponseCode responseCode =
                    reader.GetValue<AckResponseCode>();

                if (requestId == EnterRequestId)
                {
                    EnterGameResponseMessage response =
                        reader.Get<EnterGameResponseMessage>();

                    if (responseCode != AckResponseCode.Success)
                    {
                        Fail($"EnterGame refused: {responseCode}");
                        return;
                    }

                    _serverConnectionId = response.connectionId;

                    ushort protocol = reader.GetUShort();
                    ushort tickRate = reader.GetUShort();

                    if (protocol != PlayerEntityProtocol.Version)
                    {
                        Fail(
                            $"Protocol mismatch: server={protocol}, " +
                            $"client={PlayerEntityProtocol.Version}");
                        return;
                    }

                    _serverTickRate =
                        (ushort)Mathf.Clamp(
                            tickRate,
                            1,
                            ushort.MaxValue);

                    _state = State.Entered;
                    SendReady();
                    return;
                }

                if (requestId == ReadyRequestId)
                {
                    if (responseCode != AckResponseCode.Success)
                    {
                        Fail($"ClientReady refused: {responseCode}");
                        return;
                    }

                    _state = _ownedObjectId != 0
                        ? State.Active
                        : State.Ready;

                    double tickInterval =
                        1.0 / Math.Max(1, (int)_serverTickRate);

                    _nextMovementAt =
                        Time.realtimeSinceStartupAsDouble +
                        tickInterval * ((_index % 20) / 20.0);

                    return;
                }
            }

            private void ConsumeBaseline(NetDataReader reader)
            {
                reader.GetPackedUInt(); // server tick

                ushort stateCount = reader.GetUShort();
                if (stateCount > 4096)
                    throw new InvalidOperationException(
                        $"Baseline state count {stateCount} is unreasonable.");

                for (int i = 0; i < stateCount; ++i)
                {
                    GameStateSyncType stateType =
                        (GameStateSyncType)reader.GetByte();

                    switch (stateType)
                    {
                        case GameStateSyncType.Spawn:
                            ConsumeSpawnState(reader);
                            break;

                        case GameStateSyncType.Destroy:
                            ConsumeDestroyState(reader);
                            break;

                        case GameStateSyncType.Data:
                            ConsumeDataState(reader);
                            break;

                        default:
                            throw new InvalidOperationException(
                                $"Unknown state type {(byte)stateType}.");
                    }
                }
            }

            private void ConsumeSpawnState(NetDataReader reader)
            {
                bool isSceneObject = reader.GetBool();
                reader.GetPackedInt(); // scene-object or asset hash

                // Initial transform.
                reader.GetFloat();
                reader.GetFloat();
                reader.GetFloat();
                reader.GetFloat();
                reader.GetFloat();
                reader.GetFloat();

                uint objectId = reader.GetPackedUInt();
                long ownerConnectionId = reader.GetPackedLong();

                ObservedSpawns++;

                if (!isSceneObject &&
                    ownerConnectionId == _serverConnectionId)
                {
                    _ownedObjectId = objectId;

                    if (_state == State.Ready ||
                        _state == State.Active)
                    {
                        _state = State.Active;
                    }
                }

                ConsumeKnownSyncElements(reader);
            }

            private void ConsumeDestroyState(NetDataReader reader)
            {
                uint objectId = reader.GetPackedUInt();
                reader.GetByte(); // reason
                ObservedDestroys++;

                if (objectId == _ownedObjectId)
                {
                    _ownedObjectId = 0;
                    if (_state == State.Active)
                        _state = State.Ready;
                }
            }

            private void ConsumeDataState(NetDataReader reader)
            {
                reader.GetPackedUInt(); // object ID
                ConsumeKnownSyncElements(reader);
            }

            private void ConsumeDelta(NetDataReader reader)
            {
                reader.GetPackedUInt(); // tick
                ushort objectCount = reader.GetUShort();

                if (objectCount > 4096)
                    throw new InvalidOperationException(
                        $"Delta object count {objectCount} is unreasonable.");

                for (int i = 0; i < objectCount; ++i)
                {
                    reader.GetPackedUInt(); // object ID
                    ushort dataLength = reader.GetUShort();
                    int dataStart = reader.Position;
                    int dataEnd = dataStart + dataLength;

                    if (dataEnd < dataStart ||
                        dataEnd > reader.RawDataSize)
                    {
                        throw new InvalidOperationException(
                            "Delta object payload exceeds packet bounds.");
                    }

                    try
                    {
                        ConsumeKnownSyncElements(reader);
                    }
                    finally
                    {
                        // Delta objects have an explicit byte envelope; always advance
                        // to its authoritative end even if a future element was unknown.
                        reader.SetPosition(dataEnd);
                    }
                }
            }

            private void ConsumeKnownSyncElements(NetDataReader reader)
            {
                int elementCount = reader.GetPackedInt();

                if (elementCount < 0 || elementCount > 256)
                    throw new InvalidOperationException(
                        $"Sync element count {elementCount} is invalid.");

                for (int i = 0; i < elementCount; ++i)
                {
                    int elementId = reader.GetPackedInt();

                    if (elementId == _snapshotElementId)
                    {
                        reader.Get<PlayerEntitySnapshot>();
                    }
                    else if (elementId == _appearanceElementId)
                    {
                        reader.Get<PlayerEntityAppearance>();
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Unknown PlayerEntity sync element {elementId}. " +
                            "The synthetic protocol layout needs updating.");
                    }
                }
            }

            private void Fail(string reason)
            {
                if (_state == State.Failed ||
                    _state == State.Stopped)
                {
                    return;
                }

                _state = State.Failed;

                if (_index < 8)
                {
                    Debug.LogWarning(
                        $"[MMOSyntheticLoad/Bot {_index}] {reason}");
                }

                try
                {
                    _netManager?.Stop();
                }
                catch
                {
                }
            }

            private static float Next01(ref uint rng)
            {
                uint x = rng == 0 ? 1u : rng;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                rng = x;
                return (x & 0x00FFFFFFu) / 16777215f;
            }

            public void OnConnectionRequest(ConnectionRequest request)
            {
                request.Reject();
            }

            public void OnNetworkError(
                IPEndPoint endPoint,
                SocketError socketError)
            {
                if (_state == State.Stopped)
                    return;

                ProtocolErrors++;

                if (_index < 8)
                {
                    Debug.LogWarning(
                        $"[MMOSyntheticLoad/Bot {_index}] Network error " +
                        $"{socketError} from {endPoint}.");
                }
            }

            public void OnNetworkLatencyUpdate(
                NetPeer peer,
                int latency)
            {
            }

            public void OnNetworkReceive(
                NetPeer peer,
                NetPacketReader reader,
                byte channelNumber,
                DeliveryMethod deliveryMethod)
            {
                try
                {
                    HandlePacket(reader);
                }
                finally
                {
                    reader.Recycle();
                }
            }

            public void OnNetworkReceiveUnconnected(
                IPEndPoint remoteEndPoint,
                NetPacketReader reader,
                UnconnectedMessageType messageType)
            {
                reader.Recycle();
            }

            public void OnPeerConnected(NetPeer peer)
            {
                _peer = peer;
                _state = State.Connected;
                _nextPingAt = Time.realtimeSinceStartupAsDouble;

                SendEnterGame();
                SendPing();
            }

            public void OnPeerDisconnected(
                NetPeer peer,
                DisconnectInfo disconnectInfo)
            {
                _peer = null;

                if (_state != State.Stopped &&
                    _state != State.Failed)
                {
                    _state = State.Disconnected;

                    if (_index < 8)
                    {
                        Debug.LogWarning(
                            $"[MMOSyntheticLoad/Bot {_index}] Disconnected: " +
                            $"{disconnectInfo.Reason}.");
                    }
                }
            }
        }
    }
}
