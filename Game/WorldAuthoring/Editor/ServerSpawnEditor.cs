using System;
using System.Linq;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.WorldAuthoring.Editor
{
    [CustomEditor(typeof(ServerSpawn))]
    [CanEditMultipleObjects]
    public sealed class ServerSpawnEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            if (targets == null || targets.Length != 1)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Select one ServerSpawn to see resolved server-authoring status.",
                    MessageType.Info);
                return;
            }

            var spawn = target as ServerSpawn;
            if (spawn == null)
                return;

            DrawStatus(spawn);
        }

        private static void DrawStatus(ServerSpawn spawn)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Server Authoring Status", EditorStyles.boldLabel);

            Scene scene = spawn.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorGUILayout.HelpBox("Spawn is not in a loaded scene.", MessageType.Warning);
                return;
            }

            if (!spawn.gameObject.activeInHierarchy)
            {
                EditorGUILayout.HelpBox(
                    "This ServerSpawn is inactive in the hierarchy and is excluded from the current server bake.",
                    MessageType.Info);
                return;
            }

            long stableId = ServerWorldBake.StableAuthoringId(spawn.BakeId);
            EditorGUILayout.LabelField("Baked Stable ID", stableId.ToString());

            int duplicateCount = Object.FindObjectsByType<ServerSpawn>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Count(x =>
                    x != null &&
                    x != spawn &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy &&
                    ServerWorldBake.StableAuthoringId(x.BakeId) == stableId);

            if (duplicateCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Duplicate ServerSpawn identity detected ({duplicateCount + 1} active spawns share stable ID {stableId}). " +
                    "Validate/Bake will preserve the first spawn in deterministic hierarchy order and regenerate the later duplicate ID(s).",
                    MessageType.Warning);
            }

            if (!spawn.enabledForServer)
            {
                EditorGUILayout.HelpBox(
                    "This spawn is authored into the map with enabled=false; the server will not activate it as a normal spawn.",
                    MessageType.Info);
            }

            DrawMovementStatus(spawn, scene);
            DrawPortalStatus(spawn, scene);

            EditorGUILayout.Space();
            if (GUILayout.Button("Validate Scene ServerSpawns"))
                ServerWorldBake.ValidateSpawnAuthoring(scene, repairDuplicateIds: true, logSuccess: true);
        }

        private static void DrawMovementStatus(ServerSpawn spawn, Scene scene)
        {
            if (!IsSharedAiSpawnKind(spawn.kind))
            {
                EditorGUILayout.HelpBox(
                    "Shared lightweight AI movement: not applicable to this spawn kind.",
                    MessageType.Info);
                return;
            }

            PopulationRouteMarker[] routeMarkers = Object.FindObjectsByType<PopulationRouteMarker>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(x =>
                    x != null &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy)
                .ToArray();

            if (spawn.routeMarker != null)
            {
                if (spawn.routeMarker.gameObject.scene != scene)
                {
                    EditorGUILayout.HelpBox(
                        "Bake blocked: Route Marker belongs to another scene.",
                        MessageType.Error);
                    return;
                }

                if (!spawn.routeMarker.gameObject.activeInHierarchy)
                {
                    EditorGUILayout.HelpBox(
                        "Bake blocked: assigned Route Marker is inactive and will not be exported.",
                        MessageType.Error);
                    return;
                }

                EditorGUILayout.HelpBox(
                    $"Resolved movement: Route — explicit Route Marker '{spawn.routeMarker.label}'.",
                    MessageType.Info);
                return;
            }

            if (spawn.routeNodeId > 0)
            {
                bool manualRouteValid = routeMarkers.Any(
                    marker => ServerWorldBake.StableAuthoringId(marker.BakeId) == spawn.routeNodeId);

                if (!manualRouteValid)
                {
                    EditorGUILayout.HelpBox(
                        $"Bake blocked: manual routeNodeId {spawn.routeNodeId} does not match an active Population Route Marker in this scene.",
                        MessageType.Error);
                    return;
                }

                EditorGUILayout.HelpBox(
                    $"Resolved movement: Route — valid manual routeNodeId {spawn.routeNodeId}.",
                    MessageType.Info);
                return;
            }

            if (spawn.kind == ServerSpawnKind.Monster)
            {
                EditorGUILayout.HelpBox(
                    "Resolved movement: FreeRoam — Monster has no explicit route. It will use the shared lightweight FreeRoam path.",
                    MessageType.Info);
                return;
            }

            if (routeMarkers.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    "Resolved movement: Route — no explicit route is assigned, so the server will preserve the existing nearest-route fallback.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Resolved movement: Stationary — no explicit route is assigned and this scene has no active Population Route graph.",
                    MessageType.Info);
            }
        }

        private static void DrawPortalStatus(ServerSpawn spawn, Scene scene)
        {
            if (spawn.populationPortal != null)
            {
                if (spawn.populationPortal.gameObject.scene != scene)
                {
                    EditorGUILayout.HelpBox(
                        "Bake blocked: Population Portal belongs to another scene.",
                        MessageType.Error);
                    return;
                }

                if (!spawn.populationPortal.gameObject.activeInHierarchy)
                {
                    EditorGUILayout.HelpBox(
                        "Bake blocked: assigned Population Portal is inactive and will not be exported.",
                        MessageType.Error);
                    return;
                }

                EditorGUILayout.HelpBox(
                    $"Portal: explicit '{spawn.populationPortal.label}'.",
                    MessageType.Info);
                return;
            }

            if (spawn.portalId <= 0)
                return;

            PopulationPortal[] portals = Object.FindObjectsByType<PopulationPortal>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(x =>
                    x != null &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy)
                .ToArray();

            bool valid = portals.Any(
                portal => ServerWorldBake.StableAuthoringId(portal.BakeId) == spawn.portalId);

            EditorGUILayout.HelpBox(
                valid
                    ? $"Portal: valid manual portalId {spawn.portalId}."
                    : $"Bake blocked: manual portalId {spawn.portalId} does not match an active Population Portal in this scene.",
                valid ? MessageType.Info : MessageType.Error);
        }

        private static bool IsSharedAiSpawnKind(ServerSpawnKind kind) =>
            kind == ServerSpawnKind.Population ||
            kind == ServerSpawnKind.Npc ||
            kind == ServerSpawnKind.Monster;
    }
}
