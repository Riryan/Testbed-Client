using System;
using Game.Shared.Actors;
using Game.Shared.World;
using UnityEngine;

namespace Game.WorldAuthoring
{
    [AddComponentMenu("MMO/Server Spawn")]
    public sealed class ServerSpawn : MonoBehaviour
    {
        [SerializeField, HideInInspector] private string bakeId = string.Empty;
        public string label = "Spawn";
        public ServerSpawnKind kind = ServerSpawnKind.PlayerFirstSpawn;
        public AuthoritativeActorKind actorKind = AuthoritativeActorKind.Player;
        public string archetypeId = string.Empty;
        [Tooltip("Optional authoritative loot table rolled once when this Population actor dies. Empty means no death loot.")]
        public string deathLootTableId = string.Empty;
        public int priority;
        public bool enabledForServer = true;
        [Min(0.05f)] public float capsuleRadius = 0.35f;
        [Min(0.1f)] public float capsuleHeight = 1.8f;
        [Min(0.05f)] public float maximumGroundSnap = 0.65f;
        public string[] tags = Array.Empty<string>();

        [Header("Population Route / Portal")]
        [Tooltip("Preferred authoring reference. ServerWorldBake converts this marker to the compact stable route-node ID.")]
        public PopulationRouteMarker routeMarker;
        [Tooltip("Preferred authoring reference. ServerWorldBake converts this portal to the compact stable portal ID.")]
        public PopulationPortal populationPortal;
        [Tooltip("Legacy/manual stable route-node ID. Used only when Route Marker is not assigned.")]
        public long routeNodeId;
        [Tooltip("Legacy/manual stable portal ID. Used only when Population Portal is not assigned.")]
        public long portalId;
        [Tooltip("Population only. One authoring spawn can bake multiple lightweight Population identities without duplicating scene objects.")]
        [Min(1)] public int populationCount = 1;

        public string BakeId => bakeId;

        /// <summary>
        /// Assigns a fresh authoring identity. Unity duplicates serialized private fields, so a
        /// duplicated ServerSpawn initially inherits the source spawn's bakeId. ServerWorldBake
        /// uses this only when it detects a duplicated/colliding authoring identity.
        /// </summary>
        public void RegenerateBakeId()
        {
            bakeId = Guid.NewGuid().ToString("N");
        }

        private void Reset() => EnsureBakeId();

        private void OnValidate()
        {
            EnsureBakeId();
            label = string.IsNullOrWhiteSpace(label) ? gameObject.name : label.Trim();
            deathLootTableId = string.IsNullOrWhiteSpace(deathLootTableId) ? string.Empty : deathLootTableId.Trim();
            capsuleRadius = Mathf.Max(0.05f, capsuleRadius);
            capsuleHeight = Mathf.Max(capsuleRadius * 2f, capsuleHeight);
            maximumGroundSnap = Mathf.Max(0.05f, maximumGroundSnap);
            tags ??= Array.Empty<string>();
            populationCount = Mathf.Max(1, populationCount);
        }

        private void EnsureBakeId()
        {
            if (string.IsNullOrWhiteSpace(bakeId))
                bakeId = Guid.NewGuid().ToString("N");
        }
    }
}
