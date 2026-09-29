using Player.Networking;

namespace Game.Client.UI.Standalone
{
    /// <summary>Transient local drag payload. No networking and no runtime UI construction.</summary>
    public static class StandaloneAbilityDragContext
    {
        public static bool Active { get; private set; }
        public static GameplayAbilityClientReference Ability { get; private set; }

        public static void Begin(GameplayAbilityClientReference ability)
        {
            Ability = ability;
            Active = ability.WireId != 0 && !string.IsNullOrWhiteSpace(ability.DefinitionId);
        }

        public static void End()
        {
            Active = false;
            Ability = default;
        }
    }
}
