using System;
using System.Collections.Generic;
using System.IO;
using Game.Shared.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Player.Client
{
    /// <summary>
    /// Read-only client query view over the client-safe Server World Bake V2 package.
    /// It is prediction data only: the standalone GameServer still independently validates
    /// every movement/interaction and remains the sole authority.
    /// </summary>
    public static class SharedWorldClient
    {
        private readonly struct CellKey : IEquatable<CellKey>
        {
            public readonly int X;
            public readonly int Z;
            public CellKey(int x, int z) { X = x; Z = z; }
            public bool Equals(CellKey other) => X == other.X && Z == other.Z;
            public override bool Equals(object obj) => obj is CellKey other && Equals(other);
            public override int GetHashCode() => unchecked((X * 397) ^ Z);
        }

        private sealed class RuntimeWorld
        {
            private const float CellSize = 8f;
            private const float Epsilon = 0.0001f;

            private sealed class DynamicBlockerState
            {
                public ServerDynamicBlocker Definition;
                public bool Enabled;
            }

            private readonly ServerCollisionTriangle[] _support;
            private readonly ServerCollisionTriangle[] _collision;
            private readonly Dictionary<CellKey, List<int>> _supportGrid = new Dictionary<CellKey, List<int>>();
            private readonly Dictionary<CellKey, List<int>> _collisionGrid = new Dictionary<CellKey, List<int>>();
            private readonly Dictionary<long, DynamicBlockerState> _dynamicBlockers =
                new Dictionary<long, DynamicBlockerState>();
            private readonly Dictionary<CellKey, List<long>> _dynamicBlockerGrid =
                new Dictionary<CellKey, List<long>>();
            private readonly HashSet<int> _scratch = new HashSet<int>();
            private readonly HashSet<long> _dynamicBlockerScratch = new HashSet<long>();

            public SharedWorldSnapshot Snapshot { get; }

            public RuntimeWorld(SharedWorldSnapshot snapshot)
            {
                Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
                _support = snapshot.supportTriangles ?? Array.Empty<ServerCollisionTriangle>();
                _collision = snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>();
                BuildGrid(_support, _supportGrid);
                BuildGrid(_collision, _collisionGrid);

                ServerDynamicBlocker[] blockers =
                    snapshot.dynamicBlockers ?? Array.Empty<ServerDynamicBlocker>();
                for (int i = 0; i < blockers.Length; ++i)
                {
                    ServerDynamicBlocker blocker = blockers[i];
                    if (blocker == null || blocker.stableId <= 0)
                        continue;
                    _dynamicBlockers[blocker.stableId] = new DynamicBlockerState
                    {
                        Definition = blocker,
                        Enabled = blocker.enabledByDefault,
                    };
                    IndexDynamicBlocker(blocker);
                }
            }

            public bool TrySetDynamicBlockerEnabled(long stableId, bool enabled)
            {
                if (stableId <= 0 || !_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state))
                    return false;
                state.Enabled = enabled;
                return true;
            }

            public bool TryResolveGroundedMove(
                Vector3 start,
                Vector3 desiredDelta,
                float radius,
                float maximumStepUp,
                float maximumSnapDown,
                out Vector3 resolved,
                out bool blocked)
            {
                resolved = start;
                blocked = false;

                desiredDelta.y = 0f;
                if (desiredDelta.sqrMagnitude <= 0.0000001f)
                    return true;

                float safeRadius = Mathf.Max(0.05f, radius);
                float stepUp = Mathf.Max(0.01f, maximumStepUp);
                float snapDown = Mathf.Max(0.01f, maximumSnapDown);

                if (TryGroundedCandidate(start, desiredDelta, safeRadius, stepUp, snapDown, out resolved))
                    return true;

                // Match the server motor's basic soft-slide behavior. A diagonal input that
                // can legitimately slide along a wall/ledge is still useful intent and must
                // not be suppressed merely because the full diagonal candidate was blocked.
                Vector3 xOnly = start;
                Vector3 zOnly = start;
                bool canX = Mathf.Abs(desiredDelta.x) > Epsilon &&
                    TryGroundedCandidate(
                        start, new Vector3(desiredDelta.x, 0f, 0f), safeRadius, stepUp, snapDown, out xOnly);
                bool canZ = Mathf.Abs(desiredDelta.z) > Epsilon &&
                    TryGroundedCandidate(
                        start, new Vector3(0f, 0f, desiredDelta.z), safeRadius, stepUp, snapDown, out zOnly);

                if (canX && (!canZ || Mathf.Abs(desiredDelta.x) >= Mathf.Abs(desiredDelta.z)))
                {
                    resolved = xOnly;
                    return true;
                }
                if (canZ)
                {
                    resolved = zOnly;
                    return true;
                }

                // No supported axis can advance: a good client can suppress this held intent.
                blocked = true;
                return true;
            }

            private bool TryGroundedCandidate(
                Vector3 start,
                Vector3 desiredDelta,
                float radius,
                float maximumStepUp,
                float maximumSnapDown,
                out Vector3 resolved)
            {
                resolved = start;
                Vector3 target = start + desiredDelta;
                if (IsHorizontalPathBlocked(start, target, radius))
                    return false;

                if (!TryFindSupportHeight(
                        target.x,
                        target.z,
                        start.y,
                        maximumStepUp,
                        maximumSnapDown,
                        out float supportY))
                {
                    return false;
                }

                resolved = new Vector3(target.x, supportY, target.z);
                return true;
            }

            private bool IsHorizontalPathBlocked(Vector3 start, Vector3 target, float radius)
            {
                Vector3 planar = target - start;
                planar.y = 0f;
                float length = planar.magnitude;
                if (length <= Epsilon)
                    return false;

                Vector3 direction = planar / length;
                Vector3 side = new Vector3(-direction.z, 0f, direction.x) * radius;
                const float bodyProbeHeight = 0.90f;

                Vector3 a0 = start + Vector3.up * bodyProbeHeight;
                Vector3 b0 = target + Vector3.up * bodyProbeHeight;
                if (SegmentHitsCollision(a0, b0))
                    return true;
                if (SegmentHitsCollision(a0 + side, b0 + side))
                    return true;
                if (SegmentHitsCollision(a0 - side, b0 - side))
                    return true;
                return false;
            }

            private bool SegmentHitsCollision(Vector3 from, Vector3 to)
            {
                float minX = Mathf.Min(from.x, to.x) - 0.05f;
                float maxX = Mathf.Max(from.x, to.x) + 0.05f;
                float minZ = Mathf.Min(from.z, to.z) - 0.05f;
                float maxZ = Mathf.Max(from.z, to.z) + 0.05f;
                Query(_collisionGrid, minX, minZ, maxX, maxZ);

                foreach (int index in _scratch)
                {
                    if (SegmentIntersectsTriangle(from, to, _collision[index]))
                        return true;
                }

                QueryDynamicBlockers(minX, minZ, maxX, maxZ);
                foreach (long stableId in _dynamicBlockerScratch)
                {
                    if (_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) &&
                        state.Enabled &&
                        SegmentIntersectsOrientedBox(from, to, state.Definition))
                    {
                        return true;
                    }
                }
                return false;
            }

            public bool TryFindSupportHeight(
                float x,
                float z,
                float referenceY,
                float probeUp,
                float probeDown,
                out float y)
            {
                y = 0f;
                Query(_supportGrid, x - 0.01f, z - 0.01f, x + 0.01f, z + 0.01f);

                bool found = false;
                float bestDelta = float.PositiveInfinity;
                foreach (int index in _scratch)
                {
                    ServerCollisionTriangle t = _support[index];
                    if (!TryHeightAtXZ(t, x, z, out float candidate))
                        continue;
                    if (candidate > referenceY + probeUp || candidate < referenceY - probeDown)
                        continue;

                    float delta = Mathf.Abs(candidate - referenceY);
                    if (delta >= bestDelta)
                        continue;

                    bestDelta = delta;
                    y = candidate;
                    found = true;
                }
                return found;
            }

            private static bool TryHeightAtXZ(ServerCollisionTriangle t, float x, float z, out float y)
            {
                y = 0f;
                float ax = t.ax;
                float az = t.az;
                float bx = t.bx;
                float bz = t.bz;
                float cx = t.cx;
                float cz = t.cz;

                float denominator = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                if (Mathf.Abs(denominator) <= 0.0000001f)
                    return false;

                float u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / denominator;
                float v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / denominator;
                float w = 1f - u - v;
                const float edgeTolerance = -0.0005f;
                if (u < edgeTolerance || v < edgeTolerance || w < edgeTolerance)
                    return false;

                y = u * t.ay + v * t.by + w * t.cy;
                return true;
            }

            private static bool SegmentIntersectsTriangle(Vector3 from, Vector3 to, ServerCollisionTriangle t)
            {
                Vector3 a = new Vector3(t.ax, t.ay, t.az);
                Vector3 b = new Vector3(t.bx, t.by, t.bz);
                Vector3 c = new Vector3(t.cx, t.cy, t.cz);
                Vector3 direction = to - from;
                Vector3 edge1 = b - a;
                Vector3 edge2 = c - a;
                Vector3 p = Vector3.Cross(direction, edge2);
                float determinant = Vector3.Dot(edge1, p);
                if (Mathf.Abs(determinant) < 0.000001f)
                    return false;

                float inv = 1f / determinant;
                Vector3 s = from - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f)
                    return false;

                Vector3 q = Vector3.Cross(s, edge1);
                float v = Vector3.Dot(direction, q) * inv;
                if (v < 0f || u + v > 1f)
                    return false;

                float distance = Vector3.Dot(edge2, q) * inv;
                return distance >= 0f && distance <= 1f;
            }

            private void IndexDynamicBlocker(ServerDynamicBlocker blocker)
            {
                float halfX = Mathf.Max(0.01f, blocker.sizeX * 0.5f);
                float halfZ = Mathf.Max(0.01f, blocker.sizeZ * 0.5f);
                float yaw = blocker.pose.yaw * Mathf.Deg2Rad;
                float cos = Mathf.Abs(Mathf.Cos(yaw));
                float sin = Mathf.Abs(Mathf.Sin(yaw));
                float extentX = cos * halfX + sin * halfZ;
                float extentZ = sin * halfX + cos * halfZ;

                int minCellX = Mathf.FloorToInt((blocker.pose.x - extentX) / CellSize);
                int maxCellX = Mathf.FloorToInt((blocker.pose.x + extentX) / CellSize);
                int minCellZ = Mathf.FloorToInt((blocker.pose.z - extentZ) / CellSize);
                int maxCellZ = Mathf.FloorToInt((blocker.pose.z + extentZ) / CellSize);
                for (int x = minCellX; x <= maxCellX; ++x)
                {
                    for (int z = minCellZ; z <= maxCellZ; ++z)
                    {
                        CellKey key = new CellKey(x, z);
                        if (!_dynamicBlockerGrid.TryGetValue(key, out List<long> list))
                        {
                            list = new List<long>(4);
                            _dynamicBlockerGrid.Add(key, list);
                        }
                        list.Add(blocker.stableId);
                    }
                }
            }

            private void QueryDynamicBlockers(float minX, float minZ, float maxX, float maxZ)
            {
                _dynamicBlockerScratch.Clear();
                int minCellX = Mathf.FloorToInt(minX / CellSize);
                int maxCellX = Mathf.FloorToInt(maxX / CellSize);
                int minCellZ = Mathf.FloorToInt(minZ / CellSize);
                int maxCellZ = Mathf.FloorToInt(maxZ / CellSize);
                for (int x = minCellX; x <= maxCellX; ++x)
                {
                    for (int z = minCellZ; z <= maxCellZ; ++z)
                    {
                        if (!_dynamicBlockerGrid.TryGetValue(new CellKey(x, z), out List<long> list))
                            continue;
                        for (int i = 0; i < list.Count; ++i)
                            _dynamicBlockerScratch.Add(list[i]);
                    }
                }
            }

            private static bool SegmentIntersectsOrientedBox(
                Vector3 from,
                Vector3 to,
                ServerDynamicBlocker box)
            {
                float yaw = -box.pose.yaw * Mathf.Deg2Rad;
                float cos = Mathf.Cos(yaw);
                float sin = Mathf.Sin(yaw);

                static Vector3 ToLocal(Vector3 p, ServerDynamicBlocker b, float c, float si)
                {
                    float dx = p.x - b.pose.x;
                    float dz = p.z - b.pose.z;
                    return new Vector3(
                        dx * c - dz * si,
                        p.y - b.pose.y,
                        dx * si + dz * c);
                }

                Vector3 f = ToLocal(from, box, cos, sin);
                Vector3 t = ToLocal(to, box, cos, sin);
                Vector3 d = t - f;
                float hx = Mathf.Max(0.01f, box.sizeX * 0.5f);
                float hy = Mathf.Max(0.01f, box.sizeY * 0.5f);
                float hz = Mathf.Max(0.01f, box.sizeZ * 0.5f);
                float enter = 0f;
                float exit = 1f;

                if (!Slab(f.x, d.x, -hx, hx, ref enter, ref exit)) return false;
                if (!Slab(f.y, d.y, -hy, hy, ref enter, ref exit)) return false;
                return Slab(f.z, d.z, -hz, hz, ref enter, ref exit);
            }

            private static bool Slab(
                float origin,
                float direction,
                float min,
                float max,
                ref float enter,
                ref float exit)
            {
                if (Mathf.Abs(direction) < 0.0000001f)
                    return origin >= min && origin <= max;
                float inv = 1f / direction;
                float a = (min - origin) * inv;
                float b = (max - origin) * inv;
                if (a > b)
                {
                    float tmp = a;
                    a = b;
                    b = tmp;
                }
                enter = Mathf.Max(enter, a);
                exit = Mathf.Min(exit, b);
                return enter <= exit;
            }

            private static void BuildGrid(
                ServerCollisionTriangle[] triangles,
                Dictionary<CellKey, List<int>> grid)
            {
                for (int i = 0; i < triangles.Length; ++i)
                {
                    ServerCollisionTriangle t = triangles[i];
                    float minX = Mathf.Min(t.ax, Mathf.Min(t.bx, t.cx));
                    float maxX = Mathf.Max(t.ax, Mathf.Max(t.bx, t.cx));
                    float minZ = Mathf.Min(t.az, Mathf.Min(t.bz, t.cz));
                    float maxZ = Mathf.Max(t.az, Mathf.Max(t.bz, t.cz));
                    int minCellX = Mathf.FloorToInt(minX / CellSize);
                    int maxCellX = Mathf.FloorToInt(maxX / CellSize);
                    int minCellZ = Mathf.FloorToInt(minZ / CellSize);
                    int maxCellZ = Mathf.FloorToInt(maxZ / CellSize);

                    for (int x = minCellX; x <= maxCellX; ++x)
                    {
                        for (int z = minCellZ; z <= maxCellZ; ++z)
                        {
                            CellKey key = new CellKey(x, z);
                            if (!grid.TryGetValue(key, out List<int> list))
                            {
                                list = new List<int>(8);
                                grid.Add(key, list);
                            }
                            list.Add(i);
                        }
                    }
                }
            }

            private void Query(
                Dictionary<CellKey, List<int>> grid,
                float minX,
                float minZ,
                float maxX,
                float maxZ)
            {
                _scratch.Clear();
                int minCellX = Mathf.FloorToInt(minX / CellSize);
                int maxCellX = Mathf.FloorToInt(maxX / CellSize);
                int minCellZ = Mathf.FloorToInt(minZ / CellSize);
                int maxCellZ = Mathf.FloorToInt(maxZ / CellSize);
                for (int x = minCellX; x <= maxCellX; ++x)
                {
                    for (int z = minCellZ; z <= maxCellZ; ++z)
                    {
                        if (!grid.TryGetValue(new CellKey(x, z), out List<int> list))
                            continue;
                        for (int i = 0; i < list.Count; ++i)
                            _scratch.Add(list[i]);
                    }
                }
            }
        }

        private static RuntimeWorld _world;
        private static string _loadAttemptScene = string.Empty;

        public static bool IsLoaded => _world != null;
        public static string LoadedMapId => _world?.Snapshot?.mapId ?? string.Empty;
        public static string LoadedHash => _world?.Snapshot?.contentHash ?? string.Empty;

        public static bool TrySetDynamicBlockerEnabled(long stableId, bool enabled)
        {
            return EnsureLoaded() && _world.TrySetDynamicBlockerEnabled(stableId, enabled);
        }

        public static bool TryResolveGroundedMove(
            Vector3 start,
            Vector3 desiredDelta,
            float radius,
            float maximumStepUp,
            float maximumSnapDown,
            out Vector3 resolved,
            out bool blocked)
        {
            resolved = start + desiredDelta;
            blocked = false;
            if (!EnsureLoaded())
                return false;

            return _world.TryResolveGroundedMove(
                start,
                desiredDelta,
                radius,
                maximumStepUp,
                maximumSnapDown,
                out resolved,
                out blocked);
        }

        /// <summary>
        /// Resolves the precise baked support height at an X/Z position without advancing
        /// movement. Owner presentation uses this as a zero-wire grounded-height fallback
        /// when the local Unity physics probe is unavailable or intentionally disabled.
        /// </summary>
        public static bool TryFindSupportHeight(
            float x,
            float z,
            float referenceY,
            float probeUp,
            float probeDown,
            out float y)
        {
            y = referenceY;
            if (!EnsureLoaded())
                return false;

            return _world.TryFindSupportHeight(
                x,
                z,
                referenceY,
                probeUp,
                probeDown,
                out y);
        }

        private static bool EnsureLoaded()
        {
            if (_world != null)
                return true;

            Scene scene = SceneManager.GetActiveScene();
            string sceneName = scene.IsValid() ? scene.name : string.Empty;
            if (string.Equals(_loadAttemptScene, sceneName, StringComparison.Ordinal))
                return false;
            _loadAttemptScene = sceneName;

            try
            {
                string folder = Path.Combine(Application.streamingAssetsPath, "MMOWorld");
                if (!Directory.Exists(folder))
                    return false;

                string mapId = ServerMapId.Normalize(sceneName);
                string preferred = Path.Combine(folder, mapId + ".sharedworld.json");
                string path = File.Exists(preferred) ? preferred : FindFallback(folder, mapId);
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                SharedWorldSnapshot snapshot = JsonUtility.FromJson<SharedWorldSnapshot>(File.ReadAllText(path));
                if (snapshot == null || snapshot.formatVersion != SharedWorldFormat.Version ||
                    string.IsNullOrWhiteSpace(snapshot.mapId))
                {
                    Debug.LogError($"[Shared World] Invalid client world package: {path}");
                    return false;
                }

                _world = new RuntimeWorld(snapshot);
                Debug.Log(
                    $"[Shared World] Loaded '{snapshot.mapId}' revision={snapshot.bakeRevision}, " +
                    $"support={snapshot.supportTriangles?.Length ?? 0:n0}, edges={snapshot.surfaceEdges?.Length ?? 0:n0}, " +
                    $"collision={snapshot.collisionTriangles?.Length ?? 0:n0}, interactables={snapshot.interactables?.Length ?? 0:n0}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Shared World] Client package load failed: {ex.Message}");
                return false;
            }
        }

        private static string FindFallback(string folder, string mapId)
        {
            string[] files = Directory.GetFiles(folder, "*.sharedworld.json", SearchOption.TopDirectoryOnly);
            if (files.Length == 1)
                return files[0];

            for (int i = 0; i < files.Length; ++i)
            {
                string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(files[i]));
                if (name.StartsWith(mapId, StringComparison.OrdinalIgnoreCase))
                    return files[i];
            }
            return string.Empty;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _world = null;
            _loadAttemptScene = string.Empty;
        }
    }
}
