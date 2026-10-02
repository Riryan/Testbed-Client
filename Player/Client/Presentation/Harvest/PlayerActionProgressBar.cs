using UnityEngine;
using UnityEngine.UI;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Reusable client-local timed action progress presentation.
    ///
    /// Call Begin once from an already-authoritative start event/state, then Complete or Cancel
    /// from the authoritative terminal result. The bar interpolates locally between those points;
    /// it never requests progress updates and never owns gameplay authority.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerActionProgressBar : MonoBehaviour
    {
        [SerializeField] private Slider progressSlider;
        [SerializeField] private CanvasGroup canvasGroup;

        [Header("Presentation")]
        [SerializeField, Range(0.90f, 0.999f)]
        private float waitForAuthoritativeCompletionAt = 0.99f;
        [SerializeField, Min(0f)]
        private float completionHoldSeconds = 0.15f;
        [SerializeField, Range(0f, 0.5f)]
        private float fadeSeconds = 0.10f;
        [SerializeField, Range(0f, 0.5f)]
        private float fillSmoothingSeconds = 0.04f;

        private bool _active;
        private long _token;
        private float _startedAt;
        private float _durationSeconds;
        private float _displayedProgress;
        private float _hideAt = -1f;
        private float _targetAlpha;
        private bool _resetWhenHidden;

        public bool IsActive => _active;
        public long ActiveToken => _token;

        private void Awake()
        {
            ResolveReferences();
            InitializeVisualState();
        }

        private void OnDisable()
        {
            ResetImmediately();
        }

        public void Bind(
            Slider slider,
            CanvasGroup group = null)
        {
            if (slider != null)
                progressSlider = slider;
            if (group != null)
                canvasGroup = group;

            ResolveReferences();
            if (progressSlider != null)
            {
                enabled = true;
                InitializeVisualState();
            }
        }

        public void Configure(
            float waitAt,
            float holdSeconds,
            float fadeDuration = 0.10f,
            float smoothingSeconds = 0.04f)
        {
            waitForAuthoritativeCompletionAt =
                Mathf.Clamp(waitAt, 0.90f, 0.999f);
            completionHoldSeconds =
                Mathf.Max(0f, holdSeconds);
            fadeSeconds =
                Mathf.Clamp(fadeDuration, 0f, 0.5f);
            fillSmoothingSeconds =
                Mathf.Clamp(smoothingSeconds, 0f, 0.5f);
        }

        public void Begin(
            long token,
            float durationSeconds)
        {
            ResolveReferences();
            if (progressSlider == null)
                return;

            _token = token;
            _durationSeconds =
                Mathf.Max(0.01f, durationSeconds);
            _startedAt = Time.unscaledTime;
            _hideAt = -1f;
            _active = true;
            _resetWhenHidden = false;
            _displayedProgress = 0f;

            SetProgress(0f);
            Show();
        }

        public bool Complete(long token)
        {
            if (!Matches(token))
                return false;

            _active = false;
            _displayedProgress = 1f;
            SetProgress(1f);
            _hideAt =
                Time.unscaledTime +
                completionHoldSeconds;
            return true;
        }

        public bool Cancel(long token)
        {
            if (!Matches(token))
                return false;

            _active = false;
            _hideAt = -1f;
            Hide(resetWhenHidden: true);
            return true;
        }

        public void CancelCurrent()
        {
            if (_token == 0L && !_active && _hideAt < 0f)
                return;

            _active = false;
            _hideAt = -1f;
            Hide(resetWhenHidden: true);
        }

        private void Update()
        {
            if (progressSlider == null ||
                canvasGroup == null)
            {
                return;
            }

            if (_active)
            {
                float elapsed =
                    Mathf.Max(
                        0f,
                        Time.unscaledTime - _startedAt);
                float normalized =
                    Mathf.Clamp01(
                        elapsed /
                        Mathf.Max(0.01f, _durationSeconds));
                float target =
                    Mathf.Min(
                        normalized,
                        waitForAuthoritativeCompletionAt);

                if (fillSmoothingSeconds <= 0.0001f)
                {
                    _displayedProgress = target;
                }
                else
                {
                    float speed =
                        1f /
                        fillSmoothingSeconds;
                    _displayedProgress =
                        Mathf.MoveTowards(
                            _displayedProgress,
                            target,
                            Time.unscaledDeltaTime * speed);
                }

                SetProgress(_displayedProgress);
            }
            else if (_hideAt >= 0f &&
                     Time.unscaledTime >= _hideAt)
            {
                _hideAt = -1f;
                Hide(resetWhenHidden: true);
            }

            float nextAlpha;
            if (fadeSeconds <= 0.0001f)
            {
                nextAlpha = _targetAlpha;
            }
            else
            {
                nextAlpha =
                    Mathf.MoveTowards(
                        canvasGroup.alpha,
                        _targetAlpha,
                        Time.unscaledDeltaTime /
                        fadeSeconds);
            }

            canvasGroup.alpha = nextAlpha;

            if (_resetWhenHidden &&
                _targetAlpha <= 0f &&
                canvasGroup.alpha <= 0.001f)
            {
                _resetWhenHidden = false;
                ResetLogicalState();
                SetProgress(0f);
            }
        }

        private bool Matches(long token)
        {
            return (_active || _hideAt >= 0f) &&
                   token == _token;
        }

        private void ResolveReferences()
        {
            if (progressSlider == null)
                progressSlider = GetComponent<Slider>();

            if (canvasGroup == null)
                canvasGroup = GetComponent<CanvasGroup>();

            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();

            // A specialized adapter may bind a child/external Slider immediately after this
            // component is added. Do not permanently disable here just because GetComponent
            // did not find a Slider on the same GameObject during Awake.
        }

        private void InitializeVisualState()
        {
            if (progressSlider == null ||
                canvasGroup == null)
            {
                return;
            }

            progressSlider.minValue = 0f;
            progressSlider.maxValue = 1f;
            progressSlider.wholeNumbers = false;

            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            ResetLogicalState();
            SetProgress(0f);
            canvasGroup.alpha = 0f;
            _targetAlpha = 0f;
            _resetWhenHidden = false;
        }

        private void Show()
        {
            if (canvasGroup == null)
                return;

            _targetAlpha = 1f;
            if (fadeSeconds <= 0.0001f)
                canvasGroup.alpha = 1f;
        }

        private void Hide(bool resetWhenHidden)
        {
            if (canvasGroup == null)
                return;

            _targetAlpha = 0f;
            _resetWhenHidden |= resetWhenHidden;

            if (fadeSeconds <= 0.0001f)
            {
                canvasGroup.alpha = 0f;
                if (_resetWhenHidden)
                {
                    _resetWhenHidden = false;
                    ResetLogicalState();
                    SetProgress(0f);
                }
            }
        }

        private void ResetImmediately()
        {
            ResolveReferences();
            ResetLogicalState();
            _displayedProgress = 0f;
            _hideAt = -1f;
            _targetAlpha = 0f;
            _resetWhenHidden = false;

            SetProgress(0f);

            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.interactable = false;
                canvasGroup.blocksRaycasts = false;
            }
        }

        private void ResetLogicalState()
        {
            _active = false;
            _token = 0L;
            _startedAt = 0f;
            _durationSeconds = 0f;
            _hideAt = -1f;
        }

        private void SetProgress(float value)
        {
            if (progressSlider != null)
            {
                progressSlider.SetValueWithoutNotify(
                    Mathf.Clamp01(value));
            }
        }
    }
}
