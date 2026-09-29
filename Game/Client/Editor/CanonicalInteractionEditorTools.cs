#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Interactions;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Single production-facing Editor entry point for the canonical contextual interaction UI.
    /// Repairs the existing authored prefab in-place; it does not create a runtime UI path.
    /// </summary>
    public static class CanonicalInteractionEditorTools
    {
        private const string PrefabPath =
            "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        [MenuItem("MMO Tools/Interaction/Repair Canonical Interaction UI", priority = 100)]
        public static void RepairCanonicalInteractionUi()
        {
            // Trade belongs to the already-established authored Gameplay Flow. Reuse that
            // canonical authoring pass instead of introducing a second trade repair/binding system.
            // It rebuilds and serializes StandaloneTradeUI (including tradeWindowRoot and the
            // rest of its required references) into StandaloneClientUI.prefab.
            StandaloneClientUIGameplayFlowAuthoring.UpgradeExistingPrefab();
            RemoveObsoleteRootTradeComponent();

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (asset == null)
                throw new InvalidOperationException($"Missing canonical UI prefab: {PrefabPath}");

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                ClientGameplayUIRoot gameplay =
                    root.GetComponentInChildren<ClientGameplayUIRoot>(true);
                if (gameplay == null)
                    throw new InvalidOperationException(
                        "ClientGameplayUIRoot is missing from StandaloneClientUI.prefab.");

                ClientInteractionUI interaction =
                    root.GetComponentInChildren<ClientInteractionUI>(true);
                if (interaction == null)
                    interaction = root.AddComponent<ClientInteractionUI>();

                // Reuse the existing Editor-only authoring implementation. The resulting
                // hierarchy/references are serialized into the prefab; runtime never builds it.
                interaction.BuildPresentationForEditor();

                EditorUtility.SetDirty(interaction);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            List<string> errors = ValidateCanonicalInteractionContract();
            if (errors.Count != 0)
            {
                Debug.LogError("[Canonical Interaction] REPAIR INCOMPLETE:\n - " +
                               string.Join("\n - ", errors));
                return;
            }

            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            EditorGUIUtility.PingObject(Selection.activeObject);
            Debug.Log(
                "[Canonical Interaction] REPAIR PASSED. Authored interaction presentation is bound " +
                "on StandaloneClientUI.prefab. Runtime interaction UI construction is not required.");
        }

        private static void RemoveObsoleteRootTradeComponent()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            bool unloaded = false;
            try
            {
                // The supplied prefab proves the duplicate is NOT a second TradeWindow:
                // it is an obsolete StandaloneTradeUI component attached to the prefab ROOT
                // with every serialized trade reference null. The real authored TradeWindow
                // owns the fully configured StandaloneTradeUI.
                Game.Client.UI.Standalone.StandaloneTradeUI rootTrade =
                    root.GetComponent<Game.Client.UI.Standalone.StandaloneTradeUI>();

                if (rootTrade != null)
                {
                    UnityEngine.Object.DestroyImmediate(rootTrade);
                    EditorUtility.SetDirty(root);
                    PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                    Debug.Log(
                        "[Canonical Interaction] Removed obsolete unconfigured StandaloneTradeUI " +
                        "component from StandaloneClientUI prefab root.");
                }
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                    unloaded = true;
                }
            }

            if (unloaded)
                AssetDatabase.SaveAssets();
        }

        [MenuItem("MMO Tools/Interaction/Validate Canonical Interaction Contract", priority = 110)]
        public static void ValidateCanonicalInteractionFromMenu()
        {
            List<string> errors = ValidateCanonicalInteractionContract();
            if (errors.Count == 0)
            {
                Debug.Log(
                    "[Canonical Interaction] PASSED. One authored interaction controller and " +
                    "its serialized presentation are present on StandaloneClientUI.prefab.");
                return;
            }

            Debug.LogError("[Canonical Interaction] FAILED:\n - " +
                           string.Join("\n - ", errors));
        }

        private static List<string> ValidateCanonicalInteractionContract()
        {
            var errors = new List<string>();
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null)
            {
                errors.Add($"Could not load {PrefabPath}.");
                return errors;
            }

            try
            {
                ClientInteractionUI[] interactions =
                    root.GetComponentsInChildren<ClientInteractionUI>(true);
                if (interactions.Length != 1)
                {
                    errors.Add(
                        $"Expected exactly one ClientInteractionUI; found {interactions.Length}.");
                    return errors;
                }

                interactions[0].BuildPresentationForEditor();
                if (!interactions[0].HasAuthoredPresentation)
                    errors.Add("ClientInteractionUI authored presentation is incomplete.");

                ClientGameplayUIRoot[] gameplayRoots =
                    root.GetComponentsInChildren<ClientGameplayUIRoot>(true);
                if (gameplayRoots.Length != 1)
                    errors.Add(
                        $"Expected exactly one ClientGameplayUIRoot; found {gameplayRoots.Length}.");

                Game.Client.UI.Standalone.StandaloneTradeUI[] trades =
                    root.GetComponentsInChildren<Game.Client.UI.Standalone.StandaloneTradeUI>(true);
                if (trades.Length != 1)
                {
                    errors.Add(
                        $"Expected exactly one StandaloneTradeUI; found {trades.Length}.");
                }
                else
                {
                    SerializedObject tradeSerialized = new SerializedObject(trades[0]);
                    SerializedProperty tradeWindowRoot =
                        tradeSerialized.FindProperty("tradeWindowRoot");
                    if (tradeWindowRoot == null ||
                        tradeWindowRoot.objectReferenceValue == null)
                    {
                        errors.Add(
                            "StandaloneTradeUI.tradeWindowRoot is not authored/serialized.");
                    }

                    Game.Client.UI.Standalone.StandaloneClientUIRoot standaloneRoot =
                        root.GetComponent<Game.Client.UI.Standalone.StandaloneClientUIRoot>();
                    if (standaloneRoot == null)
                    {
                        errors.Add("StandaloneClientUIRoot is missing from prefab root.");
                    }
                    else
                    {
                        SerializedObject rootSerialized = new SerializedObject(standaloneRoot);
                        SerializedProperty rootTrade = rootSerialized.FindProperty("tradeWindow");
                        if (rootTrade == null || rootTrade.objectReferenceValue != trades[0])
                            errors.Add("StandaloneClientUIRoot.tradeWindow is not bound to the sole canonical StandaloneTradeUI.");
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return errors;
        }
    }
}
#endif
