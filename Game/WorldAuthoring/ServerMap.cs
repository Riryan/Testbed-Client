using System;
using Game.Shared.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring
{
    [AddComponentMenu("MMO/Server Map")]
    [DisallowMultipleComponent]
    public sealed class ServerMap : MonoBehaviour
    {
        [Serializable]
        public sealed class NavAreaRule
        {
            public string areaName = "Walkable";
            public ServerSurfaceFlags flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground;
        }

        [Header("Map")]
        [SerializeField] private string mapId = string.Empty;
        [SerializeField] private ServerMapKind mapKind = ServerMapKind.World;
        [SerializeField] private string instanceId = string.Empty;

        [Header("Bake")]
        [SerializeField] private bool bakeNavMesh = true;
        [SerializeField] private bool exportStaticCollision = true;
        [SerializeField] private LayerMask collisionLayers = ~0;
        [SerializeField, Min(1)] private int terrainSampleStep = 4;

        [Header("Server NavMesh - Recast")]
        [Tooltip("Voxel width/depth used by the standalone server navigation bake.")]
        [SerializeField, Min(0.05f)] private float navCellSize = 0.20f;
        [Tooltip("Voxel height used by the standalone server navigation bake.")]
        [SerializeField, Min(0.02f)] private float navCellHeight = 0.10f;
        [Tooltip("Tile width/depth in voxels. 128 at 0.20m = 25.6m tiles.")]
        [SerializeField, Range(32, 512)] private int navTileSize = 128;
        [SerializeField, Min(0.5f)] private float navAgentHeight = 1.80f;
        [SerializeField, Min(0.05f)] private float navAgentRadius = 0.35f;
        [SerializeField, Min(0f)] private float navAgentMaxClimb = 0.40f;
        [SerializeField, Range(1f, 89f)] private float navAgentMaxSlope = 50f;

        [Header("World Rules")]
        [SerializeField] private ServerWorldRules worldRules = new ServerWorldRules();

        [Header("Property Template")]
        [SerializeField] private ServerPropertyTemplateDefinition propertyTemplate;

        [Header("NavMesh Area Mapping")]
        [SerializeField] private NavAreaRule[] navAreas = Array.Empty<NavAreaRule>();

        public string MapId => mapId;
        public ServerMapKind MapKind => mapKind;
        public string InstanceId => instanceId ?? string.Empty;
        public bool BakeNavMesh => bakeNavMesh;
        public bool ExportStaticCollision => exportStaticCollision;
        public LayerMask CollisionLayers => collisionLayers;
        public int TerrainSampleStep => Mathf.Clamp(terrainSampleStep, 1, 32);
        public float NavCellSize => Mathf.Max(0.05f, navCellSize);
        public float NavCellHeight => Mathf.Max(0.02f, navCellHeight);
        public int NavTileSize => Mathf.Clamp(navTileSize, 32, 512);
        public float NavAgentHeight => Mathf.Max(0.5f, navAgentHeight);
        public float NavAgentRadius => Mathf.Max(0.05f, navAgentRadius);
        public float NavAgentMaxClimb => Mathf.Max(0f, navAgentMaxClimb);
        public float NavAgentMaxSlope => Mathf.Clamp(navAgentMaxSlope, 1f, 89f);
        public ServerWorldRules WorldRules => worldRules ??= new ServerWorldRules();
        public ServerPropertyTemplateDefinition PropertyTemplate => propertyTemplate;
        public NavAreaRule[] NavAreas => navAreas ?? Array.Empty<NavAreaRule>();

        private void Reset()
        {
            mapId = NormalizeId(gameObject.scene.name);
            navAreas = DefaultNavAreas();
            worldRules = new ServerWorldRules();
            terrainSampleStep = 4;
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(mapId))
                mapId = NormalizeId(gameObject.scene.name);
            else
                mapId = NormalizeId(mapId);

            instanceId = NormalizeId(instanceId);
            terrainSampleStep = Mathf.Clamp(terrainSampleStep, 1, 32);
            navCellSize = Mathf.Max(0.05f, navCellSize);
            navCellHeight = Mathf.Max(0.02f, navCellHeight);
            navTileSize = Mathf.Clamp(navTileSize, 32, 512);
            navAgentHeight = Mathf.Max(0.5f, navAgentHeight);
            navAgentRadius = Mathf.Max(0.05f, navAgentRadius);
            navAgentMaxClimb = Mathf.Max(0f, navAgentMaxClimb);
            navAgentMaxSlope = Mathf.Clamp(navAgentMaxSlope, 1f, 89f);
            worldRules ??= new ServerWorldRules();
            navAreas ??= Array.Empty<NavAreaRule>();

            if (mapKind == ServerMapKind.PropertyTemplate && propertyTemplate == null)
                ApplyPropertyDefaults();
            if (propertyTemplate != null && string.IsNullOrWhiteSpace(propertyTemplate.templateId))
                propertyTemplate.templateId = mapId;
        }

        [ContextMenu("Use Scene Name As Map ID")]
        private void UseSceneName()
        {
            mapId = NormalizeId(gameObject.scene.name);
            if (propertyTemplate != null)
                propertyTemplate.templateId = mapId;
        }

        [ContextMenu("Apply Property Baseline")]
        public void ApplyPropertyDefaults()
        {
            mapKind = ServerMapKind.PropertyTemplate;
            worldRules = new ServerWorldRules
            {
                movementProfile = ServerMovementAuthorityProfile.SafeInterior,
                combatAllowed = false,
                pvpAllowed = false,
                vehiclesAllowed = false,
            };

            propertyTemplate = new ServerPropertyTemplateDefinition
            {
                templateId = string.IsNullOrWhiteSpace(mapId) ? NormalizeId(gameObject.scene.name) : mapId,
                maximumPlaceables = 150,
                maximumCollisionPlaceables = 100,
                maximumInteractivePlaceables = 50,
                maximumDynamicStatePlaceables = 50,
                maximumStorageContainers = 12,
                categoryLimits = new[]
                {
                    Limit("Beds", 6),
                    Limit("Sofas", 8),
                    Limit("Tables", 12),
                    Limit("Chairs", 24),
                    Limit("Storage", 12),
                    Limit("InteractiveFurniture", 30),
                    Limit("Lights", 40),
                    Limit("WallDecor", 60),
                    Limit("FloorDecor", 60),
                    Limit("LargeProps", 10),
                },
            };
        }

        public ServerSurfaceFlags FlagsForNavArea(int areaIndex)
        {
            NavAreaRule[] rules = NavAreas;
            for (int i = 0; i < rules.Length; ++i)
            {
                NavAreaRule rule = rules[i];
                if (rule == null || string.IsNullOrWhiteSpace(rule.areaName))
                    continue;
                int configured = UnityEngine.AI.NavMesh.GetAreaFromName(rule.areaName);
                if (configured == areaIndex)
                    return rule.flags;
            }

            return areaIndex == 0
                ? ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground
                : ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground;
        }

        public static string NormalizeId(string value) => ServerMapId.Normalize(value);

        private static ServerPlaceableCategoryLimit Limit(string category, int maximum) =>
            new ServerPlaceableCategoryLimit { category = category, maximum = maximum };

        private static NavAreaRule[] DefaultNavAreas() => new[]
        {
            new NavAreaRule { areaName = "Walkable", flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground },
            new NavAreaRule { areaName = "Road", flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground | ServerSurfaceFlags.Road | ServerSurfaceFlags.VehicleDrivable },
            new NavAreaRule { areaName = "Sidewalk", flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground | ServerSurfaceFlags.Sidewalk },
            new NavAreaRule { areaName = "Interior", flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground | ServerSurfaceFlags.Interior | ServerSurfaceFlags.PlacementFloor },
            new NavAreaRule { areaName = "Roof", flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground | ServerSurfaceFlags.Roof },
            new NavAreaRule { areaName = "Water", flags = ServerSurfaceFlags.Water },
        };
    }
}
