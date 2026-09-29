using System;
using LiteNetLibManager;
using UnityEngine;

namespace Player.Networking
{
    /// <summary>
    /// Runtime network/client validation panel.
    ///
    /// Network-condition controls intentionally affect only the standalone remote
    /// client's ClientTransport. Host/server simulation is not modified from here.
    /// </summary>
    public sealed class MMOTestbedRuntimePanel : MonoBehaviour
    {
#if !UNITY_SERVER
        private enum NetworkConditionPreset
        {
            Clean,
            Normal,
            Jittery,
            Bad,
            VeryBad,
            Torture,
            Custom,
        }

        private static readonly string[] s_presetLabels =
        {
            "Clean",
            "Normal",
            "Jittery",
            "Bad",
            "Very Bad",
            "Torture",
            "Custom",
        };

        private PlayerEntityGameManager _manager;
        private bool _showPanel;
        private string _address = "127.0.0.1";
        private string _port = "7770";
        private Vector2 _scroll;

        private NetworkConditionPreset _preset = NetworkConditionPreset.Clean;
        private int _latencyMinMs;
        private int _latencyMaxMs;
        private int _packetLossPercent;
        private bool _networkConditionsDirty;
        private string _networkConditionStatus = "Clean";

        private bool _reconnectPending;
        private double _reconnectAt;
        private string _reconnectAddress;
        private int _reconnectPort;

        private void Awake()
        {
            _manager = GetComponent<PlayerEntityGameManager>();
            if (_manager != null)
            {
                _address = _manager.networkAddress;
                _port = _manager.networkPort.ToString();
            }

            ApplyPreset(NetworkConditionPreset.Clean, false);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F12))
                _showPanel = !_showPanel;

            if (_manager == null)
                return;

            if (_reconnectPending &&
                Time.realtimeSinceStartupAsDouble >= _reconnectAt &&
                !_manager.IsNetworkActive)
            {
                _reconnectPending = false;
                _manager.StartClient(_reconnectAddress, _reconnectPort);
                _networkConditionsDirty = true;
            }

            if (_networkConditionsDirty &&
                !_manager.IsServer &&
                _manager.ClientTransport != null)
            {
                ApplyNetworkConditionsToRemoteClient();
            }
        }

        private void OnGUI()
        {
            if (!_showPanel || _manager == null)
                return;

            GUI.depth = -1000;
            GUILayout.BeginArea(new Rect(12, 12, 500, 880), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("MMO PlayerEntity Remote Core Test");
            GUILayout.Label("F12: Hide diagnostics");
            GUILayout.Label(
                $"Server: {_manager.IsServer}   Client: {_manager.IsClientConnected}   " +
                $"Peers: {_manager.ServerTransport?.ServerPeersCount ?? 0}");
            GUILayout.Label(
                $"ConnectionId: {_manager.ClientConnectionId}   Players: {_manager.PlayersCount}   " +
                $"Ready: {_manager.ReadyPlayersCount}   Spawned: {_manager.SpawnedPlayerObjectsCount}");

            DrawConnection();

            if (!_manager.IsServer)
                DrawNetworkConditions();
            else
            {
                GUILayout.Space(8);
                GUILayout.Label("Network Conditions");
                GUILayout.Label("Lag simulation is intentionally controlled on the standalone Remote Client, not the Host/Server.");
            }

            DrawPresentationTelemetry();
            DrawServerRuntime();

            GUILayout.Space(8);
            GUILayout.Label("Movement: WASD   Sprint: Shift");

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawConnection()
        {
            GUILayout.Space(6);
            GUILayout.Label("Connection");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Address", GUILayout.Width(72));
            _address = GUILayout.TextField(_address);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(72));
            _port = GUILayout.TextField(_port);
            GUILayout.EndHorizontal();

            int port = int.TryParse(_port, out int parsed) ? Mathf.Clamp(parsed, 1, 65535) : 7770;

            if (!_manager.IsNetworkActive)
            {
                if (GUILayout.Button("Start Client", GUILayout.Height(30)))
                {
                    _manager.StartClient(_address, port);
                    _networkConditionsDirty = true;
                }

            }
            else
            {
                if (_manager.IsClientConnected && !_manager.IsServer)
                {
                    GUILayout.BeginHorizontal();

                    if (GUILayout.Button("Disconnect Client", GUILayout.Height(30)))
                    {
                        _reconnectPending = false;
                        _manager.StopClient();
                    }

                    if (GUILayout.Button("Cycle Client (1s)", GUILayout.Height(30)))
                    {
                        _reconnectAddress = _address;
                        _reconnectPort = port;
                        _reconnectAt = Time.realtimeSinceStartupAsDouble + 1.0;
                        _reconnectPending = true;
                        _manager.StopClient();
                    }

                    GUILayout.EndHorizontal();
                }
            }
        }

        private void DrawNetworkConditions()
        {
            GUILayout.Space(10);
            GUILayout.Label("Remote Client Network Conditions");

            int selected = GUILayout.SelectionGrid(
                (int)_preset,
                s_presetLabels,
                3);

            if (selected != (int)_preset)
                ApplyPreset((NetworkConditionPreset)selected, true);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Latency min", GUILayout.Width(90));
            string minText = GUILayout.TextField(_latencyMinMs.ToString(), GUILayout.Width(70));
            GUILayout.Label("ms", GUILayout.Width(24));

            GUILayout.Label("max", GUILayout.Width(30));
            string maxText = GUILayout.TextField(_latencyMaxMs.ToString(), GUILayout.Width(70));
            GUILayout.Label("ms", GUILayout.Width(24));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Packet loss", GUILayout.Width(90));
            string lossText = GUILayout.TextField(_packetLossPercent.ToString(), GUILayout.Width(70));
            GUILayout.Label("%");
            GUILayout.EndHorizontal();

            int newMin = ParseClamped(minText, _latencyMinMs, 0, 5000);
            int newMax = ParseClamped(maxText, _latencyMaxMs, 0, 5000);
            int newLoss = ParseClamped(lossText, _packetLossPercent, 0, 100);

            if (newMax < newMin)
                newMax = newMin;

            if (newMin != _latencyMinMs ||
                newMax != _latencyMaxMs ||
                newLoss != _packetLossPercent)
            {
                _latencyMinMs = newMin;
                _latencyMaxMs = newMax;
                _packetLossPercent = newLoss;
                _preset = NetworkConditionPreset.Custom;
            }

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Apply Live"))
            {
                _networkConditionsDirty = true;
                ApplyNetworkConditionsToRemoteClient();
            }

            if (GUILayout.Button("Reset Clean"))
                ApplyPreset(NetworkConditionPreset.Clean, true);

            GUILayout.EndHorizontal();

            GUILayout.Label($"Applied: {_networkConditionStatus}");
            GUILayout.Label("Min/max creates jitter. Conditions can be changed while both players are moving.");
        }

        private void DrawPresentationTelemetry()
        {
            MMORemotePresentationSnapshot p =
                MMORemotePresentationTelemetry.GetSnapshot();

            GUILayout.Space(10);
            GUILayout.Label("Remote Presentation / Lifecycle");

            GUILayout.Label(
                $"Active presentation - Owner: {p.activeOwned}  Remote: {p.activeRemote}");

            GUILayout.Label(
                $"Lifecycle - Owner spawn/destroy: {p.ownedSpawns}/{p.ownedDestroys}  " +
                $"Remote enter/exit: {p.remoteEnters}/{p.remoteExits}");

            GUILayout.Label(
                $"Generation resets: {p.generationResets}");

            GUILayout.Label(
                $"Interpolation delay avg/max: {p.averageInterpolationDelayMs:F1} / " +
                $"{p.maximumInterpolationDelayMs:F1} ms");

            GUILayout.Label(
                $"Measured jitter avg/max: {p.averageJitterMs:F1} / " +
                $"{p.maximumJitterMs:F1} ms");

            GUILayout.Label(
                $"Snapshots buffered avg/max: {p.averageBufferedSnapshots:F1} / " +
                $"{p.maximumBufferedSnapshots}");

            GUILayout.Label(
                $"Extrapolating: {p.extrapolating}  max: {p.maximumExtrapolationMs:F1} ms");

            GUILayout.Label(
                $"Presentation error avg/max: {p.averagePresentationError:F3} / " +
                $"{p.maximumPresentationError:F3} m");

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Reset Client Metrics"))
                MMORemotePresentationTelemetry.Reset();

            GUILayout.EndHorizontal();

            GUILayout.Label(
                "AOI validation: move the other player out of relevance range. " +
                "Remote active should drop and Remote exit should increment; return to range and Remote enter should increment.");
        }

        private void DrawServerRuntime()
        {
            if (_manager.ServerFoundation == null)
                return;

            CoreServerTelemetrySnapshot t = _manager.Telemetry.GetSnapshot();

            GUILayout.Space(10);
            GUILayout.Label("Server/Core Runtime");
            GUILayout.Label($"Core p99: {t.coreFrameP99Ms:F2} ms");
            GUILayout.Label($"Net tick p99: {t.networkTickP99Ms:F2} ms");
            GUILayout.Label($"Memory: {t.managedMemoryBytes / (1024.0 * 1024.0):F1} MB");
            GUILayout.Label(
                $"AOI targets/observers/cells: {t.aoiTargets}/{t.aoiObservers}/{t.aoiGridCells}");
            GUILayout.Label(
                $"AOI checks: {t.aoiCandidateChecks:n0}  cap hits: {t.aoiCandidateLimitHits:n0}");
            GUILayout.Label(
                $"RX/TX: {t.inboundBytes / 1024.0:F1} / {t.outboundBytes / 1024.0:F1} KB");
            GUILayout.Label(
                $"Drops RX/TX: {t.inboundDrops} / {t.outboundDrops}");

            CoreRuntimeSchedulerMetrics scheduler = t.scheduler;
            if (scheduler.startupGraceActive)
            {
                GUILayout.Label(
                    $"Startup grace: {scheduler.startupGraceRemainingSeconds:F2}s  " +
                    $"measured overruns: {scheduler.startupGraceOverruns}");
            }
        }

        private void ApplyPreset(NetworkConditionPreset preset, bool applyLive)
        {
            _preset = preset;

            switch (preset)
            {
                case NetworkConditionPreset.Normal:
                    SetConditions(60, 100, 0);
                    break;

                case NetworkConditionPreset.Jittery:
                    SetConditions(80, 160, 2);
                    break;

                case NetworkConditionPreset.Bad:
                    SetConditions(200, 300, 5);
                    break;

                case NetworkConditionPreset.VeryBad:
                    SetConditions(300, 450, 10);
                    break;

                case NetworkConditionPreset.Torture:
                    SetConditions(450, 700, 15);
                    break;

                case NetworkConditionPreset.Clean:
                    SetConditions(0, 0, 0);
                    break;

                case NetworkConditionPreset.Custom:
                    break;
            }

            if (applyLive)
            {
                _networkConditionsDirty = true;
                ApplyNetworkConditionsToRemoteClient();
            }
        }

        private void SetConditions(int minimumLatency, int maximumLatency, int packetLoss)
        {
            _latencyMinMs = minimumLatency;
            _latencyMaxMs = Math.Max(minimumLatency, maximumLatency);
            _packetLossPercent = Mathf.Clamp(packetLoss, 0, 100);
        }

        private void ApplyNetworkConditionsToRemoteClient()
        {
            if (_manager == null || _manager.IsServer)
            {
                _networkConditionStatus =
                    "Not applied - use the standalone Remote Client.";
                return;
            }

            if (_manager.ClientTransport == null)
            {
                _networkConditionStatus =
                    "Pending - client transport is not initialized yet.";
                return;
            }

            CoreNetworkChaosSettings chaos =
                _manager.serverFoundationSettings.chaos;

            chaos.enabled =
                _latencyMaxMs > 0 ||
                _packetLossPercent > 0;

            chaos.simulateLatency = _latencyMaxMs > 0;
            chaos.minimumRoundTripLatencyMilliseconds =
                Mathf.Max(0, _latencyMinMs);
            chaos.maximumRoundTripLatencyMilliseconds =
                Mathf.Max(_latencyMinMs, _latencyMaxMs);

            chaos.simulatePacketLoss =
                _packetLossPercent > 0;
            chaos.packetLossPercent =
                Mathf.Clamp(_packetLossPercent, 0, 100);

            _manager.ServerFoundation?.ApplyNetworkSimulation(
                _manager.ClientTransport);

            _networkConditionsDirty = false;

            _networkConditionStatus =
                chaos.enabled
                    ? $"{_latencyMinMs}-{_latencyMaxMs} ms, {_packetLossPercent}% loss"
                    : "Clean";
        }

        private static int ParseClamped(
            string value,
            int fallback,
            int minimum,
            int maximum)
        {
            if (!int.TryParse(value, out int parsed))
                return fallback;

            return Mathf.Clamp(parsed, minimum, maximum);
        }
#endif
    }
}
