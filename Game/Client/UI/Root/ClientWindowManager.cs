using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Client.UI.Root
{
    /// <summary>
    /// Opens/closes authored ClientUIRoot panels by stable semantic ID. The manager
    /// never constructs layout and never relies on hierarchy paths.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientWindowManager : MonoBehaviour
    {
        private readonly Dictionary<ClientUIPanelId, ClientUIPanelMarker> _panels =
            new Dictionary<ClientUIPanelId, ClientUIPanelMarker>();
        private readonly List<ClientUIPanelId> _openOrder = new List<ClientUIPanelId>();
        private ClientUIRoot _root;

        public event Action<ClientUIPanelId, bool> PanelVisibilityChanged;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            _root = GetComponent<ClientUIRoot>();
            RebuildRegistry();
#endif
        }

        public void RebuildRegistry()
        {
            _panels.Clear();
            ClientUIPanelMarker[] markers = GetComponentsInChildren<ClientUIPanelMarker>(true);
            foreach (ClientUIPanelMarker marker in markers)
            {
                if (marker == null || marker.PanelId == ClientUIPanelId.None)
                    continue;

                if (_panels.ContainsKey(marker.PanelId))
                {
                    Debug.LogError($"[ClientUI] Duplicate panel id {marker.PanelId} in master ClientUIRoot.", marker);
                    continue;
                }
                _panels.Add(marker.PanelId, marker);
                EnsureDraggableWindow(marker);
            }
        }

        private static void EnsureDraggableWindow(ClientUIPanelMarker marker)
        {
#if !UNITY_SERVER
            if (marker == null || !marker.ManagedWindow || marker.Modal || !(marker.transform is RectTransform))
                return;

            ClientDraggableWindow draggable = marker.GetComponent<ClientDraggableWindow>();
            if (draggable == null)
                draggable = marker.gameObject.AddComponent<ClientDraggableWindow>();
            draggable.Configure(marker.PanelId);
#endif
        }

        public bool TryGet(ClientUIPanelId id, out GameObject panel)
        {
            if (_panels.Count == 0)
                RebuildRegistry();
            if (_panels.TryGetValue(id, out ClientUIPanelMarker marker) && marker != null)
            {
                panel = marker.gameObject;
                return true;
            }
            panel = null;
            return false;
        }

        public bool IsOpen(ClientUIPanelId id) =>
            TryGet(id, out GameObject panel) && panel.activeSelf;

        public bool Open(ClientUIPanelId id)
        {
            if (!_panels.TryGetValue(id, out ClientUIPanelMarker marker) || marker == null)
            {
                RebuildRegistry();
                if (!_panels.TryGetValue(id, out marker) || marker == null)
                    return false;
            }

            if (marker.GameplayOnly && (_root == null || !_root.GameplaySessionActive))
                return false;

            if (marker.Modal)
                CloseOtherModals(id);

            SetPanelVisible(id, marker.gameObject, true);
            marker.transform.SetAsLastSibling();
            _openOrder.Remove(id);
            _openOrder.Add(id);
            return true;
        }

        public bool Close(ClientUIPanelId id)
        {
            if (!TryGet(id, out GameObject panel))
                return false;
            SetPanelVisible(id, panel, false);
            _openOrder.Remove(id);
            return true;
        }

        public bool Toggle(ClientUIPanelId id) => IsOpen(id) ? Close(id) : Open(id);

        public void CloseAllGameplayWindows()
        {
            if (_panels.Count == 0)
                RebuildRegistry();
            foreach (KeyValuePair<ClientUIPanelId, ClientUIPanelMarker> pair in _panels)
            {
                ClientUIPanelMarker marker = pair.Value;
                if (marker != null && marker.ManagedWindow && marker.GameplayOnly && marker.gameObject.activeSelf)
                    SetPanelVisible(pair.Key, marker.gameObject, false);
            }
            _openOrder.Clear();
        }

        public bool CloseTopmostEscapable()
        {
            for (int i = _openOrder.Count - 1; i >= 0; --i)
            {
                ClientUIPanelId id = _openOrder[i];
                if (!_panels.TryGetValue(id, out ClientUIPanelMarker marker) || marker == null ||
                    !marker.gameObject.activeSelf || !marker.CloseOnEscape)
                    continue;
                return Close(id);
            }
            return false;
        }

        private void CloseOtherModals(ClientUIPanelId except)
        {
            foreach (KeyValuePair<ClientUIPanelId, ClientUIPanelMarker> pair in _panels)
            {
                ClientUIPanelMarker marker = pair.Value;
                if (pair.Key != except && marker != null && marker.Modal && marker.gameObject.activeSelf)
                {
                    SetPanelVisible(pair.Key, marker.gameObject, false);
                    _openOrder.Remove(pair.Key);
                }
            }
        }

        private void SetPanelVisible(ClientUIPanelId id, GameObject panel, bool visible)
        {
            if (panel == null || panel.activeSelf == visible)
                return;
            panel.SetActive(visible);
            PanelVisibilityChanged?.Invoke(id, visible);
        }
    }
}
