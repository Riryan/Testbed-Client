#if UNITY_EDITOR
using System;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Standalone;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Skills/Abilities V2.11 authored-prefab upgrade. Replaces only the existing Skills
    /// window body and augments the existing hotbar slots; the surrounding standalone UI
    /// remains editable/preserved.
    /// </summary>
    public static class StandaloneClientUISkillsAbilitiesAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color Panel = new Color32(20, 23, 30, 245);
        private static readonly Color PanelRaised = new Color32(29, 34, 44, 248);
        private static readonly Color Field = new Color32(11, 14, 19, 240);
        private static readonly Color TextPrimary = new Color32(239, 242, 246, 255);
        private static readonly Color TextSecondary = new Color32(164, 176, 193, 255);
        private static readonly Color Accent = new Color32(103, 132, 171, 255);
        private static readonly Color AccentText = new Color32(230, 193, 107, 255);

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Skills Abilities V2.11")]
        public static void UpgradeExistingPrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                EditorUtility.DisplayDialog("Standalone UI", "StandaloneClientUI.prefab was not found.", "OK");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Transform skillsRoot = root.transform.Find("Gameplay/Windows/SkillsWindow");
                if (skillsRoot == null)
                    throw new InvalidOperationException("Gameplay/Windows/SkillsWindow was not found. Install Gameplay Flow V2.04+ first.");

                Transform body = skillsRoot.Find("Body");
                Button close = skillsRoot.Find("Header/CloseButton")?.GetComponent<Button>();
                StandaloneSkillsWindow controller = skillsRoot.GetComponent<StandaloneSkillsWindow>();
                if (body == null || close == null || controller == null)
                    throw new InvalidOperationException("SkillsWindow authored frame/controller is incomplete.");

                Font font = skillsRoot.GetComponentInChildren<Text>(true)?.font ??
                            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                            Resources.GetBuiltinResource<Font>("Arial.ttf");
                StandaloneHudTooltipPanel tooltip = root.GetComponentInChildren<StandaloneHudTooltipPanel>(true);

                for (int i = body.childCount - 1; i >= 0; --i)
                    UnityEngine.Object.DestroyImmediate(body.GetChild(i).gameObject);

                BuildSkillsBody(body, font, tooltip, controller, skillsRoot.gameObject, close);
                UpgradeHotbar(root, font, tooltip);

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
            Debug.Log("[StandaloneUI] Skills/Abilities V2.11 authored: data-driven progression, learned ability catalog, and drag-to-hotbar assignment. Existing Progression + Gameplay Settings messages are reused; no new network message was added.");
        }

        private static void BuildSkillsBody(
            Transform body,
            Font font,
            StandaloneHudTooltipPanel tooltip,
            StandaloneSkillsWindow controller,
            GameObject windowRoot,
            Button close)
        {
            GameObject tabBar = PanelObject("TabBar", body, Field);
            SetRect(tabBar.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, -42f), new Vector2(0f, 42f));
            HorizontalLayoutGroup tabs = tabBar.AddComponent<HorizontalLayoutGroup>();
            tabs.padding = new RectOffset(6, 6, 5, 5);
            tabs.spacing = 6f;
            tabs.childControlWidth = false;
            tabs.childControlHeight = false;
            tabs.childForceExpandWidth = false;
            tabs.childForceExpandHeight = false;

            Button progressionTab = ButtonObject("ProgressionTab", tabBar.transform, font, "PROGRESSION", PanelRaised);
            progressionTab.GetComponent<RectTransform>().sizeDelta = new Vector2(150f, 32f);
            Button abilitiesTab = ButtonObject("AbilitiesTab", tabBar.transform, font, "ABILITIES", PanelRaised);
            abilitiesTab.GetComponent<RectTransform>().sizeDelta = new Vector2(150f, 32f);

            Text levelSummary = TextObject("LevelSummary", body, font, 13, TextPrimary, TextAnchor.MiddleLeft, "LEVEL —     XP —");
            SetRect(levelSummary.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(12f, -78f), new Vector2(-24f, 28f));

            Text status = TextObject("Status", body, font, 12, AccentText, TextAnchor.MiddleLeft, string.Empty);
            SetRect(status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(12f, 4f), new Vector2(-24f, 28f));

            GameObject pageHost = Node("PageHost", body);
            RectTransform phr = pageHost.GetComponent<RectTransform>();
            phr.anchorMin = Vector2.zero;
            phr.anchorMax = Vector2.one;
            phr.offsetMin = new Vector2(0f, 38f);
            phr.offsetMax = new Vector2(0f, -108f);

            GameObject progressionPage = Node("ProgressionPage", pageHost.transform);
            Stretch(progressionPage.GetComponent<RectTransform>());
            RectTransform progressionContent = BuildScroll("ProgressionScroll", progressionPage.transform, out _);
            AddVerticalContentLayout(progressionContent);
            StandaloneProgressionRow progressionTemplate = BuildProgressionTemplate(progressionContent, font);
            progressionTemplate.gameObject.SetActive(false);

            GameObject abilitiesPage = Node("AbilitiesPage", pageHost.transform);
            Stretch(abilitiesPage.GetComponent<RectTransform>());
            RectTransform abilitiesContent = BuildScroll("AbilitiesScroll", abilitiesPage.transform, out _);
            AddVerticalContentLayout(abilitiesContent);
            StandaloneAbilityRow abilityTemplate = BuildAbilityTemplate(abilitiesContent, font);
            abilityTemplate.gameObject.SetActive(false);

            controller.ConfigureForEditor(
                windowRoot,
                close,
                progressionTab,
                abilitiesTab,
                levelSummary,
                status,
                progressionPage,
                progressionContent,
                progressionTemplate,
                abilitiesPage,
                abilitiesContent,
                abilityTemplate,
                tooltip);
        }

        private static StandaloneProgressionRow BuildProgressionTemplate(Transform parent, Font font)
        {
            GameObject row = PanelObject("ProgressionRowTemplate", parent, PanelRaised);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = 44f;

            Text kind = TextObject("Kind", row.transform, font, 10, TextSecondary, TextAnchor.MiddleLeft, "CATEGORY");
            SetRect(kind.rectTransform, Vector2.zero, new Vector2(0.22f, 1f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(-4f, 0f));
            Text name = TextObject("Name", row.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, "Skill Name");
            SetRect(name.rectTransform, new Vector2(0.22f, 0f), new Vector2(0.75f, 1f), new Vector2(0f, 0.5f), new Vector2(4f, 0f), new Vector2(-4f, 0f));
            Text value = TextObject("Value", row.transform, font, 12, TextPrimary, TextAnchor.MiddleRight, "0 / 100");
            SetRect(value.rectTransform, new Vector2(0.75f, 0f), Vector2.one, new Vector2(1f, 0.5f), new Vector2(4f, 0f), new Vector2(-10f, 0f));

            StandaloneProgressionRow view = row.AddComponent<StandaloneProgressionRow>();
            view.ConfigureForEditor(kind, name, value);
            return view;
        }

        private static StandaloneAbilityRow BuildAbilityTemplate(Transform parent, Font font)
        {
            GameObject row = PanelObject("AbilityRowTemplate", parent, PanelRaised);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = 68f;

            Text category = TextObject("Category", row.transform, font, 10, TextSecondary, TextAnchor.UpperLeft, "CATEGORY");
            SetRect(category.rectTransform, Vector2.zero, new Vector2(0.19f, 1f), new Vector2(0f, 1f), new Vector2(10f, -10f), new Vector2(-4f, -8f));
            Text name = TextObject("Name", row.transform, font, 14, TextPrimary, TextAnchor.UpperLeft, "Ability Name");
            SetRect(name.rectTransform, new Vector2(0.19f, 0.45f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(6f, -8f), new Vector2(-10f, -4f));
            Text detail = TextObject("Detail", row.transform, font, 11, TextSecondary, TextAnchor.LowerLeft, "Mana 10   CD 2s   Range 5m");
            SetRect(detail.rectTransform, new Vector2(0.19f, 0f), Vector2.one, new Vector2(0f, 0f), new Vector2(6f, 7f), new Vector2(-10f, -2f));

            StandaloneAbilityRow view = row.AddComponent<StandaloneAbilityRow>();
            view.ConfigureForEditor(name, category, detail);
            return view;
        }

        private static void UpgradeHotbar(GameObject root, Font font, StandaloneHudTooltipPanel tooltip)
        {
            Transform actionBar = root.transform.Find("Gameplay/HUD/ActionBar");
            if (actionBar == null)
                throw new InvalidOperationException("Gameplay/HUD/ActionBar was not found.");

            var slots = new StandaloneHotbarSlotView[Player.Client.PlayerHotbarConfig.SlotCount];
            for (int i = 0; i < slots.Length; ++i)
            {
                int slotNumber = i + 1;
                Transform slot = actionBar.Find($"ActionSlot_{slotNumber}");
                if (slot == null)
                    throw new InvalidOperationException($"ActionBar/ActionSlot_{slotNumber} was not found.");

                Text hotkey = slot.Find("Hotkey")?.GetComponent<Text>();
                if (hotkey == null)
                    hotkey = TextObject("Hotkey", slot, font, 10, TextSecondary, TextAnchor.UpperLeft, slotNumber.ToString());
                SetRect(hotkey.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 1f), new Vector2(5f, -4f), new Vector2(-5f, -4f));

                Transform existing = slot.Find("Assignment");
                Text assignment;
                if (existing != null)
                    assignment = existing.GetComponent<Text>();
                else
                    assignment = TextObject("Assignment", slot, font, 9, TextPrimary, TextAnchor.MiddleCenter, string.Empty);
                SetRect(assignment.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(4f, 5f), new Vector2(-4f, -8f));
                assignment.horizontalOverflow = HorizontalWrapMode.Wrap;
                assignment.verticalOverflow = VerticalWrapMode.Truncate;

                StandaloneHotbarSlotView view = slot.GetComponent<StandaloneHotbarSlotView>();
                if (view == null)
                    view = slot.gameObject.AddComponent<StandaloneHotbarSlotView>();
                view.ConfigureForEditor(slotNumber, hotkey, assignment, tooltip);
                slots[i] = view;
            }

            ClientGameplayUIRoot gameplay = root.GetComponentInChildren<ClientGameplayUIRoot>(true);
            if (gameplay == null)
                throw new InvalidOperationException("ClientGameplayUIRoot was not found in StandaloneClientUI.prefab.");

            SerializedObject serialized = new SerializedObject(gameplay);
            SerializedProperty property = serialized.FindProperty("hotbarSlots");
            if (property == null)
                throw new InvalidOperationException("ClientGameplayUIRoot.hotbarSlots serialized field was not found.");
            property.arraySize = slots.Length;
            for (int i = 0; i < slots.Length; ++i)
                property.GetArrayElementAtIndex(i).objectReferenceValue = slots[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(gameplay);
        }

        private static RectTransform BuildScroll(string name, Transform parent, out ScrollRect scroll)
        {
            GameObject root = PanelObject(name, parent, Field);
            Stretch(root.GetComponent<RectTransform>());

            GameObject viewport = Node("Viewport", root.transform);
            Stretch(viewport.GetComponent<RectTransform>());
            Image viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.001f);
            Mask mask = viewport.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            GameObject content = Node("Content", viewport.transform);
            RectTransform cr = content.GetComponent<RectTransform>();
            cr.anchorMin = new Vector2(0f, 1f);
            cr.anchorMax = new Vector2(1f, 1f);
            cr.pivot = new Vector2(0.5f, 1f);
            cr.anchoredPosition = Vector2.zero;
            cr.sizeDelta = Vector2.zero;

            GameObject scrollbarGo = PanelObject("Scrollbar", root.transform, PanelRaised);
            RectTransform sbr = scrollbarGo.GetComponent<RectTransform>();
            sbr.anchorMin = new Vector2(1f, 0f);
            sbr.anchorMax = Vector2.one;
            sbr.pivot = new Vector2(1f, 0.5f);
            sbr.offsetMin = new Vector2(-12f, 4f);
            sbr.offsetMax = new Vector2(-4f, -4f);
            Scrollbar scrollbar = scrollbarGo.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            scroll = root.AddComponent<ScrollRect>();
            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = cr;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.scrollSensitivity = 24f;
            return cr;
        }

        private static void AddVerticalContentLayout(RectTransform content)
        {
            VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private static GameObject Node(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject PanelObject(string name, Transform parent, Color color)
        {
            GameObject go = Node(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        private static Text TextObject(string name, Transform parent, Font font, int size, Color color, TextAnchor anchor, string value)
        {
            GameObject go = Node(name, parent);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.text = value ?? string.Empty;
            text.raycastTarget = false;
            return text;
        }

        private static Button ButtonObject(string name, Transform parent, Font font, string label, Color color)
        {
            GameObject go = PanelObject(name, parent, color);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            Text text = TextObject("Label", go.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, label);
            Stretch(text.rectTransform);
            return button;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
#endif
