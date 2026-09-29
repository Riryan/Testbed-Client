using Game.Shared.Characters;
using Game.Shared.Combat;
using Player.Networking;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-side presentation bridge owned by Player.Client.
    ///
    /// PlayerEntityClient owns the presentation lifecycle, but the concrete humanoid
    /// implementation lives in Game.Client. This interface keeps Player.Client from
    /// taking a dependency back on Game.Client while still allowing the canonical
    /// PlayerEntity path to construct visuals from compact authoritative appearance state.
    /// </summary>
    public interface IPlayerEntityVisualFactory
    {
        /// <summary>
        /// Creates a replacement presentation root or updates the existing root in place.
        /// Returns false when the requested visual profile cannot be resolved locally.
        /// Implementations must not mutate the supplied appearance recipe.
        /// </summary>
        bool TryCreateOrUpdate(
            Transform currentVisual,
            CharacterAppearanceRecipe appearance,
            long stableAppearanceId,
            bool populationPresentation,
            out Transform resolvedVisual);

        /// <summary>
        /// Applies rare authoritative equipment presentation ids to an already-created
        /// visual. Implementations resolve all Unity meshes/prefabs locally.
        /// </summary>
        bool TryApplyEquipment(
            Transform currentVisual,
            PlayerEquipmentVisualSelection[] equipmentVisuals);

        /// <summary>
        /// Resolves the local combat stance family from existing compact equipment
        /// presentation ids. This is presentation-only and adds no network fields.
        /// </summary>
        bool TryResolveCombatStance(
            PlayerEquipmentVisualSelection[] equipmentVisuals,
            out HumanoidCombatStance stance);

        /// <summary>
        /// Resolves a client-local display name for an ambient Population actor from the
        /// already-known stable actor id and local appearance catalog. This must not perform
        /// network or persistence work.
        /// </summary>
        bool TryResolvePopulationDisplayName(long actorId, CharacterAppearanceRecipe appearance, out string displayName);
    }

    public static class PlayerEntityVisualFactoryRegistry
    {
        private static IPlayerEntityVisualFactory _factory;

        public static void Register(IPlayerEntityVisualFactory factory)
        {
            _factory = factory;
        }

        public static void Unregister(IPlayerEntityVisualFactory factory)
        {
            if (object.ReferenceEquals(_factory, factory))
                _factory = null;
        }

        public static bool TryCreateOrUpdate(
            Transform currentVisual,
            CharacterAppearanceRecipe appearance,
            long stableAppearanceId,
            bool populationPresentation,
            out Transform resolvedVisual)
        {
            resolvedVisual = currentVisual;
            return _factory != null &&
                   _factory.TryCreateOrUpdate(
                       currentVisual,
                       appearance,
                       stableAppearanceId,
                       populationPresentation,
                       out resolvedVisual) &&
                   resolvedVisual != null;
        }

        public static bool TryApplyEquipment(
            Transform currentVisual,
            PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            return _factory != null &&
                   currentVisual != null &&
                   _factory.TryApplyEquipment(
                       currentVisual,
                       equipmentVisuals ?? System.Array.Empty<PlayerEquipmentVisualSelection>());
        }

        public static bool TryResolveCombatStance(
            PlayerEquipmentVisualSelection[] equipmentVisuals,
            out HumanoidCombatStance stance)
        {
            stance = HumanoidCombatStance.None;
            return _factory != null &&
                   _factory.TryResolveCombatStance(
                       equipmentVisuals ?? System.Array.Empty<PlayerEquipmentVisualSelection>(),
                       out stance);
        }

        public static bool TryResolvePopulationDisplayName(long actorId, CharacterAppearanceRecipe appearance, out string displayName)
        {
            displayName = string.Empty;
            return actorId > 0 &&
                   _factory != null &&
                   _factory.TryResolvePopulationDisplayName(actorId, appearance, out displayName) &&
                   !string.IsNullOrWhiteSpace(displayName);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession()
        {
            _factory = null;
        }
    }
}
