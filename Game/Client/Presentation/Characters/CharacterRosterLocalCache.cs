using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Player.Networking;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Local-only character-select roster cache. This cache is presentation convenience,
    /// never gameplay authority. A cache miss may recover from the authenticated server;
    /// a cache hit avoids the routine CharacterList request entirely.
    ///
    /// Cache identity is scoped by authentication endpoint + authoritative account id so
    /// Local and Live servers cannot collide even when their database ids overlap.
    /// </summary>
    public static class CharacterRosterLocalCache
    {
        private const uint Magic = 0x524F4D4D; // "MMOR" little-endian
        private const ushort FileVersion = 1;

        public static bool TryLoad(
            string serverScope,
            long accountId,
            out CharacterSessionCharacterSummary[] characters) =>
            TryLoad(serverScope, accountId, out characters, out _);

        public static bool TryLoad(
            string serverScope,
            long accountId,
            out CharacterSessionCharacterSummary[] characters,
            out long rosterRevision)
        {
            characters = Array.Empty<CharacterSessionCharacterSummary>();
            rosterRevision = 0L;
            if (accountId <= 0 || string.IsNullOrWhiteSpace(serverScope))
                return false;

            string path = PathFor(serverScope, accountId);
            if (!File.Exists(path))
                return false;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != FileVersion)
                        return false;
                    if (reader.ReadInt64() != accountId)
                        return false;

                    int count = reader.ReadByte();
                    if (count < 0 || count > CharacterListResponseMessage.MaxCharacters)
                        return false;

                    var result = new CharacterSessionCharacterSummary[count];
                    for (int i = 0; i < count; ++i)
                    {
                        long characterId = reader.ReadInt64();
                        string name = ReadBoundedString(reader, 128);
                        string mapId = ReadBoundedString(reader, 128);
                        if (characterId <= 0)
                            return false;
                        result[i] = new CharacterSessionCharacterSummary(characterId, name, mapId);
                    }

                    characters = result;
                    rosterRevision = CharacterRosterStateRevision.Compute(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Save(
            string serverScope,
            long accountId,
            CharacterSessionCharacterSummary[] characters)
        {
            if (accountId <= 0 || string.IsNullOrWhiteSpace(serverScope))
                return;

            CharacterSessionCharacterSummary[] source =
                characters ?? Array.Empty<CharacterSessionCharacterSummary>();
            int sourceLimit = Math.Min(source.Length, CharacterListResponseMessage.MaxCharacters);
            int count = 0;
            for (int i = 0; i < sourceLimit; ++i)
                if (source[i].characterId > 0)
                    count++;

            try
            {
                Directory.CreateDirectory(CacheDirectory);
                string path = PathFor(serverScope, accountId);
                string temp = path + ".tmp";

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(FileVersion);
                    writer.Write(accountId);
                    writer.Write((byte)count);
                    for (int i = 0; i < sourceLimit; ++i)
                    {
                        CharacterSessionCharacterSummary summary = source[i];
                        if (summary.characterId <= 0)
                            continue;
                        writer.Write(summary.characterId);
                        WriteBoundedString(writer, summary.name, 128);
                        WriteBoundedString(writer, summary.mapId, 128);
                    }
                }

                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                // Local cache failure must never block login or gameplay.
            }
        }

        public static CharacterSessionCharacterSummary[] AddOrUpdate(
            CharacterSessionCharacterSummary[] characters,
            CharacterSessionCharacterSummary summary)
        {
            if (summary.characterId <= 0)
                return characters ?? Array.Empty<CharacterSessionCharacterSummary>();

            CharacterSessionCharacterSummary[] source =
                characters ?? Array.Empty<CharacterSessionCharacterSummary>();
            for (int i = 0; i < source.Length; ++i)
            {
                if (source[i].characterId != summary.characterId)
                    continue;
                var next = (CharacterSessionCharacterSummary[])source.Clone();
                next[i] = summary;
                return next;
            }

            if (source.Length >= CharacterListResponseMessage.MaxCharacters)
                return source;

            var appended = new CharacterSessionCharacterSummary[source.Length + 1];
            if (source.Length > 0)
                Array.Copy(source, appended, source.Length);
            appended[source.Length] = summary;
            return appended;
        }

        public static CharacterSessionCharacterSummary[] Remove(
            CharacterSessionCharacterSummary[] characters,
            long characterId)
        {
            CharacterSessionCharacterSummary[] source =
                characters ?? Array.Empty<CharacterSessionCharacterSummary>();
            if (characterId <= 0 || source.Length == 0)
                return source;

            int index = Array.FindIndex(source, c => c.characterId == characterId);
            if (index < 0)
                return source;

            var next = new CharacterSessionCharacterSummary[source.Length - 1];
            if (index > 0)
                Array.Copy(source, 0, next, 0, index);
            if (index + 1 < source.Length)
                Array.Copy(source, index + 1, next, index, source.Length - index - 1);
            return next;
        }

        private static string CacheDirectory =>
            Path.Combine(Application.persistentDataPath, "CharacterRosterCache");

        private static string PathFor(string serverScope, long accountId)
        {
            string normalized = (serverScope ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
            string scopeHash;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                scopeHash = BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }
            return Path.Combine(CacheDirectory, $"{scopeHash}_{accountId}.bin");
        }

        private static void WriteBoundedString(BinaryWriter writer, string value, int maxChars)
        {
            string safe = value ?? string.Empty;
            if (safe.Length > maxChars)
                safe = safe.Substring(0, maxChars);
            writer.Write(safe);
        }

        private static string ReadBoundedString(BinaryReader reader, int maxChars)
        {
            string value = reader.ReadString() ?? string.Empty;
            if (value.Length > maxChars)
                throw new InvalidDataException("cached character string exceeds limit");
            return value;
        }
    }
}
