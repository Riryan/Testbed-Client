#if UNITY_EDITOR
using System;
using System.IO;
using Game.Client.Input;
using Game.Client.UI.CharacterSelect;
using Game.Client.UI.Interactions;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.Root;
using Game.Client.WorldItems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// One-time migration/authoring support for the canonical client UI prefab.
    ///
    /// The resulting ClientUIRoot.prefab is the source of truth. Runtime code does not
    /// rebuild UI hierarchy and does not bind by Transform paths. Re-running migration
    /// only adds genuinely missing framework surfaces and refreshes serialized bindings;
    /// it does not delete or recreate existing authored HUD/frontend/interaction objects.
    /// </summary>
    // RETIRED MENU: one-time migration support only. The authored ClientUIRoot.prefab is now canonical.
    // Keep this source temporarily for recovery/migration, but do not expose it as a routine editor command.
    public static class ClientUIRootPrefabBuilder
    {
        private const string TargetPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";

        public static void MigrateMasterPrefab()
        {
            EnsureAssetFolder(Path.GetDirectoryName(TargetPath)?.Replace('\\', '/'));

            string sourcePath = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath) != null
                ? TargetPath
                : FindInventoryPrefabPath();
            if (string.IsNullOrEmpty(sourcePath))
                throw new InvalidOperationException("Neither ClientUIRoot nor PlayerInventoryShell prefab could be found.");

            GameObject source = PrefabUtility.LoadPrefabContents(sourcePath);
            try
            {
                source.name = "ClientUIRoot";

                EnsureRootCanvas(source);
                EnsureRootControllers(source, out ClientUIRoot uiRoot);
                EnsureFrontendIsEmbedded(source);
                CharacterSelectAuthoredUiRepair.Repair(
                    source.GetComponentInChildren<CharacterSelectShell>(true));

                // Creates only presentation surfaces that do not already exist and then
                // stores direct serialized references on the prefab controllers.
                uiRoot.BuildPresentationForEditor();

                // The master prefab is intentionally over-complete: planned gameplay surfaces
                // exist now so future systems bind to the stable authored canvas instead of
                // forcing another UI rebuild. Existing artist-authored panels are preserved.
                ClientUIFullScaffoldBuilder.EnsureFullScaffold(source);

                PrefabUtility.SaveAsPrefabAsset(source, TargetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);

                Debug.Log(
                    "[ClientUI] Master UI migration complete. ClientUIRoot.prefab is now the authored source of truth. " +
                    "Open that prefab directly and edit it normally; do not rebuild it for visual changes.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(source);
            }
        }

        public static void ValidateMasterPrefab()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            if (prefab == null)
                throw new InvalidOperationException($"Master UI prefab was not found: {TargetPath}");

            ClientUIRoot root = prefab.GetComponent<ClientUIRoot>();
            CharacterSelectShell frontend = prefab.GetComponentInChildren<CharacterSelectShell>(true);
            ClientInteractionUI interaction = prefab.GetComponent<ClientInteractionUI>();
            PlayerInventoryShell inventory = prefab.GetComponent<PlayerInventoryShell>();

            if (root == null || frontend == null || interaction == null || inventory == null)
            {
                throw new InvalidOperationException(
                    "Master UI validation failed. Required components: ClientUIRoot, CharacterSelectShell child, " +
                    "ClientInteractionUI, PlayerInventoryShell.");
            }

            // Validate stable semantic panel IDs rather than GameObject names. The hierarchy
            // labels are for humans only and may be renamed/re-parented without becoming
            // runtime or validation keys.
            ClientUIPanelId[] requiredSurfaces =
            {
                ClientUIPanelId.FrontendConnect,
                ClientUIPanelId.HudPlayerVitals,
                ClientUIPanelId.CharacterWindow,
                ClientUIPanelId.NpcDialogueWindow,
                ClientUIPanelId.InteractionContextActions,
                ClientUIPanelId.PopupGeneric,
                ClientUIPanelId.NotificationToasts,
                ClientUIPanelId.DeathOverlay,
                ClientUIPanelId.ChatWindow,
                ClientUIPanelId.SystemDisconnectOverlay,
                ClientUIPanelId.DebugDevelopmentHud
            };
            ClientUIPanelMarker[] markers = prefab.GetComponentsInChildren<ClientUIPanelMarker>(true);
            foreach (ClientUIPanelId requiredSurface in requiredSurfaces)
            {
                bool found = false;
                foreach (ClientUIPanelMarker marker in markers)
                {
                    if (marker != null && marker.PanelId == requiredSurface)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new InvalidOperationException($"Master UI validation failed: missing authored surface '{requiredSurface}'.");
            }

            if (prefab.GetComponent<ClientWindowManager>() == null)
                throw new InvalidOperationException("Master UI validation failed: missing ClientWindowManager.");

            CharacterSelectAuthoredUiRepair.Validate();

            Debug.Log("[ClientUI] Master ClientUIRoot prefab structure validated.", prefab);
            Selection.activeObject = prefab;
        }

        public static void InstallOrConsolidateInOpenScene()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            if (prefab == null)
            {
                MigrateMasterPrefab();
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            }

            ClientUIRoot master = UnityEngine.Object.FindFirstObjectByType<ClientUIRoot>();
            if (master == null)
            {
                GameObject instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException("Failed to instantiate the master ClientUIRoot prefab.");
                Undo.RegisterCreatedObjectUndo(instance, "Install Master Client UI");
                master = instance.GetComponent<ClientUIRoot>();
            }

            CharacterSelectShell[] frontends = UnityEngine.Object.FindObjectsByType<CharacterSelectShell>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            foreach (CharacterSelectShell frontend in frontends)
            {
                if (frontend == null || frontend.transform.IsChildOf(master.transform))
                    continue;
                Undo.DestroyObjectImmediate(frontend.gameObject);
            }

            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
            {
                GameObject eventSystem = new GameObject(
                    "EventSystem",
                    typeof(EventSystem),
                    typeof(StandaloneInputModule));
                Undo.RegisterCreatedObjectUndo(eventSystem, "Create UI EventSystem");
            }

            Selection.activeObject = master.gameObject;
            EditorSceneManager.MarkSceneDirty(master.gameObject.scene);
            Debug.Log(
                "[ClientUI] Open scene consolidated to the single ClientUIRoot master prefab. " +
                "Standalone CharacterSelectShell objects were removed.",
                master);
        }

        private static void EnsureRootControllers(GameObject source, out ClientUIRoot uiRoot)
        {
            uiRoot = source.GetComponent<ClientUIRoot>();
            if (uiRoot == null)
                uiRoot = source.AddComponent<ClientUIRoot>();

            if (source.GetComponent<PlayerInventoryShell>() == null)
                source.AddComponent<PlayerInventoryShell>();
            if (source.GetComponent<PlayerUiInputController>() == null)
                source.AddComponent<PlayerUiInputController>();
            if (source.GetComponent<WorldItemPresentationController>() == null)
                source.AddComponent<WorldItemPresentationController>();
            if (source.GetComponent<ClientInteractionUI>() == null)
                source.AddComponent<ClientInteractionUI>();
            if (source.GetComponent<ClientWindowManager>() == null)
                source.AddComponent<ClientWindowManager>();
        }

        private static void EnsureRootCanvas(GameObject source)
        {
            RectTransform rect = source.GetComponent<RectTransform>();
            if (rect == null)
                rect = source.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Canvas canvas = source.GetComponent<Canvas>();
            if (canvas == null)
                canvas = source.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 70;

            CanvasScaler scaler = source.GetComponent<CanvasScaler>();
            if (scaler == null)
                scaler = source.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (source.GetComponent<GraphicRaycaster>() == null)
                source.AddComponent<GraphicRaycaster>();
        }

        private static void EnsureFrontendIsEmbedded(GameObject source)
        {
            CharacterSelectShell existing = source.GetComponentInChildren<CharacterSelectShell>(true);
            if (existing != null)
                return;

            string frontendPath = FindFrontendPrefabPath();
            if (string.IsNullOrEmpty(frontendPath))
                throw new InvalidOperationException("CharacterSelectShell prefab could not be found for master UI migration.");

            GameObject frontendPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(frontendPath);
            GameObject frontend = PrefabUtility.InstantiatePrefab(frontendPrefab, source.transform) as GameObject;
            if (frontend == null)
                throw new InvalidOperationException("Failed to embed CharacterSelectShell into ClientUIRoot.");

            // Flatten the old standalone frontend into the master prefab so the whole UI
            // is editable in one Prefab Mode hierarchy instead of hopping between assets.
            if (PrefabUtility.IsPartOfPrefabInstance(frontend))
            {
                PrefabUtility.UnpackPrefabInstance(
                    frontend,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
            }

            frontend.name = "FrontendRoot";
            RectTransform rect = frontend.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            Canvas canvas = frontend.GetComponent<Canvas>();
            if (canvas != null)
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder = 100;
            }
        }

        private static string FindInventoryPrefabPath()
        {
            string[] guids = AssetDatabase.FindAssets("PlayerInventoryShell t:Prefab");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(path), "PlayerInventoryShell.prefab", StringComparison.Ordinal))
                    continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<PlayerInventoryShell>() != null)
                    return path;
            }
            return string.Empty;
        }

        private static string FindFrontendPrefabPath()
        {
            string[] guids = AssetDatabase.FindAssets("CharacterSelectShell t:Prefab");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<CharacterSelectShell>() != null)
                    return path;
            }
            return string.Empty;
        }

        private static void EnsureAssetFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || AssetDatabase.IsValidFolder(path))
                return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = Path.GetFileName(path);
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
#endif
