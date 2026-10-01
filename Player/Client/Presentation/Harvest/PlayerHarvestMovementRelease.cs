using Player.Networking;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Narrow client-side recovery for timed Harvest movement.
    ///
    /// The normal client-safe shared-world prediction remains enabled everywhere else.
    /// While an authoritative Harvest is active, horizontal input must be allowed to reach
    /// the existing standalone GameServer movement/session path so movement can cancel or
    /// leave the interaction normally. This also removes the previous jump-only escape
    /// asymmetry because Jump was already the one movement edge that bypassed grounded
    /// shared-world suppression.
    ///
    /// No movement authority moves to the client and no new network message is introduced.
    /// The GameServer still validates every MovementCommand and the interaction session.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class PlayerHarvestMovementRelease : MonoBehaviour
    {
        private const float TerminalPredictionGraceSeconds = 0.60f;

        private PlayerEntityInput _input;
        private bool _savedPredictionEnabled;
        private bool _bypassHeld;
        private bool _harvestActive;
        private float _restoreAt = -1f;

        private void OnEnable()
        {
            PlayerHarvestPresentation.HarvestEventPresented += OnHarvestEvent;
        }

        private void OnDisable()
        {
            PlayerHarvestPresentation.HarvestEventPresented -= OnHarvestEvent;
            RestorePrediction();
            _harvestActive = false;
            _restoreAt = -1f;
        }

        private void Update()
        {
            if (_harvestActive)
            {
                EnsurePredictionBypass();
                return;
            }

            if (_bypassHeld && _restoreAt >= 0f && Time.unscaledTime >= _restoreAt)
                RestorePrediction();
        }

        private void OnHarvestEvent(PlayerHarvestEventMessage message)
        {
            if (message.Phase == PlayerHarvestEventPhase.Started)
            {
                _harvestActive = true;
                _restoreAt = -1f;
                EnsurePredictionBypass();
                return;
            }

            if (message.Phase == PlayerHarvestEventPhase.Succeeded ||
                message.Phase == PlayerHarvestEventPhase.Failed ||
                message.Phase == PlayerHarvestEventPhase.Cancelled)
            {
                _harvestActive = false;
                _restoreAt = Time.unscaledTime + TerminalPredictionGraceSeconds;
            }
        }

        private void EnsurePredictionBypass()
        {
            if (_bypassHeld && _input != null)
                return;

            if (_bypassHeld)
                RestorePrediction();

            if (!PlayerEntityClient.TryGetOwner(out PlayerEntityClient owner) || owner == null)
                return;

            PlayerEntityInput input = owner.GetComponent<PlayerEntityInput>();
            if (input == null)
                return;

            _input = input;
            _savedPredictionEnabled = input.useSharedWorldPrediction;
            input.useSharedWorldPrediction = false;
            _bypassHeld = true;
        }

        private void RestorePrediction()
        {
            if (_bypassHeld && _input != null)
                _input.useSharedWorldPrediction = _savedPredictionEnabled;

            _input = null;
            _bypassHeld = false;
            _restoreAt = -1f;
        }
    }

    internal static class PlayerHarvestMovementReleaseBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Object.FindFirstObjectByType<PlayerHarvestMovementRelease>(FindObjectsInactive.Include) != null)
                return;

            PlayerHarvestPresentation presentation =
                Object.FindFirstObjectByType<PlayerHarvestPresentation>(FindObjectsInactive.Include);

            if (presentation != null)
            {
                presentation.gameObject.AddComponent<PlayerHarvestMovementRelease>();
                return;
            }

            var go = new GameObject("Client Harvest Movement Release");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<PlayerHarvestMovementRelease>();
        }
    }
}
