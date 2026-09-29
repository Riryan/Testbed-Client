#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Client.Editor
{
    public sealed partial class CharacterVisualPaletteAuthoringWindow
    {
        private enum SceneAuthoringTab : byte
        {
            Parts = 0,
            Item = 1,
            Colors = 2,
            Dye = 3,
        }

        private enum ScenePartGroup : byte
        {
            Head = 0,
            Upper = 1,
            Lower = 2,
            Feet = 3,
            Extras = 4,
        }
        private const string AuthoringScenePath = "Assets/Game/Client/Editor/CharacterVisualAuthoring.unity";
        private const string DummyName = "[Character Visual Authoring Dummy]";

        private static readonly ushort[] HeadSlots = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 35, 36, 37 };
        private static readonly ushort[] UpperSlots = { 10, 11, 12, 13, 14, 15, 16 };
        private static readonly ushort[] LowerSlots = { 17, 18, 19 };
        private static readonly ushort[] FeetSlots = { 20, 21 };
        private static readonly ushort[] AccessorySlots = { 22, 23, 24, 29, 30 };

        private GameObject _sceneAuthoringDummy;
        private ModularCharacterAppearancePresenter _sceneAuthoringPresenter;
        private int _sceneWearableIndex = -1;
        private ushort _sceneFocusedSlotId = 10;
        private ushort _sceneColorReviewId = 1;
        private bool _sceneAuthoringDirty;
        private bool _sceneShowHead;
        private bool _sceneShowUpper = true;
        private bool _sceneShowLower = true;
        private bool _sceneShowFeet = true;
        private bool _sceneShowAccessories;
        private bool _sceneShowDyeReview;
        private bool _sceneShowGlobalColorLibrary;
        private bool _sceneReviewColorOnSecondary;
        private bool _scenePreviewReviewColor = true;
        private Vector2 _sceneScroll;
        private SceneAuthoringTab _sceneAuthoringTab = SceneAuthoringTab.Parts;
        private ScenePartGroup _scenePartGroup = ScenePartGroup.Upper;

        // The normal authoring path is an in-memory candidate. It is only appended to the
        // profile when Save As Wearable succeeds. This prevents the old "New" button from
        // creating anonymous profile records just to browse meshes. Saved wearables remain
        // directly editable through the browser below.
        private CharacterWearableSetDefinition _sceneWearableCandidate;
        private bool _sceneCandidateMode = true;
        private bool _sceneCandidateDirty;
        private string _sceneWearableSearch = string.Empty;
        private bool _sceneShowWearableBrowser = true;

        private void DrawSceneAuthoringUI()
        {
            DrawSceneAuthoringHeader();

            if (_profile == null)
            {
                EditorGUILayout.HelpBox("Build or assign CharacterVisualProfile before authoring.", MessageType.Warning);
                return;
            }

            DrawWearableSetHeader();
            CharacterWearableSetDefinition set = GetCurrentWearableSet();
            if (set == null)
            {
                EditorGUILayout.HelpBox(
                    "Create a wearable set to begin. Unassigned foundational body parts resolve to the canonical Default Base.",
                    MessageType.None);
                return;
            }

            SceneAuthoringTab nextTab = (SceneAuthoringTab)GUILayout.Toolbar(
                (int)_sceneAuthoringTab,
                new[] { "PARTS", "ITEM", "COLORS", "DYE" },
                GUILayout.Height(24f));
            if (nextTab != _sceneAuthoringTab)
            {
                _sceneAuthoringTab = nextTab;
                RestorePreview();
                _sceneShowDyeReview = false;
                ApplySceneDraftToDummy();
            }

            _sceneScroll = EditorGUILayout.BeginScrollView(_sceneScroll);
            switch (_sceneAuthoringTab)
            {
                case SceneAuthoringTab.Item:
                    DrawWearableIdentity(set);
                    break;

                case SceneAuthoringTab.Colors:
                    DrawWearableDefaultColors(set);
                    EditorGUILayout.Space(8f);
                    DrawWearableColorReview(set);
                    EditorGUILayout.Space(8f);
                    _sceneShowGlobalColorLibrary = EditorGUILayout.Foldout(
                        _sceneShowGlobalColorLibrary,
                        "GLOBAL COLOR LIBRARY",
                        true);
                    if (_sceneShowGlobalColorLibrary)
                        DrawGlobalColorLibraryReview();
                    break;

                case SceneAuthoringTab.Dye:
                    DrawFocusedMeshDetails();
                    EditorGUILayout.Space(6f);
                    DrawInlineDyeReview();
                    break;

                default:
                    DrawScenePartsTab(set);
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSceneAuthoringHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("CHARACTER VISUAL AUTHORING", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                string saveState;
                if (_sceneCandidateMode)
                {
                    saveState = _sceneCandidateDirty ? "CANDIDATE MODIFIED" : "CANDIDATE";
                    if (_sceneAuthoringDirty)
                        saveState += " / PROFILE UNSAVED";
                }
                else
                {
                    saveState = _sceneAuthoringDirty ? "UNSAVED" : "Saved";
                }
                GUILayout.Label(saveState, EditorStyles.miniBoldLabel);
            }

            CharacterVisualProfile nextProfile = (CharacterVisualProfile)EditorGUILayout.ObjectField(
                "Profile", _profile, typeof(CharacterVisualProfile), false);
            if (nextProfile != _profile)
            {
                RestorePreview();
                _profile = nextProfile;
                _sceneWearableIndex = -1;
                _sceneAuthoringDirty = false;
                _sceneWearableCandidate = null;
                _sceneCandidateMode = true;
                _sceneCandidateDirty = false;
                ApplySceneDraftToDummy();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_profile == null))
                {
                    if (GUILayout.Button("Open / Focus Scene", GUILayout.Height(24f)))
                        OpenOrCreateAuthoringScene();

                    bool inAuthoringScene = SceneManager.GetActiveScene().path == AuthoringScenePath;
                    using (new EditorGUI.DisabledScope(!inAuthoringScene))
                    {
                        if (GUILayout.Button("Frame Dummy", GUILayout.Width(92f), GUILayout.Height(24f)))
                            FrameSceneDummy();
                        if (GUILayout.Button("Rebuild", GUILayout.Width(72f), GUILayout.Height(24f)))
                            RebuildSceneDummy();
                    }

                    if (GUILayout.Button(new GUIContent("Save Assets", "Save profile changes already committed to the asset. A transient candidate is saved with Save As Wearable."), GUILayout.Width(82f), GUILayout.Height(24f)))
                        FlushSceneAuthoringChanges();
                }
            }
        }

        private void DrawScenePartsTab(CharacterWearableSetDefinition set)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(string.IsNullOrWhiteSpace(set.displayName) ? "Unnamed Wearable" : set.displayName, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label(set.region.ToString(), EditorStyles.miniLabel);
                GUILayout.Space(8f);
                GUILayout.Label(set.playerEligible ? "Player" : string.Empty, EditorStyles.miniLabel);
                GUILayout.Label(set.populationEligible ? "Population" : string.Empty, EditorStyles.miniLabel);
            }

            _scenePartGroup = (ScenePartGroup)GUILayout.Toolbar(
                (int)_scenePartGroup,
                new[] { "Head", "Upper", "Lower", "Feet", "Extras" },
                GUILayout.Height(22f));

            EditorGUILayout.Space(4f);
            switch (_scenePartGroup)
            {
                case ScenePartGroup.Head:
                    DrawSlotGroup(set, HeadSlots);
                    break;
                case ScenePartGroup.Lower:
                    DrawSlotGroup(set, LowerSlots);
                    break;
                case ScenePartGroup.Feet:
                    DrawSlotGroup(set, FeetSlots);
                    break;
                case ScenePartGroup.Extras:
                    DrawSlotGroup(set, AccessorySlots);
                    break;
                default:
                    DrawSlotGroup(set, UpperSlots);
                    break;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                "Default Base = no wearable override for that foundational slot. Optional attachment slots use None.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawWearableSetHeader()
        {
            IReadOnlyList<CharacterWearableSetDefinition> sets = _profile.WearableSets;
            int count = sets != null ? sets.Count : 0;
            if (!_sceneCandidateMode && count > 0 && (_sceneWearableIndex < 0 || _sceneWearableIndex >= count))
                _sceneWearableIndex = 0;

            CharacterWearableSetDefinition current = GetCurrentWearableSet();
            int exactMatchIndex = _sceneCandidateMode ? FindWearableByVisualSignature(current, -1) : -1;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("WEARABLE WORKFLOW", EditorStyles.boldLabel, GUILayout.Width(145f));
                    GUILayout.Label($"{count} saved", EditorStyles.miniLabel, GUILayout.Width(65f));
                    GUILayout.FlexibleSpace();

                    if (_sceneCandidateMode)
                    {
                        GUILayout.Label(exactMatchIndex >= 0 ? "MATCHES EXISTING" : "NEW CANDIDATE", EditorStyles.miniBoldLabel);
                        if (GUILayout.Button("Reset Candidate", GUILayout.Width(112f)))
                        {
                            BeginFreshWearableCandidate();
                            GUIUtility.ExitGUI();
                        }
                    }
                    else
                    {
                        GUILayout.Label("EDITING SAVED", EditorStyles.miniBoldLabel);
                        if (GUILayout.Button("Continue As Candidate", GUILayout.Width(145f)))
                        {
                            BeginWearableCandidateFrom(current);
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Find", GUILayout.Width(36f));
                    string nextSearch = EditorGUILayout.TextField(_sceneWearableSearch ?? string.Empty);
                    if (!string.Equals(nextSearch, _sceneWearableSearch, StringComparison.Ordinal))
                        _sceneWearableSearch = nextSearch;

                    _sceneShowWearableBrowser = GUILayout.Toggle(
                        _sceneShowWearableBrowser,
                        _sceneShowWearableBrowser ? "Hide Saved" : "Show Saved",
                        "Button",
                        GUILayout.Width(95f));
                }

                if (_sceneCandidateMode)
                {
                    if (exactMatchIndex >= 0)
                    {
                        CharacterWearableSetDefinition match = sets[exactMatchIndex];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.HelpBox(
                                $"This exact mesh composition is already saved as '{SafeWearableName(match)}' ({match.definitionId}).",
                                MessageType.Info);
                            if (GUILayout.Button("Edit Existing", GUILayout.Width(105f), GUILayout.Height(38f)))
                            {
                                SelectSavedWearable(exactMatchIndex);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "Current assembled mesh composition is not saved yet. Review the item, colors and dye zones, then Save As Wearable.",
                            MessageType.None);
                    }
                }
                else if (current != null)
                {
                    int duplicateVisualIndex = FindWearableByVisualSignature(current, _sceneWearableIndex);
                    if (duplicateVisualIndex >= 0)
                    {
                        CharacterWearableSetDefinition duplicate = sets[duplicateVisualIndex];
                        EditorGUILayout.HelpBox(
                            $"This saved wearable currently has the same mesh composition as '{SafeWearableName(duplicate)}' ({duplicate.definitionId}). This may be intentional, but it is not visually unique.",
                            MessageType.Warning);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(count <= 1))
                        {
                            if (GUILayout.Button("<", GUILayout.Width(32f)))
                            {
                                _sceneWearableIndex = WrapIndex(_sceneWearableIndex - 1, count);
                                OnWearableSelectionChanged();
                            }
                        }

                        GUILayout.Label(
                            $"{_sceneWearableIndex + 1} / {count}   {SafeWearableName(current)}",
                            EditorStyles.miniBoldLabel,
                            GUILayout.MinWidth(250f));

                        using (new EditorGUI.DisabledScope(count <= 1))
                        {
                            if (GUILayout.Button(">", GUILayout.Width(32f)))
                            {
                                _sceneWearableIndex = WrapIndex(_sceneWearableIndex + 1, count);
                                OnWearableSelectionChanged();
                            }
                        }

                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Delete", GUILayout.Width(70f)))
                            DeleteWearableSet();
                    }
                }

                if (_sceneShowWearableBrowser)
                    DrawWearableBrowser(sets);
            }

            CharacterWearableSetDefinition active = GetCurrentWearableSet();
            if (active == null)
                return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_sceneCandidateMode)
                {
                    bool hasExactMatch = FindWearableByVisualSignature(active, -1) >= 0;
                    using (new EditorGUI.DisabledScope(hasExactMatch))
                    {
                        if (GUILayout.Button("Save As Wearable", GUILayout.Height(25f)))
                            SaveWearableCandidate(false);
                    }

                    if (hasExactMatch)
                    {
                        if (GUILayout.Button("Save Intentional Variant", GUILayout.Width(165f), GUILayout.Height(25f)))
                            SaveWearableCandidate(true);
                    }
                }
                else
                {
                    if (GUILayout.Button("Start Next Candidate", GUILayout.Height(25f)))
                    {
                        BeginWearableCandidateFrom(active);
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        private void DrawWearableBrowser(IReadOnlyList<CharacterWearableSetDefinition> sets)
        {
            if (sets == null || sets.Count == 0)
            {
                EditorGUILayout.LabelField("No saved wearables yet.", EditorStyles.miniLabel);
                return;
            }

            string search = (_sceneWearableSearch ?? string.Empty).Trim();
            int shown = 0;
            const int maxShown = 8;
            for (int i = 0; i < sets.Count && shown < maxShown; ++i)
            {
                CharacterWearableSetDefinition set = sets[i];
                if (set == null || !WearableMatchesSearch(set, search))
                    continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(SafeWearableName(set), GUILayout.MinWidth(150f));
                    GUILayout.Label(set.definitionId ?? string.Empty, EditorStyles.miniLabel, GUILayout.MinWidth(180f));
                    GUILayout.Label(set.presentationId == 0 ? "Presentation: —" : $"Presentation: {set.presentationId}", EditorStyles.miniLabel, GUILayout.Width(105f));
                    if (GUILayout.Button("Edit", GUILayout.Width(48f)))
                    {
                        SelectSavedWearable(i);
                        GUIUtility.ExitGUI();
                    }
                }
                shown++;
            }

            if (shown == 0)
                EditorGUILayout.LabelField("No saved wearables match the search.", EditorStyles.miniLabel);
            else if (shown == maxShown)
                EditorGUILayout.LabelField("Showing first 8 matches. Narrow the search to jump directly to a specific wearable.", EditorStyles.wordWrappedMiniLabel);
        }

        private bool WearableMatchesSearch(CharacterWearableSetDefinition set, string search)
        {
            if (set == null || string.IsNullOrWhiteSpace(search))
                return set != null;

            if (ContainsIgnoreCase(set.displayName, search) ||
                ContainsIgnoreCase(set.definitionId, search) ||
                (set.presentationId != 0 && set.presentationId.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            CharacterWearablePartSelection[] parts = set.parts ?? Array.Empty<CharacterWearablePartSelection>();
            for (int i = 0; i < parts.Length; ++i)
            {
                CharacterWearablePartSelection part = parts[i];
                if (part.optionId == 0 || !_profile.TryGetOption(part.slotId, part.optionId, out CharacterVisualOptionDefinition option) || option == null)
                    continue;
                if (ContainsIgnoreCase(option.displayName, search) ||
                    (option.mesh != null && ContainsIgnoreCase(option.mesh.name, search)) ||
                    option.optionId.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool ContainsIgnoreCase(string value, string search)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeWearableName(CharacterWearableSetDefinition set)
        {
            if (set == null)
                return "Unnamed";
            return string.IsNullOrWhiteSpace(set.displayName) ? "Unnamed" : set.displayName;
        }

        private void DrawWearableIdentity(CharacterWearableSetDefinition set)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("ITEM / PRESENTATION IDENTITY", EditorStyles.boldLabel);

            string definitionId = EditorGUILayout.TextField("Internal Definition ID", set.definitionId ?? string.Empty);
            string displayName = EditorGUILayout.TextField("Player-Facing Name", set.displayName ?? string.Empty);
            ushort presentationId = (ushort)Mathf.Clamp(EditorGUILayout.IntField("Presentation ID", set.presentationId), 0, ushort.MaxValue);
            CharacterWearableRegion region = (CharacterWearableRegion)EditorGUILayout.EnumPopup("Dye / Equipment Region", set.region);

            bool player = set.playerEligible;
            bool population = set.populationEligible;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Allowed For");
                player = GUILayout.Toggle(player, "Player", "Button", GUILayout.Width(90f));
                population = GUILayout.Toggle(population, "Population", "Button", GUILayout.Width(100f));
                GUILayout.FlexibleSpace();
            }

            Sprite icon = (Sprite)EditorGUILayout.ObjectField("Inventory Icon", set.icon, typeof(Sprite), false);

            int uniquenessIgnoreIndex = _sceneCandidateMode ? -1 : _sceneWearableIndex;
            int duplicateDefinitionIndex = FindWearableByDefinitionId(definitionId, uniquenessIgnoreIndex);
            if (duplicateDefinitionIndex >= 0)
                EditorGUILayout.HelpBox($"Definition ID already belongs to '{SafeWearableName(_profile.WearableSets[duplicateDefinitionIndex])}'. It cannot be saved until the ID is unique.", MessageType.Error);

            if (presentationId != 0)
            {
                int duplicatePresentationIndex = FindWearableByPresentationId(presentationId, uniquenessIgnoreIndex);
                if (duplicatePresentationIndex >= 0)
                    EditorGUILayout.HelpBox($"Presentation ID {presentationId} already belongs to '{SafeWearableName(_profile.WearableSets[duplicatePresentationIndex])}'.", MessageType.Error);
            }

            if (definitionId != set.definitionId || displayName != set.displayName || presentationId != set.presentationId ||
                region != set.region || player != set.playerEligible || population != set.populationEligible || icon != set.icon)
            {
                RecordWearableUndo(set, "Edit Wearable Set");
                set.definitionId = definitionId;
                set.displayName = displayName;
                set.presentationId = presentationId;
                set.region = region;
                set.playerEligible = player;
                set.populationEligible = population;
                set.icon = icon;
                MarkWearableChanged(set, true);
                ApplySceneDraftToDummy();
            }
        }

        private void DrawWearableDefaultColors(CharacterWearableSetDefinition set)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("DEFAULT ITEM COLORS", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "These are authoring/default presentation colors for this wearable. Runtime dye-zone cells remain defined on the underlying visual options.",
                EditorStyles.wordWrappedMiniLabel);

            ushort primary = DrawPaletteCycleRow("Primary", set.defaultPrimaryColorId);
            ushort secondary = DrawPaletteCycleRow("Secondary", set.defaultSecondaryColorId);
            if (primary != set.defaultPrimaryColorId || secondary != set.defaultSecondaryColorId)
            {
                RecordWearableUndo(set, "Change Wearable Default Colors");
                set.defaultPrimaryColorId = primary;
                set.defaultSecondaryColorId = secondary;
                _scenePreviewReviewColor = false;
                MarkWearableChanged(set, true);
                ApplySceneDraftToDummy();
            }
        }

        private ushort DrawPaletteCycleRow(string label, ushort current)
        {
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            int count = palette != null ? palette.Length : 0;
            if (count <= 0)
                return current;

            current = (ushort)Mathf.Clamp(current <= 0 ? 1 : current, 1, count);
            ushort result = current;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                if (GUILayout.Button("<", GUILayout.Width(32f)))
                    result = CyclePalette(current, -1, count);

                Rect swatch = GUILayoutUtility.GetRect(34f, 20f, GUILayout.Width(34f), GUILayout.Height(20f));
                EditorGUI.DrawRect(swatch, palette[current - 1]);
                GUILayout.Label($"{current}: {SidekickCharacterPaletteUtility.GetClothingPaletteName(current)}", GUILayout.MinWidth(180f));

                if (GUILayout.Button(">", GUILayout.Width(32f)))
                    result = CyclePalette(current, 1, count);
                GUILayout.FlexibleSpace();
            }
            return result;
        }

        private void DrawSlotGroup(CharacterWearableSetDefinition set, ushort[] slotIds)
        {
            for (int i = 0; i < slotIds.Length; ++i)
            {
                ushort slotId = slotIds[i];
                if (!_profile.TryGetSlot(slotId, out CharacterVisualSlotDefinition slot) || slot == null)
                    continue;

                ushort selectedOptionId = GetWearableOption(set, slotId);
                CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                int selectedIndex = 0;
                for (int o = 0; o < options.Length; ++o)
                    if (options[o] != null && options[o].optionId == selectedOptionId)
                    {
                        selectedIndex = o + 1;
                        break;
                    }

                int choiceCount = options.Length + 1;
                string currentLabel;
                if (selectedIndex == 0)
                {
                    currentLabel = IsOptionalAttachmentSlot(slot.slotId) ? "None" : "Default Base";
                }
                else
                {
                    CharacterVisualOptionDefinition selected = options[selectedIndex - 1];
                    string name = selected != null && !string.IsNullOrWhiteSpace(selected.displayName)
                        ? selected.displayName
                        : selected?.mesh != null ? selected.mesh.name : "Missing Option";
                    currentLabel = selected != null ? $"{name}  [{selected.optionId}]" : name;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(slot.displayName, GUILayout.Width(145f));
                    if (GUILayout.Button("<", GUILayout.Width(32f)))
                    {
                        int next = WrapIndex(selectedIndex - 1, choiceCount);
                        SetWearableOption(set, slot, next == 0 ? (ushort)0 : options[next - 1].optionId);
                    }

                    GUILayout.Label(currentLabel, EditorStyles.helpBox, GUILayout.MinWidth(300f), GUILayout.Height(20f));

                    if (GUILayout.Button(">", GUILayout.Width(32f)))
                    {
                        int next = WrapIndex(selectedIndex + 1, choiceCount);
                        SetWearableOption(set, slot, next == 0 ? (ushort)0 : options[next - 1].optionId);
                    }

                    if (GUILayout.Button("Inspect", GUILayout.Width(70f)))
                    {
                        _sceneFocusedSlotId = slotId;
                        _sceneShowDyeReview = false;
                        RestorePreview();
                        _sceneAuthoringTab = SceneAuthoringTab.Dye;
                    }
                }
            }
        }

        private void DrawFocusedMeshDetails()
        {
            CharacterWearableSetDefinition set = GetCurrentWearableSet();
            if (set == null || !_profile.TryGetSlot(_sceneFocusedSlotId, out CharacterVisualSlotDefinition slot) || slot == null)
                return;

            ushort optionId = GetWearableOption(set, slot.slotId);
            CharacterVisualOptionDefinition option = null;
            if (optionId != 0)
                _profile.TryGetOption(slot.slotId, optionId, out option);

            EditorGUILayout.LabelField("SELECTED MESH DETAILS", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Slot", $"{slot.slotId} — {slot.displayName}");
            if (option == null)
            {
                EditorGUILayout.LabelField("Resolved", slot.allowNone ? "None / no wearable override" : "Canonical Default Base");
                return;
            }

            EditorGUILayout.LabelField("Catalog Option", $"{option.optionId} — {option.displayName}");
            EditorGUILayout.LabelField("Internal Mesh", option.mesh != null ? option.mesh.name : "Missing");
            EditorGUILayout.LabelField("Dye Region", SidekickCharacterPaletteUtility.ClothingDyeRegionLabel(slot.slotId));
            EditorGUILayout.LabelField("Saved Dye Cells", (option.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>()).Length.ToString());

            bool player = option.usageReviewed ? option.playerEligible : true;
            bool population = option.populationEligible;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Mesh Usage");
                bool nextPlayer = GUILayout.Toggle(player, "Player", "Button", GUILayout.Width(90f));
                bool nextPopulation = GUILayout.Toggle(population, "Population", "Button", GUILayout.Width(100f));
                GUILayout.FlexibleSpace();
                GUILayout.Label(option.usageReviewed ? "CONFIRMED" : "UNCONFIRMED", EditorStyles.miniBoldLabel);
                if (nextPlayer != player || nextPopulation != population)
                {
                    Undo.RecordObject(_profile, "Set Visual Mesh Usage");
                    option.usageReviewed = true;
                    option.playerEligible = nextPlayer;
                    option.populationEligible = nextPopulation;
                    MarkSceneAuthoringDirty(false);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan / Review Dye Zones", GUILayout.Width(190f)))
                    BeginInlineDyeReview(slot, option);
                if (_sceneShowDyeReview && GUILayout.Button("Close Dye Review", GUILayout.Width(130f)))
                {
                    RestorePreview();
                    _sceneShowDyeReview = false;
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawWearableColorReview(CharacterWearableSetDefinition set)
        {
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            int count = palette != null ? palette.Length : 0;
            if (set == null || count <= 0)
                return;

            _sceneColorReviewId = (ushort)Mathf.Clamp(_sceneColorReviewId <= 0 ? 1 : _sceneColorReviewId, 1, count);
            ushort currentId = _sceneColorReviewId;
            ushort nextId = currentId;

            bool globalPlayer = IsPaletteAllowedForPlayer(currentId);
            bool globalPopulation = IsPaletteAllowedForPopulation(currentId);
            bool player = IsWearablePaletteAllowedForPlayer(set, currentId);
            bool population = IsWearablePaletteAllowedForPopulation(set, currentId);

            EditorGUILayout.LabelField("WEARABLE COLOR REVIEW", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Cycle colors against this assembled wearable. Player / Population approval is saved per wearable. Unreviewed wearables inherit the global color library.",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                if (GUILayout.Button("<", GUILayout.Width(34f)))
                    nextId = CyclePalette(currentId, -1, count);

                Rect swatch = GUILayoutUtility.GetRect(56f, 36f, GUILayout.Width(56f), GUILayout.Height(36f));
                EditorGUI.DrawRect(swatch, palette[currentId - 1]);

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(235f)))
                {
                    GUILayout.Label(
                        $"{currentId}: {SidekickCharacterPaletteUtility.GetClothingPaletteName(currentId)}",
                        EditorStyles.boldLabel);
                    GUILayout.Label(
                        SidekickCharacterPaletteUtility.GetClothingPaletteFamily(currentId),
                        EditorStyles.miniLabel);
                }

                using (new EditorGUI.DisabledScope(!globalPlayer))
                {
                    bool nextPlayer = GUILayout.Toggle(player, "Player", "Button", GUILayout.Width(82f));
                    if (nextPlayer != player)
                        SetWearablePaletteUsage(set, currentId, nextPlayer, population);
                }

                using (new EditorGUI.DisabledScope(!globalPopulation))
                {
                    bool effectivePlayer = IsWearablePaletteAllowedForPlayer(set, currentId);
                    bool effectivePopulation = IsWearablePaletteAllowedForPopulation(set, currentId);
                    bool nextPopulation = GUILayout.Toggle(effectivePopulation, "Population", "Button", GUILayout.Width(96f));
                    if (nextPopulation != effectivePopulation)
                        SetWearablePaletteUsage(set, currentId, effectivePlayer, nextPopulation);
                }

                if (GUILayout.Button(">", GUILayout.Width(34f)))
                    nextId = CyclePalette(currentId, 1, count);

                GUILayout.FlexibleSpace();
            }

            if (nextId != currentId)
            {
                _sceneColorReviewId = nextId;
                _scenePreviewReviewColor = true;
                ApplySceneDraftToDummy();
                Repaint();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Preview Review Color On");
                int previewChannel = GUILayout.Toolbar(
                    _sceneReviewColorOnSecondary ? 1 : 0,
                    new[] { "Primary", "Secondary" },
                    GUILayout.Width(180f));
                bool nextSecondary = previewChannel == 1;
                if (nextSecondary != _sceneReviewColorOnSecondary)
                {
                    _sceneReviewColorOnSecondary = nextSecondary;
                    _scenePreviewReviewColor = true;
                    ApplySceneDraftToDummy();
                }

                if (GUILayout.Button("Preview Defaults", GUILayout.Width(120f)))
                {
                    _scenePreviewReviewColor = false;
                    ApplySceneDraftToDummy();
                }

                if (GUILayout.Button(_sceneReviewColorOnSecondary ? "Set Default Secondary" : "Set Default Primary", GUILayout.Width(150f)))
                {
                    RecordWearableUndo(set, "Set Wearable Default Color");
                    if (_sceneReviewColorOnSecondary)
                        set.defaultSecondaryColorId = _sceneColorReviewId;
                    else
                        set.defaultPrimaryColorId = _sceneColorReviewId;
                    _scenePreviewReviewColor = false;
                    MarkWearableChanged(set, true);
                    ApplySceneDraftToDummy();
                }

                GUILayout.FlexibleSpace();
            }

            int primaryCells;
            int secondaryCells;
            CountWearableDyeCells(set, out primaryCells, out secondaryCells);
            if (primaryCells == 0 && secondaryCells == 0)
            {
                EditorGUILayout.HelpBox(
                    "This wearable has no saved Primary/Secondary dye zones yet, so palette changes cannot alter the dummy. Select the wearable mesh under PARTS, click Inspect, then use DYE → Scan / Review Dye Zones and confirm its mapping.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"Live dye zones on this wearable: Primary {primaryCells} cell(s), Secondary {secondaryCells} cell(s). Color cycling previews immediately on the dummy.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(
                    set.colorUsageReviewed
                        ? $"Per-wearable color list: REVIEWED ({CountWearableAllowedColors(set, true)} Player / {CountWearableAllowedColors(set, false)} Population)"
                        : "Per-wearable color list: INHERITING GLOBAL",
                    EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!set.colorUsageReviewed))
                {
                    if (GUILayout.Button("Reset To Global", GUILayout.Width(110f)))
                    {
                        RecordWearableUndo(set, "Reset Wearable Color Usage");
                        set.colorUsageReviewed = false;
                        set.playerAllowedColorIds = Array.Empty<ushort>();
                        set.populationAllowedColorIds = Array.Empty<ushort>();
                        MarkWearableChanged(set, false);
                    }
                }
            }

            if (!globalPlayer || !globalPopulation)
            {
                EditorGUILayout.LabelField(
                    $"Global library for this color: Player {(globalPlayer ? "Allowed" : "Off")} / Population {(globalPopulation ? "Allowed" : "Off")}. A wearable cannot re-enable a color disabled globally.",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawGlobalColorLibraryReview()
        {
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            int count = palette != null ? palette.Length : 0;
            if (count <= 0)
                return;

            _sceneColorReviewId = (ushort)Mathf.Clamp(_sceneColorReviewId <= 0 ? 1 : _sceneColorReviewId, 1, count);
            ushort id = _sceneColorReviewId;
            bool player = IsPaletteAllowedForPlayer(id);
            bool population = IsPaletteAllowedForPopulation(id);

            EditorGUILayout.LabelField("COLOR LIBRARY REVIEW", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Cycle the stable color library and mark whether each color may be offered to Players, Population, both, or neither. This is client content metadata; it adds no network message.",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                if (GUILayout.Button("<", GUILayout.Width(34f)))
                    _sceneColorReviewId = CyclePalette(id, -1, count);

                Rect swatch = GUILayoutUtility.GetRect(54f, 34f, GUILayout.Width(54f), GUILayout.Height(34f));
                EditorGUI.DrawRect(swatch, palette[id - 1]);
                GUILayout.Label($"{id}: {SidekickCharacterPaletteUtility.GetClothingPaletteName(id)}", EditorStyles.boldLabel, GUILayout.Width(180f));
                GUILayout.Label(SidekickCharacterPaletteUtility.GetClothingPaletteFamily(id), GUILayout.Width(110f));

                bool nextPlayer = GUILayout.Toggle(player, "Player", "Button", GUILayout.Width(85f));
                bool nextPopulation = GUILayout.Toggle(population, "Population", "Button", GUILayout.Width(100f));

                if (GUILayout.Button(">", GUILayout.Width(34f)))
                    _sceneColorReviewId = CyclePalette(id, 1, count);
                GUILayout.FlexibleSpace();

                if (nextPlayer != player || nextPopulation != population)
                    SetPaletteUsage(id, nextPlayer, nextPopulation);
            }
        }

        private void DrawInlineDyeReview()
        {
            if (!_sceneShowDyeReview || _resolvedOption == null)
                return;

            EditorGUILayout.LabelField("DYE ZONE REVIEW", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This scanner runs only when explicitly requested. Skin-shared cells stay locked to Ignore. Assign Primary / Secondary / Accent only where the selected mesh visibly needs dye.",
                MessageType.None);

            DrawDyePreviewControls();
            if (!string.IsNullOrWhiteSpace(_scanReport))
                EditorGUILayout.HelpBox(_scanReport, MessageType.None);

            for (int i = 0; i < _candidates.Count; ++i)
                DrawCandidate(_candidates[i]);

            DrawSceneDyeConfirmationControls();
        }

        private void DrawSceneDyeConfirmationControls()
        {
            if (_resolvedOption == null || _resolvedSlot == null || !IsDyeReviewApplicable(_resolvedSlot))
                return;

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(_resolvedOption.paletteReviewConfirmed ? "Dye Review: CONFIRMED" : "Dye Review: NEEDS CONFIRMATION", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Confirm Mapping", GUILayout.Width(125f)))
                    ConfirmCurrentPaletteReview(false, false);
                if (GUILayout.Button("Not Dyeable", GUILayout.Width(105f)))
                    ConfirmCurrentPaletteReview(true, false);
            }
        }

        private void BeginInlineDyeReview(CharacterVisualSlotDefinition slot, CharacterVisualOptionDefinition option)
        {
            RestorePreview();
            _sceneShowDyeReview = false;
            if (slot == null || option == null || option.mesh == null)
                return;

            // In scene-authoring mode, review the renderer that is already assembled on the
            // canonical dummy. Do not create the old detached temporary FBX preview.
            SkinnedMeshRenderer renderer = FindSceneDummyRenderer(slot, option);
            if (renderer == null)
            {
                _scanReport = $"Could not resolve '{option.displayName}' on the authoring dummy. Rebuild the dummy and confirm this mesh is selected for the slot.";
                return;
            }

            SetRenderer(renderer);
            Scan();
            _sceneShowDyeReview = true;
            Selection.activeGameObject = _sceneAuthoringDummy;
            SceneView.RepaintAll();
        }

        private SkinnedMeshRenderer FindSceneDummyRenderer(CharacterVisualSlotDefinition slot, CharacterVisualOptionDefinition option)
        {
            if (_sceneAuthoringDummy == null || slot == null || option == null || option.mesh == null)
                return null;

            SkinnedMeshRenderer[] renderers = _sceneAuthoringDummy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer firstMeshMatch = null;
            string slotIdToken = slot.slotId.ToString("00");
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer candidate = renderers[i];
                if (candidate == null || candidate.sharedMesh != option.mesh)
                    continue;

                if (firstMeshMatch == null)
                    firstMeshMatch = candidate;

                string objectName = candidate.gameObject.name ?? string.Empty;
                if (objectName.IndexOf(slot.slotCode ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    objectName.IndexOf(slotIdToken, StringComparison.OrdinalIgnoreCase) >= 0)
                    return candidate;
            }

            return firstMeshMatch;
        }

        private void OpenOrCreateAuthoringScene()
        {
            if (_profile == null)
                return;

            RestorePreview();
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isDirty && active.path != AuthoringScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;
            }

            SceneAsset existing = AssetDatabase.LoadAssetAtPath<SceneAsset>(AuthoringScenePath);
            Scene scene;
            if (existing != null)
            {
                scene = EditorSceneManager.OpenScene(AuthoringScenePath, OpenSceneMode.Single);
            }
            else
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                CreateAuthoringSceneEnvironment();
                EditorSceneManager.SaveScene(scene, AuthoringScenePath);
            }

            RebuildSceneDummy();
            FrameSceneDummy();
        }

        private static void CreateAuthoringSceneEnvironment()
        {
            var lightObject = new GameObject("Authoring Key Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);

            var marker = new GameObject("Authoring Origin");
            marker.transform.position = Vector3.zero;
        }

        private void RebuildSceneDummy()
        {
            if (_profile == null || _profile.EditableBasePrefab == null)
                return;

            RestorePreview();
            DestroyOrphanedPalettePreviewObjects();

            if (_sceneAuthoringDummy != null)
                DestroyImmediate(_sceneAuthoringDummy);

            GameObject existing = GameObject.Find(DummyName);
            if (existing != null)
                DestroyImmediate(existing);

            GameObject instance = PrefabUtility.InstantiatePrefab(_profile.EditableBasePrefab) as GameObject;
            if (instance == null)
                instance = Instantiate(_profile.EditableBasePrefab);
            if (instance == null)
                return;

            instance.name = DummyName;
            instance.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Animator animator = instance.GetComponentInChildren<Animator>(true);
            if (animator != null)
                animator.enabled = false;

            _sceneAuthoringDummy = instance;
            _sceneAuthoringPresenter = instance.GetComponent<ModularCharacterAppearancePresenter>();
            if (_sceneAuthoringPresenter == null)
                _sceneAuthoringPresenter = instance.AddComponent<ModularCharacterAppearancePresenter>();
            _sceneAuthoringPresenter.Configure(_profile);
            ApplySceneDraftToDummy();
        }

        private void FrameSceneDummy()
        {
            if (_sceneAuthoringDummy == null)
                _sceneAuthoringDummy = GameObject.Find(DummyName);
            if (_sceneAuthoringDummy == null)
                return;

            Selection.activeGameObject = _sceneAuthoringDummy;
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                view.FrameSelected();
                view.Repaint();
            }
        }

        private static void DestroyOrphanedPalettePreviewObjects()
        {
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = roots.Length - 1; i >= 0; --i)
            {
                GameObject root = roots[i];
                if (root == null)
                    continue;
                if (root.name.EndsWith("[Palette Preview - Unsaved]", StringComparison.Ordinal))
                    DestroyImmediate(root);
            }
        }

        private void ApplySceneDraftToDummy()
        {
            if (_profile == null)
                return;

            // Editor script/domain reloads clear the window's non-serialized references while
            // the authoring-scene dummy may still be alive. Rebind quietly instead of making
            // color/part controls appear to do nothing until the user manually rebuilds.
            if (_sceneAuthoringDummy == null)
                _sceneAuthoringDummy = GameObject.Find(DummyName);
            if (_sceneAuthoringPresenter == null && _sceneAuthoringDummy != null)
                _sceneAuthoringPresenter = _sceneAuthoringDummy.GetComponent<ModularCharacterAppearancePresenter>();
            if (_sceneAuthoringPresenter == null)
                return;

            CharacterAppearanceRecipe recipe = _profile.CreateDefaultRecipe();
            CharacterWearableSetDefinition set = GetCurrentWearableSet();
            if (set != null)
            {
                CharacterWearablePartSelection[] parts = set.parts ?? Array.Empty<CharacterWearablePartSelection>();
                for (int i = 0; i < parts.Length; ++i)
                {
                    CharacterWearablePartSelection part = parts[i];
                    if (part.slotId == 0 || part.optionId == 0)
                        continue;
                    SetRecipeMesh(recipe, part.slotId, part.optionId);
                }

                GetRegionColorChannels(set.region, out ushort primaryChannel, out ushort secondaryChannel);
                ushort primaryColorId = set.defaultPrimaryColorId;
                ushort secondaryColorId = set.defaultSecondaryColorId;
                if (_sceneAuthoringTab == SceneAuthoringTab.Colors && _scenePreviewReviewColor)
                {
                    if (_sceneReviewColorOnSecondary)
                        secondaryColorId = _sceneColorReviewId;
                    else
                        primaryColorId = _sceneColorReviewId;
                }

                SetRecipeColor(recipe, primaryChannel, ClampPaletteId(primaryColorId));
                SetRecipeColor(recipe, secondaryChannel, ClampPaletteId(secondaryColorId));
            }

            _sceneAuthoringPresenter.Configure(_profile);
            _sceneAuthoringPresenter.ApplyAppearance(recipe);
            SceneView.RepaintAll();
        }

        private CharacterWearableSetDefinition GetCurrentWearableSet()
        {
            if (_profile == null)
                return null;

            if (_sceneCandidateMode)
            {
                if (_sceneWearableCandidate == null)
                    _sceneWearableCandidate = CreateBlankWearableCandidate();
                return _sceneWearableCandidate;
            }

            IReadOnlyList<CharacterWearableSetDefinition> sets = _profile.WearableSets;
            if (sets == null || sets.Count == 0)
            {
                _sceneCandidateMode = true;
                _sceneWearableIndex = -1;
                _sceneWearableCandidate = CreateBlankWearableCandidate();
                return _sceneWearableCandidate;
            }

            if (_sceneWearableIndex < 0 || _sceneWearableIndex >= sets.Count)
                _sceneWearableIndex = 0;
            return sets[_sceneWearableIndex];
        }

        private CharacterWearableSetDefinition CreateBlankWearableCandidate()
        {
            IReadOnlyList<CharacterWearableSetDefinition> values = _profile != null
                ? _profile.WearableSets
                : Array.Empty<CharacterWearableSetDefinition>();
            return new CharacterWearableSetDefinition
            {
                definitionId = NextWearableDefinitionId(values),
                displayName = "Wearable",
                presentationId = 0,
                region = CharacterWearableRegion.Upper,
                playerEligible = true,
                populationEligible = true,
                defaultPrimaryColorId = 1,
                defaultSecondaryColorId = 8,
                colorUsageReviewed = false,
                playerAllowedColorIds = Array.Empty<ushort>(),
                populationAllowedColorIds = Array.Empty<ushort>(),
                parts = Array.Empty<CharacterWearablePartSelection>(),
                icon = null,
            };
        }

        private static CharacterWearableSetDefinition CloneWearableSet(CharacterWearableSetDefinition source)
        {
            if (source == null)
                return null;
            return new CharacterWearableSetDefinition
            {
                definitionId = source.definitionId,
                displayName = source.displayName,
                presentationId = source.presentationId,
                region = source.region,
                playerEligible = source.playerEligible,
                populationEligible = source.populationEligible,
                defaultPrimaryColorId = source.defaultPrimaryColorId,
                defaultSecondaryColorId = source.defaultSecondaryColorId,
                colorUsageReviewed = source.colorUsageReviewed,
                playerAllowedColorIds = source.playerAllowedColorIds != null ? (ushort[])source.playerAllowedColorIds.Clone() : Array.Empty<ushort>(),
                populationAllowedColorIds = source.populationAllowedColorIds != null ? (ushort[])source.populationAllowedColorIds.Clone() : Array.Empty<ushort>(),
                parts = source.parts != null ? (CharacterWearablePartSelection[])source.parts.Clone() : Array.Empty<CharacterWearablePartSelection>(),
                icon = source.icon,
            };
        }

        private void BeginFreshWearableCandidate()
        {
            RestorePreview();
            _sceneShowDyeReview = false;
            _sceneCandidateMode = true;
            _sceneWearableIndex = -1;
            _sceneWearableCandidate = CreateBlankWearableCandidate();
            _sceneCandidateDirty = false;
            ApplySceneDraftToDummy();
            Repaint();
        }

        private void BeginWearableCandidateFrom(CharacterWearableSetDefinition source)
        {
            RestorePreview();
            _sceneShowDyeReview = false;
            _sceneCandidateMode = true;
            _sceneWearableIndex = -1;
            _sceneWearableCandidate = CloneWearableSet(source) ?? CreateBlankWearableCandidate();
            _sceneWearableCandidate.definitionId = NextWearableDefinitionId(_profile.WearableSets);
            _sceneWearableCandidate.presentationId = 0;
            _sceneCandidateDirty = false;
            ApplySceneDraftToDummy();
            Repaint();
        }

        private void SelectSavedWearable(int index)
        {
            IReadOnlyList<CharacterWearableSetDefinition> sets = _profile != null ? _profile.WearableSets : null;
            if (sets == null || sets.Count == 0)
                return;
            _sceneCandidateMode = false;
            _sceneCandidateDirty = false;
            _sceneWearableCandidate = null;
            _sceneWearableIndex = Mathf.Clamp(index, 0, sets.Count - 1);
            OnWearableSelectionChanged();
        }

        private void SaveWearableCandidate(bool allowDuplicateVisual)
        {
            if (_profile == null || !_sceneCandidateMode)
                return;

            CharacterWearableSetDefinition candidate = GetCurrentWearableSet();
            if (candidate == null)
                return;

            if (!ValidateSavedWearableIdentity(out string existingValidationError))
            {
                EditorUtility.DisplayDialog("Wearable Not Saved", existingValidationError, "OK");
                return;
            }

            string definitionId = (candidate.definitionId ?? string.Empty).Trim();
            if (definitionId.Length == 0)
            {
                EditorUtility.DisplayDialog("Wearable Not Saved", "Internal Definition ID is required.", "OK");
                return;
            }

            int duplicateDefinition = FindWearableByDefinitionId(definitionId, -1);
            if (duplicateDefinition >= 0)
            {
                EditorUtility.DisplayDialog(
                    "Wearable Not Saved",
                    $"Definition ID '{definitionId}' is already used by '{SafeWearableName(_profile.WearableSets[duplicateDefinition])}'. Definition IDs must be unique.",
                    "OK");
                return;
            }

            if (candidate.presentationId != 0)
            {
                int duplicatePresentation = FindWearableByPresentationId(candidate.presentationId, -1);
                if (duplicatePresentation >= 0)
                {
                    EditorUtility.DisplayDialog(
                        "Wearable Not Saved",
                        $"Presentation ID {candidate.presentationId} is already used by '{SafeWearableName(_profile.WearableSets[duplicatePresentation])}'.",
                        "OK");
                    return;
                }
            }

            int visualMatch = FindWearableByVisualSignature(candidate, -1);
            if (visualMatch >= 0 && !allowDuplicateVisual)
            {
                CharacterWearableSetDefinition match = _profile.WearableSets[visualMatch];
                if (EditorUtility.DisplayDialog(
                    "Visual Set Already Exists",
                    $"This exact mesh composition is already saved as '{SafeWearableName(match)}' ({match.definitionId}).",
                    "Edit Existing",
                    "Cancel"))
                    SelectSavedWearable(visualMatch);
                return;
            }

            if (visualMatch >= 0 && allowDuplicateVisual &&
                !EditorUtility.DisplayDialog(
                    "Create Intentional Variant?",
                    "The same mesh composition already exists. Create another wearable anyway for intentionally different defaults, colors, naming, or gameplay presentation?",
                    "Create Variant",
                    "Cancel"))
                return;

            var values = new List<CharacterWearableSetDefinition>(_profile.WearableSets ?? Array.Empty<CharacterWearableSetDefinition>());
            CharacterWearableSetDefinition saved = CloneWearableSet(candidate);
            saved.definitionId = definitionId;

            Undo.RecordObject(_profile, "Save Wearable Candidate");
            values.Add(saved);
            _profile.SetWearableSets(values.ToArray());
            MarkSceneAuthoringDirty(true);
            FlushSceneAuthoringChanges();

            // Keep the just-authored composition as the next transient candidate. The common
            // workflow is now Save -> click a part's > arrow -> review the next garment, with
            // no repeated New button or anonymous profile entry.
            BeginWearableCandidateFrom(saved);
        }

        private void DeleteWearableSet()
        {
            if (_sceneCandidateMode)
                return;

            CharacterWearableSetDefinition set = GetCurrentWearableSet();
            if (_profile == null || set == null)
                return;
            if (!EditorUtility.DisplayDialog("Delete Wearable Set", $"Delete '{set.displayName}'?", "Delete", "Cancel"))
                return;

            var values = new List<CharacterWearableSetDefinition>(_profile.WearableSets);
            Undo.RecordObject(_profile, "Delete Wearable Set");
            values.RemoveAt(_sceneWearableIndex);
            _profile.SetWearableSets(values.ToArray());
            MarkSceneAuthoringDirty(true);
            BeginFreshWearableCandidate();
        }

        private void OnWearableSelectionChanged()
        {
            RestorePreview();
            _sceneShowDyeReview = false;
            ApplySceneDraftToDummy();
        }

        private ushort GetWearableOption(CharacterWearableSetDefinition set, ushort slotId)
        {
            if (set?.parts == null)
                return 0;
            for (int i = 0; i < set.parts.Length; ++i)
                if (set.parts[i].slotId == slotId)
                    return set.parts[i].optionId;
            return 0;
        }

        private void SetWearableOption(CharacterWearableSetDefinition set, CharacterVisualSlotDefinition slot, ushort optionId)
        {
            if (_profile == null || set == null || slot == null)
                return;

            RecordWearableUndo(set, "Set Wearable Mesh Part");
            var parts = new List<CharacterWearablePartSelection>(set.parts ?? Array.Empty<CharacterWearablePartSelection>());
            for (int i = parts.Count - 1; i >= 0; --i)
                if (parts[i].slotId == slot.slotId)
                    parts.RemoveAt(i);
            if (optionId != 0)
                parts.Add(new CharacterWearablePartSelection(slot.slotId, optionId));
            parts.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            set.parts = parts.ToArray();
            _sceneFocusedSlotId = slot.slotId;
            RestorePreview();
            _sceneShowDyeReview = false;
            MarkWearableChanged(set, true);
            ApplySceneDraftToDummy();
        }

        private bool IsTransientWearableCandidate(CharacterWearableSetDefinition set)
        {
            return _sceneCandidateMode && set != null && object.ReferenceEquals(set, _sceneWearableCandidate);
        }

        private void RecordWearableUndo(CharacterWearableSetDefinition set, string label)
        {
            if (_profile != null && !IsTransientWearableCandidate(set))
                Undo.RecordObject(_profile, label);
        }

        private void MarkWearableChanged(CharacterWearableSetDefinition set, bool repaintScene)
        {
            if (IsTransientWearableCandidate(set))
            {
                _sceneCandidateDirty = true;
                if (repaintScene)
                    SceneView.RepaintAll();
                Repaint();
                return;
            }

            MarkSceneAuthoringDirty(repaintScene);
        }

        private void MarkSceneAuthoringDirty(bool repaintScene)
        {
            if (_profile == null)
                return;
            EditorUtility.SetDirty(_profile);
            _sceneAuthoringDirty = true;
            if (repaintScene)
                SceneView.RepaintAll();
            Repaint();
        }

        private void FlushSceneAuthoringChanges()
        {
            if (_profile == null)
                return;

            if (!ValidateSavedWearableIdentity(out string validationError))
            {
                EditorUtility.DisplayDialog("Wearable Profile Not Saved", validationError, "OK");
                return;
            }

            EditorUtility.SetDirty(_profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();
            _sceneAuthoringDirty = false;
            Repaint();
        }

        private bool ValidateSavedWearableIdentity(out string error)
        {
            error = string.Empty;
            if (_profile == null)
                return true;

            IReadOnlyList<CharacterWearableSetDefinition> sets = _profile.WearableSets;
            var definitions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var presentationIds = new Dictionary<ushort, int>();
            for (int i = 0; i < sets.Count; ++i)
            {
                CharacterWearableSetDefinition set = sets[i];
                if (set == null)
                    continue;

                string definitionId = (set.definitionId ?? string.Empty).Trim();
                if (definitionId.Length == 0)
                {
                    error = $"Saved wearable #{i + 1} ('{SafeWearableName(set)}') has no Internal Definition ID.";
                    return false;
                }

                if (definitions.TryGetValue(definitionId, out int firstDefinitionIndex))
                {
                    error = $"Duplicate Internal Definition ID '{definitionId}' is used by saved wearables #{firstDefinitionIndex + 1} and #{i + 1}.";
                    return false;
                }
                definitions.Add(definitionId, i);

                if (set.presentationId != 0)
                {
                    if (presentationIds.TryGetValue(set.presentationId, out int firstPresentationIndex))
                    {
                        error = $"Duplicate Presentation ID {set.presentationId} is used by '{SafeWearableName(sets[firstPresentationIndex])}' and '{SafeWearableName(set)}'.";
                        return false;
                    }
                    presentationIds.Add(set.presentationId, i);
                }
            }

            return true;
        }

        private bool IsWearablePaletteAllowedForPlayer(CharacterWearableSetDefinition set, ushort id)
        {
            if (set == null || !IsPaletteAllowedForPlayer(id))
                return false;
            if (!set.colorUsageReviewed)
                return true;
            return ContainsPaletteId(set.playerAllowedColorIds, id);
        }

        private bool IsWearablePaletteAllowedForPopulation(CharacterWearableSetDefinition set, ushort id)
        {
            if (set == null || !IsPaletteAllowedForPopulation(id))
                return false;
            if (!set.colorUsageReviewed)
                return true;
            return ContainsPaletteId(set.populationAllowedColorIds, id);
        }

        private void SetWearablePaletteUsage(
            CharacterWearableSetDefinition set,
            ushort id,
            bool player,
            bool population)
        {
            if (_profile == null || set == null || id == 0)
                return;

            HashSet<ushort> playerIds = BuildWearablePaletteSet(set, true);
            HashSet<ushort> populationIds = BuildWearablePaletteSet(set, false);

            if (player && IsPaletteAllowedForPlayer(id))
                playerIds.Add(id);
            else
                playerIds.Remove(id);

            if (population && IsPaletteAllowedForPopulation(id))
                populationIds.Add(id);
            else
                populationIds.Remove(id);

            RecordWearableUndo(set, "Set Wearable Color Usage");
            set.colorUsageReviewed = true;
            set.playerAllowedColorIds = SortedPaletteArray(playerIds);
            set.populationAllowedColorIds = SortedPaletteArray(populationIds);
            MarkWearableChanged(set, false);
        }

        private HashSet<ushort> BuildWearablePaletteSet(CharacterWearableSetDefinition set, bool player)
        {
            var result = new HashSet<ushort>();
            if (set == null)
                return result;

            if (set.colorUsageReviewed)
            {
                IReadOnlyList<ushort> saved = player
                    ? (IReadOnlyList<ushort>)(set.playerAllowedColorIds ?? Array.Empty<ushort>())
                    : (IReadOnlyList<ushort>)(set.populationAllowedColorIds ?? Array.Empty<ushort>());
                for (int i = 0; i < saved.Count; ++i)
                    result.Add(saved[i]);
                return result;
            }

            int count = SidekickCharacterPaletteUtility.ClothingPalette?.Length ?? 0;
            for (ushort id = 1; id <= count; ++id)
            {
                if (player ? IsPaletteAllowedForPlayer(id) : IsPaletteAllowedForPopulation(id))
                    result.Add(id);
            }
            return result;
        }

        private int CountWearableAllowedColors(CharacterWearableSetDefinition set, bool player)
        {
            int count = SidekickCharacterPaletteUtility.ClothingPalette?.Length ?? 0;
            int allowed = 0;
            for (ushort id = 1; id <= count; ++id)
            {
                if (player
                    ? IsWearablePaletteAllowedForPlayer(set, id)
                    : IsWearablePaletteAllowedForPopulation(set, id))
                    allowed++;
            }
            return allowed;
        }

        private void CountWearableDyeCells(
            CharacterWearableSetDefinition set,
            out int primaryCells,
            out int secondaryCells)
        {
            primaryCells = 0;
            secondaryCells = 0;
            if (_profile == null || set?.parts == null)
                return;

            for (int i = 0; i < set.parts.Length; ++i)
            {
                CharacterWearablePartSelection part = set.parts[i];
                if (part.slotId == 0 || part.optionId == 0 ||
                    !_profile.TryGetOption(part.slotId, part.optionId, out CharacterVisualOptionDefinition option) ||
                    option == null)
                    continue;

                CharacterVisualPaletteCellDefinition[] cells =
                    option.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>();
                for (int c = 0; c < cells.Length; ++c)
                {
                    if (!cells[c].IsValid)
                        continue;
                    if (cells[c].channel == CharacterVisualPaletteChannel.Primary)
                        primaryCells++;
                    else if (cells[c].channel == CharacterVisualPaletteChannel.Secondary)
                        secondaryCells++;
                }
            }
        }

        private bool IsPaletteAllowedForPlayer(ushort id)
        {
            if (_profile == null)
                return false;
            if (!_profile.PlayerClothingColorPoolReviewed)
                return true;
            return ContainsPaletteId(_profile.PlayerClothingPaletteIds, id);
        }

        private bool IsPaletteAllowedForPopulation(ushort id)
        {
            if (_profile == null)
                return false;
            if (!_profile.PopulationClothingColorPoolReviewed)
                return SidekickCharacterPaletteUtility.IsDefaultPopulationClothingPaletteId(id);
            return ContainsPaletteId(_profile.PopulationClothingPaletteIds, id);
        }

        private void SetPaletteUsage(ushort id, bool player, bool population)
        {
            if (_profile == null)
                return;

            var playerIds = BuildPaletteSet(_profile.PlayerClothingColorPoolReviewed, _profile.PlayerClothingPaletteIds, true);
            var populationIds = BuildPaletteSet(_profile.PopulationClothingColorPoolReviewed, _profile.PopulationClothingPaletteIds, false);
            if (player) playerIds.Add(id); else playerIds.Remove(id);
            if (population) populationIds.Add(id); else populationIds.Remove(id);

            Undo.RecordObject(_profile, "Set Clothing Color Usage");
            _profile.SetPlayerClothingColorPool(true, SortedPaletteArray(playerIds));
            _profile.SetPopulationClothingColorPool(true, SortedPaletteArray(populationIds));
            MarkSceneAuthoringDirty(false);
        }

        private HashSet<ushort> BuildPaletteSet(bool reviewed, IReadOnlyList<ushort> saved, bool player)
        {
            var result = new HashSet<ushort>();
            if (reviewed)
            {
                if (saved != null)
                    for (int i = 0; i < saved.Count; ++i)
                        result.Add(saved[i]);
                return result;
            }

            if (player)
            {
                int count = SidekickCharacterPaletteUtility.ClothingPalette?.Length ?? 0;
                for (ushort id = 1; id <= count; ++id)
                    result.Add(id);
            }
            else
            {
                ushort[] defaults = SidekickCharacterPaletteUtility.GetDefaultPopulationClothingPaletteIds();
                for (int i = 0; i < defaults.Length; ++i)
                    result.Add(defaults[i]);
            }
            return result;
        }

        private static ushort[] SortedPaletteArray(HashSet<ushort> ids)
        {
            var values = new List<ushort>(ids ?? new HashSet<ushort>());
            values.Sort();
            return values.ToArray();
        }

        private static bool ContainsPaletteId(IReadOnlyList<ushort> ids, ushort id)
        {
            if (ids == null)
                return false;
            for (int i = 0; i < ids.Count; ++i)
                if (ids[i] == id)
                    return true;
            return false;
        }

        private static ushort CyclePalette(ushort current, int delta, int count)
        {
            if (count <= 0)
                return 0;
            int index = Mathf.Clamp(current <= 0 ? 1 : current, 1, count) - 1;
            index = WrapIndex(index + delta, count);
            return (ushort)(index + 1);
        }

        private static uint ClampPaletteId(ushort id)
        {
            int count = SidekickCharacterPaletteUtility.ClothingPalette?.Length ?? 0;
            if (count <= 0)
                return 0;
            return (uint)Mathf.Clamp(id <= 0 ? 1 : id, 1, count);
        }

        private static int WrapIndex(int value, int count)
        {
            if (count <= 0)
                return 0;
            value %= count;
            if (value < 0)
                value += count;
            return value;
        }

        private static bool IsOptionalAttachmentSlot(ushort slotId)
        {
            return slotId == 22 || slotId == 23 || slotId == 24 || slotId == 29 || slotId == 30;
        }

        private static void GetRegionColorChannels(CharacterWearableRegion region, out ushort primary, out ushort secondary)
        {
            switch (region)
            {
                case CharacterWearableRegion.Lower:
                    primary = SidekickCharacterPaletteUtility.LowerPrimaryColorChannelId;
                    secondary = SidekickCharacterPaletteUtility.LowerSecondaryColorChannelId;
                    return;
                case CharacterWearableRegion.Feet:
                    primary = SidekickCharacterPaletteUtility.FeetPrimaryColorChannelId;
                    secondary = SidekickCharacterPaletteUtility.FeetSecondaryColorChannelId;
                    return;
                case CharacterWearableRegion.Accessory:
                    primary = SidekickCharacterPaletteUtility.AccessoryPrimaryColorChannelId;
                    secondary = SidekickCharacterPaletteUtility.AccessorySecondaryColorChannelId;
                    return;
                default:
                    primary = SidekickCharacterPaletteUtility.UpperPrimaryColorChannelId;
                    secondary = SidekickCharacterPaletteUtility.UpperSecondaryColorChannelId;
                    return;
            }
        }

        private int FindWearableByVisualSignature(CharacterWearableSetDefinition candidate, int ignoreIndex)
        {
            if (_profile == null || candidate == null)
                return -1;

            IReadOnlyList<CharacterWearableSetDefinition> sets = _profile.WearableSets;
            if (sets == null)
                return -1;

            for (int i = 0; i < sets.Count; ++i)
            {
                if (i == ignoreIndex || sets[i] == null)
                    continue;
                if (HaveSameWearableParts(candidate, sets[i]))
                    return i;
            }
            return -1;
        }

        private int FindWearableByDefinitionId(string definitionId, int ignoreIndex)
        {
            if (_profile == null || string.IsNullOrWhiteSpace(definitionId))
                return -1;

            IReadOnlyList<CharacterWearableSetDefinition> sets = _profile.WearableSets;
            if (sets == null)
                return -1;

            string target = definitionId.Trim();
            for (int i = 0; i < sets.Count; ++i)
            {
                if (i == ignoreIndex || sets[i] == null)
                    continue;
                if (string.Equals((sets[i].definitionId ?? string.Empty).Trim(), target, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private int FindWearableByPresentationId(ushort presentationId, int ignoreIndex)
        {
            if (_profile == null || presentationId == 0)
                return -1;

            IReadOnlyList<CharacterWearableSetDefinition> sets = _profile.WearableSets;
            if (sets == null)
                return -1;

            for (int i = 0; i < sets.Count; ++i)
            {
                if (i == ignoreIndex || sets[i] == null)
                    continue;
                if (sets[i].presentationId == presentationId)
                    return i;
            }
            return -1;
        }

        private static bool HaveSameWearableParts(CharacterWearableSetDefinition a, CharacterWearableSetDefinition b)
        {
            if (a == null || b == null)
                return false;

            CharacterWearablePartSelection[] aParts = a.parts ?? Array.Empty<CharacterWearablePartSelection>();
            CharacterWearablePartSelection[] bParts = b.parts ?? Array.Empty<CharacterWearablePartSelection>();

            int aCount = CountExplicitWearableParts(aParts);
            int bCount = CountExplicitWearableParts(bParts);
            if (aCount != bCount)
                return false;

            // Wearable parts are normally stored sorted by slot id, but compare by semantic
            // slot/option pairs so old or hand-edited profile data is still matched correctly.
            for (int i = 0; i < aParts.Length; ++i)
            {
                CharacterWearablePartSelection part = aParts[i];
                if (part.slotId == 0 || part.optionId == 0)
                    continue;

                bool found = false;
                for (int j = 0; j < bParts.Length; ++j)
                {
                    CharacterWearablePartSelection other = bParts[j];
                    if (other.slotId == part.slotId && other.optionId == part.optionId)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }

            return true;
        }

        private static int CountExplicitWearableParts(CharacterWearablePartSelection[] parts)
        {
            int count = 0;
            if (parts == null)
                return 0;
            for (int i = 0; i < parts.Length; ++i)
                if (parts[i].slotId != 0 && parts[i].optionId != 0)
                    count++;
            return count;
        }

        private static string NextWearableDefinitionId(IReadOnlyList<CharacterWearableSetDefinition> values)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
                for (int i = 0; i < values.Count; ++i)
                    if (values[i] != null && !string.IsNullOrWhiteSpace(values[i].definitionId))
                        used.Add(values[i].definitionId);

            for (int i = 1; i < 100000; ++i)
            {
                string candidate = $"wearable.new.{i:000}";
                if (!used.Contains(candidate))
                    return candidate;
            }
            return "wearable.new";
        }
    }
}
#endif
