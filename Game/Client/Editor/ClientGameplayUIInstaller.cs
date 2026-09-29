#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.Input;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Interactions;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.Root;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Canonical authoring tool for the POST-LOGIN gameplay UI.
    ///
    /// This is intentionally the one editor script to extend as the gameplay UI grows.
    /// It preserves the working frontend, Inventory, Settings, and system/recovery surfaces;
    /// archives the old gameplay presentation so it cannot participate at runtime; and
    /// rebuilds one clean GameplayUIV2 presentation root.
    ///
    /// Runtime authority/networking is never created here. The authored UI only binds to
    /// the existing ClientGameplayUIRoot / ClientInteractionUI / PlayerInventoryShell paths.
    /// </summary>
    public static class ClientGameplayUIInstaller
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";
        private const string BackupPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.LegacyBackup.prefab";
        private const string GameplayRootName = "GameplayUIV2";
        private const string LegacyArchiveName = "__LegacyGameplayUI_ARCHIVE";
        private const string PreservedWindowsName = "GameplayPreservedWindows";
        private const string InteractionRootName = "InteractionUIRoot";

        // These are old post-login presentation groups. They are archived, not deleted,
        // so existing serialized references remain valid while we finish migrating away
        // from ClientUIRoot's legacy presentation responsibilities.
        private static readonly string[] LegacyTopLevelGroups =
        {
            "HUD",
            "Chat",
            "Interaction UI",
            "Game Windows",
            "NPC Windows",
            "Debug",
        };

        // LEGACY/DEAD-END: menu registration intentionally disabled. Preserve method for rollback/reference.
        // [MenuItem("MMO Tools/UI/Gameplay V2/Rebuild Canonical Post-Login UI", priority = 2010)]
        public static void Rebuild()
        {
            EnsureOneTimeBackup();

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (prefabRoot == null)
                throw new InvalidOperationException($"Unable to load {PrefabPath}.");

            try
            {
                RequireCanonicalRoot(prefabRoot,
                    out ClientUIRoot canonicalRoot,
                    out PlayerInventoryShell inventory,
                    out ClientInteractionUI interaction);

                // -------- PHASE 1: PRESERVE KNOWN-WORKING MODULES --------
                GameObject inventoryWindow = GetInventoryWindow(inventory);
                GameObject inventoryPreserveRoot = FindPanelObject(prefabRoot, ClientUIPanelId.InventoryWindow);
                if (inventoryPreserveRoot == null)
                    inventoryPreserveRoot = inventoryWindow;

                GameObject settingsWindow = FindPanelObject(prefabRoot, ClientUIPanelId.SettingsWindow);

                Transform preservedWindows = GetOrCreateTopLevel(prefabRoot.transform, PreservedWindowsName);
                Stretch(preservedWindows as RectTransform);
                PreserveWindow(inventoryPreserveRoot, preservedWindows);
                PreserveWindow(settingsWindow, preservedWindows);

                // -------- PHASE 2: RETIRE OLD POST-LOGIN PRESENTATION --------
                // Delete only a prior V2 build. The older gameplay UI is archived below.
                RemoveTopLevel(prefabRoot.transform, GameplayRootName);
                RemoveTopLevel(prefabRoot.transform, InteractionRootName);

                ClientGameplayUIRoot oldRuntime = prefabRoot.GetComponent<ClientGameplayUIRoot>();
                if (oldRuntime != null)
                    UnityEngine.Object.DestroyImmediate(oldRuntime, true);

                Transform archive = GetOrCreateTopLevel(prefabRoot.transform, LegacyArchiveName);
                Stretch(archive as RectTransform);
                ArchiveLegacyGroups(prefabRoot.transform, archive, preservedWindows);
                archive.gameObject.SetActive(false);

                // The V2 root is now the only post-login keyboard/UI router.
                PlayerUiInputController legacyInput = prefabRoot.GetComponent<PlayerUiInputController>();
                if (legacyInput != null)
                    legacyInput.enabled = false;

                // -------- PHASE 3: BUILD THE CANONICAL GAMEPLAY ROOT --------
                Font font = GetBuiltinFont();
                GameObject visualRoot = NewUi(GameplayRootName, prefabRoot.transform);
                Stretch(visualRoot.GetComponent<RectTransform>());

                // EXTENSION POINT:
                // Add future authored gameplay modules HERE. Keep each module isolated in
                // its own BuildXxx method so a later rebuild remains deterministic/idempotent.
                BuildResourceHud(visualRoot.transform, font,
                    out Slider healthSlider, out Text healthText,
                    out Slider manaSlider, out Text manaText,
                    out Slider staminaSlider, out Text staminaText);

                BuildChat(visualRoot.transform, font,
                    out ScrollRect chatScroll,
                    out Text chatHistory,
                    out InputField chatInput,
                    out Button chatSend);

                BuildCombatTarget(visualRoot.transform, font,
                    out GameObject combatTargetRoot,
                    out Text combatTargetText,
                    out Slider combatTargetHealthSlider);

                BuildPauseMenu(visualRoot.transform, font,
                    out GameObject pauseRoot,
                    out Button resume,
                    out Button options,
                    out Button quit);

                // Existing Inventory is deliberately NOT rebuilt here.
                // Its PlayerInventoryShell and authored window are retained as-is.

                ClientGameplayUIRoot gameplay = prefabRoot.AddComponent<ClientGameplayUIRoot>();
                gameplay.ConfigureForEditor(
                    visualRoot,
                    healthSlider, healthText,
                    manaSlider, manaText,
                    staminaSlider, staminaText,
                    chatScroll, chatHistory, chatInput, chatSend,
                    combatTargetRoot, combatTargetText, combatTargetHealthSlider,
                    pauseRoot, resume, options, quit);

                // Rebuild the interaction PRESENTATION only. The controller still reuses the
                // existing target resolver, public action definitions, manager requests, and
                // network messages. Clear only its old authored visual references first.
                ResetInteractionPresentationReferences(interaction);
                interaction.BuildPresentationForEditor();

                // No managed gameplay window starts open.
                ClientUIPanelMarker[] markers = prefabRoot.GetComponentsInChildren<ClientUIPanelMarker>(true);
                for (int i = 0; i < markers.Length; ++i)
                {
                    ClientUIPanelMarker marker = markers[i];
                    if (marker != null && marker.ManagedWindow && marker.GameplayOnly)
                        marker.gameObject.SetActive(false);
                }

                if (inventoryWindow != null)
                    inventoryWindow.SetActive(false);
                if (settingsWindow != null)
                    settingsWindow.SetActive(false);
                combatTargetRoot.SetActive(false);
                pauseRoot.SetActive(false);
                visualRoot.SetActive(false);

                canonicalRoot.CaptureAuthoredReferencesFromHierarchy();

                EditorUtility.SetDirty(prefabRoot);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, PrefabPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log(
                    "[Gameplay UI V2] Canonical rebuild complete. " +
                    "Frontend preserved; Inventory preserved; Settings preserved when present; " +
                    "legacy post-login presentation archived inactive; new resources/chat/TPS target/interaction/ESC UI authored.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        // LEGACY/DEAD-END: menu registration intentionally disabled. Preserve method for rollback/reference.
        // [MenuItem("MMO Tools/UI/Gameplay V2/Validate Canonical Post-Login UI", priority = 2011)]
        public static void ValidateCanonical()
        {
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (prefabRoot == null)
                throw new InvalidOperationException($"Unable to load {PrefabPath}.");

            var errors = new List<string>();
            var warnings = new List<string>();

            try
            {
                Canvas canvas = prefabRoot.GetComponent<Canvas>();
                CanvasScaler scaler = prefabRoot.GetComponent<CanvasScaler>();
                GraphicRaycaster raycaster = prefabRoot.GetComponent<GraphicRaycaster>();
                if (canvas == null || !canvas.enabled) errors.Add("root Canvas is missing/disabled");
                if (scaler == null || !scaler.enabled) errors.Add("root CanvasScaler is missing/disabled");
                if (raycaster == null || !raycaster.enabled) errors.Add("root GraphicRaycaster is missing/disabled");

                ClientUIRoot root = prefabRoot.GetComponent<ClientUIRoot>();
                if (root == null) errors.Add("ClientUIRoot component is missing");

                ClientGameplayUIRoot gameplay = prefabRoot.GetComponent<ClientGameplayUIRoot>();
                if (gameplay == null) errors.Add("ClientGameplayUIRoot component is missing");
                else if (!gameplay.HasAuthoredBindings) errors.Add("ClientGameplayUIRoot has missing authored bindings");

                Transform gameplayVisual = prefabRoot.transform.Find(GameplayRootName);
                if (gameplayVisual == null) errors.Add($"{GameplayRootName} visual root is missing");
                else if (gameplayVisual.gameObject.activeSelf) errors.Add($"{GameplayRootName} must be authored inactive before world entry");

                Transform archive = prefabRoot.transform.Find(LegacyArchiveName);
                if (archive == null) errors.Add("legacy gameplay UI archive is missing");
                else if (archive.gameObject.activeSelf) errors.Add("legacy gameplay UI archive must stay inactive");

                PlayerUiInputController legacyInput = prefabRoot.GetComponent<PlayerUiInputController>();
                if (legacyInput != null && legacyInput.enabled)
                    errors.Add("legacy PlayerUiInputController is still enabled; V2 must be the sole post-login UI router");

                PlayerInventoryShell inventory = prefabRoot.GetComponent<PlayerInventoryShell>();
                if (inventory == null)
                {
                    errors.Add("working PlayerInventoryShell was removed");
                }
                else
                {
                    GameObject inventoryWindow = GetInventoryWindow(inventory);
                    if (inventoryWindow == null)
                        errors.Add("PlayerInventoryShell.windowRoot is missing");
                    else
                    {
                        if (IsDescendantOf(inventoryWindow.transform, archive))
                            errors.Add("Inventory was accidentally archived with legacy UI");
                        if (inventoryWindow.activeSelf)
                            errors.Add("Inventory window must be authored closed");
                    }
                }

                ClientInteractionUI interaction = prefabRoot.GetComponent<ClientInteractionUI>();
                if (interaction == null) errors.Add("ClientInteractionUI controller is missing");
                if (prefabRoot.transform.Find(InteractionRootName) == null)
                    errors.Add("rebuilt InteractionUIRoot is missing");

                GameObject settings = FindPanelObject(prefabRoot, ClientUIPanelId.SettingsWindow);
                if (settings == null)
                    warnings.Add("SettingsWindow was not present to preserve; ESC Options will report unavailable until Options is rebuilt");
                else if (IsDescendantOf(settings.transform, archive))
                    errors.Add("SettingsWindow was accidentally archived");

                ClientUIPanelMarker[] markers = prefabRoot.GetComponentsInChildren<ClientUIPanelMarker>(true);
                for (int i = 0; i < markers.Length; ++i)
                {
                    ClientUIPanelMarker marker = markers[i];
                    if (marker != null && marker.ManagedWindow && marker.GameplayOnly && marker.gameObject.activeSelf)
                        errors.Add($"gameplay managed window is authored open: {marker.PanelId} ({GetPath(marker.transform)})");
                }

                if (errors.Count == 0)
                {
                    Debug.Log(
                        warnings.Count == 0
                            ? "[Gameplay UI V2] PASSED canonical contract. Legacy UI is inactive and the new post-login root is the sole gameplay presentation path."
                            : $"[Gameplay UI V2] PASSED with {warnings.Count} warning(s):\n - {string.Join("\n - ", warnings)}");
                }
                else
                {
                    Debug.LogError(
                        "[Gameplay UI V2] FAILED canonical contract:\n - " +
                        string.Join("\n - ", errors) +
                        (warnings.Count > 0 ? "\nWarnings:\n - " + string.Join("\n - ", warnings) : string.Empty));
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private static void EnsureOneTimeBackup()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(BackupPath) != null)
                return;

            string folder = System.IO.Path.GetDirectoryName(BackupPath)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(folder) && !AssetDatabase.IsValidFolder(folder))
            {
                // Parent Prefabs folder already exists on the canonical project path; this
                // branch is here only to fail clearly if that assumption ever changes.
                throw new InvalidOperationException($"Backup folder does not exist: {folder}");
            }

            if (!AssetDatabase.CopyAsset(PrefabPath, BackupPath))
                throw new InvalidOperationException($"Could not create one-time backup at {BackupPath}.");

            AssetDatabase.SaveAssets();
            Debug.Log($"[Gameplay UI V2] Created one-time legacy prefab backup: {BackupPath}");
        }

        private static void RequireCanonicalRoot(
            GameObject prefabRoot,
            out ClientUIRoot root,
            out PlayerInventoryShell inventory,
            out ClientInteractionUI interaction)
        {
            root = prefabRoot.GetComponent<ClientUIRoot>();
            if (root == null)
                throw new InvalidOperationException("ClientUIRoot.prefab is missing ClientUIRoot.");

            if (prefabRoot.GetComponent<Canvas>() == null ||
                prefabRoot.GetComponent<CanvasScaler>() == null ||
                prefabRoot.GetComponent<GraphicRaycaster>() == null)
                throw new InvalidOperationException("ClientUIRoot must retain its canonical Canvas, CanvasScaler, and GraphicRaycaster.");

            inventory = prefabRoot.GetComponent<PlayerInventoryShell>();
            if (inventory == null)
                throw new InvalidOperationException("Existing PlayerInventoryShell is missing. The canonical rebuild will not replace Inventory.");

            interaction = prefabRoot.GetComponent<ClientInteractionUI>();
            if (interaction == null)
                throw new InvalidOperationException("Existing ClientInteractionUI controller is missing. The canonical rebuild reuses its gameplay/request path.");
        }

        private static GameObject GetInventoryWindow(PlayerInventoryShell inventory)
        {
            if (inventory == null)
                return null;

            SerializedObject serialized = new SerializedObject(inventory);
            SerializedProperty prop = serialized.FindProperty("windowRoot");
            return prop != null ? prop.objectReferenceValue as GameObject : null;
        }

        private static GameObject FindPanelObject(GameObject root, ClientUIPanelId id)
        {
            if (root == null)
                return null;
            ClientUIPanelMarker[] markers = root.GetComponentsInChildren<ClientUIPanelMarker>(true);
            for (int i = 0; i < markers.Length; ++i)
            {
                ClientUIPanelMarker marker = markers[i];
                if (marker != null && marker.PanelId == id)
                    return marker.gameObject;
            }
            return null;
        }

        private static void PreserveWindow(GameObject window, Transform preservedWindows)
        {
            if (window == null || preservedWindows == null)
                return;
            if (window.transform.parent == preservedWindows)
                return;
            window.transform.SetParent(preservedWindows, false);
            window.SetActive(false);
        }

        private static void ArchiveLegacyGroups(Transform root, Transform archive, Transform preservedWindows)
        {
            for (int i = 0; i < LegacyTopLevelGroups.Length; ++i)
            {
                Transform child = root.Find(LegacyTopLevelGroups[i]);
                if (child == null || child == archive || child == preservedWindows)
                    continue;
                child.SetParent(archive, false);
            }
        }

        /// <summary>
        /// Clears only ClientInteractionUI's old serialized presentation references so its
        /// established editor authoring method creates a fresh InteractionUIRoot. Gameplay
        /// state, target resolution, action definitions, and request methods are untouched.
        /// </summary>
        private static void ResetInteractionPresentationReferences(ClientInteractionUI interaction)
        {
            if (interaction == null)
                return;

            SerializedObject so = new SerializedObject(interaction);
            string[] objectFields =
            {
                "_uiRoot", "_menuLayer", "_menuPanel", "_menuCanvasGroup", "_dismissButton",
                "_targetNameText", "_targetKindText", "_pageText", "_detailTitleText",
                "_detailBodyText", "_detailStateText", "_centerButton", "_centerButtonText",
                "_consentLayer", "_consentTitleText", "_consentMessageText", "_consentCountdownText",
                "_consentAcceptButton", "_consentDeclineButton", "_consentCloseButton",
                "_sessionStrip", "_sessionText", "_sessionEndButton",
            };

            for (int i = 0; i < objectFields.Length; ++i)
            {
                SerializedProperty prop = so.FindProperty(objectFields[i]);
                if (prop != null)
                    prop.objectReferenceValue = null;
            }

            SerializedProperty slots = so.FindProperty("_slots");
            if (slots != null && slots.isArray)
                slots.ClearArray();

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(interaction);
        }

        // =====================================================================
        // MODULE: PLAYER RESOURCES
        // =====================================================================
        private static void BuildResourceHud(
            Transform parent,
            Font font,
            out Slider healthSlider,
            out Text healthText,
            out Slider manaSlider,
            out Text manaText,
            out Slider staminaSlider,
            out Text staminaText)
        {
            GameObject panel = NewImage("Resources", parent, new Color(0.025f, 0.03f, 0.04f, 0.86f), false);
            SetRect(panel.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(410f, 118f), new Vector2(20f, 292f), new Vector2(0f, 0f));

            healthSlider = NewResourceBar("Health", panel.transform, font, "HP", new Vector2(0f, 70f), new Color(0.68f, 0.16f, 0.16f, 1f), out healthText);
            manaSlider = NewResourceBar("Mana", panel.transform, font, "MP", new Vector2(0f, 35f), new Color(0.18f, 0.35f, 0.75f, 1f), out manaText);
            staminaSlider = NewResourceBar("Stamina", panel.transform, font, "STAM", new Vector2(0f, 0f), new Color(0.20f, 0.62f, 0.28f, 1f), out staminaText);
        }

        private static Slider NewResourceBar(string name, Transform parent, Font font, string prefix, Vector2 offset, Color fillColor, out Text label)
        {
            GameObject root = NewUi(name, parent);
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0f);
            rootRect.pivot = new Vector2(0.5f, 0f);
            rootRect.sizeDelta = new Vector2(382f, 28f);
            rootRect.anchoredPosition = new Vector2(offset.x, 10f + offset.y);

            Image background = root.AddComponent<Image>();
            background.color = new Color(0.08f, 0.09f, 0.11f, 0.96f);
            background.raycastTarget = false;

            Slider slider = root.AddComponent<Slider>();
            slider.interactable = false;
            slider.transition = Selectable.Transition.None;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;
            slider.direction = Slider.Direction.LeftToRight;

            GameObject fillArea = NewUi("Fill Area", root.transform);
            Stretch(fillArea.GetComponent<RectTransform>(), 2f, 2f, -2f, -2f);
            GameObject fill = NewImage("Fill", fillArea.transform, fillColor, false);
            Stretch(fill.GetComponent<RectTransform>());
            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.targetGraphic = background;

            label = NewText("Label", root.transform, font, prefix, 13, TextAnchor.MiddleLeft);
            Stretch(label.rectTransform, 10f, 0f, -8f, 0f);
            label.fontStyle = FontStyle.Bold;
            return slider;
        }

        // =====================================================================
        // MODULE: CHAT
        // =====================================================================
        private static void BuildChat(
            Transform parent,
            Font font,
            out ScrollRect scroll,
            out Text history,
            out InputField input,
            out Button send)
        {
            GameObject panel = NewImage("Chat", parent, new Color(0.018f, 0.022f, 0.03f, 0.78f), false);
            SetRect(panel.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(560f, 260f), new Vector2(20f, 20f), new Vector2(0f, 0f));

            GameObject viewport = NewUi("Viewport", panel.transform);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(10f, 52f);
            viewportRect.offsetMax = new Vector2(-10f, -10f);
            Image viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewportImage.raycastTarget = true;
            viewport.AddComponent<RectMask2D>();

            history = NewText("History", viewport.transform, font, string.Empty, 13, TextAnchor.UpperLeft);
            RectTransform historyRect = history.rectTransform;
            historyRect.anchorMin = new Vector2(0f, 1f);
            historyRect.anchorMax = new Vector2(1f, 1f);
            historyRect.pivot = new Vector2(0.5f, 1f);
            historyRect.anchoredPosition = Vector2.zero;
            historyRect.sizeDelta = Vector2.zero;
            history.horizontalOverflow = HorizontalWrapMode.Wrap;
            history.verticalOverflow = VerticalWrapMode.Overflow;
            ContentSizeFitter fitter = history.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll = panel.AddComponent<ScrollRect>();
            scroll.viewport = viewportRect;
            scroll.content = historyRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 18f;

            input = NewInputField("Input", panel.transform, font, "Press Enter to chat");
            RectTransform inputRect = input.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0f, 0f);
            inputRect.anchorMax = new Vector2(1f, 0f);
            inputRect.pivot = new Vector2(0.5f, 0f);
            inputRect.offsetMin = new Vector2(10f, 10f);
            inputRect.offsetMax = new Vector2(-92f, 44f);
            input.gameObject.AddComponent<ClientUiTextInputCapture>();

            send = NewButton("Send", panel.transform, font, "SEND");
            RectTransform sendRect = send.GetComponent<RectTransform>();
            sendRect.anchorMin = sendRect.anchorMax = new Vector2(1f, 0f);
            sendRect.pivot = new Vector2(1f, 0f);
            sendRect.sizeDelta = new Vector2(76f, 34f);
            sendRect.anchoredPosition = new Vector2(-10f, 10f);
        }

        // =====================================================================
        // MODULE: TPS COMBAT TARGET PRESENTATION
        // =====================================================================
        private static void BuildCombatTarget(Transform parent, Font font, out GameObject targetRoot, out Text targetText, out Slider targetHealthSlider)
        {
            targetRoot = NewImage("CombatTarget", parent, new Color(0.025f, 0.03f, 0.04f, 0.88f), false);
            SetRect(targetRoot.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(430f, 72f), new Vector2(0f, -38f), new Vector2(0.5f, 1f));
            targetText = NewText("TargetText", targetRoot.transform, font, string.Empty, 17, TextAnchor.MiddleCenter);
            SetRect(targetText.rectTransform, new Vector2(0.5f, 1f), new Vector2(402f, 34f), new Vector2(0f, -19f), new Vector2(0.5f, 0.5f));
            targetText.fontStyle = FontStyle.Bold;

            GameObject bar = NewImage("TargetHealthBar", targetRoot.transform, new Color(0.055f, 0.065f, 0.085f, 0.98f), false);
            SetRect(bar.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(402f, 16f), new Vector2(0f, 13f), new Vector2(0.5f, 0.5f));
            targetHealthSlider = bar.AddComponent<Slider>();
            targetHealthSlider.interactable = false;
            targetHealthSlider.transition = Selectable.Transition.None;
            targetHealthSlider.minValue = 0f;
            targetHealthSlider.maxValue = 1f;
            targetHealthSlider.value = 1f;

            GameObject fillArea = NewUi("FillArea", bar.transform);
            Stretch(fillArea.GetComponent<RectTransform>(), 2f, 2f, -2f, -2f);
            GameObject fill = NewImage("Fill", fillArea.transform, new Color(0.78f, 0.16f, 0.14f, 1f), false);
            Stretch(fill.GetComponent<RectTransform>());
            targetHealthSlider.fillRect = fill.GetComponent<RectTransform>();
            targetHealthSlider.targetGraphic = bar.GetComponent<Image>();
            targetRoot.SetActive(false);
        }

        // =====================================================================
        // MODULE: ESC MENU
        // =====================================================================
        private static void BuildPauseMenu(
            Transform parent,
            Font font,
            out GameObject pauseRoot,
            out Button resume,
            out Button options,
            out Button quit)
        {
            pauseRoot = NewUi("PauseMenu", parent);
            Stretch(pauseRoot.GetComponent<RectTransform>());

            GameObject dim = NewImage("Dim", pauseRoot.transform, new Color(0f, 0f, 0f, 0.58f), true);
            Stretch(dim.GetComponent<RectTransform>());

            GameObject panel = NewImage("Panel", pauseRoot.transform, new Color(0.03f, 0.035f, 0.05f, 0.98f), true);
            SetRect(panel.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(380f, 330f), Vector2.zero, new Vector2(0.5f, 0.5f));

            Text title = NewText("Title", panel.transform, font, "MENU", 24, TextAnchor.MiddleCenter);
            SetRect(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(340f, 52f), new Vector2(0f, -42f), new Vector2(0.5f, 0.5f));
            title.fontStyle = FontStyle.Bold;

            resume = NewButton("Resume", panel.transform, font, "RESUME");
            SetRect(resume.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(280f, 52f), new Vector2(0f, -112f), new Vector2(0.5f, 0.5f));
            options = NewButton("Options", panel.transform, font, "OPTIONS");
            SetRect(options.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(280f, 52f), new Vector2(0f, -178f), new Vector2(0.5f, 0.5f));
            quit = NewButton("Quit", panel.transform, font, "QUIT");
            SetRect(quit.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(280f, 52f), new Vector2(0f, -244f), new Vector2(0.5f, 0.5f));

            pauseRoot.SetActive(false);
        }

        // =====================================================================
        // SHARED AUTHORING HELPERS
        // =====================================================================
        private static Font GetBuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null)
                throw new InvalidOperationException("Unity built-in UI font is unavailable.");
            return font;
        }

        private static InputField NewInputField(string name, Transform parent, Font font, string placeholderText)
        {
            GameObject root = NewImage(name, parent, new Color(0.055f, 0.065f, 0.085f, 0.98f), true);
            InputField input = root.AddComponent<InputField>();
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 220;

            Text text = NewText("Text", root.transform, font, string.Empty, 13, TextAnchor.MiddleLeft);
            Stretch(text.rectTransform, 10f, 2f, -10f, -2f);
            text.raycastTarget = false;
            input.textComponent = text;

            Text placeholder = NewText("Placeholder", root.transform, font, placeholderText, 13, TextAnchor.MiddleLeft);
            Stretch(placeholder.rectTransform, 10f, 2f, -10f, -2f);
            placeholder.color = new Color(1f, 1f, 1f, 0.42f);
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.raycastTarget = false;
            input.placeholder = placeholder;
            return input;
        }

        private static Button NewButton(string name, Transform parent, Font font, string label)
        {
            GameObject root = NewImage(name, parent, new Color(0.10f, 0.13f, 0.18f, 0.98f), true);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = root.GetComponent<Image>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.80f, 0.80f, 0.80f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.75f);
            button.colors = colors;

            Text text = NewText("Label", root.transform, font, label, 14, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, 6f, 3f, -6f, -3f);
            text.fontStyle = FontStyle.Bold;
            return button;
        }

        private static GameObject NewUi(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject NewImage(string name, Transform parent, Color color, bool raycastTarget)
        {
            GameObject go = NewUi(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycastTarget;
            return go;
        }

        private static Text NewText(string name, Transform parent, Font font, string value, int size, TextAnchor anchor)
        {
            GameObject go = NewUi(name, parent);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.text = value ?? string.Empty;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static Transform GetOrCreateTopLevel(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
                return existing;
            return NewUi(name, parent).transform;
        }

        private static void RemoveTopLevel(Transform parent, string childName)
        {
            Transform child = parent.Find(childName);
            if (child != null)
                UnityEngine.Object.DestroyImmediate(child.gameObject, true);
        }

        private static void SetRect(RectTransform rect, Vector2 anchor, Vector2 size, Vector2 position, Vector2 pivot)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void Stretch(RectTransform rect, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
        {
            if (rect == null)
                return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(right, top);
        }

        private static bool IsDescendantOf(Transform candidate, Transform ancestor)
        {
            if (candidate == null || ancestor == null)
                return false;
            Transform current = candidate;
            while (current != null)
            {
                if (current == ancestor)
                    return true;
                current = current.parent;
            }
            return false;
        }

        private static string GetPath(Transform t)
        {
            if (t == null)
                return "<null>";
            string path = t.name;
            Transform p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }
}
#endif
