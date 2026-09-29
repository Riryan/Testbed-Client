#if UNITY_EDITOR
using System;
using Game.Client.Presentation.Characters;
using Game.Client.UI.CharacterSelect;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Character Select/Create authored-prefab pass for StandaloneClientUI.prefab.
    ///
    /// Runtime ownership stays in the existing CharacterSelectShell and CharacterCreatorShell.
    /// This tool only authors/binds editable prefab hierarchy. It creates no network path and
    /// does not construct the production UI at runtime.
    /// </summary>
    public static class StandaloneClientUICharacterAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";
        private const int AuthoredSlotCount = 16;

        private static readonly Color Backdrop = new Color32(12, 15, 20, 255);
        private static readonly Color Panel = new Color32(24, 29, 37, 252);
        private static readonly Color PanelRaised = new Color32(33, 40, 51, 255);
        private static readonly Color PanelDeep = new Color32(15, 19, 25, 255);
        private static readonly Color Accent = new Color32(58, 103, 164, 255);
        private static readonly Color AccentBright = new Color32(55, 145, 235, 255);
        private static readonly Color Border = new Color32(66, 76, 92, 255);
        private static readonly Color TextPrimary = new Color32(239, 241, 244, 255);
        private static readonly Color TextSecondary = new Color32(164, 174, 189, 255);
        private static readonly Color Danger = new Color32(112, 52, 58, 255);

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Character Select Create V2")]
        public static void UpgradeExistingPrefab()
        {
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefabAsset == null)
            {
                EditorUtility.DisplayDialog(
                    "Standalone UI",
                    "StandaloneClientUI.prefab was not found. Apply Login V2 first.",
                    "OK");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                            Resources.GetBuiltinResource<Font>("Arial.ttf");

                Transform frontend = root.transform.Find("Frontend");
                if (frontend == null)
                    throw new InvalidOperationException("StandaloneClientUI.prefab is missing the authored Frontend root.");

                CharacterSelectShell shell = frontend.GetComponent<CharacterSelectShell>();
                if (shell == null)
                    throw new InvalidOperationException("Frontend is missing CharacterSelectShell.");

                Transform backdrop = frontend.Find("Backdrop");
                if (backdrop == null)
                    throw new InvalidOperationException("Frontend is missing the Login V2 Backdrop.");

                RemoveOldCharacterSelect(backdrop);

                GameObject characterPanel = BuildCharacterSelect(
                    backdrop,
                    font,
                    out Text statusText,
                    out Button createButton,
                    out Button enterButton,
                    out Button deleteButton,
                    out Button signOutButton,
                    out CharacterSelectSlotView[] slots,
                    out CharacterSelectPreviewController preview);

                shell.ConfigureStandaloneCharacterLobbyForEditor(
                    characterPanel,
                    statusText,
                    createButton,
                    enterButton,
                    deleteButton,
                    signOutButton,
                    slots,
                    preview);

                // Reuse the existing Character Creator authoring system. It builds an editable
                // prefab hierarchy now, in the Editor, and binds the existing CharacterCreatorShell.
                CharacterCreatorAuthoredUiRepair.Repair(shell);

                characterPanel.SetActive(false);
                EditorUtility.SetDirty(shell);
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
                "[StandaloneUI] Character Select/Create V2 authored. " +
                "CharacterSelectShell/CharacterCreatorShell and existing roster/appearance/equipment caches are reused; no new network messages were added.");
        }

        private static void RemoveOldCharacterSelect(Transform backdrop)
        {
            string[] names =
            {
                "CharacterPanel_LoginMilestone",
                "CharacterSelect",
                "Character Select",
                "CharacterPanel"
            };
            for (int i = 0; i < names.Length; ++i)
            {
                Transform existing = backdrop.Find(names[i]);
                if (existing != null)
                    UnityEngine.Object.DestroyImmediate(existing.gameObject);
            }
        }

        private static GameObject BuildCharacterSelect(
            Transform parent,
            Font font,
            out Text statusText,
            out Button createButton,
            out Button enterButton,
            out Button deleteButton,
            out Button signOutButton,
            out CharacterSelectSlotView[] slots,
            out CharacterSelectPreviewController preview)
        {
            GameObject root = PanelObject("CharacterSelect", parent, Backdrop);
            Stretch(root.GetComponent<RectTransform>());

            GameObject frame = PanelObject("CharacterSelectFrame", root.transform, Panel);
            RectTransform frameRect = frame.GetComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0.07f, 0.075f);
            frameRect.anchorMax = new Vector2(0.93f, 0.925f);
            frameRect.offsetMin = Vector2.zero;
            frameRect.offsetMax = Vector2.zero;
            Outline outline = frame.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);

            Text title = TextObject("Title", frame.transform, font, 28, TextPrimary, TextAnchor.MiddleLeft, "CHARACTER SELECT");
            SetRect(title.rectTransform, new Vector2(0.035f, 0.89f), new Vector2(0.43f, 0.965f));
            Text subtitle = TextObject("Subtitle", frame.transform, font, 13, TextSecondary, TextAnchor.MiddleLeft,
                "Choose a character or create a new one.");
            SetRect(subtitle.rectTransform, new Vector2(0.035f, 0.845f), new Vector2(0.48f, 0.895f));

            statusText = TextObject("Status", frame.transform, font, 12, TextSecondary, TextAnchor.MiddleLeft, "Characters ready from local cache.");
            SetRect(statusText.rectTransform, new Vector2(0.035f, 0.795f), new Vector2(0.48f, 0.845f));

            createButton = ButtonObject("CreateCharacterButton", frame.transform, font, "CREATE CHARACTER", Accent);
            SetRect(createButton.GetComponent<RectTransform>(), new Vector2(0.035f, 0.715f), new Vector2(0.46f, 0.785f));

            // Authored scroll/list. All 16 possible slots exist in the prefab; runtime code
            // only binds/hides them according to the existing cached CharacterSession summaries.
            GameObject listFrame = PanelObject("CharacterList", frame.transform, PanelDeep);
            SetRect(listFrame.GetComponent<RectTransform>(), new Vector2(0.035f, 0.19f), new Vector2(0.46f, 0.695f));
            Outline listOutline = listFrame.AddComponent<Outline>();
            listOutline.effectColor = Border;
            listOutline.effectDistance = new Vector2(1f, -1f);

            ScrollRect scroll = listFrame.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            GameObject viewport = Node("Viewport", listFrame.transform, typeof(Image), typeof(Mask));
            Stretch(viewport.GetComponent<RectTransform>());
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = viewport.GetComponent<RectTransform>();

            GameObject content = Node("Content", viewport.transform, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = Vector2.zero;
            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = contentRect;

            slots = new CharacterSelectSlotView[AuthoredSlotCount];
            for (int i = 0; i < AuthoredSlotCount; ++i)
                slots[i] = BuildSlot(content.transform, font, i + 1);

            GameObject previewFrame = PanelObject("CharacterPreview", frame.transform, PanelDeep);
            SetRect(previewFrame.GetComponent<RectTransform>(), new Vector2(0.50f, 0.19f), new Vector2(0.965f, 0.845f));
            Outline previewOutline = previewFrame.AddComponent<Outline>();
            previewOutline.effectColor = Border;
            previewOutline.effectDistance = new Vector2(1f, -1f);

            RawImage previewImage = Node("PreviewImage", previewFrame.transform, typeof(RawImage)).GetComponent<RawImage>();
            SetRect(previewImage.rectTransform, new Vector2(0.025f, 0.07f), new Vector2(0.975f, 0.975f));
            previewImage.color = Color.white;

            Text previewName = TextObject("PreviewName", previewFrame.transform, font, 18, TextPrimary, TextAnchor.MiddleLeft, "");
            SetRect(previewName.rectTransform, new Vector2(0.04f, 0.90f), new Vector2(0.76f, 0.975f));
            Text previewHint = TextObject("PreviewHint", previewFrame.transform, font, 11, TextSecondary, TextAnchor.MiddleRight,
                "Drag to rotate  •  Wheel to zoom");
            SetRect(previewHint.rectTransform, new Vector2(0.46f, 0.015f), new Vector2(0.96f, 0.065f));

            preview = previewFrame.AddComponent<CharacterSelectPreviewController>();
            SerializedObject previewSo = new SerializedObject(preview);
            previewSo.FindProperty("previewImage").objectReferenceValue = previewImage;
            previewSo.FindProperty("previewNameText").objectReferenceValue = previewName;
            previewSo.ApplyModifiedPropertiesWithoutUndo();

            CharacterSelectPreviewDragSurface drag = previewImage.gameObject.AddComponent<CharacterSelectPreviewDragSurface>();
            drag.controller = preview;

            deleteButton = ButtonObject("Delete Character Button", frame.transform, font, "DELETE CHARACTER", Danger);
            SetRect(deleteButton.GetComponent<RectTransform>(), new Vector2(0.035f, 0.075f), new Vector2(0.22f, 0.145f));

            enterButton = ButtonObject("EnterWorldButton", frame.transform, font, "ENTER WORLD", Accent);
            SetRect(enterButton.GetComponent<RectTransform>(), new Vector2(0.34f, 0.075f), new Vector2(0.60f, 0.145f));

            signOutButton = ButtonObject("SignOutButton", frame.transform, font, "SIGN OUT", PanelRaised);
            SetRect(signOutButton.GetComponent<RectTransform>(), new Vector2(0.78f, 0.075f), new Vector2(0.965f, 0.145f));

            return root;
        }

        private static CharacterSelectSlotView BuildSlot(Transform parent, Font font, int index)
        {
            GameObject root = PanelObject($"CharacterSlot_{index}", parent, PanelRaised);
            LayoutElement element = root.AddComponent<LayoutElement>();
            element.preferredHeight = 58f;
            element.minHeight = 58f;

            Button button = root.AddComponent<Button>();
            button.targetGraphic = root.GetComponent<Image>();
            ColorBlock colors = button.colors;
            colors.normalColor = PanelRaised;
            colors.highlightedColor = new Color32(44, 55, 70, 255);
            colors.pressedColor = new Color32(38, 47, 61, 255);
            colors.selectedColor = new Color32(44, 55, 70, 255);
            colors.disabledColor = new Color32(30, 35, 43, 160);
            button.colors = colors;

            GameObject selected = PanelObject("Selected", root.transform, AccentBright);
            RectTransform selectedRect = selected.GetComponent<RectTransform>();
            selectedRect.anchorMin = new Vector2(0f, 0f);
            selectedRect.anchorMax = new Vector2(0f, 1f);
            selectedRect.pivot = new Vector2(0f, 0.5f);
            selectedRect.anchoredPosition = Vector2.zero;
            selectedRect.sizeDelta = new Vector2(4f, 0f);
            selected.GetComponent<Image>().raycastTarget = false;

            Text name = TextObject("Name", root.transform, font, 14, TextPrimary, TextAnchor.MiddleLeft, $"Character {index}");
            SetRect(name.rectTransform, new Vector2(0.035f, 0.18f), new Vector2(0.66f, 0.86f));
            Text map = TextObject("Map", root.transform, font, 11, TextSecondary, TextAnchor.MiddleRight, "Unknown map");
            SetRect(map.rectTransform, new Vector2(0.64f, 0.18f), new Vector2(0.965f, 0.86f));

            CharacterSelectSlotView view = root.AddComponent<CharacterSelectSlotView>();
            SerializedObject so = new SerializedObject(view);
            so.FindProperty("button").objectReferenceValue = button;
            so.FindProperty("nameText").objectReferenceValue = name;
            so.FindProperty("mapText").objectReferenceValue = map;
            so.FindProperty("selectedMarker").objectReferenceValue = selected;
            so.ApplyModifiedPropertiesWithoutUndo();
            selected.SetActive(false);
            return view;
        }

        private static GameObject Node(string name, Transform parent, params Type[] components)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            for (int i = 0; i < components.Length; ++i)
                if (components[i] != typeof(RectTransform))
                    go.AddComponent(components[i]);
            return go;
        }

        private static GameObject PanelObject(string name, Transform parent, Color color)
        {
            GameObject go = Node(name, parent, typeof(Image));
            Image image = go.GetComponent<Image>();
            image.color = color;
            return go;
        }

        private static Text TextObject(
            string name,
            Transform parent,
            Font font,
            int size,
            Color color,
            TextAnchor alignment,
            string value)
        {
            GameObject go = Node(name, parent, typeof(Text));
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.text = value ?? string.Empty;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static Button ButtonObject(string name, Transform parent, Font font, string label, Color color)
        {
            GameObject go = PanelObject(name, parent, color);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = AccentBright;
            colors.pressedColor = new Color32(44, 78, 121, 255);
            colors.selectedColor = AccentBright;
            colors.disabledColor = new Color32(45, 49, 59, 120);
            button.colors = colors;
            Text text = TextObject("Text", go.transform, font, 13, TextPrimary, TextAnchor.MiddleCenter, label);
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

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
#endif
