using System.Collections.Generic;
using Game.Shared.Actors;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    public static class PopulationRouteAuthoringTools
    {
        [MenuItem("MMO Tools/Population/Create Route Path")]
        private static void CreateRoutePath()
        {
            Transform parent = Selection.activeTransform;
            var go = new GameObject("Population Route Path");
            Undo.RegisterCreatedObjectUndo(go, "Create Population Route Path");
            if (parent != null)
                Undo.SetTransformParent(go.transform, parent, "Parent Population Route Path");
            go.AddComponent<PopulationRoutePath>();
            Selection.activeGameObject = go;
        }

        [MenuItem("MMO Tools/Population/Add Route Marker")]
        private static void AddRouteMarker()
        {
            PopulationRoutePath path = ResolveSelectedPath();
            if (path == null)
            {
                Debug.LogWarning("[Population Authoring] Select a Population Route Path or one of its child markers first.");
                return;
            }

            var go = new GameObject($"Route Marker {path.transform.childCount + 1:000}");
            Undo.RegisterCreatedObjectUndo(go, "Add Population Route Marker");
            Undo.SetTransformParent(go.transform, path.transform, "Parent Population Route Marker");

            Vector3 position = path.transform.position;
            Quaternion rotation = path.transform.rotation;
            PopulationRouteMarker last = LastDirectMarker(path.transform);
            if (last != null)
            {
                position = last.transform.position + last.transform.forward * 4f;
                rotation = last.transform.rotation;
            }
            else if (SceneView.lastActiveSceneView != null)
            {
                position = SceneView.lastActiveSceneView.pivot;
            }

            go.transform.SetPositionAndRotation(position, rotation);
            go.AddComponent<PopulationRouteMarker>();
            Selection.activeGameObject = go;
        }

        [MenuItem("MMO Tools/Population/Connect Selected Markers/Bidirectional")]
        private static void ConnectSelectedBidirectional()
        {
            if (!TrySelectedPair(out PopulationRouteMarker from, out PopulationRouteMarker to))
                return;

            Undo.RecordObject(from, "Connect Population Markers");
            Undo.RecordObject(to, "Connect Population Markers");
            from.oneWayOutboundLinks.Remove(to);
            to.oneWayOutboundLinks.Remove(from);
            if (!from.links.Contains(to) && !to.links.Contains(from))
                from.links.Add(to);
            EditorUtility.SetDirty(from);
            EditorUtility.SetDirty(to);
        }

        [MenuItem("MMO Tools/Population/Connect Selected Markers/One Way - Active To Other")]
        private static void ConnectSelectedOneWay()
        {
            if (!TrySelectedPair(out PopulationRouteMarker from, out PopulationRouteMarker to))
                return;

            Undo.RecordObject(from, "Connect Population Markers One Way");
            Undo.RecordObject(to, "Connect Population Markers One Way");
            from.links.Remove(to);
            to.links.Remove(from);
            to.oneWayOutboundLinks.Remove(from);
            if (!from.oneWayOutboundLinks.Contains(to))
                from.oneWayOutboundLinks.Add(to);
            EditorUtility.SetDirty(from);
            EditorUtility.SetDirty(to);
        }

        [MenuItem("MMO Tools/Population/Create Portal At Selected Marker")]
        private static void CreatePortalAtSelectedMarker()
        {
            PopulationRouteMarker marker = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<PopulationRouteMarker>()
                : null;
            if (marker == null)
            {
                Debug.LogWarning("[Population Authoring] Select a Population Route Marker first.");
                return;
            }

            var root = new GameObject($"Population Portal - {marker.name}");
            Undo.RegisterCreatedObjectUndo(root, "Create Population Portal");
            root.transform.SetPositionAndRotation(marker.transform.position, marker.transform.rotation);
            if (marker.transform.parent != null)
                Undo.SetTransformParent(root.transform, marker.transform.parent.parent, "Parent Population Portal");

            PopulationPortal portal = root.AddComponent<PopulationPortal>();
            portal.routeMarker = marker;
            portal.exterior = CreatePoint(root.transform, "Exterior", 0f);
            portal.threshold = CreatePoint(root.transform, "Threshold", -0.4f);
            portal.interaction = CreatePoint(root.transform, "Interaction", -0.8f);
            portal.approach = CreatePoint(root.transform, "Approach", -1.3f);
            portal.interiorSpawn = CreatePoint(root.transform, "Interior Spawn", -1.8f);
            Selection.activeGameObject = root;
        }

        [MenuItem("MMO Tools/Population/Create Spawn For Selected Portal")]
        private static void CreateSpawnForSelectedPortal()
        {
            PopulationPortal portal = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<PopulationPortal>()
                : null;
            if (portal == null)
            {
                Debug.LogWarning("[Population Authoring] Select a Population Portal first.");
                return;
            }

            var go = new GameObject($"Population Spawn - {portal.name}");
            Undo.RegisterCreatedObjectUndo(go, "Create Population Spawn");
            go.transform.SetPositionAndRotation(portal.EffectiveExterior.position, portal.EffectiveExterior.rotation);
            if (portal.transform.parent != null)
                Undo.SetTransformParent(go.transform, portal.transform.parent, "Parent Population Spawn");

            ServerSpawn spawn = go.AddComponent<ServerSpawn>();
            spawn.label = go.name;
            spawn.kind = ServerSpawnKind.Population;
            spawn.actorKind = AuthoritativeActorKind.Population;
            spawn.routeMarker = portal.routeMarker;
            spawn.populationPortal = portal;
            Selection.activeGameObject = go;
        }

        private static PopulationRoutePath ResolveSelectedPath()
        {
            Transform selected = Selection.activeTransform;
            if (selected == null)
                return null;
            if (selected.TryGetComponent(out PopulationRoutePath direct))
                return direct;
            return selected.GetComponentInParent<PopulationRoutePath>();
        }

        private static PopulationRouteMarker LastDirectMarker(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; --i)
            {
                Transform child = parent.GetChild(i);
                if (child != null && child.TryGetComponent(out PopulationRouteMarker marker))
                    return marker;
            }
            return null;
        }

        private static bool TrySelectedPair(out PopulationRouteMarker from, out PopulationRouteMarker to)
        {
            from = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<PopulationRouteMarker>()
                : null;
            to = null;
            if (from == null)
            {
                Debug.LogWarning("[Population Authoring] Active selection must be a Population Route Marker.");
                return false;
            }

            GameObject[] selected = Selection.gameObjects;
            for (int i = 0; i < selected.Length; ++i)
            {
                if (selected[i] == null || selected[i] == from.gameObject)
                    continue;
                PopulationRouteMarker candidate = selected[i].GetComponent<PopulationRouteMarker>();
                if (candidate != null)
                {
                    if (to != null)
                    {
                        Debug.LogWarning("[Population Authoring] Select exactly two Population Route Markers.");
                        return false;
                    }
                    to = candidate;
                }
            }

            if (to == null)
            {
                Debug.LogWarning("[Population Authoring] Select exactly two Population Route Markers.");
                return false;
            }
            return true;
        }

        private static Transform CreatePoint(Transform parent, string name, float localZ)
        {
            var point = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(point, $"Create {name}");
            Undo.SetTransformParent(point.transform, parent, $"Parent {name}");
            point.transform.localPosition = new Vector3(0f, 0f, localZ);
            point.transform.localRotation = Quaternion.identity;
            return point.transform;
        }
    }
}
