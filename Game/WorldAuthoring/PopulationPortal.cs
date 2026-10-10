using System;
using System.Collections.Generic;
using Game.Shared.Population;
using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// Authored doorway/entrance used by standalone Population as a natural spawn/despawn
    /// presentation boundary. The component is baked into ServerPopulationPortal data.
    /// </summary>
    [AddComponentMenu("MMO/Population/Portal")]
    [DisallowMultipleComponent]
    public sealed class PopulationPortal : MonoBehaviour
    {
        [SerializeField, HideInInspector] private string bakeId = string.Empty;

        [SerializeField, HideInInspector]
        public string label = "Population Portal";
        public PopulationPortalMode mode = PopulationPortalMode.SpawnAndDespawn;
        public PopulationPortalType portalType = PopulationPortalType.GenericBuilding;
        public PopulationDestinationTag tags = PopulationDestinationTag.None;
        public PopulationNpcTypeMask allowedNpcTypes = PopulationNpcTypeMask.All;

        [Header("Route")]
        [Tooltip("Closest/primary road or sidewalk marker for spawning onto the route network.")]
        public PopulationRouteMarker routeMarker;
        [Tooltip("Additional nearby route markers with clear authored line of sight to this portal. Rebuilt explicitly by the Build Client scan button.")]
        public List<PopulationRouteMarker> nearbyRouteMarkers = new List<PopulationRouteMarker>();

        // Legacy serialized doorway-sequence authoring retained only so existing scenes
        // deserialize safely. The current Population bake/runtime uses this component's own
        // transform as the single authoritative spawn/despawn anchor and ignores these fields.
        [SerializeField, HideInInspector] public Transform interiorSpawn;
        [SerializeField, HideInInspector] public Transform approach;
        [SerializeField, HideInInspector] public Transform interaction;
        [SerializeField, HideInInspector] public Transform threshold;
        [SerializeField, HideInInspector] public Transform exterior;
        [SerializeField, HideInInspector] public WorldInteractable doorWorldObject;

        [Header("Lifecycle")]
        [Min(0f)] public float minimumRespawnDelay = 8f;
        [Min(0f)] public float maximumRespawnDelay = 20f;
        [Min(0.1f)] public float blockedRetryDelay = 2f;

        [Header("Spawn Cadence")]
        [Tooltip("Minimum delay between release bursts from this portal. Uses the shared Population scheduler; no per-Pop timer is created.")]
        [Min(0f)] public float minimumSpawnInterval = 0.75f;
        [Tooltip("Maximum delay between release bursts from this portal.")]
        [Min(0f)] public float maximumSpawnInterval = 1.75f;
        [Tooltip("Maximum number of Pops this portal may release in the same activation pass before the spawn interval applies.")]
        [Min(1)] public int spawnBurstLimit = 1;

        [Header("Density")]
        [Tooltip("Maximum number of Population actors owned by this spawn-capable portal that may exist in the world at once. The bake creates exactly this many lightweight portal-owned spawn anchors.")]
        [Min(1)] public int maximumActiveInWorld = 5;
        [Min(1)] public int maximumActiveNearby = 12;
        [Min(0.5f)] public float activeNearbyRadius = 18f;
        [Min(0.1f)] public float exitClearanceRadius = 0.45f;
        public bool requireDifferentDestination = true;
        [Tooltip("Usually leave disabled: the interior->door->exterior sequence already prevents visible popping and lets players actually watch Pops enter/exit.")]
        public bool suppressWhenVisibleToPlayers;

        public string BakeId => bakeId;

        /// <summary>
        /// Assigns a fresh authoring identity. Unity duplicates serialized private fields, so a
        /// duplicated Population Portal initially inherits the source portal's bakeId. ServerWorldBake
        /// calls this only when it detects that duplicated authoring identity.
        /// </summary>
        public void RegenerateBakeId()
        {
            bakeId = Guid.NewGuid().ToString("N");
        }

        public Transform EffectiveInterior => transform;
        public Transform EffectiveApproach => transform;
        public Transform EffectiveInteraction => transform;
        public Transform EffectiveThreshold => transform;
        public Transform EffectiveExterior => transform;

        private void Reset()
        {
            EnsureBakeId();
            label = gameObject.name;
        }

        private void OnValidate()
        {
            EnsureBakeId();
            label = string.IsNullOrWhiteSpace(label) ? gameObject.name : label.Trim();
            maximumRespawnDelay = Mathf.Max(minimumRespawnDelay, maximumRespawnDelay);
            blockedRetryDelay = Mathf.Max(0.1f, blockedRetryDelay);
            minimumSpawnInterval = Mathf.Max(0f, minimumSpawnInterval);
            maximumSpawnInterval = Mathf.Max(minimumSpawnInterval, maximumSpawnInterval);
            spawnBurstLimit = Mathf.Max(1, spawnBurstLimit);
            maximumActiveInWorld = Mathf.Max(1, maximumActiveInWorld);
            maximumActiveNearby = Mathf.Max(1, maximumActiveNearby);
            activeNearbyRadius = Mathf.Max(0.5f, activeNearbyRadius);
            exitClearanceRadius = Mathf.Max(0.1f, exitClearanceRadius);
        }

        private void EnsureBakeId()
        {
            if (string.IsNullOrWhiteSpace(bakeId))
                bakeId = Guid.NewGuid().ToString("N");
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.75f, 0.25f, 1f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.24f);

            if (routeMarker != null)
                Gizmos.DrawLine(transform.position, routeMarker.transform.position);

            if (nearbyRouteMarkers == null)
                return;

            for (int i = 0; i < nearbyRouteMarkers.Count; ++i)
            {
                PopulationRouteMarker marker = nearbyRouteMarkers[i];
                if (marker == null || marker == routeMarker)
                    continue;
                Gizmos.DrawLine(transform.position, marker.transform.position);
            }
        }
    }
}
