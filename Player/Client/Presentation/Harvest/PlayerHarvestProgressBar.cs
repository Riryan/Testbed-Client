using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Local-only Harvest timer bar driven by the existing owner HarvestEvent.
    /// No progress traffic is generated: Started supplies the authoritative duration and
    /// Succeeded/Failed/Cancelled remain the authoritative terminal result.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerHarvestProgressBar : MonoBehaviour
    {
        [SerializeField] private Slider progressSlider;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField, Range(0.90f, 0.999f)] private float waitForServerAt = 0.99f;
        [SerializeField, Min(0f)] private float successHoldSeconds = 0.15f;

        private bool _active;
        private long _stableId;
        private float _startedAt;
        private float _durationSeconds;
        private float _hideAt = -1f;

        private void Awake()
        {
            if (progressSlider == null)
                progressSlider = GetComponent<Slider>();

            if (canvasGroup == null)
                canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();

            if (progressSlider == null)
            {
                Debug.LogError("[Harvest] PlayerHarvestProgressBar requires a Slider reference.", this);
                enabled = false;
                return;
            }

            progressSlider.minValue = 0f;
            progressSlider.maxValue = 1f;
            progressSlider.wholeNumbers = false;
            SetProgress(0f);
            SetVisible(false);
        }

        private void OnEnable()
        {
            PlayerHarvestPresentation.HarvestEventPresented += OnHarvestEvent;
        }

        private void OnDisable()
        {
            PlayerHarvestPresentation.HarvestEventPresented -= OnHarvestEvent;
            ResetState();
        }

        private void Update()
        {
            if (_active)
            {
                float elapsed = Mathf.Max(0f, Time.unscaledTime - _startedAt);
                float normalized = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, _durationSeconds));
                SetProgress(Mathf.Min(normalized, Mathf.Clamp(waitForServerAt, 0.90f, 0.999f)));
                return;
            }

            if (_hideAt >= 0f && Time.unscaledTime >= _hideAt)
                ResetState();
        }

        private void OnHarvestEvent(PlayerHarvestEventMessage message)
        {
            switch (message.Phase)
            {
                case PlayerHarvestEventPhase.Started:
                    Begin(message);
                    break;

                case PlayerHarvestEventPhase.Succeeded:
                    if (!MatchesActiveHarvest(message.stableId))
                        return;
                    _active = false;
                    SetProgress(1f);
                    _hideAt = Time.unscaledTime + successHoldSeconds;
                    break;

                case PlayerHarvestEventPhase.Failed:
                case PlayerHarvestEventPhase.Cancelled:
                    if (!MatchesActiveHarvest(message.stableId))
                        return;
                    ResetState();
                    break;
            }
        }

        private void Begin(PlayerHarvestEventMessage message)
        {
            _stableId = message.stableId;
            _durationSeconds = Mathf.Max(0.01f, message.DurationSeconds);
            _startedAt = Time.unscaledTime;
            _hideAt = -1f;
            _active = true;
            SetProgress(0f);
            SetVisible(true);
        }

        private bool MatchesActiveHarvest(long stableId)
        {
            return (_active || _hideAt >= 0f) && stableId == _stableId;
        }

        private void ResetState()
        {
            _active = false;
            _stableId = 0L;
            _startedAt = 0f;
            _durationSeconds = 0f;
            _hideAt = -1f;
            SetProgress(0f);
            SetVisible(false);
        }

        private void SetProgress(float value)
        {
            if (progressSlider != null)
                progressSlider.SetValueWithoutNotify(Mathf.Clamp01(value));
        }

        private void SetVisible(bool visible)
        {
            if (canvasGroup == null)
                return;
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }
    }
}
