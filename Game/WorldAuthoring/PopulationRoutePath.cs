using System.Collections.Generic;
using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// Optional authoring helper. Direct child Route Markers are connected in sibling order
    /// during ServerWorldBake. This keeps long road/sidewalk routes fast to author without
    /// creating a runtime route component on the standalone server.
    /// </summary>
    [AddComponentMenu("MMO/Population/Route Path")]
    [DisallowMultipleComponent]
    public sealed class PopulationRoutePath : MonoBehaviour
    {
        public bool connectChildrenInOrder = true;
        public bool loop;
        [Tooltip("When enabled, child order is FROM -> TO only. Otherwise each segment is bidirectional.")]
        public bool oneWay;
        [Min(0.5f)] public float width = 2.5f;
        [Min(0.01f)] public float weight = 1f;
        [Min(0f)] public float minimumClearance = 0.5f;

        public void CollectDirectMarkers(List<PopulationRouteMarker> destination)
        {
            destination.Clear();
            for (int i = 0; i < transform.childCount; ++i)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child.TryGetComponent(out PopulationRouteMarker marker))
                    destination.Add(marker);
            }
        }

        private void OnValidate()
        {
            width = Mathf.Max(0.5f, width);
            weight = Mathf.Max(0.01f, weight);
            minimumClearance = Mathf.Max(0f, minimumClearance);
        }

        private void OnDrawGizmos()
        {
            if (!connectChildrenInOrder)
                return;

            PopulationRouteMarker previous = null;
            PopulationRouteMarker first = null;
            for (int i = 0; i < transform.childCount; ++i)
            {
                Transform child = transform.GetChild(i);
                if (child == null || !child.TryGetComponent(out PopulationRouteMarker current))
                    continue;
                if (first == null)
                    first = current;
                if (previous != null)
                {
                    Gizmos.color = oneWay
                        ? new Color(1f, 0.5f, 0.05f, 0.55f)
                        : new Color(0.1f, 0.75f, 1f, 0.55f);
                    Gizmos.DrawLine(previous.transform.position, current.transform.position);
                }
                previous = current;
            }

            if (loop && first != null && previous != null && first != previous)
            {
                Gizmos.color = oneWay
                    ? new Color(1f, 0.5f, 0.05f, 0.55f)
                    : new Color(0.1f, 0.75f, 1f, 0.55f);
                Gizmos.DrawLine(previous.transform.position, first.transform.position);
            }
        }
    }
}
