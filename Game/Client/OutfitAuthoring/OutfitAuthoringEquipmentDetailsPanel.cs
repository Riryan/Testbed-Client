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
        public string state = "NOT CHECKED";
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
    /// V7 recovery gate:
    /// Armor.json -> one current server item -> Previous/Next -> Load/Edit -> mannequin.
    /// No existing-item dropdown browsing, search filtering, or cache dependency is required.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringEquipmentDetailsPanel : MonoBehaviour
    {
        public static Func<OutfitWorkbenchServerItem[]> EditorServerItemsProvider;
        public static Func<OutfitWorkbenchOptions> EditorOptionsProvider;
        public static Func<string, OutfitWorkbenchCacheStatus> EditorCacheStatusProvider;
        public static Func<string, OutfitWorkbenchEquipmentEdit, bool> EditorEquipmentUpdateHandler;
        public static Func<ushort[], ushort[], string> EditorSetDefaultBodyHandler;

        public static OutfitAuthoringEquipmentDetailsPanel Active { get; private set; }

        private OutfitAuthoringController _controller;
        private CharacterVisualProfile _profile;

        private OutfitWorkbenchServerItem[] _serverItems = Array.Empty<OutfitWorkbenchServerItem>();
        private int _serverIndex = -1;

        private OutfitAuthoringDropdown _displayDropdown;
        private InputField _search;
        private Button _newButton;
        private Button _loadButton;
        private Button _prevButton;
        private Button _nextButton;
        private Button _clearMeshesButton;

        private Text _currentSelectionText;
        private Text _serverText;
        private Text _cacheText;
        private Text _presentationText;
        private Text _messageText;

        private RectTransform _equipmentDetailsPanel;
        private Transform _colorPanel;

        public OutfitWorkbenchEquipmentEdit CaptureEquipmentEdit()
        {
            // Equipment-detail authoring is intentionally out of this recovery gate.
            // Existing save wrapper still receives a valid neutral payload.
            return new OutfitWorkbenchEquipmentEdit();
        }

        public void NotifyServerSaveCompleted(string definitionId)
        {
            RefreshArmorItems(definitionId);
        }

        private void Awake()
        {
            Active = this;
            _controller = FindObjectOfType<OutfitAuthoringController>();
            _profile = CharacterVisualProfileRegistry.Default;

            _colorPanel = FindRecursive(transform, "ColorPanel");
            if (_colorPanel != null)
                _colorPanel.gameObject.SetActive(false);

            _equipmentDetailsPanel =
                FindRecursive(transform, "EquipmentDetailsPanel") as RectTransform;
            if (_equipmentDetailsPanel != null)
                _equipmentDetailsPanel.gameObject.SetActive(false);

            _displayDropdown =
                FindRecursive(transform, "WorkbenchExistingDropdown")
                    ?.GetComponent<OutfitAuthoringDropdown>();

            _search =
                FindRecursive(transform, "WorkbenchExistingSearch")
                    ?.GetComponent<InputField>();

            _newButton =
                FindRecursive(transform, "WorkbenchNew")
                    ?.GetComponent<Button>();

            _loadButton =
                FindRecursive(transform, "WorkbenchLoad")
                    ?.GetComponent<Button>();

            _prevButton =
                FindRecursive(transform, "WorkbenchRefresh")
                    ?.GetComponent<Button>();

            _nextButton =
                FindRecursive(transform, "WorkbenchSetBase")
                    ?.GetComponent<Button>();

            _clearMeshesButton =
                FindRecursive(transform, "ClearMeshSelectionsButton")
                    ?.GetComponent<Button>();

            _currentSelectionText =
                FindRecursive(transform, "WorkbenchCurrentSelection")
                    ?.GetComponent<Text>();

            _serverText =
                FindRecursive(transform, "WorkbenchServerStatus")
                    ?.GetComponent<Text>();

            _cacheText =
                FindRecursive(transform, "WorkbenchCacheStatus")
                    ?.GetComponent<Text>();

            _presentationText =
                FindRecursive(transform, "WorkbenchPresentationStatus")
                    ?.GetComponent<Text>();

            _messageText =
                FindRecursive(transform, "WorkbenchMessage")
                    ?.GetComponent<Text>();

            if (_search != null)
                _search.gameObject.SetActive(false);

            WireButtons();
            RefreshArmorItems();
        }

        private void Start()
        {
            if (_controller == null)
                _controller = FindObjectOfType<OutfitAuthoringController>();

            if (_controller != null)
            {
                _controller.Saved -= OnSaved;
                _controller.Saved += OnSaved;
            }
        }

        private void OnDestroy()
        {
            if (Active == this)
                Active = null;

            if (_controller != null)
                _controller.Saved -= OnSaved;
        }

        private void WireButtons()
        {
            BindButton(_newButton, "NEW", BeginNew);
            BindButton(_loadButton, "LOAD / EDIT", LoadCurrent);
            BindButton(_prevButton, "< PREV", Previous);
            BindButton(_nextButton, "NEXT >", Next);

            if (_clearMeshesButton != null)
            {
                _clearMeshesButton.onClick.RemoveAllListeners();
                _clearMeshesButton.onClick.AddListener(() =>
                {
                    _controller ??= FindObjectOfType<OutfitAuthoringController>();
                    _controller?.ClearMeshSelectionsToDefaultBase();
                });
            }
        }

        private static void BindButton(Button button, string label, Action action)
        {
            if (button == null)
                return;

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => action?.Invoke());

            Text text = button.GetComponentInChildren<Text>(true);
            if (text != null)
                text.text = label;
        }

        private void RefreshArmorItems(string preferDefinitionId = null)
        {
            try
            {
                if (EditorServerItemsProvider == null)
                    throw new InvalidOperationException(
                        "Armor provider is not bound. OutfitAuthoringEquipmentDetailsBridge did not initialize.");

                _serverItems =
                    EditorServerItemsProvider.Invoke() ??
                    Array.Empty<OutfitWorkbenchServerItem>();

                if (_serverItems.Length == 0)
                    throw new InvalidOperationException(
                        "Armor provider returned zero items.");

                int selected = 0;

                if (!string.IsNullOrWhiteSpace(preferDefinitionId))
                {
                    int found = Array.FindIndex(
                        _serverItems,
                        x => x != null &&
                             string.Equals(
                                 x.definitionId,
                                 preferDefinitionId,
                                 StringComparison.OrdinalIgnoreCase));

                    if (found >= 0)
                        selected = found;
                }

                _serverIndex = Mathf.Clamp(selected, 0, _serverItems.Length - 1);
                ShowCurrentServerItem();

                SetMessage(
                    $"Armor.json loaded: {_serverItems.Length} item(s). Use PREV / NEXT, then LOAD / EDIT.",
                    true);
            }
            catch (Exception ex)
            {
                _serverItems = Array.Empty<OutfitWorkbenchServerItem>();
                _serverIndex = -1;

                if (_displayDropdown != null)
                    _displayDropdown.SetOptions(
                        new[] { "ARMOR LOAD FAILED" },
                        0);

                if (_currentSelectionText != null)
                    _currentSelectionText.text = "ARMOR BROWSE: FAILED";

                if (_serverText != null)
                    _serverText.text = "Server Armor: FAILED";

                if (_cacheText != null)
                    _cacheText.text = string.Empty;

                if (_presentationText != null)
                    _presentationText.text = string.Empty;

                SetMessage(ex.Message, false);
            }
        }

        private void ShowCurrentServerItem()
        {
            OutfitWorkbenchServerItem item = CurrentItem();
            if (item == null)
                return;

            string label =
                $"{_serverIndex + 1}/{_serverItems.Length}  " +
                $"{item.displayName}  [{item.definitionId}]";

            if (_displayDropdown != null)
                _displayDropdown.SetOptions(new[] { label }, 0);

            if (_currentSelectionText != null)
                _currentSelectionText.text =
                    $"ARMOR ITEM {_serverIndex + 1} / {_serverItems.Length}";

            if (_serverText != null)
                _serverText.text =
                    $"Server: {item.displayName} | {item.definitionId} | P{item.presentationId} | Slot={item.equipmentSlotId}";

            if (_cacheText != null)
                _cacheText.text = "Client Cache: ignored for this recovery gate";

            CharacterWearableSetDefinition wearable = FindWearable(item.definitionId, item.presentationId);

            if (_presentationText != null)
            {
                _presentationText.text = wearable != null
                    ? $"Presentation: FOUND | P{wearable.presentationId} | {wearable.parts?.Length ?? 0} mesh override(s)"
                    : "Presentation: MISSING CharacterWearableSetDefinition";
            }
        }

        private OutfitWorkbenchServerItem CurrentItem()
        {
            if (_serverIndex < 0 || _serverIndex >= _serverItems.Length)
                return null;

            return _serverItems[_serverIndex];
        }

        private CharacterWearableSetDefinition FindWearable(
            string definitionId,
            ushort presentationId = 0)
        {
            _profile ??= CharacterVisualProfileRegistry.Default;
            if (_profile == null || string.IsNullOrWhiteSpace(definitionId))
                return null;

            IReadOnlyList<CharacterWearableSetDefinition> values =
                _profile.WearableSets;

            for (int i = 0; i < values.Count; ++i)
            {
                CharacterWearableSetDefinition wearable = values[i];
                if (wearable != null &&
                    string.Equals(
                        wearable.definitionId,
                        definitionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return wearable;
                }
            }

            // Migration fallback: old Outfit Builder entries used armor.* IDs.
            // The presentation ID is stable, so use it to recover the matching
            // wearable after the server item has been canonicalized to item.armor.*.
            if (presentationId != 0)
            {
                for (int i = 0; i < values.Count; ++i)
                {
                    CharacterWearableSetDefinition wearable = values[i];
                    if (wearable != null &&
                        wearable.presentationId == presentationId)
                    {
                        return wearable;
                    }
                }
            }

            return null;
        }

        private void Previous()
        {
            if (_serverItems.Length == 0)
                return;

            _serverIndex--;
            if (_serverIndex < 0)
                _serverIndex = _serverItems.Length - 1;

            ShowCurrentServerItem();
        }

        private void Next()
        {
            if (_serverItems.Length == 0)
                return;

            _serverIndex++;
            if (_serverIndex >= _serverItems.Length)
                _serverIndex = 0;

            ShowCurrentServerItem();
        }

        private void BeginNew()
        {
            _controller?.BeginNewItem();

            if (_presentationText != null)
                _presentationText.text =
                    "Presentation: NEW item will use current mannequin mesh selections.";

            SetMessage("New Item mode.", true);
        }

        private void LoadCurrent()
        {
            OutfitWorkbenchServerItem item = CurrentItem();
            if (item == null)
            {
                SetMessage("No Armor.json item is selected.", false);
                return;
            }

            _profile ??= CharacterVisualProfileRegistry.Default;
            _controller ??= FindObjectOfType<OutfitAuthoringController>();

            if (_profile == null || _controller == null)
            {
                SetMessage(
                    "CharacterVisualProfile or OutfitAuthoringController is unavailable.",
                    false);
                return;
            }

            CharacterWearableSetDefinition wearable =
                FindWearable(item.definitionId, item.presentationId);

            ushort slotPresentationId = 0;

            OutfitAuthoringEquipmentSlotOption[] slots =
                OutfitAuthoringController.EditorEquipmentSlotProvider != null
                    ? OutfitAuthoringController.EditorEquipmentSlotProvider.Invoke()
                    : Array.Empty<OutfitAuthoringEquipmentSlotOption>();

            for (int i = 0; i < slots.Length; ++i)
            {
                OutfitAuthoringEquipmentSlotOption slot = slots[i];
                if (slot != null &&
                    string.Equals(
                        slot.slotId,
                        item.equipmentSlotId,
                        StringComparison.Ordinal))
                {
                    slotPresentationId = slot.presentationSlotId;
                    break;
                }
            }

            bool loaded =
                _controller.LoadExistingServerItem(
                    item.definitionId,
                    item.displayName,
                    item.presentationId,
                    item.equipmentSlotId,
                    slotPresentationId,
                    wearable);

            if (!loaded)
            {
                SetMessage(
                    $"Armor item '{item.definitionId}' and its presentation mapping were found, " +
                    "but OutfitAuthoringController rejected the load.",
                    false);
                return;
            }

            if (_presentationText != null)
                _presentationText.text = wearable != null
                    ? $"Presentation: APPLIED | {wearable.definitionId} | P{wearable.presentationId}"
                    : "Presentation: NONE YET | first UPDATE EXISTING will attach one";

            SetMessage(
                $"Loaded '{item.displayName}' from Armor.json. Item Name and Equipment Slot now match the saved server item; wearable meshes were applied to the mannequin.",
                true);
        }

        private void OnSaved(OutfitAuthoringSaveResult result)
        {
            if (result.success)
                RefreshArmorItems(result.definitionId);
        }

        private void SetMessage(string value, bool success)
        {
            if (_messageText != null)
            {
                _messageText.gameObject.SetActive(true);
                _messageText.text = value ?? string.Empty;
                _messageText.color = success
                    ? new Color(0.65f, 1f, 0.7f, 1f)
                    : new Color(1f, 0.55f, 0.5f, 1f);
            }

            Debug.Log(
                success
                    ? "[OutfitAuthoring] " + value
                    : "[OutfitAuthoring] ERROR: " + value);
        }

        private static Transform FindRecursive(Transform root, string name)
        {
            if (root == null)
                return null;

            if (root.name == name)
                return root;

            for (int i = 0; i < root.childCount; ++i)
            {
                Transform found =
                    FindRecursive(root.GetChild(i), name);

                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
