#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Client.OutfitAuthoring;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using Player.Networking;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringIntegrityPanel : MonoBehaviour
    {
        [SerializeField] private OutfitAuthoringDropdown itemDropdown;
        [SerializeField] private Text modeText;
        [SerializeField] private Text summaryText;
        [SerializeField] private Text detailText;
        [SerializeField] private Text problemsButtonText;
        [SerializeField] private Button newItemButton;
        [SerializeField] private Button previousButton;
        [SerializeField] private Button nextButton;
        [SerializeField] private Button loadButton;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button problemsOnlyButton;
        [SerializeField] private Button collapseButton;
        [SerializeField] private Text collapseButtonText;
        [SerializeField] private GameObject bodyRoot;

        private enum AuditState
        {
            Matched,
            CacheNotLoaded,
            CacheStale,
            ServerMissing,
            CacheMissing,
            PresentationMissing,
            IdMismatch,
            SlotMismatch,
            DuplicateServer,
            DuplicatePresentation,
            BrokenPresentation,
        }

        private sealed class ServerItem
        {
            public string definitionId;
            public string displayName;
            public ushort presentationId;
            public string equipmentSlotId;
        }

        private sealed class Record
        {
            public string definitionId;
            public string displayName;
            public ServerItem server;
            public CharacterWearableSetDefinition wearable;
            public bool hasCache;
            public GameplayItemReferenceWire cache;
            public AuditState state;
            public string detail;
        }

        private static readonly Regex DefinitionIdRegex =
            new Regex("\"definitionId\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DisplayNameRegex =
            new Regex("\"displayName\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PresentationIdRegex =
            new Regex("\"presentationId\"\\s*:\\s*(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex EquipmentSlotsRegex =
            new Regex("\"allowedEquipmentSlots\"\\s*:\\s*\\[\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly List<Record> _records = new List<Record>();
        private readonly List<Record> _visible = new List<Record>();
        private OutfitAuthoringController _controller;
        private bool _problemsOnly;
        private bool _collapsed;
        private int _selectedVisibleIndex = -1;
        private long _serverRevision;
        private long _clientCacheRevision;
        private string _clientCacheSource = string.Empty;
        private readonly Dictionary<string, GameplayItemReferenceWire> _cacheItemsByDefinition =
            new Dictionary<string, GameplayItemReferenceWire>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, GameplayEquipmentSlotReferenceWire> _cacheSlotsByDataId =
            new Dictionary<ushort, GameplayEquipmentSlotReferenceWire>();

        private void Start()
        {
            _controller = FindObjectOfType<OutfitAuthoringController>();
            Bind();
            RefreshAudit();
            RefreshMode();
        }

        private void OnDestroy()
        {
            if (itemDropdown != null)
                itemDropdown.SelectionChanged -= OnDropdownChanged;

            if (_controller != null)
            {
                _controller.AuthoringModeChanged -= RefreshMode;
                _controller.Saved -= OnSaved;
            }
        }

        private void Bind()
        {
            if (itemDropdown != null)
            {
                itemDropdown.SelectionChanged -= OnDropdownChanged;
                itemDropdown.SelectionChanged += OnDropdownChanged;
            }

            if (_controller != null)
            {
                _controller.AuthoringModeChanged -= RefreshMode;
                _controller.AuthoringModeChanged += RefreshMode;
                _controller.Saved -= OnSaved;
                _controller.Saved += OnSaved;
            }

            BindButton(newItemButton, BeginNew);
            BindButton(previousButton, Previous);
            BindButton(nextButton, Next);
            BindButton(loadButton, LoadSelected);
            BindButton(refreshButton, RefreshAudit);
            BindButton(problemsOnlyButton, ToggleProblemsOnly);
            BindButton(collapseButton, ToggleCollapsed);
        }

        private static void BindButton(Button button, Action action)
        {
            if (button == null)
                return;

            button.onClick.RemoveAllListeners();
            if (action != null)
                button.onClick.AddListener(() => action());
        }

        private void OnSaved(OutfitAuthoringSaveResult result)
        {
            if (result.success)
                RefreshAudit();
            RefreshMode();
        }

        private void BeginNew()
        {
            if (_controller == null)
                _controller = FindObjectOfType<OutfitAuthoringController>();
            _controller?.BeginNewItem();
            RefreshMode();
        }

        private void RefreshMode()
        {
            if (modeText == null)
                return;

            if (_controller == null)
                _controller = FindObjectOfType<OutfitAuthoringController>();

            modeText.text = _controller != null && _controller.IsEditingExisting
                ? $"MODE: EDIT EXISTING  |  {_controller.EditingDefinitionId}  |  P{_controller.EditingPresentationId}"
                : "MODE: NEW ITEM";
        }

        private void RefreshAudit()
        {
            _records.Clear();

            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(
                CharacterVisualCatalogBuilder.OutputAssetPath);
            if (profile == null)
            {
                SetSummary("CharacterVisualProfile.asset missing.");
                SetDetail("Cannot audit client presentation.");
                RebuildVisible();
                return;
            }

            List<ServerItem> serverItems = new List<ServerItem>();
            string serverPathText = "not configured";
            _serverRevision = 0;

            if (OutfitAuthoringSaveBridge.TryGetReviewPaths(out OutfitAuthoringSaveBridge.ReviewPaths paths))
            {
                _serverRevision = paths.revision;
                serverPathText = paths.contentRoot;
                try
                {
                    serverItems = ReadServerItems(paths.armorPath);
                }
                catch (Exception ex)
                {
                    SetSummary("Server Armor read failed: " + ex.Message);
                }
            }

            LoadClientCacheForAudit();

            CharacterWearableSetDefinition[] wearables =
                (profile.WearableSets ?? Array.Empty<CharacterWearableSetDefinition>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.definitionId))
                .ToArray();

            BuildRecords(serverItems, wearables, profile);

            int matched = _records.Count(x => x.state == AuditState.Matched);
            int problems = _records.Count - matched;
            string cacheState =
                _clientCacheRevision <= 0 ? "not loaded"
                : _serverRevision > 0 && _clientCacheRevision < _serverRevision
                    ? $"{_clientCacheRevision} STALE vs server {_serverRevision}"
                    : _clientCacheRevision.ToString();

            SetSummary(
                $"{_records.Count} item(s) | {matched} matched | {problems} problem(s)\n" +
                $"Server revision: {_serverRevision} | Client gameplay cache: {cacheState}\n" +
                $"Cache source: {_clientCacheSource}\n" +
                $"Server Content: {serverPathText}");

            RebuildVisible();
            RefreshMode();
        }

        private void LoadClientCacheForAudit()
        {
            _cacheItemsByDefinition.Clear();
            _cacheSlotsByDataId.Clear();
            _clientCacheRevision = 0;
            _clientCacheSource = "not loaded";

            if (PlayerGameplaySettingsRuntime.Revision > 0)
            {
                _clientCacheRevision = PlayerGameplaySettingsRuntime.Revision;
                _clientCacheSource = "current PlayerGameplaySettingsRuntime";

                // The runtime cache does not expose enumeration, so records are resolved lazily
                // from authoritative/presentation keys inside BuildRecords. Equipment slots can
                // also resolve through the runtime when checking each cached item.
                return;
            }

            if (!PlayerEntityGameManager.TryReadLatestGameplaySettingsCacheForEditor(
                    out GameplaySettingsSnapshotMessage snapshot,
                    out string sourcePath,
                    out string detail))
            {
                _clientCacheSource = detail;
                return;
            }

            _clientCacheRevision = snapshot.revision;
            _clientCacheSource = Path.GetFileName(sourcePath) + " | " + detail;

            GameplayItemReferenceWire[] items = snapshot.items ?? Array.Empty<GameplayItemReferenceWire>();
            for (int i = 0; i < items.Length; ++i)
            {
                GameplayItemReferenceWire item = items[i];
                if (item.dataId != 0 && !string.IsNullOrWhiteSpace(item.definitionId))
                    _cacheItemsByDefinition[item.definitionId] = item;
            }

            GameplayEquipmentSlotReferenceWire[] slots =
                snapshot.equipmentSlots ?? Array.Empty<GameplayEquipmentSlotReferenceWire>();
            for (int i = 0; i < slots.Length; ++i)
            {
                GameplayEquipmentSlotReferenceWire slot = slots[i];
                if (slot.dataId != 0 && !string.IsNullOrWhiteSpace(slot.slotId))
                    _cacheSlotsByDataId[slot.dataId] = slot;
            }
        }

        private bool TryGetCachedItem(string definitionId, out GameplayItemReferenceWire item)
        {
            item = default;
            if (PlayerGameplaySettingsRuntime.Revision > 0)
                return PlayerGameplaySettingsRuntime.TryGetItem(definitionId, out item);
            return _cacheItemsByDefinition.TryGetValue(definitionId ?? string.Empty, out item);
        }

        private bool TryResolveCachedSlot(ushort dataId, out GameplayEquipmentSlotReferenceWire slot)
        {
            slot = default;
            if (PlayerGameplaySettingsRuntime.Revision > 0)
                return PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(dataId, out slot);
            return _cacheSlotsByDataId.TryGetValue(dataId, out slot);
        }

        private void BuildRecords(
            List<ServerItem> serverItems,
            CharacterWearableSetDefinition[] wearables,
            CharacterVisualProfile profile)
        {
            var serverByDef = serverItems
                .GroupBy(x => x.definitionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var wearableByDef = wearables
                .GroupBy(x => x.definitionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var keys = new HashSet<string>(serverByDef.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(wearableByDef.Keys);

            foreach (string key in keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                List<ServerItem> s = serverByDef.TryGetValue(key, out List<ServerItem> sv) ? sv : null;
                List<CharacterWearableSetDefinition> w = wearableByDef.TryGetValue(key, out List<CharacterWearableSetDefinition> wv) ? wv : null;
                ServerItem server = s?.FirstOrDefault();
                CharacterWearableSetDefinition wearable = w?.FirstOrDefault();
                bool hasCache = TryGetCachedItem(key, out GameplayItemReferenceWire cache);

                var record = new Record
                {
                    definitionId = key,
                    displayName = server?.displayName ?? wearable?.displayName ?? key,
                    server = server,
                    wearable = wearable,
                    hasCache = hasCache,
                    cache = cache,
                };

                if (s != null && s.Count > 1)
                {
                    record.state = AuditState.DuplicateServer;
                    record.detail = $"Server has {s.Count} Armor records for this definitionId.";
                }
                else if (w != null && w.Count > 1)
                {
                    record.state = AuditState.DuplicatePresentation;
                    record.detail = $"CharacterVisualProfile has {w.Count} wearables for this definitionId.";
                }
                else if (server == null)
                {
                    record.state = AuditState.ServerMissing;
                    record.detail = "Wearable presentation exists without authoritative server Armor content.";
                }
                else if (wearable == null)
                {
                    record.state = AuditState.PresentationMissing;
                    record.detail = "Authoritative server Armor content exists without wearable presentation.";
                }
                else if (_clientCacheRevision <= 0)
                {
                    record.state = AuditState.CacheNotLoaded;
                    record.detail = "The real client gameplay-settings cache is not loaded in this Play Mode session.";
                }
                else if (_serverRevision > 0 && _clientCacheRevision < _serverRevision)
                {
                    record.state = AuditState.CacheStale;
                    record.detail = $"Client cache revision {_clientCacheRevision} is behind server revision {_serverRevision}.";
                }
                else if (!hasCache)
                {
                    record.state = AuditState.CacheMissing;
                    record.detail = "Current client gameplay-settings cache has no item reference for this server definition.";
                }
                else if (server.presentationId != wearable.presentationId || server.presentationId != cache.presentationId)
                {
                    record.state = AuditState.IdMismatch;
                    record.detail = $"presentationId server/cache/presentation = {server.presentationId}/{cache.presentationId}/{wearable.presentationId}.";
                }
                else if (!CacheSlotMatches(server.equipmentSlotId, cache))
                {
                    record.state = AuditState.SlotMismatch;
                    record.detail = $"Server slot '{server.equipmentSlotId}' is not represented by the cached allowedSlotDataIds.";
                }
                else if (HasBrokenParts(wearable, profile, out string broken))
                {
                    record.state = AuditState.BrokenPresentation;
                    record.detail = broken;
                }
                else
                {
                    record.state = AuditState.Matched;
                    record.detail = "Server, current client gameplay cache and wearable presentation agree.";
                }

                _records.Add(record);
            }
        }

        private bool CacheSlotMatches(string serverSlot, GameplayItemReferenceWire cache)
        {
            if (string.IsNullOrWhiteSpace(serverSlot))
                return true;

            ushort[] dataIds = cache.allowedSlotDataIds ?? Array.Empty<ushort>();
            for (int i = 0; i < dataIds.Length; ++i)
            {
                if (TryResolveCachedSlot(dataIds[i], out GameplayEquipmentSlotReferenceWire slot) &&
                    string.Equals(slot.slotId, serverSlot, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private void RebuildVisible()
        {
            _visible.Clear();
            for (int i = 0; i < _records.Count; ++i)
                if (!_problemsOnly || _records[i].state != AuditState.Matched)
                    _visible.Add(_records[i]);

            var labels = new List<string>(_visible.Count);
            for (int i = 0; i < _visible.Count; ++i)
                labels.Add($"{StatePrefix(_visible[i].state)} {_visible[i].displayName}");

            _selectedVisibleIndex = _visible.Count > 0
                ? Mathf.Clamp(_selectedVisibleIndex < 0 ? 0 : _selectedVisibleIndex, 0, _visible.Count - 1)
                : -1;

            if (itemDropdown != null)
                itemDropdown.SetOptions(labels, _selectedVisibleIndex);
            if (problemsButtonText != null)
                problemsButtonText.text = _problemsOnly ? "SHOW ALL" : "PROBLEMS ONLY";

            RefreshSelected();
        }

        private void OnDropdownChanged(int index)
        {
            _selectedVisibleIndex = index;
            RefreshSelected();
        }

        private void Previous()
        {
            if (_visible.Count == 0) return;
            SelectVisible(_selectedVisibleIndex <= 0 ? _visible.Count - 1 : _selectedVisibleIndex - 1);
        }

        private void Next()
        {
            if (_visible.Count == 0) return;
            SelectVisible(_selectedVisibleIndex >= _visible.Count - 1 ? 0 : _selectedVisibleIndex + 1);
        }

        private void SelectVisible(int index)
        {
            if (_visible.Count == 0) return;
            _selectedVisibleIndex = Mathf.Clamp(index, 0, _visible.Count - 1);
            if (itemDropdown != null)
                itemDropdown.Select(_selectedVisibleIndex);
            else
                RefreshSelected();
        }

        private void RefreshSelected()
        {
            if (_selectedVisibleIndex < 0 || _selectedVisibleIndex >= _visible.Count)
            {
                SetDetail("No outfit/item selected.");
                if (loadButton != null) loadButton.interactable = false;
                return;
            }

            Record r = _visible[_selectedVisibleIndex];
            string server = r.server == null
                ? "MISSING"
                : $"P{r.server.presentationId} | Slot={Safe(r.server.equipmentSlotId)}";
            string cache = _clientCacheRevision <= 0
                ? "NOT LOADED"
                : !r.hasCache ? "MISSING" : $"P{r.cache.presentationId} | DataId={r.cache.dataId}";
            string presentation = r.wearable == null
                ? "MISSING"
                : $"P{r.wearable.presentationId} | Region={r.wearable.region}";

            SetDetail(
                $"{StateLabel(r.state)}\n" +
                $"{r.displayName}\n" +
                $"Definition: {r.definitionId}\n\n" +
                $"Server:       {server}\n" +
                $"Client Cache: {cache}\n" +
                $"Presentation: {presentation}\n\n" +
                r.detail);

            if (loadButton != null)
                loadButton.interactable = r.wearable != null && r.server != null;
        }

        private void LoadSelected()
        {
            if (_selectedVisibleIndex < 0 || _selectedVisibleIndex >= _visible.Count)
                return;

            Record r = _visible[_selectedVisibleIndex];
            if (r.wearable == null || r.server == null)
            {
                SetDetail(StateLabel(r.state) + "\nCannot enter edit mode until both server item and wearable exist.");
                return;
            }

            if (_controller == null)
                _controller = FindObjectOfType<OutfitAuthoringController>();
            if (_controller == null)
            {
                SetDetail("OutfitAuthoringController was not found.");
                return;
            }

            ushort slotPresentationId = 0;
            OutfitAuthoringEquipmentSlotOption[] slots = OutfitAuthoringSaveBridge.GetEquipmentSlotsForReview();
            for (int i = 0; i < slots.Length; ++i)
            {
                if (slots[i] != null && string.Equals(slots[i].slotId, r.server.equipmentSlotId, StringComparison.Ordinal))
                {
                    slotPresentationId = slots[i].presentationSlotId;
                    break;
                }
            }

            if (_controller.LoadExistingWearable(r.wearable, r.server.equipmentSlotId, slotPresentationId))
                RefreshMode();
        }

        private void ToggleProblemsOnly()
        {
            _problemsOnly = !_problemsOnly;
            _selectedVisibleIndex = 0;
            RebuildVisible();
        }

        private void ToggleCollapsed()
        {
            _collapsed = !_collapsed;
            if (bodyRoot != null) bodyRoot.SetActive(!_collapsed);
            if (collapseButtonText != null) collapseButtonText.text = _collapsed ? "OPEN" : "HIDE";
        }

        private static bool HasBrokenParts(CharacterWearableSetDefinition wearable, CharacterVisualProfile profile, out string detail)
        {
            detail = string.Empty;
            if (wearable == null || profile == null)
                return false;

            CharacterWearablePartSelection[] parts = wearable.parts ?? Array.Empty<CharacterWearablePartSelection>();
            for (int i = 0; i < parts.Length; ++i)
            {
                CharacterWearablePartSelection part = parts[i];
                if (part.slotId == 0 || part.optionId == 0 ||
                    !profile.TryGetOption(part.slotId, part.optionId, out CharacterVisualOptionDefinition option) ||
                    option == null || option.mesh == null)
                {
                    detail = $"Broken wearable mesh reference: slot {part.slotId}, option {part.optionId}.";
                    return true;
                }
            }
            return false;
        }

        private static List<ServerItem> ReadServerItems(string armorPath)
        {
            string json = File.ReadAllText(armorPath);
            List<string> objects = ExtractItemsArrayObjects(json);
            var result = new List<ServerItem>(objects.Count);

            for (int i = 0; i < objects.Count; ++i)
            {
                string item = objects[i];
                Match definition = DefinitionIdRegex.Match(item);
                Match presentation = PresentationIdRegex.Match(item);
                if (!definition.Success || !presentation.Success ||
                    !ushort.TryParse(presentation.Groups[1].Value, out ushort presentationId))
                    continue;

                Match display = DisplayNameRegex.Match(item);
                Match slot = EquipmentSlotsRegex.Match(item);
                result.Add(new ServerItem
                {
                    definitionId = definition.Groups[1].Value,
                    displayName = display.Success ? display.Groups[1].Value : definition.Groups[1].Value,
                    presentationId = presentationId,
                    equipmentSlotId = slot.Success ? slot.Groups[1].Value : string.Empty,
                });
            }
            return result;
        }

        private static List<string> ExtractItemsArrayObjects(string json)
        {
            var result = new List<string>();
            Match items = Regex.Match(json ?? string.Empty, "\"items\"\\s*:\\s*\\[", RegexOptions.CultureInvariant);
            if (!items.Success) return result;

            int arrayStart = json.IndexOf('[', items.Index);
            if (arrayStart < 0) return result;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            int start = -1;

            for (int i = arrayStart + 1; i < json.Length; ++i)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }
            return result;
        }

        private static string Safe(string value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

        private static string StatePrefix(AuditState state)
        {
            switch (state)
            {
                case AuditState.Matched: return "[OK]";
                case AuditState.CacheNotLoaded: return "[CACHE OFF]";
                case AuditState.CacheStale: return "[STALE]";
                case AuditState.ServerMissing: return "[SERVER?]";
                case AuditState.CacheMissing: return "[CACHE?]";
                case AuditState.PresentationMissing: return "[VIS?]";
                case AuditState.IdMismatch: return "[ID!]";
                case AuditState.SlotMismatch: return "[SLOT!]";
                case AuditState.DuplicateServer: return "[SERVER DUP]";
                case AuditState.DuplicatePresentation: return "[VIS DUP]";
                case AuditState.BrokenPresentation: return "[MESH!]";
                default: return "[?]";
            }
        }

        private static string StateLabel(AuditState state)
        {
            switch (state)
            {
                case AuditState.Matched: return "MATCHED";
                case AuditState.CacheNotLoaded: return "CLIENT CACHE NOT LOADED";
                case AuditState.CacheStale: return "CLIENT CACHE STALE";
                case AuditState.ServerMissing: return "SERVER MISSING";
                case AuditState.CacheMissing: return "CLIENT CACHE MISSING";
                case AuditState.PresentationMissing: return "PRESENTATION MISSING";
                case AuditState.IdMismatch: return "PRESENTATION ID MISMATCH";
                case AuditState.SlotMismatch: return "EQUIPMENT SLOT MISMATCH";
                case AuditState.DuplicateServer: return "DUPLICATE SERVER ID";
                case AuditState.DuplicatePresentation: return "DUPLICATE PRESENTATION ID";
                case AuditState.BrokenPresentation: return "BROKEN PRESENTATION";
                default: return state.ToString();
            }
        }

        private void SetSummary(string value)
        {
            if (summaryText != null) summaryText.text = value ?? string.Empty;
        }

        private void SetDetail(string value)
        {
            if (detailText != null) detailText.text = value ?? string.Empty;
        }
    }
}
#endif
