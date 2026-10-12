using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// Optional authored route guidance. Child order is used only when connectChildrenInOrder
    /// is enabled. Scene-level blocked/forced pair overrides let designers customize the
    /// automatically generated graph without changing links on prefab instances.
    /// </summary>
    [AddComponentMenu("MMO/Population/Route Path")]
    [DisallowMultipleComponent]
    public sealed class PopulationRoutePath : MonoBehaviour
    {
        [Serializable]
        public sealed class RoutePair
        {
            public PopulationRouteMarker from;
            public PopulationRouteMarker to;
        }

        public bool connectChildrenInOrder = true;
        public bool loop;
        [Tooltip("When enabled, child order is FROM -> TO only. Otherwise each segment is bidirectional.")]
        public bool oneWay;
        [Min(0.5f)] public float width = 2.5f;
        [Min(0.01f)] public float weight = 1f;
        [Min(0f)] public float minimumClearance = 0.5f;

        [Header("Scene-wide Graph Edits")]
        [Tooltip("These pairs will never be automatically or manually linked by the baked graph. Stored on the Route Path, not on prefab marker instances.")]
        public List<RoutePair> blockedConnections = new List<RoutePair>();
        [Tooltip("Explicit bidirectional links, tried before automatic graph building. Each segment must still pass authoritative walkability validation.")]
        public List<RoutePair> forcedConnections = new List<RoutePair>();
        [Tooltip("Markers cleared for manual routing. They ignore ALL automatically inferred and legacy authored edges until explicitly connected with Connect Selected Nodes. Restore Selected Node Auto re-enables the ordinary graph rules.")]
        public List<PopulationRouteMarker> manualOnlyMarkers = new List<PopulationRouteMarker>();

        public void CollectDirectMarkers(List<PopulationRouteMarker> destination)
        {
            destination.Clear();
            for (int i = 0; i < transform.childCount; ++i)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child.gameObject.activeInHierarchy &&
                    child.TryGetComponent(out PopulationRouteMarker marker))
                    destination.Add(marker);
            }
        }

        private void OnValidate()
        {
            width = Mathf.Max(0.5f, width);
            weight = Mathf.Max(0.01f, weight);
            minimumClearance = Mathf.Max(0f, minimumClearance);
            blockedConnections ??= new List<RoutePair>();
            forcedConnections ??= new List<RoutePair>();
            manualOnlyMarkers ??= new List<PopulationRouteMarker>();
        }

        private void OnDrawGizmos()
        {
            // Editor-only graph preview owns connection rendering.
        }
    }
}
