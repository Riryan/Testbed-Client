using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringColorCell : MonoBehaviour
    {
        [SerializeField] private ushort colorId;
        [SerializeField] private Image background;
        [SerializeField] private Image swatchImage;
        [SerializeField] private Button previewButton;
        [SerializeField] private Button allowButton;
        [SerializeField] private Text allowMark;
        [SerializeField] private Text idText;

        private bool _allowed = true;

        public ushort ColorId => colorId;

        public void Configure(Color swatchColor, bool allowed, Action<ushort> preview, Action<ushort> toggleAllowed)
        {
            if (swatchImage != null) swatchImage.color = swatchColor;
            if (idText != null)
            {
                idText.text = colorId.ToString();
                float luminance = 0.2126f * swatchColor.r + 0.7152f * swatchColor.g + 0.0722f * swatchColor.b;
                idText.color = luminance > 0.5f ? Color.black : Color.white;
            }

            if (previewButton != null)
            {
                previewButton.onClick.RemoveAllListeners();
                if (preview != null) previewButton.onClick.AddListener(() => preview(colorId));
            }

            if (allowButton != null)
            {
                allowButton.onClick.RemoveAllListeners();
                if (toggleAllowed != null) allowButton.onClick.AddListener(() => toggleAllowed(colorId));
            }

            SetAllowed(allowed);
            SetPreviewSelected(false);
        }

        public void SetAllowed(bool allowed)
        {
            _allowed = allowed;
            if (allowMark != null)
            {
                allowMark.text = allowed ? "✓" : string.Empty;
                allowMark.color = allowed ? new Color(0.65f, 1f, 0.7f, 1f) : Color.clear;
            }
        }

        public void SetPreviewSelected(bool selected)
        {
            if (background == null)
                return;
            background.color = selected
                ? new Color(0.34f, 0.39f, 0.46f, 1f)
                : (_allowed ? new Color(0.12f, 0.13f, 0.15f, 0.96f) : new Color(0.07f, 0.075f, 0.085f, 0.8f));
        }
    }
}
