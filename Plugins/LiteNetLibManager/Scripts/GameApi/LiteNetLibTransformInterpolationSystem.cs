using System.Collections.Generic;
using UnityEngine;

namespace LiteNetLibManager
{
    internal static class LiteNetLibTransformInterpolationSystem
    {
        private static readonly List<LiteNetLibTransform> Transforms = new List<LiteNetLibTransform>();
        private static readonly Dictionary<LiteNetLibTransform, int> Indices = new Dictionary<LiteNetLibTransform, int>(ReferenceEqualityComparer<LiteNetLibTransform>.Instance);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Transforms.Clear();
            Indices.Clear();
        }

        internal static void Register(LiteNetLibTransform transform)
        {
            if (ReferenceEquals(transform, null) || Indices.ContainsKey(transform))
                return;
            Indices[transform] = Transforms.Count;
            Transforms.Add(transform);
        }

        internal static void Unregister(LiteNetLibTransform transform)
        {
            if (ReferenceEquals(transform, null) || !Indices.TryGetValue(transform, out int index))
                return;
            RemoveAt(index);
        }

        private static void RemoveAt(int index)
        {
            int lastIndex = Transforms.Count - 1;
            LiteNetLibTransform removed = Transforms[index];
            LiteNetLibTransform last = Transforms[lastIndex];
            if (index != lastIndex)
            {
                Transforms[index] = last;
                if (!ReferenceEquals(last, null))
                    Indices[last] = index;
            }
            Transforms.RemoveAt(lastIndex);
            if (!ReferenceEquals(removed, null))
                Indices.Remove(removed);
        }

        internal static void UpdateAll(LiteNetLibGameManager manager)
        {
            for (int i = 0; i < Transforms.Count; ++i)
            {
                LiteNetLibTransform transform = Transforms[i];
                if (ReferenceEquals(transform, null) || transform == null)
                {
                    RemoveAt(i);
                    --i;
                    continue;
                }
                if (ReferenceEquals(transform.Manager, manager))
                    transform.UpdateInterpolation();
            }
        }
    }
}
