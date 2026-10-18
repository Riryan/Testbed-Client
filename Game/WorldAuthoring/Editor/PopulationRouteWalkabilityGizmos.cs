using System;
using System.Collections.Generic;
using System.IO;
using Game.Server.Application.World;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    internal static class PopulationRouteWalkabilityGizmos
    {
        private readonly struct SegmentKey : IEquatable<SegmentKey>
        {
            public readonly int SceneHandle;
            public readonly int Ax, Ay, Az;
            public readonly int Bx, By, Bz;
            public readonly int Width;

            public SegmentKey(int sceneHandle, Vector3 a, Vector3 b, float width)
            {
                SceneHandle = sceneHandle;
                Width = Quantize(width);

                int ax = Quantize(a.x);
                int ay = Quantize(a.y);
                int az = Quantize(a.z);
                int bx = Quantize(b.x);
                int by = Quantize(b.y);
                int bz = Quantize(b.z);

                // Canonicalize endpoint order so bidirectional/path/portal renderers can share
                // one result without revalidating the same physical segment.
                bool swap =
                    ax > bx ||
                    (ax == bx && ay > by) ||
                    (ax == bx && ay == by && az > bz);

                if (!swap)
                {
                    Ax = ax; Ay = ay; Az = az;
                    Bx = bx; By = by; Bz = bz;
                }
                else
                {
                    Ax = bx; Ay = by; Az = bz;
                    Bx = ax; By = ay; Bz = az;
                }
            }

            private static int Quantize(float value) => Mathf.RoundToInt(value * 1000f);

            public bool Equals(SegmentKey other) =>
                SceneHandle == other.SceneHandle &&
                Width == other.Width &&
                Ax == other.Ax && Ay == other.Ay && Az == other.Az &&
                Bx == other.Bx && By == other.By && Bz == other.Bz;

            public override bool Equals(object obj) => obj is SegmentKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = SceneHandle;
                    hash = (hash * 397) ^ Width;
                    hash = (hash * 397) ^ Ax;
                    hash = (hash * 397) ^ Ay;
                    hash = (hash * 397) ^ Az;
                    hash = (hash * 397) ^ Bx;
                    hash = (hash * 397) ^ By;
                    hash = (hash * 397) ^ Bz;
                    return hash;
                }
            }
        }

        private static readonly Dictionary<SegmentKey, bool> Results =
            new Dictionary<SegmentKey, bool>(4096);
        private static readonly HashSet<SegmentKey> Seen =
            new HashSet<SegmentKey>();
        private static readonly Dictionary<int, (Vector3 Position, bool Valid)> MarkerResults =
            new Dictionary<int, (Vector3 Position, bool Valid)>();
        private static ServerCollisionWorld _selectedWorld;
        private static int _selectedSceneHandle = int.MinValue;
        private static DateTime _selectedMapStamp;
        private static readonly Color UnknownColor = new Color(0.55f, 0.55f, 0.55f, 0.85f);

        private static readonly Color ValidColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
        private static readonly Color InvalidColor = new Color(0.95f, 0.15f, 0.1f, 0.95f);

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active | GizmoType.Pickable)]
        private static void DrawMarker(PopulationRouteMarker marker, GizmoType gizmoType)
        {
            if (marker == null || !marker.gameObject.activeInHierarchy)
                return;

            // Same red/green placement contract as the spawner, but using the
            // exact baked static collision world. Selected markers revalidate on
            // movement; unselected markers reuse explicit Validate Scene results.
            // Only the selected marker may refresh baked world data.
            bool selected = (gizmoType & GizmoType.Selected) != 0;
            ServerCollisionWorld selectedWorld = null;
            bool worldReady = selected && TryGetSelectedWorld(marker.gameObject.scene, out selectedWorld);
            int markerId = marker.GetInstanceID();
            Vector3 position = marker.transform.position;
            bool hasResult = MarkerResults.TryGetValue(markerId, out var markerResult) &&
                             markerResult.Position == position;
            if (worldReady && !hasResult)
            {
                bool valid = MarkerIsGrounded(selectedWorld, marker.gameObject.scene, position);
                MarkerResults[markerId] = (position, valid);
                markerResult = (position, valid);
                hasResult = true;
            }
            Gizmos.color = !hasResult ? UnknownColor : markerResult.Valid ? ValidColor : InvalidColor;
            Gizmos.DrawSphere(position, 0.23f);
            if (marker.branchNode)
            {
                Gizmos.color = new Color(1f, 0.75f, 0.1f, 1f);
                Gizmos.DrawWireSphere(position, 0.31f);
            }

            if (marker.links != null)
            {
                for (int i = 0; i < marker.links.Count; ++i)
                {
                    PopulationRouteMarker linked = marker.links[i];
                    if (!ValidLink(marker, linked) || OneWayOverride(marker, linked))
                        continue;

                    DrawCached(
                        marker.gameObject.scene,
                        marker.transform.position,
                        linked.transform.position,
                        false,
                        Mathf.Max(0.5f, (marker.pathWidth + linked.pathWidth) * 0.5f));
                }
            }

            if (marker.oneWayOutboundLinks != null)
            {
                for (int i = 0; i < marker.oneWayOutboundLinks.Count; ++i)
                {
                    PopulationRouteMarker linked = marker.oneWayOutboundLinks[i];
                    if (!ValidLink(marker, linked))
                        continue;

                    DrawCached(
                        marker.gameObject.scene,
                        marker.transform.position,
                        linked.transform.position,
                        true,
                        Mathf.Max(0.5f, (marker.pathWidth + linked.pathWidth) * 0.5f));
                }
            }
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active | GizmoType.Pickable)]
        private static void DrawPortal(PopulationPortal portal, GizmoType gizmoType)
        {
            if (portal == null || !portal.gameObject.activeInHierarchy || portal.routeMarker == null)
                return;

            DrawCached(
                portal.gameObject.scene,
                portal.EffectiveExterior.position,
                portal.routeMarker.transform.position,
                false,
                Mathf.Max(0.5f, portal.routeMarker.pathWidth));
        }

        private static void DrawCached(Scene scene, Vector3 from, Vector3 to,
            bool oneWay, float width)
        {
            var key = new SegmentKey(scene.handle, from, to, width);
            bool known = Results.TryGetValue(key, out bool valid);
            Gizmos.color = !known ? UnknownColor : valid ? ValidColor : InvalidColor;
            Gizmos.DrawLine(from, to);

            if (oneWay)
                DrawArrow(from, to);
        }

        public static bool ValidateActiveScene(out int validCount, out int invalidCount)
        {
            return ValidateScene(SceneManager.GetActiveScene(), out validCount, out invalidCount);
        }

        public static bool ValidateScene(Scene scene, out int validCount, out int invalidCount)
        {
            validCount = 0;
            invalidCount = 0;

            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogWarning("[Population Route Validation] Active scene is not loaded.");
                return false;
            }

            if (!TryLoadWorld(scene, out ServerCollisionWorld world, out string detail))
            {
                // An absent/stale bake must never leave old results showing green.
                Results.Clear();
                MarkerResults.Clear();
                _selectedWorld = null;
                _selectedSceneHandle = int.MinValue;
                Debug.LogError($"[Population Route Validation] {detail}");
                return false;
            }

            // Validation is deliberately explicit and bounded. DrawGizmos never performs
            // ServerCollisionWorld queries.
            RemoveSceneResults(scene.handle);
            Seen.Clear();
            _selectedWorld = world;
            _selectedSceneHandle = scene.handle;
            ServerMap selectedMap = FindMap(scene);
            if (selectedMap != null)
            {
                string suffix = string.IsNullOrWhiteSpace(selectedMap.InstanceId) ? string.Empty :
                    "__" + ServerMap.NormalizeId(selectedMap.InstanceId);
                string path = Path.Combine(ServerWorldBake.ServerMapsFolder,
                    ServerMap.NormalizeId(selectedMap.MapId) + suffix + ".servermap.json");
                _selectedMapStamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            }

            PopulationRouteMarker[] markers = UnityEngine.Object.FindObjectsByType<PopulationRouteMarker>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                if (marker == null || marker.gameObject.scene != scene || !marker.gameObject.activeInHierarchy)
                    continue;

                MarkerResults[marker.GetInstanceID()] =
                    (marker.transform.position,
                     MarkerIsGrounded(world, scene, marker.transform.position));

                if (marker.links != null)
                {
                    for (int l = 0; l < marker.links.Count; ++l)
                    {
                        PopulationRouteMarker linked = marker.links[l];
                        if (!ValidLink(marker, linked) || OneWayOverride(marker, linked))
                            continue;

                        ValidateSegment(
                            scene,
                            world,
                            marker.transform.position,
                            linked.transform.position,
                            Mathf.Max(0.5f, (marker.pathWidth + linked.pathWidth) * 0.5f),
                            ref validCount,
                            ref invalidCount);
                    }
                }

                if (marker.oneWayOutboundLinks != null)
                {
                    for (int l = 0; l < marker.oneWayOutboundLinks.Count; ++l)
                    {
                        PopulationRouteMarker linked = marker.oneWayOutboundLinks[l];
                        if (!ValidLink(marker, linked))
                            continue;

                        ValidateSegment(
                            scene,
                            world,
                            marker.transform.position,
                            linked.transform.position,
                            Mathf.Max(0.5f, (marker.pathWidth + linked.pathWidth) * 0.5f),
                            ref validCount,
                            ref invalidCount);
                    }
                }
            }

            PopulationPortal[] portals = UnityEngine.Object.FindObjectsByType<PopulationPortal>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < portals.Length; ++i)
            {
                PopulationPortal portal = portals[i];
                if (portal == null ||
                    portal.gameObject.scene != scene ||
                    !portal.gameObject.activeInHierarchy ||
                    portal.routeMarker == null)
                {
                    continue;
                }

                ValidateSegment(
                    scene,
                    world,
                    portal.EffectiveExterior.position,
                    portal.routeMarker.transform.position,
                    Mathf.Max(0.5f, portal.routeMarker.pathWidth),
                    ref validCount,
                    ref invalidCount);
            }

            SceneView.RepaintAll();
            Debug.Log(
                $"[Population Route Validation] scene='{scene.path}' " +
                $"segments={validCount + invalidCount}, valid={validCount}, invalid={invalidCount}.");

            return true;
        }

        private static void ValidateSegment(
            Scene scene,
            ServerCollisionWorld world,
            Vector3 from,
            Vector3 to,
            float width,
            ref int validCount,
            ref int invalidCount)
        {
            var key = new SegmentKey(scene.handle, from, to, width);
            if (!Seen.Add(key))
                return;

            bool valid = SegmentTraversable(world, scene, from, to, width);
            Results[key] = valid;
            if (valid)
                validCount++;
            else
                invalidCount++;
        }

        // Validate the THREE exact lanes Population actually uses, and the
        // interpolated ground-height profile (including stairs). This remains
        // editor/bake-time work, never per-actor runtime movement.
        internal static bool SegmentTraversable(
            ServerCollisionWorld world, Scene scene, Vector3 from, Vector3 to,
            float width = 2.5f)
        {
            ServerMap map = FindMap(scene);
            float radius = map != null ? Mathf.Max(0.20f, map.NavAgentRadius) : 0.35f;
            float height = map != null ? Mathf.Max(radius * 2f, map.NavAgentHeight) : 1.8f;
            var capsule = new ServerCapsule(radius, height);
            for (int lane = -1; lane <= 1; ++lane)
                if (!LaneClear(world, from, to, lane * Mathf.Max(0f, width * 0.35f),
                        capsule, 50f))
                    return false;
            return true;
        }

        private static bool MarkerIsGrounded(ServerCollisionWorld world, Scene scene, Vector3 pos)
        {
            ServerMap map = FindMap(scene);
            float radius = map != null ? Mathf.Max(0.20f, map.NavAgentRadius) : 0.35f;
            float height = map != null ? Mathf.Max(radius * 2f, map.NavAgentHeight) : 1.8f;
            var point = new WorldPosition(pos.x, pos.y, pos.z);
            return world != null &&
                world.TryFindGround(point, radius, 0.18f, 0.18f, 50f,
                    out ServerGroundHit ground) &&
                Mathf.Abs(ground.Position.Y - pos.y) <= 0.10f &&
                world.IsStandingCapsuleClear(ground.Position,
                    new ServerCapsule(radius, height), 50f);
        }

        private static bool LaneClear(ServerCollisionWorld world,
            Vector3 from, Vector3 to, float laneOffset,
            ServerCapsule capsule, float slope)
        {
            if (world == null) return false;
            float dx = to.x - from.x, dz = to.z - from.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            if ((float.IsNaN(distance) || float.IsInfinity(distance)) || distance <= 0.05f) return false;
            int samples = Mathf.CeilToInt(distance /
                Mathf.Max(0.05f, Mathf.Min(0.15f, capsule.Radius * 0.4f)));
            if (samples <= 0 || samples > 1024) return false;
            float sideX = -dz / distance, sideZ = dx / distance;
            float previousY = 0f;
            for (int i = 0; i <= samples; ++i)
            {
                float t = i / (float)samples;
                var expected = new WorldPosition(from.x + dx * t + sideX * laneOffset,
                    Mathf.Lerp(from.y, to.y, t),
                    from.z + dz * t + sideZ * laneOffset);
                if (!world.TryFindGround(expected, capsule.Radius, 0.18f, 0.18f,
                        slope, out ServerGroundHit ground) ||
                    Mathf.Abs(ground.Position.Y - expected.Y) > 0.10f ||
                    !world.IsStandingCapsuleClear(ground.Position, capsule, slope))
                    return false;
                if (i > 0 && Mathf.Abs(ground.Position.Y - previousY) > 0.22f)
                    return false;
                previousY = ground.Position.Y;
            }
            return true;
        }

        // Existing loader, shared by selected marker previews and the explicit
        // Validate Scene operation; no parallel world validation infrastructure.
        private static bool TryGetSelectedWorld(Scene scene, out ServerCollisionWorld world)
        {
            world = null;
            ServerMap map = FindMap(scene);
            if (map == null || string.IsNullOrWhiteSpace(map.MapId)) return false;
            string suffix = string.IsNullOrWhiteSpace(map.InstanceId) ? string.Empty :
                "__" + ServerMap.NormalizeId(map.InstanceId);
            string path = Path.Combine(ServerWorldBake.ServerMapsFolder,
                ServerMap.NormalizeId(map.MapId) + suffix + ".servermap.json");
            if (!File.Exists(path)) return false;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (_selectedWorld != null && _selectedSceneHandle == scene.handle &&
                _selectedMapStamp == stamp)
            {
                world = _selectedWorld;
                return true;
            }
            if (!TryLoadWorld(scene, out world, out _)) return false;
            MarkerResults.Clear();
            Results.Clear();
            _selectedWorld = world;
            _selectedSceneHandle = scene.handle;
            _selectedMapStamp = stamp;
            return true;
        }

        private static bool TryLoadWorld(
            Scene scene,
            out ServerCollisionWorld world,
            out string detail)
        {
            world = null;
            detail = string.Empty;

            ServerMap map = FindMap(scene);
            if (map == null || string.IsNullOrWhiteSpace(map.MapId))
            {
                detail = "Scene has no valid ServerMap.";
                return false;
            }

            string suffix = string.IsNullOrWhiteSpace(map.InstanceId)
                ? string.Empty
                : "__" + ServerMap.NormalizeId(map.InstanceId);

            string path = Path.Combine(
                ServerWorldBake.ServerMapsFolder,
                ServerMap.NormalizeId(map.MapId) + suffix + ".servermap.json");

            if (!File.Exists(path))
            {
                detail = $"No baked server map exists at '{path}'. Bake Current Scene first.";
                return false;
            }

            try
            {
                ServerMapSnapshot snapshot = JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(path));
                if (snapshot == null)
                {
                    detail = $"Could not deserialize baked server map '{path}'.";
                    return false;
                }

                if (snapshot.formatVersion != ServerMapFormat.Version)
                {
                    detail =
                        $"Baked server map format is V{snapshot.formatVersion}; " +
                        $"current code requires V{ServerMapFormat.Version}. Bake Current Scene again.";
                    return false;
                }

                world = new ServerCollisionWorld(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                detail = $"Could not load baked server map '{path}': {ex.Message}";
                return false;
            }
        }

        private static ServerMap FindMap(Scene scene)
        {
            ServerMap[] maps = UnityEngine.Object.FindObjectsByType<ServerMap>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < maps.Length; ++i)
            {
                ServerMap map = maps[i];
                if (map != null && map.gameObject.scene == scene)
                    return map;
            }

            return null;
        }

        private static void RemoveSceneResults(int sceneHandle)
        {
            MarkerResults.Clear();
            if (Results.Count == 0)
                return;
            var remove = new List<SegmentKey>();
            foreach (KeyValuePair<SegmentKey, bool> pair in Results)
            {
                if (pair.Key.SceneHandle == sceneHandle)
                    remove.Add(pair.Key);
            }

            for (int i = 0; i < remove.Count; ++i)
                Results.Remove(remove[i]);
        }

        private static bool ValidLink(PopulationRouteMarker from, PopulationRouteMarker to) =>
            from != null &&
            to != null &&
            to != from &&
            to.gameObject.scene == from.gameObject.scene;

        private static bool OneWayOverride(PopulationRouteMarker from, PopulationRouteMarker to)
        {
            bool outbound = from.oneWayOutboundLinks != null && from.oneWayOutboundLinks.Contains(to);
            bool inbound = to.oneWayOutboundLinks != null && to.oneWayOutboundLinks.Contains(from);
            return outbound || inbound;
        }

        private static void DrawArrow(Vector3 from, Vector3 to)
        {
            Vector3 direction = to - from;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
                return;

            direction.Normalize();
            Vector3 center = Vector3.Lerp(from, to, 0.72f);
            Vector3 side = Vector3.Cross(Vector3.up, direction).normalized;
            float length = Mathf.Min(0.65f, Vector3.Distance(from, to) * 0.12f);
            Vector3 back = center - direction * length;
            Gizmos.DrawLine(center, back + side * length * 0.45f);
            Gizmos.DrawLine(center, back - side * length * 0.45f);
        }
    }
}
