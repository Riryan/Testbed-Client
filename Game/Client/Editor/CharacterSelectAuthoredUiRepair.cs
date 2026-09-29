#if UNITY_EDITOR
using System;
using Game.Client.Presentation.Characters;
using Game.Client.UI.CharacterSelect;
using Game.Client.UI.Root;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Reusable frontend authoring utility for the canonical ClientUIRoot.prefab.
    /// Production UI stays prefab-authored and editable; runtime construction remains
    /// compatibility fallback only.
    /// </summary>
    public static class CharacterSelectAuthoredUiRepair
    {
        private const string MasterPrefabPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";

        [MenuItem("MMO Tools/UI/Frontend/Rebuild Authored Frontend")]
        public static void InstallOrRepair()
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(MasterPrefabPath);
            if (contents == null)
                throw new InvalidOperationException($"Could not load {MasterPrefabPath}");

            try
            {
                CharacterSelectShell shell = contents.GetComponentInChildren<CharacterSelectShell>(true);
                if (shell == null)
                    throw new InvalidOperationException("ClientUIRoot.prefab does not contain CharacterSelectShell.");

                Repair(shell);
                EnsurePopupOverlay(contents);
                PrefabUtility.SaveAsPrefabAsset(contents, MasterPrefabPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            Validate();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(MasterPrefabPath);
            Debug.Log(
                "[FrontendUI] Authored frontend repaired. ClientUIRoot.prefab is the production source of truth; " +
                "runtime UI construction is fallback-only.");
        }

        [MenuItem("MMO Tools/UI/Frontend/Validate Authored Frontend")]
        public static void Validate()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MasterPrefabPath);
            if (prefab == null)
                throw new InvalidOperationException($"Missing {MasterPrefabPath}");

            CharacterSelectShell shell = prefab.GetComponentInChildren<CharacterSelectShell>(true);
            if (shell == null)
                throw new InvalidOperationException("CharacterSelectShell is missing from ClientUIRoot.prefab.");

            SerializedObject so = new SerializedObject(shell);
            RequireReference(so, "loginPanel", "Login Panel");
            RequireReference(so, "accountInput", "Account Name Input");
            RequireReference(so, "passwordInput", "Password Input");
            RequireReference(so, "loginButton", "Sign In Button");
            RequireReference(so, "createAccountButton", "Create Account Button");
            RequireReference(so, "quitButton", "Quit Button");
            RequireReference(so, "rememberAccountToggle", "Remember Account Toggle");
            RequireReference(so, "serverAvailabilityText", "Server Availability Text");
            RequireVisibleColor(so, "serverOnlineColor", "Server Online Color");
            RequireVisibleColor(so, "serverCheckingColor", "Server Checking Color");
            RequireVisibleColor(so, "serverOfflineColor", "Server Offline Color");

            RequireReference(so, "characterPanel", "Character Select Panel");
            RequireReference(so, "createCharacterButton", "Create Character Button");
            RequireReference(so, "deleteCharacterButton", "Delete Character Button");
            RequireReference(so, "enterWorldButton", "Enter World Button");
            RequireReference(so, "disconnectButton", "Sign Out Button");
            RequireReference(so, "previewController", "Character Select Preview Controller");

            SerializedProperty refresh = so.FindProperty("refreshButton");
            if (refresh != null && refresh.objectReferenceValue != null)
                throw new InvalidOperationException("Authored frontend still has a player-facing Refresh/Recover button bound.");

            SerializedProperty slots = so.FindProperty("slots");
            if (slots == null || slots.arraySize != 4)
                throw new InvalidOperationException("Character Select must author exactly 4 character slots.");
            for (int i = 0; i < 4; ++i)
            {
                if (slots.GetArrayElementAtIndex(i).objectReferenceValue == null)
                    throw new InvalidOperationException($"Character Slot {i + 1} is not bound.");
            }

            GameObject characterPanel = so.FindProperty("characterPanel")?.objectReferenceValue as GameObject;
            CharacterSelectSlotView[] authoredSlotObjects = characterPanel != null
                ? characterPanel.GetComponentsInChildren<CharacterSelectSlotView>(true)
                : Array.Empty<CharacterSelectSlotView>();
            if (authoredSlotObjects.Length != 4)
            {
                throw new InvalidOperationException(
                    $"Character Select hierarchy must contain exactly 4 slot objects; found {authoredSlotObjects.Length}. " +
                    "Stale/orphan slot GameObjects must be removed.");
            }
            for (int i = 0; i < authoredSlotObjects.Length; ++i)
            {
                bool bound = false;
                for (int j = 0; j < slots.arraySize; ++j)
                {
                    if (slots.GetArrayElementAtIndex(j).objectReferenceValue == authoredSlotObjects[i])
                    {
                        bound = true;
                        break;
                    }
                }
                if (!bound)
                    throw new InvalidOperationException($"Orphan Character Select slot '{authoredSlotObjects[i].gameObject.name}' is still authored.");
            }

            CharacterSelectPreviewController preview =
                so.FindProperty("previewController")?.objectReferenceValue as CharacterSelectPreviewController;
            if (preview == null)
                throw new InvalidOperationException("Character Select Preview Controller is not bound.");
            SerializedObject previewSo = new SerializedObject(preview);
            RequireReference(previewSo, "previewImage", "Character Preview Image");
            RequireReference(previewSo, "previewNameText", "Preview Name Text");

            SerializedProperty creator = so.FindProperty("_characterCreator");
            if (creator == null || creator.objectReferenceValue == null)
                throw new InvalidOperationException("Character Select is not bound to the authored Character Creator.");

            ValidatePopupOverlay(prefab);
            CharacterCreatorAuthoredUiRepair.Validate();
            Debug.Log("[FrontendUI] Authored frontend validation passed. Runtime visual fallback should not be needed.", prefab);
        }

        internal static void Repair(CharacterSelectShell shell)
        {
            SerializedObject so = new SerializedObject(shell);

            SerializedProperty onlineColor = so.FindProperty("serverOnlineColor");
            SerializedProperty checkingColor = so.FindProperty("serverCheckingColor");
            SerializedProperty offlineColor = so.FindProperty("serverOfflineColor");
            if (onlineColor != null) onlineColor.colorValue = new Color(0.25f, 0.9f, 0.4f, 1f);
            if (checkingColor != null) checkingColor.colorValue = new Color(0.95f, 0.72f, 0.2f, 1f);
            if (offlineColor != null) offlineColor.colorValue = new Color(0.9f, 0.3f, 0.3f, 1f);

            GameObject connectionPanel = GetReference<GameObject>(so, "connectionPanel");
            GameObject loginPanel = GetReference<GameObject>(so, "loginPanel");
            GameObject characterPanel = GetReference<GameObject>(so, "characterPanel");
            if (loginPanel == null || characterPanel == null)
                throw new InvalidOperationException("CharacterSelectShell is missing its authored Login/Character panels.");

            Rename(connectionPanel, "Connection Panel");
            Rename(loginPanel, "Login Panel");
            Rename(characterPanel, "Character Select Panel");

            Rename(GetReference<InputField>(so, "addressInput"), "Server Address Input");
            Rename(GetReference<InputField>(so, "portInput"), "Server Port Input");
            Rename(GetReference<Text>(so, "connectionStatusText"), "Connection Status Text");
            Rename(GetReference<Button>(so, "startClientButton"), "Connect Button");

            Rename(GetReference<InputField>(so, "accountInput"), "Account Name Input");
            Rename(GetReference<InputField>(so, "passwordInput"), "Password Input");
            Rename(GetReference<Text>(so, "loginStatusText"), "Login Status Text");
            Button loginButton = GetReference<Button>(so, "loginButton");
            Rename(loginButton, "Sign In Button");
            SetButtonLabel(loginButton, "SIGN IN");
            Button createAccount = GetReference<Button>(so, "createAccountButton");
            Rename(createAccount, "Create Account Button");
            SetButtonLabel(createAccount, "CREATE ACCOUNT");
            Rename(GetReference<Button>(so, "loginDisconnectButton"), "Disconnect Button");

            Button quit = GetReference<Button>(so, "quitButton");
            if (quit == null)
            {
                quit = FindComponent<Button>(loginPanel.transform, "Quit Button");
                if (quit == null)
                    quit = CreateButton(
                        "Quit Button", loginPanel.transform, "QUIT",
                        new Vector2(0f, -175f), new Vector2(160f, 46f));
                so.FindProperty("quitButton").objectReferenceValue = quit;
            }
            Rename(quit, "Quit Button");
            SetButtonLabel(quit, "QUIT");
            SetRect(quit.GetComponent<RectTransform>(), new Vector2(0f, -175f), new Vector2(160f, 46f));

            Text availability = GetReference<Text>(so, "serverAvailabilityText");
            if (availability == null)
            {
                availability = FindComponent<Text>(loginPanel.transform, "Server Availability Text");
                if (availability == null)
                {
                    availability = CreateText(
                        "Server Availability Text",
                        loginPanel.transform,
                        "●  CHECKING",
                        15,
                        TextAnchor.MiddleRight,
                        new Vector2(165f, 150f),
                        new Vector2(230f, 30f));
                }
                so.FindProperty("serverAvailabilityText").objectReferenceValue = availability;
            }
            else
            {
                Rename(availability, "Server Availability Text");
            }

            Toggle remember = GetReference<Toggle>(so, "rememberAccountToggle");
            if (remember == null)
            {
                remember = FindComponent<Toggle>(loginPanel.transform, "Remember Account Toggle");
                if (remember == null)
                    remember = CreateRememberAccountToggle(loginPanel.transform);
                so.FindProperty("rememberAccountToggle").objectReferenceValue = remember;
            }
            else
            {
                Rename(remember, "Remember Account Toggle");
            }

            Text status = GetReference<Text>(so, "statusText");
            Rename(status, "Status Text");

            InputField legacyNameInput = GetReference<InputField>(so, "characterNameInput");
            if (legacyNameInput != null)
            {
                Rename(legacyNameInput, "Legacy Character Name Input");
                legacyNameInput.gameObject.SetActive(false);
            }

            Button createCharacter = GetReference<Button>(so, "createCharacterButton");
            Rename(createCharacter, "Create Character Button");
            SetButtonLabel(createCharacter, "CREATE CHARACTER");

            Button legacyRefresh = GetReference<Button>(so, "refreshButton");
            if (legacyRefresh != null)
            {
                so.FindProperty("refreshButton").objectReferenceValue = null;
                if (legacyRefresh.transform.IsChildOf(characterPanel.transform))
                    UnityEngine.Object.DestroyImmediate(legacyRefresh.gameObject);
            }

            Button delete = GetReference<Button>(so, "deleteCharacterButton");
            if (delete == null)
            {
                delete = FindComponent<Button>(characterPanel.transform, "Delete Character Button");
                if (delete == null)
                    delete = CreateButton(
                        "Delete Character Button", characterPanel.transform, "DELETE CHARACTER",
                        new Vector2(-430f, -330f), new Vector2(220f, 52f));
                so.FindProperty("deleteCharacterButton").objectReferenceValue = delete;
            }
            Rename(delete, "Delete Character Button");
            SetButtonLabel(delete, "DELETE CHARACTER");

            Button enter = GetReference<Button>(so, "enterWorldButton");
            Rename(enter, "Enter World Button");
            SetButtonLabel(enter, "ENTER WORLD");

            Button signOut = GetReference<Button>(so, "disconnectButton");
            Rename(signOut, "Sign Out Button");
            SetButtonLabel(signOut, "SIGN OUT");

            SerializedProperty slots = so.FindProperty("slots");
            PruneOrphanCharacterSlots(characterPanel.transform, slots);
            if (slots != null && slots.arraySize > 4)
                slots.arraySize = 4;
            if (slots != null)
            {
                for (int i = 0; i < slots.arraySize; ++i)
                {
                    CharacterSelectSlotView slot = slots.GetArrayElementAtIndex(i).objectReferenceValue as CharacterSelectSlotView;
                    if (slot != null)
                        slot.gameObject.name = $"Character Slot {i + 1}";
                }
            }

            CharacterSelectPreviewController preview = GetReference<CharacterSelectPreviewController>(so, "previewController");
            if (preview == null)
            {
                preview = FindComponent<CharacterSelectPreviewController>(characterPanel.transform, "Character Preview Frame");
                if (preview == null)
                    preview = CreatePreviewSurface(characterPanel.transform);
                so.FindProperty("previewController").objectReferenceValue = preview;
            }
            Rename(preview, "Character Preview Frame");

            ApplyCharacterSelectLayout(characterPanel, createCharacter, delete, enter, signOut, slots, preview, status);

            so.ApplyModifiedPropertiesWithoutUndo();
            CharacterCreatorAuthoredUiRepair.Repair(shell);
            EditorUtility.SetDirty(shell);
        }


        private static void PruneOrphanCharacterSlots(Transform characterPanel, SerializedProperty slots)
        {
            if (characterPanel == null || slots == null)
                return;

            int canonicalCount = Mathf.Min(4, slots.arraySize);
            CharacterSelectSlotView[] canonical = new CharacterSelectSlotView[canonicalCount];
            for (int i = 0; i < canonicalCount; ++i)
                canonical[i] = slots.GetArrayElementAtIndex(i).objectReferenceValue as CharacterSelectSlotView;

            CharacterSelectSlotView[] all = characterPanel.GetComponentsInChildren<CharacterSelectSlotView>(true);
            for (int i = 0; i < all.Length; ++i)
            {
                CharacterSelectSlotView candidate = all[i];
                if (candidate == null || Array.IndexOf(canonical, candidate) >= 0)
                    continue;

                UnityEngine.Object.DestroyImmediate(candidate.gameObject);
            }
        }

        private static void ApplyCharacterSelectLayout(
            GameObject characterPanel,
            Button createCharacter,
            Button delete,
            Button enter,
            Button signOut,
            SerializedProperty slots,
            CharacterSelectPreviewController preview,
            Text status)
        {
            if (characterPanel == null)
                return;

            SetRect(characterPanel.GetComponent<RectTransform>(), Vector2.zero, new Vector2(1380f, 780f));

            Transform title = characterPanel.transform.Find("Title Text");
            Transform instruction = characterPanel.transform.Find("Instruction Text");
            if (title != null)
                SetRect(title.GetComponent<RectTransform>(), new Vector2(-385f, 325f), new Vector2(560f, 50f));
            if (instruction != null)
                SetRect(instruction.GetComponent<RectTransform>(), new Vector2(-385f, 286f), new Vector2(560f, 30f));
            if (status != null)
                SetRect(status.rectTransform, new Vector2(-385f, 245f), new Vector2(560f, 34f));
            if (createCharacter != null)
                SetRect(createCharacter.GetComponent<RectTransform>(), new Vector2(-385f, 185f), new Vector2(560f, 50f));

            if (slots != null)
            {
                float[] y = { 112f, 48f, -16f, -80f };
                for (int i = 0; i < slots.arraySize && i < y.Length; ++i)
                {
                    CharacterSelectSlotView slot = slots.GetArrayElementAtIndex(i).objectReferenceValue as CharacterSelectSlotView;
                    if (slot == null)
                        continue;

                    if (slot.transform.parent != characterPanel.transform)
                        slot.transform.SetParent(characterPanel.transform, false);
                    SetRect(slot.GetComponent<RectTransform>(), new Vector2(-385f, y[i]), new Vector2(560f, 52f));
                    NormalizeCharacterSlotText(slot);
                }
            }

            if (preview != null)
            {
                if (preview.transform.parent != characterPanel.transform)
                    preview.transform.SetParent(characterPanel.transform, false);
                SetRect(preview.GetComponent<RectTransform>(), new Vector2(330f, 35f), new Vector2(610f, 590f));
            }
            if (delete != null)
                SetRect(delete.GetComponent<RectTransform>(), new Vector2(-475f, -335f), new Vector2(220f, 50f));
            if (enter != null)
                SetRect(enter.GetComponent<RectTransform>(), new Vector2(-175f, -335f), new Vector2(270f, 50f));
            if (signOut != null)
                SetRect(signOut.GetComponent<RectTransform>(), new Vector2(485f, -335f), new Vector2(220f, 50f));
        }

        private static void NormalizeCharacterSlotText(CharacterSelectSlotView slot)
        {
            if (slot == null)
                return;
            SerializedObject slotSo = new SerializedObject(slot);
            Text name = slotSo.FindProperty("nameText")?.objectReferenceValue as Text;
            Text map = slotSo.FindProperty("mapText")?.objectReferenceValue as Text;

            if (name != null)
            {
                name.alignment = TextAnchor.MiddleLeft;
                RectTransform rect = name.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(18f, 0f);
                rect.sizeDelta = new Vector2(330f, 34f);
            }

            if (map != null)
            {
                map.alignment = TextAnchor.MiddleRight;
                RectTransform rect = map.rectTransform;
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
                rect.pivot = new Vector2(1f, 0.5f);
                rect.anchoredPosition = new Vector2(-16f, 0f);
                rect.sizeDelta = new Vector2(180f, 30f);
            }
        }

        private static void EnsurePopupOverlay(GameObject root)
        {
            if (root == null)
                throw new InvalidOperationException("ClientUIRoot prefab root is unavailable while configuring popup layering.");

            ClientPopupController popupController = root.GetComponentInChildren<ClientPopupController>(true);
            Transform popupRoot = popupController != null ? popupController.transform : null;
            if (popupRoot == null)
                throw new InvalidOperationException("ClientUIRoot.prefab is missing the authored popup controller surface.");

            Canvas frontendCanvas = root.GetComponentInChildren<CharacterSelectShell>(true)?.GetComponent<Canvas>();
            int minimumOrder = frontendCanvas != null ? frontendCanvas.sortingOrder + 100 : 200;

            Canvas popupCanvas = popupRoot.GetComponent<Canvas>();
            if (popupCanvas == null)
                popupCanvas = popupRoot.gameObject.AddComponent<Canvas>();
            popupCanvas.overrideSorting = true;
            popupCanvas.sortingOrder = Mathf.Max(popupCanvas.sortingOrder, minimumOrder);

            if (popupRoot.GetComponent<GraphicRaycaster>() == null)
                popupRoot.gameObject.AddComponent<GraphicRaycaster>();
        }

        private static void ValidatePopupOverlay(GameObject root)
        {
            ClientPopupController popupController = root != null
                ? root.GetComponentInChildren<ClientPopupController>(true)
                : null;
            Transform popupRoot = popupController != null ? popupController.transform : null;
            if (popupRoot == null)
                throw new InvalidOperationException("Authored frontend is missing the popup controller surface.");

            Canvas popupCanvas = popupRoot.GetComponent<Canvas>();
            if (popupCanvas == null || !popupCanvas.overrideSorting)
                throw new InvalidOperationException("Popups must have an authored override-sorting Canvas.");
            if (popupRoot.GetComponent<GraphicRaycaster>() == null)
                throw new InvalidOperationException("Popups must have an authored GraphicRaycaster.");

            CharacterSelectShell shell = root.GetComponentInChildren<CharacterSelectShell>(true);
            Canvas frontendCanvas = shell != null ? shell.GetComponent<Canvas>() : null;
            if (frontendCanvas != null && popupCanvas.sortingOrder <= frontendCanvas.sortingOrder)
            {
                throw new InvalidOperationException(
                    $"Popups sorting order ({popupCanvas.sortingOrder}) must be above the frontend canvas ({frontendCanvas.sortingOrder}).");
            }
        }

        private static CharacterSelectPreviewController CreatePreviewSurface(Transform parent)
        {
            GameObject frame = CreateUiObject("Character Preview Frame", parent);
            Image frameImage = frame.AddComponent<Image>();
            frameImage.color = new Color(0.055f, 0.06f, 0.08f, 1f);
            SetRect(frame.GetComponent<RectTransform>(), new Vector2(365f, -35f), new Vector2(650f, 535f));

            GameObject previewObject = CreateUiObject("Character Preview Image", frame.transform);
            RawImage rawImage = previewObject.AddComponent<RawImage>();
            rawImage.color = Color.white;
            RectTransform previewRect = rawImage.rectTransform;
            previewRect.anchorMin = Vector2.zero;
            previewRect.anchorMax = Vector2.one;
            previewRect.offsetMin = new Vector2(8f, 46f);
            previewRect.offsetMax = new Vector2(-8f, -8f);

            Text previewName = CreateText(
                "Preview Name Text", frame.transform, string.Empty, 20, TextAnchor.MiddleLeft,
                new Vector2(0f, -24f), new Vector2(590f, 34f));
            previewName.rectTransform.anchorMin = previewName.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            previewName.rectTransform.pivot = new Vector2(0.5f, 0.5f);

            Text hint = CreateText(
                "Rotate Hint Text", frame.transform, "Drag preview to rotate", 14, TextAnchor.MiddleRight,
                new Vector2(0f, 22f), new Vector2(590f, 30f));
            hint.color = new Color(0.62f, 0.66f, 0.74f, 1f);
            hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            hint.rectTransform.pivot = new Vector2(0.5f, 0.5f);

            CharacterSelectPreviewController controller = frame.AddComponent<CharacterSelectPreviewController>();
            SerializedObject previewSo = new SerializedObject(controller);
            previewSo.FindProperty("previewImage").objectReferenceValue = rawImage;
            previewSo.FindProperty("previewNameText").objectReferenceValue = previewName;
            previewSo.ApplyModifiedPropertiesWithoutUndo();

            CharacterSelectPreviewDragSurface drag = previewObject.AddComponent<CharacterSelectPreviewDragSurface>();
            drag.controller = controller;
            return controller;
        }

        private static Toggle CreateRememberAccountToggle(Transform parent)
        {
            GameObject root = CreateUiObject("Remember Account Toggle", parent);
            SetRect(root.GetComponent<RectTransform>(), new Vector2(-145f, -45f), new Vector2(260f, 32f));

            GameObject box = CreateUiObject("Checkbox Background", root.transform);
            Image boxImage = box.AddComponent<Image>();
            boxImage.color = new Color(0.18f, 0.2f, 0.27f, 1f);
            RectTransform boxRect = box.GetComponent<RectTransform>();
            boxRect.anchorMin = boxRect.anchorMax = new Vector2(0f, 0.5f);
            boxRect.pivot = new Vector2(0f, 0.5f);
            boxRect.sizeDelta = new Vector2(24f, 24f);
            boxRect.anchoredPosition = Vector2.zero;

            GameObject check = CreateUiObject("Checkmark", box.transform);
            Image checkImage = check.AddComponent<Image>();
            checkImage.color = new Color(0.35f, 0.72f, 1f, 1f);
            RectTransform checkRect = check.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.2f, 0.2f);
            checkRect.anchorMax = new Vector2(0.8f, 0.8f);
            checkRect.offsetMin = Vector2.zero;
            checkRect.offsetMax = Vector2.zero;

            Text label = CreateText(
                "Remember Account Label", root.transform, "Remember account", 15, TextAnchor.MiddleLeft,
                new Vector2(34f, 0f), new Vector2(210f, 30f));
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            label.rectTransform.pivot = new Vector2(0f, 0.5f);

            Toggle toggle = root.AddComponent<Toggle>();
            toggle.targetGraphic = boxImage;
            toggle.graphic = checkImage;
            toggle.isOn = false;
            return toggle;
        }

        private static Button CreateButton(string name, Transform parent, string label, Vector2 position, Vector2 size)
        {
            GameObject go = CreateUiObject(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.15f, 0.18f, 0.24f, 1f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            SetRect(go.GetComponent<RectTransform>(), position, size);

            Text text = CreateText("Button Label", go.transform, label, 14, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero);
            RectTransform rect = text.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return button;
        }

        private static Text CreateText(
            string name,
            Transform parent,
            string value,
            int size,
            TextAnchor anchor,
            Vector2 position,
            Vector2 dimensions)
        {
            GameObject go = CreateUiObject(name, parent);
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                        Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.text = value;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            SetRect(text.rectTransform, position, dimensions);
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            if (rect == null)
                return;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static T GetReference<T>(SerializedObject so, string property) where T : UnityEngine.Object
        {
            return so.FindProperty(property)?.objectReferenceValue as T;
        }

        private static T FindComponent<T>(Transform root, string exactName) where T : Component
        {
            if (root == null)
                return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; ++i)
            {
                if (!string.Equals(all[i].name, exactName, StringComparison.Ordinal))
                    continue;
                T component = all[i].GetComponent<T>();
                if (component != null)
                    return component;
            }
            return null;
        }

        private static void RequireVisibleColor(SerializedObject so, string property, string displayName)
        {
            SerializedProperty p = so.FindProperty(property);
            if (p == null || p.colorValue.a <= 0f)
                throw new InvalidOperationException($"Authored frontend is missing '{displayName}' ({property}).");
        }

        private static void RequireReference(SerializedObject so, string property, string displayName)
        {
            SerializedProperty p = so.FindProperty(property);
            if (p == null || p.objectReferenceValue == null)
                throw new InvalidOperationException($"Authored frontend is missing '{displayName}' ({property}).");
        }

        private static void Rename(Component component, string name)
        {
            if (component != null)
                component.gameObject.name = name;
        }

        private static void Rename(GameObject gameObject, string name)
        {
            if (gameObject != null)
                gameObject.name = name;
        }

        private static void SetButtonLabel(Button button, string text)
        {
            if (button == null)
                return;
            Text label = button.GetComponentInChildren<Text>(true);
            if (label == null)
                return;
            label.gameObject.name = "Button Label";
            label.text = text;
        }
    }
}
#endif
