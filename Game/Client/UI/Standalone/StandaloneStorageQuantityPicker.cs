using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Local-only quantity chooser used by Shift+Drop in Storage.
    /// The selected value is always clamped to 1..current local authoritative stack max.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneStorageQuantityPicker : MonoBehaviour
    {
        [SerializeField] private GameObject pickerRoot;
        [SerializeField] private Text titleText;
        [SerializeField] private InputField quantityInput;
        [SerializeField] private Button maxButton;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;

        private Action<int> _confirmed;
        private int _maximum = 1;
        private bool _wired;

        public bool IsOpen => pickerRoot != null && pickerRoot.activeSelf;

        private void Awake() => EnsureWired();
        private void OnEnable() => EnsureWired();

        private void Update()
        {
            if (!IsOpen)
                return;

            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
                Confirm();
        }

        public void Open(string title, int maximum, Action<int> confirmed)
        {
            EnsureWired();
            _maximum = Mathf.Max(1, maximum);
            _confirmed = confirmed;

            if (titleText != null)
                titleText.text = string.IsNullOrWhiteSpace(title) ? "Quantity" : title;

            if (pickerRoot != null)
                pickerRoot.SetActive(true);

            SetQuantity(_maximum);

            if (quantityInput != null)
            {
                EventSystem.current?.SetSelectedGameObject(quantityInput.gameObject);
                quantityInput.Select();
                quantityInput.ActivateInputField();
            }
        }

        public void Close()
        {
            _confirmed = null;
            if (pickerRoot != null)
                pickerRoot.SetActive(false);
        }

        private void EnsureWired()
        {
            if (_wired)
                return;
            _wired = true;

            if (quantityInput != null)
            {
                quantityInput.contentType = InputField.ContentType.IntegerNumber;
                quantityInput.onValueChanged.AddListener(OnQuantityChanged);
                quantityInput.onEndEdit.AddListener(OnQuantityEndEdit);
            }

            maxButton?.onClick.AddListener(SetMaximum);
            confirmButton?.onClick.AddListener(Confirm);
            cancelButton?.onClick.AddListener(Close);
        }

        private void OnDestroy()
        {
            if (quantityInput != null)
            {
                quantityInput.onValueChanged.RemoveListener(OnQuantityChanged);
                quantityInput.onEndEdit.RemoveListener(OnQuantityEndEdit);
            }

            maxButton?.onClick.RemoveListener(SetMaximum);
            confirmButton?.onClick.RemoveListener(Confirm);
            cancelButton?.onClick.RemoveListener(Close);
        }

        private void OnQuantityChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (!int.TryParse(value, out int parsed))
            {
                SetQuantity(1);
                return;
            }

            int clamped = Mathf.Clamp(parsed, 1, _maximum);
            if (clamped != parsed)
                SetQuantity(clamped);
        }

        private void OnQuantityEndEdit(string value)
        {
            if (!int.TryParse(value, out int parsed))
                parsed = 1;
            SetQuantity(Mathf.Clamp(parsed, 1, _maximum));
        }

        private void SetMaximum() => SetQuantity(_maximum);

        private void SetQuantity(int quantity)
        {
            if (quantityInput != null)
                quantityInput.SetTextWithoutNotify(
                    Mathf.Clamp(quantity, 1, _maximum).ToString());
        }

        private void Confirm()
        {
            int quantity = 1;
            if (quantityInput != null &&
                int.TryParse(quantityInput.text, out int parsed))
            {
                quantity = parsed;
            }

            quantity = Mathf.Clamp(quantity, 1, _maximum);
            Action<int> callback = _confirmed;
            Close();
            callback?.Invoke(quantity);
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject root,
            Text title,
            InputField input,
            Button max,
            Button confirm,
            Button cancel)
        {
            pickerRoot = root;
            titleText = title;
            quantityInput = input;
            maxButton = max;
            confirmButton = confirm;
            cancelButton = cancel;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
