#if UNITY_EDITOR
using System;
using Game.Client.UI.Standalone;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>Audio Settings V2.09 authored-prefab upgrade. Patches only AUDIOPage.</summary>
    public static class StandaloneClientUIAudioSettingsAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color PanelRaised = new Color32(27, 31, 40, 248);
        private static readonly Color Field = new Color32(10, 13, 18, 240);
        private static readonly Color TextPrimary = new Color32(239, 242, 246, 255);
        private static readonly Color TextSecondary = new Color32(167, 178, 194, 255);
        private static readonly Color AccentText = new Color32(230, 193, 107, 255);

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Audio Settings V2.09")]
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
                Transform page = root.transform.Find("Gameplay/Windows/SettingsWindow/Body/Content/AUDIOPage");
                if (page == null)
                    throw new InvalidOperationException("Settings AUDIOPage was not found. Install/run Settings V2.07 first.");

                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                for (int i = page.childCount - 1; i >= 0; --i)
                    UnityEngine.Object.DestroyImmediate(page.GetChild(i).gameObject);

                StandaloneAudioSettingsPage old = page.GetComponent<StandaloneAudioSettingsPage>();
                if (old != null) UnityEngine.Object.DestroyImmediate(old);

                BuildAudioPage(page, font);
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
            Debug.Log("[StandaloneUI] Audio Settings V2.09 authored. Master/Music/SFX/UI/Voice are local-machine-only; no GameServer traffic is introduced.");
        }

        private static void BuildAudioPage(Transform page, Font font)
        {
            Text heading = TextObject("Heading", page, font, 18, TextPrimary, TextAnchor.MiddleLeft, "AUDIO");
            SetRect(heading.rectTransform, new Vector2(0f,1f), new Vector2(1f,1f), new Vector2(0f,1f), new Vector2(16f,-8f), new Vector2(-150f,34f));

            Text localOnly = TextObject("LocalOnly", page, font, 12, TextSecondary, TextAnchor.MiddleLeft,
                "Saved on this machine only. Audio preferences never generate GameServer traffic.");
            SetRect(localOnly.rectTransform, new Vector2(0f,1f), new Vector2(1f,1f), new Vector2(0f,1f), new Vector2(16f,-40f), new Vector2(-24f,26f));

            GameObject rows = Node("AudioRows", page);
            RectTransform rowsRect = rows.GetComponent<RectTransform>();
            rowsRect.anchorMin = new Vector2(0f,0f); rowsRect.anchorMax = new Vector2(1f,1f);
            rowsRect.offsetMin = new Vector2(16f,116f); rowsRect.offsetMax = new Vector2(-16f,-88f);
            VerticalLayoutGroup layout = rows.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f; layout.childControlHeight = false; layout.childControlWidth = true;
            layout.childForceExpandHeight = false; layout.childForceExpandWidth = true;

            BuildSliderRow(rows.transform, font, "Master", "Overall client audio level.", out Slider master, out Text masterValue);
            BuildSliderRow(rows.transform, font, "Music", "Music presentation level.", out Slider music, out Text musicValue);
            BuildSliderRow(rows.transform, font, "SFX", "Combat and world sound-effect level.", out Slider sfx, out Text sfxValue);
            BuildSliderRow(rows.transform, font, "UI", "Interface sound level.", out Slider ui, out Text uiValue);
            BuildSliderRow(rows.transform, font, "Voice", "Voice-chat playback level.", out Slider voice, out Text voiceValue);

            Text status = TextObject("Status", page, font, 12, AccentText, TextAnchor.MiddleLeft, "Audio settings are local to this client.");
            SetRect(status.rectTransform, new Vector2(0f,0f), new Vector2(1f,0f), new Vector2(0f,0f), new Vector2(16f,50f), new Vector2(-290f,54f));
            status.horizontalOverflow = HorizontalWrapMode.Wrap; status.verticalOverflow = VerticalWrapMode.Truncate;

            Button reset = ButtonObject("ResetDefaults", page, font, "RESET DEFAULTS", PanelRaised);
            RectTransform resetRect = reset.GetComponent<RectTransform>();
            resetRect.anchorMin = resetRect.anchorMax = new Vector2(1f,0f); resetRect.pivot = new Vector2(1f,0f);
            resetRect.anchoredPosition = new Vector2(-142f,14f); resetRect.sizeDelta = new Vector2(132f,34f);

            Button apply = ButtonObject("Apply", page, font, "APPLY", PanelRaised);
            RectTransform applyRect = apply.GetComponent<RectTransform>();
            applyRect.anchorMin = applyRect.anchorMax = new Vector2(1f,0f); applyRect.pivot = new Vector2(1f,0f);
            applyRect.anchoredPosition = new Vector2(-16f,14f); applyRect.sizeDelta = new Vector2(112f,34f);

            StandaloneAudioSettingsPage controller = page.gameObject.AddComponent<StandaloneAudioSettingsPage>();
            controller.ConfigureForEditor(master, masterValue, music, musicValue, sfx, sfxValue, ui, uiValue, voice, voiceValue, apply, reset, status);
        }

        private static void BuildSliderRow(Transform parent, Font font, string label, string description, out Slider slider, out Text value)
        {
            GameObject row = PanelObject(label + "Row", parent, PanelRaised);
            LayoutElement element = row.AddComponent<LayoutElement>(); element.minHeight = element.preferredHeight = 68f;

            Text title = TextObject("Label", row.transform, font, 13, TextPrimary, TextAnchor.MiddleLeft, label.ToUpperInvariant());
            SetRect(title.rectTransform, new Vector2(0f,0f), new Vector2(0.40f,1f), new Vector2(0f,0.5f), new Vector2(12f,8f), new Vector2(-8f,-4f));
            Text detail = TextObject("Description", row.transform, font, 11, TextSecondary, TextAnchor.MiddleLeft, description);
            SetRect(detail.rectTransform, new Vector2(0f,0f), new Vector2(0.47f,1f), new Vector2(0f,0.5f), new Vector2(12f,-15f), new Vector2(-8f,-28f));

            GameObject sliderRoot = PanelObject("Slider", row.transform, Field);
            RectTransform sr = sliderRoot.GetComponent<RectTransform>();
            sr.anchorMin = new Vector2(0.50f,0.5f); sr.anchorMax = new Vector2(0.90f,0.5f); sr.pivot = new Vector2(0f,0.5f);
            sr.offsetMin = new Vector2(0f,-17f); sr.offsetMax = new Vector2(0f,17f);

            GameObject background = PanelObject("Background", sliderRoot.transform, new Color32(47,54,66,255));
            RectTransform br = background.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0f,0.5f); br.anchorMax = new Vector2(1f,0.5f); br.offsetMin = new Vector2(8f,-4f); br.offsetMax = new Vector2(-8f,4f);

            GameObject fillArea = Node("Fill Area", sliderRoot.transform);
            RectTransform far = fillArea.GetComponent<RectTransform>(); far.anchorMin = Vector2.zero; far.anchorMax = Vector2.one; far.offsetMin = new Vector2(8f,0f); far.offsetMax = new Vector2(-8f,0f);
            GameObject fill = PanelObject("Fill", fillArea.transform, new Color32(103,132,171,255)); Stretch(fill.GetComponent<RectTransform>());

            GameObject handleArea = Node("Handle Slide Area", sliderRoot.transform);
            RectTransform har = handleArea.GetComponent<RectTransform>(); har.anchorMin = Vector2.zero; har.anchorMax = Vector2.one; har.offsetMin = new Vector2(8f,0f); har.offsetMax = new Vector2(-8f,0f);
            GameObject handle = PanelObject("Handle", handleArea.transform, TextPrimary);
            RectTransform hr = handle.GetComponent<RectTransform>(); hr.sizeDelta = new Vector2(14f,24f); hr.anchorMin = hr.anchorMax = new Vector2(0.5f,0.5f); hr.pivot = new Vector2(0.5f,0.5f);

            slider = sliderRoot.AddComponent<Slider>();
            slider.minValue = 0f; slider.maxValue = 1f; slider.value = 1f; slider.wholeNumbers = false;
            slider.fillRect = fill.GetComponent<RectTransform>(); slider.handleRect = handle.GetComponent<RectTransform>(); slider.targetGraphic = handle.GetComponent<Image>();

            value = TextObject("Value", row.transform, font, 12, TextPrimary, TextAnchor.MiddleCenter, "100%");
            RectTransform vr = value.rectTransform; vr.anchorMin = new Vector2(0.91f,0.5f); vr.anchorMax = new Vector2(1f,0.5f); vr.pivot = new Vector2(0f,0.5f); vr.offsetMin = new Vector2(4f,-17f); vr.offsetMax = new Vector2(-6f,17f);
        }

        private static GameObject Node(string name, Transform parent) { var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); return go; }
        private static GameObject PanelObject(string name, Transform parent, Color color) { GameObject go = Node(name,parent); Image image = go.AddComponent<Image>(); image.color = color; return go; }
        private static Text TextObject(string name, Transform parent, Font font, int size, Color color, TextAnchor alignment, string text) { GameObject go=Node(name,parent); Text t=go.AddComponent<Text>(); t.font=font; t.fontSize=size; t.color=color; t.alignment=alignment; t.text=text; return t; }
        private static Button ButtonObject(string name, Transform parent, Font font, string label, Color color) { GameObject go=PanelObject(name,parent,color); Button b=go.AddComponent<Button>(); b.targetGraphic=go.GetComponent<Image>(); Text text=TextObject("Text",go.transform,font,12,TextPrimary,TextAnchor.MiddleCenter,label); Stretch(text.rectTransform); return b; }
        private static void Stretch(RectTransform rt) { rt.anchorMin=Vector2.zero; rt.anchorMax=Vector2.one; rt.offsetMin=Vector2.zero; rt.offsetMax=Vector2.zero; }
        private static void SetRect(RectTransform rt, Vector2 amin, Vector2 amax, Vector2 pivot, Vector2 pos, Vector2 size) { rt.anchorMin=amin; rt.anchorMax=amax; rt.pivot=pivot; rt.anchoredPosition=pos; rt.sizeDelta=size; }
    }
}
#endif
