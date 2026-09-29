using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Root
{
    /// <summary>
    /// Generic close-button bridge for authored windows. Keeps the prefab editable
    /// without requiring a unique controller just to close every placeholder window.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public sealed class ClientUIPanelCloseButton : MonoBehaviour
    {
        private Button _button;
        private ClientUIPanelMarker _marker;
        private ClientWindowManager _windows;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            _button = GetComponent<Button>();
            _marker = GetComponentInParent<ClientUIPanelMarker>(true);
            _windows = GetComponentInParent<ClientWindowManager>();
            _button.onClick.AddListener(Close);
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            _button?.onClick.RemoveListener(Close);
#endif
        }

        private void Close()
        {
            if (_marker != null && _windows != null)
                _windows.Close(_marker.PanelId);
            else if (_marker != null)
                _marker.gameObject.SetActive(false);
        }
    }
}
