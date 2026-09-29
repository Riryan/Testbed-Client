using Game.Shared.Population;
using Player.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Targeting
{
    /// <summary>
    /// Lightweight client-only overhead identity/target presentation for PlayerEntity.
    /// It follows the interpolated presentation root, never the authoritative network root,
    /// and contains no gameplay authority or network sends.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerEntityNameplatePresenter : MonoBehaviour
    {
        private const float DefaultHeight = 2.15f;
        private const float MaximumVisibleDistance = 45f;

        private PlayerEntityClient _client;
        private Canvas _canvas;
        private Text _nameText;
        private Text _targetText;
        private Text _lootText;
        private Camera _camera;
        private Transform _worldRoot;
        private Transform _lastPresentation;
        private string _lastName;
        private bool _selected;
        private bool _lootAvailabilityKnown;
        private bool _hasLoot;
        private float _anchorHeight = DefaultHeight;

        internal void Bind(PlayerEntityClient client)
        {
            _client = client;
            EnsureCanvas();
            RefreshSelection(PlayerEntityTargetSelection.SelectedClient);
        }

        private void OnEnable()
        {
            PlayerEntityTargetSelection.SelectedChanged += RefreshSelection;
        }

        private void OnDisable()
        {
            PlayerEntityTargetSelection.SelectedChanged -= RefreshSelection;
        }

        private void OnDestroy()
        {
            PlayerEntityTargetSelection.SelectedChanged -= RefreshSelection;
        }

        private void LateUpdate()
        {
            if (_client == null)
            {
                Destroy(this);
                return;
            }

            RefreshPopulationInteractionState();

            Transform presentation = _client.PresentationTransform;
            if (presentation == null)
            {
                SetVisible(false);
                return;
            }

            // The local player does not need its own overhead label. Remote players share
            // this exact presenter and become visible automatically as they enter AOI.
            if (_client.IsOwnerClient)
            {
                SetVisible(false);
                return;
            }

            if (_lastPresentation != presentation)
            {
                _lastPresentation = presentation;
                _anchorHeight = ResolvePresentationHeight(presentation);
            }

            string displayName = _client.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                var network = _client.NetworkBridge;
                displayName = network != null && network.ObjectId != 0
                    ? $"Player {network.ObjectId}"
                    : "Player";
            }
            if (!string.Equals(_lastName, displayName, System.StringComparison.Ordinal))
            {
                _lastName = displayName;
                if (_nameText != null)
                    _nameText.text = displayName;
            }

            if (_camera == null || !_camera.isActiveAndEnabled)
                _camera = Camera.main;
            if (_camera == null)
            {
                SetVisible(false);
                return;
            }

            Vector3 anchor = presentation.position + Vector3.up * _anchorHeight;
            float distanceSq = (_camera.transform.position - anchor).sqrMagnitude;
            if (distanceSq > MaximumVisibleDistance * MaximumVisibleDistance)
            {
                SetVisible(false);
                return;
            }

            EnsureCanvas();
            SetVisible(true);
            _worldRoot.position = anchor;

            // Unity world-space UI is front-facing opposite the transform forward vector.
            // Point the Canvas forward away from the observing camera so the UI front faces
            // the camera. Using towardCamera here shows the back of the Canvas and mirrors
            // all glyphs for the observer.
            Vector3 awayFromCamera = _worldRoot.position - _camera.transform.position;
            if (awayFromCamera.sqrMagnitude > 0.0001f)
                _worldRoot.rotation = Quaternion.LookRotation(awayFromCamera.normalized, Vector3.up);
        }

        private void RefreshSelection(PlayerEntityClient selected)
        {
            _selected = _client != null && ReferenceEquals(_client, selected);
            if (_targetText != null)
                _targetText.gameObject.SetActive(_selected);
        }

        public void SetLootAvailability(bool known, bool hasLoot)
        {
            _lootAvailabilityKnown = known;
            _hasLoot = known && hasLoot;
            if (_lootText != null)
                _lootText.gameObject.SetActive(_client != null && _client.IsPopulationPresentation && _hasLoot);
        }

        private void RefreshPopulationInteractionState()
        {
            if (_client == null || !_client.IsPopulationPresentation ||
                _client.NetworkBridge == null || !_client.NetworkBridge.IsSpawned)
            {
                if (_lootAvailabilityKnown || _hasLoot)
                    SetLootAvailability(false, false);
                return;
            }

            var network = _client.NetworkBridge;
            bool dead = _client.IsDeadPresentation;
            bool hasLoot = dead &&
                (network.Appearance.populationInteractionFlags & PopulationPublicInteractionFlags.DeathLootAvailable) != 0;

            if (!_lootAvailabilityKnown || _hasLoot != hasLoot)
                SetLootAvailability(true, hasLoot);
        }

        private void EnsureCanvas()
        {
            if (_canvas != null)
                return;

            GameObject root = new GameObject("PlayerNameplate", typeof(RectTransform), typeof(Canvas));
            root.transform.SetParent(transform, false);
            _worldRoot = root.transform;
            _worldRoot.localPosition = Vector3.zero;
            _worldRoot.localRotation = Quaternion.identity;
            _worldRoot.localScale = Vector3.one * 0.005f;

            _canvas = root.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 40;

            RectTransform canvasRect = (RectTransform)root.transform;
            canvasRect.sizeDelta = new Vector2(240f, 56f);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            _nameText = CreateText("Name", canvasRect, font, 24, new Vector2(0f, 12f), new Vector2(240f, 30f));
            _targetText = CreateText("Target", canvasRect, font, 16, new Vector2(0f, -13f), new Vector2(240f, 20f));
            _targetText.text = "TARGET";
            _targetText.fontStyle = FontStyle.Bold;
            _targetText.gameObject.SetActive(_selected);

            _lootText = CreateText("Loot", canvasRect, font, 16, new Vector2(0f, -32f), new Vector2(240f, 20f));
            _lootText.text = "LOOT";
            _lootText.fontStyle = FontStyle.Bold;
            _lootText.gameObject.SetActive(_client != null && _client.IsPopulationPresentation && _lootAvailabilityKnown && _hasLoot);
        }

        private static Text CreateText(
            string name,
            Transform parent,
            Font font,
            int fontSize,
            Vector2 anchoredPosition,
            Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.color = Color.white;

            Outline outline = go.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            return text;
        }

        private static float ResolvePresentationHeight(Transform presentation)
        {
            Renderer[] renderers = presentation.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return DefaultHeight;

            float baseY = presentation.position.y;
            float top = baseY + DefaultHeight;
            bool found = false;
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                top = found ? Mathf.Max(top, renderer.bounds.max.y) : renderer.bounds.max.y;
                found = true;
            }

            return found ? Mathf.Clamp(top - baseY + 0.18f, 1.4f, 3.2f) : DefaultHeight;
        }

        private void SetVisible(bool visible)
        {
            if (_canvas != null && _canvas.gameObject.activeSelf != visible)
                _canvas.gameObject.SetActive(visible);
        }
    }

    /// <summary>
    /// Installs nameplate presenters on the event-driven PlayerEntity registry. No scene
    /// scans are required for AOI entry/re-entry.
    /// </summary>
    internal static class PlayerEntityNameplateBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Initialize()
        {
            PlayerEntityClient.ActiveClientRegistered -= OnRegistered;
            PlayerEntityClient.ActiveClientRegistered += OnRegistered;

            var active = PlayerEntityClient.ActiveClients;
            for (int i = 0; i < active.Count; ++i)
                OnRegistered(active[i]);
        }

        private static void OnRegistered(PlayerEntityClient client)
        {
            if (client == null)
                return;
            PlayerEntityNameplatePresenter presenter = client.GetComponent<PlayerEntityNameplatePresenter>();
            if (presenter == null)
                presenter = client.gameObject.AddComponent<PlayerEntityNameplatePresenter>();
            presenter.Bind(client);
        }
    }
}
