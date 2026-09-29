using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Game.Client.UI.SocialEconomy
{
    [DisallowMultipleComponent]
    public sealed class ClientSocialEconomyRowView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private Text primaryText;
        [SerializeField] private Text secondaryText;
        [SerializeField] private Button primaryButton;
        [SerializeField] private Text primaryButtonLabel;
        [SerializeField] private Button secondaryButton;
        [SerializeField] private Text secondaryButtonLabel;

        private Action _dragStarted;
        private Action _dragEnded;
        private Action _droppedOn;
        private bool _canDrag;
        private CanvasGroup _canvasGroup;

        public void Configure(
            string primary,
            string secondary,
            string primaryAction,
            Action onPrimary,
            string secondaryAction = null,
            Action onSecondary = null,
            Action dragStarted = null,
            Action dragEnded = null,
            Action droppedOn = null)
        {
            if (primaryText != null)
                primaryText.text = primary ?? string.Empty;
            if (secondaryText != null)
                secondaryText.text = secondary ?? string.Empty;

            ConfigureButton(primaryButton, primaryButtonLabel, primaryAction, onPrimary);
            ConfigureButton(secondaryButton, secondaryButtonLabel, secondaryAction, onSecondary);
            _dragStarted = dragStarted;
            _dragEnded = dragEnded;
            _droppedOn = droppedOn;
            _canDrag = dragStarted != null;
            gameObject.SetActive(true);
        }

        public void ResetView()
        {
            if (primaryText != null) primaryText.text = string.Empty;
            if (secondaryText != null) secondaryText.text = string.Empty;
            ConfigureButton(primaryButton, primaryButtonLabel, null, null);
            ConfigureButton(secondaryButton, secondaryButtonLabel, null, null);
            _dragStarted = null;
            _dragEnded = null;
            _droppedOn = null;
            _canDrag = false;
            RestoreDragVisual();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_canDrag || eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            EnsureCanvasGroup();
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.alpha = 0.72f;
            _dragStarted?.Invoke();
        }

        // Required by Unity EventSystem so begin/end drag events are dispatched.
        public void OnDrag(PointerEventData eventData) { }

        public void OnEndDrag(PointerEventData eventData)
        {
            RestoreDragVisual();
            _dragEnded?.Invoke();
        }

        public void OnDrop(PointerEventData eventData) => _droppedOn?.Invoke();

        private void EnsureCanvasGroup()
        {
            if (_canvasGroup == null)
                _canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        }

        private void RestoreDragVisual()
        {
            if (_canvasGroup == null)
                return;
            _canvasGroup.blocksRaycasts = true;
            _canvasGroup.alpha = 1f;
        }

        private static void ConfigureButton(Button button, Text label, string caption, Action callback)
        {
            if (button == null)
                return;

            button.onClick.RemoveAllListeners();
            bool visible = !string.IsNullOrWhiteSpace(caption) && callback != null;
            button.gameObject.SetActive(visible);
            button.interactable = visible;
            if (label != null)
                label.text = caption ?? string.Empty;
            if (visible)
                button.onClick.AddListener(() => callback());
        }

#if UNITY_EDITOR
        public void CaptureAuthoredReferencesForEditor()
        {
            primaryText = transform.Find("PrimaryText")?.GetComponent<Text>();
            secondaryText = transform.Find("SecondaryText")?.GetComponent<Text>();
            primaryButton = transform.Find("PrimaryButton")?.GetComponent<Button>();
            primaryButtonLabel = primaryButton != null ? primaryButton.GetComponentInChildren<Text>(true) : null;
            secondaryButton = transform.Find("SecondaryButton")?.GetComponent<Button>();
            secondaryButtonLabel = secondaryButton != null ? secondaryButton.GetComponentInChildren<Text>(true) : null;
        }
#endif
    }
}
