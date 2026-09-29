#if UNITY_EDITOR
using System.Collections.Generic;
using Game.Client.UI.Standalone;
using Player.Client;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// V2.06 authored cleanup for the click-first interaction panel.
    /// Removes the retired Interaction Action 2/3/4 rows from the already-authored
    /// Player Config prefab. Runtime interaction presentation remains fully authored.
    /// </summary>
    public static class StandaloneClientUIInteractionPanelAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Interaction Panel V2.06")]
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
                StandaloneControlsWindow controls = root.GetComponentInChildren<StandaloneControlsWindow>(true);
                if (controls != null)
                {
                    StandaloneControlBindingRow[] allRows = root.GetComponentsInChildren<StandaloneControlBindingRow>(true);
                    var keptRows = new List<StandaloneControlBindingRow>(allRows.Length);
                    for (int i = 0; i < allRows.Length; ++i)
                    {
                        StandaloneControlBindingRow row = allRows[i];
                        if (row == null)
                            continue;

                        if (row.Action == PlayerControlAction.InteractionAction2 ||
                            row.Action == PlayerControlAction.InteractionAction3 ||
                            row.Action == PlayerControlAction.InteractionAction4)
                        {
                            Object.DestroyImmediate(row.gameObject);
                            continue;
                        }

                        keptRows.Add(row);
                    }

                    SerializedObject serializedControls = new SerializedObject(controls);
                    SerializedProperty rows = serializedControls.FindProperty("rows");
                    if (rows != null)
                    {
                        rows.arraySize = keptRows.Count;
                        for (int i = 0; i < keptRows.Count; ++i)
                            rows.GetArrayElementAtIndex(i).objectReferenceValue = keptRows[i];
                        serializedControls.ApplyModifiedPropertiesWithoutUndo();
                    }

                    EditorUtility.SetDirty(controls);
                }

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
            Debug.Log("[StandaloneUI] Interaction Panel V2.06 applied. Retired per-action interaction hotkey rows removed; Interact + clickable contextual panel is canonical.");
        }
    }
}
#endif
