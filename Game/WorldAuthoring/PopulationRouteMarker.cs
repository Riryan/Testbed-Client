using System;
using System.Collections.Generic;
using Game.Shared.Population;
using UnityEngine;
using UnityEngine.AI;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// Editor/runtime-authoring marker for the standalone Population route graph.
    /// It is baked into compact ServerPopulationRouteNode/Edge data and stripped from client builds.
    /// </summary>
    [AddComponentMenu("MMO/Population/Route Marker")]
    [DisallowMultipleComponent]
    public sealed class PopulationRouteMarker : MonoBehaviour
    {
        [SerializeField, HideInInspector] private string bakeId = string.Empty;

        [Header("Identity")]
        public string label = "Route Marker";

        [Header("Connections")]
        [Tooltip("Normal pedestrian connections. These bake as bidirectional edges, so only one endpoint needs to list the other.")]
        public List<PopulationRouteMarker> links = new List<PopulationRouteMarker>();

        [Tooltip("Explicit one-way travel FROM this marker TO the linked marker.")]
        public List<PopulationRouteMarker> oneWayOutboundLinks = new List<PopulationRouteMarker>();

        [Header("Route Shape")]
        [Min(0.5f)] public float pathWidth = 2.5f;
        [Min(0.01f)] public float routeWeight = 1f;
        public PopulationNodeType nodeType = PopulationNodeType.Regular;

        [Header("Destination Rules")]
        public bool isDestination;
        public PopulationDestinationTag destinationTags = PopulationDestinationTag.Sidewalk;
        public string destinationGroup = string.Empty;
        [Tooltip("Hard restricted nodes are excluded from ordinary Population wandering.")]
        public bool hardRestricted;
        public PopulationNpcTypeMask allowedNpcTypes = PopulationNpcTypeMask.All;

        [Header("Node Action")]
        [Min(0f)] public float minimumWaitSeconds;
        [Min(0f)] public float maximumWaitSeconds;
        [Range(0, 255)] public int actionId;

        public string BakeId => bakeId;

        /// <summary>
        /// Assigns a fresh authoring identity. Unity duplicates serialized private fields, so a
        /// duplicated Route Marker initially inherits the source marker's bakeId. ServerWorldBake
        /// calls this only when it detects that duplicated authoring identity.
        /// </summary>
        public void RegenerateBakeId()
        {
            bakeId = Guid.NewGuid().ToString("N");
        }

        private void Reset()
        {
            EnsureBakeId();
            label = gameObject.name;
        }

        private void OnValidate()
        {
            EnsureBakeId();
            label = string.IsNullOrWhiteSpace(label) ? gameObject.name : label.Trim();
            pathWidth = Mathf.Max(0.5f, pathWidth);
            routeWeight = Mathf.Max(0.01f, routeWeight);
            maximumWaitSeconds = Mathf.Max(minimumWaitSeconds, maximumWaitSeconds);
            destinationGroup = string.IsNullOrWhiteSpace(destinationGroup) ? string.Empty : destinationGroup.Trim();
            links ??= new List<PopulationRouteMarker>();
            oneWayOutboundLinks ??= new List<PopulationRouteMarker>();
        }

        private void EnsureBakeId()
        {
            if (string.IsNullOrWhiteSpace(bakeId))
                bakeId = Guid.NewGuid().ToString("N");
        }

        private void OnDrawGizmos()
        {
            float size = 0.18f;
            Gizmos.color = isDestination
                ? new Color(0.2f, 0.75f, 1f, 1f)
                : nodeType == PopulationNodeType.Intersection
                    ? new Color(1f, 0.75f, 0.1f, 1f)
                    : new Color(0.15f, 0.9f, 0.3f, 1f);
            Gizmos.DrawSphere(transform.position, size);

            float halfWidth = Mathf.Max(0.25f, pathWidth * 0.5f);
            Gizmos.DrawLine(transform.position - transform.right * halfWidth, transform.position + transform.right * halfWidth);

            if (links != null)
            {
                for (int i = 0; i < links.Count; ++i)
                {
                    PopulationRouteMarker linked = links[i];
                    if (!IsValidSceneLink(linked) || IsOneWayOverridePair(linked))
                        continue;

                    Vector3 from = transform.position;
                    Vector3 to = linked.transform.position;
                    Gizmos.color = ConnectionColor(IsConnectionWalkable(from, to, gameObject.scene));
                    Gizmos.DrawLine(from, to);
                }
            }

            if (oneWayOutboundLinks != null)
            {
                for (int i = 0; i < oneWayOutboundLinks.Count; ++i)
                {
                    PopulationRouteMarker linked = oneWayOutboundLinks[i];
                    if (!IsValidSceneLink(linked))
                        continue;

                    Vector3 from = transform.position;
                    Vector3 to = linked.transform.position;
                    Gizmos.color = ConnectionColor(IsConnectionWalkable(from, to, gameObject.scene));
                    DrawOneWayGizmo(from, to);
                }
            }
        }

        private bool IsValidSceneLink(PopulationRouteMarker linked) =>
            linked != null && linked != this && linked.gameObject.scene == gameObject.scene;

        public static bool IsConnectionWalkable(
            Vector3 from,
            Vector3 to,
            UnityEngine.SceneManagement.Scene scene)
        {
            ServerMap map = FindServerMap(scene);
            float distance = Vector3.Distance(from, to);
            int samples = Mathf.Clamp(Mathf.CeilToInt(distance / 0.5f), 1, 256);

            for (int i = 0; i <= samples; ++i)
            {
                float t = i / (float)samples;
                Vector3 sample = Vector3.Lerp(from, to, t);
                if (!IsWalkableSample(sample, scene, map))
                    return false;
            }

            return true;
        }

        private static bool IsWalkableSample(
            Vector3 sample,
            UnityEngine.SceneManagement.Scene scene,
            ServerMap map)
        {
            const float navSampleRadius = 0.65f;
            if (NavMesh.SamplePosition(sample, out NavMeshHit navHit, navSampleRadius, NavMesh.AllAreas))
            {
                Vector3 delta = navHit.position - sample;
                if (Mathf.Abs(delta.y) <= 1.0f &&
                    new Vector2(delta.x, delta.z).sqrMagnitude <= navSampleRadius * navSampleRadius)
                {
                    return true;
                }
            }

            Ray ray = new Ray(sample + Vector3.up * 1.5f, Vector3.down);
            RaycastHit[] hits = Physics.RaycastAll(ray, 3.0f, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
                return false;

            float maximumSlope = map != null ? map.NavAgentMaxSlope : 50f;
            float minimumNormalY = Mathf.Cos(maximumSlope * Mathf.Deg2Rad);

            for (int i = 0; i < hits.Length; ++i)
            {
                RaycastHit hit = hits[i];
                Collider collider = hit.collider;
                if (collider == null ||
                    collider.gameObject.scene != scene ||
                    !collider.enabled ||
                    collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (map != null &&
                    (map.CollisionLayers.value & (1 << collider.gameObject.layer)) == 0)
                {
                    continue;
                }

                if (collider.GetComponentInParent<ServerBakeIgnore>() != null ||
                    collider.GetComponentInParent<WorldInteractable>() != null)
                {
                    continue;
                }

                ServerSurface surface = collider.GetComponentInParent<ServerSurface>();
                if (surface != null &&
                    (surface.flags & Game.Shared.World.ServerSurfaceFlags.Water) != 0 &&
                    (surface.flags & Game.Shared.World.ServerSurfaceFlags.Walkable) == 0)
                {
                    continue;
                }

                if (hit.normal.y < minimumNormalY)
                    continue;

                // Match the server walk bake's default behavior: ordinary eligible level
                // geometry is walkable unless explicitly excluded. ServerSurface metadata
                // refines classification but is not required for sidewalks/floors to count.
                return true;
            }

            return false;
        }

        private static ServerMap FindServerMap(UnityEngine.SceneManagement.Scene scene)
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

        public static Color ConnectionColor(bool walkable) =>
            walkable
                ? new Color(0.15f, 0.9f, 0.3f, 0.85f)
                : new Color(0.95f, 0.15f, 0.1f, 0.9f);

        private bool IsOneWayOverridePair(PopulationRouteMarker linked)
        {
            if (linked == null)
                return false;
            bool outbound = oneWayOutboundLinks != null && oneWayOutboundLinks.Contains(linked);
            bool inbound = linked.oneWayOutboundLinks != null && linked.oneWayOutboundLinks.Contains(this);
            return outbound || inbound;
        }

        private static void DrawOneWayGizmo(Vector3 from, Vector3 to)
        {
            Gizmos.DrawLine(from, to);
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
