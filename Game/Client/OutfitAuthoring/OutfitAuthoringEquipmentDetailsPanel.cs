using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    [Serializable]
    public sealed class OutfitAuthoringStatModifierValue
    {
        public string statId = string.Empty;
        public float additive;
        public float multiplier = 1f;
    }

    [Serializable]
    public sealed class OutfitAuthoringEquipmentDetailsData
    {
        public string definitionId = string.Empty;
        public string equipmentSlotId = string.Empty;
        public int maxStack = 1;
        public float weight;
        public int maxDurability;
        public string[] tags = Array.Empty<string>();
        public OutfitAuthoringStatModifierValue[] statModifiers =
            Array.Empty<OutfitAuthoringStatModifierValue>();
    }

    public sealed class OutfitAuthoringEquipmentAuthoringOptions
    {
        public string[] tags = Array.Empty<string>();
        public string[] statIds = Array.Empty<string>();
    }

    public sealed class OutfitAuthoringEquipmentCacheStatus
    {
        public string label = "NOT CHECKED";
        public string detail = string.Empty;
    }

    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringEquipmentDetailsPanel : MonoBehaviour
    {
        public static Func<OutfitAuthoringEquipmentAuthoringOptions> EditorOptionsProvider;
        public static Func<string, OutfitAuthoringEquipmentDetailsData> EditorExistingItemProvider;
        public static Func<string, OutfitAuthoringEquipmentCacheStatus> EditorCacheStatusProvider;

        [SerializeField] private Button foldoutButton;
        [SerializeField] private Text foldoutLabel;
        [SerializeField] private GameObject bodyRoot;

        [SerializeField] private Text definitionIdText;
        [SerializeField] private Text kindSubtypeText;
        [SerializeField] private Text slotText;
        [SerializeField] private Text cacheStatusText;

        [SerializeField] private InputField maxStackInput;
        [SerializeField] private InputField weightInput;
        [SerializeField] private InputField maxDurabilityInput;

        [SerializeField] private OutfitAuthoringDropdown tag1Dropdown;
        [SerializeField] private OutfitAuthoringDropdown tag2Dropdown;
        [SerializeField] private OutfitAuthoringDropdown tag3Dropdown;

        [SerializeField] private OutfitAuthoringDropdown stat1Dropdown;
        [SerializeField] private InputField stat1AdditiveInput;
        [SerializeField] private InputField stat1MultiplierInput;

        [SerializeField] private OutfitAuthoringDropdown stat2Dropdown;
        [SerializeField] private InputField stat2AdditiveInput;
        [SerializeField] private InputField stat2MultiplierInput;

        [SerializeField] private OutfitAuthoringDropdown stat3Dropdown;
        [SerializeField] private InputField stat3AdditiveInput;
        [SerializeField] private InputField stat3MultiplierInput;

        private OutfitAuthoringController _controller;
        private OutfitAuthoringEquipmentAuthoringOptions _options =
            new OutfitAuthoringEquipmentAuthoringOptions();

        private string _loadedDefinitionId = string.Empty;
        private bool _expanded = true;

        private void Start()
        {
            _controller = FindObjectOfType<OutfitAuthoringController>();

            if (foldoutButton != null)
            {
                foldoutButton.onClick.RemoveListener(ToggleFoldout);
                foldoutButton.onClick.AddListener(ToggleFoldout);
            }

            LoadOptions();
            ResetForNew();
            RefreshFromController(force: true);
            ApplyFoldout();
        }

        private void Update()
        {
            RefreshFromController(force: false);
        }

        public OutfitAuthoringEquipmentDetailsData Capture()
        {
            return new OutfitAuthoringEquipmentDetailsData
            {
                definitionId = _loadedDefinitionId ?? string.Empty,
                equipmentSlotId = ReadCurrentSlot(),
                maxStack = Mathf.Max(1, ParseInt(maxStackInput, 1)),
                weight = Mathf.Max(0f, ParseFloat(weightInput, 0f)),
                maxDurability = Mathf.Max(0, ParseInt(maxDurabilityInput, 0)),
                tags = CaptureTags(),
                statModifiers = CaptureModifiers(),
            };
        }

        public void NotifySaved(
            string definitionId,
            string equipmentSlotId)
        {
            _loadedDefinitionId = definitionId ?? string.Empty;

            if (definitionIdText != null)
                definitionIdText.text = string.IsNullOrWhiteSpace(_loadedDefinitionId)
                    ? "Definition ID: assigned on create"
                    : "Definition ID: " + _loadedDefinitionId;

            if (!string.IsNullOrWhiteSpace(equipmentSlotId) && slotText != null)
                slotText.text = "Equipment Slot: " + equipmentSlotId;

            RefreshCacheStatus();
        }

        private void RefreshFromController(bool force)
        {
            if (_controller == null)
                _controller = FindObjectOfType<OutfitAuthoringController>();

            string definitionId =
                _controller != null && _controller.IsEditingExisting
                    ? _controller.EditingDefinitionId
                    : string.Empty;

            if (!force &&
                string.Equals(
                    definitionId ?? string.Empty,
                    _loadedDefinitionId ?? string.Empty,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(definitionId))
            {
                ResetForNew();
                return;
            }

            LoadExisting(definitionId);
        }

        private void LoadOptions()
        {
            try
            {
                _options = EditorOptionsProvider != null
                    ? EditorOptionsProvider.Invoke()
                    : new OutfitAuthoringEquipmentAuthoringOptions();
            }
            catch
            {
                _options = new OutfitAuthoringEquipmentAuthoringOptions();
            }

            string[] tags = PrependNone(_options.tags);
            string[] stats = PrependNone(_options.statIds);

            tag1Dropdown?.SetOptions(tags, 0);
            tag2Dropdown?.SetOptions(tags, 0);
            tag3Dropdown?.SetOptions(tags, 0);

            stat1Dropdown?.SetOptions(stats, 0);
            stat2Dropdown?.SetOptions(stats, 0);
            stat3Dropdown?.SetOptions(stats, 0);
        }

        private void ResetForNew()
        {
            _loadedDefinitionId = string.Empty;

            if (definitionIdText != null)
                definitionIdText.text = "Definition ID: assigned on create";
            if (kindSubtypeText != null)
                kindSubtypeText.text = "Kind: Equipment   |   Subtype: Armor";
            if (slotText != null)
                slotText.text = "Equipment Slot: uses the existing Outfit Builder slot dropdown";
            if (cacheStatusText != null)
                cacheStatusText.text = "Client Cache: not applicable until item exists";

            SetInput(maxStackInput, "1");
            SetInput(weightInput, "0");
            SetInput(maxDurabilityInput, "0");

            SelectByValue(tag1Dropdown, PrependNone(_options.tags), string.Empty);
            SelectByValue(tag2Dropdown, PrependNone(_options.tags), string.Empty);
            SelectByValue(tag3Dropdown, PrependNone(_options.tags), string.Empty);

            ResetModifier(stat1Dropdown, stat1AdditiveInput, stat1MultiplierInput);
            ResetModifier(stat2Dropdown, stat2AdditiveInput, stat2MultiplierInput);
            ResetModifier(stat3Dropdown, stat3AdditiveInput, stat3MultiplierInput);
        }

        private void LoadExisting(string definitionId)
        {
            OutfitAuthoringEquipmentDetailsData data = null;
            try
            {
                if (EditorExistingItemProvider != null)
                    data = EditorExistingItemProvider.Invoke(definitionId);
            }
            catch
            {
                data = null;
            }

            _loadedDefinitionId = definitionId ?? string.Empty;

            if (definitionIdText != null)
                definitionIdText.text = "Definition ID: " + _loadedDefinitionId;
            if (kindSubtypeText != null)
                kindSubtypeText.text = "Kind: Equipment   |   Subtype: Armor";

            if (data == null)
            {
                if (slotText != null)
                    slotText.text = "Equipment Slot: server item details unavailable";
                RefreshCacheStatus();
                return;
            }

            if (slotText != null)
                slotText.text = "Equipment Slot: " +
                    (string.IsNullOrWhiteSpace(data.equipmentSlotId)
                        ? "(none)"
                        : data.equipmentSlotId);

            SetInput(maxStackInput, Mathf.Max(1, data.maxStack).ToString(CultureInfo.InvariantCulture));
            SetInput(weightInput, Mathf.Max(0f, data.weight).ToString("0.###", CultureInfo.InvariantCulture));
            SetInput(maxDurabilityInput, Mathf.Max(0, data.maxDurability).ToString(CultureInfo.InvariantCulture));

            string[] tagOptions = PrependNone(_options.tags);
            string[] sourceTags = data.tags ?? Array.Empty<string>();
            SelectByValue(tag1Dropdown, tagOptions, sourceTags.Length > 0 ? sourceTags[0] : string.Empty);
            SelectByValue(tag2Dropdown, tagOptions, sourceTags.Length > 1 ? sourceTags[1] : string.Empty);
            SelectByValue(tag3Dropdown, tagOptions, sourceTags.Length > 2 ? sourceTags[2] : string.Empty);

            OutfitAuthoringStatModifierValue[] mods =
                data.statModifiers ?? Array.Empty<OutfitAuthoringStatModifierValue>();

            LoadModifier(
                stat1Dropdown,
                stat1AdditiveInput,
                stat1MultiplierInput,
                mods.Length > 0 ? mods[0] : null);

            LoadModifier(
                stat2Dropdown,
                stat2AdditiveInput,
                stat2MultiplierInput,
                mods.Length > 1 ? mods[1] : null);

            LoadModifier(
                stat3Dropdown,
                stat3AdditiveInput,
                stat3MultiplierInput,
                mods.Length > 2 ? mods[2] : null);

            RefreshCacheStatus();
        }

        private void RefreshCacheStatus()
        {
            if (cacheStatusText == null)
                return;

            if (string.IsNullOrWhiteSpace(_loadedDefinitionId))
            {
                cacheStatusText.text = "Client Cache: not applicable until item exists";
                return;
            }

            OutfitAuthoringEquipmentCacheStatus status = null;
            try
            {
                if (EditorCacheStatusProvider != null)
                    status = EditorCacheStatusProvider.Invoke(_loadedDefinitionId);
            }
            catch
            {
                status = null;
            }

            cacheStatusText.text = status == null
                ? "Client Cache: unavailable"
                : "Client Cache: " + status.label +
                  (string.IsNullOrWhiteSpace(status.detail)
                      ? string.Empty
                      : " | " + status.detail);
        }

        private string[] CaptureTags()
        {
            string[] labels = _options.tags ?? Array.Empty<string>();
            var values = new System.Collections.Generic.List<string>(3);

            AddSelected(values, tag1Dropdown, labels);
            AddSelected(values, tag2Dropdown, labels);
            AddSelected(values, tag3Dropdown, labels);

            return values.ToArray();
        }

        private OutfitAuthoringStatModifierValue[] CaptureModifiers()
        {
            string[] stats = _options.statIds ?? Array.Empty<string>();
            var values =
                new System.Collections.Generic.List<OutfitAuthoringStatModifierValue>(3);

            AddModifier(
                values,
                stat1Dropdown,
                stats,
                stat1AdditiveInput,
                stat1MultiplierInput);

            AddModifier(
                values,
                stat2Dropdown,
                stats,
                stat2AdditiveInput,
                stat2MultiplierInput);

            AddModifier(
                values,
                stat3Dropdown,
                stats,
                stat3AdditiveInput,
                stat3MultiplierInput);

            return values.ToArray();
        }

        private static void AddSelected(
            System.Collections.Generic.List<string> values,
            OutfitAuthoringDropdown dropdown,
            string[] source)
        {
            if (dropdown == null)
                return;

            int index = dropdown.SelectedIndex - 1;
            if (index < 0 || index >= source.Length)
                return;

            string value = source[index];
            if (string.IsNullOrWhiteSpace(value) || values.Contains(value))
                return;

            values.Add(value);
        }

        private static void AddModifier(
            System.Collections.Generic.List<OutfitAuthoringStatModifierValue> values,
            OutfitAuthoringDropdown dropdown,
            string[] stats,
            InputField additive,
            InputField multiplier)
        {
            if (dropdown == null)
                return;

            int index = dropdown.SelectedIndex - 1;
            if (index < 0 || index >= stats.Length)
                return;

            string statId = stats[index];
            if (string.IsNullOrWhiteSpace(statId))
                return;

            values.Add(new OutfitAuthoringStatModifierValue
            {
                statId = statId,
                additive = ParseFloat(additive, 0f),
                multiplier = ParseFloat(multiplier, 1f),
            });
        }

        private void LoadModifier(
            OutfitAuthoringDropdown dropdown,
            InputField additive,
            InputField multiplier,
            OutfitAuthoringStatModifierValue value)
        {
            string[] options = PrependNone(_options.statIds);
            SelectByValue(
                dropdown,
                options,
                value != null ? value.statId : string.Empty);

            SetInput(
                additive,
                (value != null ? value.additive : 0f)
                    .ToString("0.###", CultureInfo.InvariantCulture));

            SetInput(
                multiplier,
                (value != null ? value.multiplier : 1f)
                    .ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void ResetModifier(
            OutfitAuthoringDropdown dropdown,
            InputField additive,
            InputField multiplier)
        {
            if (dropdown != null)
                dropdown.Select(0);
            SetInput(additive, "0");
            SetInput(multiplier, "1");
        }

        private static string[] PrependNone(string[] source)
        {
            source ??= Array.Empty<string>();
            string[] result = new string[source.Length + 1];
            result[0] = "(None)";
            Array.Copy(source, 0, result, 1, source.Length);
            return result;
        }

        private static void SelectByValue(
            OutfitAuthoringDropdown dropdown,
            string[] optionsWithNone,
            string value)
        {
            if (dropdown == null)
                return;

            int index = 0;
            if (!string.IsNullOrWhiteSpace(value))
            {
                for (int i = 1; i < optionsWithNone.Length; ++i)
                {
                    if (string.Equals(
                        optionsWithNone[i],
                        value,
                        StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
            }

            dropdown.Select(index);
        }

        private string ReadCurrentSlot()
        {
            OutfitAuthoringEquipmentDetailsData existing = null;
            if (!string.IsNullOrWhiteSpace(_loadedDefinitionId) &&
                EditorExistingItemProvider != null)
            {
                try
                {
                    existing =
                        EditorExistingItemProvider.Invoke(_loadedDefinitionId);
                }
                catch
                {
                    existing = null;
                }
            }

            return existing?.equipmentSlotId ?? string.Empty;
        }

        private static int ParseInt(InputField field, int fallback)
        {
            if (field != null &&
                int.TryParse(
                    field.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value))
            {
                return value;
            }

            return fallback;
        }

        private static float ParseFloat(InputField field, float fallback)
        {
            if (field != null &&
                float.TryParse(
                    field.text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float value) &&
                !float.IsNaN(value) &&
                !float.IsInfinity(value))
            {
                return value;
            }

            return fallback;
        }

        private static void SetInput(InputField field, string value)
        {
            if (field != null)
                field.text = value ?? string.Empty;
        }

        private void ToggleFoldout()
        {
            _expanded = !_expanded;
            ApplyFoldout();
        }

        private void ApplyFoldout()
        {
            if (bodyRoot != null)
                bodyRoot.SetActive(_expanded);
            if (foldoutLabel != null)
                foldoutLabel.text = _expanded
                    ? "EQUIPMENT DETAILS ▼"
                    : "EQUIPMENT DETAILS ▶";
        }
    }

    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringColorFoldout : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private Text label;
        [SerializeField] private GameObject[] targets = Array.Empty<GameObject>();
        [SerializeField] private bool expanded;

        private void Awake()
        {
            if (button != null)
            {
                button.onClick.RemoveListener(Toggle);
                button.onClick.AddListener(Toggle);
            }

            Apply();
        }

        private void Toggle()
        {
            expanded = !expanded;
            Apply();
        }

        private void Apply()
        {
            GameObject[] values = targets ?? Array.Empty<GameObject>();
            for (int i = 0; i < values.Length; ++i)
                if (values[i] != null)
                    values[i].SetActive(expanded);

            if (label != null)
                label.text = expanded ? "COLORS ▼" : "COLORS ▶";
        }
    }
}
