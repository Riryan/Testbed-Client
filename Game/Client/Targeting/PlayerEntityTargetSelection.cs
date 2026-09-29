using System;
using Player.Client;
using Player.Networking;
using UnityEngine;

namespace Game.Client.Targeting
{
    /// <summary>
    /// Canonical client-only PlayerEntity target selection state.
    ///
    /// It consumes PlayerEntityClient's event-driven spawn registry rather than scanning
    /// the scene. Selection is presentation state only: gameplay requests still carry the
    /// target object id/generation and are validated by the standalone GameServer.
    /// </summary>
    public static class PlayerEntityTargetSelection
    {
        private static PlayerEntityClient _selected;
        private static bool _initialized;

        public static PlayerEntityClient SelectedClient
        {
            get
            {
                EnsureInitialized();
                ValidateSelection();
                return _selected;
            }
        }

        public static PlayerEntityNetwork SelectedNetwork => SelectedClient != null
            ? SelectedClient.NetworkBridge
            : null;

        public static event Action<PlayerEntityClient> SelectedChanged;

        public static bool CycleRemotePlayer()
        {
            EnsureInitialized();
            ValidateSelection();

            var active = PlayerEntityClient.ActiveClients;
            if (active == null || active.Count == 0)
            {
                Clear();
                return false;
            }

            uint currentId = _selected != null && _selected.NetworkBridge != null
                ? _selected.NetworkBridge.ObjectId
                : 0;

            PlayerEntityClient next = null;
            PlayerEntityClient first = null;
            uint nextId = uint.MaxValue;
            uint firstId = uint.MaxValue;

            // Deterministic object-id ordering without a temporary list or sort allocation.
            for (int i = 0; i < active.Count; ++i)
            {
                PlayerEntityClient candidate = active[i];
                if (candidate == null || !candidate.IsTargetableRemotePlayer)
                    continue;

                uint id = candidate.NetworkBridge.ObjectId;
                if (id < firstId)
                {
                    firstId = id;
                    first = candidate;
                }

                if (id > currentId && id < nextId)
                {
                    nextId = id;
                    next = candidate;
                }
            }

            if (next == null)
                next = first;
            Set(next);
            return next != null;
        }

        public static bool TrySelect(PlayerEntityNetwork network)
        {
            EnsureInitialized();
            if (network == null)
            {
                Clear();
                return true;
            }

            var active = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < active.Count; ++i)
            {
                PlayerEntityClient candidate = active[i];
                if (candidate == null || !candidate.IsTargetableRemotePlayer)
                    continue;
                if (!ReferenceEquals(candidate.NetworkBridge, network))
                    continue;

                Set(candidate);
                return true;
            }

            return false;
        }

        public static void Clear() => Set(null);

        private static void Set(PlayerEntityClient target)
        {
            if (target != null && !target.IsTargetableRemotePlayer)
                target = null;

            if (ReferenceEquals(_selected, target))
                return;

            if (_selected != null)
                _selected.DisplayNameChanged -= OnSelectedDisplayNameChanged;

            _selected = target;

            if (_selected != null)
                _selected.DisplayNameChanged += OnSelectedDisplayNameChanged;

            SelectedChanged?.Invoke(_selected);
        }

        private static void ValidateSelection()
        {
            if (_selected != null && !_selected.IsTargetableRemotePlayer)
                Set(null);
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;

            _initialized = true;
            PlayerEntityClient.ActiveClientUnregistered += OnClientUnregistered;
        }

        private static void OnSelectedDisplayNameChanged(PlayerEntityClient client)
        {
            if (ReferenceEquals(_selected, client))
                SelectedChanged?.Invoke(_selected);
        }

        private static void OnClientUnregistered(PlayerEntityClient client)
        {
            if (ReferenceEquals(_selected, client))
                Set(null);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            if (_initialized)
                PlayerEntityClient.ActiveClientUnregistered -= OnClientUnregistered;
            if (_selected != null)
                _selected.DisplayNameChanged -= OnSelectedDisplayNameChanged;
            _selected = null;
            SelectedChanged = null;
            _initialized = false;
        }
    }
}
