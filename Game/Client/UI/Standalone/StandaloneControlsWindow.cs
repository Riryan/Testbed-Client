using System;
using Player.Client;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Authored local Player Config controls surface. Bindings persist only on this machine
    /// through PlayerControlConfig/PlayerPrefs and are never sent over the network.
    /// </summary>
    public sealed class StandaloneControlsWindow : MonoBehaviour
    {
        [SerializeField] private GameObject windowRoot;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button resetDefaultsButton;
        [SerializeField] private Text statusText;
        [SerializeField] private StandaloneControlBindingRow[] rows = Array.Empty<StandaloneControlBindingRow>();

        private bool _awaitingBinding;
        private PlayerControlAction _awaitingAction;
        private int _ignoreInputUntilFrame;
        private Array _allKeyCodes;
        private bool _rebindCaptureHeld;

        public bool IsOpen => windowRoot != null && windowRoot.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            closeButton?.onClick.AddListener(Close);
            resetDefaultsButton?.onClick.AddListener(ResetDefaults);
            PlayerControlConfig.BindingChanged += OnBindingChanged;
            _allKeyCodes = Enum.GetValues(typeof(KeyCode));
            windowRoot?.SetActive(false);
            RefreshRows();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            closeButton?.onClick.RemoveListener(Close);
            resetDefaultsButton?.onClick.RemoveListener(ResetDefaults);
            PlayerControlConfig.BindingChanged -= OnBindingChanged;
            ReleaseRebindCapture();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (!_awaitingBinding || !IsOpen || Time.frameCount <= _ignoreInputUntilFrame || !UnityEngine.Input.anyKeyDown)
                return;

            foreach (object value in _allKeyCodes)
            {
                KeyCode key = (KeyCode)value;
                if (key == KeyCode.None || !UnityEngine.Input.GetKeyDown(key))
                    continue;

                if (key == KeyCode.Backspace || key == KeyCode.Delete)
                    PlayerControlConfig.SetBinding(_awaitingAction, KeyCode.None);
                else
                    PlayerControlConfig.SetBinding(_awaitingAction, key);

                _awaitingBinding = false;
                ReleaseRebindCapture();
                SetStatus(string.Empty);
                RefreshRows();
                EventSystem.current?.SetSelectedGameObject(null);
                break;
            }
#endif
        }

        public void Open()
        {
#if !UNITY_SERVER
            windowRoot?.SetActive(true);
            _awaitingBinding = false;
            SetStatus("Click a binding, then press a key. Backspace/Delete unbinds it.");
            RefreshRows();
#endif
        }

        public void Close()
        {
#if !UNITY_SERVER
            _awaitingBinding = false;
            ReleaseRebindCapture();
            windowRoot?.SetActive(false);
            EventSystem.current?.SetSelectedGameObject(null);
#endif
        }

        public void Toggle()
        {
#if !UNITY_SERVER
            if (IsOpen) Close(); else Open();
#endif
        }

        public void BeginRebind(PlayerControlAction action)
        {
#if !UNITY_SERVER
            _awaitingBinding = true;
            _awaitingAction = action;
            AcquireRebindCapture();
            _ignoreInputUntilFrame = Time.frameCount + 1;
            SetStatus($"Press a key for {PlayerControlConfig.DisplayName(action)}. Backspace/Delete = unbound.");
            RefreshRows();
#endif
        }

        private void ResetDefaults()
        {
#if !UNITY_SERVER
            _awaitingBinding = false;
            ReleaseRebindCapture();
            PlayerControlConfig.ResetAllDefaults();
            SetStatus("Default local controls restored.");
            RefreshRows();
#endif
        }


        private void AcquireRebindCapture()
        {
            if (_rebindCaptureHeld)
                return;
            _rebindCaptureHeld = true;
            LocalClientInputGate.Acquire();
        }

        private void ReleaseRebindCapture()
        {
            if (!_rebindCaptureHeld)
                return;
            _rebindCaptureHeld = false;
            LocalClientInputGate.Release();
        }

        private void OnBindingChanged(PlayerControlAction action, KeyCode key)
        {
            if (IsOpen)
                RefreshRows();
        }

        private void RefreshRows()
        {
            if (rows == null)
                return;
            for (int i = 0; i < rows.Length; ++i)
            {
                StandaloneControlBindingRow row = rows[i];
                if (row != null)
                    row.Refresh(_awaitingBinding && row.Action == _awaitingAction);
            }
        }

        private void SetStatus(string value)
        {
            if (statusText != null)
                statusText.text = value ?? string.Empty;
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            GameObject authoredRoot,
            Button authoredClose,
            Button authoredReset,
            Text authoredStatus,
            StandaloneControlBindingRow[] authoredRows)
        {
            windowRoot = authoredRoot;
            closeButton = authoredClose;
            resetDefaultsButton = authoredReset;
            statusText = authoredStatus;
            rows = authoredRows ?? Array.Empty<StandaloneControlBindingRow>();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
