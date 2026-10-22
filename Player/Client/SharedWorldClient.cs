using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Game.Shared.World;
using Player.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Player.Client
{
    /// <summary>
    /// Local, read-only prediction view of the existing shared world package.
    /// Collision and grounded movement are delegated to Game.Server.Application
    /// through the existing Player.Networking assembly dependency.
    /// Loading is unchanged in principle: async, scene-owned, and never authority.
    /// </summary>
    public static class SharedWorldClient
    {
        private sealed class RuntimeWorld
        {
            public readonly SharedWorldSnapshot Snapshot;
            private readonly SharedMovementQueries _queries;

            public RuntimeWorld(SharedWorldSnapshot snapshot)
            {
                Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
                _queries = new SharedMovementQueries(snapshot);
            }

            public bool TryResolveGroundedMove(Vector3 start, Vector3 delta,
                float radius, float stepUp, float snapDown,
                out Vector3 result, out bool blocked)
            {
                WorldPosition origin = new WorldPosition(start.x, start.y, start.z);
                _queries.TryResolveGroundedMove(origin, delta.x, delta.z,
                    radius, stepUp, snapDown, out WorldPosition moved, out blocked);
                result = new Vector3(moved.X, moved.Y, moved.Z);
                return true;
            }

            public bool TryFindSupportHeight(float x, float z, float referenceY,
                float probeUp, float probeDown, out float y) =>
                _queries.TryFindSupportHeight(x, z, referenceY, probeUp, probeDown, out y);

            public bool TrySetDynamicBlockerEnabled(long id, bool enabled) =>
                _queries.TrySetDynamicBlockerEnabled(id, enabled);
        }

        private static RuntimeWorld _world;
        private static string _loadAttemptScene = string.Empty;
        private static int _loadGeneration;
        private static bool _loadInProgress;
        private static readonly Dictionary<long, bool> PendingBlockerStates =
            new Dictionary<long, bool>();

        public static bool IsLoaded => _world != null;
        public static bool IsLoading => _loadInProgress;
        public static string LoadedMapId => _world?.Snapshot?.mapId ?? string.Empty;
        public static string LoadedHash => _world?.Snapshot?.contentHash ?? string.Empty;

        public static bool TrySetDynamicBlockerEnabled(long stableId, bool enabled)
        {
            if (stableId <= 0) return false;
            if (_world != null) return _world.TrySetDynamicBlockerEnabled(stableId, enabled);
            PendingBlockerStates[stableId] = enabled;
            EnsureLoaded();
            return false;
        }

        public static bool TryResolveGroundedMove(Vector3 start, Vector3 desiredDelta,
            float radius, float maximumStepUp, float maximumSnapDown,
            out Vector3 resolved, out bool blocked)
        {
            resolved = start + new Vector3(desiredDelta.x, 0f, desiredDelta.z);
            blocked = false;
            if (!EnsureLoaded()) return false;
            return _world.TryResolveGroundedMove(start, desiredDelta,
                radius, maximumStepUp, maximumSnapDown, out resolved, out blocked);
        }

        public static bool TryFindSupportHeight(float x, float z, float referenceY,
            float probeUp, float probeDown, out float y)
        {
            y = referenceY;
            if (!EnsureLoaded()) return false;
            return _world.TryFindSupportHeight(x, z, referenceY, probeUp, probeDown, out y);
        }

        private static bool EnsureLoaded()
        {
            if (_world != null) return true;
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded)
                BeginLoad(active.name, eagerOnly: false);
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeSceneWarmup()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded)
                BeginLoad(active.name, eagerOnly: true);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid() || active.handle != scene.handle) return;
            ClearForSceneChange();
            BeginLoad(scene.name, eagerOnly: true);
        }

        private static void OnActiveSceneChanged(Scene oldScene, Scene newScene)
        {
            ClearForSceneChange();
            if (newScene.IsValid() && newScene.isLoaded)
                BeginLoad(newScene.name, eagerOnly: true);
        }

        private static void ClearForSceneChange()
        {
            ++_loadGeneration;
            _world = null;
            _loadAttemptScene = string.Empty;
            _loadInProgress = false;
            PendingBlockerStates.Clear();
        }

        private static void BeginLoad(string sceneName, bool eagerOnly)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || _world != null ||
                string.Equals(_loadAttemptScene, sceneName, StringComparison.Ordinal))
                return;
            string folder = Path.Combine(Application.streamingAssetsPath, "MMOWorld");
            if (!Directory.Exists(folder))
            {
                _loadAttemptScene = sceneName;
                return;
            }
            string mapId = ServerMapId.Normalize(sceneName);
            string preferred = Path.Combine(folder, mapId + ".sharedworld.json.gz");
            string legacy = Path.Combine(folder, mapId + ".sharedworld.json");
            bool exactMatch = File.Exists(preferred) || File.Exists(legacy);
            if (eagerOnly && !exactMatch) return;
            _loadAttemptScene = sceneName;
            _loadInProgress = true;
            LoadAsync(sceneName, folder, ++_loadGeneration);
        }

        private static async void LoadAsync(string sceneName, string folder, int generation)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            RuntimeWorld loaded = null;
            Exception failure = null;
            try
            {
                loaded = await Task.Run(() => ReadAndBuildWorld(folder, sceneName));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            Scene active = SceneManager.GetActiveScene();
            if (generation != _loadGeneration || !active.IsValid() ||
                !string.Equals(sceneName, active.name, StringComparison.Ordinal))
                return;
            _loadInProgress = false;
            if (failure != null)
            {
                Debug.LogWarning($"[Shared World] Background preparation failed: {failure.Message}");
                return;
            }
            if (loaded == null) return;
            foreach (KeyValuePair<long, bool> pair in PendingBlockerStates)
                loaded.TrySetDynamicBlockerEnabled(pair.Key, pair.Value);
            PendingBlockerStates.Clear();
            _world = loaded;
            Debug.Log($"[Shared World] Ready '{loaded.Snapshot.mapId}' " +
                $"revision={loaded.Snapshot.bakeRevision}, " +
                $"support={loaded.Snapshot.supportTriangles?.Length ?? 0:n0}, " +
                $"collision={loaded.Snapshot.collisionTriangles?.Length ?? 0:n0}, " +
                $"prepareMs={timer.Elapsed.TotalMilliseconds:0.##} (background)");
        }

        private static RuntimeWorld ReadAndBuildWorld(string folder, string sceneName)
        {
            if (!Directory.Exists(folder)) return null;
            string mapId = ServerMapId.Normalize(sceneName);
            string preferred = Path.Combine(folder, mapId + ".sharedworld.json.gz");
            string legacy = Path.Combine(folder, mapId + ".sharedworld.json");
            string path = File.Exists(preferred) ? preferred
                : File.Exists(legacy) ? legacy : FindFallback(folder, mapId);
            if (string.IsNullOrWhiteSpace(path)) return null;
            string json;
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using (FileStream stream = File.OpenRead(path))
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                    json = reader.ReadToEnd();
            }
            else json = File.ReadAllText(path);
            SharedWorldSnapshot snapshot = JsonUtility.FromJson<SharedWorldSnapshot>(json);
            if (snapshot == null || snapshot.formatVersion != SharedWorldFormat.Version ||
                string.IsNullOrWhiteSpace(snapshot.mapId))
                throw new InvalidDataException($"Invalid client world package: {path}");
            return new RuntimeWorld(snapshot);
        }

        private static string FindFallback(string folder, string mapId)
        {
            string[] compressed = Directory.GetFiles(folder, "*.sharedworld.json.gz", SearchOption.TopDirectoryOnly);
            string[] legacy = Directory.GetFiles(folder, "*.sharedworld.json", SearchOption.TopDirectoryOnly);
            string[] files = new string[compressed.Length + legacy.Length];
            Array.Copy(compressed, files, compressed.Length);
            Array.Copy(legacy, 0, files, compressed.Length, legacy.Length);
            if (files.Length == 1) return files[0];
            for (int i = 0; i < files.Length; ++i)
            {
                string name = Path.GetFileName(files[i]);
                const string gzSuffix = ".sharedworld.json.gz";
                const string jsonSuffix = ".sharedworld.json";
                if (name.EndsWith(gzSuffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - gzSuffix.Length);
                else if (name.EndsWith(jsonSuffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - jsonSuffix.Length);
                if (name.StartsWith(mapId, StringComparison.OrdinalIgnoreCase))
                    return files[i];
            }
            return string.Empty;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            ClearForSceneChange();
        }
    }
}
