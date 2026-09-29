using System.Text;
using Game.Shared.Abilities;
using Game.Shared.Resources;
using Player.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>Authored reusable row for one learned ability.</summary>
    public sealed class StandaloneAbilityRow : MonoBehaviour, IBeginDragHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Text nameText;
        [SerializeField] private Text categoryText;
        [SerializeField] private Text detailText;
        [SerializeField] private CanvasGroup canvasGroup;

        private GameplayAbilityClientReference _ability;
        private StandaloneHudTooltipPanel _tooltip;
        private bool _bound;

        public void Bind(GameplayAbilityClientReference ability, StandaloneHudTooltipPanel tooltip)
        {
            _ability = ability;
            _tooltip = tooltip;
            _bound = ability.WireId != 0 && !string.IsNullOrWhiteSpace(ability.DefinitionId);
            Refresh();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_bound || eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            StandaloneAbilityDragContext.Begin(_ability);
            EnsureCanvasGroup();
            canvasGroup.blocksRaycasts = false;
            canvasGroup.alpha = 0.65f;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            EnsureCanvasGroup();
            canvasGroup.blocksRaycasts = true;
            canvasGroup.alpha = 1f;
            StandaloneAbilityDragContext.End();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_bound || _tooltip == null)
                return;
            _tooltip.ShowText(DisplayName(_ability), BuildTooltip(_ability));
        }

        public void OnPointerExit(PointerEventData eventData) => _tooltip?.Hide();

        private void Refresh()
        {
            if (nameText != null)
                nameText.text = _bound ? DisplayName(_ability) : "Ability";
            if (categoryText != null)
                categoryText.text = _bound ? ((AbilityCategory)_ability.presentation.category).ToString().ToUpperInvariant() : string.Empty;
            if (detailText != null)
                detailText.text = _bound ? BuildCompactDetail(_ability) : string.Empty;
        }

        private static string DisplayName(GameplayAbilityClientReference ability) =>
            string.IsNullOrWhiteSpace(ability.presentation.displayName) ? ability.DefinitionId : ability.presentation.displayName;

        private static string BuildCompactDetail(GameplayAbilityClientReference ability)
        {
            var builder = new StringBuilder(96);
            if (ability.presentation.resourceCost > 0)
            {
                builder.Append(ResourceLabel(ability.presentation.resourceId)).Append(' ').Append(ability.presentation.resourceCost);
                builder.Append("   ");
            }
            if (ability.ability.castTimeSeconds > 0f)
                builder.Append("Cast ").Append(ability.ability.castTimeSeconds.ToString("0.##")).Append("s   ");
            if (ability.ability.cooldownSeconds > 0f)
                builder.Append("CD ").Append(ability.ability.cooldownSeconds.ToString("0.##")).Append("s   ");
            if (ability.presentation.range > 0f)
                builder.Append("Range ").Append(ability.presentation.range.ToString("0.#")).Append('m');
            return builder.ToString().Trim();
        }

        private static string BuildTooltip(GameplayAbilityClientReference ability)
        {
            var builder = new StringBuilder(256);
            if (!string.IsNullOrWhiteSpace(ability.presentation.description))
                builder.AppendLine(ability.presentation.description.Trim()).AppendLine();
            builder.Append("Category: ").AppendLine(((AbilityCategory)ability.presentation.category).ToString());
            builder.Append("Target: ").Append((AbilityTargetMode)ability.presentation.targetMode)
                .Append(" / ").AppendLine(((AbilityTargetRelation)ability.presentation.targetRelation).ToString());
            if (ability.presentation.resourceCost > 0)
                builder.Append("Cost: ").Append(ability.presentation.resourceCost).Append(' ').AppendLine(ResourceLabel(ability.presentation.resourceId));
            if (ability.ability.castTimeSeconds > 0f)
                builder.Append("Cast: ").Append(ability.ability.castTimeSeconds.ToString("0.##")).AppendLine("s");
            builder.Append("Cooldown: ").Append(ability.ability.cooldownSeconds.ToString("0.##")).AppendLine("s");
            if (ability.presentation.range > 0f)
                builder.Append("Range: ").Append(ability.presentation.range.ToString("0.#")).AppendLine("m");
            builder.AppendLine().Append("Drag to the hotbar to assign.");
            return builder.ToString().TrimEnd();
        }

        private static string ResourceLabel(ushort resourceId)
        {
            CharacterResourceId id = (CharacterResourceId)resourceId;
            switch (id)
            {
                case CharacterResourceId.Health: return "HP";
                case CharacterResourceId.Mana: return "Mana";
                case CharacterResourceId.Stamina: return "Stamina";
                default: return id == CharacterResourceId.None ? "Resource" : id.ToString();
            }
        }

        private void EnsureCanvasGroup()
        {
            if (canvasGroup == null)
                canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(Text authoredName, Text authoredCategory, Text authoredDetail)
        {
            nameText = authoredName;
            categoryText = authoredCategory;
            detailText = authoredDetail;
            canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
