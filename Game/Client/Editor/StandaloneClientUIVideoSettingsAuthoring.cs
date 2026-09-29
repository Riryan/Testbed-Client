#if UNITY_EDITOR
using System;
using Game.Client.UI.Standalone;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Video Settings V2.08 authored-prefab upgrade.
    /// Patches only the existing VIDEO page so user dressing/layout changes elsewhere in
    /// StandaloneClientUI.prefab are preserved.
    /// </summary>
    public static class StandaloneClientUIVideoSettingsAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color PanelRaised = new Color32(27, 31, 40, 248);
        private static readonly Color Field = new Color32(10, 13, 18, 240);
        private static readonly Color TextPrimary = new Color32(239, 242, 246, 255);
        private static readonly Color TextSecondary = new Color32(167, 178, 194, 255);
        private static readonly Color AccentText = new Color32(230, 193, 107, 255);

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Video Settings V2.08")]
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
                Transform page = root.transform.Find("Gameplay/Windows/SettingsWindow/Body/Content/VIDEOPage");
                if (page == null)
                    throw new InvalidOperationException("Settings VIDEOPage was not found. Install/run Settings V2.07 first.");

                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                            Resources.GetBuiltinResource<Font>("Arial.ttf");

                // Preserve the page object itself because StandaloneSettingsTabs already points to it.
                for (int i = page.childCount - 1; i >= 0; --i)
                    UnityEngine.Object.DestroyImmediate(page.GetChild(i).gameObject);

                StandaloneVideoSettingsPage oldController = page.GetComponent<StandaloneVideoSettingsPage>();
                if (oldController != null)
                    UnityEngine.Object.DestroyImmediate(oldController);

                BuildVideoPage(page, font);

                if (root.GetComponent<StandaloneLocalSettingsBootstrap>() == null)
                    root.AddComponent<StandaloneLocalSettingsBootstrap>();

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
            Debug.Log("[StandaloneUI] Video Settings V2.08 authored. Resolution, display mode, VSync, frame cap, and quality are local-machine-only and applied from saved local settings at startup.");
        }

        private static void BuildVideoPage(Transform page, Font font)
        {
            Text heading = TextObject("Heading", page, font, 18, TextPrimary, TextAnchor.MiddleLeft, "VIDEO");
            SetRect(heading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(16f, -8f), new Vector2(-150f, 34f));

            Text localOnly = TextObject("LocalOnly", page, font, 12, TextSecondary, TextAnchor.MiddleLeft,
                "Saved on this machine only. Video preferences never generate GameServer traffic.");
            SetRect(localOnly.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(16f, -40f), new Vector2(-24f, 26f));

            GameObject rows = Node("VideoRows", page);
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

            BuildCycleRow(rows.transform, font, "Resolution", "Screen resolution.", out Button resolutionPrevious, out Button resolutionNext, out Text resolutionValue);
            BuildCycleRow(rows.transform, font, "Display Mode", "Borderless, exclusive fullscreen, or windowed.", out Button displayPrevious, out Button displayNext, out Text displayValue);
            BuildCycleRow(rows.transform, font, "Quality", "Uses the project's existing Unity quality levels.", out Button qualityPrevious, out Button qualityNext, out Text qualityValue);
            BuildToggleRow(rows.transform, font, "VSync", "Synchronize presentation to the display refresh rate.", out Button vSyncButton, out Text vSyncValue);
            BuildCycleRow(rows.transform, font, "Frame Limit", "Used when VSync is off.", out Button framePrevious, out Button frameNext, out Text frameValue);

            Text status = TextObject("Status", page, font, 12, AccentText, TextAnchor.MiddleLeft,
                "Changes apply locally to this client only.");
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

            StandaloneVideoSettingsPage controller = page.gameObject.AddComponent<StandaloneVideoSettingsPage>();
            controller.ConfigureForEditor(
                resolutionPrevious,
                resolutionNext,
                resolutionValue,
                displayPrevious,
                displayNext,
                displayValue,
                qualityPrevious,
                qualityNext,
                qualityValue,
                vSyncButton,
                vSyncValue,
                framePrevious,
                frameNext,
                frameValue,
                apply,
                reset,
                status);
        }

        private static void BuildCycleRow(
            Transform parent,
            Font font,
            string label,
            string description,
            out Button previous,
            out Button next,
            out Text value)
        {
            GameObject row = PanelObject(label.Replace(" ", string.Empty) + "Row", parent, PanelRaised);
            LayoutElement element = row.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 68f;

            Text title = TextObject("Label", row.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, label.ToUpperInvariant());
            SetRect(title.rectTransform, new Vector2(0f, 0f), new Vector2(0.42f, 1f), new Vector2(0f, 0.5f), new Vector2(12f, 8f), new Vector2(-8f, -4f));

            Text detail = TextObject("Description", row.transform, font, 11, TextSecondary, TextAnchor.MiddleLeft, description);
            SetRect(detail.rectTransform, new Vector2(0f, 0f), new Vector2(0.52f, 1f), new Vector2(0f, 0.5f), new Vector2(12f, -15f), new Vector2(-8f, -28f));

            previous = ButtonObject("Previous", row.transform, font, "<", Field);
            RectTransform previousRect = previous.GetComponent<RectTransform>();
            previousRect.anchorMin = previousRect.anchorMax = new Vector2(0.56f, 0.5f);
            previousRect.pivot = new Vector2(0f, 0.5f);
            previousRect.anchoredPosition = Vector2.zero;
            previousRect.sizeDelta = new Vector2(42f, 34f);

            GameObject valuePanel = PanelObject("Value", row.transform, Field);
            RectTransform valueRect = valuePanel.GetComponent<RectTransform>();
            valueRect.anchorMin = new Vector2(0.56f, 0.5f);
            valueRect.anchorMax = new Vector2(1f, 0.5f);
            valueRect.pivot = new Vector2(0f, 0.5f);
            valueRect.offsetMin = new Vector2(48f, -17f);
            valueRect.offsetMax = new Vector2(-54f, 17f);
            value = TextObject("Text", valuePanel.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, "-");
            Stretch(value.rectTransform);

            next = ButtonObject("Next", row.transform, font, ">", Field);
            RectTransform nextRect = next.GetComponent<RectTransform>();
            nextRect.anchorMin = nextRect.anchorMax = new Vector2(1f, 0.5f);
            nextRect.pivot = new Vector2(1f, 0.5f);
            nextRect.anchoredPosition = new Vector2(-6f, 0f);
            nextRect.sizeDelta = new Vector2(42f, 34f);
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

        private static Text TextObject(string name, Transform parent, Font font, int size, Color color, TextAnchor anchor, string value)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.text = value;
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
