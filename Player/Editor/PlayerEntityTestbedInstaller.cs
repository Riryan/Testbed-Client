using System;
using System.Linq;
using LiteNetLibManager;
using Testing.Player;
using Player.Client;
using Player.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Player.Editor
{
    // RETIRED MENU: old PlayerEntity testbed install/repair workflow.
    // Kept temporarily as migration/recovery code; normal client authoring/builds must not depend on it.
    public static class PlayerEntityTestbedInstaller
    {
        public static string GeneratedFolder => PlayerEntityTestAssetLocator.GeneratedFolder;
        public static string PlayerPrefabPath => PlayerEntityTestAssetLocator.PlayerPrefabPath;
        public static string ScenePath => PlayerEntityTestAssetLocator.ScenePath;

        public static void Install()
        {
            PlayerEntityTestAssetLocator.EnsureGeneratedFolder();
            AssetDatabase.Refresh();

            LiteNetLibIdentity playerPrefab = CreateOrRepairPlayerPrefab();
            if (playerPrefab == null)
            {
                Debug.LogError("[PlayerEntity] Could not create/load the PlayerEntity prefab.");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject managerGo = new GameObject("MMO Testbed Manager");
            PlayerEntityGameManager manager = managerGo.AddComponent<PlayerEntityGameManager>();
            LiteNetLibAssets assets = managerGo.AddComponent<LiteNetLibAssets>();
            SpatialInterestManager aoi = managerGo.AddComponent<SpatialInterestManager>();
            managerGo.AddComponent<MMOTestbedRuntimePanel>();

            manager.networkAddress = "127.0.0.1";
            manager.networkPort = 7770;
            manager.maxConnections = 4096;
            manager.updateFps = 20;
            manager.doNotEnterGameOnConnect = false;

            aoi.defaultVisibleRange = 48f;
            aoi.cellSize = 32f;
            aoi.maxObserversPerEntity = 160;
            aoi.hardMaxObserversPerEntity = 512;
            aoi.maxCandidateChecksPerTarget = 4096;

            AssignPlayerPrefab(assets, playerPrefab);

            GameObject spawnRoot = new GameObject("Spawn Points");
            for (int i = 0; i < 16; ++i)
            {
                float angle = i * Mathf.PI * 2f / 16f;
                GameObject spawn = new GameObject($"Spawn {i:00}");
                spawn.transform.SetParent(spawnRoot.transform);
                spawn.transform.position = new Vector3(Mathf.Cos(angle) * 10f, 0f, Mathf.Sin(angle) * 10f);
                LiteNetLibSpawnPoint point = spawn.AddComponent<LiteNetLibSpawnPoint>();
                SerializedObject pointSo = new SerializedObject(point);
                SerializedProperty radius = pointSo.FindProperty("radius");
                if (radius != null)
                    radius.floatValue = 1.5f;
                pointSo.ApplyModifiedPropertiesWithoutUndo();
            }

#if !UNITY_SERVER
            GameObject cameraGo = new GameObject("Camera");
            Camera camera = cameraGo.AddComponent<Camera>();
            cameraGo.tag = "MainCamera";
            cameraGo.transform.position = new Vector3(0f, 35f, -35f);
            cameraGo.transform.rotation = Quaternion.Euler(40f, 0f, 0f);
            camera.farClipPlane = 500f;

            GameObject lightGo = new GameObject("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
#endif

            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(assets);
            EditorUtility.SetDirty(aoi);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            var buildScenes = EditorBuildSettings.scenes.ToList();
            if (!buildScenes.Any(s => s.path == ScenePath))
            {
                buildScenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = buildScenes.ToArray();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeGameObject = managerGo;

            Debug.Log(
                $"[PlayerEntity] Installed PlayerEntity test scene with player prefab assigned. " +
                $"Prefab: {PlayerPrefabPath}, Scene: {ScenePath}");
        }

        public static void RepairPlayerPrefab()
        {
            LiteNetLibIdentity prefab = CreateOrRepairPlayerPrefab();
            if (prefab == null)
            {
                Debug.LogError("[PlayerEntity] PlayerEntity prefab repair failed.");
                return;
            }

            Debug.Log(
                $"[PlayerEntity] PlayerEntity prefab is canonical and contains no Missing Script entries: {PlayerPrefabPath}");
        }

        public static void Repair()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[PlayerEntity] Stop Play Mode before repairing the test scene.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            LiteNetLibIdentity playerPrefab = CreateOrRepairPlayerPrefab();
            if (playerPrefab == null)
            {
                Debug.LogError("[PlayerEntity] Could not create/load the PlayerEntity prefab.");
                return;
            }

            Scene scene;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            else
            {
                Debug.LogWarning("[PlayerEntity] Generated test scene does not exist yet. Running full installer.");
                Install();
                return;
            }

            PlayerEntityGameManager manager =
                UnityEngine.Object.FindFirstObjectByType<PlayerEntityGameManager>(FindObjectsInactive.Include);

            if (manager == null)
            {
                Debug.LogError("[PlayerEntity] PlayerEntityGameManager was not found in the generated scene.");
                return;
            }

            LiteNetLibAssets assets = manager.GetComponent<LiteNetLibAssets>();
            if (assets == null)
                assets = manager.gameObject.AddComponent<LiteNetLibAssets>();

            AssignPlayerPrefab(assets, playerPrefab);

            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(assets);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeGameObject = manager.gameObject;

            Debug.Log(
                $"[PlayerEntity] Repaired PlayerEntity scene. " +
                $"LiteNetLibAssets.playerPrefab = {playerPrefab.name}, " +
                $"spawnablePrefabs[0] = {playerPrefab.name}");
        }

        private static LiteNetLibIdentity CreateOrRepairPlayerPrefab()
        {
            PlayerEntityTestAssetLocator.EnsureGeneratedFolder();
            AssetDatabase.Refresh();

            GameObject existing =
                AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);

            if (existing != null)
            {
                GameObject root =
                    PrefabUtility.LoadPrefabContents(PlayerPrefabPath);

                try
                {
                    int removedMissing = RemoveMissingScriptsRecursively(root);

                    LiteNetLibIdentity identity =
                        EnsureComponent<LiteNetLibIdentity>(root);

                    identity.VisibleRange = 48f;
                    identity.PoolingSize = 32;

                    EnsureComponent<InterestObserverAnchor>(root);
                    EnsureComponent<PlayerEntityNetwork>(root);
                    RemoveLegacyUnityServerComponent(root);
                    EnsureComponent<PlayerEntityClient>(root);
                    EnsureComponent<PlayerEntityInput>(root);
                    EnsureComponent<PlayerEntityBot>(root);

                    SerializedObject so = new SerializedObject(identity);
                    SerializedProperty assetId = so.FindProperty("assetId");
                    if (assetId != null)
                        assetId.stringValue = "mmo.testbed.playerentity.v1";
                    so.ApplyModifiedPropertiesWithoutUndo();

                    PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    GameObject repaired =
                        AssetDatabase.LoadAssetAtPath<GameObject>(
                            PlayerPrefabPath);

                    int remainingMissing =
                        CountMissingScriptsRecursively(repaired);

                    if (remainingMissing != 0)
                    {
                        throw new InvalidOperationException(
                            $"PlayerEntity prefab repair failed: " +
                            $"{remainingMissing} Missing Script entries remain.");
                    }

                    if (removedMissing > 0)
                    {
                        Debug.Log(
                            $"[PlayerEntity] Removed {removedMissing} stale Missing Script " +
                            $"component record(s) from {PlayerPrefabPath}.");
                    }

                    return repaired != null
                        ? repaired.GetComponent<LiteNetLibIdentity>()
                        : null;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            GameObject prefabRoot = new GameObject("MMOPlayerEntity");
            try
            {
                LiteNetLibIdentity identity =
                    prefabRoot.AddComponent<LiteNetLibIdentity>();

                identity.VisibleRange = 48f;
                identity.PoolingSize = 32;

                prefabRoot.AddComponent<InterestObserverAnchor>();
                prefabRoot.AddComponent<PlayerEntityNetwork>();
                prefabRoot.AddComponent<PlayerEntityClient>();
                prefabRoot.AddComponent<PlayerEntityInput>();
                prefabRoot.AddComponent<PlayerEntityBot>();

                SerializedObject so = new SerializedObject(identity);
                SerializedProperty assetId = so.FindProperty("assetId");
                if (assetId != null)
                    assetId.stringValue = "mmo.testbed.playerentity.v1";
                so.ApplyModifiedPropertiesWithoutUndo();

                GameObject savedPrefab =
                    PrefabUtility.SaveAsPrefabAsset(
                        prefabRoot,
                        PlayerPrefabPath);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (savedPrefab == null)
                {
                    savedPrefab =
                        AssetDatabase.LoadAssetAtPath<GameObject>(
                            PlayerPrefabPath);
                }

                return savedPrefab != null
                    ? savedPrefab.GetComponent<LiteNetLibIdentity>()
                    : null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(prefabRoot);
            }
        }

        private static void RemoveLegacyUnityServerComponent(GameObject root)
        {
            if (root == null)
                return;

            MonoBehaviour[] behaviours = root.GetComponents<MonoBehaviour>();
            for (int i = behaviours.Length - 1; i >= 0; --i)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                    continue;

                Type type = behaviour.GetType();
                if (type.FullName != "Player.Server.PlayerEntityServer")
                    continue;

                UnityEngine.Object.DestroyImmediate(behaviour, true);
                Debug.Log(
                    "[PlayerEntity] Removed legacy Unity-authoritative PlayerEntityServer " +
                    "from the canonical client prefab. Standalone .NET GameServer owns authority.");
            }
        }

        private static T EnsureComponent<T>(GameObject gameObject)
            where T : Component
        {
            T component = gameObject.GetComponent<T>();
            return component != null
                ? component
                : gameObject.AddComponent<T>();
        }

        private static int RemoveMissingScriptsRecursively(GameObject root)
        {
            if (root == null)
                return 0;

            int removed =
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);

            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; ++i)
            {
                removed += RemoveMissingScriptsRecursively(
                    transform.GetChild(i).gameObject);
            }

            return removed;
        }

        private static int CountMissingScriptsRecursively(GameObject root)
        {
            if (root == null)
                return 0;

            int count =
                GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(root);

            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; ++i)
            {
                count += CountMissingScriptsRecursively(
                    transform.GetChild(i).gameObject);
            }

            return count;
        }

        private static void AssignPlayerPrefab(LiteNetLibAssets assets, LiteNetLibIdentity playerPrefab)
        {
            if (assets == null || playerPrefab == null)
                return;

            // Use SerializedObject so the reference is unquestionably persisted into
            // the generated scene rather than relying on a direct field assignment.
            SerializedObject so = new SerializedObject(assets);

            SerializedProperty playerProp = so.FindProperty("playerPrefab");
            if (playerProp != null)
                playerProp.objectReferenceValue = playerPrefab;

            SerializedProperty spawnablesProp = so.FindProperty("spawnablePrefabs");
            if (spawnablesProp != null)
            {
                spawnablesProp.arraySize = 1;
                spawnablesProp.GetArrayElementAtIndex(0).objectReferenceValue = playerPrefab;
            }

            SerializedProperty poolingProp = so.FindProperty("disablePooling");
            if (poolingProp != null)
                poolingProp.boolValue = false;

            so.ApplyModifiedPropertiesWithoutUndo();

            // Also assign directly so the in-memory scene state is immediately correct.
            assets.playerPrefab = playerPrefab;
            assets.spawnablePrefabs = new[] { playerPrefab };
            assets.disablePooling = false;

            EditorUtility.SetDirty(assets);
        }
    }
}
