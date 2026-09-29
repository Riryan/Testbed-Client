using Game.Server.Application.Connections;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;

namespace Game.UnityIntegration
{
    /// <summary>
    /// Deliberately non-spawning adapter used while LiteNetLibGameManager owns the
    /// canonical player object lifecycle. External adoption/removal APIs on
    /// PlayerWorldLifecycleService do not call this adapter.
    /// </summary>
    internal sealed class ExternalHostPlayerWorldAdapter : IPlayerWorldAdapter
    {
        public bool TryEnter(PlayerRuntime runtime, ConnectionKey connection, out PlayerWorldHandle handle)
        {
            handle = default(PlayerWorldHandle);
            return false;
        }

        public bool TryReadLocation(PlayerWorldHandle handle, out CharacterLocationState location)
        {
            location = default(CharacterLocationState);
            return false;
        }

        public bool TryLeave(PlayerWorldHandle handle)
        {
            return false;
        }
    }
}
