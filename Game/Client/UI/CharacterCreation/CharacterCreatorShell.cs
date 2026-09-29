using System;
using System.Collections.Generic;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.CharacterCreation
{
    /// <summary>
    /// Client-only data-driven character creator. The production UI hierarchy and styling
    /// are authored in ClientUIRoot.prefab. Runtime code only binds authored controls,
    /// instantiates authored templates for dynamic catalog entries, and drives state.
    /// </summary>
    public sealed class CharacterCreatorShell : MonoBehaviour
    {
        public const int AuthoredUiSchemaVersion = 4;

        public event Action Cancelled;
        public event Action<string, CharacterAppearanceRecipe, CharacterPresentationPreferences> CreateRequested;

        [SerializeField, HideInInspector] private int authoredUiVersion;

        [Header("Authored Groups")]
        [SerializeField] private GameObject catalogGroup;
        [SerializeField] private GameObject movementGroup;
        [SerializeField] private GameObject subcategoryGroup;
        [SerializeField] private GameObject colorPanel;

        [Header("Authored Dynamic Containers")]
        [SerializeField] private RectTransform subcategoryContent;
        [SerializeField] private RectTransform catalogContent;
        [SerializeField] private ScrollRect catalogScroll;
        [SerializeField] private RectTransform colorContent;

        [Header("Authored Static Controls")]
        [SerializeField] private Text categoryTitle;
        [SerializeField] private Text statusText;
        [SerializeField] private InputField nameInput;
        [SerializeField] private Slider movementSlider;
        [SerializeField] private Button createButton;
        [SerializeField] private Button backButton;
        [SerializeField] private Button bodyButton;
        [SerializeField] private Button faceButton;
        [SerializeField] private Button hairButton;
        [SerializeField] private Button movementButton;
        [SerializeField] private Button idlePreviewButton;
        [SerializeField] private Button walkPreviewButton;
        [SerializeField] private Button runPreviewButton;
        [SerializeField] private CharacterSelectPreviewController preview;

        [Header("Authored Dynamic Templates")]
        [SerializeField] private CharacterCreatorSubcategoryView subcategoryTemplate;
        [SerializeField] private CharacterCreatorMeshGroupView meshGroupTemplate;
        [SerializeField] private CharacterCreatorMeshOptionView meshOptionTemplate;
        [SerializeField] private CharacterCreatorMorphRowView morphRowTemplate;
        [SerializeField] private CharacterCreatorColorChannelView colorChannelTemplate;
        [SerializeField] private CharacterCreatorColorSwatchView colorSwatchTemplate;
        [SerializeField] private CharacterCreatorInfoRowView infoRowTemplate;

        private CharacterVisualProfile _profile;
        private CharacterAppearanceRecipe _appearance;
        private CharacterPresentationPreferences _presentation;
        private CharacterCreatorCategory _activeCategory;
        private ushort _activeFaceSlotId;
        private readonly List<Selectable> _dynamicSelectables = new List<Selectable>();
        private readonly List<MeshChoiceBinding> _meshChoiceBindings = new List<MeshChoiceBinding>();
        private readonly List<ColorChoiceBinding> _colorChoiceBindings = new List<ColorChoiceBinding>();
        private readonly List<SubcategoryBinding> _subcategoryBindings = new List<SubcategoryBinding>();
        private readonly List<MorphBinding> _morphBindings = new List<MorphBinding>();
        private bool _busy;
        private bool _initialized;
        private bool _draftDirty;
        private double _draftSaveAt;

        private const string DraftPreferenceKey = "MMO.CharacterCreator.Draft.V1";
        private const double DraftSaveDebounceSeconds = 0.50d;

        [Serializable]
        private sealed class CharacterCreatorDraft
        {
            public int schemaVersion = 1;
            public string name = string.Empty;
            public CharacterAppearanceRecipe appearance;
            public CharacterPresentationPreferences presentation;
        }

        private sealed class MeshChoiceBinding
        {
            public ushort slotId;
            public ushort optionId;
            public CharacterCreatorMeshOptionView view;
        }

        private sealed class ColorChoiceBinding
        {
            public ushort channelId;
            public int paletteIndex;
            public CharacterCreatorColorSwatchView view;
        }

        private sealed class SubcategoryBinding
        {
            public ushort slotId;
            public CharacterCreatorSubcategoryView view;
        }

        private sealed class MorphBinding
        {
            public ushort channelId;
            public CharacterCreatorMorphRowView view;
        }

        public bool IsOpen => gameObject.activeSelf;

        /// <summary>
        /// True when at least one requested direct-sculpt semantic is available in the
        /// currently loaded visual profile. This deliberately checks the same profile
        /// definitions used by the authored slider UI; direct manipulation is not a second system.
        /// </summary>
        public bool CanSculpt(string horizontalSemantic, string verticalSemantic)
        {
            if (_busy || _profile == null || _appearance == null)
                return false;
            return (TryFindMorph(horizontalSemantic, out CharacterVisualMorphDefinition horizontal) && horizontal.category == _activeCategory) ||
                   (TryFindMorph(verticalSemantic, out CharacterVisualMorphDefinition vertical) && vertical.category == _activeCategory);
        }

        /// <summary>
        /// Resolves the first semantic candidate supported by the active profile/category.
        /// Sculpt regions use candidate lists so one direct-manipulation surface can adapt to
        /// Synty bone channels or richer high-poly blendshape channels without a second UI.
        /// </summary>
        public string ResolveFirstSupportedSculptSemantic(string[] semanticCandidates)
        {
            if (_busy || _profile == null || _appearance == null || semanticCandidates == null)
                return string.Empty;

            for (int i = 0; i < semanticCandidates.Length; ++i)
            {
                string candidate = semanticCandidates[i];
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                if (TryFindMorph(candidate, out CharacterVisualMorphDefinition morph) && morph.category == _activeCategory)
                    return candidate;
            }

            return string.Empty;
        }

        /// <summary>
        /// Adjust a semantic creator channel by packed-byte delta. Used by the stage drag
        /// surface so viewport sculpting and sliders write the same CharacterMorphSelection.
        /// </summary>
        public bool AdjustMorphBySemantic(string semanticName, float packedDelta)
        {
            if (_busy || _appearance == null || !TryFindMorph(semanticName, out CharacterVisualMorphDefinition morph) ||
                morph.category != _activeCategory)
                return false;

            int current = GetMorphSelection(morph.channelId, morph.defaultValue);
            int next = Mathf.Clamp(Mathf.RoundToInt(current + packedDelta), 0, 255);
            if (next == current)
                return true;

            SetMorphSelection(morph.channelId, (byte)next);
            RefreshMorphBinding(morph.channelId, (byte)next);
            preview?.ApplyWorkingAppearance(_appearance);
            MarkDraftDirty();
            return true;
        }

        public void Initialize()
        {
            if (_initialized)
                return;

            if (!HasAuthoredSurface())
            {
                Debug.LogError(
                    "[CharacterCreator] Authored Character Creator UI is incomplete. Restore the canonical " +
                    "ClientUIRoot.prefab instead of running a UI rebuild/migration tool.",
                    this);
                enabled = false;
                return;
            }

            _initialized = true;
            bodyButton.onClick.AddListener(() => ShowCategory(CharacterCreatorCategory.Body));
            faceButton.onClick.AddListener(() => ShowCategory(CharacterCreatorCategory.Face));
            hairButton.onClick.AddListener(() => ShowCategory(CharacterCreatorCategory.Hair));
            movementButton.onClick.AddListener(() => ShowCategory(CharacterCreatorCategory.Movement));
            backButton.onClick.AddListener(OnBackClicked);
            createButton.onClick.AddListener(OnCreateClicked);
            nameInput.onValueChanged.AddListener(OnNameChanged);
            movementSlider.onValueChanged.AddListener(OnMovementStyleChanged);
            idlePreviewButton.onClick.AddListener(() => preview?.PreviewIdle());
            walkPreviewButton.onClick.AddListener(() => preview?.PreviewWalk());
            runPreviewButton.onClick.AddListener(() => preview?.PreviewRun());

            CloseImmediate();
        }

        public void Open()
        {
            if (!_initialized)
                Initialize();
            if (!_initialized)
                return;

            ushort creatorProfileId = preview != null ? preview.ResolveCreatorVisualProfileId() : (ushort)0;
            if (!CharacterVisualProfileRegistry.TryResolve(creatorProfileId, out _profile))
                _profile = CharacterVisualProfileRegistry.Default;

            bool restoredDraft = TryLoadDraft(
                out string draftName,
                out CharacterAppearanceRecipe draftAppearance,
                out CharacterPresentationPreferences draftPresentation);

            if (restoredDraft && _profile != null && draftAppearance.visualProfileId != _profile.VisualProfileId)
                draftAppearance = CharacterVisualProfileRegistry.RemapRecipeToProfile(draftAppearance, _profile);

            _appearance = restoredDraft
                ? draftAppearance
                : (_profile != null ? _profile.CreateDefaultRecipe() : CharacterAppearanceRecipe.CreateDefault());
            SanitizeAppearanceMorphsForActiveProfile();
            _presentation = restoredDraft
                ? draftPresentation
                : CharacterPresentationPreferences.CreateDefault();
            _busy = false;
            _draftDirty = false;

            nameInput.SetTextWithoutNotify(restoredDraft ? draftName : string.Empty);
            movementSlider.SetValueWithoutNotify(_presentation.movementStyle / 255f);

            gameObject.SetActive(true);
            preview.ShowWorkingCharacter(
                restoredDraft && !string.IsNullOrWhiteSpace(draftName) ? draftName : "New Character",
                _appearance,
                _presentation);
            ShowCategory(CharacterCreatorCategory.Body);

            SetStatus(_profile != null
                ? (restoredDraft
                    ? $"Local draft restored for visual profile {_profile.VisualProfileId}. Only supported controls are shown; the final compact recipe is sent on Create Character."
                    : $"Visual profile {_profile.VisualProfileId}: only controls supported by this model are shown. Only the final compact recipe is sent on Create Character.")
                : "Character Visual Catalog is unavailable. Rebuild it from MMO Tools/Characters/Authoring/Rebuild Character Visual Catalog.");
            RefreshInteractable();
        }

        public void Close()
        {
            if (_busy)
                return;
            SaveDraftNow();
            CloseImmediate();
        }

        public void SetBusy(bool busy, string status = null)
        {
            _busy = busy;
            if (!string.IsNullOrWhiteSpace(status))
                SetStatus(status);
            RefreshInteractable();
        }

        public void SetError(string error)
        {
            _busy = false;
            SetStatus(string.IsNullOrWhiteSpace(error) ? "Character creation failed." : error);
            RefreshInteractable();
        }

        public void CompleteAndClose(bool clearDraft = false)
        {
            _busy = false;
            if (clearDraft)
                ClearDraft();
            else
                SaveDraftNow();
            CloseImmediate();
        }


        private bool HasAuthoredSurface()
        {
            return authoredUiVersion >= AuthoredUiSchemaVersion &&
                   catalogGroup != null && movementGroup != null && subcategoryGroup != null && colorPanel != null &&
                   subcategoryContent != null && catalogContent != null && catalogScroll != null && colorContent != null &&
                   categoryTitle != null && statusText != null && nameInput != null && movementSlider != null &&
                   createButton != null && backButton != null && bodyButton != null && faceButton != null &&
                   hairButton != null && movementButton != null && idlePreviewButton != null &&
                   walkPreviewButton != null && runPreviewButton != null && preview != null &&
                   subcategoryTemplate != null && meshGroupTemplate != null && meshOptionTemplate != null &&
                   morphRowTemplate != null && colorChannelTemplate != null && colorSwatchTemplate != null &&
                   infoRowTemplate != null;
        }

        private void CloseImmediate()
        {
            preview?.ClearPreview();
            if (gameObject.activeSelf)
                gameObject.SetActive(false);
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (_draftDirty && Time.realtimeSinceStartupAsDouble >= _draftSaveAt)
                SaveDraftNow();
#endif
        }

        private void ShowCategory(CharacterCreatorCategory category)
        {
            if (categoryTitle == null)
                return;

            _activeCategory = category;
            bool movement = category == CharacterCreatorCategory.Movement;
            categoryTitle.text = movement ? "STANCE / GAIT" : category.ToString().ToUpperInvariant();
            catalogGroup.SetActive(!movement);
            movementGroup.SetActive(movement);
            preview?.FocusCreatorCategory(category);

            if (movement)
            {
                subcategoryGroup.SetActive(false);
                colorPanel.SetActive(false);
                return;
            }

            ConfigureSubcategories(category);
            RebuildCatalogCategory(category, true);
            RebuildColorPanel(category);
        }

        private void ConfigureSubcategories(CharacterCreatorCategory category)
        {
            RemoveDynamicSelectablesUnder(subcategoryContent);
            ClearChildren(subcategoryContent);
            _subcategoryBindings.Clear();
            _activeFaceSlotId = 0;

            if (category != CharacterCreatorCategory.Face || _profile == null)
            {
                subcategoryGroup.SetActive(false);
                return;
            }

            subcategoryGroup.SetActive(true);
            AddSubcategoryButton("SHAPE", 0);

            IReadOnlyList<CharacterVisualSlotDefinition> slots = _profile.Slots;
            for (int i = 0; i < slots.Count; ++i)
            {
                CharacterVisualSlotDefinition slot = slots[i];
                if (slot == null || slot.category != CharacterCreatorCategory.Face ||
                    !slot.mirrorPrimary || slot.equipmentDriven)
                    continue;

                // Fixed head/teeth/tongue are not player-facing. Eyes remains useful even
                // when its mesh is fixed because its palette is an authored creator choice.
                if (slot.slotId == 1 || slot.slotId == 36 || slot.slotId == 37)
                    continue;
                if (!slot.IsPublishedChooser && slot.slotId != 5)
                    continue;

                AddSubcategoryButton(slot.displayName.ToUpperInvariant(), slot.slotId);
            }
        }

        private void AddSubcategoryButton(string label, ushort slotId)
        {
            CharacterCreatorSubcategoryView view = Instantiate(subcategoryTemplate, subcategoryContent);
            view.name = "Subcategory_" + slotId;
            view.gameObject.SetActive(true);
            view.Bind(label, slotId == _activeFaceSlotId);
            view.Button.onClick.AddListener(() => SelectFaceSubcategory(slotId));
            _subcategoryBindings.Add(new SubcategoryBinding { slotId = slotId, view = view });
            _dynamicSelectables.Add(view.Button);
        }

        private void SelectFaceSubcategory(ushort slotId)
        {
            if (_busy || _activeCategory != CharacterCreatorCategory.Face || _activeFaceSlotId == slotId)
                return;

            _activeFaceSlotId = slotId;
            RefreshSubcategorySelection();
            RebuildCatalogCategory(CharacterCreatorCategory.Face, true);
            RebuildColorPanel(CharacterCreatorCategory.Face);
        }

        private void RefreshSubcategorySelection()
        {
            for (int i = 0; i < _subcategoryBindings.Count; ++i)
            {
                SubcategoryBinding binding = _subcategoryBindings[i];
                binding.view?.SetSelected(binding.slotId == _activeFaceSlotId);
            }
        }

        private void RebuildCatalogCategory(CharacterCreatorCategory category, bool resetScroll)
        {
            RemoveDynamicSelectablesUnder(catalogContent);
            ClearChildren(catalogContent);
            _meshChoiceBindings.Clear();
            _morphBindings.Clear();

            if (_profile == null)
            {
                AddInfoRow("Character Visual Catalog is not available. Use MMO Tools/Characters/Authoring/Rebuild Character Visual Catalog.");
                return;
            }

            int controls = 0;
            if (category == CharacterCreatorCategory.Body ||
                (category == CharacterCreatorCategory.Face && _activeFaceSlotId == 0))
            {
                IReadOnlyList<CharacterVisualMorphDefinition> morphs = _profile.Morphs;
                for (int i = 0; i < morphs.Count; ++i)
                {
                    CharacterVisualMorphDefinition morph = morphs[i];
                    if (morph == null || morph.category != category ||
                        !morph.visibleInCreator || !morph.persistInAppearance)
                        continue;

                    // Structural channels that can be manipulated directly on the current
                    // model are intentionally not duplicated as rows of sliders. The same
                    // CharacterMorphSelection is edited either way; this is only a cleaner
                    // presentation choice. Global/body-type controls and unsupported direct
                    // channels remain available as normal sliders.
                    if (preview != null && preview.SupportsDirectSculptSemantic(morph.semanticName))
                        continue;

                    AddMorphSlider(morph);
                    controls++;
                }
            }

            IReadOnlyList<CharacterVisualSlotDefinition> slots = _profile.Slots;
            for (int i = 0; i < slots.Count; ++i)
            {
                CharacterVisualSlotDefinition slot = slots[i];
                if (slot == null || slot.category != category ||
                    !slot.mirrorPrimary || slot.equipmentDriven)
                    continue;

                if (category == CharacterCreatorCategory.Face)
                {
                    if (_activeFaceSlotId == 0 || slot.slotId != _activeFaceSlotId)
                        continue;
                    if (!slot.IsPublishedChooser)
                        continue;
                }
                else if (!slot.IsPublishedChooser)
                {
                    continue;
                }

                if (AddMeshOptionGrid(slot))
                    controls++;
            }

            bool hasDirectSculpt = preview != null && preview.HasDirectSculptForCategory(category);
            if (hasDirectSculpt)
            {
                AddInfoRow("LMB + drag directly on the character to adjust supported features. Drag empty preview space to orbit the camera. Mouse wheel zooms.");
                controls++;
            }

            if (controls == 0)
                AddInfoRow("This view has no published appearance choices in the current visual catalog.");

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(catalogContent);
            if (resetScroll && catalogScroll != null)
                catalogScroll.verticalNormalizedPosition = 1f;
            RefreshInteractable();
        }

        private bool AddMeshOptionGrid(CharacterVisualSlotDefinition slot)
        {
            if (slot == null)
                return false;

            bool premium = CharacterCreatorAccessPolicy.HasPremiumAccess;
            Game.Shared.Actors.ActorFaction faction = CharacterCreatorAccessPolicy.Faction;
            var options = new List<CharacterVisualOptionDefinition>();
            CharacterVisualOptionDefinition[] source = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
            for (int i = 0; i < source.Length; ++i)
            {
                CharacterVisualOptionDefinition option = source[i];
                if (option != null && option.IsSelectableFor(faction, premium))
                    options.Add(option);
            }

            int choiceCount = options.Count + (slot.allowNone ? 1 : 0);
            if (choiceCount <= 1)
                return false;

            CharacterCreatorMeshGroupView group = Instantiate(meshGroupTemplate, catalogContent);
            group.name = "Mesh_" + slot.slotId;
            group.gameObject.SetActive(true);
            if (group.Label != null)
                group.Label.text = slot.displayName;

            if (slot.allowNone)
                CreateMeshTile(group.OptionContent, slot, null);
            for (int i = 0; i < options.Count; ++i)
                CreateMeshTile(group.OptionContent, slot, options[i]);
            return true;
        }

        private void CreateMeshTile(
            Transform parent,
            CharacterVisualSlotDefinition slot,
            CharacterVisualOptionDefinition option)
        {
            ushort optionId = option != null ? option.optionId : (ushort)0;
            string label = option != null ? option.displayName : "None";
            CharacterCreatorMeshOptionView view = Instantiate(meshOptionTemplate, parent);
            view.name = "Option_" + optionId;
            view.gameObject.SetActive(true);
            bool selected = GetMeshSelection(slot.slotId, slot.defaultOptionId) == optionId;
            view.Bind(option != null ? option.thumbnail : null, CompactOptionLabel(label), selected);
            view.Button.onClick.AddListener(() => SelectMeshOption(slot, optionId));
            _meshChoiceBindings.Add(new MeshChoiceBinding
            {
                slotId = slot.slotId,
                optionId = optionId,
                view = view,
            });
            _dynamicSelectables.Add(view.Button);
        }

        private void SelectMeshOption(CharacterVisualSlotDefinition slot, ushort optionId)
        {
            if (_busy || slot == null || _appearance == null)
                return;

            SetMeshSelection(slot.slotId, optionId);
            if (slot.mirrorSlotId != 0)
            {
                if (optionId == 0)
                    SetMeshSelection(slot.mirrorSlotId, 0);
                else if (_profile.TryGetMirrorOption(slot, optionId, out ushort mirrorOption))
                    SetMeshSelection(slot.mirrorSlotId, mirrorOption);
            }

            preview?.ApplyWorkingAppearance(_appearance);
            MarkDraftDirty();
            // Do not rebuild the catalog here. Rebuilding was the cause of the ScrollRect
            // snapping back to the top whenever a player selected an option.
            RefreshMeshSelection(slot.slotId);
        }

        private void RefreshMeshSelection(ushort slotId)
        {
            if (_profile == null || !_profile.TryGetSlot(slotId, out CharacterVisualSlotDefinition slot) || slot == null)
                return;
            ushort selected = GetMeshSelection(slotId, slot.defaultOptionId);
            for (int i = 0; i < _meshChoiceBindings.Count; ++i)
            {
                MeshChoiceBinding binding = _meshChoiceBindings[i];
                if (binding.slotId == slotId)
                    binding.view?.SetSelected(binding.optionId == selected);
            }
        }

        private static string CompactOptionLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Option";
            string[] words = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 2)
                return value;
            return words[words.Length - 2] + " " + words[words.Length - 1];
        }

        private void AddMorphSlider(CharacterVisualMorphDefinition morph)
        {
            CharacterCreatorMorphRowView view = Instantiate(morphRowTemplate, catalogContent);
            view.name = "Morph_" + morph.channelId;
            view.gameObject.SetActive(true);
            if (view.Label != null)
                view.Label.text = morph.displayName;

            Slider slider = view.Slider;
            if (slider == null)
                return;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;
            byte current = GetMorphSelection(morph.channelId, morph.defaultValue);
            slider.SetValueWithoutNotify(current / 255f);
            if (view.ValueText != null)
                view.ValueText.text = Mathf.RoundToInt(slider.value * 100f) + "%";

            slider.onValueChanged.AddListener(value =>
            {
                if (_busy)
                    return;
                byte packed = (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
                SetMorphSelection(morph.channelId, packed);
                if (view.ValueText != null)
                    view.ValueText.text = Mathf.RoundToInt(value * 100f) + "%";
                preview?.ApplyWorkingAppearance(_appearance);
                MarkDraftDirty();
            });
            _morphBindings.Add(new MorphBinding { channelId = morph.channelId, view = view });
            _dynamicSelectables.Add(slider);
        }

        private bool TryFindMorph(string semanticName, out CharacterVisualMorphDefinition morph)
        {
            morph = null;
            if (_profile == null || string.IsNullOrWhiteSpace(semanticName))
                return false;

            IReadOnlyList<CharacterVisualMorphDefinition> values = _profile.Morphs;
            for (int i = 0; i < values.Count; ++i)
            {
                CharacterVisualMorphDefinition candidate = values[i];
                if (candidate == null || !candidate.visibleInCreator || !candidate.persistInAppearance)
                    continue;
                if (!string.Equals(candidate.semanticName, semanticName, StringComparison.OrdinalIgnoreCase))
                    continue;
                morph = candidate;
                return true;
            }
            return false;
        }

        private void RefreshMorphBinding(ushort channelId, byte packed)
        {
            for (int i = 0; i < _morphBindings.Count; ++i)
            {
                MorphBinding binding = _morphBindings[i];
                if (binding.channelId != channelId || binding.view == null || binding.view.Slider == null)
                    continue;
                float value = packed / 255f;
                binding.view.Slider.SetValueWithoutNotify(value);
                if (binding.view.ValueText != null)
                    binding.view.ValueText.text = Mathf.RoundToInt(value * 100f) + "%";
            }
        }

        private void RebuildColorPanel(CharacterCreatorCategory category)
        {
            RemoveDynamicSelectablesUnder(colorContent);
            ClearChildren(colorContent);
            _colorChoiceBindings.Clear();

            if (_profile == null)
            {
                colorPanel.SetActive(false);
                return;
            }

            int channels = 0;
            IReadOnlyList<CharacterVisualColorDefinition> colors = _profile.Colors;
            for (int i = 0; i < colors.Count; ++i)
            {
                CharacterVisualColorDefinition color = colors[i];
                if (color == null || color.category != category)
                    continue;

                if (category == CharacterCreatorCategory.Face)
                {
                    if (_activeFaceSlotId != 5 ||
                        !string.Equals(color.semanticName, "eyes", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                AddColorChannel(color);
                channels++;
            }

            colorPanel.SetActive(channels > 0);
            if (channels > 0)
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(colorContent);
            }
            RefreshInteractable();
        }

        private void AddColorChannel(CharacterVisualColorDefinition color)
        {
            CharacterCreatorColorChannelView channel = Instantiate(colorChannelTemplate, colorContent);
            channel.name = "Color_" + color.channelId;
            channel.gameObject.SetActive(true);
            if (channel.Label != null)
                channel.Label.text = color.displayName;

            CreatePaletteButton(channel.SwatchContent, color, 0, Color.clear);
            uint[] swatches = color.suggestedRgba ?? Array.Empty<uint>();
            for (int i = 0; i < swatches.Length; ++i)
            {
                int paletteIndex = i + 1;
                CreatePaletteButton(
                    channel.SwatchContent,
                    color,
                    paletteIndex,
                    ModularCharacterAppearancePresenter.UnpackRgba(swatches[i]));
            }
        }

        private void CreatePaletteButton(
            Transform parent,
            CharacterVisualColorDefinition color,
            int paletteIndex,
            Color swatchColor)
        {
            CharacterCreatorColorSwatchView view = Instantiate(colorSwatchTemplate, parent);
            view.name = paletteIndex == 0 ? "Original" : "Swatch" + paletteIndex;
            view.gameObject.SetActive(true);
            int selectedIndex = GetColorPaletteIndex(color.channelId);
            view.Bind(
                swatchColor,
                paletteIndex == 0 ? "ORIGINAL" : string.Empty,
                selectedIndex == paletteIndex,
                paletteIndex != 0);
            view.Button.onClick.AddListener(() => SelectColor(color, paletteIndex));
            _colorChoiceBindings.Add(new ColorChoiceBinding
            {
                channelId = color.channelId,
                paletteIndex = paletteIndex,
                view = view,
            });
            _dynamicSelectables.Add(view.Button);
        }

        private void SelectColor(CharacterVisualColorDefinition color, int paletteIndex)
        {
            if (_busy || color == null)
                return;

            if (paletteIndex <= 0)
                RemoveColorSelection(color.channelId);
            else
                SetColorSelection(color.channelId, CharacterColorEncoding.PaletteIndex, (uint)paletteIndex);

            preview?.ApplyWorkingAppearance(_appearance);
            MarkDraftDirty();
            // Colors are deliberately independent of the selected hairstyle/mesh. Updating
            // color never rebuilds the option list and changing hairstyle never resets color.
            RefreshColorSelection(color.channelId);
        }

        private void RefreshColorSelection(ushort channelId)
        {
            int selected = GetColorPaletteIndex(channelId);
            for (int i = 0; i < _colorChoiceBindings.Count; ++i)
            {
                ColorChoiceBinding binding = _colorChoiceBindings[i];
                if (binding.channelId == channelId)
                    binding.view?.SetSelected(binding.paletteIndex == selected);
            }
        }

        private int GetColorPaletteIndex(ushort channelId)
        {
            CharacterColorSelection[] values = _appearance?.colors ?? Array.Empty<CharacterColorSelection>();
            for (int i = 0; i < values.Length; ++i)
            {
                CharacterColorSelection value = values[i];
                if (value.channelId != channelId)
                    continue;
                if (value.encoding != CharacterColorEncoding.PaletteIndex || value.value == 0)
                    return 0;
                return Mathf.Clamp((int)value.value, 1, byte.MaxValue);
            }
            return 0;
        }

        private void AddInfoRow(string message)
        {
            CharacterCreatorInfoRowView view = Instantiate(infoRowTemplate, catalogContent);
            view.name = "Info";
            view.gameObject.SetActive(true);
            if (view.MessageText != null)
                view.MessageText.text = message ?? string.Empty;
        }

        private ushort GetMeshSelection(ushort slotId, ushort fallback)
        {
            CharacterMeshSelection[] values = _appearance?.meshes ?? Array.Empty<CharacterMeshSelection>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].slotId == slotId)
                    return values[i].optionId;
            }
            return fallback;
        }

        private void SetMeshSelection(ushort slotId, ushort optionId)
        {
            var values = new List<CharacterMeshSelection>(_appearance?.meshes ?? Array.Empty<CharacterMeshSelection>());
            for (int i = 0; i < values.Count; ++i)
            {
                if (values[i].slotId != slotId)
                    continue;
                values[i] = new CharacterMeshSelection(slotId, optionId);
                _appearance.meshes = values.ToArray();
                return;
            }
            values.Add(new CharacterMeshSelection(slotId, optionId));
            values.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            _appearance.meshes = values.ToArray();
        }

        private byte GetMorphSelection(ushort channelId, byte fallback)
        {
            CharacterMorphSelection[] values = _appearance?.morphs ?? Array.Empty<CharacterMorphSelection>();
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].channelId == channelId)
                    return values[i].value;
            }
            return fallback;
        }

        private void SetMorphSelection(ushort channelId, byte value)
        {
            var values = new List<CharacterMorphSelection>(_appearance?.morphs ?? Array.Empty<CharacterMorphSelection>());
            for (int i = 0; i < values.Count; ++i)
            {
                if (values[i].channelId != channelId)
                    continue;
                values[i] = new CharacterMorphSelection(channelId, value);
                _appearance.morphs = values.ToArray();
                return;
            }
            values.Add(new CharacterMorphSelection(channelId, value));
            values.Sort((a, b) => a.channelId.CompareTo(b.channelId));
            _appearance.morphs = values.ToArray();
        }

        private void SetColorSelection(ushort channelId, CharacterColorEncoding encoding, uint value)
        {
            var values = new List<CharacterColorSelection>(_appearance?.colors ?? Array.Empty<CharacterColorSelection>());
            for (int i = 0; i < values.Count; ++i)
            {
                if (values[i].channelId != channelId)
                    continue;
                values[i] = new CharacterColorSelection(channelId, encoding, value);
                _appearance.colors = values.ToArray();
                return;
            }
            values.Add(new CharacterColorSelection(channelId, encoding, value));
            values.Sort((a, b) => a.channelId.CompareTo(b.channelId));
            _appearance.colors = values.ToArray();
        }

        private void RemoveColorSelection(ushort channelId)
        {
            var values = new List<CharacterColorSelection>(_appearance?.colors ?? Array.Empty<CharacterColorSelection>());
            values.RemoveAll(value => value.channelId == channelId);
            _appearance.colors = values.ToArray();
        }

        private void OnMovementStyleChanged(float value)
        {
            if (_presentation == null)
                _presentation = CharacterPresentationPreferences.CreateDefault();
            _presentation.movementStyle = (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
            preview?.ApplyWorkingPresentation(_presentation);
            MarkDraftDirty();
        }

        private void OnBackClicked()
        {
            if (_busy)
                return;
            SaveDraftNow();
            CloseImmediate();
            Cancelled?.Invoke();
        }

        private void OnNameChanged(string value)
        {
            if (preview != null && IsOpen)
                preview.SetPreviewDisplayName(string.IsNullOrWhiteSpace(value) ? "New Character" : value);
            MarkDraftDirty();
            RefreshInteractable();
        }

        private void MarkDraftDirty()
        {
            if (_busy || !IsOpen)
                return;
            _draftDirty = true;
            _draftSaveAt = Time.realtimeSinceStartupAsDouble + DraftSaveDebounceSeconds;
        }

        private void SaveDraftNow()
        {
            if (!_initialized || _appearance == null || _presentation == null)
                return;
            if (!_appearance.IsValid(out _) || !_presentation.IsValid(out _))
                return;

            var draft = new CharacterCreatorDraft
            {
                schemaVersion = 1,
                name = CharacterNamePolicy.Normalize(nameInput != null ? nameInput.text : string.Empty),
                appearance = _appearance.Clone(),
                presentation = _presentation.Clone(),
            };
            PlayerPrefs.SetString(DraftPreferenceKey, JsonUtility.ToJson(draft));
            _draftDirty = false;
        }

        private static bool TryLoadDraft(
            out string name,
            out CharacterAppearanceRecipe appearance,
            out CharacterPresentationPreferences presentation)
        {
            name = string.Empty;
            appearance = null;
            presentation = null;
            string json = PlayerPrefs.GetString(DraftPreferenceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                CharacterCreatorDraft draft = JsonUtility.FromJson<CharacterCreatorDraft>(json);
                if (draft == null || draft.schemaVersion != 1 || draft.appearance == null || draft.presentation == null ||
                    !draft.appearance.IsValid(out _) || !draft.presentation.IsValid(out _))
                {
                    ClearDraft();
                    return false;
                }

                name = CharacterNamePolicy.Normalize(draft.name);
                appearance = draft.appearance.Clone();
                presentation = draft.presentation.Clone();
                return true;
            }
            catch
            {
                ClearDraft();
                return false;
            }
        }

        private static void ClearDraft()
        {
            PlayerPrefs.DeleteKey(DraftPreferenceKey);
        }

        private void OnCreateClicked()
        {
            if (_busy)
                return;

            string name = CharacterNamePolicy.Normalize(nameInput != null ? nameInput.text : string.Empty);
            if (!CharacterNamePolicy.IsAllowed(name))
            {
                SetStatus("Name must be 1-16 letters with optional single spaces.");
                RefreshInteractable();
                return;
            }

            SanitizeAppearanceMorphsForActiveProfile();
            CharacterAppearanceRecipe appearance = _appearance?.Clone() ??
                                                   CharacterVisualProfileRegistry.CreateDefaultRecipeOrFallback();
            CharacterPresentationPreferences presentation =
                _presentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault();
            CreateRequested?.Invoke(name, appearance, presentation);
        }

        private void SanitizeAppearanceMorphsForActiveProfile()
        {
            if (_appearance == null || _profile == null)
                return;

            // A profile is a capability map, not ownership of the shared semantic recipe.
            // Preserve channels that this model does not know so changing model families later
            // does not erase appearance intent. Only remove a channel when the active profile
            // explicitly defines it as non-persistent/internal.
            IReadOnlyList<CharacterVisualMorphDefinition> definitions = _profile.Morphs;
            var explicitlyNonPersistent = new HashSet<ushort>();
            for (int i = 0; i < definitions.Count; ++i)
            {
                CharacterVisualMorphDefinition definition = definitions[i];
                if (definition != null && !definition.persistInAppearance)
                    explicitlyNonPersistent.Add(definition.channelId);
            }

            CharacterMorphSelection[] source = _appearance.morphs ?? Array.Empty<CharacterMorphSelection>();
            var filtered = new List<CharacterMorphSelection>(Mathf.Min(source.Length, CharacterAppearanceRecipe.MaxMorphSelections));
            bool changed = false;
            for (int i = 0; i < source.Length && filtered.Count < CharacterAppearanceRecipe.MaxMorphSelections; ++i)
            {
                CharacterMorphSelection value = source[i];
                if (value.channelId == 0 || explicitlyNonPersistent.Contains(value.channelId))
                {
                    changed = true;
                    continue;
                }
                filtered.Add(value);
            }

            if (filtered.Count != source.Length)
                changed = true;
            if (changed)
                _appearance.morphs = filtered.ToArray();
        }

        private void RefreshInteractable()
        {
            if (createButton != null)
            {
                string name = CharacterNamePolicy.Normalize(nameInput != null ? nameInput.text : string.Empty);
                createButton.interactable = !_busy && CharacterNamePolicy.IsAllowed(name);
            }
            if (backButton != null)
                backButton.interactable = !_busy;
            if (nameInput != null)
                nameInput.interactable = !_busy;
            if (movementSlider != null)
                movementSlider.interactable = !_busy;

            for (int i = 0; i < _dynamicSelectables.Count; ++i)
            {
                if (_dynamicSelectables[i] != null)
                    _dynamicSelectables[i].interactable = !_busy;
            }
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
                statusText.text = string.IsNullOrWhiteSpace(message) ? " " : message;
        }

        private void RemoveDynamicSelectablesUnder(Transform parent)
        {
            if (parent == null)
                return;
            _dynamicSelectables.RemoveAll(selectable =>
                selectable == null || selectable.transform == null || selectable.transform.IsChildOf(parent));
        }

        private static void ClearChildren(Transform parent)
        {
            if (parent == null)
                return;
            for (int i = parent.childCount - 1; i >= 0; --i)
            {
                GameObject child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }
        }
    }
}
