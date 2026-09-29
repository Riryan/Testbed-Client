using System;
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

        [Header("Identity")]
        public string label = "Population Portal";
        public PopulationPortalMode mode = PopulationPortalMode.SpawnAndDespawn;
        public PopulationPortalType portalType = PopulationPortalType.GenericBuilding;
        public PopulationDestinationTag tags = PopulationDestinationTag.None;
        public PopulationNpcTypeMask allowedNpcTypes = PopulationNpcTypeMask.All;

        [Header("Route")]
        [Tooltip("Road/sidewalk marker attached to this entrance.")]
        public PopulationRouteMarker routeMarker;

        [Header("Door Sequence")]
        [Tooltip("Off-world/interior start point. Falls back to this transform.")]
        public Transform interiorSpawn;
        [Tooltip("Point inside or immediately before the door interaction.")]
        public Transform approach;
        public Transform interaction;
        public Transform threshold;
        [Tooltip("Exterior point that leads onto the route network. Falls back to Route Marker or this transform.")]
        public Transform exterior;
        [Tooltip("Optional canonical world-interactable door. Its baked stable ID is reused by Population door sequencing.")]
        public WorldInteractable doorWorldObject;

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

        public Transform EffectiveInterior => interiorSpawn != null ? interiorSpawn : transform;
        public Transform EffectiveApproach => approach != null ? approach : transform;
        public Transform EffectiveInteraction => interaction != null ? interaction : transform;
        public Transform EffectiveThreshold => threshold != null ? threshold : transform;
        public Transform EffectiveExterior => exterior != null
            ? exterior
            : routeMarker != null ? routeMarker.transform : transform;

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
            Vector3 a = EffectiveInterior.position;
            Vector3 b = EffectiveApproach.position;
            Vector3 c = EffectiveInteraction.position;
            Vector3 d = EffectiveThreshold.position;
            Vector3 e = EffectiveExterior.position;

            Gizmos.color = new Color(0.75f, 0.25f, 1f, 0.9f);
            Gizmos.DrawWireSphere(a, 0.18f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, e);
            Gizmos.DrawWireSphere(e, 0.24f);
            if (routeMarker != null)
                Gizmos.DrawLine(e, routeMarker.transform.position);
        }
    }
}
