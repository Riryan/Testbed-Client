#if UNITY_EDITOR
using System;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Interactions;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// One-time Editor upgrade that adds the existing canonical ClientInteractionUI to the
    /// standalone authored UI prefab. The interaction hierarchy is authored into the prefab;
    /// runtime code only binds existing targets, cached/public definitions and the existing
    /// GameServer interaction request paths.
    /// </summary>
    public static class StandaloneClientUIInteractionAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Interaction V2.05")]
        public static void UpgradeExistingPrefab()
        {
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefabAsset == null)
            {
                EditorUtility.DisplayDialog("Standalone UI", "StandaloneClientUI.prefab was not found.", "OK");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                ClientGameplayUIRoot gameplay = root.GetComponent<ClientGameplayUIRoot>();
                if (gameplay == null)
                    throw new InvalidOperationException("ClientGameplayUIRoot is missing. Apply Gameplay Flow V2.04 first.");

                Transform gameplayRoot = root.transform.Find("Gameplay");
                Transform overlays = gameplayRoot != null ? gameplayRoot.Find("Overlays") : null;
                if (gameplayRoot == null || overlays == null)
                    throw new InvalidOperationException("StandaloneClientUI.prefab must contain Gameplay/Overlays.");

                ClientInteractionUI interaction = root.GetComponent<ClientInteractionUI>();
                if (interaction == null)
                    interaction = root.AddComponent<ClientInteractionUI>();

                // This builds only in the Editor and serializes the finished hierarchy into
                // StandaloneClientUI.prefab. ClientInteractionUI does not build it at runtime.
                interaction.BuildPresentationForEditor();

                Transform interactionRoot = root.transform.Find("InteractionUIRoot");
                if (interactionRoot == null)
                    interactionRoot = FindDescendant(root.transform, "InteractionUIRoot");
                if (interactionRoot == null)
                    throw new InvalidOperationException("ClientInteractionUI did not produce its authored InteractionUIRoot.");

                interactionRoot.SetParent(overlays, false);
                RectTransform rect = interactionRoot as RectTransform;
                if (rect != null)
                {
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                }
                interactionRoot.SetAsLastSibling();

                EditorUtility.SetDirty(interaction);
                EditorUtility.SetDirty(gameplay);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            EditorGUIUtility.PingObject(Selection.activeObject);
            Debug.Log("[StandaloneUI] Interaction V2.05 authored into StandaloneClientUI.prefab using the existing ClientInteractionUI and existing GameServer interaction messages.");
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null)
                return null;
            for (int i = 0; i < root.childCount; ++i)
            {
                Transform child = root.GetChild(i);
                if (child.name == name)
                    return child;
                Transform nested = FindDescendant(child, name);
                if (nested != null)
                    return nested;
            }
            return null;
        }
    }
}
#endif
