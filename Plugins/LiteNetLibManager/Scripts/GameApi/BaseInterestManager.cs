using System.Collections.Generic;
using UnityEngine;

namespace LiteNetLibManager
{
    [DisallowMultipleComponent]
    public abstract class BaseInterestManager : MonoBehaviour
    {
        [Tooltip("Default visible range will be used when Identity's visible range is <= 0f")]
        public float defaultVisibleRange = 80f;
        public LiteNetLibGameManager Manager { get; protected set; }
        public bool IsServer { get { return Manager != null && Manager.IsServer; } }

        public virtual void Setup(LiteNetLibGameManager manager)
        {
            Manager = manager;
        }

        public abstract void UpdateInterestManagementImmediate();
        public abstract void UpdateInterestManagement(float deltaTime);

        /// <summary>
        /// Reset all runtime AOI state. Called when the asset/world state is cleared.
        /// Implementations must keep this bounded and allocation-conscious.
        /// </summary>
        public virtual void ResetState() { }

        /// <summary>
        /// Notification for a newly spawned network object. The default manager keeps
        /// the legacy immediate behavior; scalable managers should override this and
        /// queue/index work instead of scanning the whole world here.
        /// </summary>
        public virtual void NotifyNewObject(LiteNetLibIdentity newObject)
        {
            if (!IsServer || newObject == null)
                return;

            // Subscribe old objects, if it should
            if (newObject.ConnectionId >= 0 && newObject.Player != null && newObject.Player.IsReady)
            {
                foreach (KeyValuePair<uint, LiteNetLibIdentity> spawnedObjKvp in Manager.Assets.SpawnedObjects)
                    Subscribe(newObject, spawnedObjKvp.Value);
            }

            // Notify to other players
            foreach (KeyValuePair<long, LiteNetLibPlayer> playerKvp in Manager.Players)
            {
                LiteNetLibPlayer player = playerKvp.Value;
                if (!player.IsReady || player.ConnectionId == newObject.ConnectionId)
                    continue;

                foreach (KeyValuePair<uint, LiteNetLibIdentity> playerObjKvp in player.SpawnedObjects)
                    Subscribe(playerObjKvp.Value, newObject);
            }
        }

        /// <summary>
        /// Notification before an object leaves the spawned-object registry.
        /// </summary>
        public virtual void NotifyObjectDestroyed(LiteNetLibIdentity destroyedObject) { }

        /// <summary>
        /// Notification after ownership changes. Scalable AOI implementations use this
        /// to maintain their observer-anchor index without scanning all player objects.
        /// </summary>
        public virtual void NotifyObjectOwnerChanged(LiteNetLibIdentity identity, long oldConnectionId, long newConnectionId) { }

        public float GetVisibleRange(LiteNetLibIdentity identity)
        {
            return identity.AlwaysVisible ? float.MaxValue : (identity.VisibleRange > 0f ? identity.VisibleRange : defaultVisibleRange);
        }

        public virtual bool ShouldSubscribe(LiteNetLibIdentity subscriber, LiteNetLibIdentity target, bool checkRange = true)
        {
            if (subscriber == null || target == null || subscriber.ConnectionId < 0)
                return false;
            if (subscriber.ConnectionId == target.ConnectionId)
                return true;

            if (target.IsHideFrom(subscriber))
                return false;

            if (!checkRange || target.AlwaysVisible)
                return true;

            float range = GetVisibleRange(target);
            Vector3 delta = subscriber.transform.position - target.transform.position;
            return delta.sqrMagnitude <= range * range;
        }

        public virtual bool Subscribe(LiteNetLibIdentity subscriber, LiteNetLibIdentity target)
        {
            if (ShouldSubscribe(subscriber, target))
            {
                subscriber.AddSubscribing(target.ObjectId);
                return true;
            }
            return false;
        }
    }
}
