using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Server.Application.World;
using Game.Shared.Population;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    // The graph is derived from scene marker transforms, not serialized back into prefab instances.
    // One builder is used by the preview button and by the authoritative World Bake export.
    internal static class PopulationRouteGraphBuilder
    {
        private const float GridSize = 16f;
        // Auto-graph connections are local waypoint continuations, never long shortcuts
        // invented solely to make the world graph a single connected component.
        private const float AutomaticRadius = 28f;
        // Local-neighbor spacing protects ordinary links, but recovery must not be
        // blocked by a misleading very-close prefab marker on the same corner.
        private const float MinimumAutoAllowance = 12f;
        private const float LocalDistanceMultiplier = 2.75f;
        private const float NearbyWaypointTolerance = 2f;
        private const float MinimumSegment = 0.08f;
        private const int CandidatesPerMarker = 32;
        private const float CosMinimumAngle = 0.65f; // ~49 degrees between automated outgoing directions

        private readonly struct Candidate
        {
            public readonly int A;
            public readonly int B;
            public readonly float DistanceSquared;
            public Candidate(int a, int b, float squared) { A = a; B = b; DistanceSquared = squared; }
        }

        private readonly struct PreviewEdge
        {
            public readonly int Owner;
            public readonly Vector3 Start;
            public readonly Vector3 End;
            public readonly bool OneWay;
            public readonly bool Automatic;
            public PreviewEdge(int owner, Vector3 start, Vector3 end, bool oneWay, bool automatic)
            {
                Owner = owner; Start = start; End = end; OneWay = oneWay; Automatic = automatic;
            }
        }

        private sealed class PreviewState
        {
            public readonly Dictionary<int, List<PreviewEdge>> ByMarker = new Dictionary<int, List<PreviewEdge>>();
        }

        private static readonly Dictionary<int, PreviewState> Previews = new Dictionary<int, PreviewState>();
        private static readonly Color AutoColor = new Color(0.18f, 0.82f, 1f, 0.9f);
        private static readonly Color AuthoredColor = new Color(0.25f, 1f, 0.30f, 0.95f);

        // This uses the same saved core map snapshot that the existing Route Validation tool
        // reads. Bake Current Scene refreshes the authoritative geometry before export.
        private static bool TryLoadPreviewWorld(Scene scene, out ServerCollisionWorld world, out string detail)
        {
            world = null;
            detail = string.Empty;
            ServerMap[] maps = UnityEngine.Object.FindObjectsByType<ServerMap>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            ServerMap map = null;
            foreach (ServerMap candidate in maps)
                if (candidate != null && candidate.gameObject.scene == scene)
                {
                    map = candidate;
                    break;
                }
            if (map == null || string.IsNullOrWhiteSpace(map.MapId))
            {
                detail = "Scene has no valid ServerMap.";
                return false;
            }
            string suffix = string.IsNullOrWhiteSpace(map.InstanceId)
                ? string.Empty : "__" + ServerMap.NormalizeId(map.InstanceId);
            string path = Path.Combine(ServerWorldBake.ServerMapsFolder,
                ServerMap.NormalizeId(map.MapId) + suffix + ".servermap.json");
            if (!File.Exists(path))
            {
                detail = "No baked server map exists at '" + path + "'.";
                return false;
            }
            try
            {
                ServerMapSnapshot snapshot = JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(path));
                if (snapshot == null || snapshot.formatVersion != ServerMapFormat.Version)
                {
                    detail = "Baked core-map format is missing or incompatible. Re-bake the current scene.";
                    return false;
                }
                world = new ServerCollisionWorld(snapshot);
                return true;
            }
            catch (Exception e)
            {
                detail = "Could not load baked world geometry: " + e.Message;
                return false;
            }
        }

        private static PopulationRoutePath[] ScenePaths(Scene scene) =>
            UnityEngine.Object.FindObjectsByType<PopulationRoutePath>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => PrefabUtility.IsPartOfPrefabInstance(x) ? 1 : 0)
                .ThenBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();

        private static bool SamePair(PopulationRouteMarker a, PopulationRouteMarker b,
            PopulationRouteMarker x, PopulationRouteMarker y) =>
            (a == x && b == y) || (a == y && b == x);

        private static bool RemoveSpecified(List<PopulationRoutePath.RoutePair> pairs,
            PopulationRouteMarker a, PopulationRouteMarker b)
        {
            if (pairs == null) return false;
            int before = pairs.Count;
            pairs.RemoveAll(p => p != null && SamePair(p.from, p.to, a, b));
            return before != pairs.Count;
        }

        private static bool TrySelectedMarkers(Scene scene, out PopulationRouteMarker a,
            out PopulationRouteMarker b, out string detail)
        {
            PopulationRouteMarker[] selected = Selection.gameObjects
                .Select(o => o != null ? o.GetComponent<PopulationRouteMarker>() : null)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .Distinct().ToArray();
            a = selected.Length == 2 ? selected[0] : null;
            b = selected.Length == 2 ? selected[1] : null;
            detail = selected.Length == 2 ? string.Empty :
                "Select exactly TWO active Population Route Marker objects in the Hierarchy (Ctrl+click).";
            return selected.Length == 2;
        }

        private static bool TrySelectedMarker(Scene scene,
            out PopulationRouteMarker marker, out string detail)
        {
            PopulationRouteMarker[] selected = Selection.gameObjects
                .Select(o => o != null ? o.GetComponent<PopulationRouteMarker>() : null)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .Distinct().ToArray();
            marker = selected.Length == 1 ? selected[0] : null;
            detail = selected.Length == 1 ? string.Empty :
                "Select exactly ONE active Population Route Marker in the Hierarchy.";
            return selected.Length == 1;
        }

        private static bool RebuildEditedPreview(Scene scene)
        {
            ClearPreview(scene);
            // The preview is recomputed from scene marker positions and saved overrides;
            // never modify prefab instances or bake files as part of an edit action.
            return BuildPreview(scene, out _, out _);
        }

        // Clearing a node means manual-only graph control, not deleting the marker.
        // This overrides automatic links AND inherited prefab/manual marker links;
        // explicit scene-level Connect Selected Nodes pairs remain allowed.
        internal static bool ClearSelectedNode(Scene scene)
        {
            if (!TrySelectedMarker(scene, out PopulationRouteMarker marker, out string detail))
            {
                Debug.LogWarning("[Population Graph Edit] " + detail);
                return false;
            }
            PopulationRoutePath[] paths = ScenePaths(scene);
            if (paths.Length == 0)
            {
                Debug.LogWarning("[Population Graph Edit] No active Population Route Path found in the scene " +
                    "to store node-level routing overrides.");
                return false;
            }
            bool changed = false;
            // A clear also discards old explicit Connect pairs for THIS node, even when
            // another path owns those pairs. Other nodes and unrelated pairs are untouched.
            foreach (PopulationRoutePath path in paths)
            {
                if (path.forcedConnections == null ||
                    !path.forcedConnections.Any(pair => pair != null &&
                        (pair.from == marker || pair.to == marker))) continue;
                Undo.RecordObject(path, "Clear Population Node Connections");
                path.forcedConnections.RemoveAll(pair => pair != null &&
                    (pair.from == marker || pair.to == marker));
                EditorUtility.SetDirty(path);
                changed = true;
            }
            PopulationRoutePath owner = paths[0];
            owner.manualOnlyMarkers ??= new List<PopulationRouteMarker>();
            if (!paths.Any(path => path.manualOnlyMarkers != null &&
                    path.manualOnlyMarkers.Contains(marker)))
            {
                Undo.RecordObject(owner, "Set Population Node Manual-Only");
                owner.manualOnlyMarkers.Add(marker);
                EditorUtility.SetDirty(owner);
                changed = true;
            }
            if (changed)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            RebuildEditedPreview(scene);
            Debug.Log($"[Population Graph Edit] Cleared all graph connections on node " +
                $"'{HierarchyPath(marker.transform)}'. Automatic links remain disabled for this node " +
                "until Restore Selected Node Auto; use Connect Selected Nodes for intended neighbors.");
            return true;
        }

        internal static bool RestoreSelectedNodeAuto(Scene scene)
        {
            if (!TrySelectedMarker(scene, out PopulationRouteMarker marker, out string detail))
            {
                Debug.LogWarning("[Population Graph Edit] " + detail);
                return false;
            }
            bool changed = false;
            foreach (PopulationRoutePath path in ScenePaths(scene))
            {
                if (path.manualOnlyMarkers == null ||
                    !path.manualOnlyMarkers.Contains(marker)) continue;
                Undo.RecordObject(path, "Restore Population Node Automatic Connections");
                path.manualOnlyMarkers.RemoveAll(x => x == marker);
                EditorUtility.SetDirty(path);
                changed = true;
            }
            if (changed)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            RebuildEditedPreview(scene);
            Debug.Log($"[Population Graph Edit] Restored automatic connections for " +
                $"'{HierarchyPath(marker.transform)}'.");
            return true;
        }

        // Scene-level edits are serialized on the existing Route Path component, never
        // on a duplicated sidewalk/crosswalk prefab's generatedLinks list.
        internal static bool EditSelectedConnection(Scene scene, string action)
        {
            if (!TrySelectedMarkers(scene, out PopulationRouteMarker a,
                    out PopulationRouteMarker b, out string detail))
            {
                Debug.LogWarning("[Population Graph Edit] " + detail);
                return false;
            }
            PopulationRoutePath[] paths = ScenePaths(scene);
            if (paths.Length == 0)
            {
                Debug.LogWarning("[Population Graph Edit] Add a Population Route Path to the scene first. " +
                    "It stores custom overrides without changing prefab instances.");
                return false;
            }
            PopulationRoutePath owner = paths[0];
            if (action == "force")
            {
                Physics.SyncTransforms();
                if (!TryLoadPreviewWorld(scene, out ServerCollisionWorld world, out detail) ||
                    !WalkableConnection(scene, world, a.transform.position, b.transform.position))
                {
                    Debug.LogWarning("[Population Graph Edit] The selected pair is not directly walkable. " +
                        "Add/move an intermediate waypoint around the obstacle, then force the two shorter connections. " + detail);
                    return false;
                }
            }
            bool changed = false;
            // Locate overrides in ANY scene route-path component, so a later edit can
            // undo a previous edit regardless of which route path originally owned it.
            foreach (PopulationRoutePath path in paths)
            {
                Undo.RecordObject(path, "Edit Population Graph Connection");
                if (RemoveSpecified(path.blockedConnections, a, b)) changed = true;
                if (RemoveSpecified(path.forcedConnections, a, b)) changed = true;
                if (changed) EditorUtility.SetDirty(path);
            }
            if (action == "block")
            {
                owner.blockedConnections ??= new List<PopulationRoutePath.RoutePair>();
                Undo.RecordObject(owner, "Block Population Connection");
                owner.blockedConnections.Add(new PopulationRoutePath.RoutePair { from = a, to = b });
                EditorUtility.SetDirty(owner);
                changed = true;
            }
            else if (action == "force")
            {
                owner.forcedConnections ??= new List<PopulationRoutePath.RoutePair>();
                Undo.RecordObject(owner, "Force Population Connection");
                owner.forcedConnections.Add(new PopulationRoutePath.RoutePair { from = a, to = b });
                EditorUtility.SetDirty(owner);
                changed = true;
            }
            if (changed)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            }
            RebuildEditedPreview(scene);
            Debug.Log($"[Population Graph Edit] {action}: '{HierarchyPath(a.transform)}' <-> " +
                $"'{HierarchyPath(b.transform)}'. Scene-level overrides are saved with the scene; rebuild the preview.");
            return true;
        }

        internal static void AddSceneWaypoint(Scene scene)
        {
            PopulationRoutePath[] paths = ScenePaths(scene);
            if (paths.Length == 0)
            {
                Debug.LogWarning("[Population Graph Edit] Add a Population Route Path to the scene first.");
                return;
            }
            // Place at the author-controlled Scene view focus. This is deliberately
            // NOT the midpoint of an obstructed edge: a midpoint might be inside a wall.
            Vector3 position = SceneView.lastActiveSceneView != null
                ? SceneView.lastActiveSceneView.pivot : paths[0].transform.position;
            GameObject go = new GameObject("Route Marker - Custom Waypoint");
            Undo.RegisterCreatedObjectUndo(go, "Add Population Route Waypoint");
            Undo.SetTransformParent(go.transform, paths[0].transform, "Parent Route Waypoint");
            go.transform.position = position;
            go.AddComponent<PopulationRouteMarker>();
            Selection.activeGameObject = go;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            ClearPreview(scene);
            Debug.Log("[Population Graph Edit] Waypoint created at Scene view focus. " +
                "Position it on walkable ground, then use Connect Selected Nodes for deliberate route segments.");
        }

        private static bool WalkableConnection(Scene scene, ServerCollisionWorld world,
            Vector3 a, Vector3 b)
        {
            if (!PopulationRouteWalkabilityGizmos.SegmentTraversable(world, scene, a, b))
                return false;
            // Baked ground alone does not catch every authored scene collider.
            // Conservative torso-height checks reject obvious visual obstacles.
            Vector3 up = Vector3.up * 1.25f;
            return !Physics.Linecast(a + up, b + up,
                ~0, QueryTriggerInteraction.Ignore);
        }

        internal static bool HasPreview(Scene scene) => scene.IsValid() && Previews.ContainsKey(scene.handle);

        internal static void ClearPreview(Scene scene)
        {
            if (scene.IsValid()) Previews.Remove(scene.handle);
            SceneView.RepaintAll();
        }

        // Called from the existing Build Population Graph / Validate buttons.
        internal static bool BuildPreview(Scene scene, out int builtEdges, out int rejectedEdges)
        {
            builtEdges = 0;
            rejectedEdges = 0;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError("[Population Graph] No loaded scene to scan.");
                return false;
            }

            if (!TryLoadPreviewWorld(scene, out ServerCollisionWorld world, out string detail))
            {
                Debug.LogError("[Population Graph] " + detail + " Use Bake Current Scene to create the initial collision data.");
                return false;
            }

            PopulationRouteMarker[] markers = UnityEngine.Object.FindObjectsByType<PopulationRouteMarker>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal)
                .ToArray();

            // Prefab copies share their serialized bakeId until World Bake repairs them.
            // A preview is non-destructive: use temporary, scene-local IDs for copies
            // or missing IDs instead of mutating prefab-instance properties on a button click.
            // These IDs exist ONLY in the preview graph. World Bake remains responsible for
            // assigning persistent identities before writing any route/spawn layer.
            var ids = new Dictionary<PopulationRouteMarker, long>(markers.Length);
            var byId = new HashSet<long>();
            int provisionalIds = 0;
            for (int i = 0; i < markers.Length; ++i)
            {
                PopulationRouteMarker marker = markers[i];
                bool hasId = !string.IsNullOrWhiteSpace(marker.BakeId);
                long id = hasId ? ServerWorldBake.StableAuthoringId(marker.BakeId) : 0L;
                if (!hasId || id <= 0 || !byId.Add(id))
                {
                    // The ordered scene index makes same-name prefab instances distinct.
                    // Probe for a free ID even in the unlikely event of a hash collision.
                    int attempt = 0;
                    do
                    {
                        id = ServerWorldBake.StableAuthoringId(
                            "preview-only|" + scene.path + "|" + HierarchyPath(marker.transform) +
                            "|" + i + "|" + attempt++);
                    }
                    while (id <= 0 || !byId.Add(id));
                    provisionalIds++;
                }
                ids.Add(marker, id);
            }
            if (provisionalIds > 0)
                Debug.LogWarning($"[Population Graph] {provisionalIds} copied or missing prefab-marker ID(s) " +
                    "received temporary preview identities. Nothing was changed in the scene. " +
                    "Bake Current Scene repairs persistent IDs; save the scene after baking.");

            var edges = new List<ServerPopulationRouteEdge>(Math.Max(16, markers.Length * 2));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            CollectAuthoredEdges(scene, markers, ids, edges, keys);
            bool connected = CompleteGraph(scene, world, markers, ids, edges, keys,
                out int generated, out int discarded, true);

            // A disconnected graph still has useful verified edges to display in Scene view.
            // Preserve the preview so the author can see which island has no safe connection.
            SavePreview(scene, markers, ids, edges, generated);
            builtEdges = edges.Count;
            rejectedEdges = discarded;
            SceneView.RepaintAll();
            if (!connected)
                return false;
            Debug.Log($"[Population Graph] Preview built from ALL scene markers: markers={markers.Length}, " +
                $"edges={edges.Count}, automatic={generated}, invalid authored edges discarded={discarded}. " +
                "Cyan=automatic, green=authored. Scene/prefab marker links were not rewritten.");
            return true;
        }

        private static string HierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }

        // Preview collects precisely the existing optional manual/ordered authoring contracts.
        // World Bake already gathers those contracts before calling CompleteGraph.
        private static void CollectAuthoredEdges(
            Scene scene, PopulationRouteMarker[] markers, Dictionary<PopulationRouteMarker, long> ids,
            List<ServerPopulationRouteEdge> output, HashSet<string> keys)
        {
            var directedPairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (PopulationRouteMarker marker in markers)
            {
                if (marker.oneWayOutboundLinks == null) continue;
                foreach (PopulationRouteMarker to in marker.oneWayOutboundLinks)
                    if (to != null && to != marker && ids.TryGetValue(to, out long toId))
                        directedPairs.Add(Pair(ids[marker], toId));
            }
            PopulationRoutePath[] paths = UnityEngine.Object.FindObjectsByType<PopulationRoutePath>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x != null && x.gameObject.scene == scene && x.gameObject.activeInHierarchy)
                .ToArray();
            var ordered = new List<PopulationRouteMarker>();
            foreach (PopulationRoutePath path in paths)
            {
                if (!path.connectChildrenInOrder || !path.oneWay) continue;
                path.CollectDirectMarkers(ordered);
                for (int i = 1; i < ordered.Count; ++i)
                    if (ids.TryGetValue(ordered[i - 1], out long a) && ids.TryGetValue(ordered[i], out long b))
                        directedPairs.Add(Pair(a, b));
                if (path.loop && ordered.Count > 2 &&
                    ids.TryGetValue(ordered[ordered.Count - 1], out long last) &&
                    ids.TryGetValue(ordered[0], out long first))
                    directedPairs.Add(Pair(last, first));
            }

            foreach (PopulationRouteMarker marker in markers)
            {
                if (marker.links == null) continue;
                foreach (PopulationRouteMarker to in marker.links)
                {
                    if (to == null || to == marker || !ids.TryGetValue(to, out long toId) ||
                        directedPairs.Contains(Pair(ids[marker], toId)) ||
                        (marker.generatedLinks != null && marker.generatedLinks.Contains(to)) ||
                        (to.generatedLinks != null && to.generatedLinks.Contains(marker)))
                        continue;
                    AddEdge(output, keys, ids[marker], toId, false,
                        (marker.pathWidth + to.pathWidth) * 0.5f, 1f, 0.5f);
                }
            }
            foreach (PopulationRoutePath path in paths)
            {
                if (!path.connectChildrenInOrder) continue;
                path.CollectDirectMarkers(ordered);
                int count = ordered.Count;
                int n = count > 1 ? count - 1 + (path.loop && count > 2 ? 1 : 0) : 0;
                for (int i = 0; i < n; ++i)
                {
                    PopulationRouteMarker from = ordered[i % count], to = ordered[(i + 1) % count];
                    if (ids.TryGetValue(from, out long fromId) && ids.TryGetValue(to, out long toId) &&
                        !directedPairs.Contains(Pair(fromId, toId)))
                        AddEdge(output, keys, fromId, toId, path.oneWay, path.width, path.weight, path.minimumClearance);
                }
            }
            foreach (PopulationRouteMarker marker in markers)
            {
                if (marker.oneWayOutboundLinks == null) continue;
                foreach (PopulationRouteMarker to in marker.oneWayOutboundLinks)
                    if (to != null && to != marker && ids.TryGetValue(to, out long toId))
                        AddEdge(output, keys, ids[marker], toId, true,
                            (marker.pathWidth + to.pathWidth) * 0.5f, 1f, 0.5f);
            }
        }

        private static string Pair(long a, long b) => a < b ? a + ":" + b : b + ":" + a;
        private static string EdgeKey(long a, long b, bool directed) =>
            directed ? $"D:{a}:{b}" : "B:" + Pair(a, b);

        private static void AddEdge(
            List<ServerPopulationRouteEdge> edges, HashSet<string> keys,
            long from, long to, bool oneWay, float width, float weight, float clearance)
        {
            if (from <= 0 || to <= 0 || from == to || !keys.Add(EdgeKey(from, to, oneWay))) return;
            edges.Add(new ServerPopulationRouteEdge
            {
                fromNodeId = from,
                toNodeId = to,
                oneWay = oneWay,
                width = Mathf.Max(0.5f, width),
                weight = Mathf.Max(0.01f, weight),
                minimumClearance = Mathf.Max(0f, clearance),
                validatedClear = true,
            });
        }

        // The authoritative bake and preview call this exact method. All connections, including
        // hand-authored ones, must pass a baked-world capsule/ground traversal check.
        internal static bool CompleteGraph(
            Scene scene, ServerCollisionWorld world,
            PopulationRouteMarker[] markers, Dictionary<PopulationRouteMarker, long> ids,
            List<ServerPopulationRouteEdge> edges, HashSet<string> keys,
            out int automatic, out int discarded, bool diagnostics)
        {
            automatic = 0;
            int autoCount = 0;
            discarded = 0;
            int n = markers.Length;
            if (n == 0) return true;

            var byId = new Dictionary<long, int>(n);
            for (int i = 0; i < n; ++i) byId[ids[markers[i]]] = i;
            var paths = ScenePaths(scene);
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            var forced = new HashSet<string>(StringComparer.Ordinal);
            var manualOnly = new HashSet<long>();
            foreach (PopulationRoutePath authoredPath in paths)
            {
                if (authoredPath.manualOnlyMarkers != null)
                    foreach (PopulationRouteMarker marker in authoredPath.manualOnlyMarkers)
                        if (marker != null && ids.TryGetValue(marker, out long manualId))
                            manualOnly.Add(manualId);
                if (authoredPath.blockedConnections != null)
                    foreach (PopulationRoutePath.RoutePair item in authoredPath.blockedConnections)
                        if (item != null && item.from != null && item.to != null &&
                            ids.TryGetValue(item.from, out long a) && ids.TryGetValue(item.to, out long b))
                            blocked.Add(Pair(a, b));
                if (authoredPath.forcedConnections != null)
                    foreach (PopulationRoutePath.RoutePair item in authoredPath.forcedConnections)
                        if (item != null && item.from != null && item.to != null &&
                            ids.TryGetValue(item.from, out long a) && ids.TryGetValue(item.to, out long b))
                            forced.Add(Pair(a, b));
            }
            forced.ExceptWith(blocked); // Block wins on contradictory authoring.
            Physics.SyncTransforms();
            var tested = new Dictionary<ulong, bool>(Math.Max(32, edges.Count));
            bool Walkable(int a, int b)
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                ulong key = ((ulong)(uint)lo << 32) | (uint)hi;
                if (tested.TryGetValue(key, out bool prior)) return prior;
                Vector3 va = markers[a].transform.position, vb = markers[b].transform.position;
                bool good = WalkableConnection(scene, world, va, vb);
                tested.Add(key, good);
                return good;
            }

            // Fail closed on geometry, but preserve authored edges when physically valid.
            var accepted = new List<ServerPopulationRouteEdge>(edges.Count);
            keys.Clear();
            foreach (ServerPopulationRouteEdge e in edges)
            {
                if (e == null || !byId.TryGetValue(e.fromNodeId, out int a) ||
                    !byId.TryGetValue(e.toNodeId, out int b) ||
                    manualOnly.Contains(e.fromNodeId) || manualOnly.Contains(e.toNodeId) ||
                    blocked.Contains(Pair(e.fromNodeId, e.toNodeId)) || !Walkable(a, b))
                {
                    discarded++;
                    if (diagnostics && e != null)
                        Debug.LogWarning($"[Population Graph] " +
                            (manualOnly.Contains(e.fromNodeId) || manualOnly.Contains(e.toNodeId)
                                ? "Manual-only node excludes original edge: "
                                : blocked.Contains(Pair(e.fromNodeId, e.toNodeId))
                                    ? "Scene-blocked authored edge omitted: "
                                    : "Unwalkable authored edge rejected: ") +
                            $"'{(byId.TryGetValue(e.fromNodeId, out int x) ? HierarchyPath(markers[x].transform) : e.fromNodeId.ToString())}' " +
                            $"-> '{(byId.TryGetValue(e.toNodeId, out int y) ? HierarchyPath(markers[y].transform) : e.toNodeId.ToString())}'.");
                    continue;
                }
                if (keys.Add(EdgeKey(e.fromNodeId, e.toNodeId, e.oneWay))) accepted.Add(e);
            }
            edges.Clear();
            edges.AddRange(accepted);
            foreach (string pair in forced.OrderBy(x => x, StringComparer.Ordinal))
            {
                int colon = pair.IndexOf(':');
                if (colon < 0 || !long.TryParse(pair.Substring(0, colon), out long from) ||
                    !long.TryParse(pair.Substring(colon + 1), out long to)) continue;
                if (!byId.TryGetValue(from, out int a) || !byId.TryGetValue(to, out int b)) continue;
                if (!Walkable(a, b))
                {
                    discarded++;
                    if (diagnostics) Debug.LogError($"[Population Graph] Forced link is obstructed: " +
                        $"'{HierarchyPath(markers[a].transform)}' -> '{HierarchyPath(markers[b].transform)}'. " +
                        "Add an intermediate waypoint instead of routing through geometry.");
                    continue;
                }
                // Do not duplicate an already authored connection.
                if (accepted.Any(e => Pair(e.fromNodeId, e.toNodeId) == pair)) continue;
                AddEdge(edges, keys, from, to, false,
                    (markers[a].pathWidth + markers[b].pathWidth) * 0.5f, 1f, 0.5f);
            }

            var neighbors = new List<int>[n];
            var degree = new int[n];
            for (int i = 0; i < n; ++i) neighbors[i] = new List<int>(4);
            var directedPairs = new HashSet<string>(StringComparer.Ordinal);
            var existingPairs = new HashSet<string>(StringComparer.Ordinal);
            void Register(int a, int b)
            {
                if (!neighbors[a].Contains(b)) { neighbors[a].Add(b); degree[a]++; }
                if (!neighbors[b].Contains(a)) { neighbors[b].Add(a); degree[b]++; }
                existingPairs.Add(Pair(ids[markers[a]], ids[markers[b]]));
            }
            foreach (ServerPopulationRouteEdge e in edges)
            {
                int a = byId[e.fromNodeId], b = byId[e.toNodeId];
                Register(a, b);
                if (e.oneWay) directedPairs.Add(Pair(e.fromNodeId, e.toNodeId));
            }
            // Do not accidentally reverse an authored one-way connection that failed traversal.
            foreach (PopulationRouteMarker marker in markers)
            {
                if (marker.oneWayOutboundLinks == null) continue;
                foreach (PopulationRouteMarker to in marker.oneWayOutboundLinks)
                    if (to != null && ids.TryGetValue(to, out long targetId))
                        directedPairs.Add(Pair(ids[marker], targetId));
            }

            // Bounded spatial index. This also sees prefab-instance markers at any hierarchy depth.
            var buckets = new Dictionary<(int, int), List<int>>();
            var positions = new Vector3[n];
            for (int i = 0; i < n; ++i)
            {
                positions[i] = markers[i].transform.position;
                var cell = Cell(positions[i]);
                if (!buckets.TryGetValue(cell, out List<int> bucket))
                    buckets.Add(cell, bucket = new List<int>());
                bucket.Add(i);
            }
            List<Candidate> nearby = CollectCandidates(AutomaticRadius, CandidatesPerMarker, positions, buckets);
            nearby.Sort((a, b) => a.DistanceSquared != b.DistanceSquared
                ? a.DistanceSquared.CompareTo(b.DistanceSquared)
                : a.A != b.A ? a.A.CompareTo(b.A) : a.B.CompareTo(b.B));

            // Avoid skipping short sidewalk waypoints in favor of a diagonal, longer
            // link. The closest peer provides the local scale at each end; sparse
            // corridors can still connect up to AutomaticRadius.
            var localSpacing = new float[n];
            for (int i = 0; i < n; ++i) localSpacing[i] = AutomaticRadius;
            foreach (Candidate c in nearby)
            {
                if (Mathf.Abs(positions[c.A].y - positions[c.B].y) > 2.5f) continue;
                float distance = Mathf.Sqrt(c.DistanceSquared);
                localSpacing[c.A] = Mathf.Min(localSpacing[c.A], distance);
                localSpacing[c.B] = Mathf.Min(localSpacing[c.B], distance);
            }

            bool BypassesWaypoint(int a, int b)
            {
                Vector3 start = positions[a], end = positions[b];
                Vector3 delta = end - start;
                float horizontalSq = delta.x * delta.x + delta.z * delta.z;
                if (horizontalSq < 0.001f) return false;
                int minX = Mathf.FloorToInt((Mathf.Min(start.x, end.x) - NearbyWaypointTolerance) / GridSize);
                int maxX = Mathf.FloorToInt((Mathf.Max(start.x, end.x) + NearbyWaypointTolerance) / GridSize);
                int minZ = Mathf.FloorToInt((Mathf.Min(start.z, end.z) - NearbyWaypointTolerance) / GridSize);
                int maxZ = Mathf.FloorToInt((Mathf.Max(start.z, end.z) + NearbyWaypointTolerance) / GridSize);
                for (int x = minX; x <= maxX; ++x)
                    for (int z = minZ; z <= maxZ; ++z)
                    {
                        if (!buckets.TryGetValue((x, z), out List<int> bucket)) continue;
                        foreach (int other in bucket)
                        {
                            if (other == a || other == b) continue;
                            Vector3 point = positions[other];
                            float t = ((point.x - start.x) * delta.x +
                                (point.z - start.z) * delta.z) / horizontalSq;
                            if (t <= 0.15f || t >= 0.85f) continue;
                            float diffX = point.x - (start.x + delta.x * t);
                            float diffZ = point.z - (start.z + delta.z * t);
                            if (diffX * diffX + diffZ * diffZ <=
                                    NearbyWaypointTolerance * NearbyWaypointTolerance &&
                                Mathf.Abs(point.y - Mathf.Lerp(start.y, end.y, t)) <= 1.5f)
                                return true;
                        }
                    }
                // Also reject a longer link if a closer, traversable waypoint occupies
                // essentially the same outward direction, even if slightly off-line.
                // A crossing at a true right angle is not wrongly treated as a bypass.
                for (int endpoint = 0; endpoint < 2; ++endpoint)
                {
                    int source = endpoint == 0 ? a : b;
                    int destination = endpoint == 0 ? b : a;
                    Vector3 aim = positions[destination] - positions[source];
                    aim.y = 0f;
                    float distance = aim.magnitude;
                    if (distance < MinimumSegment) continue;
                    aim /= distance;
                    (int sx, int sz) = Cell(positions[source]);
                    int span = Mathf.CeilToInt(distance / GridSize);
                    for (int xx = sx - span; xx <= sx + span; ++xx)
                        for (int zz = sz - span; zz <= sz + span; ++zz)
                        {
                            if (!buckets.TryGetValue((xx, zz), out List<int> list)) continue;
                            foreach (int candidate in list)
                            {
                                if (candidate == a || candidate == b) continue;
                                Vector3 shorter = positions[candidate] - positions[source];
                                if (Mathf.Abs(shorter.y) > 1.5f) continue;
                                shorter.y = 0f;
                                float nearer = shorter.magnitude;
                                if (nearer < MinimumSegment || nearer > distance * 0.84f) continue;
                                if (Vector3.Dot(aim, shorter / nearer) < 0.87f) continue;
                                if (Walkable(source, candidate)) return true;
                            }
                        }
                }
                return false;
            }

            // An automatically inferred intersection is not a route junction.
            // The path must meet at an existing marker or at an intentionally
            // authored link. Reject crossing an existing edge at similar height.
            bool CrossesExistingEdge(int a, int b)
            {
                Vector3 start = positions[a], end = positions[b];
                float rx = end.x - start.x, rz = end.z - start.z;
                foreach (ServerPopulationRouteEdge edge in edges)
                {
                    int c = byId[edge.fromNodeId], d = byId[edge.toNodeId];
                    if (c == a || c == b || d == a || d == b) continue;
                    Vector3 p = positions[c], q = positions[d];
                    float sx = q.x - p.x, sz = q.z - p.z;
                    float determinant = rx * sz - rz * sx;
                    if (Mathf.Abs(determinant) < 0.0001f) continue;
                    float qx = p.x - start.x, qz = p.z - start.z;
                    float t = (qx * sz - qz * sx) / determinant;
                    float u = (qx * rz - qz * rx) / determinant;
                    if (t <= 0.02f || t >= 0.98f || u <= 0.02f || u >= 0.98f) continue;
                    float crossingY = Mathf.Lerp(start.y, end.y, t);
                    float otherY = Mathf.Lerp(p.y, q.y, u);
                    if (Mathf.Abs(crossingY - otherY) <= 1.5f) return true;
                }
                return false;
            }

            // Two links suffice for an ordinary straight path, but crossings and
            // sidewalk junctions often use regular prefab markers, not branchNode.
            // Only allow a third/fourth automatic link when it joins graph components.
            int Limit(int index, bool recovery) => recovery ? 4 : (markers[index].branchNode ? 4 : 2);
            bool HasDirectionalRoom(int a, int b)
            {
                Vector3 direction = positions[b] - positions[a]; direction.y = 0f;
                float length = direction.magnitude;
                if (length < MinimumSegment) return false;
                direction /= length;
                foreach (int linked in neighbors[a])
                {
                    Vector3 used = positions[linked] - positions[a]; used.y = 0f;
                    if (used.sqrMagnitude < 0.001f) continue;
                    if (Vector3.Dot(direction, used.normalized) > CosMinimumAngle) return false;
                }
                return true;
            }
            string Rejection(Candidate candidate, bool recovery)
            {
                int a = candidate.A, b = candidate.B;
                string key = Pair(ids[markers[a]], ids[markers[b]]);
                if (blocked.Contains(key)) return "blocked by scene authoring";
                if (manualOnly.Contains(ids[markers[a]]) || manualOnly.Contains(ids[markers[b]]))
                    return "node set to manual-only connections";
                if (existingPairs.Contains(key)) return "already connected";
                if (directedPairs.Contains(key)) return "authored one-way restriction";
                if (degree[a] >= Limit(a, recovery) || degree[b] >= Limit(b, recovery))
                    return "junction degree cap";
                if (!HasDirectionalRoom(a, b) || !HasDirectionalRoom(b, a))
                    return "conflicts with existing connection direction";
                // Primary construction prefers local neighbors; a specifically
                // justified component-repair link may span the full 28m radius.
                float distance = Mathf.Sqrt(candidate.DistanceSquared);
                float localLimit = Mathf.Max(MinimumAutoAllowance,
                    Mathf.Min(localSpacing[a], localSpacing[b]) * LocalDistanceMultiplier);
                if (!recovery && distance > localLimit) return "too far for ordinary local spacing";
                // Component recovery is intentionally MORE conservative than normal
                // linking. A disconnected island is preferable to an unsafe diagonal.
                if (recovery && distance > Mathf.Min(14f, localLimit))
                    return "recovery would make an overlong junction shortcut";
                if (BypassesWaypoint(a, b)) return "would skip an intermediate marker";
                if (CrossesExistingEdge(a, b)) return "would cross an existing route edge";
                if (!Walkable(a, b)) return "not traversable on baked movement geometry";
                return null;
            }

            bool TryConnect(Candidate candidate, bool recovery)
            {
                if (Rejection(candidate, recovery) != null) return false;
                int a = candidate.A, b = candidate.B;
                AddEdge(edges, keys, ids[markers[a]], ids[markers[b]], false,
                    (markers[a].pathWidth + markers[b].pathWidth) * 0.5f, 1f, 0.5f);
                Register(a, b);
                autoCount++;
                return true;
            }
            // Prefer connecting nodes that would otherwise have no exit, then fill sensible
            // continuations and authored intersections without dense all-to-all shortcuts.
            for (int pass = 0; pass < 3; ++pass)
                foreach (Candidate c in nearby)
                {
                    if (pass == 0 && degree[c.A] > 0 && degree[c.B] > 0) continue;
                    if (pass == 1 && degree[c.A] >= 2 && degree[c.B] >= 2) continue;
                    if (pass == 2 && !markers[c.A].branchNode && !markers[c.B].branchNode) continue;
                    TryConnect(c, false);
                }

            // Diagnostics are scoped to the resulting safe graph. Separate walkable
            // islands are valid: pedestrians should never be given a fabricated
            // diagonal road/terrain shortcut just to force a single component.
            var parent = new int[n];
            for (int i = 0; i < n; ++i) parent[i] = i;
            int Root(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            int groups = n;
            void Merge(int a, int b)
            {
                a = Root(a); b = Root(b);
                if (a == b) return;
                parent[b] = a;
                --groups;
            }
            foreach (ServerPopulationRouteEdge edge in edges)
                Merge(byId[edge.fromNodeId], byId[edge.toNodeId]);

            // Crucial recovery pass: greedy degree-2 local linking can make a
            // perfectly walkable crossing look like two disconnected islands.
            // Reconsider only pairs joining DIFFERENT components and allow a
            // geometrically valid local T/X junction without requiring artists to
            // mark prefab-contained crossing nodes manually as branch nodes.
            // No unvalidated shortcut, graph-edge crossing or waypoint skipping.
            int junctionRecoveries = 0;
            foreach (Candidate candidate in nearby)
            {
                if (groups <= 1) break;
                if (Root(candidate.A) == Root(candidate.B)) continue;
                if (!TryConnect(candidate, true)) continue;
                Merge(candidate.A, candidate.B);
                junctionRecoveries++;
            }

            automatic = autoCount;
            if (diagnostics)
            {
                if (groups > 1)
                {
                    // Report a *nearest* separated pair within the auto-link search
                    // radius, not arbitrary endpoints from distant islands.
                    Candidate? closest = null;
                    foreach (Candidate candidate in nearby)
                        if (Root(candidate.A) != Root(candidate.B) &&
                            (!closest.HasValue || candidate.DistanceSquared < closest.Value.DistanceSquared))
                            closest = candidate;
                    string reason;
                    if (closest.HasValue)
                    {
                        Candidate candidate = closest.Value;
                        string blocker = Rejection(candidate, true) ?? "no remaining blocker";
                        reason = $"Closest still-separated markers: " +
                            $"'{HierarchyPath(markers[candidate.A].transform)}' at {positions[candidate.A]} and " +
                            $"'{HierarchyPath(markers[candidate.B].transform)}' at {positions[candidate.B]}, " +
                            $"distance={Mathf.Sqrt(candidate.DistanceSquared):F1}m; " +
                            $"connection rejected because: {blocker}. " +
                            "Only add an intermediate waypoint or explicit custom route if that path is genuinely walkable.";
                    }
                    else
                    {
                        reason = $"No pair from different components lies within the {AutomaticRadius:F0}m " +
                            "local search radius; place additional markers to bridge intended walkways.";
                    }
                    Debug.LogWarning($"[Population Graph] {groups} separate route components remain " +
                        $"({n} markers, {edges.Count} validated edges, " +
                        $"local junction repairs={junctionRecoveries}). " + reason +
                        " Unconnected islands are allowed; no artificial long-distance bridge was added.");
                }
                else
                    Debug.Log($"[Population Graph] One connected component: {n} markers, " +
                        $"{edges.Count} validated edges, automatic={autoCount}, " +
                        $"local junction repairs={junctionRecoveries}, " +
                        $"rejected unwalkable authored={discarded}.");
            }
            // A disconnected *world* is not a failed bake. Individual portal and spawn
            // entry reachability is still validated by ServerWorldBake and remains fatal.
            return true;
        }

        private static (int, int) Cell(Vector3 p) =>
            (Mathf.FloorToInt(p.x / GridSize), Mathf.FloorToInt(p.z / GridSize));

        private static List<Candidate> CollectCandidates(float radius, int perMarker,
            Vector3[] positions, Dictionary<(int, int), List<int>> buckets)
        {
            var result = new List<Candidate>();
            var seen = new HashSet<ulong>();
            int span = Mathf.CeilToInt(radius / GridSize);
            float maxSq = radius * radius;
            for (int a = 0; a < positions.Length; ++a)
            {
                (int x, int z) = Cell(positions[a]);
                var choices = new List<Candidate>();
                for (int dx = -span; dx <= span; ++dx)
                    for (int dz = -span; dz <= span; ++dz)
                    {
                        if (!buckets.TryGetValue((x + dx, z + dz), out List<int> bucket)) continue;
                        foreach (int b in bucket)
                        {
                            if (a == b) continue;
                            Vector3 difference = positions[a] - positions[b];
                            float horizontalSq = difference.x * difference.x + difference.z * difference.z;
                            // Height is not a shortcut rejection: ramps and stairs can change
                            // elevation safely. The authoritative capsule/ground sweep decides.
                            if (horizontalSq < MinimumSegment * MinimumSegment || horizontalSq > maxSq) continue;
                            choices.Add(new Candidate(Math.Min(a, b), Math.Max(a, b), horizontalSq));
                        }
                    }
                choices.Sort((u, v) => u.DistanceSquared != v.DistanceSquared
                    ? u.DistanceSquared.CompareTo(v.DistanceSquared)
                    : u.B.CompareTo(v.B));
                int count = perMarker <= 0 ? choices.Count : Math.Min(perMarker, choices.Count);
                for (int k = 0; k < count; ++k)
                {
                    Candidate c = choices[k];
                    ulong pair = ((ulong)(uint)c.A << 32) | (uint)c.B;
                    if (seen.Add(pair)) result.Add(c);
                }
            }
            return result;
        }

        private static void SavePreview(Scene scene, PopulationRouteMarker[] markers,
            Dictionary<PopulationRouteMarker, long> ids, List<ServerPopulationRouteEdge> edges, int autoCount)
        {
            var byId = new Dictionary<long, PopulationRouteMarker>(markers.Length);
            foreach (PopulationRouteMarker marker in markers) byId[ids[marker]] = marker;
            // Edges appended by CompleteGraph are the automatic subset; earlier edges are authored.
            int authoredEnd = Math.Max(0, edges.Count - autoCount);
            var state = new PreviewState();
            for (int i = 0; i < edges.Count; ++i)
            {
                ServerPopulationRouteEdge edge = edges[i];
                if (!byId.TryGetValue(edge.fromNodeId, out PopulationRouteMarker a) ||
                    !byId.TryGetValue(edge.toNodeId, out PopulationRouteMarker b)) continue;
                int owner = a.GetInstanceID();
                if (!state.ByMarker.TryGetValue(owner, out List<PreviewEdge> list))
                    state.ByMarker.Add(owner, list = new List<PreviewEdge>());
                list.Add(new PreviewEdge(owner, a.transform.position, b.transform.position,
                    edge.oneWay, i >= authoredEnd));
            }
            Previews[scene.handle] = state;
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Active)]
        private static void DrawPreview(PopulationRouteMarker marker, GizmoType type)
        {
            if (marker == null || !Previews.TryGetValue(marker.gameObject.scene.handle, out PreviewState state) ||
                !state.ByMarker.TryGetValue(marker.GetInstanceID(), out List<PreviewEdge> lines)) return;
            foreach (PreviewEdge line in lines)
            {
                Gizmos.color = line.Automatic ? AutoColor : AuthoredColor;
                Gizmos.DrawLine(line.Start, line.End);
                if (line.OneWay)
                {
                    Vector3 d = line.End - line.Start; d.y = 0f;
                    if (d.sqrMagnitude > 0.05f)
                    {
                        d.Normalize();
                        Vector3 p = Vector3.Lerp(line.Start, line.End, 0.72f);
                        Vector3 side = Vector3.Cross(Vector3.up, d) * 0.18f;
                        Gizmos.DrawLine(p, p - d * 0.35f + side);
                        Gizmos.DrawLine(p, p - d * 0.35f - side);
                    }
                }
            }
        }
    }
}
