using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Client.UI.Root
{
    /// <summary>Blocks local gameplay keys while a gameplay HUD text field owns focus.</summary>
    public sealed class ClientUiTextInputCapture : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        private bool _captured;

        public void OnSelect(BaseEventData eventData)
        {
            if (_captured)
                return;
            _captured = true;
            LocalClientInputGate.Acquire();
        }

        public void OnDeselect(BaseEventData eventData) => Release();

        private void OnDisable() => Release();
        private void OnDestroy() => Release();

        private void Release()
        {
            if (!_captured)
                return;
            _captured = false;
            LocalClientInputGate.Release();
        }
    }
}
