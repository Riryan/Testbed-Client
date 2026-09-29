using System;
using Cysharp.Threading.Tasks;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;
using Game.Client.UI.Root;
using Game.Client.UI.Gameplay;
using Game.Client.UI.Standalone;

namespace Game.Client.UI.PlayerItems
{
    /// <summary>
    /// Authored owner-only loot-window controller. The GameServer owns range, loot state,
    /// item creation, inventory mutation, and persistence. This component only presents
    /// the returned snapshot and sends Take / Take All intent.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerLootShell : MonoBehaviour
    {
        [SerializeField] private Text sourceLabel;
        [SerializeField] private Text feedbackLabel;
        [SerializeField] private Transform itemContent;
        [SerializeField] private WorldLootRowView rowTemplate;
        [SerializeField] private Button takeAllButton;

        private ClientUIRoot _root;
        private StandaloneClientUIRoot _standaloneRoot;
        private GameObject _standaloneWindowRoot;
        private PlayerEntityGameManager _manager;
        private WorldLootResponseMessage _latest;
        private bool _busy;
        private string _feedbackOverride;

        public bool IsOpen =>
            _standaloneWindowRoot != null
                ? _standaloneWindowRoot.activeSelf
                : _root != null && _root.Windows != null && _root.Windows.IsOpen(ClientUIPanelId.LootWindow);

        public void ConfigureForEditor(
            Text source,
            Text feedback,
            Transform content,
            WorldLootRowView template,
            Button takeAll)
        {
            sourceLabel = source;
            feedbackLabel = feedback;
            itemContent = content;
            rowTemplate = template;
            takeAllButton = takeAll;
        }

        public void BindRoot(ClientUIRoot root)
        {
            _root = root;
            if (_root == null)
                Close();
        }

        public void BindStandaloneRoot(StandaloneClientUIRoot root, GameObject windowRoot)
        {
            _standaloneRoot = root;
            _standaloneWindowRoot = windowRoot;
            if (_standaloneRoot == null || _standaloneWindowRoot == null)
                Close();
        }

        public void Bind(PlayerEntityGameManager manager)
        {
            if (_manager == manager)
                return;

            if (_manager != null)
                _manager.WorldLootSnapshotReceived -= OnWorldLootSnapshotReceived;

            _manager = manager;
            // StandaloneClientUIRoot is the always-active owner of the WorldLoot snapshot
            // handoff. Keep the legacy direct subscription only when this controller is
            // actually hosted by the legacy ClientUIRoot path.
            if (_manager != null && _standaloneRoot == null)
                _manager.WorldLootSnapshotReceived += OnWorldLootSnapshotReceived;
            else if (_manager == null)
                Close();
        }

        private void OnDestroy()
        {
            if (_manager != null)
                _manager.WorldLootSnapshotReceived -= OnWorldLootSnapshotReceived;
        }

        private void OnWorldLootSnapshotReceived(WorldLootResponseMessage snapshot)
        {
            Open(snapshot);
        }

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
            return;
#else
            if (takeAllButton != null)
            {
                takeAllButton.onClick.RemoveAllListeners();
                takeAllButton.onClick.AddListener(() => TakeAllAsync().Forget());
            }
#endif
        }

        public void Open(WorldLootResponseMessage snapshot)
        {
            if (!snapshot.success)
            {
                PresentFeedback($"LOOT: {snapshot.ResultCode}: {snapshot.error}");
                return;
            }

            _latest = snapshot;
            _feedbackOverride = string.Empty;
            Render();
            if (_standaloneWindowRoot != null)
            {
                _standaloneRoot?.PrepareForLootWindow(_standaloneWindowRoot);
                _standaloneWindowRoot.SetActive(true);
            }
            else if (_root?.Windows == null || !_root.Windows.Open(ClientUIPanelId.LootWindow))
            {
                Debug.LogWarning("[Loot] Authored LootWindow is unavailable. Restore the authored LootWindow binding.", this);
            }
        }

        public void Close()
        {
            if (_standaloneWindowRoot != null)
                _standaloneWindowRoot.SetActive(false);
            else
                _root?.Windows?.Close(ClientUIPanelId.LootWindow);
            _latest = default;
            _feedbackOverride = string.Empty;
            ClearRows();
        }

        private void Render()
        {
            if (sourceLabel != null)
                sourceLabel.text = string.IsNullOrWhiteSpace(_latest.sourceLabel)
                    ? "Loot"
                    : _latest.sourceLabel;

            WorldLootEntryWire[] entries = _latest.entries ?? Array.Empty<WorldLootEntryWire>();
            ClearRows();
            for (int i = 0; i < entries.Length; ++i)
            {
                WorldLootEntryWire entry = entries[i];
                if (entry.quantity <= 0 || rowTemplate == null || itemContent == null)
                    continue;

                WorldLootRowView row = Instantiate(rowTemplate, itemContent);
                row.name = $"Loot_{entry.entryIndex}_{entry.definitionId}";
                row.gameObject.SetActive(true);
                row.Bind(entry, OnTakeRequested);
            }

            bool empty = entries.Length == 0 || _latest.depleted;
            if (feedbackLabel != null)
            {
                feedbackLabel.text = !string.IsNullOrWhiteSpace(_feedbackOverride)
                    ? _feedbackOverride
                    : empty
                        ? "Nothing remains."
                        : "Select Take, or Take All.";
            }
            if (takeAllButton != null)
                takeAllButton.interactable = !_busy && !empty;
        }

        private void ClearRows()
        {
            if (itemContent == null)
                return;

            for (int i = itemContent.childCount - 1; i >= 0; --i)
            {
                Transform child = itemContent.GetChild(i);
                if (rowTemplate != null && child == rowTemplate.transform)
                    continue;
                Destroy(child.gameObject);
            }
        }

        private void OnTakeRequested(int entryIndex)
        {
            if (_busy)
                return;
            TakeEntryAsync(entryIndex).Forget();
        }

        private async UniTaskVoid TakeEntryAsync(int entryIndex)
        {
            if (_latest.stableId <= 0)
                return;
            if (!EnsureManagerForLoot())
                return;

            WorldLootEntryWire[] entries = _latest.entries ?? Array.Empty<WorldLootEntryWire>();
            WorldLootEntryWire chosen = default;
            bool found = false;
            for (int i = 0; i < entries.Length; ++i)
            {
                if (entries[i].entryIndex != entryIndex || entries[i].quantity <= 0)
                    continue;
                chosen = entries[i];
                found = true;
                break;
            }
            if (!found)
                return;

            _busy = true;
            _feedbackOverride = string.Empty;
            SetBusyPresentation($"Taking {chosen.displayName}…");
            try
            {
                WorldLootTakeResponseMessage response =
                    await _manager.RequestWorldLootTakeAsync(
                        _latest.stableId,
                        _latest.lootRevision,
                        chosen.entryIndex,
                        chosen.quantity);
                await UniTask.SwitchToMainThread();

                if (!response.success)
                {
                    _feedbackOverride = string.IsNullOrWhiteSpace(response.error)
                        ? "Loot transfer rejected."
                        : response.error;
                    Debug.LogWarning(
                        $"[Loot] Take rejected for object {_latest.stableId}, entry {chosen.entryIndex}: " +
                        $"{response.ResultCode}: {_feedbackOverride}",
                        this);
                    PresentFeedback(
                        $"LOOT: {response.ResultCode}: {_feedbackOverride}");
                    return;
                }

                ApplyTakeResponse(response);
                _feedbackOverride = string.Empty;
                PresentFeedback(
                    $"LOOT: Took {chosen.displayName} x{chosen.quantity}. Inventory saved authoritatively.");
            }
            finally
            {
                _busy = false;
                Render();
            }
        }

        private async UniTaskVoid TakeAllAsync()
        {
            if (_busy || _latest.stableId <= 0) return;
            if (!EnsureManagerForLoot()) return;

            _busy = true;
            _feedbackOverride = string.Empty;
            SetBusyPresentation("Taking all…");
            try
            {
                long stableId = _latest.stableId;
                WorldLootResponseMessage response = await _manager.RequestWorldLootTakeAllAsync(stableId, _latest.lootRevision);
                await UniTask.SwitchToMainThread();
                if (!response.success)
                {
                    _feedbackOverride = string.IsNullOrWhiteSpace(response.error) ? "Take All failed." : response.error;
                    PresentFeedback($"LOOT: Take All — {response.ResultCode}: {_feedbackOverride}");
                    return;
                }

                _latest = response;
                _feedbackOverride = string.Empty;
                PresentFeedback("LOOT: Took all available items. Inventory saved authoritatively.");
            }
            finally
            {
                _busy = false;
                Render();
            }
        }


        private void ApplyTakeResponse(WorldLootTakeResponseMessage response)
        {
            if (!response.success || response.stableId != _latest.stableId)
                return;

            WorldLootEntryWire[] source = _latest.entries ?? Array.Empty<WorldLootEntryWire>();
            var next = new System.Collections.Generic.List<WorldLootEntryWire>(source.Length);
            for (int i = 0; i < source.Length; ++i)
            {
                WorldLootEntryWire entry = source[i];
                if (entry.entryIndex == response.entryIndex)
                {
                    if (response.remainingQuantity > 0)
                    {
                        entry.quantity = response.remainingQuantity;
                        next.Add(entry);
                    }
                    continue;
                }
                next.Add(entry);
            }

            _latest.lootRevision = response.lootRevision;
            _latest.depleted = response.depleted;
            _latest.entries = next.ToArray();
        }



        private bool TryResolveRuntimeLootSourcePosition(out Vector3 worldPosition)
        {
            worldPosition = default;
            return _latest.stableId > 0 &&
                   EnsureManagerForLoot() &&
                   _manager.TryGetWorldLootSourcePosition(_latest.stableId, out worldPosition);
        }

        private bool EnsureManagerForLoot()
        {
            if (_manager != null)
                return true;

            _manager = FindFirstObjectByType<PlayerEntityGameManager>();
            if (_manager != null)
                return true;

            _feedbackOverride = "Player networking is not ready. Try again after world entry completes.";
            if (feedbackLabel != null)
                feedbackLabel.text = _feedbackOverride;
            Debug.LogError("[Loot] PlayerEntityGameManager is unavailable; Take request was not sent.", this);
            PresentFeedback($"LOOT: {_feedbackOverride}");
            return false;
        }

        private void PresentFeedback(string text)
        {
            if (_root != null)
                _root.PresentInteractionFeedback(text);
            else
                ClientGameplayUIRoot.Instance?.PresentLocalSystemMessage(text);
        }

        private void SetBusyPresentation(string text)
        {
            if (feedbackLabel != null)
                feedbackLabel.text = text;
            if (takeAllButton != null)
                takeAllButton.interactable = false;
        }
    }
}
