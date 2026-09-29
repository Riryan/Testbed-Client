using System;
using Player.Client;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// Authored hotbar drop target/presentation. Assignments are local PlayerHotbarConfig state;
    /// activation is owned by ClientGameplayUIRoot and remains server-authoritative.
    /// </summary>
    public sealed class StandaloneHotbarSlotView : MonoBehaviour, IDropHandler, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        [SerializeField, Range(1, PlayerHotbarConfig.SlotCount)] private int oneBasedSlot = 1;
        [SerializeField] private Text hotkeyText;
        [SerializeField] private Text assignmentText;
        [SerializeField] private StandaloneHudTooltipPanel tooltip;

        public int Slot => oneBasedSlot;

        private void Awake()
        {
#if !UNITY_SERVER
            PlayerHotbarConfig.Changed += OnHotbarChanged;
            PlayerControlConfig.BindingChanged += OnControlChanged;
            Refresh();
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            PlayerHotbarConfig.Changed -= OnHotbarChanged;
            PlayerControlConfig.BindingChanged -= OnControlChanged;
#endif
        }

        public void Refresh()
        {
#if !UNITY_SERVER
            if (hotkeyText != null)
                hotkeyText.text = PlayerControlConfig.BindingLabel(PlayerControlConfig.HotbarAction(oneBasedSlot));
            if (assignmentText != null)
                assignmentText.text = AssignmentLabel(PlayerHotbarConfig.Get(oneBasedSlot));
#endif
        }

        public void OnDrop(PointerEventData eventData)
        {
#if !UNITY_SERVER
            if (!StandaloneAbilityDragContext.Active)
                return;
            GameplayAbilityClientReference ability = StandaloneAbilityDragContext.Ability;
            if (ability.WireId == 0 || string.IsNullOrWhiteSpace(ability.DefinitionId))
                return;
            PlayerHotbarConfig.AssignAbility(oneBasedSlot, ability.DefinitionId);
            tooltip?.ShowText(DisplayName(ability), $"Assigned to hotbar slot {oneBasedSlot}.");
#endif
        }

        public void OnPointerClick(PointerEventData eventData)
        {
#if !UNITY_SERVER
            if (eventData != null && eventData.button == PointerEventData.InputButton.Right)
            {
                PlayerHotbarConfig.Clear(oneBasedSlot);
                tooltip?.ShowText("Hotbar", $"Cleared slot {oneBasedSlot}.");
            }
#endif
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
#if !UNITY_SERVER
            if (tooltip == null)
                return;
            PlayerHotbarAssignment assignment = PlayerHotbarConfig.Get(oneBasedSlot);
            tooltip.ShowText($"Hotbar {oneBasedSlot}", AssignmentTooltip(assignment));
#endif
        }

        public void OnPointerExit(PointerEventData eventData) => tooltip?.Hide();

        private void OnHotbarChanged(int slot, PlayerHotbarAssignment assignment)
        {
            if (slot == oneBasedSlot)
                Refresh();
        }

        private void OnControlChanged(PlayerControlAction action, KeyCode key)
        {
            if (action == PlayerControlConfig.HotbarAction(oneBasedSlot))
                Refresh();
        }

        private static string AssignmentLabel(PlayerHotbarAssignment assignment)
        {
            switch (assignment.Kind)
            {
                case PlayerHotbarEntryKind.BasicLight: return "LIGHT";
                case PlayerHotbarEntryKind.BasicHeavy: return "HEAVY";
                case PlayerHotbarEntryKind.Ability:
                    if (PlayerGameplaySettingsRuntime.TryGetAbilityClientReference(assignment.AbilityDefinitionId, out GameplayAbilityClientReference ability))
                        return DisplayName(ability);
                    return "UNAVAILABLE";
                default: return string.Empty;
            }
        }

        private static string AssignmentTooltip(PlayerHotbarAssignment assignment)
        {
            switch (assignment.Kind)
            {
                case PlayerHotbarEntryKind.BasicLight: return "Light basic attack.\nRight-click to clear.";
                case PlayerHotbarEntryKind.BasicHeavy: return "Heavy basic attack.\nRight-click to clear.";
                case PlayerHotbarEntryKind.Ability:
                    if (PlayerGameplaySettingsRuntime.TryGetAbilityClientReference(assignment.AbilityDefinitionId, out GameplayAbilityClientReference ability))
                        return DisplayName(ability) + "\nRight-click to clear.";
                    return "Assigned ability is no longer available in the current content catalog.\nRight-click to clear.";
                default: return "Empty slot. Drag a learned ability here.";
            }
        }

        private static string DisplayName(GameplayAbilityClientReference ability) =>
            ability.DisplayName;

#if UNITY_EDITOR
        public void ConfigureForEditor(int slot, Text authoredHotkey, Text authoredAssignment, StandaloneHudTooltipPanel authoredTooltip)
        {
            oneBasedSlot = Mathf.Clamp(slot, 1, PlayerHotbarConfig.SlotCount);
            hotkeyText = authoredHotkey;
            assignmentText = authoredAssignment;
            tooltip = authoredTooltip;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
