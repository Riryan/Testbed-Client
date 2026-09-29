using LiteNetLibManager;
using Testing.Player;
using Player.Client;
using Player.Networking;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Player.Editor
{
    /// <summary>
    /// Production-style pre-build gate: validate authoring state, never mutate it.
    /// Use the explicit Repair PlayerEntity Prefab command to fix authoring state.
    /// </summary>
    public sealed class PlayerEntityPrefabBuildValidator :
        IPreprocessBuildWithReport
    {
        public int callbackOrder => -900;

        public void OnPreprocessBuild(BuildReport report)
        {
            string prefabPath = PlayerEntityTestAssetLocator.PlayerPrefabPath;
            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

            if (prefab == null)
            {
                throw new BuildFailedException(
                    $"Canonical PlayerEntity prefab was not found: {prefabPath}");
            }

            int missing = CountMissing(prefab);
            if (missing > 0)
            {
                throw new BuildFailedException(
                    $"Canonical PlayerEntity prefab contains {missing} " +
                    $"Missing Script component record(s): {prefabPath}. " +
                    "Repair the canonical PlayerEntity prefab directly or restore it from source control.");
            }

            Require<LiteNetLibIdentity>(prefab, prefabPath);
            Require<InterestObserverAnchor>(prefab, prefabPath);
            Require<PlayerEntityNetwork>(prefab, prefabPath);
            Require<PlayerEntityClient>(prefab, prefabPath);
            Require<PlayerEntityInput>(prefab, prefabPath);
            Require<PlayerEntityBot>(prefab, prefabPath);
        }

        private static void Require<T>(GameObject prefab, string prefabPath)
            where T : Component
        {
            if (prefab.GetComponent<T>() != null)
                return;

            throw new BuildFailedException(
                $"Canonical PlayerEntity prefab is missing " +
                $"{typeof(T).FullName}: {prefabPath}. " +
                "Repair the canonical PlayerEntity prefab directly or restore it from source control.");
        }

        private static int CountMissing(GameObject root)
        {
            int result =
                GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(root);

            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; ++i)
            {
                result += CountMissing(
                    transform.GetChild(i).gameObject);
            }

            return result;
        }
    }
}
