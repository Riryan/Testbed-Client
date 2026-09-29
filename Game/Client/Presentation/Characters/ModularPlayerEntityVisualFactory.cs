#if !UNITY_SERVER
using Game.Shared.Characters;
using Game.Shared.Combat;
using Player.Client.Presentation;
using Player.Networking;
using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Concrete Game.Client adapter for the canonical PlayerEntityClient presentation path.
    /// It reconstructs a humanoid locally from compact catalog ids and reuses the same
    /// ModularCharacterAppearancePresenter used by the character preview/creator tooling.
    /// Character Creator remains an authoring surface only; it does not own runtime players.
    /// </summary>
    internal sealed class ModularPlayerEntityVisualFactory : IPlayerEntityVisualFactory
    {
        public bool TryCreateOrUpdate(
            Transform currentVisual,
            CharacterAppearanceRecipe appearance,
            long stableAppearanceId,
            bool populationPresentation,
            out Transform resolvedVisual)
        {
            resolvedVisual = currentVisual;
            if (appearance == null)
                return false;

            CharacterAppearanceRecipe effective = appearance;
            bool emptyRecipe =
                (appearance.meshes == null || appearance.meshes.Length == 0) &&
                (appearance.morphs == null || appearance.morphs.Length == 0) &&
                (appearance.colors == null || appearance.colors.Length == 0);

            // Standard ambient Population arrives with the existing actor id plus an empty
            // default recipe. Resolve the full Player-like appearance locally from that id.
            // Explicit/non-default recipes (for example Police profile 2) remain untouched.
            if (populationPresentation &&
                stableAppearanceId > 0 &&
                appearance.visualProfileId == CharacterAppearanceRecipe.DefaultVisualProfileId &&
                emptyRecipe &&
                CharacterVisualProfileRegistry.Default != null &&
                PopulationAppearanceRecipeBuilder.TryBuild(
                    CharacterVisualProfileRegistry.Default,
                    stableAppearanceId,
                    out CharacterAppearanceRecipe populationRecipe))
            {
                effective = populationRecipe;
            }
            else
            {
                effective = CharacterVisualProfileRegistry.NormalizeForPresentation(appearance);
            }

            if (!CharacterVisualProfileRegistry.TryResolve(
                    effective.visualProfileId,
                    out CharacterVisualProfile profile) ||
                profile == null ||
                profile.EditableBasePrefab == null)
            {
                return false;
            }

            ModularCharacterAppearancePresenter existing = currentVisual != null
                ? currentVisual.GetComponent<ModularCharacterAppearancePresenter>()
                : null;

            if (existing != null &&
                (existing.VisualProfileId == 0 || existing.VisualProfileId == profile.VisualProfileId))
            {
                existing.Configure(profile);
                existing.ApplyAppearance(effective);
                if (populationPresentation)
                    CompactPopulationVisual(currentVisual);
                resolvedVisual = currentVisual;
                return true;
            }

            GameObject actor = Object.Instantiate(profile.EditableBasePrefab);
            if (actor == null)
                return false;

            ModularCharacterAppearancePresenter presenter =
                actor.GetComponent<ModularCharacterAppearancePresenter>();
            if (presenter == null)
                presenter = actor.AddComponent<ModularCharacterAppearancePresenter>();

            presenter.Configure(profile);
            presenter.ApplyAppearance(effective);
            if (populationPresentation)
                CompactPopulationVisual(actor.transform);
            resolvedVisual = actor.transform;
            return true;
        }

        public bool TryApplyEquipment(
            Transform currentVisual,
            PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            if (currentVisual == null)
                return false;

            ModularCharacterAppearancePresenter presenter =
                currentVisual.GetComponent<ModularCharacterAppearancePresenter>();
            if (presenter == null)
                return false;

            CharacterVisualProfile profile = null;
            if (presenter.VisualProfileId != 0)
                CharacterVisualProfileRegistry.TryResolve(presenter.VisualProfileId, out profile);
            if (profile == null)
                profile = CharacterVisualProfileRegistry.Default;
            if (profile == null)
                return false;

            PopulationSkinnedMeshCompactor compactor =
                currentVisual.GetComponent<PopulationSkinnedMeshCompactor>();

            // Population currently carries no separate equipment appearance payload. The
            // preceding appearance pass has already produced the compacted base actor, so an
            // empty equipment pass must not expand and recombine that same actor a second time.
            // If Population gains actual equipment visuals later, the established equipment
            // path below still handles them and recompacts once.
            if (compactor != null && (equipmentVisuals == null || equipmentVisuals.Length == 0))
                return true;

            CharacterMeshSelection[] overrides =
                PlayerEquipmentVisualCatalog.ResolveMeshOverrides(profile, equipmentVisuals);
            presenter.ApplyEquipmentMeshOverrides(overrides);

            if (compactor != null)
                compactor.Rebuild();
            return true;
        }

        public bool TryResolveCombatStance(
            PlayerEquipmentVisualSelection[] equipmentVisuals,
            out HumanoidCombatStance stance)
        {
            stance = PlayerEquipmentVisualCatalog.ResolveCombatStance(equipmentVisuals);
            return true;
        }

        public bool TryResolvePopulationDisplayName(
            long actorId,
            CharacterAppearanceRecipe appearance,
            out string displayName)
        {
            displayName = string.Empty;
            if (actorId <= 0 || appearance == null)
                return false;

            CharacterAppearanceRecipe effective = appearance;
            bool emptyRecipe =
                (appearance.meshes == null || appearance.meshes.Length == 0) &&
                (appearance.morphs == null || appearance.morphs.Length == 0) &&
                (appearance.colors == null || appearance.colors.Length == 0);

            CharacterVisualProfile profile;
            if (appearance.visualProfileId == CharacterAppearanceRecipe.DefaultVisualProfileId &&
                emptyRecipe &&
                CharacterVisualProfileRegistry.Default != null &&
                PopulationAppearanceRecipeBuilder.TryBuild(
                    CharacterVisualProfileRegistry.Default,
                    actorId,
                    out CharacterAppearanceRecipe populationRecipe))
            {
                profile = CharacterVisualProfileRegistry.Default;
                effective = populationRecipe;
            }
            else
            {
                effective = CharacterVisualProfileRegistry.NormalizeForPresentation(appearance);
                if (!CharacterVisualProfileRegistry.TryResolve(effective.visualProfileId, out profile) || profile == null)
                    return false;
            }

            return PopulationAppearanceRecipeBuilder.TryResolveDisplayName(
                profile,
                actorId,
                effective,
                out displayName);
        }

        private static void CompactPopulationVisual(Transform visual)
        {
            if (visual == null)
                return;

            PopulationSkinnedMeshCompactor compactor =
                visual.GetComponent<PopulationSkinnedMeshCompactor>();
            if (compactor == null)
                compactor = visual.gameObject.AddComponent<PopulationSkinnedMeshCompactor>();

            // Failure is intentionally non-destructive: the just-applied modular Player-like
            // sources remain visible, so an unreadable/import-incompatible mesh cannot make
            // Population disappear. No broad runtime logging is added here.
            compactor.Rebuild();
        }
    }

    /// <summary>
    /// Registers the concrete Game.Client visual factory after Player.Client has reset its
    /// static registry for the current play session. No scene object or prefab wiring is
    /// required, and dedicated-server builds never execute this client presentation path.
    /// </summary>
    internal static class ModularPlayerEntityVisualFactoryBootstrap
    {
        private static readonly ModularPlayerEntityVisualFactory Factory =
            new ModularPlayerEntityVisualFactory();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Register()
        {
            PlayerEntityVisualFactoryRegistry.Register(Factory);
        }
    }
}
#endif
