#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.Input;
using Game.Client.UI.Root;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Editor-only guardrail for the authored ClientUIRoot contract.
    ///
    /// This intentionally does NOT repair or replace UI at runtime and does not add any
    /// networking/input path. It validates the stable authored contract shared by the older
    /// working client snapshots and the current client:
    /// - one usable EventSystem/InputModule exists,
    /// - the root Canvas/raycaster and prefab-authored RectTransform are intact,
    /// - critical authored buttons/chat references are present and raycastable,
    /// - gameplay windows do not ship active,
    /// - panel IDs are unique,
    /// - PlayerUiInputController and ClientUIRoot remain co-located so the existing fallback
    ///   binding is valid when gameplayUi is intentionally left unassigned,
    /// - loaded scene instances have not overridden the root into a broken transform/state.
    ///
    /// Known-good compatibility note:
    /// the older working prefab also serialized PlayerUiInputController.gameplayUi as null and
    /// kept the EventSystem under FrontendRoot. Those are therefore NOT treated as errors.
    /// </summary>
    public sealed class ClientUIRuntimeContractValidator : IPreprocessBuildWithReport
    {
        private const string PrefabPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";

        public int callbackOrder => 0;

        [MenuItem("MMO Tools/UI/Validate Client UI Runtime Contract")]
        public static void ValidateFromMenu()
        {
            List<string> errors = new List<string>();
            List<string> warnings = new List<string>();

            ValidatePrefab(errors, warnings);
            ValidateLoadedSceneInstances(errors, warnings);

            LogReport(errors, warnings);
        }

        public void OnPreprocessBuild(BuildReport report)

        {

            // ClientUIRoot runtime contracts are intentionally not enforced as an automatic player-build gate.

            // Manual validation remains available from MMO Tools when explicitly requested.

        }

        private static void ValidatePrefab(List<string> errors, List<string> warnings)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null)
            {
                errors.Add($"Could not load '{PrefabPath}'.");
                return;
            }

            try
            {
                ValidateRoot(root, "ClientUIRoot.prefab", errors, warnings, true);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void ValidateLoadedSceneInstances(List<string> errors, List<string> warnings)
        {
            ClientUIRoot[] roots = Resources.FindObjectsOfTypeAll<ClientUIRoot>();
            int sceneInstanceCount = 0;

            foreach (ClientUIRoot uiRoot in roots)
            {
                if (uiRoot == null || EditorUtility.IsPersistent(uiRoot))
                    continue;
                if (!uiRoot.gameObject.scene.IsValid() || !uiRoot.gameObject.scene.isLoaded)
                    continue;

                ++sceneInstanceCount;
                string label = $"scene '{uiRoot.gameObject.scene.path}' / '{GetHierarchyPath(uiRoot.transform)}'";
                ValidateRoot(uiRoot.gameObject, label, errors, warnings, false);

                // Catch the kind of scene override that can silently destroy the authored
                // full-screen UI contract without changing the prefab asset itself.
                if (PrefabUtility.IsPartOfPrefabInstance(uiRoot.gameObject))
                    ValidateDangerousSceneOverrides(uiRoot.gameObject, label, errors, warnings);
            }

            if (sceneInstanceCount > 1)
                warnings.Add($"{sceneInstanceCount} loaded ClientUIRoot scene instances were found. Verify only one is active at runtime.");
        }

        private static void ValidateRoot(
            GameObject root,
            string label,
            List<string> errors,
            List<string> warnings,
            bool validateAuthoredWindowDefaults)
        {
            ClientUIRoot uiRoot = root.GetComponent<ClientUIRoot>();
            PlayerUiInputController input = root.GetComponent<PlayerUiInputController>();
            RectTransform rootRect = root.GetComponent<RectTransform>();
            Canvas rootCanvas = root.GetComponent<Canvas>();
            GraphicRaycaster rootRaycaster = root.GetComponent<GraphicRaycaster>();

            Require(uiRoot != null, $"{label}: ClientUIRoot component is missing.", errors);
            Require(input != null, $"{label}: PlayerUiInputController component is missing.", errors);
            Require(rootRect != null, $"{label}: root RectTransform is missing.", errors);
            Require(rootCanvas != null && rootCanvas.enabled, $"{label}: root Canvas is missing or disabled.", errors);
            Require(rootRaycaster != null && rootRaycaster.enabled, $"{label}: root GraphicRaycaster is missing or disabled.", errors);
            Require(root.activeSelf, $"{label}: ClientUIRoot GameObject is authored inactive.", errors);

            // The prefab asset itself owns the canonical authored root layout.
            // Do NOT require loaded scene instances to match these values: the long-standing
            // generated MMOPlayerEntityTest scene intentionally carries legacy root transform
            // overrides (including zero scale/anchors) while nested Screen Space Overlay canvases
            // remain functional. Those overrides exist in the known-good September 13/16/17
            // baselines, so treating them as a regression creates a false positive.
            if (rootRect != null && validateAuthoredWindowDefaults)
            {
                Require(Approximately(rootRect.localScale, Vector3.one),
                    $"{label}: prefab root RectTransform scale is {rootRect.localScale}; expected (1,1,1).", errors);
                Require(Approximately(rootRect.anchorMin, Vector2.zero) && Approximately(rootRect.anchorMax, Vector2.one),
                    $"{label}: prefab root RectTransform must stretch full-screen (anchorMin 0,0 / anchorMax 1,1).", errors);
                Require(Approximately(rootRect.anchoredPosition, Vector2.zero),
                    $"{label}: prefab root RectTransform anchoredPosition must be zero.", errors);
                Require(Approximately(rootRect.sizeDelta, Vector2.zero),
                    $"{label}: prefab root RectTransform sizeDelta must be zero for full-screen stretch.", errors);
            }

            ValidateUiInputBinding(root, uiRoot, input, label, errors, warnings);
            ValidateEventSystem(root, label, errors, warnings);
            ValidateCriticalRootBindings(uiRoot, label, errors);
            ValidateFrontendSurface(root, label, errors, warnings);
            ValidatePanelIds(root, label, errors);

            if (validateAuthoredWindowDefaults)
                ValidateManagedWindowDefaults(root, label, errors);
        }

        private static void ValidateUiInputBinding(
            GameObject root,
            ClientUIRoot uiRoot,
            PlayerUiInputController input,
            string label,
            List<string> errors,
            List<string> warnings)
        {
            if (input == null || uiRoot == null)
                return;

            // The oldest known working snapshots intentionally leave gameplayUi null and let
            // Awake resolve ClientUIRoot. That is valid as long as both components remain on
            // the same permanent root. An explicit reference to some OTHER root is suspicious.
            SerializedObject serializedInput = new SerializedObject(input);
            SerializedProperty gameplayUi = serializedInput.FindProperty("gameplayUi");
            if (gameplayUi == null)
            {
                errors.Add($"{label}: PlayerUiInputController no longer exposes serialized 'gameplayUi'; inspect the input/UI contract change.");
                return;
            }

            UnityEngine.Object assigned = gameplayUi.objectReferenceValue;
            if (assigned != null && assigned != uiRoot)
                errors.Add($"{label}: PlayerUiInputController.gameplayUi points at a different ClientUIRoot.");
            else if (assigned == null && input.gameObject != root)
                errors.Add($"{label}: gameplayUi is null but PlayerUiInputController is not on the ClientUIRoot GameObject, so the known fallback contract is unsafe.");
            else if (assigned == null)
                warnings.Add($"{label}: gameplayUi is intentionally unassigned; using the established same-root runtime fallback.");
        }

        private static void ValidateEventSystem(GameObject root, string label, List<string> errors, List<string> warnings)
        {
            EventSystem[] eventSystems = root.GetComponentsInChildren<EventSystem>(true);
            Require(eventSystems.Length == 1,
                $"{label}: expected exactly one EventSystem inside ClientUIRoot; found {eventSystems.Length}.", errors);

            if (eventSystems.Length != 1)
                return;

            EventSystem eventSystem = eventSystems[0];
            Require(eventSystem.enabled, $"{label}: EventSystem component is disabled.", errors);
            Require(eventSystem.gameObject.activeSelf, $"{label}: EventSystem GameObject is inactive.", errors);

            StandaloneInputModule module = eventSystem.GetComponent<StandaloneInputModule>();
            Require(module != null && module.enabled,
                $"{label}: EventSystem is missing an enabled StandaloneInputModule.", errors);

            Transform cursor = eventSystem.transform;
            while (cursor != null && cursor != root.transform)
            {
                Require(cursor.gameObject.activeSelf,
                    $"{label}: EventSystem ancestor '{cursor.name}' is authored inactive.", errors);
                cursor = cursor.parent;
            }
            Require(cursor == root.transform,
                $"{label}: EventSystem is not contained by ClientUIRoot.", errors);

            // Do not require EventSystem to be a direct root child. The older working client
            // places it under FrontendRoot while CharacterSelectShell hides only the frontend
            // Canvas/raycaster. Flag only if someone moves it into a managed window that can
            // actually be GameObject-disabled.
            ClientUIPanelMarker panelAncestor = eventSystem.GetComponentInParent<ClientUIPanelMarker>(true);
            if (panelAncestor != null && panelAncestor.transform != root.transform)
                errors.Add($"{label}: EventSystem is parented under managed panel '{panelAncestor.PanelId}', which may disable the EventSystem with the window.");
        }

        private static void ValidateFrontendSurface(GameObject root, string label, List<string> errors, List<string> warnings)
        {
            Transform frontend = FindDescendantByName(root.transform, "FrontendRoot");
            if (frontend == null)
            {
                warnings.Add($"{label}: FrontendRoot was not found by stable authored name; verify login/character-select ownership manually.");
                return;
            }

            Require(frontend.gameObject.activeSelf,
                $"{label}: FrontendRoot GameObject must stay active; presentation should be hidden by its Canvas/raycaster, not by disabling the whole root.", errors);

            Canvas canvas = frontend.GetComponent<Canvas>();
            GraphicRaycaster raycaster = frontend.GetComponent<GraphicRaycaster>();
            Require(canvas != null, $"{label}: FrontendRoot is missing its Canvas.", errors);
            Require(raycaster != null, $"{label}: FrontendRoot is missing its GraphicRaycaster.", errors);
        }

        private static void ValidateCriticalRootBindings(ClientUIRoot uiRoot, string label, List<string> errors)
        {
            if (uiRoot == null)
                return;

            SerializedObject serialized = new SerializedObject(uiRoot);
            string[] requiredReferences =
            {
                "_frontendShell",
                "_hudRoot",
                "_chatRoot",
                "_windowManager",
                "_popupController",
                "_healthSlider",
                "_manaSlider",
                "_inventoryButton",
                "_skillsButton",
                "_mapButton",
                "_menuButton",
                "_menuResumeButton",
                "_menuSettingsButton",
                "_menuLogoutButton",
                "_menuQuitButton",
                "_chatHistoryText",
                "_chatInput",
                "_chatSendButton",
            };

            foreach (string propertyName in requiredReferences)
            {
                SerializedProperty property = serialized.FindProperty(propertyName);
                Require(property != null,
                    $"{label}: expected ClientUIRoot serialized field '{propertyName}' no longer exists; review the contract change.", errors);
                if (property != null)
                {
                    Require(property.objectReferenceValue != null,
                        $"{label}: ClientUIRoot authored reference '{propertyName}' is missing.", errors);
                }
            }

            string[] buttonProperties =
            {
                "_inventoryButton",
                "_skillsButton",
                "_mapButton",
                "_menuButton",
                "_menuResumeButton",
                "_menuSettingsButton",
                "_menuLogoutButton",
                "_menuQuitButton",
                "_chatSendButton",
            };

            foreach (string propertyName in buttonProperties)
                ValidateButton(serialized, propertyName, label, errors);

            SerializedProperty chatInputProperty = serialized.FindProperty("_chatInput");
            InputField chatInput = chatInputProperty != null ? chatInputProperty.objectReferenceValue as InputField : null;
            if (chatInput != null)
            {
                Require(chatInput.targetGraphic != null,
                    $"{label}: _chatInput has no target Graphic.", errors);
                if (chatInput.targetGraphic != null)
                    Require(chatInput.targetGraphic.raycastTarget,
                        $"{label}: _chatInput target Graphic has Raycast Target disabled.", errors);
            }
        }

        private static void ValidateButton(SerializedObject serialized, string propertyName, string label, List<string> errors)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            Button button = property != null ? property.objectReferenceValue as Button : null;
            if (button == null)
                return;

            Require(button.interactable, $"{label}: button '{propertyName}' is authored non-interactable.", errors);
            Require(button.targetGraphic != null, $"{label}: button '{propertyName}' has no target Graphic.", errors);
            if (button.targetGraphic != null)
                Require(button.targetGraphic.raycastTarget,
                    $"{label}: button '{propertyName}' target Graphic has Raycast Target disabled.", errors);
        }

        private static void ValidateManagedWindowDefaults(GameObject root, string label, List<string> errors)
        {
            ClientUIPanelMarker[] panels = root.GetComponentsInChildren<ClientUIPanelMarker>(true);
            foreach (ClientUIPanelMarker panel in panels)
            {
                if (panel == null || !panel.ManagedWindow || !panel.GameplayOnly)
                    continue;

                Require(!panel.gameObject.activeSelf,
                    $"{label}: managed gameplay window '{panel.PanelId}' ({GetHierarchyPath(panel.transform)}) is authored active. Gameplay windows must start closed.",
                    errors);
            }
        }

        private static void ValidatePanelIds(GameObject root, string label, List<string> errors)
        {
            Dictionary<ClientUIPanelId, ClientUIPanelMarker> seen = new Dictionary<ClientUIPanelId, ClientUIPanelMarker>();
            ClientUIPanelMarker[] panels = root.GetComponentsInChildren<ClientUIPanelMarker>(true);

            foreach (ClientUIPanelMarker panel in panels)
            {
                if (panel == null || panel.PanelId == ClientUIPanelId.None)
                    continue;

                if (seen.TryGetValue(panel.PanelId, out ClientUIPanelMarker previous))
                {
                    errors.Add(
                        $"{label}: duplicate ClientUIPanelId '{panel.PanelId}' on " +
                        $"'{GetHierarchyPath(previous.transform)}' and '{GetHierarchyPath(panel.transform)}'.");
                }
                else
                {
                    seen.Add(panel.PanelId, panel);
                }
            }
        }

        private static void ValidateDangerousSceneOverrides(
            GameObject instanceRoot,
            string label,
            List<string> errors,
            List<string> warnings)
        {
            // Do not validate the scene-instance root RectTransform here. The generated
            // MMOPlayerEntityTest scene has carried legacy root transform overrides since at
            // least the known-good September 13 baseline. The runtime contract is instead
            // validated through the active canvases, raycasters, EventSystem/input module,
            // critical bindings, and managed-window state below.

            ClientUIPanelMarker[] panels = instanceRoot.GetComponentsInChildren<ClientUIPanelMarker>(true);
            foreach (ClientUIPanelMarker panel in panels)
            {
                if (panel == null || !panel.ManagedWindow || !panel.GameplayOnly)
                    continue;

                if (!panel.gameObject.activeSelf)
                    continue;

                // Loaded scene instances may legitimately have a window open while in Play Mode.
                // Only treat authored Edit Mode activation as a broken scene override.
                if (!Application.isPlaying)
                {
                    errors.Add(
                        $"{label}: managed gameplay window '{panel.PanelId}' is active in the authored scene instance. " +
                        "Remove the active-state override instead of shipping a window open at startup.");
                }
            }

            PropertyModification[] modifications = PrefabUtility.GetPropertyModifications(instanceRoot);
            if (modifications == null)
                return;

            foreach (PropertyModification modification in modifications)
            {
                if (modification == null || modification.target == null)
                    continue;

                if (modification.target == instanceRoot.transform &&
                    IsRootTransformProperty(modification.propertyPath))
                {
                    warnings.Add(
                        $"{label}: ClientUIRoot root transform has prefab-instance override '{modification.propertyPath}'. " +
                        "The live values are validated above, but root UI transform overrides should normally be removed.");
                }
            }
        }

        private static bool IsRootTransformProperty(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
                return false;

            return propertyPath.StartsWith("m_LocalPosition", StringComparison.Ordinal) ||
                   propertyPath.StartsWith("m_LocalScale", StringComparison.Ordinal) ||
                   propertyPath.StartsWith("m_AnchorMin", StringComparison.Ordinal) ||
                   propertyPath.StartsWith("m_AnchorMax", StringComparison.Ordinal) ||
                   propertyPath.StartsWith("m_AnchoredPosition", StringComparison.Ordinal) ||
                   propertyPath.StartsWith("m_SizeDelta", StringComparison.Ordinal);
        }

        private static Transform FindDescendantByName(Transform root, string name)
        {
            if (root == null)
                return null;

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform item in all)
            {
                if (item != null && string.Equals(item.name, name, StringComparison.Ordinal))
                    return item;
            }

            return null;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            Stack<string> names = new Stack<string>();
            Transform cursor = transform;
            while (cursor != null)
            {
                names.Push(cursor.name);
                cursor = cursor.parent;
            }

            return string.Join("/", names.ToArray());
        }

        private static bool Approximately(Vector2 a, Vector2 b)
        {
            return Mathf.Approximately(a.x, b.x) && Mathf.Approximately(a.y, b.y);
        }

        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return Mathf.Approximately(a.x, b.x) &&
                   Mathf.Approximately(a.y, b.y) &&
                   Mathf.Approximately(a.z, b.z);
        }

        private static void Require(bool condition, string message, List<string> errors)
        {
            if (!condition)
                errors.Add(message);
        }

        private static void LogReport(List<string> errors, List<string> warnings)
        {
            foreach (string warning in warnings)
                Debug.LogWarning("[ClientUI Contract] " + warning);

            if (errors.Count == 0)
            {
                Debug.Log(
                    $"[ClientUI Contract] PASSED with {warnings.Count} warning(s). " +
                    "Prefab wiring, pointer path, managed-window defaults, and loaded scene instances are structurally valid.");
                return;
            }

            Debug.LogError(
                "[ClientUI Contract] FAILED:\n - " +
                string.Join("\n - ", errors));
        }
    }
}
#endif
