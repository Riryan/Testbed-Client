using System;
using Game.Shared.Characters;
using LiteNetLib;
using LiteNetLibManager;
using Player.Shared;

namespace Player.Networking
{
    /// <summary>
    /// Thin network bridge. It owns transport-facing state but no movement authority or presentation.
    /// Server and Client assemblies depend on this bridge; the bridge does not depend on either side.
    /// </summary>
    public sealed class PlayerEntityNetwork : LiteNetLibBehaviour
    {
        private readonly SyncFieldNetSerializableStruct<PlayerEntitySnapshot> _snapshot = new SyncFieldNetSerializableStruct<PlayerEntitySnapshot>
        {
            syncMode = LiteNetLibSyncFieldMode.ServerToClients,
            redundancyCount = 1,
        };

        private readonly SyncFieldNetSerializableStruct<PlayerEntityAppearance> _appearance = new SyncFieldNetSerializableStruct<PlayerEntityAppearance>
        {
            syncMode = LiteNetLibSyncFieldMode.ServerToClients,
            redundancyCount = 0,
        };

        public PlayerEntitySnapshot Snapshot => _snapshot.Value;
        public PlayerEntityAppearance Appearance => _appearance.Value;

        // Client-facing scalar accessors keep higher-level client assemblies from
        // depending directly on Player.Shared transport snapshot types.
        public ushort Generation => _snapshot.Value.generation;
        public string DisplayName => _appearance.Value.displayName;

        public event Action<MovementCommand> ServerMovementReceived;
        public event Action<bool, PlayerEntitySnapshot, PlayerEntitySnapshot> SnapshotChanged;
        public event Action<bool, PlayerEntityAppearance, PlayerEntityAppearance> AppearanceChanged;

        public static event Action<long, CharacterAppearanceRecipe> OwnerAppearanceObserved;
        public static event Action<long, CharacterPresentationPreferences> OwnerPresentationObserved;
        public static event Action<long, uint, PlayerEquipmentVisualSelection[]> OwnerEquipmentVisualsObserved;
        private uint _lastOwnerAppearanceSequence;

        public override void OnSetup()
        {
            _snapshot.onChange += OnSnapshotChanged;
            _appearance.onChange += OnAppearanceChanged;
        }

        public override void OnIdentityDestroy()
        {
            _snapshot.onChange -= OnSnapshotChanged;
            _appearance.onChange -= OnAppearanceChanged;
            ServerMovementReceived = null;
            SnapshotChanged = null;
            AppearanceChanged = null;
        }

        private void OnSnapshotChanged(bool initial, PlayerEntitySnapshot oldValue, PlayerEntitySnapshot newValue)
        {
            SnapshotChanged?.Invoke(initial, oldValue, newValue);
        }

        private void OnAppearanceChanged(bool initial, PlayerEntityAppearance oldValue, PlayerEntityAppearance newValue)
        {
            AppearanceChanged?.Invoke(initial, oldValue, newValue);
            NotifyOwnerAppearanceObserved(newValue);
        }

        public void NotifyOwnerAppearanceObserved(in PlayerEntityAppearance appearance)
        {
            if (!IsClient || !IsOwnerClient || appearance.characterId <= 0 ||
                appearance.sequence == 0 || appearance.sequence == _lastOwnerAppearanceSequence)
                return;

            _lastOwnerAppearanceSequence = appearance.sequence;
            OwnerAppearanceObserved?.Invoke(
                appearance.characterId,
                appearance.appearance?.Clone() ?? CharacterAppearanceRecipe.CreateDefault());
            OwnerPresentationObserved?.Invoke(
                appearance.characterId,
                appearance.presentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault());

            PlayerEquipmentVisualSelection[] sourceEquipment =
                appearance.equipmentVisuals ?? Array.Empty<PlayerEquipmentVisualSelection>();
            var equipmentCopy = new PlayerEquipmentVisualSelection[sourceEquipment.Length];
            if (sourceEquipment.Length > 0)
                Array.Copy(sourceEquipment, equipmentCopy, sourceEquipment.Length);
            OwnerEquipmentVisualsObserved?.Invoke(
                appearance.characterId,
                appearance.equipmentVersion,
                equipmentCopy);
        }

        public bool TrySendMovement(in MovementCommand command)
        {
            if (!IsOwnerClient || !IsSpawned)
                return false;
            RPC(ServerReceiveMovement, 1, DeliveryMethod.Sequenced, command);
            return true;
        }

        [ServerRpc]
        private void ServerReceiveMovement(MovementCommand command)
        {
            if (!IsServer)
                return;
            ServerMovementReceived?.Invoke(command);
        }

        public void ServerSetSnapshot(in PlayerEntitySnapshot value)
        {
            if (IsServer)
                _snapshot.Value = value;
        }

        public void ServerSetAppearance(in PlayerEntityAppearance value)
        {
            if (IsServer)
                _appearance.Value = value;
        }
    }
}
