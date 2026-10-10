using System;
using System.Collections.Generic;
using Game.Shared.Population;
using UnityEngine;

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

        // Editor-owned bookkeeping for replaceable auto-generated bidirectional links.
        // Manual links remain in 'links' without appearing here and are never removed by
        // Clean Generated Route Links.
        [SerializeField, HideInInspector]
        public List<PopulationRouteMarker> generatedLinks = new List<PopulationRouteMarker>();

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
            generatedLinks ??= new List<PopulationRouteMarker>();
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
            // Connection lines are drawn by the Editor-only baked-server validator so the
            // Scene view reflects the same ServerCollisionWorld used by the GameServer.
        }

        private bool IsValidSceneLink(PopulationRouteMarker linked) =>
            linked != null && linked != this && linked.gameObject.scene == gameObject.scene;

        private bool IsOneWayOverridePair(PopulationRouteMarker linked)
        {
            if (linked == null)
                return false;
            bool outbound = oneWayOutboundLinks != null && oneWayOutboundLinks.Contains(linked);
            bool inbound = linked.oneWayOutboundLinks != null && linked.oneWayOutboundLinks.Contains(this);
            return outbound || inbound;
        }
    }
}
