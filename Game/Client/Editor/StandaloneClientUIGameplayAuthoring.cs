#if UNITY_EDITOR
using System;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Standalone;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Authored gameplay HUD pass for StandaloneClientUI.prefab.
    ///
    /// Reuses ClientGameplayUIRoot for owner resources, pushed chat, combat-target
    /// presentation, world-entry visibility, and pause-menu behavior. The existing
    /// StandaloneCharacterInventoryWindow remains the combined Character/Inventory
    /// window and the existing HUD-owned StandaloneHudTooltipPanel is preserved.
    /// No runtime UI hierarchy or new network message is introduced.
    /// </summary>
    public static class StandaloneClientUIGameplayAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color HudPanel = new Color32(15, 18, 24, 225);
        private static readonly Color HudPanelRaised = new Color32(26, 31, 40, 245);
        private static readonly Color Field = new Color32(10, 13, 18, 238);
        private static readonly Color Accent = new Color32(55, 119, 190, 255);
        private static readonly Color Border = new Color32(67, 78, 96, 255);
        private static readonly Color TextPrimary = new Color32(239, 242, 246, 255);
        private static readonly Color TextSecondary = new Color32(167, 178, 194, 255);
        private static readonly Color Health = new Color32(150, 49, 55, 255);
        private static readonly Color Mana = new Color32(48, 103, 164, 255);
        private static readonly Color Stamina = new Color32(77, 139, 89, 255);

        // LEGACY/DEAD-END: menu registration intentionally disabled. Preserve method for rollback/reference.
        // [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Gameplay V2.03")]
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
                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                            Resources.GetBuiltinResource<Font>("Arial.ttf");

                StandaloneClientUIRoot standaloneRoot = root.GetComponent<StandaloneClientUIRoot>();
                if (standaloneRoot == null)
                    throw new InvalidOperationException("StandaloneClientUIRoot is missing from the prefab root.");

                Transform gameplay = root.transform.Find("Gameplay");
                if (gameplay == null)
                    throw new InvalidOperationException("StandaloneClientUI.prefab is missing Gameplay.");

                Transform hud = gameplay.Find("HUD");
                Transform windows = gameplay.Find("Windows");
                Transform overlays = gameplay.Find("Overlays");
                if (hud == null || windows == null || overlays == null)
                    throw new InvalidOperationException("Gameplay must contain HUD, Windows, and Overlays authored layers.");

                StandaloneCharacterInventoryWindow characterInventory =
                    root.GetComponentInChildren<StandaloneCharacterInventoryWindow>(true);
                StandaloneHudTooltipPanel tooltip =
                    root.GetComponentInChildren<StandaloneHudTooltipPanel>(true);
                if (characterInventory == null || tooltip == null)
                    throw new InvalidOperationException("The existing Character/Inventory window and HUD tooltip are required before Gameplay V2.03.");

                // Preserve the existing HUD-owned tooltip because Character/Inventory already
                // holds a serialized reference to this exact component.
                tooltip.transform.SetParent(gameplay, false);
                ClearChildren(hud);
                tooltip.transform.SetParent(hud, false);
                LayoutExistingTooltip(tooltip);

                BuildOwnerResources(hud, font,
                    out Slider healthSlider, out Text healthText,
                    out Slider manaSlider, out Text manaText,
                    out Slider staminaSlider, out Text staminaText);

                BuildChat(hud, font,
                    out ScrollRect chatScroll,
                    out Text chatHistory,
                    out InputField chatInput,
                    out Button chatSend);

                BuildCombatTarget(hud, font, out GameObject combatTargetRoot, out Text combatTargetText, out Slider combatTargetHealthSlider);
                BuildActionBar(hud, font);

                ClientGameplayUIRoot gameplayController = root.GetComponent<ClientGameplayUIRoot>();
                if (gameplayController == null)
                    gameplayController = root.AddComponent<ClientGameplayUIRoot>();

                BuildUtilityDock(hud, font, standaloneRoot, gameplayController);
                BuildPauseMenu(overlays, font,
                    out GameObject pauseMenuRoot,
                    out Button resumeButton,
                    out Button optionsButton,
                    out Button quitButton);

                gameplayController.ConfigureForEditor(
                    gameplay.gameObject,
                    healthSlider,
                    healthText,
                    manaSlider,
                    manaText,
                    staminaSlider,
                    staminaText,
                    chatScroll,
                    chatHistory,
                    chatInput,
                    chatSend,
                    combatTargetRoot,
                    combatTargetText,
                    combatTargetHealthSlider,
                    pauseMenuRoot,
                    resumeButton,
                    optionsButton,
                    quitButton);

                standaloneRoot.ConfigureForEditor(gameplay.gameObject, characterInventory, tooltip);
                gameplay.gameObject.SetActive(false);
                pauseMenuRoot.SetActive(false);
                combatTargetRoot.SetActive(false);
                tooltip.gameObject.SetActive(false);

                EditorUtility.SetDirty(gameplayController);
                EditorUtility.SetDirty(standaloneRoot);
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
            Debug.Log(
                "[StandaloneUI] Gameplay V2.03 authored. Existing ClientGameplayUIRoot now owns resource/chat/target/pause presentation; " +
                "the existing combined Character/Inventory window and fixed HUD tooltip are reused. No new network messages were added.");
        }

        private static void BuildOwnerResources(
            Transform hud,
            Font font,
            out Slider healthSlider,
            out Text healthText,
            out Slider manaSlider,
            out Text manaText,
            out Slider staminaSlider,
            out Text staminaText)
        {
            GameObject panel = PanelObject("PlayerResources", hud, HudPanel);
            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(24f, -24f);
            rect.sizeDelta = new Vector2(360f, 132f);
            AddOutline(panel);

            Text title = TextObject("Title", panel.transform, font, 15, TextPrimary, TextAnchor.MiddleLeft, "PLAYER");
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -8f), new Vector2(-28f, 24f));

            healthSlider = ResourceBar(panel.transform, font, "Health", "HP", Health, 82f, out healthText);
            manaSlider = ResourceBar(panel.transform, font, "Mana", "MP", Mana, 50f, out manaText);
            staminaSlider = ResourceBar(panel.transform, font, "Stamina", "STAM", Stamina, 18f, out staminaText);
        }

        private static Slider ResourceBar(Transform parent, Font font, string name, string prefix, Color fillColor, float y, out Text valueText)
        {
            GameObject root = PanelObject(name, parent, Field);
            RectTransform rr = root.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0f, 1f);
            rr.pivot = new Vector2(0f, 1f);
            rr.anchoredPosition = new Vector2(14f, -y);
            rr.sizeDelta = new Vector2(332f, 24f);

            Slider slider = root.AddComponent<Slider>();
            slider.interactable = false;
            slider.transition = Selectable.Transition.None;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;

            GameObject fillArea = Node("FillArea", root.transform);
            Stretch(fillArea.GetComponent<RectTransform>());
            RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
            fillAreaRect.offsetMin = new Vector2(2f, 2f);
            fillAreaRect.offsetMax = new Vector2(-2f, -2f);

            Image fill = ImageObject("Fill", fillArea.transform, fillColor);
            RectTransform fillRect = fill.rectTransform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            slider.fillRect = fillRect;
            slider.targetGraphic = root.GetComponent<Image>();

            valueText = TextObject("Value", root.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, prefix);
            Stretch(valueText.rectTransform);
            return slider;
        }

        private static void BuildChat(
            Transform hud,
            Font font,
            out ScrollRect chatScroll,
            out Text history,
            out InputField input,
            out Button send)
        {
            GameObject root = PanelObject("Chat", hud, HudPanel);
            RectTransform rr = root.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0f, 0f);
            rr.pivot = new Vector2(0f, 0f);
            rr.anchoredPosition = new Vector2(24f, 24f);
            rr.sizeDelta = new Vector2(540f, 260f);
            AddOutline(root);

            Text title = TextObject("Title", root.transform, font, 13, TextSecondary, TextAnchor.MiddleLeft, "CHAT");
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -6f), new Vector2(-20f, 22f));

            GameObject scrollRoot = PanelObject("History", root.transform, Field);
            RectTransform sr = scrollRoot.GetComponent<RectTransform>();
            sr.anchorMin = new Vector2(0f, 0f);
            sr.anchorMax = new Vector2(1f, 1f);
            sr.offsetMin = new Vector2(10f, 52f);
            sr.offsetMax = new Vector2(-10f, -32f);
            chatScroll = scrollRoot.AddComponent<ScrollRect>();
            chatScroll.horizontal = false;
            chatScroll.vertical = true;
            chatScroll.movementType = ScrollRect.MovementType.Clamped;
            chatScroll.scrollSensitivity = 24f;

            GameObject viewport = Node("Viewport", scrollRoot.transform, typeof(Image), typeof(RectMask2D));
            Stretch(viewport.GetComponent<RectTransform>());
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.GetComponent<Image>().raycastTarget = true;
            chatScroll.viewport = viewport.GetComponent<RectTransform>();

            history = TextObject("HistoryText", viewport.transform, font, 13, TextPrimary, TextAnchor.LowerLeft, string.Empty);
            RectTransform hr = history.rectTransform;
            hr.anchorMin = new Vector2(0f, 0f);
            hr.anchorMax = new Vector2(1f, 0f);
            hr.pivot = new Vector2(0.5f, 0f);
            hr.anchoredPosition = Vector2.zero;
            hr.sizeDelta = new Vector2(0f, 24f);
            history.horizontalOverflow = HorizontalWrapMode.Wrap;
            history.verticalOverflow = VerticalWrapMode.Overflow;
            ContentSizeFitter fitter = history.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            chatScroll.content = hr;

            input = InputFieldObject("Input", root.transform, font, "Type message or /help");
            RectTransform ir = input.GetComponent<RectTransform>();
            ir.anchorMin = new Vector2(0f, 0f);
            ir.anchorMax = new Vector2(1f, 0f);
            ir.pivot = new Vector2(0.5f, 0f);
            ir.offsetMin = new Vector2(10f, 10f);
            ir.offsetMax = new Vector2(-78f, 42f);

            send = ButtonObject("Send", root.transform, font, "SEND", Accent);
            RectTransform br = send.GetComponent<RectTransform>();
            br.anchorMin = br.anchorMax = new Vector2(1f, 0f);
            br.pivot = new Vector2(1f, 0f);
            br.anchoredPosition = new Vector2(-10f, 10f);
            br.sizeDelta = new Vector2(62f, 32f);
        }

        private static void BuildCombatTarget(Transform hud, Font font, out GameObject root, out Text text, out Slider healthSlider)
        {
            root = PanelObject("CombatTarget", hud, HudPanelRaised);
            RectTransform rr = root.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 1f);
            rr.pivot = new Vector2(0.5f, 1f);
            rr.anchoredPosition = new Vector2(0f, -24f);
            rr.sizeDelta = new Vector2(430f, 72f);
            AddOutline(root);

            text = TextObject("TargetText", root.transform, font, 15, TextPrimary, TextAnchor.MiddleCenter, "TARGET");
            SetRect(text.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -8f), new Vector2(-28f, 30f));

            GameObject bar = PanelObject("TargetHealthBar", root.transform, Field);
            RectTransform br = bar.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0f, 0f);
            br.anchorMax = new Vector2(1f, 0f);
            br.pivot = new Vector2(0.5f, 0f);
            br.offsetMin = new Vector2(14f, 9f);
            br.offsetMax = new Vector2(-14f, 25f);

            healthSlider = bar.AddComponent<Slider>();
            healthSlider.interactable = false;
            healthSlider.transition = Selectable.Transition.None;
            healthSlider.minValue = 0f;
            healthSlider.maxValue = 1f;
            healthSlider.value = 1f;

            GameObject fillArea = Node("FillArea", bar.transform);
            Stretch(fillArea.GetComponent<RectTransform>());
            RectTransform far = fillArea.GetComponent<RectTransform>();
            far.offsetMin = new Vector2(2f, 2f);
            far.offsetMax = new Vector2(-2f, -2f);
            Image fill = ImageObject("Fill", fillArea.transform, Health);
            Stretch(fill.rectTransform);
            healthSlider.fillRect = fill.rectTransform;
            healthSlider.targetGraphic = bar.GetComponent<Image>();
        }

        private static void BuildActionBar(Transform hud, Font font)
        {
            GameObject bar = PanelObject("ActionBar", hud, HudPanel);
            RectTransform rr = bar.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 0f);
            rr.pivot = new Vector2(0.5f, 0f);
            rr.anchoredPosition = new Vector2(0f, 24f);
            rr.sizeDelta = new Vector2(610f, 76f);
            AddOutline(bar);

            HorizontalLayoutGroup layout = bar.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 7f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlHeight = false;
            layout.childControlWidth = false;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = false;

            for (int i = 0; i < 8; ++i)
            {
                GameObject slot = PanelObject($"ActionSlot_{i + 1}", bar.transform, HudPanelRaised);
                RectTransform slotRect = slot.GetComponent<RectTransform>();
                slotRect.sizeDelta = new Vector2(66f, 56f);
                Text hotkey = TextObject("Hotkey", slot.transform, font, 12, TextSecondary, TextAnchor.UpperLeft, (i + 1).ToString());
                SetOffsets(hotkey.rectTransform, 5f, 5f, 5f, 4f);
            }
        }

        private static void BuildUtilityDock(Transform hud, Font font, StandaloneClientUIRoot standaloneRoot, ClientGameplayUIRoot gameplayController)
        {
            GameObject dock = PanelObject("UtilityDock", hud, HudPanel);
            RectTransform rr = dock.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(1f, 0f);
            rr.pivot = new Vector2(1f, 0f);
            rr.anchoredPosition = new Vector2(-24f, 24f);
            rr.sizeDelta = new Vector2(430f, 54f);
            AddOutline(dock);

            HorizontalLayoutGroup layout = dock.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 6, 6);
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlHeight = false;
            layout.childControlWidth = false;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = false;

            Button character = ButtonObject("CharacterInventory", dock.transform, font, "CHARACTER / INVENTORY", HudPanelRaised);
            character.GetComponent<RectTransform>().sizeDelta = new Vector2(190f, 40f);
            UnityEventTools.AddPersistentListener(character.onClick, standaloneRoot.ToggleCharacterInventory);

            Button skills = ButtonObject("Skills", dock.transform, font, "SKILLS", HudPanelRaised);
            skills.GetComponent<RectTransform>().sizeDelta = new Vector2(72f, 40f);
            skills.interactable = false;

            Button map = ButtonObject("Map", dock.transform, font, "MAP", HudPanelRaised);
            map.GetComponent<RectTransform>().sizeDelta = new Vector2(62f, 40f);
            map.interactable = false;

            Button menu = ButtonObject("Menu", dock.transform, font, "MENU", HudPanelRaised);
            menu.GetComponent<RectTransform>().sizeDelta = new Vector2(72f, 40f);
            UnityEventTools.AddPersistentListener(menu.onClick, gameplayController.TogglePauseMenu);
        }

        private static void BuildPauseMenu(
            Transform overlays,
            Font font,
            out GameObject root,
            out Button resume,
            out Button options,
            out Button quit)
        {
            Transform old = overlays.Find("PauseMenu");
            if (old != null)
                UnityEngine.Object.DestroyImmediate(old.gameObject);

            root = PanelObject("PauseMenu", overlays, new Color32(0, 0, 0, 150));
            Stretch(root.GetComponent<RectTransform>());

            GameObject frame = PanelObject("Frame", root.transform, HudPanelRaised);
            RectTransform fr = frame.GetComponent<RectTransform>();
            fr.anchorMin = fr.anchorMax = new Vector2(0.5f, 0.5f);
            fr.pivot = new Vector2(0.5f, 0.5f);
            fr.anchoredPosition = Vector2.zero;
            fr.sizeDelta = new Vector2(380f, 330f);
            AddOutline(frame);

            Text title = TextObject("Title", frame.transform, font, 26, TextPrimary, TextAnchor.MiddleCenter, "MENU");
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(0f, 44f));

            resume = ButtonObject("Resume", frame.transform, font, "RESUME", Accent);
            SetRect(resume.GetComponent<RectTransform>(), new Vector2(0.12f, 0.60f), new Vector2(0.88f, 0.73f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            options = ButtonObject("Options", frame.transform, font, "OPTIONS", HudPanel);
            SetRect(options.GetComponent<RectTransform>(), new Vector2(0.12f, 0.42f), new Vector2(0.88f, 0.55f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            quit = ButtonObject("Quit", frame.transform, font, "QUIT GAME", HudPanel);
            SetRect(quit.GetComponent<RectTransform>(), new Vector2(0.12f, 0.24f), new Vector2(0.88f, 0.37f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        }

        private static void LayoutExistingTooltip(StandaloneHudTooltipPanel tooltip)
        {
            RectTransform rr = tooltip.transform as RectTransform;
            if (rr == null)
                return;
            rr.anchorMin = rr.anchorMax = new Vector2(1f, 0.5f);
            rr.pivot = new Vector2(1f, 0.5f);
            rr.anchoredPosition = new Vector2(-24f, 80f);
            rr.sizeDelta = new Vector2(350f, 230f);
            Image image = tooltip.GetComponent<Image>();
            if (image != null)
            {
                image.color = HudPanelRaised;
                image.raycastTarget = false;
            }
        }

        private static void ClearChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; --i)
                UnityEngine.Object.DestroyImmediate(root.GetChild(i).gameObject);
        }

        private static InputField InputFieldObject(string name, Transform parent, Font font, string placeholderValue)
        {
            GameObject root = PanelObject(name, parent, Field);
            InputField input = root.AddComponent<InputField>();
            input.targetGraphic = root.GetComponent<Image>();
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 256;

            Text placeholder = TextObject("Placeholder", root.transform, font, 13, new Color32(108, 118, 133, 255), TextAnchor.MiddleLeft, placeholderValue);
            SetOffsets(placeholder.rectTransform, 10f, 10f, 3f, 3f);
            Text value = TextObject("Text", root.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, string.Empty);
            value.supportRichText = false;
            SetOffsets(value.rectTransform, 10f, 10f, 3f, 3f);
            input.placeholder = placeholder;
            input.textComponent = value;
            return input;
        }

        private static Button ButtonObject(string name, Transform parent, Font font, string label, Color normal)
        {
            GameObject root = PanelObject(name, parent, normal);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = root.GetComponent<Image>();
            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = Accent;
            colors.pressedColor = new Color32(40, 84, 129, 255);
            colors.selectedColor = Accent;
            colors.disabledColor = new Color(normal.r, normal.g, normal.b, 0.35f);
            button.colors = colors;
            Text text = TextObject("Text", root.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, label);
            Stretch(text.rectTransform);
            return button;
        }

        private static GameObject PanelObject(string name, Transform parent, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = color.a > 0.01f;
            return go;
        }

        private static GameObject Node(string name, Transform parent, params Type[] extraTypes)
        {
            Type[] components = new Type[(extraTypes?.Length ?? 0) + 1];
            components[0] = typeof(RectTransform);
            if (extraTypes != null)
                Array.Copy(extraTypes, 0, components, 1, extraTypes.Length);
            GameObject go = new GameObject(name, components);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Image ImageObject(string name, Transform parent, Color color)
        {
            GameObject go = PanelObject(name, parent, color);
            return go.GetComponent<Image>();
        }

        private static Text TextObject(string name, Transform parent, Font font, int size, Color color, TextAnchor alignment, string value)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.text = value;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static void AddOutline(GameObject go)
        {
            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetOffsets(RectTransform rect, float left, float right, float bottom, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
        }
    }
}
#endif
