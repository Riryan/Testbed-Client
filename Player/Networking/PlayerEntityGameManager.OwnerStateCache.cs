using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Shared;
using UnityEngine;

namespace Player.Networking
{
    /// <summary>
    /// Persistent owner-visible reconnect cache. This is a transport/UI optimization only:
    /// the GameServer remains authoritative and every cached revision is reconciled before
    /// the corresponding Ready baseline may be suppressed.
    /// </summary>
    public sealed partial class PlayerEntityGameManager
    {
        private const uint OwnerStateCacheMagic = 0x434F4D4D; // "MMOC" little-endian
        private const ushort OwnerStateCacheVersion = 1;
        private const int OwnerStateCacheMaxBytes = 2 * 1024 * 1024;

        private long _clientOwnerStateCharacterId;
        private long _friendsCacheRevision;

        private async UniTask PrepareOwnerStateCachesForAdmissionAsync(
            long characterId,
            int millisecondsTimeout)
        {
            if (IsServer || !IsClientConnected || characterId <= 0)
                return;

            if (_clientOwnerStateCharacterId > 0 && _clientOwnerStateCharacterId != characterId)
                PersistClientOwnerStateCaches();

            _clientOwnerStateCharacterId = characterId;
            RestoreClientOwnerStateCaches(characterId);

            // These probes are independent once Settings has been restored/validated.
            // They carry only already-known revisions and never grant authority.
            await UniTask.WhenAll(
                ProbePlayerItemsCacheBeforeReadyAsync(millisecondsTimeout),
                ProbeProgressionCacheBeforeReadyAsync(millisecondsTimeout),
                ProbeFriendsCacheBeforeReadyAsync(millisecondsTimeout));
        }

        private void RestoreClientOwnerStateCaches(long characterId)
        {
            _latestPlayerItems = default;
            _latestProgression = default;
            _latestFriends = default;
            _hasFriendsCache = false;
            _friendsCacheRevision = 0;

            string scope = AuthenticationServiceBaseUrl;

            if (TryLoadOwnerStateCache(scope, characterId, "items", out PlayerItemsResponseMessage items) &&
                items.success && items.inventoryCapacity > 0 &&
                items.contentRevision > 0 &&
                items.contentRevision == PlayerGameplaySettingsRuntime.Revision)
            {
                ApplyLatestPlayerItems(items, notify: false);
                RefreshPlayerItemPresentationFromGameplaySettings();
            }

            if (TryLoadOwnerStateCache(scope, characterId, "progression", out ProgressionSnapshotMessage progression) &&
                progression.success && progression.contentRevision > 0 &&
                progression.contentRevision == PlayerGameplaySettingsRuntime.Revision)
            {
                _latestProgression = progression;
                _progressionReconciliationPending = false;
            }

            if (TryLoadOwnerStateCache(scope, characterId, "friends", out FriendsStateMessage friends))
            {
                FriendEntryWire[] entries = friends.friends ?? Array.Empty<FriendEntryWire>();
                // Presence and pending invitations are live/session state. Persist membership only.
                for (int i = 0; i < entries.Length; ++i)
                    entries[i].online = false;
                friends.friends = entries;
                friends.pendingInviterCharacterId = 0;
                friends.pendingInviterName = string.Empty;
                _latestFriends = friends;
                _hasFriendsCache = true;
                _friendsCacheRevision = FriendsStateRevision.Compute(entries);
            }
        }

        private async UniTask ProbePlayerItemsCacheBeforeReadyAsync(int millisecondsTimeout)
        {
            if (!HasPlayerItemsCache || !IsClientConnected)
                return;

            try
            {
                AsyncResponseData<PlayerItemsResponseMessage> response =
                    await ClientSendRequestAsync<PlayerItemsSnapshotRequestMessage, PlayerItemsResponseMessage>(
                        PlayerItemRequestTypes.Snapshot,
                        new PlayerItemsSnapshotRequestMessage
                        {
                            knownContentRevision = _latestPlayerItems.contentRevision,
                            knownInventoryRevision = _latestPlayerItems.inventoryRevision,
                            knownEquipmentRevision = _latestPlayerItems.equipmentRevision,
                        },
                        Math.Min(millisecondsTimeout, 3000));

                if (response.IsSuccess && response.Response.IsNotModified)
                    return;

                // A stale/unsupported probe intentionally does nothing. The existing Ready
                // baseline will replace the cache authoritatively.
            }
            catch
            {
                // Local cache/probe failure must never block world admission.
            }
        }

        private async UniTask ProbeProgressionCacheBeforeReadyAsync(int millisecondsTimeout)
        {
            if (!_latestProgression.success || !IsClientConnected)
                return;

            try
            {
                AsyncResponseData<ProgressionSnapshotMessage> response =
                    await ClientSendRequestAsync<ProgressionSnapshotRequestMessage, ProgressionSnapshotMessage>(
                        ProgressionRequestTypes.Snapshot,
                        new ProgressionSnapshotRequestMessage
                        {
                            knownContentRevision = _latestProgression.contentRevision,
                            knownRevision = _latestProgression.revision,
                        },
                        Math.Min(millisecondsTimeout, 3000));

                if (response.IsSuccess && response.Response.IsNotModified)
                    return;
            }
            catch
            {
                // Fail open to the normal Ready baseline.
            }
        }

        private async UniTask ProbeFriendsCacheBeforeReadyAsync(int millisecondsTimeout)
        {
            if (!_hasFriendsCache || _friendsCacheRevision == 0 || !IsClientConnected)
                return;

            try
            {
                // Request 700 is reused as a pre-Ready membership-revision hint. The
                // authoritative friend list is loaded after Ready and compared before any
                // full list is retransmitted. The response is deliberately ignored here.
                await ClientSendRequestAsync<EmptySocialRequestMessage, FriendsStateMessage>(
                    FriendRequestTypes.Snapshot,
                    new EmptySocialRequestMessage { knownRevision = _friendsCacheRevision },
                    Math.Min(millisecondsTimeout, 3000));
            }
            catch
            {
                // Fail open to the normal Ready friend hydration.
            }
        }

        private void PersistClientOwnerStateCaches()
        {
            long characterId = _clientOwnerStateCharacterId;
            if (characterId <= 0)
                return;

            string scope = AuthenticationServiceBaseUrl;
            if (HasPlayerItemsCache)
                SaveOwnerStateCache(scope, characterId, "items", _latestPlayerItems);
            if (_latestProgression.success)
                SaveOwnerStateCache(scope, characterId, "progression", _latestProgression);
            if (_hasFriendsCache)
            {
                FriendsStateMessage durable = _latestFriends;
                FriendEntryWire[] source = durable.friends ?? Array.Empty<FriendEntryWire>();
                var membership = new FriendEntryWire[source.Length];
                for (int i = 0; i < source.Length; ++i)
                {
                    membership[i] = source[i];
                    membership[i].online = false;
                }
                durable.friends = membership;
                durable.pendingInviterCharacterId = 0;
                durable.pendingInviterName = string.Empty;
                SaveOwnerStateCache(scope, characterId, "friends", durable);
            }
        }

        private void ResetClientOwnerStateCacheAdmission()
        {
            _clientOwnerStateCharacterId = 0;
            _friendsCacheRevision = 0;
        }

        private static bool TryLoadOwnerStateCache<T>(
            string serverScope,
            long characterId,
            string kind,
            out T value)
            where T : struct, INetSerializable
        {
            value = default;
            string path = OwnerStateCachePath(serverScope, characterId, kind);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            try
            {
                byte[] payload;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadUInt32() != OwnerStateCacheMagic ||
                        reader.ReadUInt16() != OwnerStateCacheVersion ||
                        reader.ReadUInt16() != PlayerEntityProtocol.Version ||
                        reader.ReadInt64() != characterId)
                    {
                        DeleteOwnerStateCache(path);
                        return false;
                    }

                    int length = reader.ReadInt32();
                    if (length <= 0 || length > OwnerStateCacheMaxBytes ||
                        stream.Length - stream.Position != length)
                    {
                        DeleteOwnerStateCache(path);
                        return false;
                    }
                    payload = reader.ReadBytes(length);
                    if (payload.Length != length)
                    {
                        DeleteOwnerStateCache(path);
                        return false;
                    }
                }

                var netReader = new NetDataReader(payload);
                value.Deserialize(netReader);
                if (netReader.AvailableBytes != 0)
                {
                    DeleteOwnerStateCache(path);
                    value = default;
                    return false;
                }
                return true;
            }
            catch
            {
                DeleteOwnerStateCache(path);
                value = default;
                return false;
            }
        }

        private static void SaveOwnerStateCache<T>(
            string serverScope,
            long characterId,
            string kind,
            T value)
            where T : struct, INetSerializable
        {
            if (characterId <= 0 || string.IsNullOrWhiteSpace(serverScope) || string.IsNullOrWhiteSpace(kind))
                return;

            try
            {
                var netWriter = new NetDataWriter();
                value.Serialize(netWriter);
                byte[] payload = netWriter.CopyData();
                if (payload.Length <= 0 || payload.Length > OwnerStateCacheMaxBytes)
                    return;

                string path = OwnerStateCachePath(serverScope, characterId, kind);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(OwnerStateCacheMagic);
                    writer.Write(OwnerStateCacheVersion);
                    writer.Write(PlayerEntityProtocol.Version);
                    writer.Write(characterId);
                    writer.Write(payload.Length);
                    writer.Write(payload);
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                // A local optimization must never block gameplay or logout.
            }
        }

        private static string OwnerStateCachePath(string serverScope, long characterId, string kind)
        {
            if (characterId <= 0 || string.IsNullOrWhiteSpace(serverScope) || string.IsNullOrWhiteSpace(kind))
                return string.Empty;
            string normalized = serverScope.Trim().TrimEnd('/').ToLowerInvariant();
            string scopeHash;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                scopeHash = BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
            }
            return Path.Combine(
                Application.persistentDataPath,
                "OwnerStateCache",
                scopeHash,
                $"{characterId}_{kind}.bin");
        }

        private static void DeleteOwnerStateCache(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }
}
