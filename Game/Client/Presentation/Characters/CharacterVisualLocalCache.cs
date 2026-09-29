using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Shared.Characters;
using Player.Networking;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Local, non-authoritative presentation cache. It exists only to avoid resending or
    /// rebuilding unchanged visuals on the same computer. Missing/invalid cache always
    /// falls back safely to server authority + generic presentation.
    /// </summary>
    public static class CharacterVisualLocalCache
    {
        private const uint Magic = 0x564F4D4D; // "MMOV" little-endian
        private const ushort FileVersion = 2;
        private static string _scopeKey = "default";

        /// <summary>
        /// Scopes local visual files to the selected backend so Local/Live character ids
        /// cannot collide. This affects presentation files only and has no authority role.
        /// </summary>
        public static void SetServerScope(string serverScope)
        {
            string normalized = (serverScope ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
            if (normalized.Length == 0)
            {
                _scopeKey = "default";
                return;
            }

            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                    _scopeKey = BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
                }
            }
            catch
            {
                _scopeKey = "default";
            }
        }

        private sealed class Entry
        {
            public long characterId;
            public uint catalogVersion;
            public CharacterAppearanceRecipe appearance;
            public CharacterPresentationPreferences presentation;
            public uint equipmentVersion;
            public PlayerEquipmentVisualSelection[] equipmentVisuals;
        }

        public static bool TryLoadAppearance(long characterId, out CharacterAppearanceRecipe appearance)
        {
            appearance = null;
            if (!TryLoad(characterId, out Entry entry) || entry.appearance == null)
                return false;
            appearance = entry.appearance.Clone();
            return true;
        }

        public static bool TryLoadPresentation(long characterId, out CharacterPresentationPreferences presentation)
        {
            presentation = null;
            if (!TryLoad(characterId, out Entry entry) || entry.presentation == null)
                return false;
            presentation = entry.presentation.Clone();
            return true;
        }

        public static bool TryLoadEquipment(
            long characterId,
            out uint equipmentVersion,
            out PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            equipmentVersion = 0;
            equipmentVisuals = Array.Empty<PlayerEquipmentVisualSelection>();
            if (!TryLoad(characterId, out Entry entry))
                return false;

            equipmentVersion = entry.equipmentVersion;
            PlayerEquipmentVisualSelection[] source =
                entry.equipmentVisuals ?? Array.Empty<PlayerEquipmentVisualSelection>();
            equipmentVisuals = new PlayerEquipmentVisualSelection[source.Length];
            if (source.Length > 0)
                Array.Copy(source, equipmentVisuals, source.Length);
            return true;
        }

        public static void SaveAppearance(long characterId, CharacterAppearanceRecipe appearance)
        {
            if (characterId <= 0 || appearance == null || !appearance.IsValid(out _))
                return;

            Entry entry;
            if (!TryLoad(characterId, out entry))
            {
                entry = new Entry
                {
                    characterId = characterId,
                    appearance = appearance.Clone(),
                    presentation = CharacterPresentationPreferences.CreateDefault(),
                    equipmentVersion = 0,
                    equipmentVisuals = Array.Empty<PlayerEquipmentVisualSelection>(),
                };
            }
            else
            {
                if (entry.appearance != null && entry.appearance.revision > appearance.revision)
                    return;
                entry.appearance = appearance.Clone();
            }

            entry.catalogVersion = CharacterVisualProfileRegistry.CatalogVersionFor(appearance.visualProfileId);
            Write(entry);
        }

        public static void SavePresentation(long characterId, CharacterPresentationPreferences presentation)
        {
            if (characterId <= 0 || presentation == null || !presentation.IsValid(out _))
                return;

            Entry entry;
            if (!TryLoad(characterId, out entry))
            {
                entry = new Entry
                {
                    characterId = characterId,
                    appearance = CharacterVisualProfileRegistry.CreateDefaultRecipeOrFallback(),
                    presentation = presentation.Clone(),
                    equipmentVersion = 0,
                    equipmentVisuals = Array.Empty<PlayerEquipmentVisualSelection>(),
                };
            }
            else
            {
                if (entry.presentation != null && entry.presentation.revision > presentation.revision)
                    return;
                entry.presentation = presentation.Clone();
            }

            if (entry.appearance != null)
                entry.catalogVersion = CharacterVisualProfileRegistry.CatalogVersionFor(entry.appearance.visualProfileId);
            Write(entry);
        }

        public static void SaveEquipment(
            long characterId,
            uint equipmentVersion,
            PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            if (characterId <= 0)
                return;

            PlayerEquipmentVisualSelection[] source =
                equipmentVisuals ?? Array.Empty<PlayerEquipmentVisualSelection>();
            if (source.Length > PlayerEntityAppearance.MaxEquipmentVisualSelections)
                return;

            for (int i = 0; i < source.Length; ++i)
            {
                if (source[i].equipmentSlotPresentationId == 0 || source[i].itemPresentationId == 0)
                    return;

                for (int j = i + 1; j < source.Length; ++j)
                    if (source[i].equipmentSlotPresentationId == source[j].equipmentSlotPresentationId)
                        return;
            }

            Entry entry;
            if (!TryLoad(characterId, out entry))
            {
                entry = new Entry
                {
                    characterId = characterId,
                    appearance = CharacterVisualProfileRegistry.CreateDefaultRecipeOrFallback(),
                    presentation = CharacterPresentationPreferences.CreateDefault(),
                    equipmentVersion = equipmentVersion,
                    equipmentVisuals = Array.Empty<PlayerEquipmentVisualSelection>(),
                };
            }
            else if (entry.equipmentVersion > equipmentVersion)
            {
                return;
            }

            entry.equipmentVersion = equipmentVersion;
            entry.equipmentVisuals = new PlayerEquipmentVisualSelection[source.Length];
            if (source.Length > 0)
                Array.Copy(source, entry.equipmentVisuals, source.Length);

            if (entry.appearance != null)
                entry.catalogVersion = CharacterVisualProfileRegistry.CatalogVersionFor(entry.appearance.visualProfileId);
            Write(entry);
        }

        public static void Delete(long characterId)
        {
            if (characterId <= 0)
                return;
            try
            {
                string path = PathFor(characterId);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Presentation cache failure must never block gameplay/login.
            }
        }

        private static bool TryLoad(long characterId, out Entry entry)
        {
            entry = null;
            if (characterId <= 0)
                return false;

            string path = PathFor(characterId);
            if (!File.Exists(path))
                return false;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != Magic)
                        return false;

                    ushort fileVersion = reader.ReadUInt16();
                    if (fileVersion < 1 || fileVersion > FileVersion)
                        return false;

                    long storedCharacterId = reader.ReadInt64();
                    if (storedCharacterId != characterId)
                        return false;

                    uint catalogVersion = reader.ReadUInt32();
                    CharacterAppearanceRecipe appearance = ReadAppearance(reader);
                    CharacterPresentationPreferences presentation = ReadPresentation(reader);
                    uint equipmentVersion = 0;
                    PlayerEquipmentVisualSelection[] equipmentVisuals = Array.Empty<PlayerEquipmentVisualSelection>();
                    if (fileVersion >= 2)
                    {
                        equipmentVersion = reader.ReadUInt32();
                        int equipmentCount = reader.ReadByte();
                        if (equipmentCount > PlayerEntityAppearance.MaxEquipmentVisualSelections)
                            return false;

                        equipmentVisuals = new PlayerEquipmentVisualSelection[equipmentCount];
                        for (int i = 0; i < equipmentCount; ++i)
                        {
                            ushort slotPresentationId = reader.ReadUInt16();
                            ushort itemPresentationId = reader.ReadUInt16();
                            if (slotPresentationId == 0 || itemPresentationId == 0)
                                return false;
                            equipmentVisuals[i] = new PlayerEquipmentVisualSelection(
                                slotPresentationId,
                                itemPresentationId);
                        }
                    }

                    if (appearance == null || !appearance.IsValid(out _) ||
                        presentation == null || !presentation.IsValid(out _))
                        return false;

                    uint currentCatalog = CharacterVisualProfileRegistry.CatalogVersionFor(appearance.visualProfileId);
                    if (catalogVersion != 0 && currentCatalog != 0 && catalogVersion != currentCatalog)
                        return false;

                    entry = new Entry
                    {
                        characterId = characterId,
                        catalogVersion = catalogVersion,
                        appearance = appearance,
                        presentation = presentation,
                        equipmentVersion = equipmentVersion,
                        equipmentVisuals = equipmentVisuals,
                    };
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void Write(Entry entry)
        {
            if (entry == null || entry.characterId <= 0 || entry.appearance == null || entry.presentation == null)
                return;

            try
            {
                Directory.CreateDirectory(CacheDirectory);
                string path = PathFor(entry.characterId);
                string temp = path + ".tmp";

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Magic);
                    writer.Write(FileVersion);
                    writer.Write(entry.characterId);
                    writer.Write(entry.catalogVersion);
                    WriteAppearance(writer, entry.appearance);
                    WritePresentation(writer, entry.presentation);
                    writer.Write(entry.equipmentVersion);
                    PlayerEquipmentVisualSelection[] equipment =
                        entry.equipmentVisuals ?? Array.Empty<PlayerEquipmentVisualSelection>();
                    int equipmentCount = Math.Min(
                        equipment.Length,
                        PlayerEntityAppearance.MaxEquipmentVisualSelections);
                    writer.Write((byte)equipmentCount);
                    for (int i = 0; i < equipmentCount; ++i)
                    {
                        writer.Write(equipment[i].equipmentSlotPresentationId);
                        writer.Write(equipment[i].itemPresentationId);
                    }
                }

                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                // Never fail character creation/login because a local cache write failed.
            }
        }

        private static void WriteAppearance(BinaryWriter writer, CharacterAppearanceRecipe value)
        {
            writer.Write(value.schemaVersion);
            writer.Write(value.visualProfileId);
            writer.Write(value.revision);

            CharacterMeshSelection[] meshes = value.meshes ?? Array.Empty<CharacterMeshSelection>();
            writer.Write((ushort)meshes.Length);
            for (int i = 0; i < meshes.Length; ++i)
            {
                writer.Write(meshes[i].slotId);
                writer.Write(meshes[i].optionId);
            }

            CharacterMorphSelection[] morphs = value.morphs ?? Array.Empty<CharacterMorphSelection>();
            writer.Write((ushort)morphs.Length);
            for (int i = 0; i < morphs.Length; ++i)
            {
                writer.Write(morphs[i].channelId);
                writer.Write(morphs[i].value);
            }

            CharacterColorSelection[] colors = value.colors ?? Array.Empty<CharacterColorSelection>();
            writer.Write((ushort)colors.Length);
            for (int i = 0; i < colors.Length; ++i)
            {
                writer.Write(colors[i].channelId);
                writer.Write((byte)colors[i].encoding);
                writer.Write(colors[i].value);
            }
        }

        private static CharacterAppearanceRecipe ReadAppearance(BinaryReader reader)
        {
            var value = new CharacterAppearanceRecipe
            {
                schemaVersion = reader.ReadUInt16(),
                visualProfileId = reader.ReadUInt16(),
                revision = reader.ReadUInt32(),
            };

            int meshCount = reader.ReadUInt16();
            if (meshCount > CharacterAppearanceRecipe.MaxMeshSelections)
                return null;
            value.meshes = new CharacterMeshSelection[meshCount];
            for (int i = 0; i < meshCount; ++i)
                value.meshes[i] = new CharacterMeshSelection(reader.ReadUInt16(), reader.ReadUInt16());

            int morphCount = reader.ReadUInt16();
            if (morphCount > CharacterAppearanceRecipe.MaxMorphSelections)
                return null;
            value.morphs = new CharacterMorphSelection[morphCount];
            for (int i = 0; i < morphCount; ++i)
                value.morphs[i] = new CharacterMorphSelection(reader.ReadUInt16(), reader.ReadByte());

            int colorCount = reader.ReadUInt16();
            if (colorCount > CharacterAppearanceRecipe.MaxColorSelections)
                return null;
            value.colors = new CharacterColorSelection[colorCount];
            for (int i = 0; i < colorCount; ++i)
            {
                ushort channel = reader.ReadUInt16();
                CharacterColorEncoding encoding = (CharacterColorEncoding)reader.ReadByte();
                uint packed = reader.ReadUInt32();
                value.colors[i] = new CharacterColorSelection(channel, encoding, packed);
            }
            return value;
        }

        private static void WritePresentation(BinaryWriter writer, CharacterPresentationPreferences value)
        {
            writer.Write(value.schemaVersion);
            writer.Write(value.revision);
            writer.Write(value.movementStyle);
        }

        private static CharacterPresentationPreferences ReadPresentation(BinaryReader reader)
        {
            return new CharacterPresentationPreferences
            {
                schemaVersion = reader.ReadUInt16(),
                revision = reader.ReadUInt32(),
                movementStyle = reader.ReadByte(),
            };
        }

        private static string CacheDirectory =>
            Path.Combine(Application.persistentDataPath, "CharacterVisualCache", _scopeKey);

        private static string PathFor(long characterId) =>
            Path.Combine(CacheDirectory, characterId.ToString() + ".bin");
    }
}
