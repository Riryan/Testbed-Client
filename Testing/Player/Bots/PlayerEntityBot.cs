using System;
using LiteNetLibManager;
using Player.Client;
using Player.Networking;
using Player.Shared;
using UnityEngine;

namespace Testing.Player
{
    /// <summary>
    /// A real network-client bot: it runs only on the owning client and sends the same
    /// movement intent contract as manual input. Launch many headless Player builds for CCU tests.
    /// </summary>
    public sealed class PlayerEntityBot : LiteNetLibBehaviour
    {
        public bool enableInEditor = false;
        [Min(2f)] public float routeRadius = 20f;
        [Range(0f, 1f)] public float sprintProbability = 0.25f;
        [Min(0.1f)] public float directionChangeSeconds = 2.5f;

#if !UNITY_SERVER
        private PlayerEntityNetwork _network;
        private LogicUpdater _logicUpdater;
        private uint _sequence;
        private int _seed;
        private uint _rng;
        private double _nextDirectionAt;
        private float _angle;
        private bool _sprint;
#endif
        private bool _active;

        public bool Active => _active;

        public override void OnStartOwnerClient()
        {
#if !UNITY_SERVER
            string[] args = Environment.GetCommandLineArgs();
            _active = enableInEditor || PlayerEntityGameManager.HasArg(args, "-mmoBot");
            if (!_active)
                return;

            _seed = PlayerEntityGameManager.GetIntArg(args, "-mmoBotSeed", unchecked((int)ConnectionId));
            _rng = (uint)(_seed == 0 ? 1 : _seed);
            PlayerEntityInput input = GetComponent<PlayerEntityInput>();
            if (input != null) input.SetEnabled(false);

            _network = GetComponent<PlayerEntityNetwork>();
            _logicUpdater = Manager?.LogicUpdater;
            if (_logicUpdater != null)
                _logicUpdater.OnTick += OnClientTick;
            ChooseDirection(0.0);
#endif
        }

        public override void OnNetworkDestroy(byte reasons) => Cleanup();
        public override void OnIdentityDestroy() => Cleanup();

        private void Cleanup()
        {
#if !UNITY_SERVER
            if (_logicUpdater != null)
                _logicUpdater.OnTick -= OnClientTick;
            _logicUpdater = null;
            _network = null;
#endif
            _active = false;
        }

        private void OnClientTick(LogicUpdater updater)
        {
#if !UNITY_SERVER
            if (!_active || _network == null || !IsOwnerClient)
                return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= _nextDirectionAt)
                ChooseDirection(now);

            Vector2 input = new Vector2(Mathf.Sin(_angle), Mathf.Cos(_angle));
            _network.TrySendMovement(new MovementCommand
            {
                sequence = ++_sequence,
                inputX = PlayerEntityQuantization.QuantizeInput(input.x),
                inputZ = PlayerEntityQuantization.QuantizeInput(input.y),
                yaw = PlayerEntityQuantization.QuantizeYaw(_angle * Mathf.Rad2Deg),
                flags = _sprint ? (byte)PlayerEntityFlags.Sprinting : (byte)0,
            });
#endif
        }

#if !UNITY_SERVER
        private void ChooseDirection(double now)
        {
            float jitter = Next01() * 1.2f - 0.6f;
            _angle += 0.65f + jitter;
            _sprint = Next01() < sprintProbability;
            _nextDirectionAt =
                now +
                directionChangeSeconds *
                (0.75 + Next01() * 0.5);
        }

        private float Next01()
        {
            uint x = _rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _rng = x;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
#endif
    }
}
