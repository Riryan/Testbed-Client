using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace Player.Networking
{
    internal struct ItemPresentationLayoutEntry : INetSerializable
    {
        public long itemInstanceId;
        public int visualSlot;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(itemInstanceId);
            writer.Put(visualSlot);
        }

        public void Deserialize(NetDataReader reader)
        {
            itemInstanceId = reader.GetLong();
            visualSlot = reader.GetInt();
        }
    }

    internal struct ItemPresentationLayoutCacheMessage : INetSerializable
    {
        public const int MaxEntriesPerContainer = 1024;

        public ItemPresentationLayoutEntry[] inventory;
        public ItemPresentationLayoutEntry[] storage;

        public void Serialize(NetDataWriter writer)
        {
            WriteEntries(writer, inventory);
            WriteEntries(writer, storage);
        }

        public void Deserialize(NetDataReader reader)
        {
            inventory = ReadEntries(reader);
            storage = ReadEntries(reader);
        }

        private static void WriteEntries(NetDataWriter writer, ItemPresentationLayoutEntry[] entries)
        {
            ItemPresentationLayoutEntry[] values = entries ?? Array.Empty<ItemPresentationLayoutEntry>();
            int count = Math.Min(values.Length, MaxEntriesPerContainer);
            writer.Put((ushort)count);
            for (int i = 0; i < count; ++i)
                values[i].Serialize(writer);
        }

        private static ItemPresentationLayoutEntry[] ReadEntries(NetDataReader reader)
        {
            int count = reader.GetUShort();
            if (count > MaxEntriesPerContainer)
                throw new InvalidOperationException("item presentation layout cache is too large");

            var values = new ItemPresentationLayoutEntry[count];
            for (int i = 0; i < count; ++i)
            {
                ItemPresentationLayoutEntry entry = default;
                entry.Deserialize(reader);
                values[i] = entry;
            }
            return values;
        }
    }

    public sealed partial class PlayerEntityGameManager
    {
        private const string ItemPresentationLayoutCacheKind = "item_layout";

        // These maps are CLIENT PRESENTATION ONLY.
        // GameServer/Gateway inventorySlot values remain opaque server packing indices.
        private readonly Dictionary<long, int> _inventoryPresentationSlots =
            new Dictionary<long, int>();
        private readonly Dictionary<long, int> _storagePresentationSlots =
            new Dictionary<long, int>();
        private readonly Dictionary<long, int> _pendingInventoryPresentationSlots =
            new Dictionary<long, int>();

        private long _itemPresentationLayoutCharacterId = long.MinValue;
        private string _itemPresentationLayoutServerScope = string.Empty;
        private bool _itemPresentationLayoutLoaded;

        private void EnsureItemPresentationLayoutLoaded()
        {
            long characterId = _clientOwnerStateCharacterId;
            string serverScope = AuthenticationServiceBaseUrl ?? string.Empty;
            if (_itemPresentationLayoutLoaded &&
                _itemPresentationLayoutCharacterId == characterId &&
                string.Equals(_itemPresentationLayoutServerScope, serverScope, StringComparison.Ordinal))
                return;

            _inventoryPresentationSlots.Clear();
            _storagePresentationSlots.Clear();
            _pendingInventoryPresentationSlots.Clear();

            _itemPresentationLayoutCharacterId = characterId;
            _itemPresentationLayoutServerScope = serverScope;
            _itemPresentationLayoutLoaded = true;

            if (characterId <= 0)
                return;

            if (!TryLoadOwnerStateCache(
                    AuthenticationServiceBaseUrl,
                    characterId,
                    ItemPresentationLayoutCacheKind,
                    out ItemPresentationLayoutCacheMessage cached))
                return;

            LoadLayoutEntries(cached.inventory, _inventoryPresentationSlots);
            LoadLayoutEntries(cached.storage, _storagePresentationSlots);
        }

        private static void LoadLayoutEntries(
            ItemPresentationLayoutEntry[] entries,
            Dictionary<long, int> destination)
        {
            ItemPresentationLayoutEntry[] values =
                entries ?? Array.Empty<ItemPresentationLayoutEntry>();

            for (int i = 0; i < values.Length; ++i)
            {
                ItemPresentationLayoutEntry entry = values[i];
                if (entry.itemInstanceId <= 0 || entry.visualSlot < 0)
                    continue;
                if (!destination.ContainsKey(entry.itemInstanceId))
                    destination.Add(entry.itemInstanceId, entry.visualSlot);
            }
        }

        private void PersistItemPresentationLayout()
        {
            EnsureItemPresentationLayoutLoaded();
            long characterId = _itemPresentationLayoutCharacterId;
            if (characterId <= 0)
                return;

            var cache = new ItemPresentationLayoutCacheMessage
            {
                inventory = ToLayoutEntries(_inventoryPresentationSlots),
                storage = ToLayoutEntries(_storagePresentationSlots),
            };

            SaveOwnerStateCache(
                AuthenticationServiceBaseUrl,
                characterId,
                ItemPresentationLayoutCacheKind,
                cache);
        }

        private static ItemPresentationLayoutEntry[] ToLayoutEntries(
            Dictionary<long, int> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<ItemPresentationLayoutEntry>();

            var ids = new List<long>(source.Keys);
            ids.Sort();

            var entries = new ItemPresentationLayoutEntry[ids.Count];
            for (int i = 0; i < ids.Count; ++i)
            {
                long id = ids[i];
                entries[i] = new ItemPresentationLayoutEntry
                {
                    itemInstanceId = id,
                    visualSlot = source[id],
                };
            }
            return entries;
        }

        private PlayerItemsResponseMessage BuildPresentedPlayerItems(
            PlayerItemsResponseMessage authoritative)
        {
            if (authoritative.inventoryCapacity <= 0)
                return authoritative;

            EnsureItemPresentationLayoutLoaded();

            if (ReconcilePresentationLayout(
                    _inventoryPresentationSlots,
                    authoritative.inventory,
                    authoritative.inventoryCapacity,
                    _pendingInventoryPresentationSlots))
                PersistItemPresentationLayout();

            PlayerItemWire[] source =
                authoritative.inventory ?? Array.Empty<PlayerItemWire>();
            var presented = new PlayerItemWire[source.Length];

            for (int i = 0; i < source.Length; ++i)
            {
                PlayerItemWire item = source[i];
                if (item.itemInstanceId > 0 &&
                    _inventoryPresentationSlots.TryGetValue(
                        item.itemInstanceId,
                        out int visualSlot))
                    item.inventorySlot = visualSlot;
                else
                    item.inventorySlot = -1;
                presented[i] = item;
            }

            Array.Sort(
                presented,
                (left, right) => left.inventorySlot.CompareTo(right.inventorySlot));

            PlayerItemsResponseMessage result = authoritative;
            result.inventory = presented;
            return result;
        }

        private StorageStateMessage BuildPresentedStorageState(
            StorageStateMessage authoritative)
        {
            if (authoritative.capacity <= 0)
                return authoritative;

            EnsureItemPresentationLayoutLoaded();

            if (ReconcilePresentationLayout(
                    _storagePresentationSlots,
                    authoritative.items,
                    authoritative.capacity,
                    pending: null))
                PersistItemPresentationLayout();

            PlayerItemWire[] source =
                authoritative.items ?? Array.Empty<PlayerItemWire>();
            var presented = new PlayerItemWire[source.Length];

            for (int i = 0; i < source.Length; ++i)
            {
                PlayerItemWire item = source[i];
                if (item.itemInstanceId > 0 &&
                    _storagePresentationSlots.TryGetValue(
                        item.itemInstanceId,
                        out int visualSlot))
                    item.inventorySlot = visualSlot;
                else
                    item.inventorySlot = -1;
                presented[i] = item;
            }

            Array.Sort(
                presented,
                (left, right) => left.inventorySlot.CompareTo(right.inventorySlot));

            StorageStateMessage result = authoritative;
            result.items = presented;
            return result;
        }

        private static bool ReconcilePresentationLayout(
            Dictionary<long, int> current,
            PlayerItemWire[] source,
            int capacity,
            Dictionary<long, int> pending)
        {
            if (current == null || capacity <= 0)
                return false;

            PlayerItemWire[] items = source ?? Array.Empty<PlayerItemWire>();
            var presentIds = new HashSet<long>();
            for (int i = 0; i < items.Length; ++i)
            {
                long id = items[i].itemInstanceId;
                if (id > 0)
                    presentIds.Add(id);
            }

            var next = new Dictionary<long, int>(presentIds.Count);
            var occupied = new bool[capacity];

            // Preserve valid cached visual positions first.
            var ids = new List<long>(presentIds);
            ids.Sort();
            for (int i = 0; i < ids.Count; ++i)
            {
                long id = ids[i];
                if (!current.TryGetValue(id, out int slot) ||
                    slot < 0 ||
                    slot >= capacity ||
                    occupied[slot])
                    continue;

                next[id] = slot;
                occupied[slot] = true;
            }

            // A local drag destination for an authoritative container transition wins
            // when that item newly enters Inventory and the requested visual slot is free.
            if (pending != null && pending.Count > 0)
            {
                for (int i = 0; i < ids.Count; ++i)
                {
                    long id = ids[i];
                    if (next.ContainsKey(id) ||
                        !pending.TryGetValue(id, out int slot) ||
                        slot < 0 ||
                        slot >= capacity ||
                        occupied[slot])
                        continue;

                    next[id] = slot;
                    occupied[slot] = true;
                }
            }

            // No cache / new item: deterministic client-local ordering.
            for (int i = 0; i < ids.Count; ++i)
            {
                long id = ids[i];
                if (next.ContainsKey(id))
                    continue;

                int slot = FirstFree(occupied);
                if (slot < 0)
                    break;

                next[id] = slot;
                occupied[slot] = true;
            }

            bool changed = !SameLayout(current, next);

            current.Clear();
            foreach (KeyValuePair<long, int> pair in next)
                current[pair.Key] = pair.Value;

            if (pending != null && pending.Count > 0)
            {
                var consumed = new List<long>();
                foreach (KeyValuePair<long, int> pair in pending)
                    if (presentIds.Contains(pair.Key))
                        consumed.Add(pair.Key);
                for (int i = 0; i < consumed.Count; ++i)
                    pending.Remove(consumed[i]);
            }

            return changed;
        }

        private static bool SameLayout(
            Dictionary<long, int> left,
            Dictionary<long, int> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (KeyValuePair<long, int> pair in left)
                if (!right.TryGetValue(pair.Key, out int slot) || slot != pair.Value)
                    return false;

            return true;
        }

        private static int FirstFree(bool[] occupied)
        {
            if (occupied == null)
                return -1;
            for (int i = 0; i < occupied.Length; ++i)
                if (!occupied[i])
                    return i;
            return -1;
        }

        private PlayerItemMutationResponseMessage MoveInventoryPresentation(
            int fromVisualSlot,
            int toVisualSlot)
        {
            if (!HasPlayerItemsCache)
                return PlayerItemMutationResponseMessage.Failed(
                    (byte)Game.Shared.Protocol.PlayerItemOperationStatus.ItemUnavailable,
                    "inventory state has not hydrated locally");

            int capacity = _latestPlayerItems.inventoryCapacity;
            if (fromVisualSlot < 0 || fromVisualSlot >= capacity ||
                toVisualSlot < 0 || toVisualSlot >= capacity)
                return PlayerItemMutationResponseMessage.Failed(
                    (byte)Game.Shared.Protocol.PlayerItemOperationStatus.InvalidSlot,
                    "inventory visual slot is outside the known capacity");

            if (fromVisualSlot == toVisualSlot)
                return LocalItemPresentationSuccess();

            EnsureItemPresentationLayoutLoaded();
            ReconcilePresentationLayout(
                _inventoryPresentationSlots,
                _latestPlayerItems.inventory,
                capacity,
                _pendingInventoryPresentationSlots);

            long sourceId = FindItemAtVisualSlot(
                _inventoryPresentationSlots,
                fromVisualSlot);
            if (sourceId <= 0)
                return PlayerItemMutationResponseMessage.Failed(
                    (byte)Game.Shared.Protocol.PlayerItemOperationStatus.ItemUnavailable,
                    "source inventory visual slot is empty locally");

            long targetId = FindItemAtVisualSlot(
                _inventoryPresentationSlots,
                toVisualSlot);

            _inventoryPresentationSlots[sourceId] = toVisualSlot;
            if (targetId > 0)
                _inventoryPresentationSlots[targetId] = fromVisualSlot;

            PersistItemPresentationLayout();

            // Presentation-only reorder. No request, no authoritative revision, no DB write.
            PlayerItemsChangedReceived?.Invoke(
                BuildPresentedPlayerItems(_latestPlayerItems));

            return LocalItemPresentationSuccess();
        }

        private static long FindItemAtVisualSlot(
            Dictionary<long, int> layout,
            int visualSlot)
        {
            foreach (KeyValuePair<long, int> pair in layout)
                if (pair.Value == visualSlot)
                    return pair.Key;
            return 0;
        }

        private bool TryGetRawInventoryItemByPresentationSlot(
            int visualSlot,
            out PlayerItemWire item)
        {
            item = default;
            if (_latestPlayerItems.inventoryCapacity <= 0)
                return false;

            EnsureItemPresentationLayoutLoaded();
            ReconcilePresentationLayout(
                _inventoryPresentationSlots,
                _latestPlayerItems.inventory,
                _latestPlayerItems.inventoryCapacity,
                _pendingInventoryPresentationSlots);

            long id = FindItemAtVisualSlot(
                _inventoryPresentationSlots,
                visualSlot);
            if (id <= 0)
                return false;

            PlayerItemWire[] values =
                _latestPlayerItems.inventory ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].itemInstanceId != id)
                    continue;
                item = values[i]; // raw authoritative/server packing slot is preserved here.
                return true;
            }

            return false;
        }

        private bool TryGetRawStorageItemByPresentationSlot(
            int visualSlot,
            out PlayerItemWire item)
        {
            item = default;
            if (_latestStorage.capacity <= 0)
                return false;

            EnsureItemPresentationLayoutLoaded();
            ReconcilePresentationLayout(
                _storagePresentationSlots,
                _latestStorage.items,
                _latestStorage.capacity,
                pending: null);

            long id = FindItemAtVisualSlot(
                _storagePresentationSlots,
                visualSlot);
            if (id <= 0)
                return false;

            PlayerItemWire[] values =
                _latestStorage.items ?? Array.Empty<PlayerItemWire>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].itemInstanceId != id)
                    continue;
                item = values[i]; // raw authoritative/server storage slot.
                return true;
            }

            return false;
        }

        private void ReserveInventoryPresentationSlot(
            long itemInstanceId,
            int preferredVisualSlot)
        {
            if (itemInstanceId <= 0 ||
                preferredVisualSlot < 0 ||
                _latestPlayerItems.inventoryCapacity <= 0 ||
                preferredVisualSlot >= _latestPlayerItems.inventoryCapacity)
                return;

            EnsureItemPresentationLayoutLoaded();
            _pendingInventoryPresentationSlots[itemInstanceId] =
                preferredVisualSlot;
        }

        private void CancelInventoryPresentationReservation(
            long itemInstanceId)
        {
            if (itemInstanceId <= 0)
                return;
            _pendingInventoryPresentationSlots.Remove(itemInstanceId);
        }

        private static PlayerItemMutationResponseMessage LocalItemPresentationSuccess() =>
            new PlayerItemMutationResponseMessage
            {
                success = true,
                status = (byte)Game.Shared.Protocol.PlayerItemOperationStatus.Success,
                error = string.Empty,
            };
    }
}
