#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Root;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.SocialEconomy;
using Game.Client.UI.Standalone;
using Player.Client;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Editor-only authored Gameplay Flow V2.04 upgrade.
    /// The resulting StandaloneClientUI.prefab is ordinary editable Unity UI. Runtime code
    /// binds caches/events/requests only; it does not construct these windows or HUD surfaces.
    /// </summary>
    public static class StandaloneClientUIGameplayFlowAuthoring
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

        private static readonly PlayerControlAction[] ControlRows =
        {
            PlayerControlAction.MoveForward,
            PlayerControlAction.MoveBackward,
            PlayerControlAction.MoveLeft,
            PlayerControlAction.MoveRight,
            PlayerControlAction.Jump,
            PlayerControlAction.Sprint,
            PlayerControlAction.Interact,
            PlayerControlAction.ToggleCombatStance,
            PlayerControlAction.Crouch,
            PlayerControlAction.OpenInventory,
            PlayerControlAction.ReleaseCursor,
            PlayerControlAction.OpenSkills,
            PlayerControlAction.OpenMap,
            PlayerControlAction.OpenSocial,
            PlayerControlAction.OpenMenu,
            PlayerControlAction.FocusChat,
            PlayerControlAction.OpenEmotes,
            PlayerControlAction.ReloadOrRespawn,
            PlayerControlAction.PickupNearest,
            PlayerControlAction.InteractionAction2,
            PlayerControlAction.InteractionAction3,
            PlayerControlAction.InteractionAction4,
            PlayerControlAction.DropSelected,
            PlayerControlAction.HotbarSlot1,
            PlayerControlAction.HotbarSlot2,
            PlayerControlAction.HotbarSlot3,
            PlayerControlAction.HotbarSlot4,
            PlayerControlAction.HotbarSlot5,
            PlayerControlAction.HotbarSlot6,
            PlayerControlAction.HotbarSlot7,
            PlayerControlAction.HotbarSlot8,
            PlayerControlAction.HotbarSlot9,
            PlayerControlAction.HotbarSlot10,
        };

        // LEGACY/DEAD-END: menu registration intentionally disabled. Preserve method for rollback/reference.
        // [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Gameplay Flow V2.04")]
        [MenuItem("MMO Tools/UI/Standalone UI/Repair Target Health Bar V2.12")]
        public static void RepairTargetHealthBar()
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
                ClientGameplayUIRoot gameplayController = root.GetComponent<ClientGameplayUIRoot>();
                if (gameplayController == null)
                    throw new InvalidOperationException("ClientGameplayUIRoot is missing from StandaloneClientUI.prefab.");

                Transform gameplay = root.transform.Find("Gameplay");
                Transform hud = gameplay != null ? gameplay.Find("HUD") : null;
                Transform combatTarget = hud != null ? hud.Find("CombatTarget") : null;
                if (combatTarget == null)
                    throw new InvalidOperationException("Current StandaloneClientUI.prefab is missing Gameplay/HUD/CombatTarget.");

                Slider healthSlider = combatTarget.GetComponentInChildren<Slider>(true);
                Transform existing = combatTarget.Find("TargetHealthBar");
                if (healthSlider == null || existing == null)
                {
                    if (existing != null)
                        UnityEngine.Object.DestroyImmediate(existing.gameObject);

                    GameObject bar = PanelObject("TargetHealthBar", combatTarget, Field);
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

                RectTransform targetRect = combatTarget.GetComponent<RectTransform>();
                if (targetRect != null && targetRect.sizeDelta.y < 72f)
                    targetRect.sizeDelta = new Vector2(targetRect.sizeDelta.x, 72f);

                SerializedObject serialized = new SerializedObject(gameplayController);
                SerializedProperty property = serialized.FindProperty("combatTargetHealthSlider");
                if (property == null)
                    throw new InvalidOperationException("ClientGameplayUIRoot.combatTargetHealthSlider serialized field was not found.");
                property.objectReferenceValue = healthSlider;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                EditorUtility.SetDirty(gameplayController);
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
            Debug.Log("[StandaloneUI] Target Health Bar V2.12 repaired on the current StandaloneClientUI.prefab. No other HUD/windows were rebuilt.");
        }

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
                    throw new InvalidOperationException("Character/Inventory and HUD tooltip must already exist before V2.04.");

                ClientGameplayUIRoot gameplayController = root.GetComponent<ClientGameplayUIRoot>();
                if (gameplayController == null)
                    gameplayController = root.AddComponent<ClientGameplayUIRoot>();

                // Preserve the exact shared tooltip component while rebuilding the HUD around it.
                tooltip.transform.SetParent(gameplay, false);
                ClearChildren(hud);
                tooltip.transform.SetParent(hud, false);
                LayoutExistingTooltip(tooltip);

                BuildOwnerResources(hud, font,
                    out Slider healthSlider, out Text healthText,
                    out Slider manaSlider, out Text manaText,
                    out Slider staminaSlider, out Text staminaText);
                BuildChat(hud, font, out ScrollRect chatScroll, out Text chatHistory, out InputField chatInput, out Button chatSend);
                BuildCombatTarget(hud, font, out GameObject combatTargetRoot, out Text combatTargetText, out Slider combatTargetHealthSlider);
                GameObject explorationDot = BuildExplorationDot(hud);
                Text[] hotbarBindingTexts = BuildActionBar(hud, font);

                ClearWindowsExcept(windows, characterInventory.transform);
                ClearChildren(overlays);
                StandaloneDragGhost dragGhost = BuildDragGhost(overlays);
                RebuildCharacterInventory(characterInventory, font, tooltip, dragGhost);
                StandaloneSkillsWindow skills = BuildSkillsWindow(windows, font);
                GameObject map = BuildSimpleWindow(windows, font, "MapWindow", "MAP", "World map presentation will bind here.", standaloneRoot.CloseMap);
                StandaloneSocialEconomyUI socialEconomy = BuildSocialEconomyUi(
                    windows, hud, overlays, font, standaloneRoot,
                    out GameObject social, out GameObject storage,
                    out GameObject partyHud, out GameObject friendInvitePopup, out GameObject partyInvitePopup);
                GameObject emotes = BuildEmotesWindow(windows, font, standaloneRoot);
                StandaloneControlsWindow controls = BuildControlsWindow(windows, font);
                StandaloneTradeUI trade = BuildTradeWindow(windows, font);
                PlayerLootShell loot = BuildLootWindow(windows, font, standaloneRoot);

                BuildUtilityDock(hud, font, standaloneRoot, gameplayController, tooltip);
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
                    quitButton,
                    explorationDot,
                    hotbarBindingTexts);

                standaloneRoot.ConfigureForEditor(
                    gameplay.gameObject,
                    characterInventory,
                    skills,
                    map,
                    social,
                    emotes,
                    controls,
                    trade,
                    socialEconomy,
                    tooltip,
                    loot);

                characterInventory.gameObject.SetActive(false);
                skills.gameObject.SetActive(false);
                map.SetActive(false);
                social.SetActive(false);
                storage.SetActive(false);
                partyHud.SetActive(false);
                friendInvitePopup.SetActive(false);
                partyInvitePopup.SetActive(false);
                loot.gameObject.SetActive(false);
                emotes.SetActive(false);
                controls.gameObject.SetActive(false);
                trade.gameObject.SetActive(false);
                pauseMenuRoot.SetActive(false);
                combatTargetRoot.SetActive(false);
                explorationDot.SetActive(false);
                dragGhost.gameObject.SetActive(false);
                tooltip.gameObject.SetActive(false);
                gameplay.gameObject.SetActive(false);

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
            Debug.Log("[StandaloneUI] Gameplay Flow V2.04 authored: compact cached Inventory/Equipment, drag/drop, HP/Mana/Stamina HUD, chat capture, Party/Friends/Storage owner-state UI, non-combat dot, quick links, Skills, Emotes, Trade, and local Player Config controls.");
        }

        private static void RebuildCharacterInventory(
            StandaloneCharacterInventoryWindow controller,
            Font font,
            StandaloneHudTooltipPanel tooltip,
            StandaloneDragGhost dragGhost)
        {
            Transform root = controller.transform;
            ClearChildren(root);
            RectTransform wr = root as RectTransform;
            wr.anchorMin = wr.anchorMax = new Vector2(0.5f, 0.5f);
            wr.pivot = new Vector2(0.5f, 0.5f);
            wr.anchoredPosition = Vector2.zero;
            wr.sizeDelta = new Vector2(1120f, 670f);
            Image rootImage = controller.GetComponent<Image>();
            if (rootImage != null) rootImage.color = new Color32(20, 22, 28, 242);

            GameObject header = PanelObject("Header", root, HudPanelRaised);
            SetRect(header.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(0f, 54f));
            Text title = TextObject("Title", header.transform, font, 21, TextPrimary, TextAnchor.MiddleLeft, "CHARACTER / INVENTORY");
            SetOffsets(title.rectTransform, 16f, 70f, 0f, 0f);
            Button close = ButtonObject("CloseButton", header.transform, font, "X", HudPanel);
            RectTransform cr = close.GetComponent<RectTransform>();
            cr.anchorMin = cr.anchorMax = new Vector2(1f, 0.5f);
            cr.pivot = new Vector2(1f, 0.5f);
            cr.anchoredPosition = new Vector2(-8f, 0f);
            cr.sizeDelta = new Vector2(44f, 38f);

            GameObject left = PanelObject("CharacterPanel", root, HudPanel);
            RectTransform lr = left.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0f, 0f);
            lr.anchorMax = new Vector2(0.36f, 1f);
            lr.offsetMin = new Vector2(12f, 12f);
            lr.offsetMax = new Vector2(-6f, -66f);
            AddOutline(left);

            Text charName = TextObject("CharacterName", left.transform, font, 22, TextPrimary, TextAnchor.UpperLeft, "Character");
            SetRect(charName.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -12f), new Vector2(-28f, 30f));
            Text level = TextObject("Level", left.transform, font, 14, TextSecondary, TextAnchor.UpperLeft, "Level --");
            SetRect(level.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -44f), new Vector2(-28f, 22f));
            Text summary = TextObject("Summary", left.transform, font, 13, TextSecondary, TextAnchor.UpperLeft, "Inventory not hydrated");
            SetRect(summary.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -70f), new Vector2(-28f, 22f));

            Text equipmentHeading = TextObject("EquipmentHeading", left.transform, font, 15, TextPrimary, TextAnchor.MiddleLeft, "EQUIPMENT");
            SetRect(equipmentHeading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -108f), new Vector2(-28f, 28f));

            GameObject equipmentArea = PanelObject("EquipmentArea", left.transform, Field);
            RectTransform ear = equipmentArea.GetComponent<RectTransform>();
            ear.anchorMin = new Vector2(0f, 0f);
            ear.anchorMax = new Vector2(1f, 1f);
            ear.offsetMin = new Vector2(12f, 12f);
            ear.offsetMax = new Vector2(-12f, -140f);
            GameObject equipmentContentGo = Node("Content", equipmentArea.transform);
            RectTransform equipmentContent = equipmentContentGo.GetComponent<RectTransform>();
            Stretch(equipmentContent);
            equipmentContent.offsetMin = new Vector2(8f, 8f);
            equipmentContent.offsetMax = new Vector2(-8f, -8f);
            GridLayoutGroup eqGrid = equipmentContentGo.AddComponent<GridLayoutGroup>();
            eqGrid.cellSize = new Vector2(174f, 52f);
            eqGrid.spacing = new Vector2(7f, 7f);
            eqGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            eqGrid.constraintCount = 2;
            eqGrid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            eqGrid.childAlignment = TextAnchor.UpperLeft;
            StandaloneEquipmentSlotView eqTemplate = CreateEquipmentSlotTemplate(equipmentContent, font);
            eqTemplate.gameObject.SetActive(false);

            GameObject right = PanelObject("InventoryPanel", root, HudPanel);
            RectTransform rr = right.GetComponent<RectTransform>();
            rr.anchorMin = new Vector2(0.36f, 0f);
            rr.anchorMax = new Vector2(1f, 1f);
            rr.offsetMin = new Vector2(6f, 12f);
            rr.offsetMax = new Vector2(-12f, -66f);
            AddOutline(right);

            Text inventoryHeading = TextObject("InventoryHeading", right.transform, font, 17, TextPrimary, TextAnchor.MiddleLeft, "INVENTORY");
            SetRect(inventoryHeading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -10f), new Vector2(-28f, 32f));

            GameObject invScrollGo = ScrollArea("InventoryScroll", right.transform, out ScrollRect inventoryScroll, out RectTransform inventoryContent);
            RectTransform isr = invScrollGo.GetComponent<RectTransform>();
            isr.anchorMin = Vector2.zero;
            isr.anchorMax = Vector2.one;
            isr.offsetMin = new Vector2(14f, 50f);
            isr.offsetMax = new Vector2(-14f, -48f);
            GridLayoutGroup invGrid = inventoryContent.gameObject.AddComponent<GridLayoutGroup>();
            invGrid.cellSize = new Vector2(92f, 92f);
            invGrid.spacing = new Vector2(8f, 8f);
            invGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            invGrid.constraintCount = 6;
            invGrid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            invGrid.childAlignment = TextAnchor.UpperLeft;
            ContentSizeFitter invFitter = inventoryContent.gameObject.AddComponent<ContentSizeFitter>();
            invFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            StandaloneInventorySlotView invTemplate = CreateInventorySlotTemplate(inventoryContent, font);
            invTemplate.gameObject.SetActive(false);

            Text feedback = TextObject("Feedback", right.transform, font, 13, new Color32(230, 193, 107, 255), TextAnchor.MiddleLeft, string.Empty);
            SetRect(feedback.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(14f, 12f), new Vector2(-28f, 28f));

            controller.ConfigureForEditor(
                root.gameObject,
                close,
                charName,
                level,
                summary,
                feedback,
                inventoryScroll,
                inventoryContent,
                invTemplate,
                equipmentContent,
                eqTemplate,
                tooltip,
                dragGhost);
        }

        private static StandaloneInventorySlotView CreateInventorySlotTemplate(Transform parent, Font font)
        {
            GameObject go = PanelObject("InventorySlotTemplate", parent, HudPanelRaised);
            LayoutElement layout = go.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = 92f;
            layout.minHeight = layout.preferredHeight = 92f;
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();

            Image selection = ImageObject("SelectedFrame", go.transform, Accent);
            Stretch(selection.rectTransform);
            selection.raycastTarget = false;

            Image icon = ImageObject("ItemIcon", go.transform, Color.white);
            icon.raycastTarget = false;
            RectTransform ir = icon.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0.5f, 0.5f);
            ir.pivot = new Vector2(0.5f, 0.5f);
            ir.sizeDelta = new Vector2(62f, 62f);
            ir.anchoredPosition = Vector2.zero;

            Text quantity = TextObject("Quantity", go.transform, font, 13, TextPrimary, TextAnchor.LowerRight, string.Empty);
            SetOffsets(quantity.rectTransform, 5f, 5f, 5f, 5f);

            StandaloneInventorySlotView view = go.AddComponent<StandaloneInventorySlotView>();
            view.ConfigureForEditor(button, icon, null, quantity, selection);
            return view;
        }

        private static StandaloneEquipmentSlotView CreateEquipmentSlotTemplate(Transform parent, Font font)
        {
            GameObject go = PanelObject("EquipmentSlotTemplate", parent, HudPanelRaised);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();

            Image highlight = ImageObject("DropHighlight", go.transform, Color.clear);
            Stretch(highlight.rectTransform);
            highlight.raycastTarget = false;
            highlight.enabled = false;

            Image icon = ImageObject("ItemIcon", go.transform, Color.white);
            RectTransform ir = icon.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0f, 0.5f);
            ir.pivot = new Vector2(0f, 0.5f);
            ir.sizeDelta = new Vector2(38f, 38f);
            ir.anchoredPosition = new Vector2(7f, 0f);
            icon.raycastTarget = false;

            Text slot = TextObject("SlotName", go.transform, font, 10, TextSecondary, TextAnchor.UpperLeft, "Slot");
            SetRect(slot.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 1f), new Vector2(51f, -5f), new Vector2(-7f, 18f));
            Text item = TextObject("ItemName", go.transform, font, 12, TextPrimary, TextAnchor.LowerLeft, "Empty");
            SetRect(item.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0f), new Vector2(51f, 5f), new Vector2(-7f, 22f));

            StandaloneEquipmentSlotView view = go.AddComponent<StandaloneEquipmentSlotView>();
            view.ConfigureForEditor(button, icon, slot, item, highlight);
            return view;
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
            rect.sizeDelta = new Vector2(360f, 100f);
            AddOutline(panel);

            // Local-player HUD: no redundant player-name/title text. Order is explicitly
            // HP -> Mana -> Stamina.
            healthSlider = ResourceBar(panel.transform, font, "Health", "HP", Health, 12f, out healthText);
            manaSlider = ResourceBar(panel.transform, font, "Mana", "MP", Mana, 40f, out manaText);
            staminaSlider = ResourceBar(panel.transform, font, "Stamina", "STAM", Stamina, 68f, out staminaText);
        }

        private static Slider ResourceBar(Transform parent, Font font, string name, string prefix, Color fillColor, float yFromTop, out Text valueText)
        {
            GameObject root = PanelObject(name, parent, Field);
            RectTransform rr = root.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0f, 1f);
            rr.pivot = new Vector2(0f, 1f);
            rr.anchoredPosition = new Vector2(12f, -yFromTop);
            rr.sizeDelta = new Vector2(336f, 24f);

            Slider slider = root.AddComponent<Slider>();
            slider.interactable = false;
            slider.transition = Selectable.Transition.None;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;

            GameObject fillArea = Node("FillArea", root.transform);
            Stretch(fillArea.GetComponent<RectTransform>());
            RectTransform fa = fillArea.GetComponent<RectTransform>();
            fa.offsetMin = new Vector2(2f, 2f);
            fa.offsetMax = new Vector2(-2f, -2f);
            Image fill = ImageObject("Fill", fillArea.transform, fillColor);
            Stretch(fill.rectTransform);
            fill.raycastTarget = false;
            slider.fillRect = fill.rectTransform;
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
            rr.anchorMin = rr.anchorMax = Vector2.zero;
            rr.pivot = Vector2.zero;
            rr.anchoredPosition = new Vector2(24f, 24f);
            rr.sizeDelta = new Vector2(520f, 240f);
            AddOutline(root);

            GameObject scrollRoot = PanelObject("History", root.transform, Field);
            RectTransform sr = scrollRoot.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(10f, 48f);
            sr.offsetMax = new Vector2(-10f, -10f);
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
            hr.sizeDelta = new Vector2(0f, 24f);
            history.horizontalOverflow = HorizontalWrapMode.Wrap;
            history.verticalOverflow = VerticalWrapMode.Overflow;
            ContentSizeFitter fitter = history.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            chatScroll.content = hr;

            input = InputFieldObject("Input", root.transform, font, "Press Enter to chat");
            input.gameObject.AddComponent<ClientUiTextInputCapture>();
            RectTransform ir = input.GetComponent<RectTransform>();
            ir.anchorMin = new Vector2(0f, 0f);
            ir.anchorMax = new Vector2(1f, 0f);
            ir.pivot = new Vector2(0.5f, 0f);
            ir.offsetMin = new Vector2(10f, 10f);
            ir.offsetMax = new Vector2(-78f, 40f);

            send = ButtonObject("Send", root.transform, font, "SEND", Accent);
            RectTransform br = send.GetComponent<RectTransform>();
            br.anchorMin = br.anchorMax = new Vector2(1f, 0f);
            br.pivot = new Vector2(1f, 0f);
            br.anchoredPosition = new Vector2(-10f, 10f);
            br.sizeDelta = new Vector2(62f, 30f);
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

        private static GameObject BuildExplorationDot(Transform hud)
        {
            GameObject dot = PanelObject("ExplorationDot", hud, Color.white);
            RectTransform rr = dot.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 0.5f);
            rr.pivot = new Vector2(0.5f, 0.5f);
            rr.anchoredPosition = Vector2.zero;
            rr.sizeDelta = new Vector2(4f, 4f);
            dot.GetComponent<Image>().raycastTarget = false;
            return dot;
        }

        private static Text[] BuildActionBar(Transform hud, Font font)
        {
            GameObject bar = PanelObject("ActionBar", hud, HudPanel);
            RectTransform rr = bar.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 0f);
            rr.pivot = new Vector2(0.5f, 0f);
            rr.anchoredPosition = new Vector2(0f, 24f);
            rr.sizeDelta = new Vector2(690f, 70f);
            AddOutline(bar);

            HorizontalLayoutGroup layout = bar.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlHeight = false;
            layout.childControlWidth = false;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = false;

            var labels = new Text[10];
            for (int i = 0; i < labels.Length; ++i)
            {
                GameObject slot = PanelObject($"ActionSlot_{i + 1}", bar.transform, HudPanelRaised);
                slot.GetComponent<RectTransform>().sizeDelta = new Vector2(60f, 54f);
                labels[i] = TextObject("Hotkey", slot.transform, font, 11, TextSecondary, TextAnchor.UpperLeft, PlayerControlConfig.BindingLabel(PlayerControlConfig.HotbarAction(i + 1)));
                SetOffsets(labels[i].rectTransform, 5f, 5f, 5f, 4f);
            }
            return labels;
        }

        private static void BuildUtilityDock(
            Transform hud,
            Font font,
            StandaloneClientUIRoot standaloneRoot,
            ClientGameplayUIRoot gameplayController,
            StandaloneHudTooltipPanel tooltip)
        {
            GameObject dock = PanelObject("QuickLinks", hud, HudPanel);
            RectTransform rr = dock.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(1f, 0f);
            rr.pivot = new Vector2(1f, 0f);
            rr.anchoredPosition = new Vector2(-24f, 24f);
            rr.sizeDelta = new Vector2(322f, 54f);
            AddOutline(dock);

            HorizontalLayoutGroup layout = dock.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 6, 6);
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlHeight = false;
            layout.childControlWidth = false;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = false;

            AddQuickLink(dock.transform, font, "Inventory", "I", "Character / Inventory", "Open the combined Character and Inventory window.", tooltip, standaloneRoot.ToggleCharacterInventory);
            AddQuickLink(dock.transform, font, "Skills", "P", "Skills", "Open Skills and progression.", tooltip, standaloneRoot.ToggleSkills);
            AddQuickLink(dock.transform, font, "Map", "M", "Map", "Open the world map.", tooltip, standaloneRoot.ToggleMap);
            AddQuickLink(dock.transform, font, "Social", "O", "Social", "Open Friends / Party / Guild social tools.", tooltip, standaloneRoot.ToggleSocial);
            AddQuickLink(dock.transform, font, "Emotes", "EM", "Emotes", "Open the clickable Emote panel.", tooltip, standaloneRoot.ToggleEmotes);
            AddQuickLink(dock.transform, font, "Menu", "≡", "Menu", "Open the in-game menu.", tooltip, gameplayController.TogglePauseMenu);
        }

        private static void AddQuickLink(
            Transform parent,
            Font font,
            string name,
            string glyph,
            string tooltipTitle,
            string tooltipBody,
            StandaloneHudTooltipPanel tooltip,
            UnityEngine.Events.UnityAction clicked)
        {
            Button button = ButtonObject(name, parent, font, glyph, HudPanelRaised);
            button.GetComponent<RectTransform>().sizeDelta = new Vector2(46f, 40f);
            UnityEventTools.AddPersistentListener(button.onClick, clicked);
            StandaloneHudTooltipTarget target = button.gameObject.AddComponent<StandaloneHudTooltipTarget>();
            target.ConfigureForEditor(tooltip, tooltipTitle, tooltipBody);
        }

        private static StandaloneSkillsWindow BuildSkillsWindow(Transform windows, Font font)
        {
            GameObject root = BuildWindowFrame(windows, font, "SkillsWindow", "SKILLS", new Vector2(760f, 650f), out Transform body, out Button close);
            StandaloneSkillsWindow controller = root.AddComponent<StandaloneSkillsWindow>();

            GameObject scrollRoot = ScrollArea("SkillsScroll", body, out ScrollRect scroll, out RectTransform content);
            Stretch(scrollRoot.GetComponent<RectTransform>());
            Text summary = TextObject("Summary", content, font, 15, TextPrimary, TextAnchor.UpperLeft, "Skills have not been hydrated yet.");
            summary.horizontalOverflow = HorizontalWrapMode.Wrap;
            summary.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform sr = summary.rectTransform;
            sr.anchorMin = new Vector2(0f, 1f);
            sr.anchorMax = new Vector2(1f, 1f);
            sr.pivot = new Vector2(0.5f, 1f);
            sr.anchoredPosition = Vector2.zero;
            sr.sizeDelta = new Vector2(-24f, 80f);
            ContentSizeFitter fitter = summary.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = sr;
            controller.ConfigureForEditor(root, close, summary);
            return controller;
        }

        private static GameObject BuildSimpleWindow(
            Transform windows,
            Font font,
            string name,
            string title,
            string bodyText,
            UnityEngine.Events.UnityAction closeAction)
        {
            GameObject root = BuildWindowFrame(windows, font, name, title, new Vector2(760f, 620f), out Transform body, out Button close);
            UnityEventTools.AddPersistentListener(close.onClick, closeAction);
            Text bodyLabel = TextObject("BodyText", body, font, 16, TextSecondary, TextAnchor.UpperLeft, bodyText);
            SetOffsets(bodyLabel.rectTransform, 20f, 20f, 20f, 20f);
            return root;
        }

        private static StandaloneSocialEconomyUI BuildSocialEconomyUi(
            Transform windows,
            Transform hud,
            Transform overlays,
            Font font,
            StandaloneClientUIRoot rootController,
            out GameObject socialRoot,
            out GameObject storageRoot,
            out GameObject partyHudRoot,
            out GameObject friendInvitePopupRoot,
            out GameObject partyInvitePopupRoot)
        {
            StandaloneSocialEconomyUI controller = rootController.GetComponent<StandaloneSocialEconomyUI>();
            if (controller == null)
                controller = rootController.gameObject.AddComponent<StandaloneSocialEconomyUI>();

            socialRoot = BuildWindowFrame(windows, font, "SocialWindow", "SOCIAL", new Vector2(860f, 650f), out Transform socialBody, out Button socialClose);
            UnityEventTools.AddPersistentListener(socialClose.onClick, rootController.CloseSocial);

            Button friendsTab = ButtonObject("FriendsTabButton", socialBody, font, "FRIENDS", Accent);
            SetRect(friendsTab.GetComponent<RectTransform>(), new Vector2(0.02f, 1f), new Vector2(0.23f, 1f), new Vector2(0f, 1f), new Vector2(0f, -4f), new Vector2(0f, 36f));
            Button partyTab = ButtonObject("PartyTabButton", socialBody, font, "PARTY", HudPanelRaised);
            SetRect(partyTab.GetComponent<RectTransform>(), new Vector2(0.24f, 1f), new Vector2(0.45f, 1f), new Vector2(0f, 1f), new Vector2(0f, -4f), new Vector2(0f, 36f));
            Button guildTab = ButtonObject("GuildTabButton", socialBody, font, "GUILD", HudPanelRaised);
            SetRect(guildTab.GetComponent<RectTransform>(), new Vector2(0.46f, 1f), new Vector2(0.67f, 1f), new Vector2(0f, 1f), new Vector2(0f, -4f), new Vector2(0f, 36f));

            GameObject friendsTabRoot = Node("FriendsTab", socialBody);
            RectTransform ftr = friendsTabRoot.GetComponent<RectTransform>();
            ftr.anchorMin = Vector2.zero;
            ftr.anchorMax = Vector2.one;
            ftr.offsetMin = new Vector2(12f, 12f);
            ftr.offsetMax = new Vector2(-12f, -48f);
            Text friendsStatus = TextObject("StatusText", friendsTabRoot.transform, font, 13, TextSecondary, TextAnchor.MiddleLeft, "Friends state has not hydrated yet.");
            SetRect(friendsStatus.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(4f, -2f), new Vector2(-8f, 30f));
            BuildSocialList(friendsTabRoot.transform, font, "FriendsList", 42f, 8f, out Transform friendsContent, out ClientSocialEconomyRowView friendsTemplate);

            GameObject partyTabRoot = Node("PartyTab", socialBody);
            RectTransform ptr = partyTabRoot.GetComponent<RectTransform>();
            ptr.anchorMin = Vector2.zero;
            ptr.anchorMax = Vector2.one;
            ptr.offsetMin = new Vector2(12f, 12f);
            ptr.offsetMax = new Vector2(-12f, -48f);
            Text partyStatus = TextObject("StatusText", partyTabRoot.transform, font, 13, TextSecondary, TextAnchor.MiddleLeft, "Not in a party.");
            SetRect(partyStatus.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(4f, -2f), new Vector2(-8f, 30f));
            BuildSocialList(partyTabRoot.transform, font, "PartyList", 42f, 56f, out Transform partyContent, out ClientSocialEconomyRowView partyTemplate);
            Button leaveParty = ButtonObject("LeavePartyButton", partyTabRoot.transform, font, "LEAVE PARTY", HudPanelRaised);
            SetRect(leaveParty.GetComponent<RectTransform>(), new Vector2(0.58f, 0f), new Vector2(0.77f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            Button disbandParty = ButtonObject("DisbandPartyButton", partyTabRoot.transform, font, "DISBAND", HudPanelRaised);
            SetRect(disbandParty.GetComponent<RectTransform>(), new Vector2(0.79f, 0f), new Vector2(0.98f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            partyTabRoot.SetActive(false);

            GameObject guildTabRoot = Node("GuildTab", socialBody);
            RectTransform gtr = guildTabRoot.GetComponent<RectTransform>();
            gtr.anchorMin = Vector2.zero; gtr.anchorMax = Vector2.one;
            gtr.offsetMin = new Vector2(12f, 12f); gtr.offsetMax = new Vector2(-12f, -48f);
            Text guildStatus = TextObject("StatusText", guildTabRoot.transform, font, 13, TextSecondary, TextAnchor.MiddleLeft, "Guild state has not hydrated yet.");
            SetRect(guildStatus.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(4f, -2f), new Vector2(-8f, 30f));
            BuildSocialList(guildTabRoot.transform, font, "GuildList", 86f, 56f, out Transform guildContent, out ClientSocialEconomyRowView guildTemplate);
            InputField guildName = InputFieldObject("GuildNameInput", guildTabRoot.transform, font, "Guild name");
            SetRect(guildName.GetComponent<RectTransform>(), new Vector2(0.02f, 0f), new Vector2(0.38f, 0f), new Vector2(0f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            Button createGuild = ButtonObject("CreateGuildButton", guildTabRoot.transform, font, "CREATE", Accent);
            SetRect(createGuild.GetComponent<RectTransform>(), new Vector2(0.40f, 0f), new Vector2(0.56f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            Button leaveGuild = ButtonObject("LeaveGuildButton", guildTabRoot.transform, font, "LEAVE GUILD", HudPanelRaised);
            SetRect(leaveGuild.GetComponent<RectTransform>(), new Vector2(0.58f, 0f), new Vector2(0.77f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            Button disbandGuild = ButtonObject("DisbandGuildButton", guildTabRoot.transform, font, "DISBAND", HudPanelRaised);
            SetRect(disbandGuild.GetComponent<RectTransform>(), new Vector2(0.79f, 0f), new Vector2(0.98f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            guildTabRoot.SetActive(false);

            partyHudRoot = PanelObject("PartyFrames", hud, HudPanel);
            RectTransform phr = partyHudRoot.GetComponent<RectTransform>();
            phr.anchorMin = phr.anchorMax = new Vector2(0f, 1f);
            phr.pivot = new Vector2(0f, 1f);
            phr.anchoredPosition = new Vector2(24f, -136f);
            phr.sizeDelta = new Vector2(300f, 164f);
            AddOutline(partyHudRoot);
            Text partyHudText = TextObject("PartyText", partyHudRoot.transform, font, 12, TextPrimary, TextAnchor.UpperLeft, "PARTY");
            SetRect(partyHudText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 1f), new Vector2(10f, 8f), new Vector2(-74f, -8f));
            Button partyHudOpen = ButtonObject("OpenPartyButton", partyHudRoot.transform, font, "OPEN", HudPanelRaised);
            RectTransform phb = partyHudOpen.GetComponent<RectTransform>();
            phb.anchorMin = phb.anchorMax = new Vector2(1f, 1f);
            phb.pivot = new Vector2(1f, 1f);
            phb.anchoredPosition = new Vector2(-8f, -8f);
            phb.sizeDelta = new Vector2(58f, 28f);

            friendInvitePopupRoot = BuildSocialInvitePopup(overlays, font, "FriendInvitePopup", "FRIEND REQUEST", out Text friendInviteText, out Button friendAccept, out Button friendDecline);
            partyInvitePopupRoot = BuildSocialInvitePopup(overlays, font, "PartyInvitePopup", "PARTY INVITE", out Text partyInviteText, out Button partyAccept, out Button partyDecline);
            GameObject guildInvitePopupRoot = BuildSocialInvitePopup(overlays, font, "GuildInvitePopup", "GUILD INVITE", out Text guildInviteText, out Button guildAccept, out Button guildDecline);

            storageRoot = BuildWindowFrame(windows, font, "StorageWindow", "STORAGE", new Vector2(1040f, 680f), out Transform storageBody, out Button storageClose);
            Text storageStatus = TextObject("StatusText", storageBody, font, 13, TextSecondary, TextAnchor.MiddleLeft, "Storage is opened from an authorized world interaction.");
            SetRect(storageStatus.rectTransform, new Vector2(0.02f, 0.93f), new Vector2(0.72f, 1f), new Vector2(0f, 1f), Vector2.zero, Vector2.zero);
            Text storageCapacity = TextObject("CapacityText", storageBody, font, 13, TextPrimary, TextAnchor.MiddleRight, "Storage capacity pending");
            SetRect(storageCapacity.rectTransform, new Vector2(0.72f, 0.93f), new Vector2(0.98f, 1f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            BuildStorageColumn(storageBody, font, "InventoryColumn", "YOUR INVENTORY", 0.02f, 0.495f, out Transform storageInventoryContent, out ClientSocialEconomyRowView storageInventoryTemplate);
            BuildStorageColumn(storageBody, font, "StorageColumn", "STORAGE", 0.505f, 0.98f, out Transform storageContent, out ClientSocialEconomyRowView storageTemplate);

            controller.ConfigureForEditor(
                socialRoot,
                friendsTab,
                partyTab,
                guildTab,
                friendsTabRoot,
                partyTabRoot,
                guildTabRoot,
                friendsStatus,
                friendsContent,
                friendsTemplate,
                partyStatus,
                partyContent,
                partyTemplate,
                leaveParty,
                disbandParty,
                guildStatus,
                guildContent,
                guildTemplate,
                guildName,
                createGuild,
                leaveGuild,
                disbandGuild,
                guildInvitePopupRoot,
                guildInviteText,
                guildAccept,
                guildDecline,
                partyHudRoot,
                partyHudText,
                partyHudOpen,
                friendInvitePopupRoot,
                friendInviteText,
                friendAccept,
                friendDecline,
                partyInvitePopupRoot,
                partyInviteText,
                partyAccept,
                partyDecline,
                storageRoot,
                storageStatus,
                storageCapacity,
                storageClose,
                storageInventoryContent,
                storageInventoryTemplate,
                storageContent,
                storageTemplate);
            return controller;
        }

        private static void BuildSocialList(
            Transform parent,
            Font font,
            string name,
            float topInset,
            float bottomInset,
            out Transform content,
            out ClientSocialEconomyRowView rowTemplate)
        {
            GameObject scrollRoot = ScrollArea(name, parent, out ScrollRect _, out RectTransform listContent);
            RectTransform sr = scrollRoot.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(4f, bottomInset);
            sr.offsetMax = new Vector2(-4f, -topInset);

            VerticalLayoutGroup layout = listContent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 5f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = listContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            rowTemplate = BuildTradeRowTemplate(listContent, font);
            rowTemplate.gameObject.SetActive(false);
            content = listContent;
        }

        private static GameObject BuildSocialInvitePopup(
            Transform overlays,
            Font font,
            string name,
            string titleValue,
            out Text message,
            out Button accept,
            out Button decline)
        {
            GameObject root = PanelObject(name, overlays, new Color32(20, 22, 28, 252));
            RectTransform rr = root.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 0.5f);
            rr.pivot = new Vector2(0.5f, 0.5f);
            rr.anchoredPosition = Vector2.zero;
            rr.sizeDelta = new Vector2(540f, 260f);
            AddOutline(root);

            Text title = TextObject("Title", root.transform, font, 21, TextPrimary, TextAnchor.MiddleCenter, titleValue);
            SetRect(title.rectTransform, new Vector2(0.06f, 0.72f), new Vector2(0.94f, 0.92f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            message = TextObject("MessageText", root.transform, font, 16, TextPrimary, TextAnchor.MiddleCenter, string.Empty);
            SetRect(message.rectTransform, new Vector2(0.08f, 0.40f), new Vector2(0.92f, 0.70f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            accept = ButtonObject("AcceptButton", root.transform, font, "ACCEPT", Accent);
            SetRect(accept.GetComponent<RectTransform>(), new Vector2(0.14f, 0.12f), new Vector2(0.46f, 0.30f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            decline = ButtonObject("DeclineButton", root.transform, font, "DECLINE", HudPanelRaised);
            SetRect(decline.GetComponent<RectTransform>(), new Vector2(0.54f, 0.12f), new Vector2(0.86f, 0.30f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            return root;
        }

        private static void BuildStorageColumn(
            Transform parent,
            Font font,
            string name,
            string heading,
            float minX,
            float maxX,
            out Transform content,
            out ClientSocialEconomyRowView rowTemplate)
        {
            GameObject panel = PanelObject(name, parent, HudPanel);
            RectTransform pr = panel.GetComponent<RectTransform>();
            pr.anchorMin = new Vector2(minX, 0.02f);
            pr.anchorMax = new Vector2(maxX, 0.91f);
            pr.offsetMin = new Vector2(0f, 0f);
            pr.offsetMax = new Vector2(0f, 0f);
            AddOutline(panel);

            Text title = TextObject("Heading", panel.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, heading);
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -4f), new Vector2(-20f, 28f));

            GameObject scrollRoot = ScrollArea("Scroll", panel.transform, out ScrollRect _, out RectTransform listContent);
            RectTransform sr = scrollRoot.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(8f, 8f);
            sr.offsetMax = new Vector2(-8f, -38f);

            VerticalLayoutGroup layout = listContent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 5f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = listContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            rowTemplate = BuildTradeRowTemplate(listContent, font);
            rowTemplate.gameObject.SetActive(false);
            content = listContent;
        }

        private static GameObject BuildEmotesWindow(Transform windows, Font font, StandaloneClientUIRoot rootController)
        {
            GameObject root = BuildWindowFrame(windows, font, "EmotesWindow", "EMOTES", new Vector2(720f, 560f), out Transform body, out Button close);
            UnityEventTools.AddPersistentListener(close.onClick, rootController.CloseEmotes);

            Text note = TextObject("Note", body, font, 13, TextSecondary, TextAnchor.UpperLeft, "Clickable emote entries will bind to the canonical emote system here.");
            SetRect(note.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(16f, -14f), new Vector2(-32f, 30f));

            GameObject gridRoot = Node("EmoteGrid", body);
            RectTransform gr = gridRoot.GetComponent<RectTransform>();
            gr.anchorMin = Vector2.zero;
            gr.anchorMax = Vector2.one;
            gr.offsetMin = new Vector2(16f, 16f);
            gr.offsetMax = new Vector2(-16f, -58f);
            GridLayoutGroup grid = gridRoot.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(150f, 58f);
            grid.spacing = new Vector2(8f, 8f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 4;
            grid.childAlignment = TextAnchor.UpperLeft;
            for (int i = 0; i < 12; ++i)
            {
                Button emote = ButtonObject($"Emote_{i + 1:00}", gridRoot.transform, font, "EMOTE", HudPanelRaised);
                emote.interactable = false;
            }
            return root;
        }

        private static StandaloneControlsWindow BuildControlsWindow(Transform windows, Font font)
        {
            GameObject root = BuildWindowFrame(windows, font, "PlayerConfigWindow", "PLAYER CONFIG / CONTROLS", new Vector2(780f, 720f), out Transform body, out Button close);
            StandaloneControlsWindow controller = root.AddComponent<StandaloneControlsWindow>();

            Text localOnly = TextObject("LocalOnly", body, font, 12, TextSecondary, TextAnchor.MiddleLeft, "Local machine only — bindings are never sent over the network.");
            SetRect(localOnly.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(14f, -8f), new Vector2(-150f, 28f));
            Button reset = ButtonObject("ResetDefaults", body, font, "RESET DEFAULTS", HudPanelRaised);
            RectTransform rr = reset.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(1f, 1f);
            rr.pivot = new Vector2(1f, 1f);
            rr.anchoredPosition = new Vector2(-12f, -6f);
            rr.sizeDelta = new Vector2(132f, 30f);

            Text status = TextObject("Status", body, font, 12, new Color32(230, 193, 107, 255), TextAnchor.MiddleLeft, string.Empty);
            SetRect(status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(14f, 8f), new Vector2(-28f, 28f));

            GameObject scrollRoot = ScrollArea("BindingsScroll", body, out ScrollRect scroll, out RectTransform content);
            RectTransform sr = scrollRoot.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(12f, 42f);
            sr.offsetMax = new Vector2(-12f, -44f);
            VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 5f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var rows = new List<StandaloneControlBindingRow>(ControlRows.Length);
            for (int i = 0; i < ControlRows.Length; ++i)
            {
                PlayerControlAction action = ControlRows[i];
                GameObject rowGo = PanelObject($"Binding_{action}", content, HudPanelRaised);
                LayoutElement element = rowGo.AddComponent<LayoutElement>();
                element.minHeight = element.preferredHeight = 38f;
                Text actionText = TextObject("Action", rowGo.transform, font, 12, TextPrimary, TextAnchor.MiddleLeft, PlayerControlConfig.DisplayName(action));
                SetRect(actionText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(-170f, 0f));
                Button bind = ButtonObject("Binding", rowGo.transform, font, PlayerControlConfig.BindingLabel(action), Field);
                RectTransform br = bind.GetComponent<RectTransform>();
                br.anchorMin = br.anchorMax = new Vector2(1f, 0.5f);
                br.pivot = new Vector2(1f, 0.5f);
                br.anchoredPosition = new Vector2(-6f, 0f);
                br.sizeDelta = new Vector2(150f, 28f);
                Text bindingText = bind.GetComponentInChildren<Text>(true);
                StandaloneControlBindingRow row = rowGo.AddComponent<StandaloneControlBindingRow>();
                row.ConfigureForEditor(action, actionText, bindingText, bind, controller);
                rows.Add(row);
            }

            controller.ConfigureForEditor(root, close, reset, status, rows.ToArray());
            return controller;
        }

        private static PlayerLootShell BuildLootWindow(Transform windows, Font font, StandaloneClientUIRoot rootController)
        {
            GameObject root = BuildWindowFrame(windows, font, "LootWindow", "LOOT", new Vector2(620f, 560f), out Transform body, out Button close);
            PlayerLootShell controller = root.AddComponent<PlayerLootShell>();
            UnityEventTools.AddPersistentListener(close.onClick, controller.Close);

            Text source = TextObject("SourceLabel", body, font, 16, TextPrimary, TextAnchor.MiddleLeft, "Loot");
            SetRect(source.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(8f, -4f), new Vector2(-16f, 32f));

            GameObject scrollRoot = ScrollArea("LootScroll", body, out ScrollRect scroll, out RectTransform content);
            RectTransform scrollRect = scrollRoot.GetComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0f, 0f);
            scrollRect.anchorMax = new Vector2(1f, 1f);
            scrollRect.offsetMin = new Vector2(8f, 58f);
            scrollRect.offsetMax = new Vector2(-8f, -42f);

            VerticalLayoutGroup layout = content.gameObject.GetComponent<VerticalLayoutGroup>();
            if (layout == null) layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = content.gameObject.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject rowObject = PanelObject("LootRowTemplate", content, HudPanelRaised);
            rowObject.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 42f);
            LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 42f;
            Text rowLabel = TextObject("ItemLabel", rowObject.transform, font, 14, TextPrimary, TextAnchor.MiddleLeft, "Item x1");
            SetRect(rowLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0.76f, 1f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(-4f, 0f));
            Button take = ButtonObject("TakeButton", rowObject.transform, font, "TAKE", Accent);
            SetRect(take.GetComponent<RectTransform>(), new Vector2(0.78f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(-4f, -6f));
            WorldLootRowView row = rowObject.AddComponent<WorldLootRowView>();
            row.ConfigureForEditor(rowLabel, take);
            rowObject.SetActive(false);

            Text feedback = TextObject("FeedbackLabel", body, font, 12, TextSecondary, TextAnchor.MiddleLeft, string.Empty);
            SetRect(feedback.rectTransform, new Vector2(0f, 0f), new Vector2(0.72f, 0f), new Vector2(0f, 0f), new Vector2(8f, 8f), new Vector2(-4f, 40f));
            Button takeAll = ButtonObject("TakeAllButton", body, font, "TAKE ALL", Accent);
            SetRect(takeAll.GetComponent<RectTransform>(), new Vector2(0.74f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 8f), new Vector2(-8f, 40f));

            controller.ConfigureForEditor(source, feedback, content, row, takeAll);
            controller.BindStandaloneRoot(rootController, root);
            return controller;
        }

        private static StandaloneTradeUI BuildTradeWindow(Transform windows, Font font)
        {
            GameObject root = BuildWindowFrame(windows, font, "TradeWindow", "TRADE", new Vector2(1120f, 720f), out Transform body, out Button close);
            close.gameObject.SetActive(false); // Trade is authoritative/modal; Decline/Cancel/end-state closes it.
            StandaloneTradeUI controller = root.AddComponent<StandaloneTradeUI>();

            GameObject activeState = Node("ActiveState", body);
            Stretch(activeState.GetComponent<RectTransform>());

            Text partner = TextObject("PartnerText", activeState.transform, font, 16, TextPrimary, TextAnchor.MiddleLeft, "Trading with --");
            SetRect(partner.rectTransform, new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(8f, -4f), new Vector2(-12f, 28f));
            Text status = TextObject("StatusText", activeState.transform, font, 13, new Color32(230, 193, 107, 255), TextAnchor.MiddleRight, string.Empty);
            SetRect(status.rectTransform, new Vector2(0.48f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-8f, -4f), new Vector2(-12f, 28f));

            BuildTradeListColumn(activeState.transform, font, "Inventory", "YOUR INVENTORY", 0.00f, 0.325f,
                out Transform inventoryContent, out ClientSocialEconomyRowView inventoryTemplate);
            BuildTradeListColumn(activeState.transform, font, "OwnOffer", "YOUR OFFER", 0.337f, 0.662f,
                out Transform ownOfferContent, out ClientSocialEconomyRowView ownOfferTemplate);
            BuildTradeListColumn(activeState.transform, font, "PartnerOffer", "THEIR OFFER", 0.674f, 1.00f,
                out Transform partnerOfferContent, out ClientSocialEconomyRowView partnerOfferTemplate);

            Text ownState = TextObject("OwnStateText", activeState.transform, font, 12, TextSecondary, TextAnchor.MiddleLeft, "YOU: EDITING");
            SetRect(ownState.rectTransform, new Vector2(0f, 0f), new Vector2(0.33f, 0f), new Vector2(0f, 0f), new Vector2(8f, 50f), new Vector2(-12f, 24f));
            Text partnerState = TextObject("PartnerStateText", activeState.transform, font, 12, TextSecondary, TextAnchor.MiddleLeft, "PARTNER: EDITING");
            SetRect(partnerState.rectTransform, new Vector2(0.34f, 0f), new Vector2(0.67f, 0f), new Vector2(0f, 0f), new Vector2(8f, 50f), new Vector2(-12f, 24f));

            Button lockButton = ButtonObject("LockButton", activeState.transform, font, "LOCK OFFER", HudPanelRaised);
            SetRect(lockButton.GetComponent<RectTransform>(), new Vector2(0.56f, 0f), new Vector2(0.70f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            Text lockLabel = lockButton.GetComponentInChildren<Text>(true);
            Button confirm = ButtonObject("ConfirmButton", activeState.transform, font, "CONFIRM", Accent);
            SetRect(confirm.GetComponent<RectTransform>(), new Vector2(0.71f, 0f), new Vector2(0.84f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));
            Button cancel = ButtonObject("CancelButton", activeState.transform, font, "CANCEL", HudPanelRaised);
            SetRect(cancel.GetComponent<RectTransform>(), new Vector2(0.85f, 0f), new Vector2(0.98f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(0f, 38f));

            GameObject requestState = PanelObject("RequestState", body, new Color32(20, 22, 28, 252));
            Stretch(requestState.GetComponent<RectTransform>());
            Text requestTitle = TextObject("Title", requestState.transform, font, 24, TextPrimary, TextAnchor.MiddleCenter, "TRADE REQUEST");
            SetRect(requestTitle.rectTransform, new Vector2(0.20f, 0.66f), new Vector2(0.80f, 0.82f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Text requestMessage = TextObject("MessageText", requestState.transform, font, 17, TextPrimary, TextAnchor.MiddleCenter, "A player wants to trade with you.");
            SetRect(requestMessage.rectTransform, new Vector2(0.18f, 0.46f), new Vector2(0.82f, 0.64f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Button accept = ButtonObject("AcceptButton", requestState.transform, font, "ACCEPT", Accent);
            SetRect(accept.GetComponent<RectTransform>(), new Vector2(0.30f, 0.28f), new Vector2(0.48f, 0.38f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Button decline = ButtonObject("DeclineButton", requestState.transform, font, "DECLINE", HudPanelRaised);
            SetRect(decline.GetComponent<RectTransform>(), new Vector2(0.52f, 0.28f), new Vector2(0.70f, 0.38f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            controller.ConfigureForEditor(
                root, activeState, requestState, requestMessage, accept, decline,
                partner, status, ownState, partnerState,
                inventoryContent, inventoryTemplate,
                ownOfferContent, ownOfferTemplate,
                partnerOfferContent, partnerOfferTemplate,
                lockButton, lockLabel, confirm, cancel);
            return controller;
        }

        private static void BuildTradeListColumn(
            Transform parent,
            Font font,
            string name,
            string heading,
            float minX,
            float maxX,
            out Transform content,
            out ClientSocialEconomyRowView rowTemplate)
        {
            GameObject panel = PanelObject(name, parent, HudPanel);
            RectTransform pr = panel.GetComponent<RectTransform>();
            pr.anchorMin = new Vector2(minX, 0f);
            pr.anchorMax = new Vector2(maxX, 1f);
            pr.offsetMin = new Vector2(6f, 92f);
            pr.offsetMax = new Vector2(-6f, -42f);
            AddOutline(panel);

            Text title = TextObject("Heading", panel.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, heading);
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -4f), new Vector2(-20f, 28f));

            GameObject scrollRoot = ScrollArea("Scroll", panel.transform, out ScrollRect scroll, out RectTransform listContent);
            RectTransform sr = scrollRoot.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(8f, 8f);
            sr.offsetMax = new Vector2(-8f, -38f);

            VerticalLayoutGroup layout = listContent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 5f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = listContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            rowTemplate = BuildTradeRowTemplate(listContent, font);
            rowTemplate.gameObject.SetActive(false);
            content = listContent;
        }

        private static ClientSocialEconomyRowView BuildTradeRowTemplate(Transform parent, Font font)
        {
            GameObject rowGo = PanelObject("RowTemplate", parent, HudPanelRaised);
            LayoutElement element = rowGo.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 64f;

            Text primary = TextObject("PrimaryText", rowGo.transform, font, 12, TextPrimary, TextAnchor.UpperLeft, "Item");
            SetRect(primary.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0.5f), new Vector2(8f, 8f), new Vector2(-174f, -4f));
            Text secondary = TextObject("SecondaryText", rowGo.transform, font, 10, TextSecondary, TextAnchor.LowerLeft, string.Empty);
            SetRect(secondary.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0.5f), new Vector2(8f, 4f), new Vector2(-174f, -30f));

            Button primaryButton = ButtonObject("PrimaryButton", rowGo.transform, font, "ACTION", Accent);
            RectTransform pbr = primaryButton.GetComponent<RectTransform>();
            pbr.anchorMin = pbr.anchorMax = new Vector2(1f, 0.68f);
            pbr.pivot = new Vector2(1f, 0.5f);
            pbr.anchoredPosition = new Vector2(-6f, 0f);
            pbr.sizeDelta = new Vector2(80f, 26f);
            Button secondaryButton = ButtonObject("SecondaryButton", rowGo.transform, font, "MORE", HudPanel);
            RectTransform sbr = secondaryButton.GetComponent<RectTransform>();
            sbr.anchorMin = sbr.anchorMax = new Vector2(1f, 0.28f);
            sbr.pivot = new Vector2(1f, 0.5f);
            sbr.anchoredPosition = new Vector2(-6f, 0f);
            sbr.sizeDelta = new Vector2(80f, 24f);

            ClientSocialEconomyRowView row = rowGo.AddComponent<ClientSocialEconomyRowView>();
            row.CaptureAuthoredReferencesForEditor();
            return row;
        }

        private static GameObject BuildWindowFrame(
            Transform parent,
            Font font,
            string name,
            string titleValue,
            Vector2 size,
            out Transform body,
            out Button close)
        {
            GameObject root = PanelObject(name, parent, new Color32(20, 22, 28, 242));
            RectTransform rr = root.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 0.5f);
            rr.pivot = new Vector2(0.5f, 0.5f);
            rr.anchoredPosition = Vector2.zero;
            rr.sizeDelta = size;
            AddOutline(root);

            GameObject header = PanelObject("Header", root.transform, HudPanelRaised);
            SetRect(header.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(0f, 52f));
            Text title = TextObject("Title", header.transform, font, 20, TextPrimary, TextAnchor.MiddleLeft, titleValue);
            SetOffsets(title.rectTransform, 16f, 70f, 0f, 0f);
            close = ButtonObject("CloseButton", header.transform, font, "X", HudPanel);
            RectTransform cr = close.GetComponent<RectTransform>();
            cr.anchorMin = cr.anchorMax = new Vector2(1f, 0.5f);
            cr.pivot = new Vector2(1f, 0.5f);
            cr.anchoredPosition = new Vector2(-8f, 0f);
            cr.sizeDelta = new Vector2(44f, 36f);

            GameObject bodyGo = Node("Body", root.transform);
            RectTransform br = bodyGo.GetComponent<RectTransform>();
            br.anchorMin = Vector2.zero;
            br.anchorMax = Vector2.one;
            br.offsetMin = new Vector2(10f, 10f);
            br.offsetMax = new Vector2(-10f, -62f);
            body = bodyGo.transform;
            return root;
        }

        private static StandaloneDragGhost BuildDragGhost(Transform overlays)
        {
            GameObject root = PanelObject("DragGhost", overlays, new Color32(18, 21, 28, 230));
            RectTransform rr = root.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = Vector2.zero;
            rr.pivot = new Vector2(0.5f, 0.5f);
            rr.sizeDelta = new Vector2(58f, 58f);
            root.GetComponent<Image>().raycastTarget = false;
            Image icon = ImageObject("Icon", root.transform, Color.white);
            icon.raycastTarget = false;
            Stretch(icon.rectTransform);
            icon.rectTransform.offsetMin = new Vector2(5f, 5f);
            icon.rectTransform.offsetMax = new Vector2(-5f, -5f);
            StandaloneDragGhost ghost = root.AddComponent<StandaloneDragGhost>();
            ghost.ConfigureForEditor(rr, icon);
            return ghost;
        }

        private static void BuildPauseMenu(
            Transform overlays,
            Font font,
            out GameObject root,
            out Button resume,
            out Button options,
            out Button quit)
        {
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
            options = ButtonObject("Options", frame.transform, font, "PLAYER CONFIG", HudPanel);
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

        private static GameObject ScrollArea(string name, Transform parent, out ScrollRect scroll, out RectTransform content)
        {
            GameObject root = PanelObject(name, parent, Field);
            scroll = root.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 22f;

            GameObject viewport = Node("Viewport", root.transform, typeof(Image), typeof(RectMask2D));
            Stretch(viewport.GetComponent<RectTransform>());
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.GetComponent<Image>().raycastTarget = true;
            scroll.viewport = viewport.GetComponent<RectTransform>();

            GameObject contentGo = Node("Content", viewport.transform);
            content = contentGo.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            scroll.content = content;
            return root;
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

        private static void ClearChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; --i)
                UnityEngine.Object.DestroyImmediate(root.GetChild(i).gameObject);
        }

        private static void ClearWindowsExcept(Transform windows, Transform keep)
        {
            for (int i = windows.childCount - 1; i >= 0; --i)
            {
                Transform child = windows.GetChild(i);
                if (child != keep)
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }
    }
}
#endif
