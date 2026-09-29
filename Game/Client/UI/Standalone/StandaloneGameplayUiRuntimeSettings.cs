using Player.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Applies local presentation settings to the already-authored standalone Canvas.
    /// It never builds UI at runtime and owns no network state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StandaloneGameplayUiRuntimeSettings : MonoBehaviour
    {
        [SerializeField] private CanvasScaler canvasScaler;
        [SerializeField] private Vector2 authoredReferenceResolution = new Vector2(1920f, 1080f);
        [SerializeField] private float authoredScaleFactor = 1f;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            PlayerGameplayUiConfig.Changed += Apply;
            Apply();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            PlayerGameplayUiConfig.Changed -= Apply;
#endif
        }

        private void Apply()
        {
#if !UNITY_SERVER
            if (canvasScaler == null)
                return;

            float scale = Mathf.Clamp(PlayerGameplayUiConfig.UiScale, 0.75f, 1.50f);
            switch (canvasScaler.uiScaleMode)
            {
                case CanvasScaler.ScaleMode.ScaleWithScreenSize:
                    Vector2 baseline = authoredReferenceResolution;
                    if (baseline.x <= 1f || baseline.y <= 1f)
                        baseline = new Vector2(1920f, 1080f);
                    canvasScaler.referenceResolution = baseline / scale;
                    break;
                case CanvasScaler.ScaleMode.ConstantPixelSize:
                    canvasScaler.scaleFactor = Mathf.Max(0.01f, authoredScaleFactor) * scale;
                    break;
            }
#endif
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(CanvasScaler scaler)
        {
            canvasScaler = scaler;
            if (scaler != null)
            {
                authoredReferenceResolution = scaler.referenceResolution;
                authoredScaleFactor = scaler.scaleFactor;
            }
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
