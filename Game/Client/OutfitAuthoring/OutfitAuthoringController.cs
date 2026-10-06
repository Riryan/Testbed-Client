using System;
using System.Collections.Generic;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    [Serializable]
    public sealed class OutfitAuthoringEquipmentSlotOption
    {
        public ushort dataId;
        public string slotId;
        public string displayName;
        public int order;
        public ushort presentationSlotId;

        public string Label => string.IsNullOrWhiteSpace(displayName)
            ? (slotId ?? "Equipment Slot")
            : displayName;
    }

    [Serializable]
    public sealed class OutfitAuthoringSaveRequest
    {
        public string itemName;
        public string equipmentSlotId;
        public string equipmentSlotDisplayName;
        public ushort equipmentSlotPresentationId;
        public CharacterWearableRegion visualRegion = CharacterWearableRegion.Upper;
        public CharacterWearablePartSelection[] parts = Array.Empty<CharacterWearablePartSelection>();
        public ushort[] allowedColorIds = Array.Empty<ushort>();
        public ushort defaultPrimaryColorId;
        public ushort defaultSecondaryColorId;
        public bool updateExisting;
        public string existingDefinitionId;
        public ushort existingPresentationId;
    }

    public readonly struct OutfitAuthoringSaveResult
    {
        public readonly bool success;
        public readonly string message;
        public readonly string definitionId;
        public readonly ushort presentationId;
        public readonly bool updatedExisting;

        public OutfitAuthoringSaveResult(
            bool success,
            string message,
            string definitionId = "",
            ushort presentationId = 0,
            bool updatedExisting = false)
        {
            this.success = success;
            this.message = message ?? string.Empty;
            this.definitionId = definitionId ?? string.Empty;
            this.presentationId = presentationId;
            this.updatedExisting = updatedExisting;
        }
    }

    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringController : MonoBehaviour
    {
        public static Func<OutfitAuthoringSaveRequest, OutfitAuthoringSaveResult> EditorSaveHandler;
        public static Func<OutfitAuthoringEquipmentSlotOption[]> EditorEquipmentSlotProvider;
        public static Func<CharacterWearablePartSelection[], int> EditorEnsurePrimaryDyeHandler;

        [Header("Preview")]
        [SerializeField] private Transform previewAnchor;
        [SerializeField] private Camera previewCamera;
        [Tooltip("When disabled, the Outfit Builder never moves the authored preview camera or changes its FOV.")]
        [SerializeField] private bool autoFramePreviewCamera = false;
        [Tooltip("The spawned Outfit Authoring Player Animator defaults off. Enable only when animation preview is wanted.")]
        [SerializeField] private bool previewAnimatorEnabled = false;

        [Header("Item")]
        [SerializeField] private InputField itemNameInput;
        [SerializeField] private OutfitAuthoringDropdown equipmentSlotDropdown;
        [SerializeField] private OutfitAuthoringDropdown visualLayerDropdown;

        [Header("Meshes")]
        [SerializeField] private OutfitAuthoringSlotRow[] slotRows = Array.Empty<OutfitAuthoringSlotRow>();

        [Header("Colors")]
        [SerializeField] private OutfitAuthoringColorCell[] colorCells = Array.Empty<OutfitAuthoringColorCell>();
        [SerializeField] private Text previewColorText;

        [Header("Actions")]
        [SerializeField] private Button saveButton;
        [SerializeField] private Text statusText;

        private sealed class SlotState
        {
            public OutfitAuthoringSlotRow row;
            public CharacterVisualSlotDefinition slot;
            public readonly List<CharacterVisualOptionDefinition> options = new List<CharacterVisualOptionDefinition>();
            public int optionIndex = -1;
        }

        private static readonly ushort[] PreviewPrimaryColorChannels =
        {
            SidekickCharacterPaletteUtility.ClothingPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.UpperPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.LowerPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.FeetPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.AccessoryPrimaryColorChannelId,
        };

        private static readonly ushort[] PreviewSecondaryColorChannels =
        {
            SidekickCharacterPaletteUtility.ClothingSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.UpperSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.LowerSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.FeetSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.AccessorySecondaryColorChannelId,
        };

        private readonly List<SlotState> _slotStates = new List<SlotState>();
        private readonly List<OutfitAuthoringEquipmentSlotOption> _equipmentSlots = new List<OutfitAuthoringEquipmentSlotOption>();
        private readonly HashSet<ushort> _allowedColorIds = new HashSet<ushort>();
        private readonly List<ushort> _availableColorIds = new List<ushort>();

        private Text _compactColorNameText;
        private Button _compactColorUseButton;
        private Button _compactColorPrimaryButton;
        private Button _compactColorSecondaryButton;
        private Image _compactColorSwatch;
        private int _compactColorIndex;
        private ushort _testPrimaryColorId;
        private ushort _testSecondaryColorId;

        private Vector3 _previewOrbitTarget;
        private float _previewOrbitDistance;
        private float _previewOrbitYaw;
        private float _previewOrbitPitch;
        private bool _previewOrbitReady;
        private bool _previewDragging;
        private Vector3 _previewLastMousePosition;
        private Vector3 _previewInitialPosition;
        private Quaternion _previewInitialRotation;
        private float _previewInitialFov;

        private CharacterVisualProfile _profile;
        private CharacterAppearanceRecipe _recipe;
        private GameObject _mannequin;
        private ModularCharacterAppearancePresenter _presenter;
        private ushort _previewColorId;
        private string _editingDefinitionId = string.Empty;
        private ushort _editingPresentationId;

        public bool IsEditingExisting => !string.IsNullOrWhiteSpace(_editingDefinitionId);
        public string EditingDefinitionId => _editingDefinitionId;
        public ushort EditingPresentationId => _editingPresentationId;
        public event Action AuthoringModeChanged;
        public event Action<OutfitAuthoringSaveResult> Saved;

        private void Start()
        {
            _profile = CharacterVisualProfileRegistry.Default;
            if (_profile == null || _profile.EditableBasePrefab == null)
            {
                SetStatus("CharacterVisualProfile or EditableBasePrefab is missing.", false);
                return;
            }

            _recipe = _profile.CreateDefaultRecipe();
            LoadEquipmentSlots();
            BuildMannequin();
            BindMeshRows();
            BindColors();
            BuildCompactColorUi();
            BindItemControls();
            BindSave();
            CapturePreviewOrbit();
            ApplyPreview();
            RefreshSaveLabel();
            SetStatus("Ready - New Item", true);
        }

        private void OnDestroy()
        {
            if (_mannequin != null)
                Destroy(_mannequin);
        }

        private void Update()
        {
            UpdatePreviewInteraction();
        }

        private void LoadEquipmentSlots()
        {
            _equipmentSlots.Clear();
            OutfitAuthoringEquipmentSlotOption[] values = EditorEquipmentSlotProvider != null
                ? EditorEquipmentSlotProvider.Invoke()
                : Array.Empty<OutfitAuthoringEquipmentSlotOption>();

            if (values == null)
                return;

            for (int i = 0; i < values.Length; ++i)
            {
                OutfitAuthoringEquipmentSlotOption option = values[i];
                if (option != null && !string.IsNullOrWhiteSpace(option.slotId))
                    _equipmentSlots.Add(option);
            }

            _equipmentSlots.Sort((a, b) =>
            {
                int byOrder = a.order.CompareTo(b.order);
                if (byOrder != 0) return byOrder;
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
        }

        private void BuildMannequin()
        {
            Vector3 position = previewAnchor != null ? previewAnchor.position : Vector3.zero;
            Quaternion rotation = previewAnchor != null ? previewAnchor.rotation : Quaternion.identity;

            _mannequin = Instantiate(_profile.EditableBasePrefab, position, rotation);
            _mannequin.name = "Outfit Authoring Player";
            if (previewAnchor != null)
                _mannequin.transform.SetParent(previewAnchor, true);

            _presenter = _mannequin.GetComponent<ModularCharacterAppearancePresenter>();
            if (_presenter == null)
                _presenter = _mannequin.AddComponent<ModularCharacterAppearancePresenter>();
            _presenter.Configure(_profile);
            _presenter.ApplyAppearance(_recipe);

            Animator animator = _mannequin.GetComponentInChildren<Animator>(true);
            if (animator != null)
                animator.enabled = previewAnimatorEnabled;

            // Preserve the authored camera by default. This lets the scene camera be moved,
            // rotated and have its FOV adjusted normally without Play Mode overwriting it.
            if (autoFramePreviewCamera && previewCamera != null)
                FramePreviewCamera();
        }

        private void FramePreviewCamera()
        {
            if (previewCamera == null || _mannequin == null)
                return;

            Bounds bounds = CalculateBounds(_mannequin);
            Vector3 center = bounds.center;
            float height = Mathf.Max(1.6f, bounds.size.y);

            // Optional convenience framing only. Includes extra vertical room so feet/head
            // are not tight to the viewport edges. The normal/default mode does not call this.
            float distance = Mathf.Max(3.8f, height * 2.0f);
            previewCamera.transform.position =
                center + new Vector3(0f, 0f, distance);
            previewCamera.transform.LookAt(center);
            previewCamera.nearClipPlane = 0.05f;
            previewCamera.fieldOfView = 40f;
        }

        private void BindMeshRows()
        {
            _slotStates.Clear();
            OutfitAuthoringSlotRow[] rows = slotRows ?? Array.Empty<OutfitAuthoringSlotRow>();
            for (int i = 0; i < rows.Length; ++i)
            {
                OutfitAuthoringSlotRow row = rows[i];
                if (row == null || row.PrimarySlotId == 0)
                    continue;

                if (!_profile.TryGetSlot(row.PrimarySlotId, out CharacterVisualSlotDefinition slot) || slot == null)
                {
                    row.SetAvailable(false);
                    continue;
                }

                var state = new SlotState { row = row, slot = slot };
                CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                for (int o = 0; o < options.Length; ++o)
                {
                    CharacterVisualOptionDefinition option = options[o];
                    if (option != null && option.optionId != 0 && option.mesh != null && option.AllowsPlayerUse)
                        state.options.Add(option);
                }

                state.options.Sort((a, b) => string.Compare(
                    DisplayOptionName(a),
                    DisplayOptionName(b),
                    StringComparison.OrdinalIgnoreCase));

                row.SetAvailable(true);
                row.Bind(
                    () => CycleSlot(state, -1),
                    () => CycleSlot(state, 1));
                _slotStates.Add(state);
                RefreshSlotLabel(state);
            }
        }

        private void BindColors()
        {
            _allowedColorIds.Clear();
            _availableColorIds.Clear();

            HashSet<ushort> available = BuildAvailableColorSet();
            foreach (ushort id in available)
                _availableColorIds.Add(id);
            _availableColorIds.Sort();

            // The old swatch grid is intentionally hidden. The compact Color Setup box
            // below Mesh Parts uses this same canonical palette data.
            OutfitAuthoringColorCell[] cells = colorCells ?? Array.Empty<OutfitAuthoringColorCell>();
            for (int i = 0; i < cells.Length; ++i)
                if (cells[i] != null)
                    cells[i].gameObject.SetActive(false);

            _compactColorIndex = 0;
            _testPrimaryColorId = 0;
            _testSecondaryColorId = 0;
        }

        private void BindItemControls()
        {
            if (equipmentSlotDropdown != null)
            {
                var labels = new List<string>(_equipmentSlots.Count);
                for (int i = 0; i < _equipmentSlots.Count; ++i)
                    labels.Add(_equipmentSlots[i].Label);
                equipmentSlotDropdown.SetOptions(labels, labels.Count > 0 ? 0 : -1);
            }

            if (visualLayerDropdown != null)
            {
                visualLayerDropdown.SetOptions(new[]
                {
                    "Upper / Shirt / Coat",
                    "Lower / Pants",
                    "Feet / Boots / Shoes",
                    "Accessory / Gloves",
                }, 0);
                visualLayerDropdown.SelectionChanged += _ => ApplyPreview();
            }
        }

        private void BindSave()
        {
            if (saveButton != null)
            {
                saveButton.onClick.RemoveListener(SaveOutfit);
                saveButton.onClick.AddListener(SaveOutfit);
            }

            Transform clearTransform = FindChildRecursive(
                transform.root,
                "ClearMeshSelectionsButton");

            if (clearTransform != null)
            {
                Button clearButton = clearTransform.GetComponent<Button>();
                if (clearButton != null)
                {
                    clearButton.onClick.RemoveListener(
                        ClearMeshSelectionsToDefaultBase);
                    clearButton.onClick.AddListener(
                        ClearMeshSelectionsToDefaultBase);
                }
            }
        }

        private void CycleSlot(SlotState state, int delta)
        {
            if (state == null)
                return;

            int choiceCount = state.options.Count + 1;
            if (choiceCount <= 1)
                return;

            int currentChoice = state.optionIndex + 1;
            int next = (currentChoice + delta) % choiceCount;
            if (next < 0)
                next += choiceCount;

            state.optionIndex = next - 1;
            RefreshSlotLabel(state);
            ApplyPreview();
        }

        private void RefreshSlotLabel(SlotState state)
        {
            if (state?.row == null)
                return;

            if (state.optionIndex < 0 || state.optionIndex >= state.options.Count)
            {
                state.row.SetValue("Default Base");
                return;
            }

            CharacterVisualOptionDefinition option = state.options[state.optionIndex];
            state.row.SetValue($"{DisplayOptionName(option)}   [{state.optionIndex + 1}/{state.options.Count}]");
        }

        private static string DisplayOptionName(CharacterVisualOptionDefinition option)
        {
            if (option == null)
                return "Missing Mesh";
            if (!string.IsNullOrWhiteSpace(option.displayName))
                return option.displayName;
            return option.mesh != null ? option.mesh.name : "Mesh";
        }

        private void PreviewColor(ushort colorId)
        {
            if (colorId == 0)
                return;

            _previewColorId = colorId;
            if (previewColorText != null)
                previewColorText.text = $"Preview: {colorId:00}  {SidekickCharacterPaletteUtility.GetClothingPaletteName(colorId)}";

            OutfitAuthoringColorCell[] cells = colorCells ?? Array.Empty<OutfitAuthoringColorCell>();
            for (int i = 0; i < cells.Length; ++i)
                if (cells[i] != null)
                    cells[i].SetPreviewSelected(cells[i].ColorId == colorId);

            ApplyPreview();
        }

        private void ToggleColorAllowed(ushort colorId)
        {
            if (colorId == 0)
                return;

            bool allowed;
            if (_allowedColorIds.Contains(colorId))
            {
                _allowedColorIds.Remove(colorId);
                allowed = false;
            }
            else
            {
                _allowedColorIds.Add(colorId);
                allowed = true;
            }

            OutfitAuthoringColorCell[] cells = colorCells ?? Array.Empty<OutfitAuthoringColorCell>();
            for (int i = 0; i < cells.Length; ++i)
                if (cells[i] != null && cells[i].ColorId == colorId)
                    cells[i].SetAllowed(allowed);
        }

        private void ApplyPreview()
        {
            if (_profile == null || _presenter == null)
                return;

            _recipe = _profile.CreateDefaultRecipe();
            List<CharacterWearablePartSelection> parts = BuildPartSelections();
            for (int i = 0; i < parts.Count; ++i)
                SetRecipeMesh(_recipe, parts[i].slotId, parts[i].optionId);

            // Wearable-wide color preview: one Primary value is pushed through every
            // canonical primary clothing channel, and one Secondary value through every
            // canonical secondary channel. This prevents a multi-mesh outfit from showing
            // hips in one default color and legs in another while authoring.
            if (_testPrimaryColorId > 0)
            {
                for (int i = 0; i < PreviewPrimaryColorChannels.Length; ++i)
                    SetRecipeColor(_recipe, PreviewPrimaryColorChannels[i], _testPrimaryColorId);
            }

            if (_testSecondaryColorId > 0)
            {
                for (int i = 0; i < PreviewSecondaryColorChannels.Length; ++i)
                    SetRecipeColor(_recipe, PreviewSecondaryColorChannels[i], _testSecondaryColorId);
            }

            _presenter.ApplyAppearance(_recipe);
        }

        private List<CharacterWearablePartSelection> BuildPartSelections()
        {
            var parts = new List<CharacterWearablePartSelection>(14);
            for (int i = 0; i < _slotStates.Count; ++i)
            {
                SlotState state = _slotStates[i];
                if (state.optionIndex < 0 || state.optionIndex >= state.options.Count)
                    continue;

                CharacterVisualOptionDefinition option = state.options[state.optionIndex];
                parts.Add(new CharacterWearablePartSelection(state.row.PrimarySlotId, option.optionId));

                CharacterVisualSlotDefinition slot = state.slot;
                if (slot != null && slot.mirrorSlotId != 0 &&
                    _profile.TryGetMirrorOption(slot, option.optionId, out ushort mirrorOptionId) &&
                    mirrorOptionId != 0)
                {
                    parts.Add(new CharacterWearablePartSelection(slot.mirrorSlotId, mirrorOptionId));
                }
            }

            parts.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            return parts;
        }

        private void SaveOutfit()
        {
            if (EditorSaveHandler == null)
            {
                SetStatus("Editor save bridge is not loaded. Open this scene inside the Unity Editor.", false);
                return;
            }

            string itemName = itemNameInput != null ? (itemNameInput.text ?? string.Empty).Trim() : string.Empty;
            if (itemName.Length == 0)
            {
                SetStatus("Name the item before saving.", false);
                return;
            }

            int selectedEquipmentIndex = equipmentSlotDropdown != null ? equipmentSlotDropdown.SelectedIndex : -1;
            if (selectedEquipmentIndex < 0 || selectedEquipmentIndex >= _equipmentSlots.Count)
            {
                SetStatus("No server Equipment Slot is selected. Configure the Server Content Root first.", false);
                return;
            }

            OutfitAuthoringEquipmentSlotOption selectedSlot = _equipmentSlots[selectedEquipmentIndex];
            var allowed = new List<ushort>(_allowedColorIds);
            allowed.Sort();

            int visualIndex = visualLayerDropdown != null ? visualLayerDropdown.SelectedIndex : 0;
            CharacterWearableRegion region = (CharacterWearableRegion)Mathf.Clamp(visualIndex, 0, 3);

            OutfitAuthoringSaveResult result = EditorSaveHandler(new OutfitAuthoringSaveRequest
            {
                itemName = itemName,
                equipmentSlotId = selectedSlot.slotId,
                equipmentSlotDisplayName = selectedSlot.displayName,
                equipmentSlotPresentationId = selectedSlot.presentationSlotId,
                visualRegion = region,
                parts = BuildPartSelections().ToArray(),
                allowedColorIds = allowed.ToArray(),
                defaultPrimaryColorId = _testPrimaryColorId,
                defaultSecondaryColorId = _testSecondaryColorId,
                updateExisting = IsEditingExisting,
                existingDefinitionId = _editingDefinitionId,
                existingPresentationId = _editingPresentationId,
            });

            if (result.success && !string.IsNullOrWhiteSpace(result.definitionId) && result.presentationId != 0)
            {
                _editingDefinitionId = result.definitionId;
                _editingPresentationId = result.presentationId;
                RefreshSaveLabel();
                AuthoringModeChanged?.Invoke();
            }

            SetStatus(result.message, result.success);
            Saved?.Invoke(result);
        }

        // Integrity overlay load hook. This is merged into the existing controller so the
        // overlay can rehydrate an already-authored item without inventing another path.
        /// <summary>
        /// Clears all explicit wearable mesh selections back to the profile's canonical
        /// Default Base state. The next save payload contains no stale prior-item mesh parts.
        /// </summary>
        public void ClearMeshSelectionsToDefaultBase()
        {
            if (_profile == null || _presenter == null)
            {
                SetStatus("Cannot clear meshes: preview is not initialized.", false);
                return;
            }

            for (int i = 0; i < _slotStates.Count; ++i)
            {
                SlotState state = _slotStates[i];
                if (state == null)
                    continue;

                state.optionIndex = -1;
                RefreshSlotLabel(state);
            }

            // Rebuild from the canonical default recipe directly so the mannequin and
            // authoring state cannot retain a previously loaded wearable.
            _recipe = _profile.CreateDefaultRecipe();
            _presenter.ApplyAppearance(_recipe);

            SetStatus(
                "Mesh selections cleared to Default Base. Previous item mesh parts will not be saved.",
                true);
        }

        public void BeginNewItem()
        {
            _editingDefinitionId = string.Empty;
            _editingPresentationId = 0;

            if (itemNameInput != null)
                itemNameInput.text = string.Empty;
            if (equipmentSlotDropdown != null)
                equipmentSlotDropdown.Select(_equipmentSlots.Count > 0 ? 0 : -1);
            if (visualLayerDropdown != null)
                visualLayerDropdown.Select(0);

            for (int i = 0; i < _slotStates.Count; ++i)
            {
                _slotStates[i].optionIndex = -1;
                RefreshSlotLabel(_slotStates[i]);
            }

            _allowedColorIds.Clear();
            _testPrimaryColorId = 0;
            _testSecondaryColorId = 0;
            _compactColorIndex = 0;
            _previewColorId = 0;
            RefreshCompactColorUi();

            ApplyPreview();
            RefreshSaveLabel();
            SetStatus("New Item mode.", true);
            AuthoringModeChanged?.Invoke();
        }

        public bool LoadExistingServerItem(
            string definitionId,
            string savedItemDisplayName,
            ushort existingPresentationId,
            string equipmentSlotId,
            ushort equipmentSlotPresentationId,
            CharacterWearableSetDefinition wearable)
        {
            if (_profile == null || _presenter == null ||
                string.IsNullOrWhiteSpace(definitionId))
                return false;

            _editingDefinitionId = definitionId.Trim();
            _editingPresentationId = existingPresentationId;

            if (itemNameInput != null)
                itemNameInput.text = !string.IsNullOrWhiteSpace(savedItemDisplayName)
                    ? savedItemDisplayName
                    : _editingDefinitionId;

            if (equipmentSlotDropdown != null)
            {
                int selected = -1;
                for (int i = 0; i < _equipmentSlots.Count; ++i)
                {
                    OutfitAuthoringEquipmentSlotOption option = _equipmentSlots[i];
                    if (option == null)
                        continue;

                    if ((!string.IsNullOrWhiteSpace(equipmentSlotId) &&
                         string.Equals(option.slotId, equipmentSlotId, StringComparison.Ordinal)) ||
                        (equipmentSlotPresentationId != 0 &&
                         option.presentationSlotId == equipmentSlotPresentationId))
                    {
                        selected = i;
                        break;
                    }
                }

                equipmentSlotDropdown.Select(selected);
            }

            CharacterWearableRegion region =
                wearable != null
                    ? wearable.region
                    : GuessRegionForEquipmentSlot(equipmentSlotId);

            if (visualLayerDropdown != null)
                visualLayerDropdown.Select(Mathf.Clamp((int)region, 0, 3));

            CharacterWearablePartSelection[] parts =
                wearable?.parts ?? Array.Empty<CharacterWearablePartSelection>();

            for (int i = 0; i < _slotStates.Count; ++i)
            {
                SlotState state = _slotStates[i];
                state.optionIndex = -1;

                ushort wanted = 0;
                for (int p = 0; p < parts.Length; ++p)
                {
                    if (parts[p].slotId == state.row.PrimarySlotId)
                    {
                        wanted = parts[p].optionId;
                        break;
                    }
                }

                if (wanted != 0)
                {
                    for (int o = 0; o < state.options.Count; ++o)
                    {
                        CharacterVisualOptionDefinition option = state.options[o];
                        if (option != null && option.optionId == wanted)
                        {
                            state.optionIndex = o;
                            break;
                        }
                    }
                }

                RefreshSlotLabel(state);
            }

            _allowedColorIds.Clear();
            if (wearable != null)
            {
                ushort[] allowedColors = wearable.playerAllowedColorIds ?? Array.Empty<ushort>();
                for (int i = 0; i < allowedColors.Length; ++i)
                    if (allowedColors[i] != 0)
                        _allowedColorIds.Add(allowedColors[i]);

                _testPrimaryColorId = wearable.defaultPrimaryColorId;
                _testSecondaryColorId = wearable.defaultSecondaryColorId;
                _previewColorId = _testPrimaryColorId;

                int preferred = _availableColorIds.IndexOf(_testPrimaryColorId);
                if (preferred < 0 && _allowedColorIds.Count > 0)
                {
                    foreach (ushort id in _allowedColorIds)
                    {
                        preferred = _availableColorIds.IndexOf(id);
                        if (preferred >= 0)
                            break;
                    }
                }
                _compactColorIndex = preferred >= 0 ? preferred : 0;
            }
            else
            {
                _testPrimaryColorId = 0;
                _testSecondaryColorId = 0;
                _previewColorId = 0;
                _compactColorIndex = 0;
            }

            RefreshCompactColorUi();
            ApplyPreview();
            RefreshSaveLabel();

            SetStatus(
                wearable != null
                    ? $"Editing existing server item {_editingDefinitionId} | presentation {wearable.presentationId}."
                    : $"Editing existing server item {_editingDefinitionId} | no presentation yet. UPDATE EXISTING will attach one.",
                true);

            AuthoringModeChanged?.Invoke();
            return true;
        }

        public bool LoadExistingWearable(
            CharacterWearableSetDefinition wearable,
            string equipmentSlotId,
            ushort equipmentSlotPresentationId,
            string savedItemDisplayName = null)
        {
            if (wearable == null)
                return false;

            return LoadExistingServerItem(
                wearable.definitionId,
                savedItemDisplayName,
                wearable.presentationId,
                equipmentSlotId,
                equipmentSlotPresentationId,
                wearable);
        }

        private static CharacterWearableRegion GuessRegionForEquipmentSlot(
            string equipmentSlotId)
        {
            if (string.Equals(equipmentSlotId, "Legs", StringComparison.OrdinalIgnoreCase))
                return CharacterWearableRegion.Lower;

            if (string.Equals(equipmentSlotId, "Feet", StringComparison.OrdinalIgnoreCase))
                return CharacterWearableRegion.Feet;

            if (string.Equals(equipmentSlotId, "Hands", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(equipmentSlotId, "Back", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(equipmentSlotId, "Accessory", StringComparison.OrdinalIgnoreCase))
                return CharacterWearableRegion.Accessory;

            return CharacterWearableRegion.Upper;
        }

        private void RefreshSaveLabel()
        {
            if (saveButton == null)
                return;
            Text label = saveButton.GetComponentInChildren<Text>(true);
            if (label != null)
                label.text = IsEditingExisting ? "UPDATE EXISTING" : "CREATE NEW";
        }

        private void BuildCompactColorUi()
        {
            Transform panelTransform = FindSceneTransformByName("ColorPanel");
            if (panelTransform == null)
                return;

            GameObject panel = panelTransform.gameObject;
            panel.SetActive(true);

            RectTransform panelRect = panelTransform as RectTransform;
            if (panelRect != null)
            {
                panelRect.anchorMin = new Vector2(1f, 1f);
                panelRect.anchorMax = new Vector2(1f, 1f);
                panelRect.pivot = new Vector2(0.5f, 0.5f);
                panelRect.anchoredPosition = new Vector2(-260f, -810f);
                panelRect.sizeDelta = new Vector2(490f, 132f);
            }

            // Retire the old grid visually without deleting any prefab data.
            for (int i = 0; i < panelTransform.childCount; ++i)
                panelTransform.GetChild(i).gameObject.SetActive(false);

            if (previewColorText != null)
            {
                previewColorText.gameObject.SetActive(true);
                previewColorText.transform.SetParent(panelTransform, false);
                RectTransform textRect = previewColorText.rectTransform;
                textRect.anchorMin = new Vector2(0.5f, 1f);
                textRect.anchorMax = new Vector2(0.5f, 1f);
                textRect.pivot = new Vector2(0.5f, 1f);
                textRect.anchoredPosition = new Vector2(0f, -12f);
                textRect.sizeDelta = new Vector2(210f, 30f);
                previewColorText.alignment = TextAnchor.MiddleCenter;
                _compactColorNameText = previewColorText;
            }

            Transform templateTransform = FindSceneTransformByName("ClearMeshSelectionsButton");
            Button template = templateTransform != null ? templateTransform.GetComponent<Button>() : null;
            if (template == null)
                return;

            Button previous = CreateCompactButton(template, panelTransform, "ColorPrevButton", "< PREV", new Vector2(-192f, -12f), new Vector2(74f, 30f));
            Button next = CreateCompactButton(template, panelTransform, "ColorNextButton", "NEXT >", new Vector2(192f, -12f), new Vector2(74f, 30f));
            _compactColorUseButton = CreateCompactButton(template, panelTransform, "ColorUseButton", "[ ] USE", new Vector2(112f, -12f), new Vector2(82f, 30f));
            _compactColorPrimaryButton = CreateCompactButton(template, panelTransform, "ColorPrimaryButton", "TEST PRIMARY", new Vector2(-105f, -72f), new Vector2(190f, 34f));
            _compactColorSecondaryButton = CreateCompactButton(template, panelTransform, "ColorSecondaryButton", "TEST SECONDARY", new Vector2(105f, -72f), new Vector2(190f, 34f));

            if (previous != null) previous.onClick.AddListener(() => CycleCompactColor(-1));
            if (next != null) next.onClick.AddListener(() => CycleCompactColor(1));
            if (_compactColorUseButton != null) _compactColorUseButton.onClick.AddListener(ToggleCurrentCompactColorAllowed);
            if (_compactColorPrimaryButton != null) _compactColorPrimaryButton.onClick.AddListener(TestCurrentColorAsPrimary);
            if (_compactColorSecondaryButton != null) _compactColorSecondaryButton.onClick.AddListener(TestCurrentColorAsSecondary);

            GameObject swatchObject = new GameObject("ColorCurrentSwatch", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            swatchObject.layer = panel.layer;
            swatchObject.transform.SetParent(panelTransform, false);
            RectTransform swatchRect = swatchObject.GetComponent<RectTransform>();
            swatchRect.anchorMin = new Vector2(0.5f, 1f);
            swatchRect.anchorMax = new Vector2(0.5f, 1f);
            swatchRect.pivot = new Vector2(0.5f, 1f);
            swatchRect.anchoredPosition = new Vector2(-118f, -13f);
            swatchRect.sizeDelta = new Vector2(26f, 26f);
            _compactColorSwatch = swatchObject.GetComponent<Image>();

            RefreshCompactColorUi();
        }

        private static Button CreateCompactButton(
            Button template,
            Transform parent,
            string objectName,
            string labelText,
            Vector2 anchoredPosition,
            Vector2 size)
        {
            if (template == null || parent == null)
                return null;

            Button button = Instantiate(template, parent, false);
            button.name = objectName;
            button.gameObject.SetActive(true);
            button.onClick.RemoveAllListeners();

            RectTransform rect = button.transform as RectTransform;
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = size;
            }

            Text label = button.GetComponentInChildren<Text>(true);
            if (label != null)
                label.text = labelText;

            return button;
        }

        private void CycleCompactColor(int delta)
        {
            if (_availableColorIds.Count == 0)
                return;

            _compactColorIndex = (_compactColorIndex + delta) % _availableColorIds.Count;
            if (_compactColorIndex < 0)
                _compactColorIndex += _availableColorIds.Count;
            RefreshCompactColorUi();
        }

        private ushort CurrentCompactColorId =>
            _compactColorIndex >= 0 && _compactColorIndex < _availableColorIds.Count
                ? _availableColorIds[_compactColorIndex]
                : (ushort)0;

        private void ToggleCurrentCompactColorAllowed()
        {
            ushort colorId = CurrentCompactColorId;
            if (colorId == 0)
                return;

            if (!_allowedColorIds.Add(colorId))
                _allowedColorIds.Remove(colorId);

            RefreshCompactColorUi();
        }

        private int CountSelectedPartsWithDyeChannel(
            CharacterWearablePartSelection[] parts,
            CharacterVisualPaletteChannel channel)
        {
            if (_profile == null || parts == null)
                return 0;

            int count = 0;
            var seen = new HashSet<ulong>();
            for (int i = 0; i < parts.Length; ++i)
            {
                CharacterWearablePartSelection part = parts[i];
                if (part.slotId == 0 || part.optionId == 0)
                    continue;

                ulong key = ((ulong)part.slotId << 32) | part.optionId;
                if (!seen.Add(key) ||
                    !_profile.TryGetOption(
                        part.slotId,
                        part.optionId,
                        out CharacterVisualOptionDefinition option) ||
                    option == null)
                    continue;

                CharacterVisualPaletteCellDefinition[] cells =
                    option.paletteCells ?? Array.Empty<CharacterVisualPaletteCellDefinition>();

                for (int c = 0; c < cells.Length; ++c)
                {
                    if (cells[c].IsValid && cells[c].channel == channel)
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }

        private int EnsurePrimaryDyeForSelectedParts(
            CharacterWearablePartSelection[] parts)
        {
            if (parts == null || parts.Length == 0)
                return 0;

            int ready = CountSelectedPartsWithDyeChannel(
                parts,
                CharacterVisualPaletteChannel.Primary);

            if (ready >= parts.Length || EditorEnsurePrimaryDyeHandler == null)
                return ready;

            try
            {
                return EditorEnsurePrimaryDyeHandler.Invoke(parts);
            }
            catch (Exception ex)
            {
                SetStatus($"Primary dye authoring failed: {ex.Message}", false);
                return ready;
            }
        }

        private void TestCurrentColorAsPrimary()
        {
            ushort colorId = CurrentCompactColorId;
            if (colorId == 0)
                return;

            CharacterWearablePartSelection[] parts =
                BuildPartSelections().ToArray();

            int ready = EnsurePrimaryDyeForSelectedParts(parts);

            _testPrimaryColorId = colorId;
            _previewColorId = colorId;
            RefreshCompactColorUi();
            ApplyPreview();

            if (parts.Length > 0 && ready == 0)
            {
                SetStatus(
                    "PRIMARY could not apply because no usable non-skin dye cell was found for the selected mesh set.",
                    false);
            }
            else if (parts.Length > 0)
            {
                SetStatus(
                    $"PRIMARY preview applied to {ready}/{parts.Length} selected mesh part(s).",
                    ready == parts.Length);
            }
        }

        private void TestCurrentColorAsSecondary()
        {
            ushort colorId = CurrentCompactColorId;
            if (colorId == 0)
                return;

            CharacterWearablePartSelection[] parts =
                BuildPartSelections().ToArray();
            int ready = CountSelectedPartsWithDyeChannel(
                parts,
                CharacterVisualPaletteChannel.Secondary);

            _testSecondaryColorId = colorId;
            RefreshCompactColorUi();
            ApplyPreview();

            if (parts.Length > 0 && ready == 0)
            {
                SetStatus(
                    "SECONDARY has no authored dye cells on this mesh set. It was not auto-guessed.",
                    false);
            }
            else if (parts.Length > 0)
            {
                SetStatus(
                    $"SECONDARY preview applied to {ready}/{parts.Length} selected mesh part(s).",
                    ready == parts.Length);
            }
        }

        private void RefreshCompactColorUi()
        {
            ushort colorId = CurrentCompactColorId;
            string colorName = colorId != 0
                ? SidekickCharacterPaletteUtility.GetClothingPaletteName(colorId)
                : "No palette colors";

            if (_compactColorNameText != null)
                _compactColorNameText.text = colorId != 0 ? $"{colorId:00}  {colorName}" : colorName;

            if (_compactColorSwatch != null)
            {
                Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
                _compactColorSwatch.color = colorId > 0 && colorId <= palette.Length
                    ? palette[colorId - 1]
                    : Color.clear;
            }

            SetButtonLabel(_compactColorUseButton,
                colorId != 0 && _allowedColorIds.Contains(colorId) ? "[X] USE" : "[ ] USE");
            SetButtonLabel(_compactColorPrimaryButton,
                _testPrimaryColorId == 0
                    ? "TEST PRIMARY"
                    : $"PRIMARY: {SidekickCharacterPaletteUtility.GetClothingPaletteName(_testPrimaryColorId)}");
            SetButtonLabel(_compactColorSecondaryButton,
                _testSecondaryColorId == 0
                    ? "TEST SECONDARY"
                    : $"SECONDARY: {SidekickCharacterPaletteUtility.GetClothingPaletteName(_testSecondaryColorId)}");
        }

        private static void SetButtonLabel(Button button, string text)
        {
            if (button == null)
                return;
            Text label = button.GetComponentInChildren<Text>(true);
            if (label != null)
                label.text = text ?? string.Empty;
        }

        private void CapturePreviewOrbit()
        {
            if (previewCamera == null || _mannequin == null)
                return;

            Bounds bounds = CalculateBounds(_mannequin);
            _previewOrbitTarget = bounds.center;
            Vector3 offset = previewCamera.transform.position - _previewOrbitTarget;
            _previewOrbitDistance = Mathf.Max(0.25f, offset.magnitude);
            _previewOrbitYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            _previewOrbitPitch = Mathf.Asin(Mathf.Clamp(offset.y / _previewOrbitDistance, -1f, 1f)) * Mathf.Rad2Deg;
            _previewInitialPosition = previewCamera.transform.position;
            _previewInitialRotation = previewCamera.transform.rotation;
            _previewInitialFov = previewCamera.fieldOfView;
            _previewOrbitReady = true;
        }

        private void UpdatePreviewInteraction()
        {
            if (previewCamera == null || !_previewOrbitReady)
                return;

            bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

            if (UnityEngine.Input.GetMouseButtonDown(0) && !overUi)
            {
                _previewDragging = true;
                _previewLastMousePosition = UnityEngine.Input.mousePosition;
            }

            if (UnityEngine.Input.GetMouseButtonUp(0))
                _previewDragging = false;

            if (_previewDragging && UnityEngine.Input.GetMouseButton(0))
            {
                Vector3 now = UnityEngine.Input.mousePosition;
                Vector3 delta = now - _previewLastMousePosition;
                _previewLastMousePosition = now;

                _previewOrbitYaw -= delta.x * 0.32f;
                _previewOrbitPitch = Mathf.Clamp(_previewOrbitPitch + delta.y * 0.32f, -35f, 78f);
                ApplyPreviewOrbitPose();
            }

            float scroll = UnityEngine.Input.mouseScrollDelta.y;
            if (!overUi && !Mathf.Approximately(scroll, 0f))
                previewCamera.fieldOfView = Mathf.Clamp(previewCamera.fieldOfView - scroll * 2.25f, 18f, 55f);
        }

        private void ApplyPreviewOrbitPose()
        {
            if (previewCamera == null || !_previewOrbitReady)
                return;

            Quaternion orbit = Quaternion.Euler(_previewOrbitPitch, _previewOrbitYaw, 0f);
            previewCamera.transform.position =
                _previewOrbitTarget + orbit * Vector3.forward * _previewOrbitDistance;
            previewCamera.transform.LookAt(_previewOrbitTarget);
        }

        private static Transform FindSceneTransformByName(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return null;

            RectTransform[] values = Resources.FindObjectsOfTypeAll<RectTransform>();
            for (int i = 0; i < values.Length; ++i)
            {
                RectTransform value = values[i];
                if (value != null && value.gameObject.scene.IsValid() &&
                    string.Equals(value.name, objectName, StringComparison.Ordinal))
                    return value;
            }

            return null;
        }

        private HashSet<ushort> BuildAvailableColorSet()
        {
            var result = new HashSet<ushort>();
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            IReadOnlyList<ushort> configured = _profile.PlayerClothingPaletteIds;
            if (_profile.PlayerClothingColorPoolReviewed && configured != null && configured.Count > 0)
            {
                for (int i = 0; i < configured.Count; ++i)
                    if (configured[i] > 0 && configured[i] <= palette.Length)
                        result.Add(configured[i]);
            }
            else
            {
                for (ushort id = 1; id <= palette.Length; ++id)
                    result.Add(id);
            }
            return result;
        }

        private static void SetRecipeMesh(CharacterAppearanceRecipe recipe, ushort slotId, ushort optionId)
        {
            if (recipe == null || slotId == 0)
                return;

            var values = new List<CharacterMeshSelection>(recipe.meshes ?? Array.Empty<CharacterMeshSelection>());
            for (int i = values.Count - 1; i >= 0; --i)
                if (values[i].slotId == slotId)
                    values.RemoveAt(i);

            if (optionId != 0)
                values.Add(new CharacterMeshSelection(slotId, optionId));
            values.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            recipe.meshes = values.ToArray();
        }

        private static void SetRecipeColor(CharacterAppearanceRecipe recipe, ushort channelId, ushort paletteId)
        {
            if (recipe == null || channelId == 0 || paletteId == 0)
                return;

            var values = new List<CharacterColorSelection>(recipe.colors ?? Array.Empty<CharacterColorSelection>());
            for (int i = values.Count - 1; i >= 0; --i)
                if (values[i].channelId == channelId)
                    values.RemoveAt(i);

            values.Add(new CharacterColorSelection(channelId, CharacterColorEncoding.PaletteIndex, paletteId));
            values.Sort((a, b) => a.channelId.CompareTo(b.channelId));
            recipe.colors = values.ToArray();
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root != null ? root.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
            bool has = false;
            Bounds result = new Bounds(root != null ? root.transform.position : Vector3.zero, Vector3.one);
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.bounds.size.sqrMagnitude <= 0.000001f)
                    continue;

                if (!has)
                {
                    result = renderer.bounds;
                    has = true;
                }
                else
                {
                    result.Encapsulate(renderer.bounds);
                }
            }
            return result;
        }

        private static Transform FindChildRecursive(
            Transform root,
            string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
                return null;

            if (string.Equals(root.name, childName, StringComparison.Ordinal))
                return root;

            for (int i = 0; i < root.childCount; ++i)
            {
                Transform found =
                    FindChildRecursive(root.GetChild(i), childName);

                if (found != null)
                    return found;
            }

            return null;
        }

        private void SetStatus(string message, bool success)
        {
            if (statusText == null)
                return;

            statusText.text = string.IsNullOrWhiteSpace(message) ? (success ? "Ready" : "Error") : message;
            statusText.color = success
                ? new Color(0.55f, 0.95f, 0.62f, 1f)
                : new Color(1f, 0.55f, 0.5f, 1f);
        }
    }
}
