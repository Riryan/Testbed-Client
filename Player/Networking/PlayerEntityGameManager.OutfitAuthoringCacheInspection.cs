#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using LiteNetLib.Utils;
using UnityEngine;
using Player.Shared;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        /// <summary>
        /// Editor-only inspection of the existing persisted GameplaySettingsCache format.
        /// Reuses the same cache magic/version/protocol constants and snapshot Deserialize path;
        /// it does not introduce a second cache representation or any network request.
        /// </summary>
        public static bool TryReadLatestGameplaySettingsCacheForEditor(
            out GameplaySettingsSnapshotMessage snapshot,
            out string sourcePath,
            out string detail)
        {
            snapshot = default;
            sourcePath = string.Empty;
            detail = string.Empty;

            string directory = Path.Combine(Application.persistentDataPath, "GameplaySettingsCache");
            if (!Directory.Exists(directory))
            {
                detail = "GameplaySettingsCache directory does not exist yet.";
                return false;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.bin", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                detail = "Could not enumerate GameplaySettingsCache: " + ex.Message;
                return false;
            }

            if (files.Length == 0)
            {
                detail = "No persisted gameplay-settings cache files exist yet.";
                return false;
            }

            Array.Sort(files, (a, b) =>
            {
                DateTime at;
                DateTime bt;
                try { at = File.GetLastWriteTimeUtc(a); } catch { at = DateTime.MinValue; }
                try { bt = File.GetLastWriteTimeUtc(b); } catch { bt = DateTime.MinValue; }
                return bt.CompareTo(at);
            });

            for (int i = 0; i < files.Length; ++i)
            {
                string path = files[i];
                try
                {
                    byte[] payload;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new BinaryReader(stream, Encoding.UTF8))
                    {
                        if (reader.ReadUInt32() != GameplaySettingsCacheMagic ||
                            reader.ReadUInt16() != GameplaySettingsCacheVersion ||
                            reader.ReadUInt16() != PlayerEntityProtocol.Version)
                        {
                            continue;
                        }

                        int length = reader.ReadInt32();
                        if (length <= 0 || length > GameplaySettingsCacheMaxBytes ||
                            stream.Length - stream.Position != length)
                        {
                            continue;
                        }

                        payload = reader.ReadBytes(length);
                        if (payload.Length != length)
                            continue;
                    }

                    var netReader = new NetDataReader(payload);
                    GameplaySettingsSnapshotMessage candidate = default;
                    candidate.Deserialize(netReader);
                    if (!candidate.success || candidate.revision <= 0)
                        continue;

                    snapshot = candidate;
                    sourcePath = path;
                    detail = files.Length > 1
                        ? $"Using newest valid persisted cache of {files.Length} cache file(s)."
                        : "Using persisted gameplay-settings cache.";
                    return true;
                }
                catch
                {
                    // Try the next cache file. Inspection never mutates or deletes cache state.
                }
            }

            detail = $"No valid gameplay-settings cache snapshot was readable from {files.Length} cache file(s).";
            return false;
        }
    }
}
#endif
