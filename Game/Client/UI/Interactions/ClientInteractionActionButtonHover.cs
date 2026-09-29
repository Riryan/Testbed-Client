using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Client.UI.Interactions
{
    public sealed class ClientInteractionActionButtonHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private Action<int, bool> _hoverChanged;
        private int _slotIndex = -1;

        public void Bind(Action<int, bool> hoverChanged, int slotIndex)
        {
            _hoverChanged = hoverChanged;
            _slotIndex = slotIndex;
        }

        public void OnPointerEnter(PointerEventData eventData) => _hoverChanged?.Invoke(_slotIndex, true);
        public void OnPointerExit(PointerEventData eventData) => _hoverChanged?.Invoke(_slotIndex, false);
    }
}
