#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Client.OutfitAuthoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    public static class OutfitAuthoringEquipmentDetailsSceneInstaller
    {
        private static readonly string[] SceneCandidates =
        {
            "Assets/Scenes/OutfitAuthoring.unity",
            "Assets/Game/Client/OutfitAuthoring/Scenes/OutfitAuthoring.unity",
            "Assets/Game/Client/Editor/OutfitAuthoring/OutfitAuthoring.unity",
        };

        private const string DetailsPanelName = "EquipmentDetailsPanel";
        private const string ColorFoldoutButtonName = "ColorFoldoutButton";

        [MenuItem("MMO Tools/Characters/Outfit Builder/Install or Repair Equipment Details")]
        public static void InstallOrRepair()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog(
                    "Outfit Builder",
                    "Exit Play Mode before modifying the serialized Outfit Builder UI.",
                    "OK");
                return;
            }

            string scenePath = ResolvePreferredScenePath();
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                EditorUtility.DisplayDialog(
                    "Outfit Builder",
                    "Could not locate OutfitAuthoring.unity in any known current/legacy location.",
                    "OK");
                return;
            }

            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            Canvas canvas = UnityEngine.Object.FindObjectsOfType<Canvas>(true)
                .FirstOrDefault(x => x != null && x.gameObject.name == "Outfit Authoring UI");

            if (canvas == null)
            {
                Debug.LogError(
                    $"[OutfitAuthoring] Install failed in '{scenePath}': " +
                    "Canvas 'Outfit Authoring UI' was not found.");
                return;
            }

            Transform colorPanel = canvas.transform.Find("ColorPanel");
            Transform itemPanel = canvas.transform.Find("ItemPanel");

            if (colorPanel == null || itemPanel == null)
            {
                Debug.LogError(
                    $"[OutfitAuthoring] Install failed in '{scenePath}': " +
                    $"ColorPanel={(colorPanel != null ? "FOUND" : "MISSING")}, " +
                    $"ItemPanel={(itemPanel != null ? "FOUND" : "MISSING")}.");
                return;
            }

            InstallColorFoldout(colorPanel);
            InstallEquipmentDetails(canvas.transform);

            RectTransform colorRect = colorPanel.GetComponent<RectTransform>();
            RectTransform itemRect = itemPanel.GetComponent<RectTransform>();

            if (colorRect != null)
                colorRect.anchoredPosition = new Vector2(235f, -180f);

            if (itemRect != null)
                itemRect.anchoredPosition = new Vector2(235f, -875f);

            EditorSceneManager.MarkSceneDirty(scene);

            bool saved = EditorSceneManager.SaveScene(scene);
            if (!saved)
            {
                Debug.LogError(
                    $"[OutfitAuthoring] Install created UI in '{scenePath}' but Unity did not save the scene.");
                return;
            }

            OutfitAuthoringEquipmentDetailsPanel details =
                UnityEngine.Object.FindObjectsOfType<OutfitAuthoringEquipmentDetailsPanel>(true)
                    .FirstOrDefault();

            OutfitAuthoringColorFoldout foldout =
                UnityEngine.Object.FindObjectsOfType<OutfitAuthoringColorFoldout>(true)
                    .FirstOrDefault();

            Debug.Log(
                $"[OutfitAuthoring] Equipment Details install completed in '{scenePath}'. " +
                $"DetailsPanel={(details != null ? "FOUND" : "MISSING")} | " +
                $"ColorFoldout={(foldout != null ? "FOUND" : "MISSING")}.");

            if (details == null || foldout == null)
            {
                Debug.LogError(
                    "[OutfitAuthoring] Install did not produce both required serialized components. " +
                    "Do not enter Play Mode yet.");
            }
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Validate Equipment Details")]
        public static void Validate()
        {
            List<string> existingScenes = ExistingCandidatePaths();

            if (existingScenes.Count == 0)
            {
                Debug.LogError(
                    "[OutfitAuthoring] Equipment Details validation failed: " +
                    "no OutfitAuthoring.unity scene exists at any known path.");
                return;
            }

            var reports = new List<string>();

            for (int i = 0; i < existingScenes.Count; ++i)
            {
                string path = existingScenes[i];
                Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                Canvas canvas = UnityEngine.Object.FindObjectsOfType<Canvas>(true)
                    .FirstOrDefault(x => x != null && x.gameObject.name == "Outfit Authoring UI");

                OutfitAuthoringEquipmentDetailsPanel details =
                    UnityEngine.Object.FindObjectsOfType<OutfitAuthoringEquipmentDetailsPanel>(true)
                        .FirstOrDefault();

                OutfitAuthoringColorFoldout foldout =
                    UnityEngine.Object.FindObjectsOfType<OutfitAuthoringColorFoldout>(true)
                        .FirstOrDefault();

                Transform detailsObject =
                    canvas != null ? canvas.transform.Find(DetailsPanelName) : null;

                Transform colorPanel =
                    canvas != null ? canvas.transform.Find("ColorPanel") : null;

                reports.Add(
                    $"{path}\n" +
                    $"  Canvas: {(canvas != null ? "FOUND" : "MISSING")}\n" +
                    $"  EquipmentDetailsPanel object: {(detailsObject != null ? "FOUND" : "MISSING")}\n" +
                    $"  OutfitAuthoringEquipmentDetailsPanel component: {(details != null ? "FOUND" : "MISSING")}\n" +
                    $"  ColorPanel: {(colorPanel != null ? "FOUND" : "MISSING")}\n" +
                    $"  OutfitAuthoringColorFoldout component: {(foldout != null ? "FOUND" : "MISSING")}");

                if (canvas != null && detailsObject != null && details != null &&
                    colorPanel != null && foldout != null)
                {
                    Debug.Log(
                        "[OutfitAuthoring] Equipment Details validation passed.\n" +
                        reports[reports.Count - 1]);
                    return;
                }
            }

            Debug.LogError(
                "[OutfitAuthoring] Equipment Details validation failed in every discovered OutfitAuthoring scene.\n\n" +
                string.Join("\n\n", reports) +
                "\n\nRun 'Install or Repair Equipment Details' again. " +
                "The installer now logs the exact scene path it modifies and the component state immediately after saving.");
        }

        private static string ResolvePreferredScenePath()
        {
            // Prefer the currently open OutfitAuthoring scene if it is one of the known candidates.
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid() && !string.IsNullOrWhiteSpace(active.path))
            {
                for (int i = 0; i < SceneCandidates.Length; ++i)
                {
                    if (string.Equals(
                        active.path,
                        SceneCandidates[i],
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return SceneCandidates[i];
                    }
                }
            }

            // Current canonical moved location first.
            for (int i = 0; i < SceneCandidates.Length; ++i)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SceneCandidates[i]) != null)
                    return SceneCandidates[i];
            }

            return string.Empty;
        }

        private static List<string> ExistingCandidatePaths()
        {
            var result = new List<string>();

            for (int i = 0; i < SceneCandidates.Length; ++i)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SceneCandidates[i]) != null)
                    result.Add(SceneCandidates[i]);
            }

            return result;
        }

        private static void InstallColorFoldout(Transform colorPanel)
        {
            Transform oldButton = colorPanel.Find(ColorFoldoutButtonName);
            if (oldButton != null)
                UnityEngine.Object.DestroyImmediate(oldButton.gameObject);

            OutfitAuthoringColorFoldout oldComponent =
                colorPanel.GetComponent<OutfitAuthoringColorFoldout>();
            if (oldComponent != null)
                UnityEngine.Object.DestroyImmediate(oldComponent);

            List<GameObject> targets = new List<GameObject>();

            for (int i = 0; i < colorPanel.childCount; ++i)
            {
                Transform child = colorPanel.GetChild(i);
                if (child != null)
                    targets.Add(child.gameObject);
            }

            Button button = CreateButton(
                ColorFoldoutButtonName,
                colorPanel,
                "COLORS ▶",
                400f,
                30f);

            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -6f);

            OutfitAuthoringColorFoldout foldout =
                colorPanel.gameObject.AddComponent<OutfitAuthoringColorFoldout>();

            SerializedObject so = new SerializedObject(foldout);
            SetRef(so, "button", button);
            SetRef(so, "label", button.GetComponentInChildren<Text>(true));

            SerializedProperty targetsProperty = so.FindProperty("targets");
            targetsProperty.arraySize = targets.Count;

            for (int i = 0; i < targets.Count; ++i)
            {
                targetsProperty
                    .GetArrayElementAtIndex(i)
                    .objectReferenceValue = targets[i];
            }

            so.FindProperty("expanded").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();

            button.transform.SetAsLastSibling();
        }

        private static void InstallEquipmentDetails(Transform canvas)
        {
            Transform existing = canvas.Find(DetailsPanelName);
            if (existing != null)
                UnityEngine.Object.DestroyImmediate(existing.gameObject);

            GameObject panel = CreateUi(DetailsPanelName, canvas);

            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = new Vector2(235f, -515f);
            panelRect.sizeDelta = new Vector2(430f, 350f);

            panel.AddComponent<CanvasRenderer>();

            Image bg = panel.AddComponent<Image>();
            bg.color = new Color(0.075f, 0.085f, 0.1f, 0.97f);

            Button foldout = CreateButton(
                "Header",
                panel.transform,
                "EQUIPMENT DETAILS ▼",
                404f,
                32f);
            SetRect(
                foldout.GetComponent<RectTransform>(),
                13f,
                -10f,
                404f,
                32f);

            GameObject body = CreateUi("Body", panel.transform);

            RectTransform bodyRect = body.GetComponent<RectTransform>();
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = new Vector2(13f, 12f);
            bodyRect.offsetMax = new Vector2(-13f, -50f);

            Text definition = CreateText(
                "DefinitionId",
                body.transform,
                "Definition ID: assigned on create",
                11,
                TextAnchor.MiddleLeft);
            SetRect(definition.rectTransform, 0f, 0f, 404f, 22f);

            Text kind = CreateText(
                "KindSubtype",
                body.transform,
                "Kind: Equipment   |   Subtype: Armor",
                11,
                TextAnchor.MiddleLeft);
            SetRect(kind.rectTransform, 0f, -24f, 404f, 22f);

            Text slot = CreateText(
                "Slot",
                body.transform,
                "Equipment Slot: uses existing Outfit Builder dropdown",
                11,
                TextAnchor.MiddleLeft);
            SetRect(slot.rectTransform, 0f, -48f, 404f, 22f);

            Text cache = CreateText(
                "CacheStatus",
                body.transform,
                "Client Cache: not applicable until item exists",
                10,
                TextAnchor.MiddleLeft);
            cache.color = new Color(0.66f, 0.82f, 1f, 1f);
            SetRect(cache.rectTransform, 0f, -72f, 404f, 22f);

            InputField maxStack = CreateLabeledInput(
                body.transform,
                "Max Stack",
                "1",
                0f,
                -102f,
                126f);

            InputField weight = CreateLabeledInput(
                body.transform,
                "Weight",
                "0",
                139f,
                -102f,
                126f);

            InputField durability = CreateLabeledInput(
                body.transform,
                "Max Durability",
                "0",
                278f,
                -102f,
                126f);

            Text tagsLabel = CreateText(
                "TagsLabel",
                body.transform,
                "Tags",
                11,
                TextAnchor.MiddleLeft);
            SetRect(tagsLabel.rectTransform, 0f, -154f, 55f, 26f);

            OutfitAuthoringDropdown tag1 = CreateDropdown("Tag1", body.transform, 108f);
            SetRect(tag1.GetComponent<RectTransform>(), 58f, -154f, 108f, 28f);

            OutfitAuthoringDropdown tag2 = CreateDropdown("Tag2", body.transform, 108f);
            SetRect(tag2.GetComponent<RectTransform>(), 176f, -154f, 108f, 28f);

            OutfitAuthoringDropdown tag3 = CreateDropdown("Tag3", body.transform, 108f);
            SetRect(tag3.GetComponent<RectTransform>(), 294f, -154f, 108f, 28f);

            Text modsLabel = CreateText(
                "ModifiersLabel",
                body.transform,
                "Stat Modifiers    Stat ID                   Add          Mult",
                10,
                TextAnchor.MiddleLeft);
            SetRect(modsLabel.rectTransform, 0f, -190f, 404f, 20f);

            ModifierRow row1 = CreateModifierRow(body.transform, "Stat1", -214f);
            ModifierRow row2 = CreateModifierRow(body.transform, "Stat2", -248f);
            ModifierRow row3 = CreateModifierRow(body.transform, "Stat3", -282f);

            OutfitAuthoringEquipmentDetailsPanel component =
                panel.AddComponent<OutfitAuthoringEquipmentDetailsPanel>();

            SerializedObject so = new SerializedObject(component);

            SetRef(so, "foldoutButton", foldout);
            SetRef(so, "foldoutLabel", foldout.GetComponentInChildren<Text>(true));
            SetRef(so, "bodyRoot", body);
            SetRef(so, "definitionIdText", definition);
            SetRef(so, "kindSubtypeText", kind);
            SetRef(so, "slotText", slot);
            SetRef(so, "cacheStatusText", cache);
            SetRef(so, "maxStackInput", maxStack);
            SetRef(so, "weightInput", weight);
            SetRef(so, "maxDurabilityInput", durability);
            SetRef(so, "tag1Dropdown", tag1);
            SetRef(so, "tag2Dropdown", tag2);
            SetRef(so, "tag3Dropdown", tag3);
            SetRef(so, "stat1Dropdown", row1.dropdown);
            SetRef(so, "stat1AdditiveInput", row1.additive);
            SetRef(so, "stat1MultiplierInput", row1.multiplier);
            SetRef(so, "stat2Dropdown", row2.dropdown);
            SetRef(so, "stat2AdditiveInput", row2.additive);
            SetRef(so, "stat2MultiplierInput", row2.multiplier);
            SetRef(so, "stat3Dropdown", row3.dropdown);
            SetRef(so, "stat3AdditiveInput", row3.additive);
            SetRef(so, "stat3MultiplierInput", row3.multiplier);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private readonly struct ModifierRow
        {
            public readonly OutfitAuthoringDropdown dropdown;
            public readonly InputField additive;
            public readonly InputField multiplier;

            public ModifierRow(
                OutfitAuthoringDropdown dropdown,
                InputField additive,
                InputField multiplier)
            {
                this.dropdown = dropdown;
                this.additive = additive;
                this.multiplier = multiplier;
            }
        }

        private static ModifierRow CreateModifierRow(
            Transform parent,
            string name,
            float y)
        {
            OutfitAuthoringDropdown dropdown =
                CreateDropdown(name + "Dropdown", parent, 210f);
            SetRect(dropdown.GetComponent<RectTransform>(), 0f, y, 210f, 28f);

            InputField additive = CreateInput(name + "Add", parent, "0");
            SetRect(additive.GetComponent<RectTransform>(), 220f, y, 82f, 28f);

            InputField multiplier = CreateInput(name + "Mult", parent, "1");
            SetRect(multiplier.GetComponent<RectTransform>(), 312f, y, 90f, 28f);

            return new ModifierRow(dropdown, additive, multiplier);
        }

        private static InputField CreateLabeledInput(
            Transform parent,
            string label,
            string initialValue,
            float x,
            float y,
            float width)
        {
            GameObject root = CreateUi(label.Replace(" ", string.Empty), parent);
            SetRect(root.GetComponent<RectTransform>(), x, y, width, 46f);

            Text text = CreateText(
                "Label",
                root.transform,
                label,
                10,
                TextAnchor.MiddleLeft);
            SetRect(text.rectTransform, 0f, 0f, width, 18f);

            InputField input = CreateInput("Input", root.transform, initialValue);
            SetRect(input.GetComponent<RectTransform>(), 0f, -20f, width, 26f);

            return input;
        }

        private static InputField CreateInput(
            string name,
            Transform parent,
            string initialValue)
        {
            GameObject go = CreateUi(name, parent);
            go.AddComponent<CanvasRenderer>();

            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0.12f, 0.14f, 0.18f, 1f);

            InputField field = go.AddComponent<InputField>();

            Text text = CreateText(
                "Text",
                go.transform,
                initialValue,
                11,
                TextAnchor.MiddleLeft);

            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(7f, 2f);
            text.rectTransform.offsetMax = new Vector2(-7f, -2f);

            field.textComponent = text;
            field.text = initialValue;
            field.targetGraphic = bg;

            return field;
        }

        private static OutfitAuthoringDropdown CreateDropdown(
            string name,
            Transform parent,
            float width)
        {
            GameObject root = CreateUi(name, parent);

            Button open = CreateButton(
                "Open",
                root.transform,
                "(None)",
                width,
                28f);

            RectTransform openRect = open.GetComponent<RectTransform>();
            openRect.anchorMin = Vector2.zero;
            openRect.anchorMax = Vector2.one;
            openRect.offsetMin = Vector2.zero;
            openRect.offsetMax = Vector2.zero;

            Text selected = open.GetComponentInChildren<Text>(true);

            GameObject panel = CreateUi("OptionsPanel", root.transform);
            panel.AddComponent<CanvasRenderer>();

            Image image = panel.AddComponent<Image>();
            image.color = new Color(0.07f, 0.08f, 0.1f, 0.995f);

            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = new Vector2(0f, -2f);
            panelRect.sizeDelta = new Vector2(0f, 220f);

            ScrollRect scroll = panel.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;

            GameObject viewport = CreateUi("Viewport", panel.transform);

            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(3f, 3f);
            viewportRect.offsetMax = new Vector2(-3f, -3f);

            viewport.AddComponent<RectMask2D>();

            GameObject content = CreateUi("Content", viewport.transform);

            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 40f);

            scroll.viewport = viewportRect;
            scroll.content = contentRect;

            Button template = CreateButton(
                "OptionTemplate",
                content.transform,
                "Option",
                width - 12f,
                28f);

            template.gameObject.SetActive(false);

            OutfitAuthoringDropdown dropdown =
                root.AddComponent<OutfitAuthoringDropdown>();

            SerializedObject so = new SerializedObject(dropdown);
            SetRef(so, "openButton", open);
            SetRef(so, "selectedText", selected);
            SetRef(so, "optionsPanel", panel);
            SetRef(so, "optionsContainer", contentRect);
            SetRef(so, "optionTemplate", template);
            so.ApplyModifiedPropertiesWithoutUndo();

            return dropdown;
        }

        private static Button CreateButton(
            string name,
            Transform parent,
            string label,
            float width,
            float height)
        {
            GameObject go = CreateUi(name, parent);
            go.AddComponent<CanvasRenderer>();

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.14f, 0.18f, 0.25f, 1f);

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;

            Text text = CreateText(
                "Label",
                go.transform,
                label,
                11,
                TextAnchor.MiddleCenter);

            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);

            return button;
        }

        private static Text CreateText(
            string name,
            Transform parent,
            string value,
            int fontSize,
            TextAnchor alignment)
        {
            GameObject go = CreateUi(name, parent);
            go.AddComponent<CanvasRenderer>();

            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            text.raycastTarget = false;
            text.text = value;

            return text;
        }

        private static GameObject CreateUi(
            string name,
            Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void SetRef(
            SerializedObject so,
            string propertyName,
            UnityEngine.Object value)
        {
            SerializedProperty property = so.FindProperty(propertyName);

            if (property == null)
                throw new InvalidOperationException(
                    $"Missing serialized property '{propertyName}'.");

            property.objectReferenceValue = value;
        }

        private static void SetRect(
            RectTransform rect,
            float x,
            float y,
            float width,
            float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
#endif
