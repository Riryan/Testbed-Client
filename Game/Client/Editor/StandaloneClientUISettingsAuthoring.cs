#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.UI.Standalone;
using Player.Client;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Settings V2.07 authored-prefab upgrade.
    /// Replaces the temporary Player Config window with a normal MMO Settings shell while
    /// preserving the existing local-only PlayerControlConfig implementation.
    /// No runtime UI hierarchy is built by this tool; it authors the prefab in the Editor.
    /// </summary>
    public static class StandaloneClientUISettingsAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color Window = new Color32(20, 22, 28, 246);
        private static readonly Color Panel = new Color32(15, 18, 24, 235);
        private static readonly Color PanelRaised = new Color32(27, 31, 40, 248);
        private static readonly Color Field = new Color32(10, 13, 18, 240);
        private static readonly Color Border = new Color32(67, 78, 96, 255);
        private static readonly Color TextPrimary = new Color32(239, 242, 246, 255);
        private static readonly Color TextSecondary = new Color32(167, 178, 194, 255);
        private static readonly Color AccentText = new Color32(230, 193, 107, 255);

        // Interaction Action 2/3/4 were retired by V2.06. The clickable contextual panel is canonical.
        private static readonly PlayerControlAction[] ControlRows =
        {
            PlayerControlAction.MoveForward,
            PlayerControlAction.MoveBackward,
            PlayerControlAction.MoveLeft,
            PlayerControlAction.MoveRight,
            PlayerControlAction.Jump,
            PlayerControlAction.Sprint,
            PlayerControlAction.Crouch,
            PlayerControlAction.Interact,
            PlayerControlAction.ToggleCombatStance,
            PlayerControlAction.ReloadOrRespawn,
            PlayerControlAction.PickupNearest,
            PlayerControlAction.DropSelected,
            PlayerControlAction.OpenInventory,
            PlayerControlAction.OpenSkills,
            PlayerControlAction.OpenMap,
            PlayerControlAction.OpenSocial,
            PlayerControlAction.OpenEmotes,
            PlayerControlAction.OpenMenu,
            PlayerControlAction.FocusChat,
            PlayerControlAction.ReleaseCursor,
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

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Settings V2.07")]
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
                Transform windows = root.transform.Find("Gameplay/Windows");
                if (windows == null)
                    throw new InvalidOperationException("StandaloneClientUI.prefab is missing Gameplay/Windows.");

                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                            Resources.GetBuiltinResource<Font>("Arial.ttf");

                Transform oldPlayerConfig = windows.Find("PlayerConfigWindow");
                if (oldPlayerConfig != null)
                    UnityEngine.Object.DestroyImmediate(oldPlayerConfig.gameObject);

                Transform oldSettings = windows.Find("SettingsWindow");
                if (oldSettings != null)
                    UnityEngine.Object.DestroyImmediate(oldSettings.gameObject);

                StandaloneControlsWindow controls = BuildSettingsWindow(windows, font);

                StandaloneClientUIRoot standaloneRoot = root.GetComponent<StandaloneClientUIRoot>();
                if (standaloneRoot == null)
                    throw new InvalidOperationException("StandaloneClientUIRoot is missing from the prefab root.");

                SerializedObject serializedRoot = new SerializedObject(standaloneRoot);
                SerializedProperty controlsProperty = serializedRoot.FindProperty("controlsWindow");
                if (controlsProperty == null)
                    throw new InvalidOperationException("StandaloneClientUIRoot.controlsWindow serialized field was not found.");
                controlsProperty.objectReferenceValue = controls;
                serializedRoot.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(standaloneRoot);

                controls.gameObject.SetActive(false);
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
            Debug.Log("[StandaloneUI] Settings V2.07 authored. Controls are local-only/rebindable; Video, Audio, and Gameplay/UI tabs are staged for the next settings passes.");
        }

        private static StandaloneControlsWindow BuildSettingsWindow(Transform windows, Font font)
        {
            GameObject root = PanelObject("SettingsWindow", windows, Window);
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.anchoredPosition = Vector2.zero;
            rootRect.sizeDelta = new Vector2(900f, 720f);
            AddOutline(root);

            GameObject header = PanelObject("Header", root.transform, PanelRaised);
            SetRect(header.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(0f, 54f));
            Text title = TextObject("Title", header.transform, font, 20, TextPrimary, TextAnchor.MiddleLeft, "SETTINGS");
            SetRect(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(-80f, 0f));
            Button close = ButtonObject("CloseButton", header.transform, font, "X", Panel);
            RectTransform closeRect = close.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-9f, 0f);
            closeRect.sizeDelta = new Vector2(44f, 36f);

            GameObject body = Node("Body", root.transform);
            RectTransform bodyRect = body.GetComponent<RectTransform>();
            bodyRect.anchorMin = Vector2.zero;
            bodyRect.anchorMax = Vector2.one;
            bodyRect.offsetMin = new Vector2(10f, 10f);
            bodyRect.offsetMax = new Vector2(-10f, -64f);

            GameObject nav = PanelObject("Tabs", body.transform, Panel);
            SetRect(nav.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(170f, 0f));
            VerticalLayoutGroup navLayout = nav.AddComponent<VerticalLayoutGroup>();
            navLayout.padding = new RectOffset(10, 10, 12, 12);
            navLayout.spacing = 8f;
            navLayout.childControlWidth = true;
            navLayout.childControlHeight = false;
            navLayout.childForceExpandWidth = true;
            navLayout.childForceExpandHeight = false;

            Button controlsTab = TabButton(nav.transform, font, "CONTROLS");
            Button videoTab = TabButton(nav.transform, font, "VIDEO");
            Button audioTab = TabButton(nav.transform, font, "AUDIO");
            Button gameplayUiTab = TabButton(nav.transform, font, "GAMEPLAY / UI");

            GameObject content = PanelObject("Content", body.transform, new Color32(12, 14, 19, 232));
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = new Vector2(180f, 0f);
            contentRect.offsetMax = Vector2.zero;

            GameObject controlsPage = BuildControlsPage(content.transform, font, out Button reset, out Text status, out StandaloneControlBindingRow[] rows);
            GameObject videoPage = BuildPlaceholderPage(content.transform, font, "VIDEO", "Video settings are staged for the next pass.\nResolution, display mode, VSync, frame cap and quality will remain local-only.");
            GameObject audioPage = BuildPlaceholderPage(content.transform, font, "AUDIO", "Audio settings are staged for the next pass.\nMaster, music, SFX, UI and voice levels will remain local-only.");
            GameObject gameplayUiPage = BuildPlaceholderPage(content.transform, font, "GAMEPLAY / UI", "Gameplay/UI preferences are staged for the next pass.\nMouse sensitivity, UI scale and local presentation preferences belong here.");

            StandaloneControlsWindow controls = root.AddComponent<StandaloneControlsWindow>();
            for (int i = 0; i < rows.Length; ++i)
            {
                StandaloneControlBindingRow row = rows[i];
                if (row == null)
                    continue;
                // Re-run editor configuration now that the owner exists.
                SerializedObject serializedRow = new SerializedObject(row);
                SerializedProperty owner = serializedRow.FindProperty("owner");
                if (owner != null)
                {
                    owner.objectReferenceValue = controls;
                    serializedRow.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorUtility.SetDirty(row);
            }
            controls.ConfigureForEditor(root, close, reset, status, rows);

            StandaloneSettingsTabs tabs = root.AddComponent<StandaloneSettingsTabs>();
            tabs.ConfigureForEditor(controlsTab, videoTab, audioTab, gameplayUiTab, controlsPage, videoPage, audioPage, gameplayUiPage);

            controlsPage.SetActive(true);
            videoPage.SetActive(false);
            audioPage.SetActive(false);
            gameplayUiPage.SetActive(false);
            return controls;
        }

        private static GameObject BuildControlsPage(
            Transform parent,
            Font font,
            out Button reset,
            out Text status,
            out StandaloneControlBindingRow[] rows)
        {
            GameObject page = Node("ControlsPage", parent);
            Stretch(page.GetComponent<RectTransform>());

            Text heading = TextObject("Heading", page.transform, font, 18, TextPrimary, TextAnchor.MiddleLeft, "CONTROLS");
            SetRect(heading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(16f, -8f), new Vector2(-150f, 34f));
            Text localOnly = TextObject("LocalOnly", page.transform, font, 12, TextSecondary, TextAnchor.MiddleLeft, "Saved on this machine only. Control bindings are never sent to the GameServer.");
            SetRect(localOnly.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(16f, -40f), new Vector2(-160f, 26f));

            reset = ButtonObject("ResetDefaults", page.transform, font, "RESET DEFAULTS", PanelRaised);
            RectTransform resetRect = reset.GetComponent<RectTransform>();
            resetRect.anchorMin = resetRect.anchorMax = new Vector2(1f, 1f);
            resetRect.pivot = new Vector2(1f, 1f);
            resetRect.anchoredPosition = new Vector2(-14f, -12f);
            resetRect.sizeDelta = new Vector2(138f, 30f);

            status = TextObject("Status", page.transform, font, 12, AccentText, TextAnchor.MiddleLeft, "Click a binding, then press a key. Backspace/Delete unbinds it.");
            SetRect(status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(16f, 8f), new Vector2(-32f, 30f));

            GameObject scrollRoot = BuildScrollArea("BindingsScroll", page.transform, out RectTransform content);
            RectTransform scrollRect = scrollRoot.GetComponent<RectTransform>();
            scrollRect.anchorMin = Vector2.zero;
            scrollRect.anchorMax = Vector2.one;
            scrollRect.offsetMin = new Vector2(14f, 44f);
            scrollRect.offsetMax = new Vector2(-14f, -76f);

            VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 5f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var builtRows = new List<StandaloneControlBindingRow>(ControlRows.Length);
            for (int i = 0; i < ControlRows.Length; ++i)
            {
                PlayerControlAction action = ControlRows[i];
                GameObject rowGo = PanelObject($"Binding_{action}", content, PanelRaised);
                LayoutElement element = rowGo.AddComponent<LayoutElement>();
                element.minHeight = element.preferredHeight = 38f;

                Text actionText = TextObject("Action", rowGo.transform, font, 12, TextPrimary, TextAnchor.MiddleLeft, PlayerControlConfig.DisplayName(action));
                SetRect(actionText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(-170f, 0f));

                Button bind = ButtonObject("Binding", rowGo.transform, font, PlayerControlConfig.BindingLabel(action), Field);
                RectTransform bindRect = bind.GetComponent<RectTransform>();
                bindRect.anchorMin = bindRect.anchorMax = new Vector2(1f, 0.5f);
                bindRect.pivot = new Vector2(1f, 0.5f);
                bindRect.anchoredPosition = new Vector2(-6f, 0f);
                bindRect.sizeDelta = new Vector2(150f, 28f);

                Text bindingText = bind.GetComponentInChildren<Text>(true);
                StandaloneControlBindingRow row = rowGo.AddComponent<StandaloneControlBindingRow>();
                row.ConfigureForEditor(action, actionText, bindingText, bind, null);
                builtRows.Add(row);
            }

            rows = builtRows.ToArray();
            return page;
        }

        private static GameObject BuildPlaceholderPage(Transform parent, Font font, string title, string detail)
        {
            GameObject page = Node(title.Replace(" / ", "").Replace(" ", string.Empty) + "Page", parent);
            Stretch(page.GetComponent<RectTransform>());

            Text heading = TextObject("Heading", page.transform, font, 20, TextPrimary, TextAnchor.MiddleLeft, title);
            SetRect(heading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(-24f, 42f));
            Text description = TextObject("Description", page.transform, font, 14, TextSecondary, TextAnchor.UpperLeft, detail);
            description.horizontalOverflow = HorizontalWrapMode.Wrap;
            description.verticalOverflow = VerticalWrapMode.Overflow;
            SetRect(description.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(24f, -82f), new Vector2(-24f, -24f));
            return page;
        }

        private static Button TabButton(Transform parent, Font font, string label)
        {
            Button button = ButtonObject(label.Replace(" / ", "").Replace(" ", string.Empty) + "Tab", parent, font, label, PanelRaised);
            LayoutElement layout = button.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = 42f;
            return button;
        }

        private static GameObject BuildScrollArea(string name, Transform parent, out RectTransform content)
        {
            GameObject root = PanelObject(name, parent, Field);
            ScrollRect scroll = root.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 26f;

            GameObject viewport = PanelObject("Viewport", root.transform, new Color(0f, 0f, 0f, 0f));
            Stretch(viewport.GetComponent<RectTransform>());
            viewport.AddComponent<RectMask2D>();

            GameObject contentGo = Node("Content", viewport.transform);
            content = contentGo.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = content;
            return root;
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

        private static void AddOutline(GameObject go)
        {
            Outline outline = go.GetComponent<Outline>();
            if (outline == null)
                outline = go.AddComponent<Outline>();
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

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size)
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
