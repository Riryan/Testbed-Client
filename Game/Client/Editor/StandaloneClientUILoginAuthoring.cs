using System;
using Game.Client.Presentation.Characters;
using Game.Client.UI.CharacterSelect;
using Game.Client.UI.Standalone;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Editor-only upgrade for the authored standalone UI prefab.
    ///
    /// This does not add a second authentication implementation. It authors a new Login/Create
    /// Account presentation and wires the existing CharacterSelectShell directly to it. The
    /// existing CharacterSelectShell continues to own auto-connect, HTTPS authentication,
    /// admission-token redemption, remembered account, server status, and frontend lifecycle.
    ///
    /// No runtime UI hierarchy is constructed by this tool or by the standalone UI scripts.
    /// </summary>
    public static class StandaloneClientUILoginAuthoring
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color Backdrop = new Color32(8, 10, 15, 245);
        private static readonly Color Panel = new Color32(23, 27, 35, 250);
        private static readonly Color PanelRaised = new Color32(33, 39, 50, 255);
        private static readonly Color Field = new Color32(16, 19, 25, 255);
        private static readonly Color Accent = new Color32(66, 112, 170, 255);
        private static readonly Color AccentPressed = new Color32(52, 85, 126, 255);
        private static readonly Color Border = new Color32(73, 82, 98, 255);
        private static readonly Color TextPrimary = new Color32(235, 238, 242, 255);
        private static readonly Color TextSecondary = new Color32(168, 178, 193, 255);
        private static readonly Color Online = new Color32(115, 202, 139, 255);
        private static readonly Color Checking = new Color32(219, 183, 94, 255);
        private static readonly Color Offline = new Color32(211, 105, 105, 255);

        [MenuItem("MMO Tools/UI/Standalone UI/Upgrade Existing Prefab - Login V2")]
        public static void UpgradeExistingPrefab()
        {
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefabAsset == null)
            {
                EditorUtility.DisplayDialog(
                    "Standalone UI",
                    "StandaloneClientUI.prefab was not found. Run 'Create Foundation V1' first, then run this Login V2 upgrade.",
                    "OK");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                            Resources.GetBuiltinResource<Font>("Arial.ttf");

                StandaloneClientUIRoot rootController = root.GetComponent<StandaloneClientUIRoot>();
                if (rootController == null)
                    throw new InvalidOperationException("StandaloneClientUIRoot is missing from the prefab root.");

                EnsureAuthoredEventSystem(root.transform);
                GameObject gameplayRoot = EnsureGameplayRoot(root.transform);
                GameObject frontend = RebuildFrontend(root.transform, font, out CharacterSelectShell frontendShell);

                StandaloneCharacterInventoryWindow characterInventory =
                    root.GetComponentInChildren<StandaloneCharacterInventoryWindow>(true);
                StandaloneHudTooltipPanel tooltip =
                    root.GetComponentInChildren<StandaloneHudTooltipPanel>(true);

                rootController.ConfigureForEditor(gameplayRoot, characterInventory, tooltip);
                gameplayRoot.SetActive(false);
                frontend.SetActive(true);

                EditorUtility.SetDirty(rootController);
                EditorUtility.SetDirty(frontendShell);
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
                "[StandaloneUI] Login V2 authored onto StandaloneClientUI.prefab. " +
                "Existing CharacterSelectShell owns auto-connect/login/create-account logic; no new network messages were added.");
        }

        private static GameObject RebuildFrontend(
            Transform root,
            Font font,
            out CharacterSelectShell shell)
        {
            Transform previous = root.Find("Frontend");
            if (previous != null)
                UnityEngine.Object.DestroyImmediate(previous.gameObject);

            GameObject frontend = Node("Frontend", root, typeof(Canvas), typeof(GraphicRaycaster), typeof(CharacterSelectShell));
            Stretch(frontend.GetComponent<RectTransform>());
            Canvas canvas = frontend.GetComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 200;
            shell = frontend.GetComponent<CharacterSelectShell>();

            GameObject backdrop = PanelObject("Backdrop", frontend.transform, Backdrop);
            Stretch(backdrop.GetComponent<RectTransform>());

            // Left-side authored visual/branding region. Kept intentionally generic so it can
            // be replaced with artwork without changing authentication code.
            GameObject visual = PanelObject("LoginVisual", backdrop.transform, new Color32(12, 16, 24, 255));
            RectTransform visualRect = visual.GetComponent<RectTransform>();
            visualRect.anchorMin = Vector2.zero;
            visualRect.anchorMax = new Vector2(0.56f, 1f);
            visualRect.offsetMin = Vector2.zero;
            visualRect.offsetMax = Vector2.zero;
            visual.GetComponent<Image>().raycastTarget = false;

            Text gameTitle = TextObject("GameTitle", visual.transform, font, 42, TextPrimary, TextAnchor.LowerLeft, "AFTER DARK");
            SetRect(gameTitle.rectTransform, new Vector2(0.08f, 0.53f), new Vector2(0.88f, 0.66f), Vector2.zero, Vector2.zero);
            Text gameSubtitle = TextObject("GameSubtitle", visual.transform, font, 18, TextSecondary, TextAnchor.UpperLeft,
                "Standalone MMO Client\nLogin / Create Account");
            SetRect(gameSubtitle.rectTransform, new Vector2(0.08f, 0.42f), new Vector2(0.82f, 0.54f), Vector2.zero, Vector2.zero);

            GameObject loginPanel = PanelObject("LoginPanel", backdrop.transform, Panel);
            RectTransform panelRect = loginPanel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.61f, 0.13f);
            panelRect.anchorMax = new Vector2(0.94f, 0.87f);
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;
            Outline panelOutline = loginPanel.AddComponent<Outline>();
            panelOutline.effectColor = Border;
            panelOutline.effectDistance = new Vector2(1f, -1f);

            Text title = TextObject("Title", loginPanel.transform, font, 30, TextPrimary, TextAnchor.UpperLeft, "WELCOME BACK");
            SetRect(title.rectTransform, new Vector2(0.08f, 0.84f), new Vector2(0.92f, 0.94f), Vector2.zero, Vector2.zero);
            Text subtitle = TextObject("Subtitle", loginPanel.transform, font, 15, TextSecondary, TextAnchor.UpperLeft,
                "Sign in to continue, or create a new account.");
            SetRect(subtitle.rectTransform, new Vector2(0.08f, 0.78f), new Vector2(0.92f, 0.85f), Vector2.zero, Vector2.zero);

            Text serverAvailability = TextObject("Server Availability Text", loginPanel.transform, font, 14, Checking, TextAnchor.MiddleLeft, "●  CHECKING");
            SetRect(serverAvailability.rectTransform, new Vector2(0.08f, 0.70f), new Vector2(0.62f, 0.76f), Vector2.zero, Vector2.zero);
            Text connectionStatus = TextObject("ConnectionStatus", loginPanel.transform, font, 12, TextSecondary, TextAnchor.MiddleRight, "Connecting...");
            SetRect(connectionStatus.rectTransform, new Vector2(0.48f, 0.70f), new Vector2(0.92f, 0.76f), Vector2.zero, Vector2.zero);

            Text accountLabel = TextObject("AccountLabel", loginPanel.transform, font, 13, TextSecondary, TextAnchor.LowerLeft, "ACCOUNT");
            SetRect(accountLabel.rectTransform, new Vector2(0.08f, 0.62f), new Vector2(0.92f, 0.67f), Vector2.zero, Vector2.zero);
            InputField accountInput = InputFieldObject("AccountInput", loginPanel.transform, font, "Account name", false);
            SetRect(accountInput.GetComponent<RectTransform>(), new Vector2(0.08f, 0.54f), new Vector2(0.92f, 0.62f), Vector2.zero, Vector2.zero);

            Text passwordLabel = TextObject("PasswordLabel", loginPanel.transform, font, 13, TextSecondary, TextAnchor.LowerLeft, "PASSWORD");
            SetRect(passwordLabel.rectTransform, new Vector2(0.08f, 0.46f), new Vector2(0.92f, 0.51f), Vector2.zero, Vector2.zero);
            InputField passwordInput = InputFieldObject("PasswordInput", loginPanel.transform, font, "Password", true);
            SetRect(passwordInput.GetComponent<RectTransform>(), new Vector2(0.08f, 0.38f), new Vector2(0.92f, 0.46f), Vector2.zero, Vector2.zero);

            Toggle remember = ToggleObject("Remember Account Toggle", loginPanel.transform, font, "Remember account");
            SetRect(remember.GetComponent<RectTransform>(), new Vector2(0.08f, 0.31f), new Vector2(0.55f, 0.37f), Vector2.zero, Vector2.zero);

            Button login = ButtonObject("LoginButton", loginPanel.transform, font, "SIGN IN", Accent);
            SetRect(login.GetComponent<RectTransform>(), new Vector2(0.08f, 0.21f), new Vector2(0.48f, 0.29f), Vector2.zero, Vector2.zero);
            Button create = ButtonObject("CreateAccountButton", loginPanel.transform, font, "CREATE ACCOUNT", PanelRaised);
            SetRect(create.GetComponent<RectTransform>(), new Vector2(0.52f, 0.21f), new Vector2(0.92f, 0.29f), Vector2.zero, Vector2.zero);

            Text loginStatus = TextObject("LoginStatus", loginPanel.transform, font, 13, TextSecondary, TextAnchor.UpperLeft, "Checking game server...");
            SetRect(loginStatus.rectTransform, new Vector2(0.08f, 0.11f), new Vector2(0.92f, 0.19f), Vector2.zero, Vector2.zero);

            Button refresh = ButtonObject("Refresh Status", loginPanel.transform, font, "REFRESH STATUS", PanelRaised);
            SetRect(refresh.GetComponent<RectTransform>(), new Vector2(0.08f, 0.035f), new Vector2(0.45f, 0.095f), Vector2.zero, Vector2.zero);
            Button quit = ButtonObject("Quit Button", loginPanel.transform, font, "QUIT", PanelRaised);
            SetRect(quit.GetComponent<RectTransform>(), new Vector2(0.69f, 0.035f), new Vector2(0.92f, 0.095f), Vector2.zero, Vector2.zero);

            // Login-only milestone destination. This is an authored placeholder rather than
            // a runtime-built Character Select. The next pass will replace its contents with
            // the full authored character-select surface while keeping the same shell logic.
            GameObject characterPlaceholder = PanelObject("CharacterPanel_LoginMilestone", backdrop.transform, Panel);
            Stretch(characterPlaceholder.GetComponent<RectTransform>());
            characterPlaceholder.AddComponent<CharacterSelectPreviewController>();
            Text characterTitle = TextObject("Title", characterPlaceholder.transform, font, 30, TextPrimary, TextAnchor.MiddleCenter,
                "CHARACTER SELECT\n\nLogin accepted. Character Select is the next authored UI pass.");
            SetRect(characterTitle.rectTransform, new Vector2(0.22f, 0.38f), new Vector2(0.78f, 0.62f), Vector2.zero, Vector2.zero);
            Text characterStatus = TextObject("Status", characterPlaceholder.transform, font, 14, TextSecondary, TextAnchor.UpperCenter, "Authenticated.");
            SetRect(characterStatus.rectTransform, new Vector2(0.25f, 0.27f), new Vector2(0.75f, 0.36f), Vector2.zero, Vector2.zero);

            Button hiddenDelete = ButtonObject("Delete Character Button", characterPlaceholder.transform, font, "DELETE", PanelRaised);
            hiddenDelete.gameObject.SetActive(false);

            characterPlaceholder.SetActive(false);

            ConfigureCharacterSelectShell(
                shell,
                loginPanel,
                characterPlaceholder,
                accountInput,
                passwordInput,
                loginStatus,
                login,
                create,
                refresh,
                quit,
                remember,
                serverAvailability,
                connectionStatus,
                characterStatus,
                hiddenDelete);

            return frontend;
        }

        private static void ConfigureCharacterSelectShell(
            CharacterSelectShell shell,
            GameObject loginPanel,
            GameObject characterPanel,
            InputField accountInput,
            InputField passwordInput,
            Text loginStatus,
            Button loginButton,
            Button createAccountButton,
            Button refreshStatusButton,
            Button quitButton,
            Toggle rememberAccount,
            Text serverAvailability,
            Text connectionStatus,
            Text characterStatus,
            Button deleteCharacterButton)
        {
            shell.ConfigureStandaloneLoginForEditor(
                loginPanel,
                characterPanel,
                accountInput,
                passwordInput,
                loginStatus,
                loginButton,
                createAccountButton,
                refreshStatusButton,
                quitButton,
                rememberAccount,
                serverAvailability,
                connectionStatus,
                characterStatus,
                deleteCharacterButton,
                characterPanel.GetComponent<CharacterSelectPreviewController>(),
                Online,
                Checking,
                Offline);
            EditorUtility.SetDirty(shell);
        }

        private static GameObject EnsureGameplayRoot(Transform root)
        {
            Transform existing = root.Find("Gameplay");
            GameObject gameplay = existing != null ? existing.gameObject : Node("Gameplay", root);
            Stretch(gameplay.GetComponent<RectTransform>());

            string[] layerNames = { "HUD", "Windows", "Popups", "Overlays" };
            for (int i = 0; i < layerNames.Length; ++i)
            {
                Transform layer = root.Find(layerNames[i]);
                if (layer != null && layer.parent == root)
                    layer.SetParent(gameplay.transform, false);
            }

            return gameplay;
        }

        private static void EnsureAuthoredEventSystem(Transform root)
        {
            Transform existing = root.Find("EventSystem");
            if (existing != null)
                return;

            GameObject eventSystem = new GameObject(
                "EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            eventSystem.transform.SetParent(root, false);
        }

        private static InputField InputFieldObject(string name, Transform parent, Font font, string placeholderValue, bool password)
        {
            GameObject root = PanelObject(name, parent, Field);
            Outline outline = root.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);

            InputField input = root.AddComponent<InputField>();
            input.targetGraphic = root.GetComponent<Image>();
            input.lineType = InputField.LineType.SingleLine;
            input.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
            input.characterLimit = password ? 128 : 16;

            Text placeholder = TextObject("Placeholder", root.transform, font, 16, new Color32(112, 121, 136, 255), TextAnchor.MiddleLeft, placeholderValue);
            SetOffsets(placeholder.rectTransform, 14, 14, 4, 4);
            Text value = TextObject("Text", root.transform, font, 16, TextPrimary, TextAnchor.MiddleLeft, string.Empty);
            value.supportRichText = false;
            SetOffsets(value.rectTransform, 14, 14, 4, 4);

            input.placeholder = placeholder;
            input.textComponent = value;
            return input;
        }

        private static Toggle ToggleObject(string name, Transform parent, Font font, string label)
        {
            GameObject root = Node(name, parent);
            Toggle toggle = root.AddComponent<Toggle>();

            Image box = ImageObject("Background", root.transform, Field);
            RectTransform boxRect = box.rectTransform;
            boxRect.anchorMin = boxRect.anchorMax = new Vector2(0f, 0.5f);
            boxRect.pivot = new Vector2(0f, 0.5f);
            boxRect.anchoredPosition = Vector2.zero;
            boxRect.sizeDelta = new Vector2(22f, 22f);
            Outline outline = box.gameObject.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);

            Image check = ImageObject("Checkmark", box.transform, Accent);
            RectTransform checkRect = check.rectTransform;
            checkRect.anchorMin = new Vector2(0.2f, 0.2f);
            checkRect.anchorMax = new Vector2(0.8f, 0.8f);
            checkRect.offsetMin = Vector2.zero;
            checkRect.offsetMax = Vector2.zero;

            Text text = TextObject("Label", root.transform, font, 14, TextSecondary, TextAnchor.MiddleLeft, label);
            SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(32f, 0f), Vector2.zero);

            toggle.targetGraphic = box;
            toggle.graphic = check;
            toggle.isOn = false;
            return toggle;
        }

        private static Button ButtonObject(string name, Transform parent, Font font, string label, Color normal)
        {
            GameObject root = PanelObject(name, parent, normal);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = root.GetComponent<Image>();
            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = Accent;
            colors.pressedColor = AccentPressed;
            colors.selectedColor = Accent;
            colors.disabledColor = new Color(normal.r, normal.g, normal.b, 0.35f);
            button.colors = colors;
            Text text = TextObject("Text", root.transform, font, 15, TextPrimary, TextAnchor.MiddleCenter, label);
            Stretch(text.rectTransform);
            return button;
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

        private static GameObject PanelObject(string name, Transform parent, Color color)
        {
            GameObject go = Node(name, parent, typeof(CanvasRenderer), typeof(Image));
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = color.a > 0.01f;
            return go;
        }

        private static Image ImageObject(string name, Transform parent, Color color)
        {
            GameObject go = Node(name, parent, typeof(CanvasRenderer), typeof(Image));
            Image image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        private static Text TextObject(string name, Transform parent, Font font, int size, Color color, TextAnchor alignment, string value)
        {
            GameObject go = Node(name, parent, typeof(CanvasRenderer), typeof(Text));
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

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
