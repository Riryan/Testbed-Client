using System;
using System.Text;
using Cysharp.Threading.Tasks;
using Game.Client.Content;
using Game.Client.UI.Root;
using Game.Shared.Content;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Recovery
{
    /// <summary>
    /// Minimal event-driven presentation for recovered progression, reputation/Heat and
    /// crafting. Layout is prefab-authored; runtime only binds state and instantiates the
    /// authored recipe-row template for locally cached recipe definitions.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientRecoveryUI : MonoBehaviour
    {
        [Header("Authored recovery views")]
        [SerializeField] private Text skillsText;
        [SerializeField] private Text professionsText;
        [SerializeField] private Text heatText;
        [SerializeField] private Text craftingText;
        [SerializeField] private RectTransform craftingButtonsRoot;
        [SerializeField] private Button recipeButtonTemplate;

        private ClientUIRoot _root;
        private PlayerEntityGameManager _manager;
        private long _craftingStationStableId;
        private bool _craftBusy;

        public bool HasAuthoredBindings =>
            skillsText != null && professionsText != null && heatText != null && craftingText != null &&
            craftingButtonsRoot != null && recipeButtonTemplate != null;

#if UNITY_EDITOR
        public void ConfigureAuthoredViewsForEditor(
            Text skills,
            Text professions,
            Text heat,
            Text craftingSummary,
            RectTransform recipeRoot,
            Button recipeTemplate)
        {
            skillsText = skills;
            professionsText = professions;
            heatText = heat;
            craftingText = craftingSummary;
            craftingButtonsRoot = recipeRoot;
            recipeButtonTemplate = recipeTemplate;
        }
#endif

        public void BindRoot(ClientUIRoot root)
        {
            if (_root == root) return;
            if (_root?.Windows != null) _root.Windows.PanelVisibilityChanged -= OnPanelVisibilityChanged;
            _root = root;
            if (_root?.Windows != null) _root.Windows.PanelVisibilityChanged += OnPanelVisibilityChanged;
        }

        public void BindManager(PlayerEntityGameManager manager)
        {
            if (_manager == manager) return;
            if (_manager != null) _manager.ProgressionSnapshotReceived -= OnProgressionChanged;
            _manager = manager;
            if (_manager != null) _manager.ProgressionSnapshotReceived += OnProgressionChanged;
            RefreshVisible();
        }

        public void OpenCrafting(long stationStableId)
        {
            if (stationStableId <= 0 || _root?.Windows == null || !HasAuthoredBindings) return;
            _craftingStationStableId = stationStableId;
            RenderCrafting();
            _root.Windows.Open(ClientUIPanelId.CraftingWindow);
        }

        private void OnDestroy()
        {
            if (_root?.Windows != null) _root.Windows.PanelVisibilityChanged -= OnPanelVisibilityChanged;
            if (_manager != null) _manager.ProgressionSnapshotReceived -= OnProgressionChanged;
        }

        private void OnProgressionChanged(ProgressionSnapshotMessage _) => RefreshVisible();

        private void OnPanelVisibilityChanged(ClientUIPanelId panel, bool visible)
        {
            if (!visible || !HasAuthoredBindings) return;
            switch (panel)
            {
                case ClientUIPanelId.SkillsWindow: RenderSkills(); break;
                case ClientUIPanelId.ProfessionsWindow: RenderProfessions(); break;
                case ClientUIPanelId.HeatBountyWindow: RenderHeat(); break;
                case ClientUIPanelId.CraftingWindow: RenderCrafting(); break;
            }
        }

        private void RefreshVisible()
        {
            if (_root?.Windows == null || !HasAuthoredBindings) return;
            if (_root.Windows.IsOpen(ClientUIPanelId.SkillsWindow)) RenderSkills();
            if (_root.Windows.IsOpen(ClientUIPanelId.ProfessionsWindow)) RenderProfessions();
            if (_root.Windows.IsOpen(ClientUIPanelId.HeatBountyWindow)) RenderHeat();
            if (_root.Windows.IsOpen(ClientUIPanelId.CraftingWindow)) RenderCrafting();
        }

        private ProgressionSnapshotMessage State => _manager != null ? _manager.LatestProgression : default;

        private void RenderSkills()
        {
            ProgressionSnapshotMessage state = State;
            if (!state.success) { skillsText.text = "Progression state has not been received yet."; return; }
            var sb = new StringBuilder(512);
            sb.Append("Level ").Append(state.level).Append("   XP ").Append(state.experience).AppendLine();
            AppendTracks(sb, state, includeProfessions: false);
            skillsText.text = sb.ToString();
        }

        private void RenderProfessions()
        {
            ProgressionSnapshotMessage state = State;
            if (!state.success) { professionsText.text = "Progression state has not been received yet."; return; }
            var sb = new StringBuilder(384);
            AppendTracks(sb, state, includeProfessions: true);
            if (sb.Length == 0) sb.Append("No profession progress yet.");
            professionsText.text = sb.ToString();
        }

        private static void AppendTracks(StringBuilder sb, ProgressionSnapshotMessage state, bool includeProfessions)
        {
            ProgressTrackWire[] tracks = state.tracks ?? Array.Empty<ProgressTrackWire>();
            int written = 0;
            for (int i = 0; i < tracks.Length; ++i)
            {
                ProgressTrackWire track = tracks[i];
                bool isProfession = ClientRecoveryContentCache.TryGetTrack(track.dataId, out ProgressTrackDefinition definition) &&
                                    definition != null && definition.kind == ProgressTrackKind.Profession;
                if (isProfession != includeProfessions) continue;
                string label = definition == null
                    ? $"Track #{track.dataId}"
                    : string.IsNullOrWhiteSpace(definition.displayName) ? definition.definitionId : definition.displayName;
                sb.Append(label).Append(": ").Append(track.value);
                if (definition != null && definition.maximumValue > 0) sb.Append(" / ").Append(definition.maximumValue);
                sb.AppendLine();
                written++;
            }
            if (!includeProfessions && written == 0) sb.Append("No active mastery/progression tracks yet.");
        }

        private void RenderHeat()
        {
            ProgressionSnapshotMessage state = State;
            if (!state.success) { heatText.text = "Reputation/Heat state has not been received yet."; return; }
            var sb = new StringBuilder(512);
            if (state.factionDataId != 0)
            {
                string faction = ClientRecoveryContentCache.TryGetFaction(state.factionDataId, out FactionDefinition f) && f != null
                    ? (string.IsNullOrWhiteSpace(f.displayName) ? f.definitionId : f.displayName)
                    : $"Faction #{state.factionDataId}";
                sb.Append("Faction: ").Append(faction).AppendLine();
            }
            ReputationWire[] rep = state.reputation ?? Array.Empty<ReputationWire>();
            for (int i = 0; i < rep.Length; ++i)
            {
                string name = ClientRecoveryContentCache.TryGetFaction(rep[i].factionDataId, out FactionDefinition f) && f != null
                    ? (string.IsNullOrWhiteSpace(f.displayName) ? f.definitionId : f.displayName)
                    : $"Faction #{rep[i].factionDataId}";
                sb.Append("Rep — ").Append(name).Append(": ").Append(rep[i].value).AppendLine();
            }
            HeatWire[] heat = state.heat ?? Array.Empty<HeatWire>();
            for (int i = 0; i < heat.Length; ++i)
            {
                string name = ClientRecoveryContentCache.TryGetJurisdiction(heat[i].jurisdictionDataId, out JurisdictionDefinition j) && j != null
                    ? (string.IsNullOrWhiteSpace(j.displayName) ? j.definitionId : j.displayName)
                    : $"Jurisdiction #{heat[i].jurisdictionDataId}";
                sb.Append("Heat — ").Append(name).Append(": ").Append(heat[i].value)
                    .Append("   Bounty ").Append(heat[i].bounty).Append("   Evidence ").Append(heat[i].evidence).AppendLine();
            }
            if (sb.Length == 0) sb.Append("No faction, reputation, or Heat state yet.");
            heatText.text = sb.ToString();
        }

        private void RenderCrafting()
        {
            ClearCraftButtons();
            ProgressionSnapshotMessage state = State;
            if (!ClientRecoveryContentCache.Loaded)
            {
                craftingText.text = "Client recipe catalog unavailable.\n" + ClientRecoveryContentCache.LoadError;
                return;
            }
            if (!state.success)
            {
                craftingText.text = "Known recipe state has not been received yet.";
                return;
            }

            RecipeDefinition[] recipes = ClientRecoveryContentCache.GetKnownRecipes(state.knownRecipeDataIds);
            craftingText.text = _craftingStationStableId > 0
                ? $"Station {_craftingStationStableId} — {recipes.Length} known recipe(s)."
                : $"{recipes.Length} known recipe(s). Select a crafting station first.";

            int count = Math.Min(recipes.Length, 16);
            for (int i = 0; i < count; ++i) CreateRecipeButton(recipes[i]);
        }

        private void CreateRecipeButton(RecipeDefinition recipe)
        {
            Button button = Instantiate(recipeButtonTemplate, craftingButtonsRoot, false);
            button.gameObject.name = $"Recipe_{recipe.dataId}";
            button.gameObject.SetActive(true);
            ushort recipeId = recipe.dataId;
            button.interactable = !_craftBusy && _craftingStationStableId > 0;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => CraftAsync(recipeId).Forget());
            Text label = button.GetComponentInChildren<Text>(true);
            if (label != null) label.text = BuildRecipeLabel(recipe);
        }

        private static string BuildRecipeLabel(RecipeDefinition recipe)
        {
            var sb = new StringBuilder(160);
            sb.Append(string.IsNullOrWhiteSpace(recipe.displayName) ? recipe.definitionId : recipe.displayName);
            RecipeIngredientDefinition[] ingredients = recipe.ingredients ?? Array.Empty<RecipeIngredientDefinition>();
            if (ingredients.Length > 0) sb.Append("  [");
            for (int i = 0; i < ingredients.Length; ++i)
            {
                if (i > 0) sb.Append(", ");
                RecipeIngredientDefinition item = ingredients[i];
                string name = item.itemDefinitionId;
                if (PlayerGameplaySettingsRuntime.TryGetItem(item.itemDataId, out GameplayItemReferenceWire wire) && !string.IsNullOrWhiteSpace(wire.displayName))
                    name = wire.displayName;
                if (string.IsNullOrWhiteSpace(name)) name = $"item#{item.itemDataId}";
                sb.Append(name).Append(" x").Append(item.quantity);
            }
            if (ingredients.Length > 0) sb.Append(']');
            return sb.ToString();
        }

        private async UniTaskVoid CraftAsync(ushort recipeDataId)
        {
            if (_craftBusy || _manager == null || _craftingStationStableId <= 0 || recipeDataId == 0) return;
            _craftBusy = true;
            RenderCrafting();
            try
            {
                CraftResponseMessage response = await _manager.CraftAsync(_craftingStationStableId, recipeDataId);
                await UniTask.SwitchToMainThread();
                _root?.PresentInteractionFeedback(response.success
                    ? "CRAFTING: craft completed authoritatively."
                    : $"CRAFTING: {response.error}");
            }
            finally
            {
                _craftBusy = false;
                RenderCrafting();
            }
        }

        private void ClearCraftButtons()
        {
            if (craftingButtonsRoot == null) return;
            for (int i = craftingButtonsRoot.childCount - 1; i >= 0; --i)
            {
                Transform child = craftingButtonsRoot.GetChild(i);
                if (recipeButtonTemplate != null && child == recipeButtonTemplate.transform) continue;
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }
        }
    }
}
