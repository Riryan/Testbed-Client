#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Game.Client.Presentation.Characters;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Client.Editor
{
    /// <summary>
    /// Editor-only Sidekick palette authoring for the existing CharacterVisualProfile.
    /// It ports the proven uMMORPG UV0 + skin-mask discovery workflow without adding
    /// runtime/network state. Saved cells live on the existing visual option definition.
    /// </summary>
    public sealed partial class CharacterVisualPaletteAuthoringWindow : EditorWindow
    {
        private sealed class PaletteCellCandidate
        {
            public int colorMapWidth;
            public int colorMapHeight;
            public int cellX;
            public int cellY;
            public int totalReferences;
            public int nonSkinReferences;
            public int skinReferences;
            public bool hasUsableSkinMask;
            public bool isSuggested;
            public Color sourceColor;
        }

        private enum VisualUsageChoice : byte
        {
            None = 0,
            PlayerOnly = 1,
            PopulationOnly = 2,
            PlayerAndPopulation = 3,
        }

        private enum AuthoringTab : byte
        {
            ReviewQueue = 0,
            PopulationColors = 1,
            ScannerDebug = 2,
        }

        private enum ReviewFilter : byte
        {
            All = 0,
            NeedsReview = 1,
            Confirmed = 2,
            Player = 3,
            Population = 4,
            Both = 5,
            None = 6,
            Stale = 7,
        }

        private enum ReviewScope : byte
        {
            All = 0,
            HairBrowsBeards = 1,
            Clothing = 2,
            Other = 3,
        }

        private sealed class BatchVisualEntry
        {
            public CharacterVisualSlotDefinition slot;
            public CharacterVisualOptionDefinition option;
            public string assetPath;
        }

        private sealed class BatchVisualGroup
        {
            public string key;
            public string displayName;
            public readonly List<BatchVisualEntry> entries = new List<BatchVisualEntry>();
            public bool expanded = true;
        }

        private readonly struct PaletteCellKey : IEquatable<PaletteCellKey>
        {
            public readonly int width;
            public readonly int height;
            public readonly int x;
            public readonly int y;

            public PaletteCellKey(int width, int height, int x, int y)
            {
                this.width = width;
                this.height = height;
                this.x = x;
                this.y = y;
            }

            public bool Equals(PaletteCellKey other) =>
                width == other.width && height == other.height && x == other.x && y == other.y;

            public override bool Equals(object obj) => obj is PaletteCellKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = width;
                    hash = (hash * 397) ^ height;
                    hash = (hash * 397) ^ x;
                    hash = (hash * 397) ^ y;
                    return hash;
                }
            }
        }

        private static readonly Regex SlotRegex =
            new Regex(@"_(\d{2})[A-Z]{3,5}_", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly int ColorMapId = Shader.PropertyToID("_ColorMap");
        private static readonly int SkinMaskId = Shader.PropertyToID("_SkinMaskTexture");
        private static readonly int PackedMaskId = Shader.PropertyToID("_PackedMaskMap");
        private static readonly int UsePackedMasksId = Shader.PropertyToID("_UsePackedMasks");

        private CharacterVisualProfile _profile;
        private SkinnedMeshRenderer _renderer;
        private int _materialIndex;
        private CharacterVisualSlotDefinition _resolvedSlot;
        private CharacterVisualOptionDefinition _resolvedOption;
        private readonly List<PaletteCellCandidate> _candidates = new List<PaletteCellCandidate>();
        private Vector2 _windowScroll;
        private string _scanReport = "Select a modular humanoid renderer, resolve it to the existing Character Visual Catalog, then scan UV0 palette cells.";

        private SkinnedMeshRenderer _previewRenderer;
        private int _previewMaterialIndex = -1;
        private readonly List<int> _previewMaterialIndices = new List<int>();
        private readonly List<MaterialPropertyBlock> _previewOriginalBlocks = new List<MaterialPropertyBlock>();
        private readonly List<Texture2D> _previewTextures = new List<Texture2D>();
        private int _previewCellX = -1;
        private int _previewCellY = -1;
        private GameObject _previewInstanceRoot;
        private UnityEngine.Object _previewPreviousSelection;

        // Editor-only validation controls. These use the exact production clothing palette
        // but never modify runtime/player/population state. Palette ids are 1-based to
        // match CharacterColorSelection / SidekickCharacterPaletteUtility.
        private int _previewPrimaryColorId = 3;
        private int _previewSecondaryColorId = 4;


        private DefaultAsset _scanFolder;
        private readonly List<BatchVisualEntry> _batchEntries = new List<BatchVisualEntry>();
        private readonly List<BatchVisualGroup> _batchGroups = new List<BatchVisualGroup>();
        private readonly Dictionary<ulong, string> _batchScanSignatures = new Dictionary<ulong, string>();
        private Vector2 _batchScroll;
        private bool _showBatchReview = true;
        private bool _showPopulationColorPool = true;
        private AuthoringTab _activeTab = AuthoringTab.ReviewQueue;
        private ReviewFilter _reviewFilter = ReviewFilter.All;
        private ReviewScope _reviewScope = ReviewScope.All;
        private string _reviewSearch = string.Empty;
        private VisualUsageChoice _bulkUsageChoice = VisualUsageChoice.PlayerAndPopulation;
        private string _currentScanSignature = string.Empty;
        private int _colorFamilyFilter;
        private static readonly string[] ColorFamilyFilterLabels =
        {
            "All Families", "Neutral", "Red / Pink", "Orange / Yellow", "Green", "Blue", "Purple", "Brown / Tan",
        };

        private static Material _cachedCanonicalPaletteMaterial;
        private static bool _canonicalPaletteSearchPerformed;

        [MenuItem("MMO Tools/Characters/Authoring/Population Clothing Palette Inspector")]
        private static void Open()
        {
            CharacterVisualPaletteAuthoringWindow window = GetWindow<CharacterVisualPaletteAuthoringWindow>();
            window.titleContent = new GUIContent("Visual Authoring");
            window.minSize = new Vector2(560f, 360f);
            window.Show();
        }

        private void OnEnable()
        {
            _profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(CharacterVisualCatalogBuilder.OutputAssetPath);
        }

        private void OnDisable()
        {
            RestorePreview();
        }

        private void OnGUI()
        {
            DrawSceneAuthoringUI();
        }

        private void DrawReviewQueueTab()
        {
            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Folder Review Queue", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Scan a content folder once, approve Player/Population usage in batches, then review only unresolved dye-zone decisions. Related modular pieces are grouped by their existing catalog display name so an outfit/set can be handled together.",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _scanFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                    "Source Folder", _scanFolder, typeof(DefaultAsset), false);
                using (new EditorGUI.DisabledScope(_scanFolder == null))
                {
                    if (GUILayout.Button("Scan Folder", GUILayout.Width(110f), GUILayout.Height(22f)))
                        ScanFolderCatalogOptions();
                }
            }

            if (_batchEntries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Choose a Project folder containing the modular FBXs and click Scan Folder. The queue is derived from the existing Character Visual Catalog; no duplicate mesh list is created.",
                    MessageType.None);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawQueueProgress();
            DrawQueueControls();

            for (int i = 0; i < _batchGroups.Count; ++i)
            {
                BatchVisualGroup group = _batchGroups[i];
                if (group == null || !GroupPassesFilter(group))
                    continue;
                DrawBatchGroup(group);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "Workflow: set usage in bulk where the folder rule is known, then use Review Dye / Confirm & Next only for clothing meshes that need visual palette-zone approval. Exact UV0 signatures can be analyzed and reused explicitly; nothing is auto-confirmed from a guess.",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private void DrawPopulationColorsTab()
        {
            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);
            DrawPopulationColorPool();
            EditorGUILayout.EndScrollView();
        }

        private void DrawScannerDebugTab()
        {
            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Mesh Dye Review", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Use this page only for the actual artistic decision: which referenced Character Master palette cells are Primary, Secondary, Accent, or Ignore. The queue handles bookkeeping and progress.",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                SkinnedMeshRenderer next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                    "Renderer", _renderer, typeof(SkinnedMeshRenderer), true);
                if (next != _renderer)
                    SetRenderer(next);

                if (GUILayout.Button("Use Selection", GUILayout.Width(110f)))
                    UseSelection();
            }

            DrawResolvedOption();
            DrawMaterialSelection();
            DrawDyePreviewControls();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_renderer == null || _renderer.sharedMesh == null || _resolvedOption == null))
                {
                    if (GUILayout.Button("Scan Palette Cells", GUILayout.Height(28f)))
                        Scan();
                }

                using (new EditorGUI.DisabledScope(_candidates.Count == 0 || _resolvedOption == null))
                {
                    if (GUILayout.Button("Auto Primary Suggestion", GUILayout.Width(165f), GUILayout.Height(28f)))
                        AutoAssignSuggestions();
                }

                using (new EditorGUI.DisabledScope(_previewRenderer == null))
                {
                    if (GUILayout.Button("Restore Preview", GUILayout.Width(120f), GUILayout.Height(28f)))
                        RestorePreview();
                }

                using (new EditorGUI.DisabledScope(_candidates.Count == 0))
                {
                    if (GUILayout.Button("Copy Report", GUILayout.Width(100f), GUILayout.Height(28f)))
                        EditorGUIUtility.systemCopyBuffer = BuildReport();
                }
            }

            EditorGUILayout.HelpBox(_scanReport, MessageType.None);

            if (_candidates.Count > 0)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField($"Palette Cell Review ({_candidates.Count})", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Skin-shared cells remain locked to Ignore. Highlight uncertain cells, then save only the zones that should actually be recolored.",
                    EditorStyles.wordWrappedMiniLabel);

                for (int i = 0; i < _candidates.Count; ++i)
                    DrawCandidate(_candidates[i]);
            }

            DrawReviewConfirmationControls();
            EditorGUILayout.EndScrollView();
        }

        private void DrawQueueProgress()
        {
            int usageConfirmed = 0;
            int dyeApplicable = 0;
            int dyeConfirmed = 0;
            int stale = 0;
            for (int i = 0; i < _batchEntries.Count; ++i)
            {
                BatchVisualEntry entry = _batchEntries[i];
                if (entry?.option == null)
                    continue;
                if (entry.option.usageReviewed)
                    usageConfirmed++;
                if (!IsDyeReviewApplicable(entry.slot))
                    continue;
                dyeApplicable++;
                if (IsPaletteReviewStale(entry.option))
                    stale++;
                else if (entry.option.paletteReviewConfirmed)
                    dyeConfirmed++;
            }

            float usageProgress = _batchEntries.Count > 0 ? usageConfirmed / (float)_batchEntries.Count : 1f;
            float dyeProgress = dyeApplicable > 0 ? dyeConfirmed / (float)dyeApplicable : 1f;
            Rect usageRect = GUILayoutUtility.GetRect(10f, 18f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(usageRect, usageProgress, $"Usage approval: {usageConfirmed} / {_batchEntries.Count}");
            Rect dyeRect = GUILayoutUtility.GetRect(10f, 18f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(dyeRect, dyeProgress, $"Dye-zone review: {dyeConfirmed} / {dyeApplicable}" + (stale > 0 ? $"   ({stale} stale)" : string.Empty));
        }

        private void DrawQueueControls()
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                _reviewSearch = EditorGUILayout.TextField("Search", _reviewSearch);
                _reviewScope = (ReviewScope)EditorGUILayout.EnumPopup(_reviewScope, GUILayout.Width(145f));
                _reviewFilter = (ReviewFilter)EditorGUILayout.EnumPopup(_reviewFilter, GUILayout.Width(125f));
                if (GUILayout.Button("Clear", GUILayout.Width(55f)))
                {
                    _reviewSearch = string.Empty;
                    _reviewScope = ReviewScope.All;
                    _reviewFilter = ReviewFilter.All;
                }
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Batch usage", GUILayout.Width(78f));
                _bulkUsageChoice = (VisualUsageChoice)EditorGUILayout.EnumPopup(_bulkUsageChoice, GUILayout.Width(165f));
                if (GUILayout.Button("Apply To Visible Scope", GUILayout.Width(145f)))
                    ApplyUsageToVisibleEntries(_bulkUsageChoice);
                if (GUILayout.Button("Apply To Whole Folder", GUILayout.Width(140f)))
                    ApplyUsageToEntries(_batchEntries, _bulkUsageChoice, "Apply Visual Usage To Scanned Folder");

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Analyze Dye Signatures", GUILayout.Width(165f)))
                    AnalyzeFolderDyeSignatures();
                if (GUILayout.Button("Review Next", GUILayout.Width(95f)))
                    ReviewNextPending(null);
            }
        }

        private bool GroupPassesFilter(BatchVisualGroup group)
        {
            if (group == null)
                return false;
            bool anyInScope = false;
            for (int i = 0; i < group.entries.Count; ++i)
                if (EntryPassesScope(group.entries[i])) { anyInScope = true; break; }
            if (!anyInScope)
                return false;
            if (!string.IsNullOrWhiteSpace(_reviewSearch))
            {
                string needle = _reviewSearch.Trim();
                bool match = (group.displayName ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match)
                {
                    for (int i = 0; i < group.entries.Count; ++i)
                    {
                        BatchVisualEntry entry = group.entries[i];
                        if ((entry?.option?.rendererPath ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (entry?.assetPath ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (entry?.slot?.displayName ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            match = true;
                            break;
                        }
                    }
                }
                if (!match)
                    return false;
            }

            if (_reviewFilter == ReviewFilter.All)
                return true;

            for (int i = 0; i < group.entries.Count; ++i)
                if (EntryPassesScope(group.entries[i]) && EntryPassesFilter(group.entries[i], _reviewFilter))
                    return true;
            return false;
        }

        private bool EntryPassesScope(BatchVisualEntry entry)
        {
            if (_reviewScope == ReviewScope.All)
                return true;
            ushort slotId = entry?.slot?.slotId ?? 0;
            bool hair = slotId == 2 || slotId == 3 || slotId == 4 || slotId == 9;
            bool clothing = IsDyeReviewApplicable(entry?.slot);
            switch (_reviewScope)
            {
                case ReviewScope.HairBrowsBeards:
                    return hair;
                case ReviewScope.Clothing:
                    return clothing;
                case ReviewScope.Other:
                    return !hair && !clothing;
                default:
                    return true;
            }
        }

        private bool EntryPassesFilter(BatchVisualEntry entry, ReviewFilter filter)
        {
            CharacterVisualOptionDefinition option = entry?.option;
            if (option == null)
                return false;
            bool stale = IsPaletteReviewStale(option);
            bool dyeApplicable = IsDyeReviewApplicable(entry.slot);
            bool confirmed = option.usageReviewed && (!dyeApplicable || (option.paletteReviewConfirmed && !stale));
            switch (filter)
            {
                case ReviewFilter.NeedsReview:
                    return !confirmed;
                case ReviewFilter.Confirmed:
                    return confirmed;
                case ReviewFilter.Player:
                    return option.AllowsPlayerUse;
                case ReviewFilter.Population:
                    return option.AllowsPopulationUse;
                case ReviewFilter.Both:
                    return option.AllowsPlayerUse && option.AllowsPopulationUse;
                case ReviewFilter.None:
                    return option.usageReviewed && !option.playerEligible && !option.populationEligible;
                case ReviewFilter.Stale:
                    return stale;
                default:
                    return true;
            }
        }

        private void DrawBatchGroup(BatchVisualGroup group)
        {
            int usageConfirmed = 0;
            int dyeApplicable = 0;
            int dyeConfirmed = 0;
            int stale = 0;
            bool allPlayer = true;
            bool allPopulation = true;
            bool anyPlayer = false;
            bool anyPopulation = false;
            for (int i = 0; i < group.entries.Count; ++i)
            {
                BatchVisualEntry entry = group.entries[i];
                CharacterVisualOptionDefinition option = entry?.option;
                if (option == null)
                    continue;
                if (option.usageReviewed)
                    usageConfirmed++;
                bool player = option.AllowsPlayerUse;
                bool population = option.AllowsPopulationUse;
                anyPlayer |= player;
                anyPopulation |= population;
                allPlayer &= player;
                allPopulation &= population;
                if (IsDyeReviewApplicable(entry.slot))
                {
                    dyeApplicable++;
                    if (IsPaletteReviewStale(option))
                        stale++;
                    else if (option.paletteReviewConfirmed)
                        dyeConfirmed++;
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string icon = stale > 0 ? "⚠" :
                        (usageConfirmed == group.entries.Count && dyeConfirmed == dyeApplicable ? "✓" : "•");
                    group.expanded = EditorGUILayout.Foldout(
                        group.expanded,
                        $"{icon} {group.displayName}   [{group.entries.Count} pieces]",
                        true,
                        EditorStyles.foldoutHeader);

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Preview Outfit", GUILayout.Width(105f)))
                        PreviewBatchGroup(group);
                    if (GUILayout.Button("Review Next", GUILayout.Width(92f)))
                        ReviewNextPending(group);
                }

                string usageSummary = allPlayer && allPopulation ? "Both" :
                    (allPlayer && !anyPopulation ? "Player" :
                     (allPopulation && !anyPlayer ? "Population" :
                      (!anyPlayer && !anyPopulation ? "None" : "Mixed")));
                EditorGUILayout.LabelField(
                    $"Usage: {usageSummary} ({usageConfirmed}/{group.entries.Count} explicitly confirmed)   |   Dye review: {dyeConfirmed}/{dyeApplicable}" +
                    (stale > 0 ? $"   |   STALE: {stale}" : string.Empty),
                    EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Set whole group", GUILayout.Width(95f));
                    if (GUILayout.Button("Both", GUILayout.Width(58f)))
                        ApplyUsageToEntries(group.entries, VisualUsageChoice.PlayerAndPopulation, "Set Visual Group To Player + Population");
                    if (GUILayout.Button("Player", GUILayout.Width(58f)))
                        ApplyUsageToEntries(group.entries, VisualUsageChoice.PlayerOnly, "Set Visual Group To Player Only");
                    if (GUILayout.Button("Population", GUILayout.Width(78f)))
                        ApplyUsageToEntries(group.entries, VisualUsageChoice.PopulationOnly, "Set Visual Group To Population Only");
                    if (GUILayout.Button("None", GUILayout.Width(58f)))
                        ApplyUsageToEntries(group.entries, VisualUsageChoice.None, "Disable Visual Group");
                    GUILayout.FlexibleSpace();
                }

                if (!group.expanded)
                    return;

                for (int i = 0; i < group.entries.Count; ++i)
                {
                    BatchVisualEntry entry = group.entries[i];
                    if (!EntryPassesScope(entry))
                        continue;
                    if (_reviewFilter != ReviewFilter.All && !EntryPassesFilter(entry, _reviewFilter))
                        continue;
                    DrawWorkflowEntry(entry);
                }
            }
        }

        private void DrawWorkflowEntry(BatchVisualEntry entry)
        {
            CharacterVisualOptionDefinition option = entry?.option;
            if (option == null)
                return;

            bool dyeApplicable = IsDyeReviewApplicable(entry.slot);
            bool stale = dyeApplicable && IsPaletteReviewStale(option);
            string dyeStatus = !dyeApplicable ? "N/A" :
                stale ? "STALE" :
                option.paletteReviewConfirmed
                    ? ((option.paletteCells?.Length ?? 0) > 0 ? "Confirmed" : "Not Dyeable")
                    : "Needs Review";

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(14f);
                GUILayout.Label(entry.slot != null ? entry.slot.slotId.ToString() : "-", GUILayout.Width(28f));
                GUILayout.Label(entry.slot != null ? entry.slot.displayName : "Unknown Slot", GUILayout.Width(130f));

                VisualUsageChoice current = GetUsageChoice(option);
                VisualUsageChoice next = (VisualUsageChoice)EditorGUILayout.EnumPopup(current, GUILayout.Width(155f));
                if (next != current)
                    SaveUsage(option, next);
                if (!option.usageReviewed && GUILayout.Button("Confirm Use", GUILayout.Width(85f)))
                    SaveUsage(option, current);

                GUILayout.Label(dyeStatus, stale ? EditorStyles.boldLabel : EditorStyles.miniLabel, GUILayout.Width(85f));
                GUILayout.Label($"Cells {option.paletteCells?.Length ?? 0}", GUILayout.Width(55f));

                using (new EditorGUI.DisabledScope(!dyeApplicable))
                {
                    if (GUILayout.Button("Review Dye", GUILayout.Width(82f)))
                        ReviewBatchEntry(entry);
                }
            }
        }

        private void ApplyUsageToVisibleEntries(VisualUsageChoice choice)
        {
            var visible = new List<BatchVisualEntry>();
            for (int i = 0; i < _batchEntries.Count; ++i)
            {
                BatchVisualEntry entry = _batchEntries[i];
                if (!EntryPassesScope(entry))
                    continue;
                if (_reviewFilter != ReviewFilter.All && !EntryPassesFilter(entry, _reviewFilter))
                    continue;
                if (!string.IsNullOrWhiteSpace(_reviewSearch))
                {
                    string needle = _reviewSearch.Trim();
                    bool match = (entry?.option?.displayName ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (entry?.option?.rendererPath ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (entry?.slot?.displayName ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (entry?.assetPath ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match)
                        continue;
                }
                visible.Add(entry);
            }
            ApplyUsageToEntries(visible, choice, "Apply Visual Usage To Visible Review Scope");
        }

        private void ApplyUsageToEntries(IList<BatchVisualEntry> entries, VisualUsageChoice choice, string undoName)
        {
            if (_profile == null || entries == null || entries.Count == 0)
                return;
            Undo.RecordObject(_profile, undoName);
            for (int i = 0; i < entries.Count; ++i)
            {
                CharacterVisualOptionDefinition option = entries[i]?.option;
                if (option == null)
                    continue;
                SetUsageNoSave(option, choice);
            }
            SaveProfile();
        }

        private static void SetUsageNoSave(CharacterVisualOptionDefinition option, VisualUsageChoice choice)
        {
            if (option == null)
                return;
            option.usageReviewed = true;
            option.playerEligible = choice == VisualUsageChoice.PlayerOnly || choice == VisualUsageChoice.PlayerAndPopulation;
            option.populationEligible = choice == VisualUsageChoice.PopulationOnly || choice == VisualUsageChoice.PlayerAndPopulation;
        }

        private static bool IsDyeReviewApplicable(CharacterVisualSlotDefinition slot)
        {
            if (slot == null)
                return false;
            ushort id = slot.slotId;
            return (id >= 10 && id <= 24) || id == 29 || id == 30;
        }

        private bool IsPaletteReviewStale(CharacterVisualOptionDefinition option)
        {
            if (option == null || !option.paletteReviewConfirmed)
                return false;
            string saved = option.paletteReviewFingerprint ?? string.Empty;
            string current = ComputePaletteReviewFingerprint(option);
            return string.IsNullOrEmpty(saved) || !string.Equals(saved, current, StringComparison.Ordinal);
        }

        private static string ComputePaletteReviewFingerprint(CharacterVisualOptionDefinition option)
        {
            if (option?.mesh == null)
                return string.Empty;
            string path = AssetDatabase.GetAssetPath(option.mesh) ?? string.Empty;
            string hash = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.GetAssetDependencyHash(path).ToString();
            Mesh mesh = option.mesh;
            return hash + "|" + mesh.name + "|" + mesh.vertexCount + "|" + mesh.subMeshCount;
        }

        private static ulong EntryKey(BatchVisualEntry entry)
        {
            if (entry?.slot == null || entry.option == null)
                return 0UL;
            return ((ulong)entry.slot.slotId << 32) | entry.option.optionId;
        }

        private void BuildBatchGroups()
        {
            _batchGroups.Clear();
            var lookup = new Dictionary<string, BatchVisualGroup>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _batchEntries.Count; ++i)
            {
                BatchVisualEntry entry = _batchEntries[i];
                if (entry?.option == null)
                    continue;
                string display = string.IsNullOrWhiteSpace(entry.option.displayName)
                    ? System.IO.Path.GetFileNameWithoutExtension(entry.assetPath)
                    : entry.option.displayName.Trim();
                string key = display;
                if (!lookup.TryGetValue(key, out BatchVisualGroup group))
                {
                    group = new BatchVisualGroup { key = key, displayName = display, expanded = true };
                    lookup.Add(key, group);
                    _batchGroups.Add(group);
                }
                group.entries.Add(entry);
            }

            _batchGroups.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
            for (int g = 0; g < _batchGroups.Count; ++g)
                _batchGroups[g].entries.Sort((a, b) => (a.slot?.slotId ?? 0).CompareTo(b.slot?.slotId ?? 0));
        }

        private void ReviewNextPending(BatchVisualGroup group)
        {
            IList<BatchVisualEntry> entries = group != null ? (IList<BatchVisualEntry>)group.entries : _batchEntries;
            for (int i = 0; i < entries.Count; ++i)
            {
                BatchVisualEntry entry = entries[i];
                if (entry?.option == null || !IsDyeReviewApplicable(entry.slot))
                    continue;
                if (!entry.option.paletteReviewConfirmed || IsPaletteReviewStale(entry.option))
                {
                    ReviewBatchEntry(entry);
                    return;
                }
            }
            _scanReport = group != null
                ? $"{group.displayName}: all dye-applicable pieces are confirmed."
                : "All dye-applicable pieces in the scanned folder are confirmed.";
        }

        private void DrawReviewConfirmationControls()
        {
            if (_resolvedOption == null || _resolvedSlot == null || !IsDyeReviewApplicable(_resolvedSlot))
                return;

            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool stale = IsPaletteReviewStale(_resolvedOption);
                EditorGUILayout.LabelField(
                    _resolvedOption.paletteReviewConfirmed && !stale
                        ? "Dye Review: CONFIRMED"
                        : (stale ? "Dye Review: STALE — mesh/content changed" : "Dye Review: NEEDS CONFIRMATION"),
                    EditorStyles.boldLabel);

                int exactMatches = CountExactSignatureMatches();
                if (exactMatches > 0)
                    EditorGUILayout.LabelField($"Exact scanned UV0 signature matches available: {exactMatches}", EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Confirm Mapping", GUILayout.Height(28f)))
                        ConfirmCurrentPaletteReview(false, false);
                    if (GUILayout.Button("Confirm + Next", GUILayout.Height(28f)))
                        ConfirmCurrentPaletteReview(false, true);
                    if (GUILayout.Button("Not Dyeable + Next", GUILayout.Height(28f)))
                        ConfirmCurrentPaletteReview(true, true);
                }

                using (new EditorGUI.DisabledScope(exactMatches <= 0 || !_resolvedOption.paletteReviewConfirmed))
                {
                    if (GUILayout.Button($"Apply This Confirmed Mapping To {exactMatches} Exact Signature Match(es)"))
                        ApplyCurrentMappingToExactSignatureMatches();
                }
            }
        }

        private void ConfirmCurrentPaletteReview(bool notDyeable, bool reviewNext)
        {
            if (_profile == null || _resolvedOption == null || _resolvedSlot == null)
                return;
            Undo.RecordObject(_profile, "Confirm Clothing Dye Review");
            if (notDyeable)
                _resolvedOption.paletteCells = Array.Empty<CharacterVisualPaletteCellDefinition>();
            _resolvedOption.paletteReviewConfirmed = true;
            _resolvedOption.paletteReviewFingerprint = ComputePaletteReviewFingerprint(_resolvedOption);
            _resolvedOption.paletteScanSignature = !string.IsNullOrEmpty(_currentScanSignature)
                ? _currentScanSignature
                : BuildSavedPaletteCellSignature(_resolvedOption.paletteCells);
            SaveProfile();

            if (!reviewNext)
                return;
            BatchVisualEntry current = FindBatchEntry(_resolvedSlot.slotId, _resolvedOption.optionId);
            int start = current != null ? _batchEntries.IndexOf(current) + 1 : 0;
            for (int offset = 0; offset < _batchEntries.Count; ++offset)
            {
                BatchVisualEntry entry = _batchEntries[(start + offset) % _batchEntries.Count];
                if (entry?.option == null || !IsDyeReviewApplicable(entry.slot))
                    continue;
                if (!entry.option.paletteReviewConfirmed || IsPaletteReviewStale(entry.option))
                {
                    ReviewBatchEntry(entry);
                    return;
                }
            }
            _scanReport = "All dye-applicable pieces in the scanned folder are confirmed.";
        }

        private BatchVisualEntry FindBatchEntry(ushort slotId, ushort optionId)
        {
            for (int i = 0; i < _batchEntries.Count; ++i)
            {
                BatchVisualEntry entry = _batchEntries[i];
                if (entry?.slot?.slotId == slotId && entry.option?.optionId == optionId)
                    return entry;
            }
            return null;
        }

        private int CountExactSignatureMatches()
        {
            if (_resolvedOption == null || string.IsNullOrEmpty(_currentScanSignature))
                return 0;
            int count = 0;
            BatchVisualEntry current = _resolvedSlot != null ? FindBatchEntry(_resolvedSlot.slotId, _resolvedOption.optionId) : null;
            ulong currentKey = EntryKey(current);
            foreach (KeyValuePair<ulong, string> pair in _batchScanSignatures)
            {
                if (pair.Key == currentKey)
                    continue;
                if (string.Equals(pair.Value, _currentScanSignature, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private void ApplyCurrentMappingToExactSignatureMatches()
        {
            if (_profile == null || _resolvedOption == null || string.IsNullOrEmpty(_currentScanSignature))
                return;
            CharacterVisualPaletteCellDefinition[] source = _resolvedOption.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>();
            BatchVisualEntry current = _resolvedSlot != null ? FindBatchEntry(_resolvedSlot.slotId, _resolvedOption.optionId) : null;
            ulong currentKey = EntryKey(current);
            int applied = 0;
            Undo.RecordObject(_profile, "Reuse Confirmed Dye Mapping");
            for (int i = 0; i < _batchEntries.Count; ++i)
            {
                BatchVisualEntry entry = _batchEntries[i];
                if (entry?.option == null || EntryKey(entry) == currentKey)
                    continue;
                if (!_batchScanSignatures.TryGetValue(EntryKey(entry), out string signature) ||
                    !string.Equals(signature, _currentScanSignature, StringComparison.Ordinal))
                    continue;
                entry.option.paletteCells = (CharacterVisualPaletteCellDefinition[])source.Clone();
                entry.option.paletteReviewConfirmed = true;
                entry.option.paletteReviewFingerprint = ComputePaletteReviewFingerprint(entry.option);
                entry.option.paletteScanSignature = signature;
                applied++;
            }
            SaveProfile();
            _scanReport = $"Reused the confirmed dye-zone mapping on {applied} exact UV0 signature match(es).";
        }

        private void AnalyzeFolderDyeSignatures()
        {
            _batchScanSignatures.Clear();
            if (_batchEntries.Count == 0)
                return;
            try
            {
                for (int i = 0; i < _batchEntries.Count; ++i)
                {
                    BatchVisualEntry entry = _batchEntries[i];
                    if (entry?.option == null || !IsDyeReviewApplicable(entry.slot))
                        continue;
                    EditorUtility.DisplayProgressBar(
                        "Analyze Clothing Dye Signatures",
                        entry.option.displayName + " / " + entry.slot.displayName,
                        _batchEntries.Count > 0 ? i / (float)_batchEntries.Count : 1f);
                    if (TryScanBatchEntry(entry, out List<PaletteCellCandidate> candidates, out string signature))
                        _batchScanSignatures[EntryKey(entry)] = signature;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            _scanReport = $"Analyzed {_batchScanSignatures.Count} dye-applicable catalog meshes. Exact signatures can now be reused only when you explicitly choose to do so.";
        }

        private bool TryScanBatchEntry(BatchVisualEntry entry, out List<PaletteCellCandidate> candidates, out string signature)
        {
            candidates = new List<PaletteCellCandidate>();
            signature = string.Empty;
            if (!TryResolveBatchRenderer(entry, out SkinnedMeshRenderer renderer) || renderer == null)
                return false;

            var aggregate = new Dictionary<PaletteCellKey, PaletteCellCandidate>();
            var readableTextureCache = new Dictionary<Texture2D, Texture2D>();
            try
            {
                if (!ScanRenderer(renderer, aggregate, readableTextureCache, out _))
                    return false;
            }
            finally
            {
                foreach (Texture2D readable in readableTextureCache.Values)
                    if (readable != null)
                        DestroyImmediate(readable);
            }

            candidates.AddRange(aggregate.Values);
            candidates.Sort(CompareCandidates);
            MarkSuggestions(candidates);
            signature = BuildCandidateSignature(candidates);
            return candidates.Count > 0;
        }

        private static string BuildCandidateSignature(IList<PaletteCellCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return "NONE";
            var builder = new StringBuilder(candidates.Count * 16);
            for (int i = 0; i < candidates.Count; ++i)
            {
                PaletteCellCandidate candidate = candidates[i];
                builder.Append(candidate.colorMapWidth).Append('x').Append(candidate.colorMapHeight)
                    .Append(':').Append(candidate.cellX).Append(',').Append(candidate.cellY)
                    .Append(candidate.skinReferences > 0 ? 'S' : 'C').Append(';');
            }
            return builder.ToString();
        }

        private static string BuildSavedPaletteCellSignature(CharacterVisualPaletteCellDefinition[] cells)
        {
            if (cells == null || cells.Length == 0)
                return "NONE";
            var copy = new List<CharacterVisualPaletteCellDefinition>(cells);
            SortCells(copy);
            var builder = new StringBuilder(copy.Count * 16);
            for (int i = 0; i < copy.Count; ++i)
            {
                CharacterVisualPaletteCellDefinition cell = copy[i];
                builder.Append(cell.colorMapWidth).Append('x').Append(cell.colorMapHeight)
                    .Append(':').Append(cell.cellX).Append(',').Append(cell.cellY).Append(';');
            }
            return builder.ToString();
        }

        private bool TryResolveBatchRenderer(BatchVisualEntry entry, out SkinnedMeshRenderer match)
        {
            match = null;
            if (entry?.option?.mesh == null)
                return false;
            string assetPath = !string.IsNullOrWhiteSpace(entry.assetPath)
                ? entry.assetPath
                : AssetDatabase.GetAssetPath(entry.option.mesh);
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null)
                return false;
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer candidate = renderers[i];
                if (candidate != null && candidate.sharedMesh == entry.option.mesh)
                {
                    match = candidate;
                    return true;
                }
            }
            return false;
        }

        private void PreviewBatchGroup(BatchVisualGroup group)
        {
            RestorePreview();
            if (_profile == null || group == null || _profile.EditableBasePrefab == null)
                return;

            GameObject source = _profile.EditableBasePrefab;
            GameObject instance = PrefabUtility.InstantiatePrefab(source) as GameObject;
            if (instance == null)
                instance = Instantiate(source);
            if (instance == null)
                return;

            _previewInstanceRoot = instance;
            instance.name = group.displayName + " [Full Outfit Preview - Unsaved]";
            instance.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null)
                instance.transform.position = view.pivot;

            Animator animator = instance.GetComponentInChildren<Animator>(true);
            if (animator != null)
                animator.enabled = false;

            ModularCharacterAppearancePresenter presenter = instance.GetComponent<ModularCharacterAppearancePresenter>();
            if (presenter == null)
                presenter = instance.AddComponent<ModularCharacterAppearancePresenter>();
            presenter.Configure(_profile);

            Game.Shared.Characters.CharacterAppearanceRecipe recipe = _profile.CreateDefaultRecipe();
            for (int i = 0; i < group.entries.Count; ++i)
            {
                BatchVisualEntry entry = group.entries[i];
                if (entry?.slot == null || entry.option == null)
                    continue;
                SetRecipeMesh(recipe, entry.slot.slotId, entry.option.optionId);
            }

            // Deliberately distinct region colors make cross-region mistakes obvious during review.
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.UpperPrimaryColorChannelId, 9);   // Pink
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.UpperSecondaryColorChannelId, 16); // Cream
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.LowerPrimaryColorChannelId, 33); // Green
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.LowerSecondaryColorChannelId, 32); // Olive
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.FeetPrimaryColorChannelId, 10); // Orange
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.FeetSecondaryColorChannelId, 6); // Brown
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.AccessoryPrimaryColorChannelId, 41); // Purple
            SetRecipeColor(recipe, SidekickCharacterPaletteUtility.AccessorySecondaryColorChannelId, 15); // Off White
            presenter.ApplyAppearance(recipe);

            _previewPreviousSelection = Selection.activeObject;
            Selection.activeGameObject = instance;
            if (view != null)
            {
                view.FrameSelected();
                view.Repaint();
            }
            _scanReport = $"Previewing full '{group.displayName}' outfit on the existing editable humanoid base. Preview colors are intentionally distinct by region: pink upper, green lower, orange feet. The object is unsaved and removed by Restore Preview.";
        }

        private static void SetRecipeMesh(Game.Shared.Characters.CharacterAppearanceRecipe recipe, ushort slotId, ushort optionId)
        {
            if (recipe == null)
                return;
            var values = new List<Game.Shared.Characters.CharacterMeshSelection>(recipe.meshes ?? Array.Empty<Game.Shared.Characters.CharacterMeshSelection>());
            for (int i = 0; i < values.Count; ++i)
            {
                if (values[i].slotId != slotId)
                    continue;
                values[i] = new Game.Shared.Characters.CharacterMeshSelection(slotId, optionId);
                recipe.meshes = values.ToArray();
                return;
            }
            values.Add(new Game.Shared.Characters.CharacterMeshSelection(slotId, optionId));
            recipe.meshes = values.ToArray();
        }

        private static void SetRecipeColor(Game.Shared.Characters.CharacterAppearanceRecipe recipe, ushort channelId, uint paletteId)
        {
            if (recipe == null)
                return;
            var values = new List<Game.Shared.Characters.CharacterColorSelection>(recipe.colors ?? Array.Empty<Game.Shared.Characters.CharacterColorSelection>());
            for (int i = 0; i < values.Count; ++i)
            {
                if (values[i].channelId != channelId)
                    continue;
                values[i] = new Game.Shared.Characters.CharacterColorSelection(
                    channelId,
                    Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                    paletteId);
                recipe.colors = values.ToArray();
                return;
            }
            values.Add(new Game.Shared.Characters.CharacterColorSelection(
                channelId,
                Game.Shared.Characters.CharacterColorEncoding.PaletteIndex,
                paletteId));
            recipe.colors = values.ToArray();
        }

        private void DrawBatchCatalogReview()
        {
            EditorGUILayout.Space(6f);
            _showBatchReview = EditorGUILayout.Foldout(
                _showBatchReview,
                "Folder Visual Usage Review",
                true,
                EditorStyles.foldoutHeader);
            if (!_showBatchReview)
                return;

            EditorGUILayout.LabelField(
                "Scans the selected Project folder against the existing Character Visual Catalog. Review status and Player/Population eligibility are saved on the existing catalog option; no second mesh list is created.",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _scanFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                    "Source Folder", _scanFolder, typeof(DefaultAsset), false);
                using (new EditorGUI.DisabledScope(_profile == null || _scanFolder == null))
                {
                    if (GUILayout.Button("Scan Folder", GUILayout.Width(110f)))
                        ScanFolderCatalogOptions();
                }
            }

            if (_batchEntries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Choose a folder containing the modular FBXs and click Scan Folder. The list is derived from the existing catalog mesh references, so stable catalog option IDs remain the saved identity.",
                    MessageType.None);
                return;
            }

            int reviewed = 0;
            int player = 0;
            int population = 0;
            int none = 0;
            for (int i = 0; i < _batchEntries.Count; ++i)
            {
                CharacterVisualOptionDefinition option = _batchEntries[i].option;
                if (option == null)
                    continue;
                if (option.usageReviewed)
                    reviewed++;
                if (option.usageReviewed && !option.playerEligible && !option.populationEligible)
                    none++;
                if (option.AllowsPlayerUse)
                    player++;
                if (option.AllowsPopulationUse)
                    population++;
            }

            EditorGUILayout.LabelField(
                $"{_batchEntries.Count} catalog options | {reviewed} confirmed | Player {player} | Population {population} | None {none}",
                EditorStyles.miniBoldLabel);

            _batchScroll = EditorGUILayout.BeginScrollView(_batchScroll, GUILayout.MinHeight(140f), GUILayout.MaxHeight(260f));
            for (int i = 0; i < _batchEntries.Count; ++i)
                DrawBatchEntry(_batchEntries[i]);
            EditorGUILayout.EndScrollView();
        }

        private void DrawBatchEntry(BatchVisualEntry entry)
        {
            if (entry == null || entry.option == null)
                return;

            CharacterVisualOptionDefinition option = entry.option;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(option.usageReviewed ? "✓" : "?", GUILayout.Width(18f));
                string slot = entry.slot != null ? entry.slot.slotId.ToString() : "-";
                GUIContent label = new GUIContent(
                    $"{slot}  {option.displayName}",
                    entry.assetPath ?? string.Empty);
                GUILayout.Label(label, GUILayout.MinWidth(230f));

                VisualUsageChoice current = GetUsageChoice(option);
                VisualUsageChoice next = (VisualUsageChoice)EditorGUILayout.EnumPopup(current, GUILayout.Width(155f));
                if (next != current)
                    SaveUsage(option, next);

                int cellCount = option.paletteCells != null ? option.paletteCells.Length : 0;
                GUILayout.Label($"Cells {cellCount}", GUILayout.Width(58f));

                if (!option.usageReviewed)
                {
                    if (GUILayout.Button("Confirm", GUILayout.Width(70f)))
                        SaveUsage(option, current);
                }

                if (GUILayout.Button("Review Dye", GUILayout.Width(88f)))
                    ReviewBatchEntry(entry);
            }
        }

        private void ScanFolderCatalogOptions()
        {
            RestorePreview();
            _batchEntries.Clear();
            _batchGroups.Clear();
            _batchScanSignatures.Clear();
            if (_profile == null || _scanFolder == null)
                return;

            string folderPath = AssetDatabase.GetAssetPath(_scanFolder);
            if (string.IsNullOrWhiteSpace(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
            {
                _scanReport = "Source Folder must be a Project folder.";
                return;
            }

            string prefix = folderPath.TrimEnd('/') + "/";
            IReadOnlyList<CharacterVisualSlotDefinition> slots = _profile.Slots;
            for (int s = 0; s < slots.Count; ++s)
            {
                CharacterVisualSlotDefinition slot = slots[s];
                if (slot == null)
                    continue;
                CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                for (int o = 0; o < options.Length; ++o)
                {
                    CharacterVisualOptionDefinition option = options[o];
                    if (option == null || option.mesh == null)
                        continue;
                    string assetPath = AssetDatabase.GetAssetPath(option.mesh) ?? string.Empty;
                    if (!assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    _batchEntries.Add(new BatchVisualEntry
                    {
                        slot = slot,
                        option = option,
                        assetPath = assetPath,
                    });
                }
            }

            _batchEntries.Sort((a, b) =>
            {
                int path = string.Compare(a.assetPath, b.assetPath, StringComparison.OrdinalIgnoreCase);
                if (path != 0)
                    return path;
                int slot = (a.slot?.slotId ?? 0).CompareTo(b.slot?.slotId ?? 0);
                if (slot != 0)
                    return slot;
                return string.Compare(a.option?.displayName, b.option?.displayName, StringComparison.OrdinalIgnoreCase);
            });

            BuildBatchGroups();
            _scanReport = _batchEntries.Count > 0
                ? $"Folder scan found {_batchEntries.Count} existing Character Visual Catalog option(s) in {_batchGroups.Count} visual group(s) under {folderPath}."
                : $"No Character Visual Catalog mesh references were found under {folderPath}.";
        }

        private void ReviewBatchEntry(BatchVisualEntry entry)
        {
            if (entry == null || entry.option == null || entry.option.mesh == null)
                return;

            if (!TryResolveBatchRenderer(entry, out SkinnedMeshRenderer match) || match == null)
            {
                _scanReport = $"Catalog mesh {entry.option.displayName} was found in the folder list, but its FBX renderer could not be resolved.";
                return;
            }

            SetRenderer(match);
            Scan();
            _activeTab = AuthoringTab.ScannerDebug;
            Repaint();
        }

        private static VisualUsageChoice GetUsageChoice(CharacterVisualOptionDefinition option)
        {
            if (option == null)
                return VisualUsageChoice.None;

            bool player = option.usageReviewed ? option.playerEligible : true;
            bool population = option.populationEligible;
            if (player && population)
                return VisualUsageChoice.PlayerAndPopulation;
            if (player)
                return VisualUsageChoice.PlayerOnly;
            if (population)
                return VisualUsageChoice.PopulationOnly;
            return VisualUsageChoice.None;
        }

        private static string GetUsageLabel(CharacterVisualOptionDefinition option)
        {
            if (option == null)
                return "Not resolved";
            string value = GetUsageChoice(option).ToString();
            return option.usageReviewed ? value + " — CONFIRMED" : value + " — UNCONFIRMED DEFAULT";
        }

        private void SaveUsage(CharacterVisualOptionDefinition option, VisualUsageChoice choice)
        {
            if (_profile == null || option == null)
                return;

            Undo.RecordObject(_profile, "Confirm Character Visual Usage");
            SetUsageNoSave(option, choice);
            SaveProfile();
        }

        private void DrawPopulationColorPool()
        {
            if (_profile == null)
                return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Population Clothing Color Pool", EditorStyles.boldLabel);
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            if (palette == null || palette.Length == 0)
            {
                EditorGUILayout.HelpBox("Production clothing palette is empty.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField(
                "These are client-side stable palette IDs. Players may eventually use the full dye library, while ambient Population chooses only from the checked subset. The same approved pool is used independently for Upper, Lower, Feet and Accessory regional colors. No Population dye values are sent over the wire.",
                EditorStyles.wordWrappedMiniLabel);

            bool reviewed = _profile.PopulationClothingColorPoolReviewed;
            var approved = new HashSet<ushort>();
            IReadOnlyList<ushort> saved = _profile.PopulationClothingPaletteIds;
            if (reviewed)
            {
                for (int i = 0; i < saved.Count; ++i)
                    approved.Add(saved[i]);
            }
            else
            {
                ushort[] defaults = SidekickCharacterPaletteUtility.GetDefaultPopulationClothingPaletteIds();
                for (int i = 0; i < defaults.Length; ++i)
                    approved.Add(defaults[i]);
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(reviewed
                        ? $"CONFIRMED — {approved.Count} / {palette.Length} colors allowed"
                        : $"UNCONFIRMED — using curated Population-safe defaults ({approved.Count} / {palette.Length})",
                    EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Population-Safe Defaults", GUILayout.Width(160f)))
                {
                    approved.Clear();
                    ushort[] defaults = SidekickCharacterPaletteUtility.GetDefaultPopulationClothingPaletteIds();
                    for (int i = 0; i < defaults.Length; ++i)
                        approved.Add(defaults[i]);
                    SavePopulationColorPool(approved);
                }
                if (GUILayout.Button("Allow All", GUILayout.Width(80f)))
                {
                    approved.Clear();
                    for (ushort id = 1; id <= palette.Length; ++id)
                        approved.Add(id);
                    SavePopulationColorPool(approved);
                }
                if (GUILayout.Button("Allow None", GUILayout.Width(85f)))
                {
                    approved.Clear();
                    SavePopulationColorPool(approved);
                }
            }

            _colorFamilyFilter = EditorGUILayout.Popup("Color Family", _colorFamilyFilter, ColorFamilyFilterLabels);
            string requiredFamily = _colorFamilyFilter <= 0 ? null : ColorFamilyFilterLabels[_colorFamilyFilter];
            int columns = Mathf.Max(1, Mathf.FloorToInt((position.width - 36f) / 170f));

            for (int familyIndex = 1; familyIndex < ColorFamilyFilterLabels.Length; ++familyIndex)
            {
                string family = ColorFamilyFilterLabels[familyIndex];
                if (requiredFamily != null && !string.Equals(requiredFamily, family, StringComparison.OrdinalIgnoreCase))
                    continue;

                var familyIndices = new List<int>();
                for (int i = 0; i < palette.Length; ++i)
                    if (string.Equals(SidekickCharacterPaletteUtility.GetClothingPaletteFamily(i + 1), family, StringComparison.OrdinalIgnoreCase))
                        familyIndices.Add(i);
                if (familyIndices.Count == 0)
                    continue;

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField(family, EditorStyles.boldLabel);
                for (int startIndex = 0; startIndex < familyIndices.Count; startIndex += columns)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        int endIndex = Mathf.Min(familyIndices.Count, startIndex + columns);
                        for (int j = startIndex; j < endIndex; ++j)
                        {
                            int i = familyIndices[j];
                            ushort id = (ushort)(i + 1);
                            bool before = approved.Contains(id);
                            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(164f), GUILayout.Height(54f)))
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    bool after = EditorGUILayout.Toggle(before, GUILayout.Width(18f));
                                    Rect swatch = GUILayoutUtility.GetRect(38f, 30f, GUILayout.Width(38f), GUILayout.Height(30f));
                                    EditorGUI.DrawRect(swatch, palette[i]);
                                    using (new EditorGUILayout.VerticalScope())
                                    {
                                        GUILayout.Label($"{id}: {SidekickCharacterPaletteUtility.GetClothingPaletteName(id)}", EditorStyles.miniBoldLabel);
                                        GUILayout.Label(family, EditorStyles.miniLabel);
                                    }
                                    if (after != before)
                                    {
                                        if (after)
                                            approved.Add(id);
                                        else
                                            approved.Remove(id);
                                        SavePopulationColorPool(approved);
                                    }
                                }
                            }
                        }
                        GUILayout.FlexibleSpace();
                    }
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "Important: enabling a color here only makes that stable palette ID eligible for deterministic Population selection. It does not alter which parts of a garment are dyeable; those remain the explicitly confirmed Primary / Secondary palette cells on each mesh.",
                MessageType.None);
        }

        private void SavePopulationColorPool(HashSet<ushort> approved)
        {
            if (_profile == null)
                return;

            var ids = new List<ushort>(approved ?? new HashSet<ushort>());
            ids.Sort();
            Undo.RecordObject(_profile, "Confirm Population Clothing Color Pool");
            _profile.SetPopulationClothingColorPool(true, ids.ToArray());
            SaveProfile();
        }

        private static string GetClothingPaletteLabel(ushort id) =>
            SidekickCharacterPaletteUtility.GetClothingPaletteName(id);

        private void DrawResolvedOption()
        {
            string slotText = _resolvedSlot != null
                ? $"{_resolvedSlot.slotId} — {_resolvedSlot.displayName}"
                : "Not resolved";
            string optionText = _resolvedOption != null
                ? $"{_resolvedOption.optionId} — {_resolvedOption.displayName}"
                : "Not resolved";

            EditorGUILayout.LabelField("Catalog Slot", slotText);
            EditorGUILayout.LabelField("Catalog Option", optionText);
            if (_resolvedSlot != null)
            {
                EditorGUILayout.LabelField(
                    "Runtime Dye Region",
                    SidekickCharacterPaletteUtility.ClothingDyeRegionLabel(_resolvedSlot.slotId));
            }
            if (_resolvedOption != null)
            {
                string usage = GetUsageLabel(_resolvedOption);
                EditorGUILayout.LabelField("Usage Approval", usage);
                bool stale = IsPaletteReviewStale(_resolvedOption);
                string dyeReview = !_resolvedOption.paletteReviewConfirmed
                    ? "NEEDS CONFIRMATION"
                    : (stale ? "STALE — SOURCE CHANGED" : "CONFIRMED");
                EditorGUILayout.LabelField("Dye Review", dyeReview);
                EditorGUILayout.LabelField("Saved Palette Cells", (_resolvedOption.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>()).Length.ToString());
            }
        }

        private void DrawMaterialSelection()
        {
            if (_renderer == null)
                return;

            Material[] materials = _renderer.sharedMaterials;
            int count = materials != null ? materials.Length : 0;
            if (count <= 0)
            {
                EditorGUILayout.LabelField("Material", "No material assigned — scanner will use canonical Character Master palette metadata if available.");
                return;
            }

            _materialIndex = Mathf.Clamp(_materialIndex, 0, count - 1);
            if (count == 1)
            {
                EditorGUILayout.LabelField("Material", materials[0] != null ? materials[0].name : "Missing Material");
                return;
            }

            string[] labels = new string[count];
            for (int i = 0; i < count; ++i)
                labels[i] = i + " — " + (materials[i] != null ? materials[i].name : "Missing Material");

            int next = EditorGUILayout.Popup("Preview Material Slot", _materialIndex, labels);
            if (next != _materialIndex)
            {
                RestorePreview();
                _materialIndex = next;
            }
        }

        private void DrawDyePreviewControls()
        {
            if (_resolvedOption == null)
                return;

            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            int paletteCount = palette != null ? palette.Length : 0;
            if (paletteCount <= 0)
                return;

            _previewPrimaryColorId = Mathf.Clamp(_previewPrimaryColorId, 1, paletteCount);
            _previewSecondaryColorId = Mathf.Clamp(_previewSecondaryColorId, 1, paletteCount);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Runtime Dye Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Uses the exact production clothing palette and the saved Primary / Secondary cell assignments. Accent and Ignore remain unchanged, matching the current runtime dye behavior.",
                EditorStyles.wordWrappedMiniLabel);

            DrawPalettePreviewRow("Primary Palette ID", ref _previewPrimaryColorId, palette);
            DrawPalettePreviewRow("Secondary Palette ID", ref _previewSecondaryColorId, palette);

            CharacterVisualPaletteCellDefinition[] cells =
                _resolvedOption.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>();
            bool hasDyeCells = HasRuntimeDyeCells(cells);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!hasDyeCells || _renderer == null))
                {
                    if (GUILayout.Button("Preview Saved Dyes", GUILayout.Height(24f)))
                        PreviewSavedDyes();

                    if (GUILayout.Button("Random Dye Preview", GUILayout.Width(150f), GUILayout.Height(24f)))
                    {
                        _previewPrimaryColorId = UnityEngine.Random.Range(1, paletteCount + 1);
                        _previewSecondaryColorId = UnityEngine.Random.Range(1, paletteCount + 1);
                        PreviewSavedDyes();
                    }
                }
            }

            if (!hasDyeCells)
            {
                EditorGUILayout.HelpBox(
                    "No saved Primary/Secondary cells yet. Assign at least one scanned cell, then use Preview Saved Dyes to verify the actual recolor before Play Mode.",
                    MessageType.None);
            }
        }

        private static void DrawPalettePreviewRow(string label, ref int paletteId, Color[] palette)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var labels = new string[palette.Length];
                for (int i = 0; i < labels.Length; ++i)
                    labels[i] = $"{i + 1}: {SidekickCharacterPaletteUtility.GetClothingPaletteName(i + 1)}";
                int index = Mathf.Clamp(paletteId - 1, 0, palette.Length - 1);
                index = EditorGUILayout.Popup(label, index, labels);
                paletteId = index + 1;
                Rect swatch = GUILayoutUtility.GetRect(42f, 20f, GUILayout.Width(42f));
                EditorGUI.DrawRect(swatch, palette[index]);
            }
        }

        private static bool HasRuntimeDyeCells(CharacterVisualPaletteCellDefinition[] cells)
        {
            if (cells == null)
                return false;
            for (int i = 0; i < cells.Length; ++i)
            {
                CharacterVisualPaletteCellDefinition cell = cells[i];
                if (cell.IsValid &&
                    (cell.channel == CharacterVisualPaletteChannel.Primary ||
                     cell.channel == CharacterVisualPaletteChannel.Secondary))
                {
                    return true;
                }
            }
            return false;
        }

        private void DrawCandidate(PaletteCellCandidate candidate)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect swatch = GUILayoutUtility.GetRect(28f, 18f, GUILayout.Width(28f));
                    EditorGUI.DrawRect(swatch, candidate.sourceColor);

                    GUILayout.Label($"Cell ({candidate.cellX}, {candidate.cellY})", GUILayout.Width(95f));
                    GUILayout.Label($"Refs {candidate.totalReferences}", GUILayout.Width(65f));
                    GUILayout.Label($"Cloth {candidate.nonSkinReferences}", GUILayout.Width(70f));
                    GUILayout.Label($"Skin {candidate.skinReferences}", GUILayout.Width(60f));

                    if (candidate.skinReferences > 0)
                        GUILayout.Label("SKIN SHARED", EditorStyles.boldLabel, GUILayout.Width(95f));
                    else if (candidate.isSuggested)
                        GUILayout.Label("Suggested", GUILayout.Width(70f));
                    else if (!candidate.hasUsableSkinMask)
                        GUILayout.Label("Verify mask", GUILayout.Width(70f));
                    else
                        GUILayout.Space(70f);

                    GUILayout.FlexibleSpace();

                    bool highlighted = _previewRenderer != null &&
                                       _previewCellX == candidate.cellX &&
                                       _previewCellY == candidate.cellY;
                    if (GUILayout.Button(highlighted ? "Highlighted" : "Highlight", GUILayout.Width(90f)))
                        Highlight(candidate);
                }

                int current = GetAssignmentPopupValue(candidate);
                using (new EditorGUI.DisabledScope(candidate.skinReferences > 0))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Assignment", GUILayout.Width(72f));
                        if (GUILayout.Toggle(current == 0, "Ignore", "Button", GUILayout.Width(70f)) && current != 0)
                            SaveAssignment(candidate, 0);
                        if (GUILayout.Toggle(current == 1, "Primary", "Button", GUILayout.Width(76f)) && current != 1)
                            SaveAssignment(candidate, 1);
                        if (GUILayout.Toggle(current == 2, "Secondary", "Button", GUILayout.Width(88f)) && current != 2)
                            SaveAssignment(candidate, 2);
                        if (GUILayout.Toggle(current == 3, "Accent", "Button", GUILayout.Width(70f)) && current != 3)
                            SaveAssignment(candidate, 3);
                        GUILayout.FlexibleSpace();
                    }
                }

                if (candidate.skinReferences > 0 && current != 0)
                    SaveAssignment(candidate, 0);
            }
        }

        private void UseSelection()
        {
            GameObject selected = Selection.activeGameObject;
            SkinnedMeshRenderer renderer = null;
            if (selected != null)
            {
                renderer = selected.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null)
                    renderer = selected.GetComponentInChildren<SkinnedMeshRenderer>(true);
            }
            SetRenderer(renderer);
            if (renderer == null)
                _scanReport = "The current selection does not contain a SkinnedMeshRenderer.";
        }

        private void SetRenderer(SkinnedMeshRenderer renderer)
        {
            RestorePreview();
            _renderer = renderer;
            _materialIndex = 0;
            _candidates.Clear();
            _currentScanSignature = string.Empty;
            ResolveCatalogOption();
            _scanReport = renderer != null
                ? (_resolvedOption != null
                    ? "Renderer resolved to the existing Character Visual Catalog. Scan UV0 palette cells."
                    : "Renderer selected, but no matching catalog option was found. Rebuild the Character Visual Catalog if this is newly imported content.")
                : "Select a modular humanoid renderer.";
        }

        private void ResolveCatalogOption()
        {
            _resolvedSlot = null;
            _resolvedOption = null;
            if (_profile == null || _renderer == null || _renderer.sharedMesh == null)
                return;

            ushort slotId = ExtractSlotId(_renderer.name);
            if (slotId == 0 && _renderer.transform.parent != null)
                slotId = ExtractSlotId(_renderer.transform.parent.name);
            if (slotId == 0 || !_profile.TryGetSlot(slotId, out CharacterVisualSlotDefinition slot) || slot == null)
                return;

            CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
            CharacterVisualOptionDefinition meshMatch = null;
            for (int i = 0; i < options.Length; ++i)
            {
                CharacterVisualOptionDefinition option = options[i];
                if (option == null || option.mesh != _renderer.sharedMesh)
                    continue;

                if (!string.IsNullOrEmpty(option.rendererPath) &&
                    (option.rendererPath.EndsWith("/" + _renderer.name, StringComparison.Ordinal) ||
                     string.Equals(option.rendererPath, _renderer.name, StringComparison.Ordinal)))
                {
                    _resolvedSlot = slot;
                    _resolvedOption = option;
                    return;
                }

                if (meshMatch == null)
                    meshMatch = option;
            }

            if (meshMatch != null)
            {
                _resolvedSlot = slot;
                _resolvedOption = meshMatch;
            }
        }

        private static ushort ExtractSlotId(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return 0;
            Match match = SlotRegex.Match(name);
            return match.Success && ushort.TryParse(match.Groups[1].Value, out ushort value) ? value : (ushort)0;
        }

        private void Scan()
        {
            RestorePreview();
            _candidates.Clear();
            _currentScanSignature = string.Empty;
            if (_renderer == null || _renderer.sharedMesh == null || _resolvedOption == null)
            {
                _scanReport = "Renderer/catalog option is not ready.";
                return;
            }

            var aggregate = new Dictionary<PaletteCellKey, PaletteCellCandidate>();
            var readableTextureCache = new Dictionary<Texture2D, Texture2D>();
            string warning = null;
            try
            {
                if (!ScanRenderer(_renderer, aggregate, readableTextureCache, out warning))
                {
                    _scanReport = string.IsNullOrWhiteSpace(warning) ? "Palette scan found no usable cells." : warning;
                    return;
                }
            }
            finally
            {
                foreach (Texture2D readable in readableTextureCache.Values)
                    if (readable != null)
                        DestroyImmediate(readable);
            }

            _candidates.AddRange(aggregate.Values);
            _candidates.Sort(CompareCandidates);
            MarkSuggestions(_candidates);
            _currentScanSignature = BuildCandidateSignature(_candidates);
            BatchVisualEntry scannedEntry = _resolvedSlot != null && _resolvedOption != null
                ? FindBatchEntry(_resolvedSlot.slotId, _resolvedOption.optionId)
                : null;
            if (scannedEntry != null)
                _batchScanSignatures[EntryKey(scannedEntry)] = _currentScanSignature;

            int sharedSkin = 0;
            int noMask = 0;
            for (int i = 0; i < _candidates.Count; ++i)
            {
                if (_candidates[i].skinReferences > 0)
                    sharedSkin++;
                if (!_candidates[i].hasUsableSkinMask)
                    noMask++;
            }

            var summary = new StringBuilder();
            summary.Append("Scanned ").Append(_renderer.sharedMesh.name).Append(" and found ")
                   .Append(_candidates.Count).Append(" referenced _ColorMap cell(s).");
            if (sharedSkin > 0)
                summary.Append(" ").Append(sharedSkin).Append(" cell(s) are shared with skin and are locked to Ignore.");
            if (noMask > 0)
                summary.Append(" ").Append(noMask).Append(" cell(s) had no usable skin mask; verify those with Highlight.");
            if (!string.IsNullOrWhiteSpace(warning))
                summary.Append(" ").Append(warning);
            _scanReport = summary.ToString();
        }

        private void AutoAssignSuggestions()
        {
            if (_resolvedOption == null)
                return;

            Undo.RecordObject(_profile, "Auto Assign Population Palette Cells");
            var cells = new List<CharacterVisualPaletteCellDefinition>();
            CharacterVisualPaletteCellDefinition[] existing = _resolvedOption.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>();
            for (int i = 0; i < existing.Length; ++i)
                if (existing[i].IsValid && !ContainsCandidateCell(existing[i], onlySuggested: true))
                    cells.Add(existing[i]);

            for (int i = 0; i < _candidates.Count; ++i)
            {
                PaletteCellCandidate candidate = _candidates[i];
                if (!candidate.isSuggested || candidate.skinReferences > 0)
                    continue;
                cells.Add(ToDefinition(candidate, CharacterVisualPaletteChannel.Primary));
            }

            SortCells(cells);
            _resolvedOption.paletteCells = cells.ToArray();
            _resolvedOption.paletteReviewConfirmed = false;
            _resolvedOption.paletteReviewFingerprint = string.Empty;
            _resolvedOption.paletteScanSignature = _currentScanSignature;
            SaveProfile();
        }

        private bool ContainsCandidateCell(CharacterVisualPaletteCellDefinition definition, bool onlySuggested)
        {
            for (int i = 0; i < _candidates.Count; ++i)
            {
                PaletteCellCandidate candidate = _candidates[i];
                if (onlySuggested && !candidate.isSuggested)
                    continue;
                if (SameCell(definition, candidate))
                    return true;
            }
            return false;
        }

        private int GetAssignmentPopupValue(PaletteCellCandidate candidate)
        {
            if (_resolvedOption == null || _resolvedOption.paletteCells == null)
                return 0;

            for (int i = 0; i < _resolvedOption.paletteCells.Length; ++i)
            {
                CharacterVisualPaletteCellDefinition cell = _resolvedOption.paletteCells[i];
                if (!cell.IsValid || !SameCell(cell, candidate))
                    continue;
                return 1 + (int)cell.channel;
            }
            return 0;
        }

        private void SaveAssignment(PaletteCellCandidate candidate, int popupValue)
        {
            if (_resolvedOption == null || _profile == null)
                return;

            Undo.RecordObject(_profile, "Assign Population Palette Cell");
            var cells = new List<CharacterVisualPaletteCellDefinition>(
                _resolvedOption.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>());
            for (int i = cells.Count - 1; i >= 0; --i)
                if (SameCell(cells[i], candidate))
                    cells.RemoveAt(i);

            if (popupValue > 0 && candidate.skinReferences == 0)
            {
                CharacterVisualPaletteChannel channel =
                    (CharacterVisualPaletteChannel)Mathf.Clamp(popupValue - 1, 0, 2);
                cells.Add(ToDefinition(candidate, channel));
            }

            SortCells(cells);
            _resolvedOption.paletteCells = cells.ToArray();
            _resolvedOption.paletteReviewConfirmed = false;
            _resolvedOption.paletteReviewFingerprint = string.Empty;
            _resolvedOption.paletteScanSignature = _currentScanSignature;
            SaveProfile();
        }

        private void SaveProfile()
        {
            if (_profile == null)
                return;
            EditorUtility.SetDirty(_profile);
            _sceneAuthoringDirty = true;
            Repaint();
        }

        private void PreviewSavedDyes()
        {
            RestorePreview();
            if (_renderer == null || _resolvedOption == null)
                return;

            CharacterVisualPaletteCellDefinition[] cells =
                _resolvedOption.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>();
            if (!HasRuntimeDyeCells(cells))
            {
                _scanReport = "Cannot preview dyes: this catalog option has no saved Primary/Secondary palette cells.";
                return;
            }

            SkinnedMeshRenderer previewRenderer = ResolveHighlightRenderer();
            if (previewRenderer == null)
            {
                _scanReport = "Cannot preview dyes: a temporary preview renderer could not be created.";
                return;
            }

            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            if (palette == null || palette.Length == 0)
            {
                DestroyTemporaryPreviewInstance();
                _scanReport = "Cannot preview dyes: the production clothing palette is empty.";
                return;
            }

            Color primary = palette[Mathf.Clamp(_previewPrimaryColorId - 1, 0, palette.Length - 1)];
            Color secondary = palette[Mathf.Clamp(_previewSecondaryColorId - 1, 0, palette.Length - 1)];

            Material[] materials = previewRenderer.sharedMaterials ?? Array.Empty<Material>();
            int materialCount = Mathf.Max(1, materials.Length);
            int previewedSlots = 0;
            int changedCells = 0;

            for (int materialIndex = 0; materialIndex < materialCount; ++materialIndex)
            {
                Material material = GetRendererMaterial(previewRenderer, materialIndex);
                Material paletteMaterial = ResolvePaletteMaterial(material);
                if (paletteMaterial == null || !(paletteMaterial.GetTexture(ColorMapId) is Texture2D source))
                    continue;

                Texture2D copy = CreateReadableTextureCopy(source);
                if (copy == null)
                    continue;

                bool changed = false;
                for (int i = 0; i < cells.Length; ++i)
                {
                    CharacterVisualPaletteCellDefinition cell = cells[i];
                    if (!cell.IsValid ||
                        cell.colorMapWidth != copy.width || cell.colorMapHeight != copy.height ||
                        cell.cellX < 0 || cell.cellX >= copy.width ||
                        cell.cellY < 0 || cell.cellY >= copy.height)
                    {
                        continue;
                    }

                    Color dye;
                    switch (cell.channel)
                    {
                        case CharacterVisualPaletteChannel.Primary:
                            dye = primary;
                            break;
                        case CharacterVisualPaletteChannel.Secondary:
                            dye = secondary;
                            break;
                        default:
                            // Accent and Ignore intentionally preserve authored palette color.
                            continue;
                    }

                    Color original = copy.GetPixel(cell.cellX, cell.cellY);
                    dye.a = original.a;
                    copy.SetPixel(cell.cellX, cell.cellY, dye);
                    changed = true;
                    changedCells++;
                }

                if (!changed)
                {
                    DestroyImmediate(copy);
                    continue;
                }

                copy.Apply(false, true);
                copy.hideFlags = HideFlags.HideAndDontSave;
                copy.name = source.name + "_ClothingDyePreview_M" + materialIndex;

                var originalBlock = new MaterialPropertyBlock();
                previewRenderer.GetPropertyBlock(originalBlock, materialIndex);
                var previewBlock = new MaterialPropertyBlock();
                previewRenderer.GetPropertyBlock(previewBlock, materialIndex);
                previewBlock.SetTexture(ColorMapId, copy);
                previewRenderer.SetPropertyBlock(previewBlock, materialIndex);

                _previewMaterialIndices.Add(materialIndex);
                _previewOriginalBlocks.Add(originalBlock);
                _previewTextures.Add(copy);
                previewedSlots++;
            }

            if (previewedSlots == 0)
            {
                DestroyTemporaryPreviewInstance();
                _scanReport = "Cannot preview dyes: no Character Master _ColorMap matched the saved cell metadata.";
                return;
            }

            _previewRenderer = previewRenderer;
            _previewMaterialIndex = -3;
            _previewCellX = -1;
            _previewCellY = -1;

            if (_previewInstanceRoot != null)
                FrameTemporaryDyePreview(previewRenderer, previewedSlots, changedCells);
            else
                _scanReport =
                    $"Previewing saved clothing dyes across {previewedSlots} Character Master material slot(s). Primary palette id {_previewPrimaryColorId}, Secondary palette id {_previewSecondaryColorId}.";

            SceneView.RepaintAll();
        }

        private void Highlight(PaletteCellCandidate candidate)
        {
            RestorePreview();
            if (_renderer == null)
                return;

            SkinnedMeshRenderer previewRenderer = ResolveHighlightRenderer();
            if (previewRenderer == null)
            {
                _scanReport = "Cannot highlight: a temporary preview renderer could not be created.";
                return;
            }

            Material[] materials = previewRenderer.sharedMaterials ?? Array.Empty<Material>();
            int materialCount = Mathf.Max(1, materials.Length);
            int highlightedSlots = 0;

            for (int materialIndex = 0; materialIndex < materialCount; ++materialIndex)
            {
                Material material = GetRendererMaterial(previewRenderer, materialIndex);
                Material paletteMaterial = ResolvePaletteMaterial(material);
                if (paletteMaterial == null || !(paletteMaterial.GetTexture(ColorMapId) is Texture2D source))
                    continue;

                Texture2D copy = CreateReadableTextureCopy(source);
                if (copy == null || candidate.cellX < 0 || candidate.cellX >= copy.width ||
                    candidate.cellY < 0 || candidate.cellY >= copy.height)
                {
                    if (copy != null)
                        DestroyImmediate(copy);
                    continue;
                }

                Color original = copy.GetPixel(candidate.cellX, candidate.cellY);
                Color highlight = Color.magenta;
                highlight.a = original.a;
                copy.SetPixel(candidate.cellX, candidate.cellY, highlight);
                copy.Apply(false, true);
                copy.hideFlags = HideFlags.HideAndDontSave;
                copy.name = source.name + "_PaletteHighlight_M" + materialIndex;

                var originalBlock = new MaterialPropertyBlock();
                previewRenderer.GetPropertyBlock(originalBlock, materialIndex);
                var previewBlock = new MaterialPropertyBlock();
                previewRenderer.GetPropertyBlock(previewBlock, materialIndex);
                previewBlock.SetTexture(ColorMapId, copy);
                previewRenderer.SetPropertyBlock(previewBlock, materialIndex);

                _previewMaterialIndices.Add(materialIndex);
                _previewOriginalBlocks.Add(originalBlock);
                _previewTextures.Add(copy);
                highlightedSlots++;
            }

            if (highlightedSlots == 0)
            {
                DestroyTemporaryPreviewInstance();
                _scanReport = "Cannot highlight: no Character Master _ColorMap could be resolved on any material slot.";
                return;
            }

            _previewRenderer = previewRenderer;
            // The scanner aggregates indexed geometry across every submesh. Highlight the
            // same palette cell on every material slot that consumes Character Master so
            // multi-material FBX donors do not make valid clothing regions look missing.
            _previewMaterialIndex = -2;
            _previewCellX = candidate.cellX;
            _previewCellY = candidate.cellY;

            if (_previewInstanceRoot != null)
                FrameTemporaryPreview(previewRenderer, highlightedSlots);
            else
                _scanReport =
                    $"Highlighted cell ({_previewCellX}, {_previewCellY}) in magenta across {highlightedSlots} Character Master material slot(s).";

            SceneView.RepaintAll();
        }

        private SkinnedMeshRenderer ResolveHighlightRenderer()
        {
            if (_renderer == null)
                return null;

            // A renderer selected directly from an imported FBX is a persistent asset
            // object. MaterialPropertyBlocks applied to that object have no visible
            // Scene representation, which makes Highlight impossible to verify. Create
            // a temporary, unsaved instance of that same FBX only for visual authoring.
            if (!EditorUtility.IsPersistent(_renderer))
                return _renderer;

            string assetPath = AssetDatabase.GetAssetPath(_renderer);
            if (string.IsNullOrEmpty(assetPath))
                assetPath = AssetDatabase.GetAssetPath(_renderer.sharedMesh);
            if (string.IsNullOrEmpty(assetPath))
                return null;

            GameObject sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (sourceRoot == null)
                return null;

            GameObject instance = PrefabUtility.InstantiatePrefab(sourceRoot) as GameObject;
            if (instance == null)
                instance = Instantiate(sourceRoot);
            if (instance == null)
                return null;

            _previewInstanceRoot = instance;
            _previewInstanceRoot.name = sourceRoot.name + " [Palette Preview - Unsaved]";
            _previewInstanceRoot.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
                _previewInstanceRoot.transform.position = sceneView.pivot;

            SkinnedMeshRenderer[] renderers =
                _previewInstanceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer match = null;
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer candidate = renderers[i];
                if (candidate == null || candidate.sharedMesh != _renderer.sharedMesh)
                    continue;

                if (string.Equals(candidate.name, _renderer.name, StringComparison.Ordinal))
                {
                    match = candidate;
                    break;
                }

                if (match == null)
                    match = candidate;
            }

            if (match == null)
            {
                DestroyTemporaryPreviewInstance();
                return null;
            }

            // Imported modular FBXs can contain more than one renderable child. Only
            // show the exact catalog option being authored so the highlighted region is
            // unambiguous.
            Renderer[] allRenderers = _previewInstanceRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < allRenderers.Length; ++i)
                if (allRenderers[i] != null)
                    allRenderers[i].enabled = allRenderers[i] == match;

            match.enabled = true;
            NormalizeTemporaryPreviewMaterials(match);
            return match;
        }

        private void NormalizeTemporaryPreviewMaterials(SkinnedMeshRenderer previewRenderer)
        {
            if (previewRenderer == null || previewRenderer.sharedMesh == null)
                return;

            // The modular FBXs are mesh donors and commonly carry no production
            // Character Master material (or only an importer Lit placeholder). The old
            // uMMORPG palette authoring preview solved this by normalizing the temporary
            // preview instance onto the same canonical Character Master palette material
            // that runtime character presentation uses. Do the same here; never modify
            // the imported FBX asset itself.
            Material canonical = ResolveCanonicalPaletteMaterial();
            Material[] catalogMaterials = _resolvedOption != null
                ? (_resolvedOption.materials ?? Array.Empty<Material>())
                : Array.Empty<Material>();
            Material[] importedMaterials = previewRenderer.sharedMaterials ?? Array.Empty<Material>();

            int materialCount = Mathf.Max(1, previewRenderer.sharedMesh.subMeshCount);
            var normalized = new Material[materialCount];
            for (int i = 0; i < materialCount; ++i)
            {
                Material candidate = i < catalogMaterials.Length ? catalogMaterials[i] : null;
                if (!HasUsableColorMap(candidate))
                    candidate = i < importedMaterials.Length ? importedMaterials[i] : null;
                if (!HasUsableColorMap(candidate))
                    candidate = canonical;
                normalized[i] = candidate;
            }

            previewRenderer.sharedMaterials = normalized;
        }

        private void FrameTemporaryDyePreview(
            SkinnedMeshRenderer previewRenderer,
            int previewedSlots,
            int changedCells)
        {
            if (previewRenderer == null || _previewInstanceRoot == null)
                return;

            _previewPreviousSelection = Selection.activeObject;
            Selection.activeGameObject = previewRenderer.gameObject;
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.FrameSelected();
                sceneView.Repaint();
            }

            _scanReport =
                $"Previewing saved clothing dyes on a temporary unsaved Scene preview: Primary palette id {_previewPrimaryColorId}, " +
                $"Secondary palette id {_previewSecondaryColorId}, {changedCells} assigned cell application(s) across {previewedSlots} Character Master material slot(s). " +
                "Accent and Ignore remain unchanged, matching current runtime behavior. Use Restore Preview when finished.";
        }

        private void FrameTemporaryPreview(SkinnedMeshRenderer previewRenderer, int highlightedSlots)
        {
            if (previewRenderer == null || _previewInstanceRoot == null)
                return;

            _previewPreviousSelection = Selection.activeObject;
            Selection.activeGameObject = previewRenderer.gameObject;
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.FrameSelected();
                sceneView.Repaint();
            }

            _scanReport =
                $"Highlighted cell ({_previewCellX}, {_previewCellY}) in magenta across {highlightedSlots} Character Master material slot(s) on a temporary unsaved Scene preview. " +
                "The scanner aggregates all submeshes, so Highlight now mirrors that behavior instead of previewing only material slot 0. " +
                "Use Restore Preview or choose another cell when finished.";
        }

        private void RestorePreview()
        {
            if (_previewRenderer != null && _previewInstanceRoot == null)
            {
                int count = Mathf.Min(_previewMaterialIndices.Count, _previewOriginalBlocks.Count);
                for (int i = 0; i < count; ++i)
                    _previewRenderer.SetPropertyBlock(_previewOriginalBlocks[i], _previewMaterialIndices[i]);
                SceneView.RepaintAll();
            }

            for (int i = 0; i < _previewTextures.Count; ++i)
                if (_previewTextures[i] != null)
                    DestroyImmediate(_previewTextures[i]);

            DestroyTemporaryPreviewInstance();

            _previewRenderer = null;
            _previewMaterialIndex = -1;
            _previewMaterialIndices.Clear();
            _previewOriginalBlocks.Clear();
            _previewTextures.Clear();
            _previewCellX = -1;
            _previewCellY = -1;
        }

        private void DestroyTemporaryPreviewInstance()
        {
            if (_previewInstanceRoot == null)
                return;

            bool previewWasSelected = Selection.activeGameObject != null &&
                                      (Selection.activeGameObject == _previewInstanceRoot ||
                                       Selection.activeGameObject.transform.IsChildOf(_previewInstanceRoot.transform));

            // Move the Inspector selection away from the temporary object BEFORE it is
            // destroyed. Destroying a currently-inspected preview caused Unity to try to
            // create SerializedObjects around a MissingReference on the next GUI event.
            if (previewWasSelected)
                Selection.activeObject = _previewPreviousSelection != null ? _previewPreviousSelection : _renderer;

            DestroyImmediate(_previewInstanceRoot);
            _previewInstanceRoot = null;
            _previewPreviousSelection = null;
            SceneView.RepaintAll();
        }

        private string BuildReport()
        {
            var report = new StringBuilder();
            report.AppendLine(_scanReport);
            report.AppendLine("Renderer: " + (_renderer != null ? _renderer.name : "None"));
            report.AppendLine("Catalog: " + (_resolvedOption != null ? _resolvedOption.displayName : "Unresolved"));
            for (int i = 0; i < _candidates.Count; ++i)
            {
                PaletteCellCandidate candidate = _candidates[i];
                int assignment = GetAssignmentPopupValue(candidate);
                string channel = assignment == 0
                    ? "Ignore"
                    : ((CharacterVisualPaletteChannel)(assignment - 1)).ToString();
                report.Append("(").Append(candidate.cellX).Append(", ").Append(candidate.cellY).Append(") ")
                      .Append(channel).Append(" refs=").Append(candidate.totalReferences)
                      .Append(" cloth=").Append(candidate.nonSkinReferences)
                      .Append(" skin=").Append(candidate.skinReferences);
                if (candidate.isSuggested)
                    report.Append(" suggested");
                report.AppendLine();
            }
            return report.ToString();
        }

        private static bool ScanRenderer(
            SkinnedMeshRenderer renderer,
            Dictionary<PaletteCellKey, PaletteCellCandidate> aggregate,
            Dictionary<Texture2D, Texture2D> readableTextureCache,
            out string warning)
        {
            warning = null;
            Material paletteMaterial = null;
            Material[] materials = renderer.sharedMaterials;
            if (materials != null)
            {
                for (int i = 0; i < materials.Length; ++i)
                {
                    if (HasUsableColorMap(materials[i]))
                    {
                        paletteMaterial = materials[i];
                        break;
                    }
                }
            }
            if (paletteMaterial == null)
                paletteMaterial = ResolveCanonicalPaletteMaterial();

            Texture2D colorMap = null;
            Texture2D readableColorMap = null;
            Texture2D skinMask = null;
            Vector2 colorScale = Vector2.one;
            Vector2 colorOffset = Vector2.zero;
            if (paletteMaterial != null && paletteMaterial.GetTexture(ColorMapId) is Texture2D resolvedColorMap)
            {
                colorMap = resolvedColorMap;
                readableColorMap = GetReadableTexture(colorMap, readableTextureCache);
                colorScale = paletteMaterial.GetTextureScale("_ColorMap");
                colorOffset = paletteMaterial.GetTextureOffset("_ColorMap");
                skinMask = ResolveReadableRawSkinMask(paletteMaterial, readableTextureCache);
            }

            int paletteWidth = colorMap != null ? colorMap.width : 32;
            int paletteHeight = colorMap != null ? colorMap.height : 32;
            if (paletteWidth <= 0 || paletteHeight <= 0)
            {
                warning = "Invalid _ColorMap dimensions.";
                return false;
            }

            Mesh.MeshDataArray meshDataArray = default;
            NativeArray<Vector2> uvs = default;
            bool acquired = false;
            try
            {
                meshDataArray = Mesh.AcquireReadOnlyMeshData(renderer.sharedMesh);
                acquired = true;
                Mesh.MeshData data = meshDataArray[0];
                if (data.vertexCount <= 0)
                {
                    warning = "Mesh has no vertices.";
                    return false;
                }

                uvs = new NativeArray<Vector2>(data.vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                data.GetUVs(0, uvs);
                if (uvs.Length == 0)
                {
                    warning = "UV0 is missing.";
                    return false;
                }

                bool hasSkinMask = skinMask != null;
                int referencedVertices = 0;
                for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
                {
                    SubMeshDescriptor descriptor = data.GetSubMesh(submesh);
                    if (descriptor.indexCount <= 0)
                        continue;

                    NativeArray<int> indices = default;
                    try
                    {
                        indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                        data.GetIndices(indices, submesh, true);
                        for (int i = 0; i < indices.Length; ++i)
                        {
                            int vertex = indices[i];
                            if (vertex < 0 || vertex >= uvs.Length)
                                continue;

                            Vector2 uv = uvs[vertex];
                            if (!IsFinite(uv.x) || !IsFinite(uv.y))
                                continue;

                            Vector2 transformed = Vector2.Scale(uv, colorScale) + colorOffset;
                            int x = TextureCoordinateToCell(
                                transformed.x,
                                paletteWidth,
                                colorMap != null ? colorMap.wrapModeU : TextureWrapMode.Clamp);
                            int y = TextureCoordinateToCell(
                                transformed.y,
                                paletteHeight,
                                colorMap != null ? colorMap.wrapModeV : TextureWrapMode.Clamp);

                            var key = new PaletteCellKey(paletteWidth, paletteHeight, x, y);
                            if (!aggregate.TryGetValue(key, out PaletteCellCandidate candidate))
                            {
                                Color sourceColor = Color.gray;
                                if (readableColorMap != null && x < readableColorMap.width && y < readableColorMap.height)
                                    sourceColor = readableColorMap.GetPixel(x, y);

                                candidate = new PaletteCellCandidate
                                {
                                    colorMapWidth = paletteWidth,
                                    colorMapHeight = paletteHeight,
                                    cellX = x,
                                    cellY = y,
                                    sourceColor = sourceColor,
                                };
                                aggregate.Add(key, candidate);
                            }

                            candidate.totalReferences++;
                            referencedVertices++;
                            if (hasSkinMask)
                            {
                                candidate.hasUsableSkinMask = true;
                                if (SampleRawSkinMask(skinMask, transformed) > 0.5f)
                                    candidate.nonSkinReferences++;
                                else
                                    candidate.skinReferences++;
                            }
                            else
                            {
                                candidate.nonSkinReferences++;
                            }
                        }
                    }
                    finally
                    {
                        if (indices.IsCreated)
                            indices.Dispose();
                    }
                }

                if (referencedVertices == 0)
                {
                    warning = "UV0 was present, but no indexed vertices could be scanned.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                warning = "Direct UV0 palette scan failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (uvs.IsCreated)
                    uvs.Dispose();
                if (acquired)
                    meshDataArray.Dispose();
            }
        }

        private static void MarkSuggestions(List<PaletteCellCandidate> results)
        {
            PaletteCellCandidate best = null;
            for (int i = 0; i < results.Count; ++i)
            {
                PaletteCellCandidate candidate = results[i];
                candidate.isSuggested = false;
                if (candidate.skinReferences > 0)
                    continue;

                int score = candidate.nonSkinReferences > 0 ? candidate.nonSkinReferences : candidate.totalReferences;
                if (best == null)
                {
                    best = candidate;
                    continue;
                }

                int bestScore = best.nonSkinReferences > 0 ? best.nonSkinReferences : best.totalReferences;
                if (score > bestScore || (score == bestScore && CompareCandidates(candidate, best) < 0))
                    best = candidate;
            }

            if (best != null)
                best.isSuggested = true;
        }

        private static int CompareCandidates(PaletteCellCandidate a, PaletteCellCandidate b)
        {
            int byHeight = a.colorMapHeight.CompareTo(b.colorMapHeight);
            if (byHeight != 0) return byHeight;
            int byWidth = a.colorMapWidth.CompareTo(b.colorMapWidth);
            if (byWidth != 0) return byWidth;
            int byY = a.cellY.CompareTo(b.cellY);
            return byY != 0 ? byY : a.cellX.CompareTo(b.cellX);
        }

        private static void SortCells(List<CharacterVisualPaletteCellDefinition> cells)
        {
            cells.Sort((a, b) =>
            {
                int byChannel = a.channel.CompareTo(b.channel);
                if (byChannel != 0) return byChannel;
                int byHeight = a.colorMapHeight.CompareTo(b.colorMapHeight);
                if (byHeight != 0) return byHeight;
                int byWidth = a.colorMapWidth.CompareTo(b.colorMapWidth);
                if (byWidth != 0) return byWidth;
                int byY = a.cellY.CompareTo(b.cellY);
                return byY != 0 ? byY : a.cellX.CompareTo(b.cellX);
            });
        }

        private static CharacterVisualPaletteCellDefinition ToDefinition(
            PaletteCellCandidate candidate,
            CharacterVisualPaletteChannel channel) =>
            new CharacterVisualPaletteCellDefinition
            {
                colorMapWidth = candidate.colorMapWidth,
                colorMapHeight = candidate.colorMapHeight,
                cellX = candidate.cellX,
                cellY = candidate.cellY,
                channel = channel,
            };

        private static bool SameCell(CharacterVisualPaletteCellDefinition cell, PaletteCellCandidate candidate) =>
            cell.colorMapWidth == candidate.colorMapWidth &&
            cell.colorMapHeight == candidate.colorMapHeight &&
            cell.cellX == candidate.cellX &&
            cell.cellY == candidate.cellY;

        private static bool HasUsableColorMap(Material material) =>
            material != null && material.HasProperty(ColorMapId) && material.GetTexture(ColorMapId) is Texture2D;

        private static Material ResolvePaletteMaterial(Material preferred) =>
            HasUsableColorMap(preferred) ? preferred : ResolveCanonicalPaletteMaterial();

        private static Material ResolveCanonicalPaletteMaterial()
        {
            if (HasUsableColorMap(_cachedCanonicalPaletteMaterial))
                return _cachedCanonicalPaletteMaterial;
            if (_canonicalPaletteSearchPerformed)
                return null;

            _canonicalPaletteSearchPerformed = true;
            Material best = null;
            int bestScore = int.MinValue;
            ScoreMaterialAssets(AssetDatabase.FindAssets("Starter_01 t:Material"), ref best, ref bestScore);
            if (best == null)
                ScoreMaterialAssets(AssetDatabase.FindAssets("t:Material"), ref best, ref bestScore);
            _cachedCanonicalPaletteMaterial = best;
            return best;
        }

        private static void ScoreMaterialAssets(string[] guids, ref Material best, ref int bestScore)
        {
            if (guids == null)
                return;

            for (int i = 0; i < guids.Length; ++i)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                Material material = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!HasUsableColorMap(material))
                    continue;

                int score = 0;
                if (material.shader != null && string.Equals(material.shader.name, "Custom/URP/Character Master", StringComparison.Ordinal))
                    score += 100000;
                if (string.Equals(material.name, "Starter_01", StringComparison.OrdinalIgnoreCase))
                    score += 10000;
                if (material.HasProperty(SkinMaskId) && material.GetTexture(SkinMaskId) != null)
                    score += 1000;
                if (material.HasProperty(PackedMaskId) && material.GetTexture(PackedMaskId) != null)
                    score += 500;

                if (best == null || score > bestScore ||
                    (score == bestScore && string.Compare(path, AssetDatabase.GetAssetPath(best), StringComparison.OrdinalIgnoreCase) < 0))
                {
                    best = material;
                    bestScore = score;
                }
            }
        }

        private static Material GetRendererMaterial(Renderer renderer, int materialIndex)
        {
            Material[] materials = renderer != null ? renderer.sharedMaterials : null;
            return materials != null && materialIndex >= 0 && materialIndex < materials.Length
                ? materials[materialIndex]
                : null;
        }

        private static Texture2D ResolveReadableRawSkinMask(
            Material material,
            Dictionary<Texture2D, Texture2D> cache)
        {
            if (material == null)
                return null;

            Texture2D source = null;
            if (material.HasProperty(UsePackedMasksId) && material.GetFloat(UsePackedMasksId) > 0.5f && material.HasProperty(PackedMaskId))
                source = material.GetTexture(PackedMaskId) as Texture2D;
            if (source == null && material.HasProperty(SkinMaskId))
                source = material.GetTexture(SkinMaskId) as Texture2D;
            return GetReadableTexture(source, cache);
        }

        private static Texture2D GetReadableTexture(Texture2D source, Dictionary<Texture2D, Texture2D> cache)
        {
            if (source == null)
                return null;
            if (cache.TryGetValue(source, out Texture2D existing))
                return existing;
            Texture2D copy = CreateReadableTextureCopy(source);
            cache[source] = copy;
            return copy;
        }

        private static float SampleRawSkinMask(Texture2D mask, Vector2 uv)
        {
            if (mask == null)
                return 1f;
            float u = WrapCoordinate(uv.x, mask.wrapModeU);
            float v = WrapCoordinate(uv.y, mask.wrapModeV);
            return mask.GetPixelBilinear(u, v).r;
        }

        private static int TextureCoordinateToCell(float coordinate, int size, TextureWrapMode wrap)
        {
            float value = WrapCoordinate(coordinate, wrap);
            return Mathf.Clamp(Mathf.FloorToInt(value * Mathf.Max(1, size)), 0, Mathf.Max(1, size) - 1);
        }

        private static float WrapCoordinate(float coordinate, TextureWrapMode wrap)
        {
            switch (wrap)
            {
                case TextureWrapMode.Repeat:
                    return Mathf.Repeat(coordinate, 1f);
                case TextureWrapMode.Mirror:
                case TextureWrapMode.MirrorOnce:
                    return Mathf.PingPong(coordinate, 1f);
                default:
                    return Mathf.Clamp(coordinate, 0f, 0.999999f);
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static Texture2D CreateReadableTextureCopy(Texture2D source)
        {
            if (source == null)
                return null;

            RenderTexture temporary = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false)
                {
                    filterMode = source.filterMode,
                    wrapModeU = source.wrapModeU,
                    wrapModeV = source.wrapModeV,
                    anisoLevel = source.anisoLevel,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0, false);
                copy.Apply(false, false);
                return copy;
            }
            catch
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null)
                    RenderTexture.ReleaseTemporary(temporary);
            }
        }
    }
}
#endif
