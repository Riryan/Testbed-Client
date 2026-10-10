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

            public SegmentKey(int sceneHandle, Vector3 a, Vector3 b)
            {
                SceneHandle = sceneHandle;

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
                Ax == other.Ax && Ay == other.Ay && Az == other.Az &&
                Bx == other.Bx && By == other.By && Bz == other.Bz;

            public override bool Equals(object obj) => obj is SegmentKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = SceneHandle;
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

        private static readonly Color ValidColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
        private static readonly Color InvalidColor = new Color(0.95f, 0.15f, 0.1f, 0.95f);

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active | GizmoType.Pickable)]
        private static void DrawMarker(PopulationRouteMarker marker, GizmoType gizmoType)
        {
            if (marker == null || !marker.gameObject.activeInHierarchy)
                return;

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
                        false);
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
                        true);
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
                false);
        }

        private static void DrawCached(Scene scene, Vector3 from, Vector3 to, bool oneWay)
        {
            var key = new SegmentKey(scene.handle, from, to);
            bool valid = Results.TryGetValue(key, out bool cached) && cached;

            Gizmos.color = valid ? ValidColor : InvalidColor;
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
                Debug.LogError($"[Population Route Validation] {detail}");
                return false;
            }

            // Validation is deliberately explicit and bounded. DrawGizmos never performs
            // ServerCollisionWorld queries.
            RemoveSceneResults(scene.handle);
            Seen.Clear();

            PopulationRouteMarker[] markers = UnityEngine.Object.FindObjectsByType<PopulationRouteMarker>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                if (marker == null || marker.gameObject.scene != scene || !marker.gameObject.activeInHierarchy)
                    continue;

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
            ref int validCount,
            ref int invalidCount)
        {
            var key = new SegmentKey(scene.handle, from, to);
            if (!Seen.Add(key))
                return;

            bool valid = SegmentTraversable(world, scene, from, to);
            Results[key] = valid;
            if (valid)
                validCount++;
            else
                invalidCount++;
        }

        internal static bool SegmentTraversable(
            ServerCollisionWorld world,
            Scene scene,
            Vector3 from,
            Vector3 to)
        {
            ServerMap map = FindMap(scene);
            float radius = map != null ? map.NavAgentRadius : 0.35f;
            float height = map != null ? map.NavAgentHeight : 1.8f;
            float stepHeight = map != null ? map.NavAgentMaxClimb : 0.4f;
            float slope = map != null ? map.NavAgentMaxSlope : 50f;
            float groundSnap = 0.5f;

            var capsule = new ServerCapsule(radius, height);
            var authoredStart = new WorldPosition(from.x, from.y, from.z);

            if (!world.TryFindGround(
                    authoredStart,
                    radius,
                    1.5f,
                    1.5f,
                    slope,
                    out ServerGroundHit startGround))
            {
                return false;
            }

            if (!world.IsStandingCapsuleClear(startGround.Position, capsule, slope))
                return false;

            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float horizontal = Mathf.Sqrt(dx * dx + dz * dz);
            if (horizontal <= 0.001f)
                return true;

            int samples = Mathf.Clamp(
                Mathf.CeilToInt(horizontal / Mathf.Max(0.10f, radius * 0.45f)),
                1,
                512);

            float previousY = startGround.Position.Y;
            for (int i = 1; i <= samples; ++i)
            {
                float t = i / (float)samples;
                float x = Mathf.Lerp(from.x, to.x, t);
                float z = Mathf.Lerp(from.z, to.z, t);
                var probe = new WorldPosition(x, previousY, z);

                if (!world.TryFindGround(
                        probe,
                        radius,
                        Mathf.Max(0.05f, stepHeight + 0.05f),
                        Mathf.Max(0.05f, groundSnap),
                        slope,
                        out ServerGroundHit ground))
                {
                    return false;
                }

                float dy = ground.Position.Y - previousY;
                if (dy > stepHeight + 0.05f || dy < -groundSnap - 0.05f)
                    return false;

                if (!world.IsStandingCapsuleClear(ground.Position, capsule, slope))
                    return false;

                previousY = ground.Position.Y;
            }

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
