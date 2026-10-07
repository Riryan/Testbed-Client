#if UNITY_EDITOR
using System;
using Game.Client.UI.Achievements;
using Game.Client.UI.Standalone;
using Player.Client;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Authors Achievements directly into the user's real StandaloneClientUI.prefab.
    ///
    /// It clones the existing Skills quick-link/window so the Achievement UI inherits
    /// the exact current authored styling, dimensions, outline, header, scroll view,
    /// progression row template, and status presentation.
    ///
    /// Runtime does not construct the window hierarchy.
    /// </summary>
    public static class AchievementMilestoneUIAuthoring
    {
        private const string TargetPath =
            "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        [MenuItem("MMO Tools/UI/Achievements/Install or Repair + Validate")]
        public static void InstallOrRepairAndValidate()
        {
            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);

            if (prefab == null)
                throw new InvalidOperationException(
                    $"StandaloneClientUI prefab not found: {TargetPath}");

            GameObject root =
                PrefabUtility.LoadPrefabContents(TargetPath);

            try
            {
                InstallOrRepair(root);
                PrefabUtility.SaveAsPrefabAsset(root, TargetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Validate();
        }

        [MenuItem("MMO Tools/UI/Achievements/Validate")]
        public static void Validate()
        {
            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);

            if (prefab == null)
                throw new InvalidOperationException(
                    $"StandaloneClientUI prefab not found: {TargetPath}");

            StandaloneAchievementMilestoneWindow ui =
                prefab.GetComponentInChildren<
                    StandaloneAchievementMilestoneWindow>(true);

            if (ui == null ||
                !ui.HasAuthoredBindings)
                throw new InvalidOperationException(
                    "Standalone Achievement UI is missing or has incomplete authored bindings.");

            StandaloneAchievementHotkeyBridge bridge =
                prefab.GetComponent<StandaloneAchievementHotkeyBridge>();

            if (bridge == null || !bridge.HasAuthoredBindings)
                throw new InvalidOperationException(
                    "Achievement hotkey bridge is missing or has incomplete authored bindings.");

            StandaloneControlsWindow controls =
                prefab.GetComponentInChildren<StandaloneControlsWindow>(true);

            bool hasBindingRow = false;
            if (controls != null)
            {
                StandaloneControlBindingRow[] rows =
                    controls.GetComponentsInChildren<StandaloneControlBindingRow>(true);

                for (int i = 0; i < rows.Length; ++i)
                {
                    if (rows[i] != null &&
                        rows[i].Action == PlayerControlAction.OpenAchievements)
                    {
                        hasBindingRow = true;
                        break;
                    }
                }
            }

            if (!hasBindingRow)
                throw new InvalidOperationException(
                    "Controls UI is missing the Achievements binding row.");

            Debug.Log(
                "[ClientUI] Achievement/Milestone UI validated against StandaloneClientUI.prefab. " +
                "Achievements is in Controls with default Y and remains local-only.",
                prefab);

            Selection.activeObject = prefab;
        }

        private static void InstallOrRepair(
            GameObject root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            StandaloneSkillsWindow sourceSkills =
                root.GetComponentInChildren<
                    StandaloneSkillsWindow>(true);

            if (sourceSkills == null)
                throw new InvalidOperationException(
                    "The uploaded/current StandaloneClientUI prefab has no StandaloneSkillsWindow.");

            GameObject skillsWindow =
                sourceSkills.gameObject;

            Button skillsQuickLink =
                FindButton(root, "Skills");

            if (skillsQuickLink == null)
                throw new InvalidOperationException(
                    "The real StandaloneClientUI QuickLinks/Skills button was not found.");

            Button achievementQuickLink =
                EnsureAchievementQuickLink(
                    skillsQuickLink);

            GameObject achievementWindow =
                EnsureAchievementWindow(
                    skillsWindow);

            StandaloneAchievementMilestoneWindow achievementUi =
                ConfigureAchievementWindow(
                    achievementWindow,
                    skillsWindow,
                    achievementQuickLink,
                    skillsQuickLink);

            EnsureAchievementControlBinding(root);
            EnsureAchievementHotkeyBridge(root, achievementUi);

            ExpandQuickLinksForNewButton(
                achievementQuickLink.transform.parent as RectTransform);

            EditorUtility.SetDirty(root);
        }

        private static Button EnsureAchievementQuickLink(
            Button skillsButton)
        {
            Transform parent =
                skillsButton.transform.parent;

            if (parent == null)
                throw new InvalidOperationException(
                    "Skills QuickLink has no parent.");

            Transform existing =
                parent.Find("Achievements");

            Button button =
                existing != null
                    ? existing.GetComponent<Button>()
                    : null;

            if (button == null)
            {
                GameObject clone =
                    UnityEngine.Object.Instantiate(
                        skillsButton.gameObject,
                        parent,
                        false);

                clone.name = "Achievements";
                button = clone.GetComponent<Button>();

                if (button == null)
                    throw new InvalidOperationException(
                        "Unable to clone the existing Skills QuickLink button.");

                button.transform.SetSiblingIndex(
                    Math.Min(
                        skillsButton.transform.GetSiblingIndex() + 1,
                        parent.childCount - 1));
            }

            // Do not retain the cloned persistent ToggleSkills call.
            button.onClick =
                new Button.ButtonClickedEvent();

            Text label =
                button.GetComponentInChildren<Text>(true);

            if (label != null)
                label.text = "A";

            StandaloneHudTooltipTarget tooltip =
                button.GetComponent<
                    StandaloneHudTooltipTarget>();

            if (tooltip != null)
            {
                SerializedObject serialized =
                    new SerializedObject(tooltip);

                SerializedProperty tooltipReference =
                    serialized.FindProperty("tooltip");

                StandaloneHudTooltipPanel panel =
                    tooltipReference != null
                        ? tooltipReference.objectReferenceValue
                            as StandaloneHudTooltipPanel
                        : null;

                tooltip.ConfigureForEditor(
                    panel,
                    "Achievements",
                    "Open achievements and milestones.");
            }

            EditorUtility.SetDirty(button);
            return button;
        }

        private static GameObject EnsureAchievementWindow(
            GameObject skillsWindow)
        {
            Transform parent =
                skillsWindow.transform.parent;

            if (parent == null)
                throw new InvalidOperationException(
                    "SkillsWindow has no parent.");

            Transform existing =
                parent.Find("AchievementsWindow");

            GameObject window =
                existing != null
                    ? existing.gameObject
                    : null;

            if (window == null)
            {
                window =
                    UnityEngine.Object.Instantiate(
                        skillsWindow,
                        parent,
                        false);

                window.name = "AchievementsWindow";
                window.transform.SetSiblingIndex(
                    Math.Min(
                        skillsWindow.transform.GetSiblingIndex() + 1,
                        parent.childCount - 1));
            }

            StandaloneSkillsWindow copiedSkills =
                window.GetComponent<
                    StandaloneSkillsWindow>();

            if (copiedSkills != null)
                UnityEngine.Object.DestroyImmediate(
                    copiedSkills);

            window.SetActive(false);

            return window;
        }

        private static StandaloneAchievementMilestoneWindow ConfigureAchievementWindow(
            GameObject window,
            GameObject skillsWindow,
            Button achievementQuickLink,
            Button skillsQuickLink)
        {
            Transform header =
                window.transform.Find("Header");

            Transform body =
                window.transform.Find("Body");

            Transform overlay =
                window.transform.Find("__SkillsProgressionOverlay");

            Transform status =
                window.transform.Find("__SkillsStatusOverlay");

            Transform flatList =
                window.transform.Find("__SkillsFlatListV14");

            if (header == null ||
                overlay == null ||
                status == null)
            {
                throw new InvalidOperationException(
                    "The current real SkillsWindow layout changed. Expected Header, " +
                    "__SkillsProgressionOverlay, and __SkillsStatusOverlay.");
            }

            if (body != null)
                body.gameObject.SetActive(false);

            if (flatList != null)
                flatList.gameObject.SetActive(false);

            overlay.name =
                "__AchievementMilestoneOverlay";

            status.name =
                "__AchievementStatusOverlay";

            overlay.gameObject.SetActive(true);
            status.gameObject.SetActive(true);

            Text title =
                header.Find("Title")?.GetComponent<Text>();

            if (title != null)
                title.text =
                    "ACHIEVEMENTS / MILESTONES";

            Button closeButton =
                header.Find("CloseButton")?.GetComponent<Button>();

            if (closeButton == null)
                throw new InvalidOperationException(
                    "AchievementsWindow clone is missing the existing Skills CloseButton.");

            closeButton.onClick =
                new Button.ButtonClickedEvent();

            Transform skillHeader =
                overlay.Find("SkillHeader");

            if (skillHeader != null)
            {
                SetText(
                    skillHeader.Find("Type"),
                    "TYPE");

                SetText(
                    skillHeader.Find("Skill"),
                    "MILESTONE");

                SetText(
                    skillHeader.Find("Value"),
                    "PROGRESS");
            }

            StandaloneProgressionRow rowTemplate =
                overlay.GetComponentInChildren<
                    StandaloneProgressionRow>(true);

            if (rowTemplate == null)
                throw new InvalidOperationException(
                    "AchievementsWindow clone is missing the existing ProgressionRowTemplate.");

            RectTransform content =
                rowTemplate.transform.parent
                    as RectTransform;

            if (content == null)
                throw new InvalidOperationException(
                    "ProgressionRowTemplate has no RectTransform content parent.");

            rowTemplate.gameObject.name =
                "AchievementRowTemplate";

            rowTemplate.gameObject.SetActive(false);

            // The real Skills content contains only the template today. Remove accidental
            // duplicates if the installer is rerun after a previous partial install.
            for (int i = content.childCount - 1; i >= 0; --i)
            {
                Transform child =
                    content.GetChild(i);

                if (child == rowTemplate.transform)
                    continue;

                if (child.GetComponent<
                        StandaloneProgressionRow>() != null)
                {
                    UnityEngine.Object.DestroyImmediate(
                        child.gameObject);
                }
            }

            Text statusText =
                status.GetComponent<Text>();

            if (statusText == null)
                throw new InvalidOperationException(
                    "Achievements status overlay has no Text component.");

            statusText.text =
                "Achievements use the existing progression cache. Other-faction achievements are hidden.";

            StandaloneAchievementMilestoneWindow binder =
                window.GetComponent<
                    StandaloneAchievementMilestoneWindow>() ??
                window.AddComponent<
                    StandaloneAchievementMilestoneWindow>();

            binder.ConfigureForEditor(
                window,
                skillsWindow,
                achievementQuickLink,
                skillsQuickLink,
                closeButton,
                statusText,
                content,
                rowTemplate);

            EditorUtility.SetDirty(binder);
            EditorUtility.SetDirty(window);
            return binder;
        }

        private static void EnsureAchievementHotkeyBridge(
            GameObject root,
            StandaloneAchievementMilestoneWindow achievementUi)
        {
            StandaloneClientUIRoot uiRoot =
                root.GetComponent<StandaloneClientUIRoot>();

            if (uiRoot == null)
                throw new InvalidOperationException(
                    "StandaloneClientUIRoot was not found on the prefab root.");

            StandaloneAchievementHotkeyBridge bridge =
                root.GetComponent<StandaloneAchievementHotkeyBridge>() ??
                root.AddComponent<StandaloneAchievementHotkeyBridge>();

            bridge.ConfigureForEditor(
                uiRoot,
                achievementUi);

            EditorUtility.SetDirty(bridge);
        }

        private static void EnsureAchievementControlBinding(
            GameObject root)
        {
            StandaloneControlsWindow controls =
                root.GetComponentInChildren<StandaloneControlsWindow>(true);

            if (controls == null)
                throw new InvalidOperationException(
                    "StandaloneControlsWindow was not found in the real StandaloneClientUI prefab.");

            SerializedObject controlsSerialized =
                new SerializedObject(controls);

            SerializedProperty rowsProperty =
                controlsSerialized.FindProperty("rows");

            if (rowsProperty == null)
                throw new InvalidOperationException(
                    "StandaloneControlsWindow.rows was not found.");

            StandaloneControlBindingRow source = null;
            StandaloneControlBindingRow existing = null;

            for (int i = 0; i < rowsProperty.arraySize; ++i)
            {
                StandaloneControlBindingRow row =
                    rowsProperty.GetArrayElementAtIndex(i).objectReferenceValue
                        as StandaloneControlBindingRow;

                if (row == null)
                    continue;

                if (row.Action == PlayerControlAction.OpenAchievements)
                    existing = row;

                if (row.Action == PlayerControlAction.OpenSkills)
                    source = row;
            }

            if (source == null)
                throw new InvalidOperationException(
                    "The existing Skills control binding row was not found.");

            StandaloneControlBindingRow achievementRow = existing;

            if (achievementRow == null)
            {
                GameObject clone =
                    UnityEngine.Object.Instantiate(
                        source.gameObject,
                        source.transform.parent,
                        false);

                clone.name = "Binding_OpenAchievements";

                achievementRow =
                    clone.GetComponent<StandaloneControlBindingRow>();

                if (achievementRow == null)
                    throw new InvalidOperationException(
                        "Unable to clone the existing Skills control binding row.");

                achievementRow.transform.SetSiblingIndex(
                    Math.Min(
                        source.transform.GetSiblingIndex() + 1,
                        source.transform.parent.childCount - 1));
            }

            SerializedObject rowSerialized =
                new SerializedObject(achievementRow);

            Text actionText =
                rowSerialized.FindProperty("actionText")?.objectReferenceValue
                    as Text;

            Text bindingText =
                rowSerialized.FindProperty("bindingText")?.objectReferenceValue
                    as Text;

            Button rebindButton =
                rowSerialized.FindProperty("rebindButton")?.objectReferenceValue
                    as Button;

            achievementRow.ConfigureForEditor(
                PlayerControlAction.OpenAchievements,
                actionText,
                bindingText,
                rebindButton,
                controls);

            bool listed = false;

            for (int i = 0; i < rowsProperty.arraySize; ++i)
            {
                if (rowsProperty.GetArrayElementAtIndex(i).objectReferenceValue ==
                    achievementRow)
                {
                    listed = true;
                    break;
                }
            }

            if (!listed)
            {
                int oldSize = rowsProperty.arraySize;
                rowsProperty.arraySize = oldSize + 1;
                rowsProperty.GetArrayElementAtIndex(oldSize).objectReferenceValue =
                    achievementRow;
            }

            controlsSerialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(achievementRow);
            EditorUtility.SetDirty(controls);
        }

        private static void ExpandQuickLinksForNewButton(
            RectTransform quickLinks)
        {
            if (quickLinks == null)
                return;

            // The real prefab is 322px wide for six fixed 46px buttons + spacing.
            // Add only the space required by the seventh cloned button.
            float requiredWidth = 373f;

            if (quickLinks.sizeDelta.x < requiredWidth)
            {
                quickLinks.sizeDelta =
                    new Vector2(
                        requiredWidth,
                        quickLinks.sizeDelta.y);
            }

            EditorUtility.SetDirty(quickLinks);
        }

        private static Button FindButton(
            GameObject root,
            string exactName)
        {
            Button[] buttons =
                root.GetComponentsInChildren<Button>(true);

            for (int i = 0; i < buttons.Length; ++i)
            {
                Button button =
                    buttons[i];

                if (button != null &&
                    string.Equals(
                        button.gameObject.name,
                        exactName,
                        StringComparison.Ordinal))
                    return button;
            }

            return null;
        }

        private static void SetText(
            Transform transform,
            string value)
        {
            if (transform == null)
                return;

            Text text =
                transform.GetComponent<Text>();

            if (text != null)
                text.text =
                    value ?? string.Empty;
        }
    }
}
#endif
