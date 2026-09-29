using Player.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>One authored row inside Player Config / Controls.</summary>
    public sealed class StandaloneControlBindingRow : MonoBehaviour
    {
        [SerializeField] private PlayerControlAction action;
        [SerializeField] private Text actionText;
        [SerializeField] private Text bindingText;
        [SerializeField] private Button rebindButton;
        [SerializeField] private StandaloneControlsWindow owner;

        public PlayerControlAction Action => action;

        private void Awake() => rebindButton?.onClick.AddListener(OnClicked);
        private void OnDestroy() => rebindButton?.onClick.RemoveListener(OnClicked);

        private void OnClicked() => owner?.BeginRebind(action);

        public void Refresh(bool awaitingThisAction)
        {
            if (actionText != null)
                actionText.text = PlayerControlConfig.DisplayName(action);
            if (bindingText != null)
                bindingText.text = awaitingThisAction ? "Press key..." : PlayerControlConfig.BindingLabel(action);
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(
            PlayerControlAction authoredAction,
            Text authoredActionText,
            Text authoredBindingText,
            Button authoredButton,
            StandaloneControlsWindow authoredOwner)
        {
            action = authoredAction;
            actionText = authoredActionText;
            bindingText = authoredBindingText;
            rebindButton = authoredButton;
            owner = authoredOwner;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
