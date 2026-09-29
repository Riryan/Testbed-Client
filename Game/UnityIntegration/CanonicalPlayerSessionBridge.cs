using System;
using Game.Server.Application.Sessions;
using Game.Server.Application.World;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.World;
using LiteNetLibManager;
using UnityEngine;

namespace Game.UnityIntegration
{
    /// <summary>
    /// Thin boundary between the portable character/session runtime and
    /// LiteNetLibGameManager's canonical Ready/SpawnPlayer lifecycle.
    ///
    /// This class never spawns or destroys a player. It only adopts an identity
    /// already created by LiteNetLibGameManager and captures final authoritative
    /// location before canonical cleanup.
    /// </summary>
    public sealed class CanonicalPlayerSessionBridge
    {
        private readonly LiteNetLibGameManager _manager;
        private readonly PlayerSessionService _sessions;
        private readonly PlayerWorldLifecycleService _worldLifecycle;
        private readonly PlayerWorldBindingRegistry _bindings;

        public CanonicalPlayerSessionBridge(
            LiteNetLibGameManager manager,
            PlayerSessionService sessions,
            PlayerWorldLifecycleService worldLifecycle,
            PlayerWorldBindingRegistry bindings)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _worldLifecycle = worldLifecycle ?? throw new ArgumentNullException(nameof(worldLifecycle));
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        }

        public bool TryAdoptSpawnedPlayer(
            PlayerSessionHandle handle,
            LiteNetLibIdentity playerIdentity,
            out PlayerWorldLifecycleResult result)
        {
            result = default(PlayerWorldLifecycleResult);

            if (!_manager.IsServer || !handle.IsValid || playerIdentity == null)
                return false;

            if (playerIdentity.ObjectId == 0 ||
                playerIdentity.ConnectionId != handle.Connection.Value)
            {
                return false;
            }

            if (!_sessions.TryGetSession(handle, out PlayerSession session) ||
                !session.TryGetAwaitingWorldEntryRuntime(out PlayerRuntime runtime))
            {
                return false;
            }

            ApplyRuntimeLocation(playerIdentity.transform, runtime);

            result = _worldLifecycle.AdoptExternalEnter(
                handle,
                new PlayerWorldHandle(playerIdentity.ObjectId));

            return result.Success;
        }

        /// <summary>
        /// Captures final authoritative transform state and removes only the portable
        /// application binding. Call before LiteNetLib performs canonical player
        /// destruction on NotReady/disconnect.
        /// </summary>
        public bool TryCompleteLeaveBeforeCanonicalDestroy(
            PlayerSessionHandle handle,
            out PlayerWorldLifecycleResult result)
        {
            result = default(PlayerWorldLifecycleResult);

            if (!_manager.IsServer || !handle.IsValid)
                return false;

            if (!_sessions.TryGetSession(handle, out PlayerSession session) ||
                session.Runtime == null ||
                !_bindings.TryGet(handle, out PlayerWorldBinding binding))
            {
                return false;
            }

            if (binding.Handle.Value > uint.MaxValue)
                return false;

            CharacterLocationState finalLocation = session.Runtime.Location;
            uint objectId = (uint)binding.Handle.Value;

            if (_manager.Assets != null &&
                _manager.Assets.TryGetSpawnedObject(objectId, out LiteNetLibIdentity identity) &&
                identity != null &&
                identity.ConnectionId == handle.Connection.Value)
            {
                finalLocation = CaptureLocation(identity.transform, session.Runtime.Location);
            }

            result = _worldLifecycle.CompleteExternalLeave(handle, finalLocation);
            return result.Success;
        }

        /// <summary>
        /// Samples the canonical LiteNetLib-owned PlayerEntity transform into the
        /// portable runtime without changing network ownership. Periodic persistence
        /// calls this before building location checkpoint batches.
        /// </summary>
        public bool TryCaptureAuthoritativeLocation(PlayerSessionHandle handle)
        {
            if (!_manager.IsServer || !handle.IsValid ||
                !_sessions.TryGetSession(handle, out PlayerSession session) ||
                session.State != Game.Shared.Sessions.PlayerSessionState.InWorld ||
                session.Runtime == null ||
                !_bindings.TryGet(handle, out PlayerWorldBinding binding) ||
                binding.Handle.Value > uint.MaxValue)
            {
                return false;
            }

            uint objectId = (uint)binding.Handle.Value;
            if (_manager.Assets == null ||
                !_manager.Assets.TryGetSpawnedObject(objectId, out LiteNetLibIdentity identity) ||
                identity == null ||
                identity.ConnectionId != handle.Connection.Value)
            {
                return false;
            }

            CharacterLocationState location = CaptureLocation(
                identity.transform,
                session.Runtime.Location);
            session.Runtime.UpdateLocation(location);
            return true;
        }

        public bool TryGetRuntime(PlayerSessionHandle handle, out PlayerRuntime runtime)
        {
            runtime = null;
            if (!_sessions.TryGetSession(handle, out PlayerSession session))
                return false;

            runtime = session.Runtime;
            return runtime != null;
        }

        private static void ApplyRuntimeLocation(Transform target, PlayerRuntime runtime)
        {
            if (target == null || runtime == null)
                return;

            WorldPosition p = runtime.Location.Position;
            target.SetPositionAndRotation(
                new Vector3(p.X, p.Y, p.Z),
                Quaternion.Euler(0f, runtime.Location.YawDegrees, 0f));
        }

        private static CharacterLocationState CaptureLocation(
            Transform source,
            CharacterLocationState previous)
        {
            Vector3 p = source.position;
            float yaw = source.eulerAngles.y;
            return new CharacterLocationState(
                previous.MapId,
                previous.InstanceId,
                new WorldPosition(p.x, p.y, p.z),
                yaw);
        }
    }
}
