using System.Linq;
using Game.Shared.Actors;
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
        private static bool advanced;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var kind = serializedObject.FindProperty("kind");
            var actorKind = serializedObject.FindProperty("actorKind");

            EditorGUILayout.LabelField("Server Spawn", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("label"), new GUIContent("Label"));
            EditorGUILayout.PropertyField(kind, new GUIContent("Type"));

            if (!kind.hasMultipleDifferentValues)
                SynchronizeActorKind((ServerSpawnKind)kind.enumValueIndex, actorKind);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("enabledForServer"), new GUIContent("Enabled"));

            if (!kind.hasMultipleDifferentValues)
            {
                ServerSpawnKind value = (ServerSpawnKind)kind.enumValueIndex;
                DrawTypeSpecific(value);
            }

            advanced = EditorGUILayout.Foldout(advanced, "Advanced", true);
            if (advanced)
                DrawAdvanced(kind, actorKind);

            serializedObject.ApplyModifiedProperties();

            if (targets == null || targets.Length != 1)
                return;

            if (target is ServerSpawn spawn)
                DrawStatus(spawn);
        }

        private void DrawTypeSpecific(ServerSpawnKind kind)
        {
            switch (kind)
            {
                case ServerSpawnKind.Population:
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Population", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("archetypeId"), new GUIContent("Archetype Id"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("populationCount"), new GUIContent("Count"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("routeMarker"), new GUIContent("Route Marker"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("populationPortal"), new GUIContent("Population Portal"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("deathLootTableId"), new GUIContent("Death Loot Table Id"));
                    break;

                case ServerSpawnKind.Npc:
                case ServerSpawnKind.Monster:
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField(kind == ServerSpawnKind.Npc ? "NPC" : "Monster", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("archetypeId"), new GUIContent("Archetype Id"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("routeMarker"), new GUIContent("Route Marker"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("populationPortal"), new GUIContent("Population Portal"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("deathLootTableId"), new GUIContent("Death Loot Table Id"));
                    break;
            }
        }

        private void DrawAdvanced(SerializedProperty kind, SerializedProperty actorKind)
        {
            using (new EditorGUI.DisabledScope(!kind.hasMultipleDifferentValues))
                EditorGUILayout.PropertyField(actorKind, new GUIContent("Actor Kind (Internal)"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("priority"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("capsuleRadius"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("capsuleHeight"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maximumGroundSnap"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tags"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("routeNodeId"), new GUIContent("Manual Route Node Id"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("portalId"), new GUIContent("Manual Portal Id"));
        }

        private static void SynchronizeActorKind(ServerSpawnKind kind, SerializedProperty actorKind)
        {
            AuthoritativeActorKind resolved;
            switch (kind)
            {
                case ServerSpawnKind.Monster:
                case ServerSpawnKind.Npc:
                case ServerSpawnKind.Population:
                    resolved = AuthoritativeActorKind.Population;
                    break;
                default:
                    resolved = AuthoritativeActorKind.Player;
                    break;
            }

            int value = (int)resolved;
            if (actorKind.intValue != value)
                actorKind.intValue = value;
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
                EditorGUILayout.HelpBox("This ServerSpawn is inactive in the hierarchy and is excluded from the current server bake.", MessageType.Info);
                return;
            }

            long stableId = ServerWorldBake.StableAuthoringId(spawn.BakeId);
            EditorGUILayout.LabelField("Baked Stable ID", stableId.ToString());

            int duplicateCount = Object.FindObjectsByType<ServerSpawn>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(x => x != null && x != spawn && x.gameObject.scene == scene && x.gameObject.activeInHierarchy && ServerWorldBake.StableAuthoringId(x.BakeId) == stableId);

            if (duplicateCount > 0)
                EditorGUILayout.HelpBox($"Duplicate ServerSpawn identity detected ({duplicateCount + 1} active spawns share stable ID {stableId}). Validate/Bake will repair later duplicate IDs.", MessageType.Warning);

            if (!spawn.enabledForServer)
                EditorGUILayout.HelpBox("This spawn is authored with enabled=false; the server will not activate it as a normal spawn.", MessageType.Info);

            DrawMovementStatus(spawn, scene);
            DrawPortalStatus(spawn, scene);

            EditorGUILayout.Space();
            if (GUILayout.Button("Validate Scene ServerSpawns"))
                ServerWorldBake.ValidateSpawnAuthoring(scene, repairDuplicateIds: true, logSuccess: true);
        }

        private static void DrawMovementStatus(ServerSpawn spawn, Scene scene)
        {
            if (!IsSharedAiSpawnKind(spawn.kind)) return;

            PopulationRouteMarker[] routeMarkers = Object.FindObjectsByType<PopulationRouteMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy).ToArray();

            if (spawn.routeMarker != null)
            {
                if (spawn.routeMarker.gameObject.scene != scene)
                    EditorGUILayout.HelpBox("Bake blocked: Route Marker belongs to another scene.", MessageType.Error);
                else if (!spawn.routeMarker.gameObject.activeInHierarchy)
                    EditorGUILayout.HelpBox("Bake blocked: assigned Route Marker is inactive and will not be exported.", MessageType.Error);
                else
                    EditorGUILayout.HelpBox($"Resolved movement: Route — explicit Route Marker '{spawn.routeMarker.label}'.", MessageType.Info);
                return;
            }

            if (spawn.routeNodeId > 0)
            {
                bool valid = routeMarkers.Any(marker => ServerWorldBake.StableAuthoringId(marker.BakeId) == spawn.routeNodeId);
                EditorGUILayout.HelpBox(valid ? $"Resolved movement: Route — valid manual routeNodeId {spawn.routeNodeId}." : $"Bake blocked: manual routeNodeId {spawn.routeNodeId} does not match an active Population Route Marker in this scene.", valid ? MessageType.Info : MessageType.Error);
                return;
            }

            if (spawn.kind == ServerSpawnKind.Monster)
                EditorGUILayout.HelpBox("Resolved movement: FreeRoam — Monster has no explicit route.", MessageType.Info);
            else if (routeMarkers.Length > 0)
                EditorGUILayout.HelpBox("Resolved movement: Route — no explicit route is assigned, so the existing nearest-route fallback applies.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Resolved movement: Stationary — no route is assigned and this scene has no active Population Route graph.", MessageType.Info);
        }

        private static void DrawPortalStatus(ServerSpawn spawn, Scene scene)
        {
            if (spawn.populationPortal != null)
            {
                if (spawn.populationPortal.gameObject.scene != scene)
                    EditorGUILayout.HelpBox("Bake blocked: Population Portal belongs to another scene.", MessageType.Error);
                else if (!spawn.populationPortal.gameObject.activeInHierarchy)
                    EditorGUILayout.HelpBox("Bake blocked: assigned Population Portal is inactive and will not be exported.", MessageType.Error);
                else
                    EditorGUILayout.HelpBox($"Portal: explicit '{spawn.populationPortal.label}'.", MessageType.Info);
                return;
            }

            if (spawn.portalId <= 0) return;

            bool valid = Object.FindObjectsByType<PopulationPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Any(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy && ServerWorldBake.StableAuthoringId(x.BakeId) == spawn.portalId);
            EditorGUILayout.HelpBox(valid ? $"Portal: valid manual portalId {spawn.portalId}." : $"Bake blocked: manual portalId {spawn.portalId} does not match an active Population Portal in this scene.", valid ? MessageType.Info : MessageType.Error);
        }

        private static bool IsSharedAiSpawnKind(ServerSpawnKind kind) => kind == ServerSpawnKind.Population || kind == ServerSpawnKind.Npc || kind == ServerSpawnKind.Monster;
    }
}
