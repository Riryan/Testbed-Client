using System;
using System.Collections.Generic;
using System.Linq;
using Game.Shared.Actors;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

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
            from.generatedLinks?.Remove(to);
            to.generatedLinks?.Remove(from);
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
            from.generatedLinks?.Remove(to);
            to.generatedLinks?.Remove(from);
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

        public static bool AssignNearbyPortalMarkersActiveScene(
            float maximumDistance,
            out int assigned,
            out int unresolved)
        {
            assigned = 0;
            unresolved = 0;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogWarning("[Population Authoring] Active scene is not loaded.");
                return false;
            }

            maximumDistance = Mathf.Clamp(maximumDistance, 1f, 50f);

            PopulationRouteMarker[] markers = Object.FindObjectsByType<PopulationRouteMarker>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None)
                .Where(x =>
                    x != null &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy)
                .ToArray();

            PopulationPortal[] portals = Object.FindObjectsByType<PopulationPortal>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None)
                .Where(x =>
                    x != null &&
                    x.gameObject.scene == scene &&
                    x.gameObject.activeInHierarchy)
                .ToArray();

            if (portals.Length == 0)
                return true;

            float maxSq = maximumDistance * maximumDistance;
            for (int p = 0; p < portals.Length; ++p)
            {
                PopulationPortal portal = portals[p];
                if (portal.routeMarker != null)
                    continue;

                PopulationRouteMarker best = null;
                float bestSq = float.PositiveInfinity;
                Vector3 from = portal.transform.position;

                for (int m = 0; m < markers.Length; ++m)
                {
                    PopulationRouteMarker marker = markers[m];
                    if (marker == null)
                        continue;

                    Vector3 delta = marker.transform.position - from;
                    float horizontalSq = delta.x * delta.x + delta.z * delta.z;
                    if (horizontalSq > maxSq || horizontalSq >= bestSq)
                        continue;

                    // Portal marker discovery is intentionally geometric only.
                    // Doorways often have a door/wall collider between the portal anchor and
                    // the sidewalk marker, so route-style NavMesh/corridor rejection is too
                    // strict here. Authoritative walkability is validated by Server World Bake.
                    best = marker;
                    bestSq = horizontalSq;
                }

                if (best == null)
                {
                    unresolved++;
                    continue;
                }

                Undo.RecordObject(portal, "Assign Population Portal Route Marker");
                portal.routeMarker = best;
                EditorUtility.SetDirty(portal);
                assigned++;
            }

            if (assigned > 0)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

            PopulationSpawnAuthoringSummary.MarkDirty();
            SceneView.RepaintAll();

            Debug.Log(
                $"[Population Authoring] Portal route-marker scan complete: assigned={assigned}, " +
                $"unresolved={unresolved}, radius={maximumDistance:0.##}m.");

            return true;
        }

        public static bool AutoConnectActiveScene(
            float maximumDistance,
            out int added,
            out int removed)
        {
            added = 0;
            removed = 0;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogWarning("[Population Authoring] Active scene is not loaded.");
                return false;
            }

            maximumDistance = Mathf.Clamp(maximumDistance, 2f, 30f);
            removed = CleanGeneratedLinks(scene);

            PopulationRouteMarker[] markers = Object.FindObjectsByType<PopulationRouteMarker>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => x.BakeId, StringComparer.Ordinal)
                .ToArray();

            if (markers.Length < 2)
                return true;

            var neighbors = BuildExistingNeighbors(markers);

            var candidatesByMarker = new Dictionary<PopulationRouteMarker, List<Candidate>>(markers.Length);
            var pairCandidates = new List<PairCandidate>(markers.Length * 3);

            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker a = markers[i];
                var list = new List<Candidate>(8);
                candidatesByMarker[a] = list;

                for (int j = i + 1; j < markers.Length; ++j)
                {
                    PopulationRouteMarker b = markers[j];
                    if (AreAlreadyNeighbors(neighbors, a, b))
                        continue;

                    Vector3 delta = b.transform.position - a.transform.position;
                    float horizontalSq = delta.x * delta.x + delta.z * delta.z;
                    if (horizontalSq > maximumDistance * maximumDistance || horizontalSq < 0.04f)
                        continue;

                    float distance = Mathf.Sqrt(horizontalSq);
                    if (!IsDirectAuthoringCorridorClear(a, b, maximumDistance))
                        continue;

                    list.Add(new Candidate(b, distance));
                    if (!candidatesByMarker.TryGetValue(b, out List<Candidate> reverse))
                    {
                        reverse = new List<Candidate>(8);
                        candidatesByMarker[b] = reverse;
                    }
                    reverse.Add(new Candidate(a, distance));
                    pairCandidates.Add(new PairCandidate(a, b, distance));
                }
            }

            foreach (List<Candidate> list in candidatesByMarker.Values)
                list.Sort((x, y) => x.Distance.CompareTo(y.Distance));
            pairCandidates.Sort((x, y) => x.Distance.CompareTo(y.Distance));

            for (int i = 0; i < pairCandidates.Count; ++i)
            {
                PairCandidate pair = pairCandidates[i];
                int maxA = MaxAutomaticDegree(pair.A);
                int maxB = MaxAutomaticDegree(pair.B);

                if (NeighborCount(neighbors, pair.A) >= maxA ||
                    NeighborCount(neighbors, pair.B) >= maxB)
                {
                    continue;
                }

                if (!IsMutualNearest(pair, candidatesByMarker, maxA, maxB))
                    continue;

                if (!DirectionAllows(pair.A, pair.B, neighbors) ||
                    !DirectionAllows(pair.B, pair.A, neighbors))
                {
                    continue;
                }

                PopulationRouteMarker owner =
                    string.CompareOrdinal(pair.A.BakeId, pair.B.BakeId) <= 0
                        ? pair.A
                        : pair.B;
                PopulationRouteMarker target = owner == pair.A ? pair.B : pair.A;

                Undo.RecordObject(owner, "Auto Connect Population Routes");
                if (!owner.links.Contains(target))
                    owner.links.Add(target);
                owner.generatedLinks ??= new List<PopulationRouteMarker>();
                if (!owner.generatedLinks.Contains(target))
                    owner.generatedLinks.Add(target);
                EditorUtility.SetDirty(owner);

                AddNeighbor(neighbors, pair.A, pair.B);
                AddNeighbor(neighbors, pair.B, pair.A);
                added++;
            }

            if (added > 0 || removed > 0)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

            PopulationSpawnAuthoringSummary.MarkDirty();
            SceneView.RepaintAll();
            Debug.Log(
                $"[Population Authoring] Local auto-connect complete: added={added}, " +
                $"replacedGenerated={removed}, radius={maximumDistance:0.##}m.");
            return true;
        }

        public static int CleanGeneratedLinksActiveScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return 0;

            int removed = CleanGeneratedLinks(scene);
            if (removed > 0)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            SceneView.RepaintAll();
            Debug.Log($"[Population Authoring] Removed {removed} generated route link(s); manual links were preserved.");
            return removed;
        }

        private readonly struct Candidate
        {
            public readonly PopulationRouteMarker Marker;
            public readonly float Distance;
            public Candidate(PopulationRouteMarker marker, float distance)
            {
                Marker = marker;
                Distance = distance;
            }
        }

        private readonly struct PairCandidate
        {
            public readonly PopulationRouteMarker A;
            public readonly PopulationRouteMarker B;
            public readonly float Distance;
            public PairCandidate(PopulationRouteMarker a, PopulationRouteMarker b, float distance)
            {
                A = a;
                B = b;
                Distance = distance;
            }
        }

        private static int CleanGeneratedLinks(Scene scene)
        {
            int removed = 0;
            PopulationRouteMarker[] markers = Object.FindObjectsByType<PopulationRouteMarker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            // Snapshot generated pairs before mutating either endpoint. A generated
            // bidirectional connection may have been serialized on both markers by an
            // older authoring pass, while ownership metadata exists on only one endpoint.
            // Cleaning a generated pair therefore removes that exact pair from BOTH
            // endpoints and clears reciprocal generated metadata.
            var pairs = new List<(PopulationRouteMarker A, PopulationRouteMarker B)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                if (marker == null ||
                    marker.gameObject.scene != scene ||
                    marker.generatedLinks == null)
                {
                    continue;
                }

                for (int g = 0; g < marker.generatedLinks.Count; ++g)
                {
                    PopulationRouteMarker target = marker.generatedLinks[g];
                    if (target == null ||
                        target == marker ||
                        target.gameObject.scene != scene)
                    {
                        continue;
                    }

                    string a = marker.BakeId ?? string.Empty;
                    string b = target.BakeId ?? string.Empty;
                    string key = string.CompareOrdinal(a, b) <= 0
                        ? a + "|" + b
                        : b + "|" + a;

                    if (seen.Add(key))
                        pairs.Add((marker, target));
                }
            }

            for (int i = 0; i < pairs.Count; ++i)
            {
                PopulationRouteMarker a = pairs[i].A;
                PopulationRouteMarker b = pairs[i].B;
                if (a == null || b == null)
                    continue;

                Undo.RecordObjects(
                    new UnityEngine.Object[] { a, b },
                    "Clean Generated Population Routes");

                bool changedA = false;
                bool changedB = false;

                if (a.links != null && a.links.Remove(b))
                {
                    removed++;
                    changedA = true;
                }

                if (b.links != null && b.links.Remove(a))
                {
                    removed++;
                    changedB = true;
                }

                if (a.generatedLinks != null && a.generatedLinks.Remove(b))
                    changedA = true;

                if (b.generatedLinks != null && b.generatedLinks.Remove(a))
                    changedB = true;

                if (changedA)
                    EditorUtility.SetDirty(a);
                if (changedB)
                    EditorUtility.SetDirty(b);
            }

            // Prune stale/null generated metadata that did not form a valid pair.
            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                if (marker == null ||
                    marker.gameObject.scene != scene ||
                    marker.generatedLinks == null)
                {
                    continue;
                }

                bool changed = false;
                for (int g = marker.generatedLinks.Count - 1; g >= 0; --g)
                {
                    PopulationRouteMarker target = marker.generatedLinks[g];
                    if (target == null ||
                        target == marker ||
                        target.gameObject.scene != scene)
                    {
                        if (!changed)
                            Undo.RecordObject(marker, "Clean Generated Population Routes");
                        marker.generatedLinks.RemoveAt(g);
                        changed = true;
                    }
                }

                if (changed)
                    EditorUtility.SetDirty(marker);
            }

            return removed;
        }

        private static Dictionary<PopulationRouteMarker, HashSet<PopulationRouteMarker>> BuildExistingNeighbors(
            PopulationRouteMarker[] markers)
        {
            var result = new Dictionary<PopulationRouteMarker, HashSet<PopulationRouteMarker>>(markers.Length);
            for (int i = 0; i < markers.Length; ++i)
                result[markers[i]] = new HashSet<PopulationRouteMarker>();

            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                AddListedNeighbors(marker, marker.links, result);
                AddListedNeighbors(marker, marker.oneWayOutboundLinks, result);
            }
            return result;
        }

        private static void AddListedNeighbors(
            PopulationRouteMarker from,
            List<PopulationRouteMarker> listed,
            Dictionary<PopulationRouteMarker, HashSet<PopulationRouteMarker>> neighbors)
        {
            if (listed == null || !neighbors.TryGetValue(from, out HashSet<PopulationRouteMarker> set))
                return;

            for (int i = 0; i < listed.Count; ++i)
            {
                PopulationRouteMarker to = listed[i];
                if (to == null || to == from || !neighbors.ContainsKey(to))
                    continue;
                set.Add(to);
                neighbors[to].Add(from);
            }
        }

        private static bool IsDirectAuthoringCorridorClear(
            PopulationRouteMarker a,
            PopulationRouteMarker b,
            float maximumDistance) =>
            IsDirectAuthoringCorridorClear(
                a.transform.position,
                b.transform.position,
                maximumDistance,
                a.transform,
                b.transform);

        private static bool IsDirectAuthoringCorridorClear(
            Vector3 from,
            Vector3 to,
            float maximumDistance,
            Transform sourceTransform,
            Transform targetTransform)
        {
            if (!NavMesh.SamplePosition(from, out NavMeshHit fromHit, 1.25f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(to, out NavMeshHit toHit, 1.25f, NavMesh.AllAreas))
            {
                return false;
            }

            Vector3 navDelta = toHit.position - fromHit.position;
            navDelta.y = 0f;
            if (navDelta.sqrMagnitude > maximumDistance * maximumDistance)
                return false;

            if (NavMesh.Raycast(fromHit.position, toHit.position, out _, NavMesh.AllAreas))
                return false;

            // NavMesh owns walk support. A torso-height physics line adds a cheap scene-collider
            // obstruction check without treating the floor/stair tread itself as a blocker.
            Vector3 rayFrom = fromHit.position + Vector3.up * 1.2f;
            Vector3 rayTo = toHit.position + Vector3.up * 1.2f;
            if (Physics.Linecast(rayFrom, rayTo, out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore))
            {
                Transform hitTransform = hit.collider != null ? hit.collider.transform : null;
                if (hitTransform != null &&
                    hitTransform != sourceTransform &&
                    hitTransform != targetTransform &&
                    !hitTransform.IsChildOf(sourceTransform) &&
                    !hitTransform.IsChildOf(targetTransform))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsMutualNearest(
            PairCandidate pair,
            Dictionary<PopulationRouteMarker, List<Candidate>> candidates,
            int maxA,
            int maxB)
        {
            return CandidateRank(pair.A, pair.B, candidates) < maxA &&
                   CandidateRank(pair.B, pair.A, candidates) < maxB;
        }

        private static int CandidateRank(
            PopulationRouteMarker from,
            PopulationRouteMarker target,
            Dictionary<PopulationRouteMarker, List<Candidate>> candidates)
        {
            if (!candidates.TryGetValue(from, out List<Candidate> list))
                return int.MaxValue;

            for (int i = 0; i < list.Count; ++i)
                if (list[i].Marker == target)
                    return i;
            return int.MaxValue;
        }

        private static bool DirectionAllows(
            PopulationRouteMarker from,
            PopulationRouteMarker to,
            Dictionary<PopulationRouteMarker, HashSet<PopulationRouteMarker>> neighbors)
        {
            if (!neighbors.TryGetValue(from, out HashSet<PopulationRouteMarker> existing) ||
                existing.Count == 0)
            {
                return true;
            }

            Vector3 newDirection = to.transform.position - from.transform.position;
            newDirection.y = 0f;
            if (newDirection.sqrMagnitude <= 0.0001f)
                return false;
            newDirection.Normalize();

            float minimumAngle = from.branchNode
                ? 35f
                : 100f;

            foreach (PopulationRouteMarker neighbor in existing)
            {
                if (neighbor == null)
                    continue;
                Vector3 direction = neighbor.transform.position - from.transform.position;
                direction.y = 0f;
                if (direction.sqrMagnitude <= 0.0001f)
                    continue;
                direction.Normalize();

                if (Vector3.Angle(newDirection, direction) < minimumAngle)
                    return false;
            }

            return true;
        }

        private static int MaxAutomaticDegree(PopulationRouteMarker marker) =>
            marker != null && marker.branchNode
                ? 4
                : 2;

        private static int NeighborCount(
            Dictionary<PopulationRouteMarker, HashSet<PopulationRouteMarker>> neighbors,
            PopulationRouteMarker marker) =>
            neighbors.TryGetValue(marker, out HashSet<PopulationRouteMarker> set) ? set.Count : 0;

        private static bool AreAlreadyNeighbors(
            Dictionary<PopulationRouteMarker, HashSet<PopulationRouteMarker>> neighbors,
            PopulationRouteMarker a,
            PopulationRouteMarker b) =>
            neighbors.TryGetValue(a, out HashSet<PopulationRouteMarker> set) && set.Contains(b);

        private static void AddNeighbor(
            Dictionary<PopulationRouteMarker, HashSet<PopulationRouteMarker>> neighbors,
            PopulationRouteMarker from,
            PopulationRouteMarker to)
        {
            if (from == null || to == null)
                return;
            if (!neighbors.TryGetValue(from, out HashSet<PopulationRouteMarker> set))
            {
                set = new HashSet<PopulationRouteMarker>();
                neighbors.Add(from, set);
            }
            set.Add(to);
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
