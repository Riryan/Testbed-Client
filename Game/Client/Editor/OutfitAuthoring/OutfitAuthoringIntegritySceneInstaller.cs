#if UNITY_EDITOR
using System;
using System.Linq;
using Game.Client.OutfitAuthoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    public static class OutfitAuthoringIntegritySceneInstaller
    {
        private const string ScenePath = "Assets/Game/Client/Editor/OutfitAuthoring/OutfitAuthoring.unity";
        private const string PanelName = "IntegrityOverlayPanel";

        [MenuItem("MMO Tools/Characters/Outfit Builder/Install or Repair Integrity Overlay")]
        public static void InstallOrRepair()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Outfit Integrity Overlay", "Exit Play Mode before installing or repairing scene UI.", "OK");
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Canvas canvas = UnityEngine.Object.FindObjectsOfType<Canvas>(true)
                .FirstOrDefault(x => x != null && x.gameObject.name == "Outfit Authoring UI");

            if (canvas == null)
            {
                EditorUtility.DisplayDialog("Outfit Integrity Overlay", "The serialized 'Outfit Authoring UI' Canvas was not found.", "OK");
                return;
            }

            Transform existing = canvas.transform.Find(PanelName);
            if (existing != null)
                UnityEngine.Object.DestroyImmediate(existing.gameObject);

            GameObject panel = CreateUi(PanelName, canvas.transform);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 1f);
            panelRect.anchorMax = new Vector2(0.5f, 1f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = new Vector2(0f, -78f);
            panelRect.sizeDelta = new Vector2(690f, 300f);

            Image panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.055f, 0.065f, 0.08f, 0.97f);

            Text header = CreateText("Header", panel.transform, "CATALOG INTEGRITY / EXISTING OUTFITS", 17, TextAnchor.MiddleLeft);
            SetRect(header.rectTransform, 14f, -8f, 570f, 30f);

            Button collapse = CreateButton("Collapse", panel.transform, "HIDE", 74f, 28f);
            SetRect(collapse.GetComponent<RectTransform>(), 601f, -8f, 74f, 28f);

            GameObject body = CreateUi("Body", panel.transform);
            RectTransform bodyRect = body.GetComponent<RectTransform>();
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = new Vector2(10f, 10f);
            bodyRect.offsetMax = new Vector2(-10f, -44f);

            Text summary = CreateText("Summary", body.transform, "Not scanned.", 12, TextAnchor.UpperLeft);
            SetRect(summary.rectTransform, 0f, 0f, 670f, 48f);

            Text existingLabel = CreateText("ExistingLabel", body.transform, "Existing Item", 12, TextAnchor.MiddleLeft);
            SetRect(existingLabel.rectTransform, 0f, -52f, 100f, 30f);

            OutfitAuthoringDropdown dropdown = CreateDropdown("ExistingItemDropdown", body.transform);
            SetRect(dropdown.GetComponent<RectTransform>(), 108f, -52f, 562f, 34f);

            Button previous = CreateButton("Previous", body.transform, "< PREV", 92f, 32f);
            SetRect(previous.GetComponent<RectTransform>(), 0f, -94f, 92f, 32f);

            Button next = CreateButton("Next", body.transform, "NEXT >", 92f, 32f);
            SetRect(next.GetComponent<RectTransform>(), 100f, -94f, 92f, 32f);

            Button load = CreateButton("Load", body.transform, "LOAD ON MANNEQUIN", 180f, 32f);
            SetRect(load.GetComponent<RectTransform>(), 200f, -94f, 180f, 32f);

            Button refresh = CreateButton("Refresh", body.transform, "REFRESH / VALIDATE", 145f, 32f);
            SetRect(refresh.GetComponent<RectTransform>(), 388f, -94f, 145f, 32f);

            Button problems = CreateButton("ProblemsOnly", body.transform, "PROBLEMS ONLY", 129f, 32f);
            SetRect(problems.GetComponent<RectTransform>(), 541f, -94f, 129f, 32f);
            Text problemsText = problems.GetComponentInChildren<Text>(true);

            Text detail = CreateText("Detail", body.transform, "Select an item.", 12, TextAnchor.UpperLeft);
            SetRect(detail.rectTransform, 0f, -134f, 670f, 110f);
            detail.horizontalOverflow = HorizontalWrapMode.Wrap;
            detail.verticalOverflow = VerticalWrapMode.Overflow;

            OutfitAuthoringIntegrityPanel component = panel.AddComponent<OutfitAuthoringIntegrityPanel>();
            var so = new SerializedObject(component);
            SetRef(so, "itemDropdown", dropdown);
            SetRef(so, "summaryText", summary);
            SetRef(so, "detailText", detail);
            SetRef(so, "problemsButtonText", problemsText);
            SetRef(so, "previousButton", previous);
            SetRef(so, "nextButton", next);
            SetRef(so, "loadButton", load);
            SetRef(so, "refreshButton", refresh);
            SetRef(so, "problemsOnlyButton", problems);
            SetRef(so, "collapseButton", collapse);
            SetRef(so, "collapseButtonText", collapse.GetComponentInChildren<Text>(true));
            SetRef(so, "bodyRoot", body);
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = panel;
            Debug.Log("[OutfitAuthoring] Integrity overlay installed/repaired and serialized into OutfitAuthoring.unity.");
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Validate Integrity Overlay")]
        public static void Validate()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Canvas canvas = UnityEngine.Object.FindObjectsOfType<Canvas>(true)
                .FirstOrDefault(x => x != null && x.gameObject.name == "Outfit Authoring UI");

            Transform panel = canvas != null ? canvas.transform.Find(PanelName) : null;
            OutfitAuthoringIntegrityPanel component =
                panel != null ? panel.GetComponent<OutfitAuthoringIntegrityPanel>() : null;

            if (canvas == null || panel == null || component == null)
            {
                Debug.LogError("[OutfitAuthoring] Integrity validation failed: serialized overlay is not installed correctly.");
                return;
            }

            Debug.Log("[OutfitAuthoring] Integrity overlay validation passed. Serialized scene UI is installed.");
        }

        private static void SetRef(SerializedObject so, string propertyName, UnityEngine.Object value)
        {
            SerializedProperty property = so.FindProperty(propertyName);
            if (property == null)
                throw new InvalidOperationException($"Missing serialized property '{propertyName}'.");
            property.objectReferenceValue = value;
        }

        private static GameObject CreateUi(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Text CreateText(string name, Transform parent, string value, int fontSize, TextAnchor alignment)
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

        private static Button CreateButton(string name, Transform parent, string label, float width, float height)
        {
            GameObject go = CreateUi(name, parent);
            go.AddComponent<CanvasRenderer>();
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.15f, 0.19f, 0.27f, 1f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;

            Text text = CreateText("Label", go.transform, label, 12, TextAnchor.MiddleCenter);
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            return button;
        }

        private static OutfitAuthoringDropdown CreateDropdown(string name, Transform parent)
        {
            GameObject root = CreateUi(name, parent);

            Button openButton = CreateButton("Open", root.transform, "No options", 562f, 34f);
            RectTransform openRect = openButton.GetComponent<RectTransform>();
            openRect.anchorMin = Vector2.zero;
            openRect.anchorMax = Vector2.one;
            openRect.offsetMin = Vector2.zero;
            openRect.offsetMax = Vector2.zero;
            Text selectedText = openButton.GetComponentInChildren<Text>(true);

            GameObject optionsPanel = CreateUi("OptionsPanel", root.transform);
            optionsPanel.AddComponent<CanvasRenderer>();
            Image panelImage = optionsPanel.AddComponent<Image>();
            panelImage.color = new Color(0.075f, 0.085f, 0.11f, 0.99f);

            RectTransform optionsRect = optionsPanel.GetComponent<RectTransform>();
            optionsRect.anchorMin = new Vector2(0f, 0f);
            optionsRect.anchorMax = new Vector2(1f, 0f);
            optionsRect.pivot = new Vector2(0.5f, 1f);
            optionsRect.anchoredPosition = new Vector2(0f, -4f);
            optionsRect.sizeDelta = new Vector2(0f, 280f);

            ScrollRect scroll = optionsPanel.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            GameObject viewport = CreateUi("Viewport", optionsPanel.transform);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(4f, 4f);
            viewportRect.offsetMax = new Vector2(-4f, -4f);
            viewport.AddComponent<RectMask2D>();

            GameObject content = CreateUi("Content", viewport.transform);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 40f);
            scroll.viewport = viewportRect;
            scroll.content = contentRect;

            Button template = CreateButton("OptionTemplate", content.transform, "Option", 540f, 30f);
            template.gameObject.SetActive(false);
            RectTransform templateRect = template.GetComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0f, 1f);
            templateRect.anchorMax = new Vector2(1f, 1f);
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.anchoredPosition = new Vector2(0f, -6f);
            templateRect.sizeDelta = new Vector2(-12f, 30f);

            OutfitAuthoringDropdown dropdown = root.AddComponent<OutfitAuthoringDropdown>();
            var so = new SerializedObject(dropdown);
            SetRef(so, "openButton", openButton);
            SetRef(so, "selectedText", selectedText);
            SetRef(so, "optionsPanel", optionsPanel);
            SetRef(so, "optionsContainer", contentRect);
            SetRef(so, "optionTemplate", template);
            so.ApplyModifiedPropertiesWithoutUndo();

            return dropdown;
        }

        private static void SetRect(RectTransform rect, float x, float y, float width, float height)
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
