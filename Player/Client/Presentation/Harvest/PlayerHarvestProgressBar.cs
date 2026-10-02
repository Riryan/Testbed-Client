using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Harvest adapter for the reusable PlayerActionProgressBar.
    ///
    /// Harvest still uses the existing authoritative Started/terminal HarvestEvent messages.
    /// The generic bar interpolates locally from the authoritative duration, so normal progress
    /// presentation adds no polling or progress packets.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerHarvestProgressBar : MonoBehaviour
    {
        [SerializeField] private Slider progressSlider;
        [SerializeField] private CanvasGroup canvasGroup;

        [Header("Shared Progress Presentation")]
        [SerializeField, Range(0.90f, 0.999f)]
        private float waitForServerAt = 0.99f;
        [SerializeField, Min(0f)]
        private float successHoldSeconds = 0.15f;
        [SerializeField, Range(0f, 0.5f)]
        private float fadeSeconds = 0.10f;
        [SerializeField, Range(0f, 0.5f)]
        private float fillSmoothingSeconds = 0.04f;

        private PlayerActionProgressBar _progressBar;

        private void Awake()
        {
            ResolveProgressBar();
        }

        private void OnEnable()
        {
            ResolveProgressBar();
            PlayerHarvestPresentation.HarvestEventPresented +=
                OnHarvestEvent;
        }

        private void OnDisable()
        {
            PlayerHarvestPresentation.HarvestEventPresented -=
                OnHarvestEvent;

            if (_progressBar != null)
                _progressBar.CancelCurrent();
        }

        private void ResolveProgressBar()
        {
            if (_progressBar == null)
                _progressBar = GetComponent<PlayerActionProgressBar>();

            if (_progressBar == null)
                _progressBar = gameObject.AddComponent<PlayerActionProgressBar>();

            _progressBar.Bind(
                progressSlider,
                canvasGroup);

            _progressBar.Configure(
                waitForServerAt,
                successHoldSeconds,
                fadeSeconds,
                fillSmoothingSeconds);
        }

        private void OnHarvestEvent(
            PlayerHarvestEventMessage message)
        {
            ResolveProgressBar();

            switch (message.Phase)
            {
                case PlayerHarvestEventPhase.Started:
                    _progressBar.Begin(
                        message.stableId,
                        message.DurationSeconds);
                    break;

                case PlayerHarvestEventPhase.Succeeded:
                    _progressBar.Complete(
                        message.stableId);
                    break;

                case PlayerHarvestEventPhase.Failed:
                case PlayerHarvestEventPhase.Cancelled:
                    _progressBar.Cancel(
                        message.stableId);
                    break;
            }
        }
    }
}
