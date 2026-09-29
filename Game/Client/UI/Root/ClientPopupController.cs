using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Root
{
    /// <summary>
    /// Reusable authored popup surface. Gameplay systems may request presentation here,
    /// but any authoritative mutation still belongs to the server-owned feature service.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientPopupController : MonoBehaviour
    {
        [SerializeField] private GameObject modalBlocker;
        [SerializeField] private GameObject popupPanel;
        [SerializeField] private Text titleText;
        [SerializeField] private Text messageText;
        [SerializeField] private Image iconImage;
        [SerializeField] private InputField optionalInput;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button primaryButton;
        [SerializeField] private Text primaryLabel;
        [SerializeField] private Button secondaryButton;
        [SerializeField] private Text secondaryLabel;
        [SerializeField] private Button tertiaryButton;
        [SerializeField] private Text tertiaryLabel;

        private Action _primary;
        private Action _secondary;
        private Action _tertiary;

        public bool IsVisible => popupPanel != null && popupPanel.activeSelf;
        public string InputText => optionalInput != null ? optionalInput.text : string.Empty;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            closeButton?.onClick.AddListener(Hide);
            primaryButton?.onClick.AddListener(OnPrimary);
            secondaryButton?.onClick.AddListener(OnSecondary);
            tertiaryButton?.onClick.AddListener(OnTertiary);
            Hide();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            closeButton?.onClick.RemoveListener(Hide);
            primaryButton?.onClick.RemoveListener(OnPrimary);
            secondaryButton?.onClick.RemoveListener(OnSecondary);
            tertiaryButton?.onClick.RemoveListener(OnTertiary);
#endif
        }

        public void Show(
            string title,
            string message,
            string primary = "OK",
            Action onPrimary = null,
            string secondary = null,
            Action onSecondary = null,
            string tertiary = null,
            Action onTertiary = null,
            bool showInput = false,
            string inputValue = "")
        {
            if (popupPanel == null)
                return;

            if (titleText != null) titleText.text = title ?? string.Empty;
            if (messageText != null) messageText.text = message ?? string.Empty;
            ConfigureButton(primaryButton, primaryLabel, primary);
            ConfigureButton(secondaryButton, secondaryLabel, secondary);
            ConfigureButton(tertiaryButton, tertiaryLabel, tertiary);
            if (optionalInput != null)
            {
                optionalInput.gameObject.SetActive(showInput);
                optionalInput.text = inputValue ?? string.Empty;
            }

            _primary = onPrimary;
            _secondary = onSecondary;
            _tertiary = onTertiary;
            if (modalBlocker != null)
            {
                modalBlocker.SetActive(true);
                modalBlocker.transform.SetAsLastSibling();
            }
            popupPanel.SetActive(true);
            popupPanel.transform.SetAsLastSibling();

            if (showInput && optionalInput != null)
            {
                Canvas.ForceUpdateCanvases();
                optionalInput.Select();
                optionalInput.ActivateInputField();
                if (EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(optionalInput.gameObject);
            }
        }

        public void ShowInformation(string title, string message) => Show(title, message);
        public void ShowError(string message) => Show("ERROR", message);
        public void ShowWarning(string message) => Show("WARNING", message);
        public void ShowConfirmation(string title, string message, Action yes, Action no = null) =>
            Show(title, message, "YES", yes, "NO", no);

        public void Hide()
        {
            popupPanel?.SetActive(false);
            modalBlocker?.SetActive(false);
            _primary = null;
            _secondary = null;
            _tertiary = null;
        }

        private static void ConfigureButton(Button button, Text label, string text)
        {
            if (button == null)
                return;
            bool visible = !string.IsNullOrWhiteSpace(text);
            button.gameObject.SetActive(visible);
            if (visible && label != null)
                label.text = text;
        }

        private void OnPrimary()
        {
            Action callback = _primary;
            Hide();
            callback?.Invoke();
        }

        private void OnSecondary()
        {
            Action callback = _secondary;
            Hide();
            callback?.Invoke();
        }

        private void OnTertiary()
        {
            Action callback = _tertiary;
            Hide();
            callback?.Invoke();
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject blocker,
            GameObject panel,
            Text title,
            Text message,
            Image icon,
            InputField input,
            Button close,
            Button primary,
            Text primaryText,
            Button secondary,
            Text secondaryText,
            Button tertiary,
            Text tertiaryText)
        {
            modalBlocker = blocker;
            popupPanel = panel;
            titleText = title;
            messageText = message;
            iconImage = icon;
            optionalInput = input;
            closeButton = close;
            primaryButton = primary;
            primaryLabel = primaryText;
            secondaryButton = secondary;
            secondaryLabel = secondaryText;
            tertiaryButton = tertiary;
            tertiaryLabel = tertiaryText;
        }
#endif
    }
}
