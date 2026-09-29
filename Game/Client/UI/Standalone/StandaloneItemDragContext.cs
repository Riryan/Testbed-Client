using Player.Networking;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Client-only transient drag payload shared by authored Inventory, Equipment, and Storage UI.
    /// It never mutates gameplay state and never introduces a network operation; drop targets route
    /// the payload into the existing authoritative item/storage request methods.
    /// </summary>
    public static class StandaloneItemDragContext
    {
        public enum SourceKind : byte
        {
            None = 0,
            Inventory = 1,
            Equipment = 2,
            Storage = 3,
        }

        public static SourceKind Kind { get; private set; }
        public static int SlotIndex { get; private set; } = -1;
        public static string EquipmentSlotId { get; private set; } = string.Empty;
        public static PlayerItemWire Item { get; private set; }
        public static bool Active => Kind != SourceKind.None;

        public static void BeginInventory(int slotIndex, PlayerItemWire item)
        {
            Kind = SourceKind.Inventory;
            SlotIndex = slotIndex;
            EquipmentSlotId = string.Empty;
            Item = item;
        }

        public static void BeginEquipment(string equipmentSlotId, PlayerItemWire item)
        {
            Kind = SourceKind.Equipment;
            SlotIndex = -1;
            EquipmentSlotId = equipmentSlotId ?? string.Empty;
            Item = item;
        }

        public static void BeginStorage(int storageSlotIndex, PlayerItemWire item)
        {
            Kind = SourceKind.Storage;
            SlotIndex = storageSlotIndex;
            EquipmentSlotId = string.Empty;
            Item = item;
        }

        public static void Clear()
        {
            Kind = SourceKind.None;
            SlotIndex = -1;
            EquipmentSlotId = string.Empty;
            Item = default;
        }
    }
}
