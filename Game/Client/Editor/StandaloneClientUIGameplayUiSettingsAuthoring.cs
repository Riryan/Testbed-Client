#if UNITY_EDITOR
using System;
using Game.Client.UI.Standalone;
using Player.Client;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Gameplay/UI Settings V2.10 authored-prefab upgrade.
    /// Replaces only GAMEPLAYUIPage and adds a serialized runtime settings applier to the prefab root.
    /// </summary>
    public static class StandaloneClientUIGameplayUiSettingsAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color PanelRaised = new Color32(27, 31, 40, 248);
        private static readonly Color Field = new Color32(10, 13, 18, 240);
        private static readonly Color TextPrimary = new Color32(239, 242, 246, 255);
        private static readonly Color TextSecondary = new Color32(167, 178, 194, 255);
        private static readonly Color AccentText = new Color32(230, 193, 107, 255);

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Gameplay UI Settings V2.10")]
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
                Transform page = root.transform.Find("Gameplay/Windows/SettingsWindow/Body/Content/GAMEPLAYUIPage");
                if (page == null)
                    throw new InvalidOperationException("Settings GAMEPLAYUIPage was not found. Install/run Settings V2.07 first.");

                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                            Resources.GetBuiltinResource<Font>("Arial.ttf");

                for (int i = page.childCount - 1; i >= 0; --i)
                    UnityEngine.Object.DestroyImmediate(page.GetChild(i).gameObject);

                StandaloneGameplayUiSettingsPage oldPage = page.GetComponent<StandaloneGameplayUiSettingsPage>();
                if (oldPage != null)
                    UnityEngine.Object.DestroyImmediate(oldPage);

                BuildGameplayUiPage(page, font);

                CanvasScaler scaler = root.GetComponent<CanvasScaler>();
                if (scaler == null)
                    throw new InvalidOperationException("StandaloneClientUI root CanvasScaler was not found.");

                StandaloneGameplayUiRuntimeSettings runtime = root.GetComponent<StandaloneGameplayUiRuntimeSettings>();
                if (runtime == null)
                    runtime = root.AddComponent<StandaloneGameplayUiRuntimeSettings>();
                runtime.ConfigureForEditor(scaler);

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
            Debug.Log("[StandaloneUI] Gameplay/UI Settings V2.10 authored: local mouse sensitivity, invert Y, UI scale, chat timestamps, and exploration interaction dot. No GameServer traffic is introduced.");
        }

        private static void BuildGameplayUiPage(Transform page, Font font)
        {
            Text heading = TextObject("Heading", page, font, 18, TextPrimary, TextAnchor.MiddleLeft, "GAMEPLAY / UI");
            SetRect(heading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(16f, -8f), new Vector2(-150f, 34f));

            Text localOnly = TextObject("LocalOnly", page, font, 12, TextSecondary, TextAnchor.MiddleLeft,
                "Saved on this machine only. These presentation preferences are never sent to the GameServer.");
            SetRect(localOnly.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(16f, -40f), new Vector2(-24f, 26f));

            GameObject rows = Node("GameplayUiRows", page);
            RectTransform rowsRect = rows.GetComponent<RectTransform>();
            rowsRect.anchorMin = new Vector2(0f, 0f);
            rowsRect.anchorMax = new Vector2(1f, 1f);
            rowsRect.offsetMin = new Vector2(16f, 116f);
            rowsRect.offsetMax = new Vector2(-16f, -88f);
            VerticalLayoutGroup layout = rows.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;

            BuildSliderRow(rows.transform, font, "Mouse Sensitivity", "Local camera-look multiplier.", 0.25f, 2.00f, 1f,
                out Slider mouseSensitivity, out Text mouseSensitivityValue);
            BuildToggleRow(rows.transform, font, "Invert Y", "Invert vertical camera look.",
                out Button invertY, out Text invertYValue);
            BuildSliderRow(rows.transform, font, "UI Scale", "Scale the authored standalone client UI.", 0.75f, 1.50f, 1f,
                out Slider uiScale, out Text uiScaleValue);
            BuildToggleRow(rows.transform, font, "Chat Timestamps", "Show local HH:mm timestamps using the existing pushed chat timestamp.",
                out Button chatTimestamps, out Text chatTimestampsValue);
            BuildToggleRow(rows.transform, font, "Interaction Dot", "Show the small center dot while outside combat stance.",
                out Button interactionDot, out Text interactionDotValue);

            Text status = TextObject("Status", page, font, 12, AccentText, TextAnchor.MiddleLeft,
                "Gameplay/UI settings are local to this client.");
            SetRect(status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(16f, 50f), new Vector2(-290f, 54f));
            status.horizontalOverflow = HorizontalWrapMode.Wrap;
            status.verticalOverflow = VerticalWrapMode.Truncate;

            Button reset = ButtonObject("ResetDefaults", page, font, "RESET DEFAULTS", PanelRaised);
            RectTransform resetRect = reset.GetComponent<RectTransform>();
            resetRect.anchorMin = resetRect.anchorMax = new Vector2(1f, 0f);
            resetRect.pivot = new Vector2(1f, 0f);
            resetRect.anchoredPosition = new Vector2(-142f, 14f);
            resetRect.sizeDelta = new Vector2(132f, 34f);

            Button apply = ButtonObject("Apply", page, font, "APPLY", PanelRaised);
            RectTransform applyRect = apply.GetComponent<RectTransform>();
            applyRect.anchorMin = applyRect.anchorMax = new Vector2(1f, 0f);
            applyRect.pivot = new Vector2(1f, 0f);
            applyRect.anchoredPosition = new Vector2(-16f, 14f);
            applyRect.sizeDelta = new Vector2(112f, 34f);

            StandaloneGameplayUiSettingsPage controller = page.gameObject.AddComponent<StandaloneGameplayUiSettingsPage>();
            controller.ConfigureForEditor(
                mouseSensitivity, mouseSensitivityValue,
                invertY, invertYValue,
                uiScale, uiScaleValue,
                chatTimestamps, chatTimestampsValue,
                interactionDot, interactionDotValue,
                apply, reset, status);
        }

        private static void BuildSliderRow(
            Transform parent,
            Font font,
            string label,
            string description,
            float minimum,
            float maximum,
            float defaultValue,
            out Slider slider,
            out Text value)
        {
            GameObject row = PanelObject(label.Replace(" ", string.Empty) + "Row", parent, PanelRaised);
            LayoutElement element = row.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 68f;

            Text title = TextObject("Label", row.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, label.ToUpperInvariant());
            SetRect(title.rectTransform, new Vector2(0f, 0f), new Vector2(0.40f, 1f), new Vector2(0f, 0.5f), new Vector2(12f, 8f), new Vector2(-8f, -4f));
            Text detail = TextObject("Description", row.transform, font, 11, TextSecondary, TextAnchor.MiddleLeft, description);
            SetRect(detail.rectTransform, new Vector2(0f, 0f), new Vector2(0.47f, 1f), new Vector2(0f, 0.5f), new Vector2(12f, -15f), new Vector2(-8f, -28f));

            GameObject sliderRoot = PanelObject("Slider", row.transform, Field);
            RectTransform sr = sliderRoot.GetComponent<RectTransform>();
            sr.anchorMin = new Vector2(0.50f, 0.5f);
            sr.anchorMax = new Vector2(0.90f, 0.5f);
            sr.pivot = new Vector2(0f, 0.5f);
            sr.offsetMin = new Vector2(0f, -17f);
            sr.offsetMax = new Vector2(0f, 17f);

            GameObject background = PanelObject("Background", sliderRoot.transform, new Color32(47, 54, 66, 255));
            RectTransform br = background.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0f, 0.5f);
            br.anchorMax = new Vector2(1f, 0.5f);
            br.offsetMin = new Vector2(8f, -4f);
            br.offsetMax = new Vector2(-8f, 4f);

            GameObject fillArea = Node("Fill Area", sliderRoot.transform);
            RectTransform far = fillArea.GetComponent<RectTransform>();
            far.anchorMin = Vector2.zero;
            far.anchorMax = Vector2.one;
            far.offsetMin = new Vector2(8f, 0f);
            far.offsetMax = new Vector2(-8f, 0f);
            GameObject fill = PanelObject("Fill", fillArea.transform, new Color32(103, 132, 171, 255));
            Stretch(fill.GetComponent<RectTransform>());

            GameObject handleArea = Node("Handle Slide Area", sliderRoot.transform);
            RectTransform har = handleArea.GetComponent<RectTransform>();
            har.anchorMin = Vector2.zero;
            har.anchorMax = Vector2.one;
            har.offsetMin = new Vector2(8f, 0f);
            har.offsetMax = new Vector2(-8f, 0f);
            GameObject handle = PanelObject("Handle", handleArea.transform, TextPrimary);
            RectTransform hr = handle.GetComponent<RectTransform>();
            hr.sizeDelta = new Vector2(14f, 24f);
            hr.anchorMin = hr.anchorMax = new Vector2(0.5f, 0.5f);
            hr.pivot = new Vector2(0.5f, 0.5f);

            slider = sliderRoot.AddComponent<Slider>();
            slider.minValue = minimum;
            slider.maxValue = maximum;
            slider.value = defaultValue;
            slider.wholeNumbers = false;
            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.handleRect = handle.GetComponent<RectTransform>();
            slider.targetGraphic = handle.GetComponent<Image>();

            value = TextObject("Value", row.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, "100%");
            RectTransform vr = value.rectTransform;
            vr.anchorMin = new Vector2(0.91f, 0.5f);
            vr.anchorMax = new Vector2(1f, 0.5f);
            vr.pivot = new Vector2(0f, 0.5f);
            vr.offsetMin = new Vector2(4f, -17f);
            vr.offsetMax = new Vector2(-6f, 17f);
        }

        private static void BuildToggleRow(
            Transform parent,
            Font font,
            string label,
            string description,
            out Button toggle,
            out Text value)
        {
            GameObject row = PanelObject(label.Replace(" ", string.Empty) + "Row", parent, PanelRaised);
            LayoutElement element = row.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 68f;

            Text title = TextObject("Label", row.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, label.ToUpperInvariant());
            SetRect(title.rectTransform, new Vector2(0f, 0f), new Vector2(0.55f, 1f), new Vector2(0f, 0.5f), new Vector2(12f, 8f), new Vector2(-8f, -4f));
            Text detail = TextObject("Description", row.transform, font, 11, TextSecondary, TextAnchor.MiddleLeft, description);
            SetRect(detail.rectTransform, new Vector2(0f, 0f), new Vector2(0.67f, 1f), new Vector2(0f, 0.5f), new Vector2(12f, -15f), new Vector2(-8f, -28f));

            toggle = ButtonObject("Toggle", row.transform, font, string.Empty, Field);
            RectTransform toggleRect = toggle.GetComponent<RectTransform>();
            toggleRect.anchorMin = toggleRect.anchorMax = new Vector2(1f, 0.5f);
            toggleRect.pivot = new Vector2(1f, 0.5f);
            toggleRect.anchoredPosition = new Vector2(-6f, 0f);
            toggleRect.sizeDelta = new Vector2(170f, 34f);

            Text existing = toggle.GetComponentInChildren<Text>(true);
            if (existing != null)
                UnityEngine.Object.DestroyImmediate(existing.gameObject);
            value = TextObject("Text", toggle.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, "OFF");
            Stretch(value.rectTransform);
        }

        private static GameObject Node(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject PanelObject(string name, Transform parent, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static Text TextObject(string name, Transform parent, Font font, int size, Color color, TextAnchor anchor, string textValue)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.text = textValue;
            text.raycastTarget = false;
            return text;
        }

        private static Button ButtonObject(string name, Transform parent, Font font, string label, Color background)
        {
            GameObject go = PanelObject(name, parent, background);
            Button button = go.AddComponent<Button>();
            Text text = TextObject("Text", go.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, label);
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

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 position,
            Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}
#endif
