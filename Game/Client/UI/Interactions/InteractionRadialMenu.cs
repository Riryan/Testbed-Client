using System;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Interactions
{
    /// <summary>
    /// Client-only radial presentation for server-discovered interactions. It does not
    /// decide which actions are legal and never mutates gameplay state. The hierarchy is
    /// generated as a safe testbed fallback so it can later be replaced by authored UI
    /// without changing the server/discovery contract.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionRadialMenu : MonoBehaviour
    {
        private const float Radius = 142f;
        private const int MaxVisibleActions = 12;

        private GameObject _root;
        private RectTransform _buttonRoot;
        private Text _targetLabel;
        private Text _detailLabel;
        private Font _font;
        private Action<InteractionActionEntryWire> _selected;
        private bool _inputCaptured;

        public bool IsOpen => _root != null && _root.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            EnsureHierarchy();
            Hide();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (IsOpen && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                Hide();
#endif
        }

        private void OnDisable() => ReleaseInput();
        private void OnDestroy() => ReleaseInput();

        public void Show(InteractionMenuResponseMessage menu, Action<InteractionActionEntryWire> selected)
        {
#if !UNITY_SERVER
            EnsureHierarchy();
            _selected = selected;
            _targetLabel.text = string.IsNullOrWhiteSpace(menu.targetLabel)
                ? menu.target.Kind.ToString()
                : menu.targetLabel;
            _detailLabel.text = string.IsNullOrWhiteSpace(menu.detail)
                ? "Choose an interaction"
                : menu.detail;

            ClearButtons();
            InteractionActionEntryWire[] actions = menu.actions ?? Array.Empty<InteractionActionEntryWire>();
            int count = Mathf.Min(actions.Length, MaxVisibleActions);
            for (int i = 0; i < count; ++i)
                AddAction(actions[i], i, count);

            if (count == 0)
                _detailLabel.text = "No authoritative actions are available for this target yet.";

            _root.SetActive(true);
            AcquireInput();
#endif
        }

        public void Hide()
        {
            if (_root != null)
                _root.SetActive(false);
            _selected = null;
            ReleaseInput();
        }

        private void AddAction(InteractionActionEntryWire entry, int index, int count)
        {
            float angle = 90f - (360f * index / Mathf.Max(1, count));
            float radians = angle * Mathf.Deg2Rad;
            Vector2 position = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * Radius;

            GameObject buttonObject = NewImage($"Action_{entry.actionId}", _buttonRoot, new Color(0.10f, 0.12f, 0.16f, 0.96f));
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(124f, 62f);
            rect.anchoredPosition = position;

            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonObject.GetComponent<Image>();
            button.interactable = entry.IsAvailable;

            string labelText = string.IsNullOrWhiteSpace(entry.label) ? entry.ActionId.ToString() : entry.label;
            if (entry.ConsentMode != Game.Shared.Interactions.InteractionConsentMode.None)
                labelText += "\n[Consent]";
            else if (!entry.IsAvailable && !string.IsNullOrWhiteSpace(entry.disabledReason))
                labelText += "\n" + Trim(entry.disabledReason, 22);

            Text label = NewText("Label", buttonObject.transform, labelText, 13, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);

            InteractionActionEntryWire captured = entry;
            button.onClick.AddListener(() =>
            {
                Action<InteractionActionEntryWire> callback = _selected;
                Hide();
                callback?.Invoke(captured);
            });
        }

        private void EnsureHierarchy()
        {
            if (_root != null)
                return;

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null)
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            Transform existing = transform.Find("InteractionRadialMenu");
            if (existing != null)
            {
                _root = existing.gameObject;
                _buttonRoot = existing.Find("ButtonRoot") as RectTransform;
                _targetLabel = existing.Find("Center/Target")?.GetComponent<Text>();
                _detailLabel = existing.Find("Center/Detail")?.GetComponent<Text>();
                if (_buttonRoot != null && _targetLabel != null && _detailLabel != null)
                    return;
                Destroy(existing.gameObject);
            }

            _root = NewUi("InteractionRadialMenu", transform);
            Stretch(_root.GetComponent<RectTransform>());

            GameObject blocker = NewImage("Blocker", _root.transform, new Color(0f, 0f, 0f, 0.32f));
            Stretch(blocker.GetComponent<RectTransform>());

            GameObject buttonRoot = NewUi("ButtonRoot", _root.transform);
            _buttonRoot = buttonRoot.GetComponent<RectTransform>();
            _buttonRoot.anchorMin = _buttonRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _buttonRoot.sizeDelta = new Vector2(420f, 420f);
            _buttonRoot.anchoredPosition = Vector2.zero;

            GameObject center = NewImage("Center", _root.transform, new Color(0.035f, 0.045f, 0.06f, 0.97f));
            RectTransform centerRect = center.GetComponent<RectTransform>();
            centerRect.anchorMin = centerRect.anchorMax = new Vector2(0.5f, 0.5f);
            centerRect.sizeDelta = new Vector2(190f, 105f);
            centerRect.anchoredPosition = Vector2.zero;

            _targetLabel = NewText("Target", center.transform, "TARGET", 17, TextAnchor.MiddleCenter);
            RectTransform targetRect = _targetLabel.rectTransform;
            targetRect.anchorMin = new Vector2(0f, 0.5f);
            targetRect.anchorMax = new Vector2(1f, 1f);
            targetRect.offsetMin = new Vector2(8f, 0f);
            targetRect.offsetMax = new Vector2(-8f, -4f);

            _detailLabel = NewText("Detail", center.transform, "Choose an interaction", 12, TextAnchor.UpperCenter);
            RectTransform detailRect = _detailLabel.rectTransform;
            detailRect.anchorMin = new Vector2(0f, 0f);
            detailRect.anchorMax = new Vector2(1f, 0.5f);
            detailRect.offsetMin = new Vector2(8f, 6f);
            detailRect.offsetMax = new Vector2(-8f, 0f);
        }

        private void ClearButtons()
        {
            if (_buttonRoot == null)
                return;
            for (int i = _buttonRoot.childCount - 1; i >= 0; --i)
                Destroy(_buttonRoot.GetChild(i).gameObject);
        }

        private void AcquireInput()
        {
            if (_inputCaptured)
                return;
            LocalClientInputGate.Acquire();
            _inputCaptured = true;
        }

        private void ReleaseInput()
        {
            if (!_inputCaptured)
                return;
            LocalClientInputGate.Release();
            _inputCaptured = false;
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;
            return value.Substring(0, Mathf.Max(1, max - 1)) + "…";
        }

        private GameObject NewUi(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private GameObject NewImage(string name, Transform parent, Color color)
        {
            GameObject go = NewUi(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        private Text NewText(string name, Transform parent, string value, int size, TextAnchor anchor)
        {
            GameObject go = NewUi(name, parent);
            Text text = go.AddComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
