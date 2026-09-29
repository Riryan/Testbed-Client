using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Game.Client.UI.SocialEconomy
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasGroup))]
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

        private void Awake()
        {
            EnsureCanvasGroup();
            RestoreDragVisual();
        }

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
            // Row templates are embedded inside the authored Standalone UI prefab and are
            // cloned at runtime. Establish the drag dependency here as well as in Awake so
            // inactive templates/clones are safe before they are activated by Configure().
            EnsureCanvasGroup();

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
            RestoreDragVisual();
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

            if (!EnsureCanvasGroup())
                return;

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

        private bool EnsureCanvasGroup()
        {
            // Do not use null-coalescing with UnityEngine.Object here. Unity's destroyed-object
            // "fake null" semantics can leave a stale native component reference looking null
            // through the overloaded equality operator while still being non-null to CLR ?? logic.
            // Resolve explicitly and verify the Unity object before any property access.
            if (_canvasGroup != null)
                return true;

            _canvasGroup = GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();

            return _canvasGroup != null;
        }

        private void RestoreDragVisual()
        {
            if (!EnsureCanvasGroup())
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
