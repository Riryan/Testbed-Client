using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    [Serializable]
    public sealed class OutfitWorkbenchStatModifier
    {
        public string statId = string.Empty;
        public float additive;
        public float multiplier = 1f;
    }

    [Serializable]
    public sealed class OutfitWorkbenchServerItem
    {
        public string definitionId = string.Empty;
        public string displayName = string.Empty;
        public ushort presentationId;
        public string equipmentSlotId = string.Empty;
        public int maxStack = 1;
        public float weight;
        public int maxDurability;
        public string[] tags = Array.Empty<string>();
        public OutfitWorkbenchStatModifier[] statModifiers = Array.Empty<OutfitWorkbenchStatModifier>();
    }

    public sealed class OutfitWorkbenchOptions
    {
        public string[] tags = Array.Empty<string>();
        public string[] statIds = Array.Empty<string>();
    }

    public sealed class OutfitWorkbenchCacheStatus
    {
        public string state = "NOT LOADED";
        public string detail = string.Empty;
    }

    [Serializable]
    public sealed class OutfitWorkbenchEquipmentEdit
    {
        public int maxStack = 1;
        public float weight;
        public int maxDurability;
        public string[] tags = Array.Empty<string>();
        public OutfitWorkbenchStatModifier[] statModifiers = Array.Empty<OutfitWorkbenchStatModifier>();
    }

    /// <summary>
    /// Permanent Outfit Builder workbench attached directly to Outfit Authoring UI.prefab.
    /// It reuses the existing ColorPanel, ItemPanel, mesh builder/controller and the existing
    /// IntegrityOverlayPanel shell. No one-shot scene installer or generated editor UI is used.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringWorkbench : MonoBehaviour
    {
        public static Func<OutfitWorkbenchServerItem[]> EditorServerItemsProvider;
        public static Func<OutfitWorkbenchOptions> EditorOptionsProvider;
        public static Func<string, OutfitWorkbenchCacheStatus> EditorCacheStatusProvider;
        public static Func<string, OutfitWorkbenchEquipmentEdit, bool> EditorEquipmentUpdateHandler;
        public static Func<ushort[], ushort[], string> EditorSetDefaultBodyHandler;

        public static OutfitAuthoringWorkbench Active { get; private set; }

        private Transform _colorPanel;
        private Transform _itemPanel;
        private RectTransform _workbenchPanel;
        private Button _colorFoldoutButton;
        private Text _colorFoldoutLabel;
        private bool _colorsExpanded;

        private OutfitAuthoringController _controller;
        private CharacterVisualProfile _profile;
        private OutfitWorkbenchServerItem[] _serverItems = Array.Empty<OutfitWorkbenchServerItem>();
        private OutfitWorkbenchOptions _options = new OutfitWorkbenchOptions();

        private Text _currentSelectionText;
        private Text _identityText;
        private Text _cacheText;
        private Text _messageText;
        private OutfitAuthoringDropdown _existingDropdown;
        private Button _loadButton;
        private Button _newButton;
        private Button _refreshButton;
        private Button _setBaseButton;
        private InputField _maxStack;
        private InputField _weight;
        private InputField _durability;
        private readonly OutfitAuthoringDropdown[] _tagDropdowns = new OutfitAuthoringDropdown[3];
        private readonly OutfitAuthoringDropdown[] _statDropdowns = new OutfitAuthoringDropdown[3];
        private readonly InputField[] _statAdditive = new InputField[3];
        private readonly InputField[] _statMultiplier = new InputField[3];
        private int _selectedExistingIndex = -1;
        private string _loadedDefinitionId = string.Empty;
        private string _lastMeshSignature = string.Empty;
        private float _nextSelectionScan;

        private static readonly Color PanelColor = new Color(0.055f, 0.065f, 0.08f, 0.97f);
        private static readonly Color FieldColor = new Color(0.045f, 0.055f, 0.075f, 1f);

        private void Awake()
        {
            Active = this;
            _profile = CharacterVisualProfileRegistry.Default;
            _controller = FindObjectOfType<OutfitAuthoringController>();

            _colorPanel = FindRecursive(transform, "ColorPanel");
            _itemPanel = FindRecursive(transform, "ItemPanel");
            Transform shell = FindRecursive(transform, "IntegrityOverlayPanel") ??
                              FindRecursive(transform, "EquipmentWorkbenchPanel");

            if (shell == null)
            {
                Debug.LogError("[OutfitAuthoring] Prefab is missing IntegrityOverlayPanel/EquipmentWorkbenchPanel.");
                enabled = false;
                return;
            }

            shell.gameObject.name = "EquipmentWorkbenchPanel";
            _workbenchPanel = shell as RectTransform;

            BindColorFoldout();
            BuildWorkbenchUi(shell);
            RefreshAll();
            LayoutPanels();
        }

        private void Start()
        {
            if (_controller == null)
                _controller = FindObjectOfType<OutfitAuthoringController>();

            if (_controller != null)
            {
                _controller.AuthoringModeChanged -= OnAuthoringModeChanged;
                _controller.AuthoringModeChanged += OnAuthoringModeChanged;
                _controller.Saved -= OnControllerSaved;
                _controller.Saved += OnControllerSaved;
            }
        }

        private void OnDestroy()
        {
            if (Active == this)
                Active = null;

            if (_controller != null)
            {
                _controller.AuthoringModeChanged -= OnAuthoringModeChanged;
                _controller.Saved -= OnControllerSaved;
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextSelectionScan)
                return;
            _nextSelectionScan = Time.unscaledTime + 0.35f;
            RefreshCurrentSelectionStatus();
        }

        public OutfitWorkbenchEquipmentEdit CaptureEquipmentEdit()
        {
            return new OutfitWorkbenchEquipmentEdit
            {
                maxStack = Mathf.Max(1, ParseInt(_maxStack, 1)),
                weight = Mathf.Max(0f, ParseFloat(_weight, 0f)),
                maxDurability = Mathf.Max(0, ParseInt(_durability, 0)),
                tags = CaptureSelected(_tagDropdowns, _options.tags),
                statModifiers = CaptureModifiers(),
            };
        }

        public void NotifyServerSaveCompleted(string definitionId)
        {
            _loadedDefinitionId = definitionId ?? string.Empty;
            RefreshAll();
            SelectDefinition(_loadedDefinitionId, loadIntoController: false);
        }

        private void BindColorFoldout()
        {
            if (_colorPanel == null)
                return;

            Transform buttonTransform = _colorPanel.Find("ColorFoldoutButton");
            if (buttonTransform == null)
                return;

            _colorFoldoutButton = buttonTransform.GetComponent<Button>();
            _colorFoldoutLabel = buttonTransform.GetComponentInChildren<Text>(true);

            RectTransform buttonRect = buttonTransform as RectTransform;
            if (buttonRect != null)
            {
                buttonRect.anchorMin = new Vector2(0.5f, 1f);
                buttonRect.anchorMax = new Vector2(0.5f, 1f);
                buttonRect.pivot = new Vector2(0.5f, 1f);
                buttonRect.anchoredPosition = new Vector2(0f, -8f);
                buttonRect.sizeDelta = new Vector2(400f, 32f);
            }

            if (_colorFoldoutButton != null)
            {
                _colorFoldoutButton.onClick.RemoveAllListeners();
                _colorFoldoutButton.onClick.AddListener(ToggleColors);
            }

            _colorsExpanded = false;
            ApplyColorFoldout();
        }

        private void ToggleColors()
        {
            _colorsExpanded = !_colorsExpanded;
            ApplyColorFoldout();
            LayoutPanels();
        }

        private void ApplyColorFoldout()
        {
            if (_colorPanel == null)
                return;

            for (int i = 0; i < _colorPanel.childCount; ++i)
            {
                Transform child = _colorPanel.GetChild(i);
                if (child == null || child.gameObject.name == "ColorFoldoutButton")
                    continue;
                child.gameObject.SetActive(_colorsExpanded);
            }

            if (_colorFoldoutLabel != null)
                _colorFoldoutLabel.text = _colorsExpanded ? "COLORS ▼" : "COLORS ▶";

            RectTransform rect = _colorPanel as RectTransform;
            if (rect != null)
                rect.sizeDelta = new Vector2(430f, _colorsExpanded ? 290f : 48f);
        }

        private void LayoutPanels()
        {
            float detailsY = _colorsExpanded ? -505f : -265f;
            float itemY = detailsY - 470f;

            if (_workbenchPanel != null)
            {
                _workbenchPanel.anchorMin = new Vector2(0f, 1f);
                _workbenchPanel.anchorMax = new Vector2(0f, 1f);
                _workbenchPanel.pivot = new Vector2(0.5f, 1f);
                _workbenchPanel.anchoredPosition = new Vector2(235f, detailsY);
                _workbenchPanel.sizeDelta = new Vector2(430f, 450f);
            }

            RectTransform itemRect = _itemPanel as RectTransform;
            if (itemRect != null)
                itemRect.anchoredPosition = new Vector2(235f, itemY);
        }

        private void BuildWorkbenchUi(Transform host)
        {
            for (int i = host.childCount - 1; i >= 0; --i)
                Destroy(host.GetChild(i).gameObject);

            Image background = host.GetComponent<Image>();
            if (background != null)
                background.color = PanelColor;

            _currentSelectionText = CreateText(host, "CurrentSelection", "CURRENT: checking...", 12, FontStyle.Bold);
            SetRect(_currentSelectionText.rectTransform, 14f, -10f, 402f, 28f);

            _existingDropdown = CreateDropdown(host, "ExistingItem", 402f);
            SetRect(_existingDropdown.GetComponent<RectTransform>(), 14f, -44f, 402f, 32f);
            _existingDropdown.SelectionChanged += index => _selectedExistingIndex = index;

            _newButton = CreateButton(host, "New", "NEW", 72f, BeginNew);
            SetRect(_newButton.GetComponent<RectTransform>(), 14f, -82f, 72f, 30f);

            _loadButton = CreateButton(host, "Load", "LOAD / EDIT", 112f, LoadSelectedExisting);
            SetRect(_loadButton.GetComponent<RectTransform>(), 94f, -82f, 112f, 30f);

            _refreshButton = CreateButton(host, "Refresh", "REFRESH", 92f, RefreshAll);
            SetRect(_refreshButton.GetComponent<RectTransform>(), 214f, -82f, 92f, 30f);

            _setBaseButton = CreateButton(host, "SetBase", "SET UNEQUIPPED BASE", 102f, SetCurrentAsDefaultBody);
            SetRect(_setBaseButton.GetComponent<RectTransform>(), 314f, -82f, 102f, 30f);

            _identityText = CreateText(host, "Identity", "Definition: NEW | Kind: Equipment | Subtype: Armor", 10, FontStyle.Normal);
            SetRect(_identityText.rectTransform, 14f, -118f, 402f, 22f);

            _cacheText = CreateText(host, "Cache", "Cache: not checked", 10, FontStyle.Normal);
            SetRect(_cacheText.rectTransform, 14f, -140f, 402f, 22f);

            _maxStack = CreateLabeledInput(host, "Max Stack", "1", 14f, -170f, 122f);
            _weight = CreateLabeledInput(host, "Weight", "0", 154f, -170f, 122f);
            _durability = CreateLabeledInput(host, "Max Durability", "0", 294f, -170f, 122f);

            Text tagLabel = CreateText(host, "TagLabel", "TAGS", 10, FontStyle.Bold);
            SetRect(tagLabel.rectTransform, 14f, -224f, 45f, 26f);
            for (int i = 0; i < 3; ++i)
            {
                _tagDropdowns[i] = CreateDropdown(host, "Tag" + (i + 1), 112f);
                SetRect(_tagDropdowns[i].GetComponent<RectTransform>(), 62f + i * 118f, -224f, 112f, 28f);
            }

            Text statHeader = CreateText(host, "StatHeader", "STAT MODIFIERS          STAT ID                        ADD            MULT", 9, FontStyle.Bold);
            SetRect(statHeader.rectTransform, 14f, -260f, 402f, 20f);

            for (int i = 0; i < 3; ++i)
            {
                float y = -284f - i * 34f;
                _statDropdowns[i] = CreateDropdown(host, "Stat" + (i + 1), 214f);
                SetRect(_statDropdowns[i].GetComponent<RectTransform>(), 14f, y, 214f, 28f);
                _statAdditive[i] = CreateInput(host, "StatAdd" + i, "0");
                SetRect(_statAdditive[i].GetComponent<RectTransform>(), 238f, y, 82f, 28f);
                _statMultiplier[i] = CreateInput(host, "StatMult" + i, "1");
                SetRect(_statMultiplier[i].GetComponent<RectTransform>(), 330f, y, 86f, 28f);
            }

            _messageText = CreateText(host, "Message", "Server content is authoritative. Cache verification covers only fields actually shipped to the client.", 9, FontStyle.Normal);
            _messageText.verticalOverflow = VerticalWrapMode.Overflow;
            SetRect(_messageText.rectTransform, 14f, -390f, 402f, 48f);
        }

        private void RefreshAll()
        {
            _profile = CharacterVisualProfileRegistry.Default;
            if (_controller == null)
                _controller = FindObjectOfType<OutfitAuthoringController>();

            try
            {
                _serverItems = EditorServerItemsProvider != null
                    ? EditorServerItemsProvider.Invoke() ?? Array.Empty<OutfitWorkbenchServerItem>()
                    : Array.Empty<OutfitWorkbenchServerItem>();
            }
            catch (Exception ex)
            {
                _serverItems = Array.Empty<OutfitWorkbenchServerItem>();
                SetMessage("Server read failed: " + ex.Message, false);
            }

            try
            {
                _options = EditorOptionsProvider != null
                    ? EditorOptionsProvider.Invoke() ?? new OutfitWorkbenchOptions()
                    : new OutfitWorkbenchOptions();
            }
            catch
            {
                _options = new OutfitWorkbenchOptions();
            }

            string selectedDefinition = _loadedDefinitionId;
            var labels = new List<string>(_serverItems.Length);
            for (int i = 0; i < _serverItems.Length; ++i)
            {
                OutfitWorkbenchServerItem item = _serverItems[i];
                labels.Add($"{item.displayName}  [{item.definitionId}]");
            }

            _selectedExistingIndex = -1;
            if (!string.IsNullOrWhiteSpace(selectedDefinition))
            {
                for (int i = 0; i < _serverItems.Length; ++i)
                {
                    if (string.Equals(_serverItems[i].definitionId, selectedDefinition, StringComparison.OrdinalIgnoreCase))
                    {
                        _selectedExistingIndex = i;
                        break;
                    }
                }
            }
            if (_selectedExistingIndex < 0 && _serverItems.Length > 0)
                _selectedExistingIndex = 0;

            _existingDropdown?.SetOptions(labels, _selectedExistingIndex);
            FillOptionDropdowns();

            if (!string.IsNullOrWhiteSpace(_loadedDefinitionId))
                SelectDefinition(_loadedDefinitionId, loadIntoController: false);
            else
                RefreshCurrentSelectionStatus();
        }

        private void FillOptionDropdowns()
        {
            string[] tags = PrependNone(_options.tags);
            string[] stats = PrependNone(_options.statIds);

            for (int i = 0; i < _tagDropdowns.Length; ++i)
                _tagDropdowns[i]?.SetOptions(tags, 0);
            for (int i = 0; i < _statDropdowns.Length; ++i)
                _statDropdowns[i]?.SetOptions(stats, 0);
        }

        private void BeginNew()
        {
            _loadedDefinitionId = string.Empty;
            _controller?.BeginNewItem();
            SetInput(_maxStack, "1");
            SetInput(_weight, "0");
            SetInput(_durability, "0");
            FillOptionDropdowns();
            for (int i = 0; i < 3; ++i)
            {
                SetInput(_statAdditive[i], "0");
                SetInput(_statMultiplier[i], "1");
            }
            if (_identityText != null)
                _identityText.text = "Definition: NEW | Kind: Equipment | Subtype: Armor";
            if (_cacheText != null)
                _cacheText.text = "Cache: not applicable until created";
            SetMessage("New equipment item. The existing Item Name / Equipment Slot / Visual Layer controls remain authoritative.", true);
            RefreshCurrentSelectionStatus();
        }

        private void LoadSelectedExisting()
        {
            if (_selectedExistingIndex < 0 || _selectedExistingIndex >= _serverItems.Length)
                return;
            SelectDefinition(_serverItems[_selectedExistingIndex].definitionId, loadIntoController: true);
        }

        private void SelectDefinition(string definitionId, bool loadIntoController)
        {
            OutfitWorkbenchServerItem item = _serverItems.FirstOrDefault(x =>
                x != null && string.Equals(x.definitionId, definitionId, StringComparison.OrdinalIgnoreCase));

            if (item == null)
            {
                SetMessage("Server item not found: " + definitionId, false);
                return;
            }

            _loadedDefinitionId = item.definitionId;
            if (_identityText != null)
                _identityText.text = $"Definition: {item.definitionId} | P{item.presentationId} | Slot: {item.equipmentSlotId}";

            SetInput(_maxStack, Mathf.Max(1, item.maxStack).ToString(CultureInfo.InvariantCulture));
            SetInput(_weight, Mathf.Max(0f, item.weight).ToString("0.###", CultureInfo.InvariantCulture));
            SetInput(_durability, Mathf.Max(0, item.maxDurability).ToString(CultureInfo.InvariantCulture));
            ApplySelectedValues(_tagDropdowns, _options.tags, item.tags);
            ApplyModifiers(item.statModifiers);
            RefreshCacheStatus(item.definitionId);

            if (loadIntoController && _controller != null && _profile != null)
            {
                CharacterWearableSetDefinition wearable = _profile.WearableSets.FirstOrDefault(x =>
                    x != null && string.Equals(x.definitionId, item.definitionId, StringComparison.OrdinalIgnoreCase));

                if (wearable == null)
                {
                    SetMessage("Server item exists, but its CharacterWearableSetDefinition is missing.", false);
                    return;
                }

                ushort slotPresentationId = 0;
                OutfitAuthoringEquipmentSlotOption[] slots = OutfitAuthoringController.EditorEquipmentSlotProvider != null
                    ? OutfitAuthoringController.EditorEquipmentSlotProvider.Invoke()
                    : Array.Empty<OutfitAuthoringEquipmentSlotOption>();

                for (int i = 0; i < slots.Length; ++i)
                {
                    if (slots[i] != null && string.Equals(slots[i].slotId, item.equipmentSlotId, StringComparison.Ordinal))
                    {
                        slotPresentationId = slots[i].presentationSlotId;
                        break;
                    }
                }

                if (_controller.LoadExistingWearable(wearable, item.equipmentSlotId, slotPresentationId))
                    SetMessage("Loaded existing server item and wearable presentation for editing.", true);
            }
        }

        private void RefreshCacheStatus(string definitionId)
        {
            if (_cacheText == null)
                return;

            try
            {
                OutfitWorkbenchCacheStatus status = EditorCacheStatusProvider != null
                    ? EditorCacheStatusProvider.Invoke(definitionId)
                    : null;
                _cacheText.text = status == null
                    ? "Cache: unavailable"
                    : "Cache: " + status.state +
                      (string.IsNullOrWhiteSpace(status.detail) ? string.Empty : " | " + status.detail);
            }
            catch (Exception ex)
            {
                _cacheText.text = "Cache: ERROR | " + ex.Message;
            }
        }

        private void RefreshCurrentSelectionStatus()
        {
            if (_profile == null || _currentSelectionText == null)
                return;

            List<CharacterWearablePartSelection> differences = CaptureCurrentDifferencesFromDefaults();
            string signature = Signature(differences);
            if (signature == _lastMeshSignature && !string.IsNullOrEmpty(signature))
                return;
            _lastMeshSignature = signature;

            if (differences.Count == 0)
            {
                _currentSelectionText.text = "CURRENT: DEFAULT UNEQUIPPED BODY";
                return;
            }

            CharacterWearableSetDefinition match = FindWearableByExactParts(differences);
            if (match == null)
            {
                _currentSelectionText.text = "CURRENT: NOT AUTHORED";
                return;
            }

            bool serverExists = _serverItems.Any(x => x != null &&
                string.Equals(x.definitionId, match.definitionId, StringComparison.OrdinalIgnoreCase) &&
                x.presentationId == match.presentationId);

            _currentSelectionText.text = serverExists
                ? $"CURRENT: ALREADY AUTHORED — {match.displayName} [{match.definitionId}]"
                : $"CURRENT: PRESENTATION ONLY — {match.definitionId}";
        }

        private List<CharacterWearablePartSelection> CaptureCurrentDifferencesFromDefaults()
        {
            var result = new List<CharacterWearablePartSelection>();
            Dictionary<ushort, ushort> active = CaptureCurrentActiveOptions();
            IReadOnlyList<CharacterVisualSlotDefinition> slots = _profile.Slots;

            for (int i = 0; i < slots.Count; ++i)
            {
                CharacterVisualSlotDefinition slot = slots[i];
                if (slot == null || !active.TryGetValue(slot.slotId, out ushort optionId))
                    continue;
                if (optionId == 0 || optionId == slot.defaultOptionId)
                    continue;
                result.Add(new CharacterWearablePartSelection(slot.slotId, optionId));
            }

            result.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            return result;
        }

        private Dictionary<ushort, ushort> CaptureCurrentActiveOptions()
        {
            var result = new Dictionary<ushort, ushort>();
            ModularCharacterAppearancePresenter presenter = FindObjectOfType<ModularCharacterAppearancePresenter>();
            if (presenter == null || _profile == null)
                return result;

            SkinnedMeshRenderer[] renderers = presenter.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer.sharedMesh == null ||
                    !TryExtractSlotId(renderer.gameObject.name, out ushort slotId) ||
                    !_profile.TryGetSlot(slotId, out CharacterVisualSlotDefinition slot) || slot == null)
                    continue;

                CharacterVisualOptionDefinition[] options = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                for (int o = 0; o < options.Length; ++o)
                {
                    CharacterVisualOptionDefinition option = options[o];
                    if (option != null && option.optionId != 0 && option.mesh == renderer.sharedMesh)
                    {
                        result[slotId] = option.optionId;
                        break;
                    }
                }
            }
            return result;
        }

        private CharacterWearableSetDefinition FindWearableByExactParts(List<CharacterWearablePartSelection> parts)
        {
            if (_profile == null)
                return null;

            string wanted = Signature(parts);
            IReadOnlyList<CharacterWearableSetDefinition> wearables = _profile.WearableSets;
            for (int i = 0; i < wearables.Count; ++i)
            {
                CharacterWearableSetDefinition wearable = wearables[i];
                if (wearable == null)
                    continue;
                var values = (wearable.parts ?? Array.Empty<CharacterWearablePartSelection>())
                    .Where(x => x.slotId != 0 && x.optionId != 0)
                    .OrderBy(x => x.slotId)
                    .ToList();
                if (Signature(values) == wanted)
                    return wearable;
            }
            return null;
        }

        private void SetCurrentAsDefaultBody()
        {
            Dictionary<ushort, ushort> active = CaptureCurrentActiveOptions();
            if (active.Count == 0)
            {
                SetMessage("No active mannequin mesh selections could be resolved.", false);
                return;
            }

            ushort[] slotIds = active.Keys.OrderBy(x => x).ToArray();
            ushort[] optionIds = slotIds.Select(x => active[x]).ToArray();

            if (EditorSetDefaultBodyHandler == null)
            {
                SetMessage("Editor default-body bridge is unavailable.", false);
                return;
            }

            string result = EditorSetDefaultBodyHandler.Invoke(slotIds, optionIds);
            SetMessage(result, !result.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
            _profile = CharacterVisualProfileRegistry.Default;
            _lastMeshSignature = string.Empty;
            RefreshCurrentSelectionStatus();
        }

        private void OnAuthoringModeChanged()
        {
            if (_controller == null)
                return;
            if (!_controller.IsEditingExisting)
                _loadedDefinitionId = string.Empty;
            _lastMeshSignature = string.Empty;
        }

        private void OnControllerSaved(OutfitAuthoringSaveResult result)
        {
            if (result.success)
                NotifyServerSaveCompleted(result.definitionId);
        }

        private OutfitWorkbenchStatModifier[] CaptureModifiers()
        {
            string[] stats = _options.statIds ?? Array.Empty<string>();
            var result = new List<OutfitWorkbenchStatModifier>(3);
            for (int i = 0; i < 3; ++i)
            {
                int index = _statDropdowns[i] != null ? _statDropdowns[i].SelectedIndex - 1 : -1;
                if (index < 0 || index >= stats.Length)
                    continue;
                string stat = stats[index];
                if (string.IsNullOrWhiteSpace(stat))
                    continue;
                result.Add(new OutfitWorkbenchStatModifier
                {
                    statId = stat,
                    additive = ParseFloat(_statAdditive[i], 0f),
                    multiplier = ParseFloat(_statMultiplier[i], 1f),
                });
            }
            return result.ToArray();
        }

        private void ApplyModifiers(OutfitWorkbenchStatModifier[] modifiers)
        {
            modifiers ??= Array.Empty<OutfitWorkbenchStatModifier>();
            string[] options = PrependNone(_options.statIds);
            for (int i = 0; i < 3; ++i)
            {
                OutfitWorkbenchStatModifier modifier = i < modifiers.Length ? modifiers[i] : null;
                SelectValue(_statDropdowns[i], options, modifier?.statId);
                SetInput(_statAdditive[i], (modifier?.additive ?? 0f).ToString("0.###", CultureInfo.InvariantCulture));
                SetInput(_statMultiplier[i], (modifier?.multiplier ?? 1f).ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        private static string[] CaptureSelected(OutfitAuthoringDropdown[] dropdowns, string[] source)
        {
            source ??= Array.Empty<string>();
            var result = new List<string>();
            for (int i = 0; i < dropdowns.Length; ++i)
            {
                int index = dropdowns[i] != null ? dropdowns[i].SelectedIndex - 1 : -1;
                if (index < 0 || index >= source.Length)
                    continue;
                string value = source[index];
                if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value))
                    result.Add(value);
            }
            return result.ToArray();
        }

        private static void ApplySelectedValues(OutfitAuthoringDropdown[] dropdowns, string[] source, string[] values)
        {
            string[] options = PrependNone(source);
            values ??= Array.Empty<string>();
            for (int i = 0; i < dropdowns.Length; ++i)
                SelectValue(dropdowns[i], options, i < values.Length ? values[i] : null);
        }

        private static string[] PrependNone(string[] source)
        {
            source ??= Array.Empty<string>();
            string[] result = new string[source.Length + 1];
            result[0] = "(None)";
            Array.Copy(source, 0, result, 1, source.Length);
            return result;
        }

        private static void SelectValue(OutfitAuthoringDropdown dropdown, string[] options, string value)
        {
            if (dropdown == null)
                return;
            int selected = 0;
            if (!string.IsNullOrWhiteSpace(value))
            {
                for (int i = 1; i < options.Length; ++i)
                {
                    if (string.Equals(options[i], value, StringComparison.Ordinal))
                    {
                        selected = i;
                        break;
                    }
                }
            }
            dropdown.Select(selected);
        }

        private static string Signature(IEnumerable<CharacterWearablePartSelection> values)
        {
            return string.Join("|", values.OrderBy(x => x.slotId).Select(x => x.slotId + ":" + x.optionId));
        }

        private static bool TryExtractSlotId(string name, out ushort slotId)
        {
            slotId = 0;
            if (string.IsNullOrEmpty(name))
                return false;
            for (int i = 0; i + 2 < name.Length; ++i)
            {
                if (name[i] != '_' || !char.IsDigit(name[i + 1]) || !char.IsDigit(name[i + 2]))
                    continue;
                if (ushort.TryParse(name.Substring(i + 1, 2), out ushort parsed) && parsed != 0)
                {
                    slotId = parsed;
                    return true;
                }
            }
            return false;
        }

        private void SetMessage(string value, bool success)
        {
            if (_messageText == null)
                return;
            _messageText.text = value ?? string.Empty;
            _messageText.color = success
                ? new Color(0.65f, 1f, 0.7f, 1f)
                : new Color(1f, 0.55f, 0.5f, 1f);
        }

        private static int ParseInt(InputField field, int fallback)
        {
            return field != null && int.TryParse(field.text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value : fallback;
        }

        private static float ParseFloat(InputField field, float fallback)
        {
            return field != null && float.TryParse(field.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                   !float.IsNaN(value) && !float.IsInfinity(value)
                ? value : fallback;
        }

        private static void SetInput(InputField field, string value)
        {
            if (field != null)
                field.text = value ?? string.Empty;
        }

        private static Transform FindRecursive(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; ++i)
            {
                Transform found = FindRecursive(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static Text CreateText(Transform parent, string name, string value, int size, FontStyle style)
        {
            GameObject go = CreateUi(parent, name);
            go.AddComponent<CanvasRenderer>();
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private static Button CreateButton(Transform parent, string name, string label, float width, Action action)
        {
            GameObject go = CreateUi(parent, name);
            go.AddComponent<CanvasRenderer>();
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.14f, 0.18f, 0.25f, 1f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            Text text = CreateText(go.transform, "Label", label, 10, FontStyle.Bold);
            text.alignment = TextAnchor.MiddleCenter;
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;
            button.onClick.AddListener(() => action?.Invoke());
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, 30f);
            return button;
        }

        private static InputField CreateLabeledInput(Transform parent, string label, string initial, float x, float y, float width)
        {
            GameObject group = CreateUi(parent, label.Replace(" ", string.Empty));
            SetRect(group.GetComponent<RectTransform>(), x, y, width, 48f);
            Text title = CreateText(group.transform, "Label", label, 9, FontStyle.Normal);
            SetRect(title.rectTransform, 0f, 0f, width, 18f);
            InputField input = CreateInput(group.transform, "Input", initial);
            SetRect(input.GetComponent<RectTransform>(), 0f, -20f, width, 26f);
            return input;
        }

        private static InputField CreateInput(Transform parent, string name, string initial)
        {
            GameObject go = CreateUi(parent, name);
            go.AddComponent<CanvasRenderer>();
            Image image = go.AddComponent<Image>();
            image.color = FieldColor;
            InputField input = go.AddComponent<InputField>();
            Text text = CreateText(go.transform, "Text", initial, 10, FontStyle.Normal);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = new Vector2(6f, 2f);
            text.rectTransform.offsetMax = new Vector2(-6f, -2f);
            input.textComponent = text;
            input.targetGraphic = image;
            input.text = initial;
            return input;
        }

        private static OutfitAuthoringDropdown CreateDropdown(Transform parent, string name, float width)
        {
            GameObject root = CreateUi(parent, name);
            Button open = CreateButton(root.transform, "Open", "(None)", width, null);
            RectTransform openRect = open.GetComponent<RectTransform>();
            openRect.anchorMin = Vector2.zero;
            openRect.anchorMax = Vector2.one;
            openRect.offsetMin = Vector2.zero;
            openRect.offsetMax = Vector2.zero;
            Text selectedText = open.GetComponentInChildren<Text>(true);

            GameObject optionsPanel = CreateUi(root.transform, "OptionsPanel");
            optionsPanel.AddComponent<CanvasRenderer>();
            Image image = optionsPanel.AddComponent<Image>();
            image.color = new Color(0.07f, 0.08f, 0.1f, 0.995f);
            RectTransform optionsRect = optionsPanel.GetComponent<RectTransform>();
            optionsRect.anchorMin = new Vector2(0f, 0f);
            optionsRect.anchorMax = new Vector2(1f, 0f);
            optionsRect.pivot = new Vector2(0.5f, 1f);
            optionsRect.anchoredPosition = new Vector2(0f, -2f);
            optionsRect.sizeDelta = new Vector2(0f, 230f);

            ScrollRect scroll = optionsPanel.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            GameObject viewport = CreateUi(optionsPanel.transform, "Viewport");
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(3f, 3f);
            viewportRect.offsetMax = new Vector2(-3f, -3f);
            viewport.AddComponent<RectMask2D>();

            GameObject content = CreateUi(viewport.transform, "Content");
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 40f);
            scroll.viewport = viewportRect;
            scroll.content = contentRect;

            Button template = CreateButton(content.transform, "OptionTemplate", "Option", width - 12f, null);
            template.gameObject.SetActive(false);

            OutfitAuthoringDropdown dropdown = root.AddComponent<OutfitAuthoringDropdown>();
            // The existing dropdown intentionally exposes no setup API; serialize the same private fields
            // through reflection once at runtime so all actual option behavior remains in the existing class.
            SetPrivate(dropdown, "openButton", open);
            SetPrivate(dropdown, "selectedText", selectedText);
            SetPrivate(dropdown, "optionsPanel", optionsPanel);
            SetPrivate(dropdown, "optionsContainer", contentRect);
            SetPrivate(dropdown, "optionTemplate", template);
            return dropdown;
        }

        private static void SetPrivate(object instance, string fieldName, object value)
        {
            var field = instance.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field?.SetValue(instance, value);
        }

        private static GameObject CreateUi(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void SetRect(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
