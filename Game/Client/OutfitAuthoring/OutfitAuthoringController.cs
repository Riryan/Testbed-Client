using System;
using System.Collections.Generic;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEngine;
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
    }

    public readonly struct OutfitAuthoringSaveResult
    {
        public readonly bool success;
        public readonly string message;

        public OutfitAuthoringSaveResult(bool success, string message)
        {
            this.success = success;
            this.message = message ?? string.Empty;
        }
    }

    /// <summary>
    /// Scene-authored Outfit Builder controller. The UI hierarchy is serialized in the scene;
    /// this component only binds current catalog/server data and drives the live Player preview.
    /// It reuses CharacterVisualProfile, ModularCharacterAppearancePresenter and the existing
    /// standalone server equipment/content contracts. No runtime UI construction is performed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringController : MonoBehaviour
    {
        public static Func<OutfitAuthoringSaveRequest, OutfitAuthoringSaveResult> EditorSaveHandler;
        public static Func<OutfitAuthoringEquipmentSlotOption[]> EditorEquipmentSlotProvider;

        [Header("Preview")]
        [SerializeField] private Transform previewAnchor;
        [SerializeField] private Camera previewCamera;

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
            public int optionIndex = -1; // -1 = canonical Default Base
        }

        private static readonly ushort[] PreviewColorChannels =
        {
            SidekickCharacterPaletteUtility.ClothingPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.ClothingSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.UpperPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.UpperSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.LowerPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.LowerSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.FeetPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.FeetSecondaryColorChannelId,
            SidekickCharacterPaletteUtility.AccessoryPrimaryColorChannelId,
            SidekickCharacterPaletteUtility.AccessorySecondaryColorChannelId,
        };

        private readonly List<SlotState> _slotStates = new List<SlotState>();
        private readonly List<OutfitAuthoringEquipmentSlotOption> _equipmentSlots = new List<OutfitAuthoringEquipmentSlotOption>();
        private readonly HashSet<ushort> _allowedColorIds = new HashSet<ushort>();

        private CharacterVisualProfile _profile;
        private CharacterAppearanceRecipe _recipe;
        private GameObject _mannequin;
        private ModularCharacterAppearancePresenter _presenter;
        private ushort _previewColorId;

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
            BindItemControls();
            BindSave();
            ApplyPreview();
            SetStatus("Ready", true);
        }

        private void OnDestroy()
        {
            if (_mannequin != null)
                Destroy(_mannequin);
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
                if (option != null && !string.IsNullOrWhiteSpace(option.slotId) && option.presentationSlotId != 0)
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
                animator.enabled = false;

            if (previewCamera != null)
                FramePreviewCamera();
        }

        private void FramePreviewCamera()
        {
            Bounds bounds = CalculateBounds(_mannequin);
            Vector3 center = bounds.center;
            float height = Mathf.Max(1.6f, bounds.size.y);
            float distance = Mathf.Max(3.0f, height * 1.65f);
            previewCamera.transform.position = center + new Vector3(0f, height * 0.03f, distance);
            previewCamera.transform.LookAt(center + Vector3.up * height * 0.02f);
            previewCamera.nearClipPlane = 0.05f;
            previewCamera.fieldOfView = 35f;
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
            HashSet<ushort> available = BuildAvailableColorSet();
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            OutfitAuthoringColorCell[] cells = colorCells ?? Array.Empty<OutfitAuthoringColorCell>();

            for (int i = 0; i < cells.Length; ++i)
            {
                OutfitAuthoringColorCell cell = cells[i];
                if (cell == null)
                    continue;

                ushort id = cell.ColorId;
                bool enabled = id > 0 && id <= palette.Length && available.Contains(id);
                cell.gameObject.SetActive(enabled);
                if (!enabled)
                    continue;

                _allowedColorIds.Add(id);
                cell.Configure(
                    palette[id - 1],
                    true,
                    PreviewColor,
                    ToggleColorAllowed);
            }
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
            }
        }

        private void BindSave()
        {
            if (saveButton == null)
                return;
            saveButton.onClick.RemoveListener(SaveOutfit);
            saveButton.onClick.AddListener(SaveOutfit);
        }

        private void CycleSlot(SlotState state, int delta)
        {
            if (state == null)
                return;

            int choiceCount = state.options.Count + 1; // Default + every Player-allowed catalog mesh
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

            if (_previewColorId > 0)
            {
                for (int i = 0; i < PreviewColorChannels.Length; ++i)
                    SetRecipeColor(_recipe, PreviewColorChannels[i], _previewColorId);
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
            });

            SetStatus(result.message, result.success);
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
