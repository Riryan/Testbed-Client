using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiteNetLibManager
{
    public enum CoreReplicationPriority : byte
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3,
    }

    [Serializable]
    public sealed class CoreReplicationLodTier
    {
        [Min(0f)] public float maximumDistance = 15f;
        [Min(0.1f)] public float updatesPerSecond = 20f;
    }

    [Serializable]
    public sealed class CoreReplicationLodSettings
    {
        [Tooltip("Policy framework only. Actual gameplay replication decides which state is safe to throttle.")]
        public List<CoreReplicationLodTier> tiers = new List<CoreReplicationLodTier>
        {
            new CoreReplicationLodTier { maximumDistance = 15f, updatesPerSecond = 20f },
            new CoreReplicationLodTier { maximumDistance = 40f, updatesPerSecond = 10f },
            new CoreReplicationLodTier { maximumDistance = 80f, updatesPerSecond = 5f },
            new CoreReplicationLodTier { maximumDistance = 160f, updatesPerSecond = 2f },
        };
        [Min(0.1f)] public float beyondLastTierUpdatesPerSecond = 1f;
        [Min(1f)] public float criticalPriorityMultiplier = 4f;
        [Min(1f)] public float highPriorityMultiplier = 2f;
        [Range(0.1f, 1f)] public float lowPriorityMultiplier = 0.5f;

        internal void ClampUnsafeValues()
        {
            tiers ??= new List<CoreReplicationLodTier>();
            tiers.Sort((a, b) => (a?.maximumDistance ?? float.MaxValue).CompareTo(b?.maximumDistance ?? float.MaxValue));
            float previous = 0f;
            for (int i = 0; i < tiers.Count; ++i)
            {
                CoreReplicationLodTier tier = tiers[i];
                if (tier == null)
                    continue;
                tier.maximumDistance = Math.Max(previous, tier.maximumDistance);
                tier.updatesPerSecond = Math.Max(0.1f, tier.updatesPerSecond);
                previous = tier.maximumDistance;
            }
            beyondLastTierUpdatesPerSecond = Math.Max(0.1f, beyondLastTierUpdatesPerSecond);
            criticalPriorityMultiplier = Math.Max(1f, criticalPriorityMultiplier);
            highPriorityMultiplier = Math.Max(1f, highPriorityMultiplier);
            lowPriorityMultiplier = Mathf.Clamp(lowPriorityMultiplier, 0.1f, 1f);
        }
    }

    public sealed class CoreReplicationLodPolicy
    {
        private readonly CoreReplicationLodSettings _settings;

        public CoreReplicationLodPolicy(CoreReplicationLodSettings settings)
        {
            _settings = settings ?? new CoreReplicationLodSettings();
            _settings.ClampUnsafeValues();
        }

        public float GetUpdatesPerSecond(float distance, CoreReplicationPriority priority)
        {
            float hz = _settings.beyondLastTierUpdatesPerSecond;
            for (int i = 0; i < _settings.tiers.Count; ++i)
            {
                CoreReplicationLodTier tier = _settings.tiers[i];
                if (tier != null && distance <= tier.maximumDistance)
                {
                    hz = tier.updatesPerSecond;
                    break;
                }
            }

            switch (priority)
            {
                case CoreReplicationPriority.Critical:
                    hz *= _settings.criticalPriorityMultiplier;
                    break;
                case CoreReplicationPriority.High:
                    hz *= _settings.highPriorityMultiplier;
                    break;
                case CoreReplicationPriority.Low:
                    hz *= _settings.lowPriorityMultiplier;
                    break;
            }
            return Math.Max(0.1f, hz);
        }

        public double GetIntervalSeconds(float distance, CoreReplicationPriority priority)
        {
            return 1.0 / GetUpdatesPerSecond(distance, priority);
        }
    }

    public interface ICoreReplicationPriorityProvider
    {
        CoreReplicationPriority ReplicationPriority { get; }
    }
}
