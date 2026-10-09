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
    [InitializeOnLoad]
    internal static class PopulationRouteWalkabilityGizmos
    {
        private sealed class CachedWorld
        {
            public long WriteTicks;
            public double NextFileCheckAt;
            public ServerCollisionWorld Collision;
        }

        private readonly struct SegmentKey : IEquatable<SegmentKey>
        {
            public readonly int SceneHandle;
            public readonly long Revision;
            public readonly int Fx, Fy, Fz;
            public readonly int Tx, Ty, Tz;

            public SegmentKey(int sceneHandle, long revision, Vector3 from, Vector3 to)
            {
                SceneHandle = sceneHandle;
                Revision = revision;
                Fx = Quantize(from.x);
                Fy = Quantize(from.y);
                Fz = Quantize(from.z);
                Tx = Quantize(to.x);
                Ty = Quantize(to.y);
                Tz = Quantize(to.z);
            }

            private static int Quantize(float value) => Mathf.RoundToInt(value * 1000f);

            public bool Equals(SegmentKey other) =>
                SceneHandle == other.SceneHandle &&
                Revision == other.Revision &&
                Fx == other.Fx && Fy == other.Fy && Fz == other.Fz &&
                Tx == other.Tx && Ty == other.Ty && Tz == other.Tz;

            public override bool Equals(object obj) => obj is SegmentKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = SceneHandle;
                    hash = (hash * 397) ^ Revision.GetHashCode();
                    hash = (hash * 397) ^ Fx;
                    hash = (hash * 397) ^ Fy;
                    hash = (hash * 397) ^ Fz;
                    hash = (hash * 397) ^ Tx;
                    hash = (hash * 397) ^ Ty;
                    hash = (hash * 397) ^ Tz;
                    return hash;
                }
            }
        }

        private static readonly Dictionary<string, CachedWorld> WorldCache =
            new Dictionary<string, CachedWorld>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, ServerMap> MapCache =
            new Dictionary<int, ServerMap>();
        private static readonly Dictionary<SegmentKey, bool> SegmentCache =
            new Dictionary<SegmentKey, bool>(4096);

        private const int MaximumCachedSegments = 16384;

        private static readonly Color ValidColor = new Color(0.15f, 0.9f, 0.3f, 0.9f);
        private static readonly Color InvalidColor = new Color(0.95f, 0.15f, 0.1f, 0.95f);

        static PopulationRouteWalkabilityGizmos()
        {
            EditorApplication.projectChanged += CacheClear;
            AssemblyReloadEvents.beforeAssemblyReload += CacheClear;
        }

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
                    DrawValidated(marker.gameObject.scene, marker.transform.position, linked.transform.position, false);
                }
            }

            if (marker.oneWayOutboundLinks != null)
            {
                for (int i = 0; i < marker.oneWayOutboundLinks.Count; ++i)
                {
                    PopulationRouteMarker linked = marker.oneWayOutboundLinks[i];
                    if (!ValidLink(marker, linked))
                        continue;
                    DrawValidated(marker.gameObject.scene, marker.transform.position, linked.transform.position, true);
                }
            }
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active | GizmoType.Pickable)]
        private static void DrawPath(PopulationRoutePath path, GizmoType gizmoType)
        {
            if (path == null || !path.gameObject.activeInHierarchy || !path.connectChildrenInOrder)
                return;

            PopulationRouteMarker first = null;
            PopulationRouteMarker previous = null;
            for (int i = 0; i < path.transform.childCount; ++i)
            {
                Transform child = path.transform.GetChild(i);
                if (child == null || !child.TryGetComponent(out PopulationRouteMarker current))
                    continue;

                if (first == null)
                    first = current;
                if (previous != null)
                    DrawValidated(path.gameObject.scene, previous.transform.position, current.transform.position, path.oneWay);
                previous = current;
            }

            if (path.loop && first != null && previous != null && first != previous)
                DrawValidated(path.gameObject.scene, previous.transform.position, first.transform.position, path.oneWay);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active | GizmoType.Pickable)]
        private static void DrawPortal(PopulationPortal portal, GizmoType gizmoType)
        {
            if (portal == null || !portal.gameObject.activeInHierarchy || portal.routeMarker == null)
                return;

            DrawValidated(
                portal.gameObject.scene,
                portal.EffectiveExterior.position,
                portal.routeMarker.transform.position,
                false);
        }

        private static void DrawValidated(Scene scene, Vector3 from, Vector3 to, bool oneWay)
        {
            bool valid = false;
            if (TryGetWorld(scene, out ServerCollisionWorld world, out long revision))
            {
                var key = new SegmentKey(scene.handle, revision, from, to);
                if (!SegmentCache.TryGetValue(key, out valid))
                {
                    valid = SegmentTraversable(world, scene, from, to);
                    if (SegmentCache.Count >= MaximumCachedSegments)
                        SegmentCache.Clear();
                    SegmentCache[key] = valid;
                }
            }

            Gizmos.color = valid ? ValidColor : InvalidColor;
            Gizmos.DrawLine(from, to);

            if (oneWay)
                DrawArrow(from, to);
        }

        private static bool SegmentTraversable(
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

            // Resolve the starting point onto authoritative baked support first. After that,
            // walk the segment incrementally using the previous resolved Y exactly like the
            // runtime grounded motor, instead of interpolating authored endpoint heights.
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

            if (!world.IsStandingCapsuleClear(
                    startGround.Position,
                    capsule,
                    slope,
                    stepHeight))
            {
                return false;
            }

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

                if (!world.IsStandingCapsuleClear(
                        ground.Position,
                        capsule,
                        slope,
                        stepHeight))
                {
                    return false;
                }

                previousY = ground.Position.Y;
            }

            return true;
        }

        private static bool TryGetWorld(
            Scene scene,
            out ServerCollisionWorld world,
            out long revision)
        {
            world = null;
            revision = 0L;

            ServerMap map = FindMap(scene);
            if (map == null || string.IsNullOrWhiteSpace(map.MapId))
                return false;

            string suffix = string.IsNullOrWhiteSpace(map.InstanceId)
                ? string.Empty
                : "__" + ServerMap.NormalizeId(map.InstanceId);
            string path = Path.Combine(
                ServerWorldBake.ServerMapsFolder,
                ServerMap.NormalizeId(map.MapId) + suffix + ".servermap.json");

            double now = EditorApplication.timeSinceStartup;
            if (WorldCache.TryGetValue(path, out CachedWorld cached) &&
                cached != null &&
                cached.Collision != null &&
                now < cached.NextFileCheckAt)
            {
                world = cached.Collision;
                revision = cached.WriteTicks;
                return true;
            }

            if (!File.Exists(path))
                return false;

            long ticks = File.GetLastWriteTimeUtc(path).Ticks;
            if (cached != null &&
                cached.Collision != null &&
                cached.WriteTicks == ticks)
            {
                cached.NextFileCheckAt = now + 1.0d;
                world = cached.Collision;
                revision = cached.WriteTicks;
                return true;
            }

            try
            {
                ServerMapSnapshot snapshot = JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(path));
                if (snapshot == null)
                    return false;

                world = new ServerCollisionWorld(snapshot);
                WorldCache[path] = new CachedWorld
                {
                    WriteTicks = ticks,
                    NextFileCheckAt = now + 1.0d,
                    Collision = world,
                };

                // A new baked map invalidates all segment answers. They are repopulated lazily
                // only when Unity actually asks to draw that connection.
                SegmentCache.Clear();
                revision = ticks;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ServerMap FindMap(Scene scene)
        {
            if (MapCache.TryGetValue(scene.handle, out ServerMap cached) &&
                cached != null &&
                cached.gameObject.scene == scene)
            {
                return cached;
            }

            ServerMap[] maps = UnityEngine.Object.FindObjectsByType<ServerMap>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < maps.Length; ++i)
            {
                ServerMap map = maps[i];
                if (map != null && map.gameObject.scene == scene)
                {
                    MapCache[scene.handle] = map;
                    return map;
                }
            }

            MapCache.Remove(scene.handle);
            return null;
        }

        private static bool ValidLink(PopulationRouteMarker from, PopulationRouteMarker to) =>
            from != null && to != null && to != from && to.gameObject.scene == from.gameObject.scene;

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

        private static void CacheClear()
        {
            WorldCache.Clear();
            MapCache.Clear();
            SegmentCache.Clear();
        }
    }
}
