using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.Server.Application.World;
using Game.Shared.Population;
using Game.Shared.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    // Authoring-only changes live IN the existing .routes.json. The standalone server
    // ignores this additional JSON field; World Bake reapplies it to regenerated routes.
    [Serializable]
    internal sealed class PopulationBakedGraphEdits
    {
        [Serializable]
        public sealed class MovedNode
        {
            public long id;
            public ServerPose pose;
        }

        [Serializable]
        public sealed class NodePair
        {
            public long a;
            public long b;
        }

        public ServerPopulationRouteNode[] addedNodes = Array.Empty<ServerPopulationRouteNode>();
        public long[] removedNodeIds = Array.Empty<long>();
        public long[] manualOnlyNodeIds = Array.Empty<long>();
        public MovedNode[] movedNodes = Array.Empty<MovedNode>();
        public NodePair[] blockedPairs = Array.Empty<NodePair>();
        public ServerPopulationRouteEdge[] manualEdges = Array.Empty<ServerPopulationRouteEdge>();
    }

    // Production graph editing workspace; no standalone migration or one-shot menu tool.
    // Nodes and edges are read from the published baked layer and edited in-place.
    internal sealed class PopulationBakedGraphEditor : EditorWindow
    {
        [Serializable]
        internal sealed class Layer
        {
            public int formatVersion = 1;
            public string mapId;
            public string instanceId;
            public string coreContentHash;
            public ServerPopulationRouteNode[] populationNodes = Array.Empty<ServerPopulationRouteNode>();
            public ServerPopulationRouteEdge[] populationEdges = Array.Empty<ServerPopulationRouteEdge>();
            public ServerPopulationPortal[] populationPortals = Array.Empty<ServerPopulationPortal>();
            public PopulationBakedGraphEdits editorEdits = new PopulationBakedGraphEdits();
        }

        private Scene scene;
        private string routesPath;
        private Layer layer;
        private ServerCollisionWorld collision;
        private long selectedA;
        private long selectedB;
        private bool moveSelected;
        private bool dirty;
        private Vector2 scroll;
        private string lastError = string.Empty;
        private string lastSaveStatus = string.Empty;
        private bool lastSaveSucceeded;
        private string loadedDiskText;
        private string lastPairResult = string.Empty;
        private MessageType lastPairMessageType = MessageType.None;
        private long lastPairedA;
        private long lastPairedB;
        private bool lastPairAdded;
        private string lastSceneSync = string.Empty;
        private double nextSceneSync;
        private readonly HashSet<long> sceneMovedNodes = new HashSet<long>();
        // Authoring validation only. Never repair or regenerate duplicate authoring IDs implicitly.
        private readonly List<string> sceneMarkerProblems = new List<string>();
        private int importedSceneMarkerCount;
        // Scene-only presentation: baked node identity does not depend on a GameObject.
        private bool showNodeNumbers = true;
        private bool showAllNodeLabels;
        private bool fadeUnselectedEdges = true;
        private string nodeSearch = string.Empty;
        private long hoveredNode;
        private GUIStyle numberStyle;
        private GUIStyle focusedStyle;

        internal static string RoutePath(Scene scene)
        {
            ServerMap map = UnityEngine.Object.FindObjectsByType<ServerMap>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(x => x != null && x.gameObject.scene == scene);
            if (map == null || string.IsNullOrWhiteSpace(map.MapId)) return null;
            string suffix = string.IsNullOrWhiteSpace(map.InstanceId) ? string.Empty :
                "__" + ServerMap.NormalizeId(map.InstanceId);
            return Path.Combine(ServerWorldBake.ServerMapsFolder,
                ServerMap.NormalizeId(map.MapId) + suffix + ".routes.json");
        }

        internal static bool RouteLayerIncludesId(Scene scene, long id)
        {
            string path = RoutePath(scene);
            if (id <= 0 || string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                Layer result = JsonUtility.FromJson<Layer>(File.ReadAllText(path));
                return result != null && result.populationNodes != null &&
                    result.populationNodes.Any(x => x != null && x.stableId == id);
            }
            catch { return false; }
        }

        private static PopulationBakedGraphEdits Normalize(PopulationBakedGraphEdits edits)
        {
            edits ??= new PopulationBakedGraphEdits();
            edits.addedNodes ??= Array.Empty<ServerPopulationRouteNode>();
            edits.removedNodeIds ??= Array.Empty<long>();
            edits.manualOnlyNodeIds ??= Array.Empty<long>();
            edits.movedNodes ??= Array.Empty<PopulationBakedGraphEdits.MovedNode>();
            edits.blockedPairs ??= Array.Empty<PopulationBakedGraphEdits.NodePair>();
            edits.manualEdges ??= Array.Empty<ServerPopulationRouteEdge>();
            return edits;
        }

        internal static PopulationBakedGraphEdits ReadEdits(string path, string mapId, string instanceId)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new PopulationBakedGraphEdits();
            // Do not apply another map's authoring edits, even when filenames collide.
            Layer prior = JsonUtility.FromJson<Layer>(File.ReadAllText(path));
            if (prior == null || prior.formatVersion != 1 ||
                !string.Equals(prior.mapId ?? string.Empty, mapId ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(prior.instanceId ?? string.Empty, instanceId ?? string.Empty, StringComparison.Ordinal))
                throw new InvalidDataException("Existing baked Population route-layer identity does not match this map. Resolve it before rebaking so edits are not lost.");
            return Normalize(prior.editorEdits);
        }

        private static string Pair(long a, long b) => a < b ? a + ":" + b : b + ":" + a;
        private static Vector3 Point(ServerPose p) => new Vector3(p.x, p.y, p.z);
        private static ServerPose WithPosition(ServerPose p, Vector3 xyz) => new ServerPose(xyz.x, xyz.y, xyz.z, p.yaw);
        private static ServerPopulationRouteEdge NewEdge(long a, long b) => new ServerPopulationRouteEdge
        {
            fromNodeId = a, toNodeId = b, oneWay = false,
            width = 2.5f, weight = 1f, minimumClearance = 0.5f, validatedClear = true
        };

        private static bool Connects(ServerPopulationRouteEdge e, long a, long b) =>
            e != null && Pair(e.fromNodeId, e.toNodeId) == Pair(a, b);

        private static bool SafeDirect(Scene scene, ServerCollisionWorld world, Vector3 a, Vector3 b)
        {
            if (world == null || !PopulationRouteWalkabilityGizmos.SegmentTraversable(world, scene, a, b))
                return false;
            // The published geometry determines authority; existing scene colliders
            // additionally reject obvious editor-visible wall and decoration crossings.
            return !Physics.Linecast(a + Vector3.up * 1.25f,
                b + Vector3.up * 1.25f, ~0, QueryTriggerInteraction.Ignore);
        }

        internal static bool ApplyEdits(Scene scene, ServerCollisionWorld world,
            PopulationBakedGraphEdits edits,
            ref ServerPopulationRouteNode[] nodes, ref ServerPopulationRouteEdge[] edges,
            ServerPopulationPortal[] portals)
        {
            edits = Normalize(edits);
            // Most worlds have no manual graph edits. Keep that original bake path
            // unchanged and avoid a second full collision traversal of every edge.
            if (edits.addedNodes.Length == 0 && edits.removedNodeIds.Length == 0 &&
                edits.manualOnlyNodeIds.Length == 0 && edits.movedNodes.Length == 0 &&
                edits.blockedPairs.Length == 0 && edits.manualEdges.Length == 0)
                return true;
            Physics.SyncTransforms();
            var list = new List<ServerPopulationRouteNode>(nodes ?? Array.Empty<ServerPopulationRouteNode>());
            var removed = new HashSet<long>(edits.removedNodeIds ?? Array.Empty<long>());
            var manual = new HashSet<long>(edits.manualOnlyNodeIds ?? Array.Empty<long>());
            var moved = new Dictionary<long, ServerPose>();
            foreach (PopulationBakedGraphEdits.MovedNode item in edits.movedNodes ?? Array.Empty<PopulationBakedGraphEdits.MovedNode>())
                if (item != null && item.id > 0) moved[item.id] = item.pose;

            list.RemoveAll(x => x == null || removed.Contains(x.stableId));
            var known = new HashSet<long>(list.Select(x => x.stableId));
            foreach (ServerPopulationRouteNode node in edits.addedNodes ?? Array.Empty<ServerPopulationRouteNode>())
            {
                if (node == null || node.stableId <= 0 || removed.Contains(node.stableId)) continue;
                if (!known.Add(node.stableId))
                {
                    Debug.LogError($"[Population Graph Bake] Added node ID {node.stableId} collides with a scene marker.");
                    return false;
                }
                list.Add(node);
            }
            foreach (ServerPopulationRouteNode node in list)
                if (moved.TryGetValue(node.stableId, out ServerPose pose)) node.pose = pose;

            var lookup = list.ToDictionary(x => x.stableId);
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (PopulationBakedGraphEdits.NodePair pair in edits.blockedPairs ?? Array.Empty<PopulationBakedGraphEdits.NodePair>())
                if (pair != null) blocked.Add(Pair(pair.a, pair.b));

            bool Valid(long a, long b)
            {
                return lookup.TryGetValue(a, out ServerPopulationRouteNode x) &&
                    lookup.TryGetValue(b, out ServerPopulationRouteNode y) && a != b &&
                    SafeDirect(scene, world, Point(x.pose), Point(y.pose));
            }

            var output = new List<ServerPopulationRouteEdge>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ServerPopulationRouteEdge edge in edges ?? Array.Empty<ServerPopulationRouteEdge>())
            {
                if (edge == null || blocked.Contains(Pair(edge.fromNodeId, edge.toNodeId)) ||
                    manual.Contains(edge.fromNodeId) || manual.Contains(edge.toNodeId) ||
                    !Valid(edge.fromNodeId, edge.toNodeId)) continue;
                string key = edge.oneWay ? "D:" + edge.fromNodeId + ":" + edge.toNodeId :
                    "B:" + Pair(edge.fromNodeId, edge.toNodeId);
                if (keys.Add(key)) output.Add(edge);
            }
            // Saved manual links are revalidated against the freshly baked collision world.
            // Invalid links must be REMOVED, not left in the edit record to fail every bake.
            // edits is the same object subsequently serialized in ServerWorldBake.RouteLayerFile;
            // the atomic routes-layer write persists this cleanup along with the valid graph.
            var validManualEdges = new List<ServerPopulationRouteEdge>();
            var removedManualExamples = new List<string>(3);
            int removedManualCount = 0;
            foreach (ServerPopulationRouteEdge edge in edits.manualEdges ?? Array.Empty<ServerPopulationRouteEdge>())
            {
                if (edge == null || !Valid(edge.fromNodeId, edge.toNodeId))
                {
                    ++removedManualCount;
                    if (edge != null && removedManualExamples.Count < 3)
                        removedManualExamples.Add($"{edge.fromNodeId} -> {edge.toNodeId}");
                    continue;
                }
                validManualEdges.Add(edge);
                output.RemoveAll(e => Connects(e, edge.fromNodeId, edge.toNodeId));
                output.Add(edge);
            }
            // Never overwrite the user's saved manual connections implicitly.
            // Auto Connect refuses invalid manual links instead of pruning them.
            if (removedManualCount > 0)
            {
                Debug.LogError($"[Population Graph Bake] REFUSED: {removedManualCount} saved manual " +
                    "connection(s) failed geometry validation. No manual edit record was changed. " +
                    (removedManualExamples.Count > 0
                        ? "Examples: " + string.Join("; ", removedManualExamples) : string.Empty));
                return false;
            }

            // The prior scene may still contain a spawn whose marker or manual route ID
            // refers to a node removed by the baked editor. Fail before publishing routes.
            foreach (ServerSpawn spawn in UnityEngine.Object.FindObjectsByType<ServerSpawn>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (spawn == null || spawn.gameObject.scene != scene ||
                    !spawn.gameObject.activeInHierarchy || !spawn.enabledForServer) continue;
                long spawnId = spawn.routeMarker != null
                    ? ServerWorldBake.StableAuthoringId(spawn.routeMarker.BakeId)
                    : spawn.routeNodeId;
                if (spawnId > 0 && !lookup.ContainsKey(spawnId))
                {
                    if (spawn.kind == ServerSpawnKind.Population)
                    {
                        // The final spawn-export pass omits invalid Population entries.
                        // A deleted graph node must not prevent unrelated portals baking.
                        Debug.LogWarning($"[Population Graph Bake] Population Spawn '{spawn.name}' " +
                            $"references removed node {spawnId}; it will be omitted from the bake.", spawn);
                        continue;
                    }
                    Debug.LogError($"[Population Graph Bake] Spawn '{spawn.name}' references removed " +
                        $"route node {spawnId}. Assign an existing baked node before rebaking.");
                    return false;
                }
            }
            // Reject edits that delete a route node which a portal still owns.
            foreach (ServerPopulationPortal portal in portals ?? Array.Empty<ServerPopulationPortal>())
            {
                if (portal == null) continue;
                if ((portal.routeNodeId > 0 && !lookup.ContainsKey(portal.routeNodeId)) ||
                    (portal.routeNodeIds != null && portal.routeNodeIds.Any(id => id > 0 && !lookup.ContainsKey(id))))
                {
                    // FilterPopulationPortals runs after graph edits. It will remove
                    // this Portal or stale optional entries before routes/spawns export.
                    Debug.LogWarning($"[Population Graph Bake] Portal '{portal.label}' has a " +
                        "removed graph-node reference. Its invalid entry will be omitted " +
                        "during final Population Portal validation.");
                }
            }
            nodes = list.ToArray();
            edges = output.ToArray();
            return true;
        }

        internal static void Open(Scene target)
        {
            var window = GetWindow<PopulationBakedGraphEditor>("Edit Baked Population Graph");
            window.Load(target);
            window.Show();
            window.Focus();
            SceneView.RepaintAll();
        }

        // Avoid rebaking over unsaved graph edits. Reuse the existing Save workflow.
        internal static bool SaveOpenEditsBeforeBake(Scene target)
        {
            foreach (PopulationBakedGraphEditor editor in Resources.FindObjectsOfTypeAll<PopulationBakedGraphEditor>())
            {
                if (editor == null || editor.layer == null || !editor.dirty ||
                    editor.scene.handle != target.handle) continue;
                if (!EditorUtility.DisplayDialog("Unsaved Baked Graph Changes",
                        "Save your graph edits before building? The bake will preserve saved edits.",
                        "Save & Build", "Cancel"))
                    return false;
                editor.Save();
                if (editor.dirty) return false;
            }
            return true;
        }

        // The Build Client three-step workflow saves through this editor's existing
        // validation and atomic routes-layer write. It does not duplicate graph storage.
        // Never report a successful save without verifying the actual layer on disk.
        // The server reads this file at startup; this operation does not hot-reload GameServer.
        internal static bool SaveOpenGraph(Scene target, out string status)
        {
            foreach (PopulationBakedGraphEditor editor in Resources.FindObjectsOfTypeAll<PopulationBakedGraphEditor>())
            {
                if (editor == null || editor.layer == null || editor.scene.handle != target.handle)
                    continue;
                try
                {
                    editor.SyncSceneNodes(true);
                    if (!editor.dirty)
                    {
                        if (editor.TryVerifyPublishedGraph(out string details))
                        {
                            status = $"SAVE VERIFIED: {details}. BAKE then restart GameServer.";
                            editor.lastSaveSucceeded = true;
                            editor.lastSaveStatus = status;
                            return true;
                        }
                        status = "SAVE BLOCKED: disk graph differs from loaded editor. RELOAD rather than overwrite: " + details;
                        editor.lastSaveSucceeded = false;
                        editor.lastSaveStatus = status;
                        return false;
                    }
                    if (editor.Save())
                    {
                        status = editor.lastSaveStatus;
                        return true;
                    }
                    status = "SAVE FAILED: " + editor.lastError;
                    return false;
                }
                catch (Exception ex)
                {
                    editor.lastError = ex.Message;
                    editor.lastSaveSucceeded = false;
                    editor.lastSaveStatus = "SAVE FAILED: " + ex.Message;
                    Debug.LogError("[Population Baked Graph] " + editor.lastSaveStatus);
                    status = editor.lastSaveStatus;
                    return false;
                }
            }
            status = "SAVE FAILED: Open EDIT first. No graph editor is loaded for this scene.";
            return false;
        }

        internal static bool HasUnsavedOpenGraph(Scene target, out string reason)
        {
            foreach (PopulationBakedGraphEditor editor in Resources.FindObjectsOfTypeAll<PopulationBakedGraphEditor>())
            {
                if (editor == null || editor.layer == null || editor.scene.handle != target.handle)
                    continue;
                try
                {
                    editor.SyncSceneNodes(true);
                    if (editor.dirty)
                    {
                        reason = "The open route graph has unsaved edits. Press SAVE, fix validation errors, then BAKE.";
                        return true;
                    }
                    if (!editor.TryVerifyPublishedGraph(out string details))
                    {
                        reason = "The open graph does not match its saved route layer: " + details + ". Reload or SAVE before BAKE.";
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    reason = "Unable to validate open graph: " + ex.Message;
                    return true;
                }
            }
            reason = string.Empty;
            return false;
        }

        internal static void RefreshOpenEditorsAfterBake(Scene target)
        {
            foreach (PopulationBakedGraphEditor editor in Resources.FindObjectsOfTypeAll<PopulationBakedGraphEditor>())
                if (editor != null && editor.scene.handle == target.handle)
                    editor.Load(target);
        }

        private void OnEnable() => SceneView.duringSceneGui += DrawScene;
        private void OnDisable() => SceneView.duringSceneGui -= DrawScene;

        private void Load(Scene target)
        {
            if (dirty && !EditorUtility.DisplayDialog("Unsaved Graph Changes",
                    "Discard unsaved edits to the current baked graph?", "Discard", "Cancel")) return;
            scene = target;
            // The baked editor owns the visible routes while open; hiding the older
            // auto-preview avoids showing two contradictory sets of connections.
            PopulationRouteGraphBuilder.ClearPreview(scene);
            routesPath = RoutePath(target);
            dirty = false;
            nextSceneSync = 0;
            lastSceneSync = string.Empty;
            sceneMovedNodes.Clear();
            sceneMarkerProblems.Clear();
            importedSceneMarkerCount = 0;
            selectedA = selectedB = hoveredNode = 0;
            moveSelected = false;
            lastError = string.Empty;
            lastSaveStatus = string.Empty;
            lastSaveSucceeded = false;
            loadedDiskText = null;
            layer = null;
            collision = null;
            if (string.IsNullOrEmpty(routesPath) || !File.Exists(routesPath))
            {
                lastError = "No baked *.routes.json exists. Bake Current Scene first.";
                Repaint();
                return;
            }
            try
            {
                loadedDiskText = File.ReadAllText(routesPath);
                layer = JsonUtility.FromJson<Layer>(loadedDiskText);
                if (layer == null || layer.formatVersion != 1)
                    throw new InvalidDataException("Routes layer format is invalid.");
                layer.populationNodes ??= Array.Empty<ServerPopulationRouteNode>();
                layer.populationEdges ??= Array.Empty<ServerPopulationRouteEdge>();
                layer.editorEdits = Normalize(layer.editorEdits);
                ServerMap map = UnityEngine.Object.FindObjectsByType<ServerMap>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(x => x != null && x.gameObject.scene == scene);
                string suffix = string.IsNullOrWhiteSpace(map.InstanceId) ? string.Empty :
                    "__" + ServerMap.NormalizeId(map.InstanceId);
                string corePath = Path.Combine(ServerWorldBake.ServerMapsFolder,
                    ServerMap.NormalizeId(map.MapId) + suffix + ".servermap.json");
                var snapshot = JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(corePath));
                if (snapshot == null || snapshot.formatVersion != ServerMapFormat.Version ||
                    !string.Equals(layer.coreContentHash, snapshot.contentHash, StringComparison.Ordinal))
                    throw new InvalidDataException("The graph is stale relative to baked geometry. Bake Current Scene first.");
                collision = new ServerCollisionWorld(snapshot);
            }
            catch (Exception e) { layer = null; lastError = e.Message; }
            SyncSceneNodes(true);
            Repaint();
            SceneView.RepaintAll();
        }

        private ServerPopulationRouteNode Node(long id) => layer?.populationNodes?.FirstOrDefault(x => x != null && x.stableId == id);

        // A placed PopulationRouteMarker is the authoritative editor position for
        // a scene-backed node. The routes layer remains the editable graph output,
        // but its preview must track Unity prefab/parent Transform movement.
        // Graph-only (Add Node) waypoints retain their own baked position.
        private PopulationRouteMarker FindSceneMarker(long id)
        {
            if (id <= 0) return null;
            PopulationRouteMarker[] matches = UnityEngine.Object.FindObjectsByType<PopulationRouteMarker>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(m => m != null && m.gameObject.scene == scene &&
                    m.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(m.BakeId) &&
                    ServerWorldBake.StableAuthoringId(m.BakeId) == id)
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        // AUTO LOAD CURRENT merges hand-placed scene markers into the existing saved graph.
        // Existing nodes/edges and manual overrides are never regenerated, removed or replaced.
        // New scene-backed markers are deliberately NOT added to editorEdits.addedNodes:
        // AUTO CONNECT's exporter already includes actual PopulationRouteMarker components,
        // and persisting them as graph-only addedNodes would create duplicate IDs on export.
        private void SyncSceneNodes(bool force = false)
        {
            if (layer == null || !scene.IsValid() || !scene.isLoaded) return;
            double now = EditorApplication.timeSinceStartup;
            if (!force && now < nextSceneSync) return;
            nextSceneSync = now + 0.15d;

            layer.editorEdits = Normalize(layer.editorEdits);
            var byId = layer.populationNodes.Where(n => n != null)
                .ToDictionary(n => n.stableId);
            var graphOnlyIds = new HashSet<long>(layer.editorEdits.addedNodes
                .Where(n => n != null).Select(n => n.stableId));
            var explicitlyRemovedIds = new HashSet<long>(layer.editorEdits.removedNodeIds);
            var changed = new List<long>();
            var imported = new List<ServerPopulationRouteNode>();
            sceneMarkerProblems.Clear();

            // Scan the ACTUAL scene, including nested prefab marker instances.
            // Never move, relabel or delete markers during the scan.
            PopulationRouteMarker[] markers = UnityEngine.Object.FindObjectsByType<PopulationRouteMarker>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(m => m != null && m.gameObject.scene == scene && m.gameObject.activeInHierarchy)
                .ToArray();
            var uniqueMarkers = new Dictionary<long, PopulationRouteMarker>();
            var duplicateIds = new HashSet<long>();
            foreach (PopulationRouteMarker marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker.BakeId))
                {
                    sceneMarkerProblems.Add($"Scene marker '{marker.name}' has no stable Bake ID. Select and repair it in Unity.");
                    continue;
                }
                long id = ServerWorldBake.StableAuthoringId(marker.BakeId);
                if (id <= 0)
                {
                    sceneMarkerProblems.Add($"Scene marker '{marker.name}' has invalid Bake ID '{marker.BakeId}'.");
                    continue;
                }
                if (!uniqueMarkers.TryAdd(id, marker))
                    duplicateIds.Add(id);
            }
            foreach (long duplicate in duplicateIds)
            {
                string names = string.Join(", ", markers.Where(m =>
                    !string.IsNullOrWhiteSpace(m.BakeId) &&
                    ServerWorldBake.StableAuthoringId(m.BakeId) == duplicate)
                    .Select(m => m.name).Distinct().Take(4));
                sceneMarkerProblems.Add($"Duplicate Route Marker ID {duplicate}: {names}. Assign a unique Bake ID before SAVE.");
                uniqueMarkers.Remove(duplicate);
            }

            foreach (var entry in uniqueMarkers)
            {
                long id = entry.Key;
                PopulationRouteMarker marker = entry.Value;
                if (explicitlyRemovedIds.Contains(id))
                {
                    // Respect a prior explicit manual removal. Never restore it as a side effect.
                    continue;
                }
                if (graphOnlyIds.Contains(id))
                {
                    sceneMarkerProblems.Add($"Scene marker '{marker.name}' collides with existing graph-only node ID {id}.");
                    continue;
                }
                if (!byId.TryGetValue(id, out ServerPopulationRouteNode node))
                {
                    // Use the same canonical node fields as ServerWorldBake.ExportPopulation.
                    node = new ServerPopulationRouteNode
                    {
                        stableId = id,
                        label = marker.label ?? marker.gameObject.name,
                        pose = new ServerPose(marker.transform.position.x,
                            marker.transform.position.y, marker.transform.position.z,
                            marker.transform.eulerAngles.y),
                        pathWidth = Mathf.Max(0.5f, marker.pathWidth),
                        routeWeight = Mathf.Max(0.01f, marker.routeWeight),
                        nodeType = marker.branchNode
                            ? PopulationNodeType.Intersection : PopulationNodeType.Regular,
                        isDestination = false,
                        destinationTags = PopulationDestinationTag.None,
                        destinationGroup = string.Empty,
                        hardRestricted = false,
                        allowedNpcTypes = PopulationNpcTypeMask.All,
                        minimumWaitSeconds = 0f,
                        maximumWaitSeconds = 0f,
                        actionId = 0,
                    };
                    imported.Add(node);
                    byId.Add(id, node);
                    changed.Add(id);
                    continue;
                }

                Vector3 current = marker.transform.position;
                if ((Point(node.pose) - current).sqrMagnitude > 0.0001f)
                {
                    node.pose = WithPosition(node.pose, current);
                    changed.Add(id);
                }
                // The scene owns the authoring metadata for prefab-backed markers.
                // Do not override existing saved edge settings or graph-only nodes.
                string sceneLabel = marker.label ?? marker.gameObject.name;
                float width = Mathf.Max(0.5f, marker.pathWidth);
                float weight = Mathf.Max(0.01f, marker.routeWeight);
                PopulationNodeType nodeType = marker.branchNode
                    ? PopulationNodeType.Intersection : PopulationNodeType.Regular;
                if (node.label != sceneLabel || node.pathWidth != width ||
                    node.routeWeight != weight || node.nodeType != nodeType)
                {
                    node.label = sceneLabel;
                    node.pathWidth = width;
                    node.routeWeight = weight;
                    node.nodeType = nodeType;
                    if (!changed.Contains(id)) changed.Add(id);
                }
                // Scene transforms override old graph-only position overrides for this ID.
                int before = layer.editorEdits.movedNodes.Length;
                layer.editorEdits.movedNodes = layer.editorEdits.movedNodes
                    .Where(m => m != null && m.id != id).ToArray();
                if (before != layer.editorEdits.movedNodes.Length && !changed.Contains(id))
                    changed.Add(id);
            }

            if (imported.Count > 0)
            {
                layer.populationNodes = layer.populationNodes.Concat(imported).ToArray();
                importedSceneMarkerCount += imported.Count;
            }
            if (changed.Count == 0) return;
            foreach (long id in changed) sceneMovedNodes.Add(id);
            lastSceneSync = imported.Count > 0
                ? $"AUTO LOAD CURRENT: imported {imported.Count} new hand-placed Route Marker(s), " +
                  $"graph now contains {layer.populationNodes.Length} nodes. " +
                  "Select A and B to connect them; VALIDATE and SAVE when ready."
                : $"Synced {changed.Count} existing scene Route Marker(s). " +
                  "Validate links before SAVE; nothing was disconnected.";
            Modified();
        }
        // Manual repair only: Unity prefab duplication can clone a marker's hidden Bake ID.
        // Never silently reassign authored identities while merely loading or validating.
        private void AssignNewIdToSelectedSceneMarker()
        {
            PopulationRouteMarker marker = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<PopulationRouteMarker>() : null;
            if (marker == null || marker.gameObject.scene != scene)
            {
                EditorUtility.DisplayDialog("Select a Route Marker",
                    "Select the newly placed Route Marker GameObject in the Unity Hierarchy first.", "OK");
                return;
            }
            long oldId = string.IsNullOrWhiteSpace(marker.BakeId) ? 0L :
                ServerWorldBake.StableAuthoringId(marker.BakeId);
            int copies = UnityEngine.Object.FindObjectsByType<PopulationRouteMarker>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(m => m != null && m.gameObject.scene == scene &&
                    m.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(m.BakeId) &&
                    ServerWorldBake.StableAuthoringId(m.BakeId) == oldId);
            if (oldId > 0 && copies < 2)
            {
                EditorUtility.DisplayDialog("Stable ID Is Already Unique",
                    "Only missing or duplicated Route Marker IDs need repair. " +
                    "Changing a valid existing ID would break saved connections.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Assign Unique Route Marker ID",
                    $"Give selected scene marker '{marker.name}' a NEW stable ID? " +
                    "Only this selected marker will change. Existing saved connections are untouched. " +
                    "Use this on the NEW duplicate, not on the original saved marker.",
                    "Assign New ID", "Cancel")) return;
            Undo.RecordObject(marker, "Assign Unique Route Marker ID");
            marker.RegenerateBakeId();
            if (PrefabUtility.IsPartOfPrefabInstance(marker))
                PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
            EditorUtility.SetDirty(marker);
            EditorSceneManager.MarkSceneDirty(scene);
            SyncSceneNodes(true);
            SceneView.RepaintAll();
        }

        private string Label(long id) => Node(id) is ServerPopulationRouteNode n ? n.label + " (" + id + ")" : "None";

        // Node numbers correspond to the saved layer's array, not to prefab hierarchy order.
        // Display the full stable ID as well: node numbers may change after regeneration.
        private int Number(long id) => layer?.populationNodes == null ? 0 :
            Array.FindIndex(layer.populationNodes, n => n != null && n.stableId == id) + 1;

        private string NodeSummary(long id)
        {
            ServerPopulationRouteNode node = Node(id);
            if (node == null) return "None";
            Vector3 position = Point(node.pose);
            return $"#{Number(id)}  {node.label}  [{id}]  ({position.x:0.0}, {position.y:0.0}, {position.z:0.0})";
        }

        private int ConnectionCount(long id) => layer?.populationEdges == null ? 0 :
            layer.populationEdges.Count(e => e != null &&
                (e.fromNodeId == id || e.toNodeId == id));

        private void FrameNode(long id)
        {
            ServerPopulationRouteNode node = Node(id);
            SceneView view = SceneView.lastActiveSceneView;
            if (node == null || view == null) return;
            view.Frame(new Bounds(Point(node.pose), Vector3.one * 12f), false);
            view.Repaint();
        }

        private void FramePair()
        {
            ServerPopulationRouteNode a = Node(selectedA), b = Node(selectedB);
            SceneView view = SceneView.lastActiveSceneView;
            if (a == null || b == null || view == null) return;
            var bounds = new Bounds(Point(a.pose), Vector3.zero);
            bounds.Encapsulate(Point(b.pose));
            bounds.Expand(8f);
            view.Frame(bounds, false);
            view.Repaint();
        }

        private void Modified()
        {
            dirty = true;
            lastSaveSucceeded = false;
            lastSaveStatus = "UNSAVED CHANGES — SAVE and BAKE required.";
            Repaint();
            SceneView.RepaintAll();
        }

        private bool DirectlyWalkable(long a, long b, out string reason)
        {
            ServerPopulationRouteNode na = Node(a), nb = Node(b);
            if (na == null || nb == null || collision == null)
            {
                reason = "One or both nodes (or baked collision data) are unavailable.";
                return false;
            }
            Vector3 from = Point(na.pose), to = Point(nb.pose);
            if (!PopulationRouteWalkabilityGizmos.SegmentTraversable(collision, scene, from, to))
            {
                reason = "The direct segment failed baked-ground, slope, step, or clearance validation.";
                return false;
            }
            if (Physics.Linecast(from + Vector3.up * 1.25f,
                to + Vector3.up * 1.25f, out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore))
            {
                reason = "The direct segment intersects scene collider '" +
                    (hit.collider != null ? hit.collider.name : "unknown") + "'.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        // Each action is one independent A -> B pair. After Pair or Clear, both
        // selections reset so the very next click always starts a NEW A.
        private void Choose(long id)
        {
            if (selectedA == 0)
            {
                selectedA = id;
                selectedB = 0;
            }
            else if (selectedA == id)
                selectedB = 0;
            else if (selectedB == id)
                selectedB = 0;
            else
                selectedB = id;
            moveSelected = false;
            // Retain the last operation result while picking the next nodes so
            // a rejected Pair is not immediately hidden by the next click.
            Repaint(); SceneView.RepaintAll();
        }

        private void NewPair()
        {
            selectedA = selectedB = hoveredNode = 0;
            moveSelected = false;
            lastPairResult = string.Empty;
            lastPairMessageType = MessageType.None;
            lastPairedA = lastPairedB = 0;
            lastPairAdded = false;
            Repaint(); SceneView.RepaintAll();
        }

        private void FinishPairAction(string summary, MessageType type = MessageType.Info,
            long a = 0, long b = 0, bool showAddedLink = false)
        {
            // Each operation terminates the pair. Never leave A locked/yellow.
            NewPair();
            lastPairResult = summary;
            lastPairMessageType = type;
            lastPairedA = a;
            lastPairedB = b;
            lastPairAdded = showAddedLink;
            Repaint(); SceneView.RepaintAll();
        }

        private bool SelectedPairConnected() => selectedA > 0 && selectedB > 0 &&
            layer?.populationEdges != null &&
            layer.populationEdges.Any(e => Connects(e, selectedA, selectedB));

        private void ClearA()
        {
            if (selectedA == 0) return;
            var e = layer.editorEdits;
            e.manualOnlyNodeIds = e.manualOnlyNodeIds.Concat(new[] { selectedA }).Distinct().ToArray();
            e.manualEdges = e.manualEdges.Where(x => x != null && x.fromNodeId != selectedA && x.toNodeId != selectedA).ToArray();
            layer.populationEdges = layer.populationEdges.Where(x => x != null && x.fromNodeId != selectedA && x.toNodeId != selectedA).ToArray();
            Modified();
        }

        private void RestoreA()
        {
            if (selectedA == 0) return;
            layer.editorEdits.manualOnlyNodeIds = layer.editorEdits.manualOnlyNodeIds.Where(x => x != selectedA).ToArray();
            Modified();
            // Reconnects automatically on next Bake Current Scene. Does not invent unvalidated edges here.
        }

        private void Connect()
        {
            if (Node(selectedA) == null || Node(selectedB) == null || selectedA == selectedB)
            {
                FinishPairAction("Invalid or stale A/B selection. Click a new A, then B.");
                return;
            }

            long a = selectedA, b = selectedB;
            // Idempotent: NEVER rewrite an existing edge as a manual override.
            // This preserves any authored width, weight or one-way settings.
            if (SelectedPairConnected())
            {
                FinishPairAction($"ALREADY CONNECTED: #{Number(a)} + #{Number(b)} (unchanged).", MessageType.Info, a, b, true);
                return;
            }
            if (!DirectlyWalkable(a, b, out string reason))
            {
                // A rejected Pair is still the END of this A+B operation. In the
                // old editor this branch left A locked, so the next scene click
                // replaced B and the editor appeared to stop pairing new nodes.
                // Keep collision authority intact, but always unlock the next pair.
                FinishPairAction($"REJECTED: #{Number(a)} + #{Number(b)}. {reason} " +
                    "Use an intermediate waypoint.", MessageType.Warning, a, b);
                return;
            }
            var edits = layer.editorEdits;
            string key = Pair(a, b);
            edits.blockedPairs = edits.blockedPairs.Where(x => x != null && Pair(x.a, x.b) != key).ToArray();
            edits.manualEdges = edits.manualEdges.Where(x => !Connects(x, a, b))
                .Concat(new[] { NewEdge(a, b) }).ToArray();
            layer.populationEdges = layer.populationEdges.Where(x => !Connects(x, a, b))
                .Concat(new[] { NewEdge(a, b) }).ToArray();
            Modified();
            FinishPairAction($"PAIRED: #{Number(a)} + #{Number(b)}. New link added; select the next A.", MessageType.Info, a, b, true);
        }

        private void Disconnect()
        {
            if (Node(selectedA) == null || Node(selectedB) == null || selectedA == selectedB)
            {
                FinishPairAction("Invalid or stale A/B selection. Click a new A, then B.");
                return;
            }

            long a = selectedA, b = selectedB;
            string key = Pair(a, b);
            bool hadConnection = SelectedPairConnected();
            var edits = layer.editorEdits;
            bool hadManualEdge = edits.manualEdges.Any(x => Connects(x, a, b));
            bool alreadyBlocked = edits.blockedPairs.Any(x => x != null && Pair(x.a, x.b) == key);
            edits.manualEdges = edits.manualEdges.Where(x => !Connects(x, a, b)).ToArray();
            if (!alreadyBlocked)
                edits.blockedPairs = edits.blockedPairs.Concat(new[] {
                    new PopulationBakedGraphEdits.NodePair { a = a, b = b }
                }).ToArray();
            layer.populationEdges = layer.populationEdges.Where(x => !Connects(x, a, b)).ToArray();
            // Persist the block even when A+B currently have no edge: a later World
            // Bake must not automatically reconnect a deliberately cleared pair.
            if (hadConnection || hadManualEdge || !alreadyBlocked)
                Modified();
            FinishPairAction(hadConnection
                ? $"LINK CLEARED: #{Number(a)} - #{Number(b)}. Select the next A."
                : alreadyBlocked
                    ? $"ALREADY CLEARED: #{Number(a)} - #{Number(b)} (unchanged)."
                    : $"NO LINK: #{Number(a)} - #{Number(b)}. Automatic reconnection blocked.",
                MessageType.Info, a, b);
        }

        private void AddNode()
        {
            Vector3 position = SceneView.lastActiveSceneView != null ?
                SceneView.lastActiveSceneView.pivot : Vector3.zero;
            long id;
            do { id = ServerWorldBake.StableAuthoringId("baked-graph:" + Guid.NewGuid().ToString("N")); }
            while (id <= 0 || Node(id) != null);
            var node = new ServerPopulationRouteNode
            {
                stableId = id,
                label = "Custom Waypoint",
                pose = new ServerPose(position.x, position.y, position.z, 0),
                pathWidth = 2.5f,
                routeWeight = 1f,
                nodeType = PopulationNodeType.Regular,
                allowedNpcTypes = PopulationNpcTypeMask.All,
                destinationTags = PopulationDestinationTag.None
            };
            layer.populationNodes = layer.populationNodes.Concat(new[] { node }).ToArray();
            layer.editorEdits.addedNodes = layer.editorEdits.addedNodes.Concat(new[] { node }).ToArray();
            selectedA = id; selectedB = 0; moveSelected = true;
            Modified();
        }

        private void MoveNode(long id, Vector3 position)
        {
            ServerPopulationRouteNode node = Node(id);
            if (node == null) return;
            node.pose = WithPosition(node.pose, position);
            var edits = layer.editorEdits;
            ServerPopulationRouteNode added = edits.addedNodes.FirstOrDefault(x => x != null && x.stableId == id);
            if (added != null)
            {
                added.pose = node.pose;
            }
            else
            {
                PopulationRouteMarker marker = FindSceneMarker(id);
                if (marker != null)
                {
                    // A prefab/scene marker owns its actual position. Do not persist
                    // an overriding graph-only position that will hide later moves.
                    Undo.RecordObject(marker.transform, "Move Population Route Marker");
                    marker.transform.position = position;
                    if (PrefabUtility.IsPartOfPrefabInstance(marker.transform))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(marker.transform);
                    EditorUtility.SetDirty(marker.transform);
                    EditorSceneManager.MarkSceneDirty(scene);
                    edits.movedNodes = edits.movedNodes.Where(x => x != null && x.id != id).ToArray();
                }
                else
                {
                    // A baked node without a scene marker keeps the existing saved
                    // position-override contract.
                    PopulationBakedGraphEdits.MovedNode old = edits.movedNodes
                        .FirstOrDefault(x => x != null && x.id == id);
                    if (old == null)
                        edits.movedNodes = edits.movedNodes.Concat(new[] {
                            new PopulationBakedGraphEdits.MovedNode { id = id, pose = node.pose }
                        }).ToArray();
                    else old.pose = node.pose;
                }
            }
            // Never clear every link when a node moves. Preserve the topology;
            // Save checks the new positions and removes only unsafe connections.
            lastSceneSync = "Node moved. Existing links retained until validation on SAVE GRAPH.";
            Modified();
        }

        private void AssignAtoSelectedSpawn()
        {
            if (selectedA <= 0 || Node(selectedA) == null) return;
            ServerSpawn spawn = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<ServerSpawn>() : null;
            if (spawn == null || spawn.gameObject.scene != scene)
            {
                EditorUtility.DisplayDialog("Select Spawn",
                    "Select a ServerSpawn in the Unity Hierarchy, then click Assign A to Selected Spawn.", "OK");
                return;
            }
            if (spawn.populationPortal != null || spawn.portalId > 0)
            {
                EditorUtility.DisplayDialog("Portal-Owned Spawn",
                    "This spawn belongs to a Population Portal. Set the portal entry route separately; " +
                    "a direct spawn override would be superseded by portal authority.", "OK");
                return;
            }
            Undo.RecordObject(spawn, "Assign Baked Population Route Node to Spawn");
            spawn.routeMarker = null;
            spawn.routeNodeId = selectedA;
            EditorUtility.SetDirty(spawn);
            if (PrefabUtility.IsPartOfPrefabInstance(spawn))
                PrefabUtility.RecordPrefabInstancePropertyModifications(spawn);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[Population Baked Graph] Assigned routeNodeId={selectedA} to ServerSpawn '{spawn.name}'.");
        }

        private void RemoveNode()
        {
            if (selectedA == 0) return;
            ServerPopulationRouteNode node = Node(selectedA);
            if (node == null) return;
            if (layer.populationPortals != null && layer.populationPortals.Any(p => p != null &&
                (p.routeNodeId == selectedA || (p.routeNodeIds != null && p.routeNodeIds.Contains(selectedA)))))
            {
                EditorUtility.DisplayDialog("Node Is Used By Portal",
                    "A portal references this node. Reassign the portal entry before removing it.", "OK");
                return;
            }
            foreach (ServerSpawn spawn in UnityEngine.Object.FindObjectsByType<ServerSpawn>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (spawn == null || spawn.gameObject.scene != scene ||
                    !spawn.gameObject.activeInHierarchy || !spawn.enabledForServer) continue;
                long spawnId = spawn.routeMarker != null
                    ? ServerWorldBake.StableAuthoringId(spawn.routeMarker.BakeId)
                    : spawn.routeNodeId;
                if (spawnId == selectedA)
                {
                    EditorUtility.DisplayDialog("Node Is Used By Spawn",
                        "A ServerSpawn references this node. Reassign that spawn before removing it.", "OK");
                    return;
                }
            }
            if (!EditorUtility.DisplayDialog("Remove Baked Population Node",
                "Remove this node and every edge connected to it? This will persist through World Bake.", "Remove", "Cancel")) return;
            var e = layer.editorEdits;
            e.addedNodes = e.addedNodes.Where(n => n != null && n.stableId != selectedA).ToArray();
            e.movedNodes = e.movedNodes.Where(n => n != null && n.id != selectedA).ToArray();
            e.manualOnlyNodeIds = e.manualOnlyNodeIds.Where(id => id != selectedA).ToArray();
            e.blockedPairs = e.blockedPairs.Where(x => x != null && x.a != selectedA && x.b != selectedA).ToArray();
            e.manualEdges = e.manualEdges.Where(x => x != null && x.fromNodeId != selectedA && x.toNodeId != selectedA).ToArray();
            if (!e.removedNodeIds.Contains(selectedA))
                e.removedNodeIds = e.removedNodeIds.Concat(new[] { selectedA }).ToArray();
            layer.populationNodes = layer.populationNodes.Where(n => n != null && n.stableId != selectedA).ToArray();
            layer.populationEdges = layer.populationEdges.Where(x => x != null && x.fromNodeId != selectedA && x.toNodeId != selectedA).ToArray();
            selectedA = selectedB = 0;
            Modified();
        }

        private void OnGUI()
        {
            if (layer != null) SyncSceneNodes();
            if (layer == null)
            {
                EditorGUILayout.HelpBox(string.IsNullOrEmpty(lastError) ?
                    "Open this editor from Tools/MMO/Build Client after baking." : lastError,
                    MessageType.Warning);
                if (GUILayout.Button("Reload Baked Graph")) Load(SceneManager.GetActiveScene());
                return;
            }

            EditorGUILayout.LabelField("Baked Population Graph", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Nodes: {layer.populationNodes.Length} | Edges: {layer.populationEdges.Length} " +
                $"| Manual links: {layer.editorEdits.manualEdges.Length} | Blocked pairs: {layer.editorEdits.blockedPairs.Length}");
            if (importedSceneMarkerCount > 0)
                EditorGUILayout.HelpBox($"{importedSceneMarkerCount} hand-placed scene Route Marker(s) added to the EDIT workspace. " +
                    "They are not saved until you connect/validate them and press SAVE.", MessageType.Info);
            foreach (string problem in sceneMarkerProblems.Take(12))
                EditorGUILayout.HelpBox(problem, MessageType.Error);
            if (sceneMarkerProblems.Count > 12)
                EditorGUILayout.HelpBox($"{sceneMarkerProblems.Count - 12} additional marker identity error(s).", MessageType.Error);
            if (sceneMarkerProblems.Count > 0 &&
                GUILayout.Button("Assign New ID to Selected NEW Route Marker"))
                AssignNewIdToSelectedSceneMarker();
            EditorGUILayout.HelpBox("Click A, click B, then PAIR or CLEAR LINK. " +
                "Both selections reset after every action, including an already-connected pair. " +
                "Yellow = A, magenta = B. SAVE GRAPH to persist edits in *.routes.json.", MessageType.Info);
            if (!string.IsNullOrEmpty(lastPairResult))
                EditorGUILayout.HelpBox(lastPairResult, lastPairMessageType);
            if (!string.IsNullOrEmpty(lastSceneSync))
                EditorGUILayout.HelpBox(lastSceneSync, MessageType.Info);
            if (dirty)
                EditorGUILayout.HelpBox("Unsaved changes — GameServer still uses the previous graph.", MessageType.Warning);
            if (!string.IsNullOrWhiteSpace(lastSaveStatus))
                EditorGUILayout.HelpBox(lastSaveStatus,
                    lastSaveSucceeded ? MessageType.None : MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Node at Scene Focus")) AddNode();
                if (GUILayout.Button("Reload / Discard Edits")) Load(scene);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene Visibility", EditorStyles.boldLabel);
            bool numbers = EditorGUILayout.ToggleLeft("Show node numbers beside every marker", showNodeNumbers);
            bool names = EditorGUILayout.ToggleLeft("Show full labels for every marker (can be crowded)", showAllNodeLabels);
            bool faded = EditorGUILayout.ToggleLeft("Fade unrelated edges when a node is selected", fadeUnselectedEdges);
            if (numbers != showNodeNumbers || names != showAllNodeLabels || faded != fadeUnselectedEdges)
            {
                showNodeNumbers = numbers;
                showAllNodeLabels = names;
                fadeUnselectedEdges = faded;
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Selected Nodes", EditorStyles.boldLabel);
            if (selectedA > 0)
            {
                EditorGUILayout.HelpBox($"A  {NodeSummary(selectedA)}\n{ConnectionCount(selectedA)} connection(s)", MessageType.None);
                if (layer.editorEdits.manualOnlyNodeIds.Contains(selectedA))
                    EditorGUILayout.HelpBox("This node is MANUAL-ONLY. Auto Connect will not create links for it. " +
                        "Use Restore Auto for A and rerun Auto Connect if you want automatic links back.", MessageType.Warning);
            }
            else
                EditorGUILayout.LabelField("A: Click a node in the Scene view or the list below.");
            if (selectedB > 0)
                EditorGUILayout.HelpBox($"B  {NodeSummary(selectedB)}\n{ConnectionCount(selectedB)} connection(s)", MessageType.None);
            else
                EditorGUILayout.LabelField("B: Click a second node to select its partner.");

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = Node(selectedA) != null;
                if (GUILayout.Button("Frame A in Scene")) FrameNode(selectedA);
                GUI.enabled = Node(selectedB) != null;
                if (GUILayout.Button("Frame B in Scene")) FrameNode(selectedB);
                GUI.enabled = Node(selectedA) != null && Node(selectedB) != null;
                if (GUILayout.Button("Frame A + B")) FramePair();
                GUI.enabled = true;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("New Pair (Clear Selection)")) NewPair();
                GUI.enabled = selectedA > 0;
                if (GUILayout.Button(moveSelected ? "Stop Moving A" : "Move Node A"))
                {
                    moveSelected = !moveSelected;
                    FrameNode(selectedA);
                    SceneView.RepaintAll();
                }
                GUI.enabled = true;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = selectedA > 0;
                if (GUILayout.Button("Clear Node A Links")) ClearA();
                if (GUILayout.Button("Restore Auto for A")) RestoreA();
                if (GUILayout.Button("Remove Node A")) RemoveNode();
                GUI.enabled = true;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = Node(selectedA) != null && Node(selectedB) != null;
                if (GUILayout.Button("PAIR A + B", GUILayout.Height(32))) Connect();
                if (GUILayout.Button("CLEAR A-B LINK", GUILayout.Height(32))) Disconnect();
                GUI.enabled = true;
            }
            if (selectedA != 0 && selectedB != 0)
                EditorGUILayout.LabelField(SelectedPairConnected() ?
                    "A + B are currently CONNECTED." : "A + B are currently DISCONNECTED.",
                    EditorStyles.boldLabel);
            GUI.enabled = selectedA > 0;
            if (GUILayout.Button("Assign A to Selected ServerSpawn")) AssignAtoSelectedSpawn();
            GUI.enabled = true;

            if (selectedA > 0 && Node(selectedA) != null)
            {
                ServerPopulationRouteNode n = Node(selectedA);
                bool bakedOnly = layer.editorEdits.addedNodes != null &&
                    layer.editorEdits.addedNodes.Any(x => x != null && x.stableId == n.stableId);
                GUI.enabled = bakedOnly;
                string label = EditorGUILayout.TextField("Custom Node Label", n.label ?? string.Empty);
                GUI.enabled = true;
                if (bakedOnly && label != n.label)
                {
                    n.label = label;
                    ServerPopulationRouteNode added = layer.editorEdits.addedNodes.FirstOrDefault(x =>
                        x != null && x.stableId == n.stableId);
                    if (added != null) added.label = label;
                    Modified();
                }
            }

            // This adjacency list makes the selected physical junction explicit; click any
            // neighbor to locate it, or use it as B for Connect / Disconnect.
            if (selectedA > 0 && Node(selectedA) != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Connections From A ({ConnectionCount(selectedA)})", EditorStyles.boldLabel);
                ServerPopulationRouteEdge[] connected = layer.populationEdges.Where(e => e != null &&
                    (e.fromNodeId == selectedA || e.toNodeId == selectedA)).ToArray();
                if (connected.Length == 0)
                    EditorGUILayout.HelpBox("This node has no connections. Select another node as B to connect it.",
                        MessageType.None);
                foreach (ServerPopulationRouteEdge edge in connected)
                {
                    long other = edge.fromNodeId == selectedA ? edge.toNodeId : edge.fromNodeId;
                    if (Node(other) == null) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"#{Number(other)}  {Node(other).label}", GUILayout.MinWidth(100));
                        if (GUILayout.Button("Select B", GUILayout.Width(72)))
                        {
                            selectedB = other; SceneView.RepaintAll();
                        }
                        if (GUILayout.Button("Frame", GUILayout.Width(54))) FrameNode(other);
                        if (GUILayout.Button("Disconnect", GUILayout.Width(88)))
                        {
                            selectedB = other;
                            Disconnect();
                        }
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = dirty;
                if (GUILayout.Button("Save Baked Graph", GUILayout.Height(30))) Save();
                GUI.enabled = true;
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Locate a Node", EditorStyles.boldLabel);
            nodeSearch = EditorGUILayout.TextField("Find name, ID or #", nodeSearch);
            EditorGUILayout.HelpBox("Clicking a row selects and frames that node in the Scene view. " +
                "The node's number and world position distinguish identical 'Route Marker' labels.", MessageType.None);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(100));
            for (int i = 0; i < layer.populationNodes.Length; ++i)
            {
                ServerPopulationRouteNode node = layer.populationNodes[i];
                if (node == null) continue;
                Vector3 p = Point(node.pose);
                string name = $"#{i + 1}  {node.label}   X:{p.x:0.0} Y:{p.y:0.0} Z:{p.z:0.0}";
                if (!string.IsNullOrWhiteSpace(nodeSearch) &&
                    name.IndexOf(nodeSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    node.stableId.ToString().IndexOf(nodeSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                string prefix = node.stableId == selectedA ? "[A] " :
                    node.stableId == selectedB ? "[B] " : "";
                if (GUILayout.Button(prefix + name, EditorStyles.miniButton))
                {
                    Choose(node.stableId);
                    FrameNode(node.stableId);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private bool TryVerifyPublishedGraph(out string details)
        {
            details = "Graph layer missing or not loaded";
            if (layer == null || string.IsNullOrWhiteSpace(routesPath) || !File.Exists(routesPath))
                return false;
            try
            {
                Layer disk = JsonUtility.FromJson<Layer>(File.ReadAllText(routesPath));
                if (disk == null || disk.formatVersion != 1 ||
                    disk.populationNodes == null || disk.populationEdges == null ||
                    !string.Equals(disk.mapId, layer.mapId, StringComparison.Ordinal) ||
                    !string.Equals(disk.instanceId ?? string.Empty,
                        layer.instanceId ?? string.Empty, StringComparison.Ordinal) ||
                    !string.Equals(disk.coreContentHash, layer.coreContentHash, StringComparison.Ordinal))
                {
                    details = "Route layer header / map identity / core hash mismatch";
                    return false;
                }
                // Canonical serialization catches node poses, IDs, directional links,
                // manual overrides, blocked pairs, and any future serialized fields.
                if (!string.Equals(JsonUtility.ToJson(disk, false),
                    JsonUtility.ToJson(layer, false), StringComparison.Ordinal))
                {
                    details = $"Disk contains {disk.populationNodes.Length} nodes, " +
                        $"{disk.populationEdges.Length} edges but differs from EDIT workspace";
                    return false;
                }
                details = $"{disk.populationNodes.Length} nodes, " +
                    $"{disk.populationEdges.Length} edges, " +
                    $"{(disk.editorEdits?.manualEdges?.Length ?? 0)} manual edges on disk";
                return true;
            }
            catch (Exception ex)
            {
                details = ex.Message;
                return false;
            }
        }

        private bool Save()
        {
            if (layer == null) return false;
            string temporary = null;
            string backup = null;
            bool published = false;
            try
            {
                SyncSceneNodes(true);
                if (sceneMarkerProblems.Count > 0)
                    throw new InvalidDataException("Graph SAVE refused: " +
                        string.Join(" | ", sceneMarkerProblems.Take(4)) +
                        ". No saved routes were changed.");
                if (layer.populationNodes == null || layer.populationEdges == null ||
                    layer.editorEdits == null)
                    throw new InvalidDataException("Route graph contains missing node, edge, or edit arrays.");
                var refs = new HashSet<long>(layer.populationNodes.Where(x => x != null).Select(x => x.stableId));
                if (layer.populationNodes.Any(x => x == null) ||
                    refs.Count != layer.populationNodes.Length || refs.Any(x => x <= 0))
                    throw new InvalidDataException("Duplicate, null, or invalid node ID.");
                foreach (ServerPopulationRouteNode node in layer.populationNodes)
                {
                    Vector3 pos = Point(node.pose);
                    if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) ||
                        float.IsInfinity(pos.x) || float.IsInfinity(pos.y) || float.IsInfinity(pos.z))
                        throw new InvalidDataException("Invalid node position: " + node.stableId);
                }
                var changedIds = new HashSet<long>(layer.editorEdits.addedNodes
                    .Where(x => x != null).Select(x => x.stableId));
                foreach (PopulationBakedGraphEdits.MovedNode moved in layer.editorEdits.movedNodes)
                    if (moved != null) changedIds.Add(moved.id);
                changedIds.UnionWith(sceneMovedNodes);
                foreach (ServerPopulationRouteNode node in layer.populationNodes)
                {
                    if (changedIds.Contains(node.stableId) &&
                        !PopulationRouteWalkabilityGizmos.SegmentTraversable(
                            collision, scene, Point(node.pose), Point(node.pose)))
                        throw new InvalidDataException("Route Node '" + node.label + "' (" +
                            node.stableId + ") is not grounded on a safe walkable surface.");
                }
                Physics.SyncTransforms();
                var invalidConnections = new List<string>();
                foreach (ServerPopulationRouteEdge edge in layer.populationEdges)
                {
                    if (edge == null)
                    {
                        invalidConnections.Add("Null saved connection");
                        continue;
                    }
                    if (!refs.Contains(edge.fromNodeId) || !refs.Contains(edge.toNodeId))
                    {
                        invalidConnections.Add($"{edge.fromNodeId} -> {edge.toNodeId}: missing node");
                        continue;
                    }
                    if (!DirectlyWalkable(edge.fromNodeId, edge.toNodeId, out string why))
                        invalidConnections.Add($"{edge.fromNodeId} -> {edge.toNodeId}: {why}");
                }
                if (invalidConnections.Count != 0)
                    throw new InvalidDataException($"SAVE blocked: {invalidConnections.Count} invalid connection(s). " +
                        string.Join(" | ", invalidConnections.Take(8)) +
                        ". Fix them using EDIT; no saved links were modified.");
                if (!File.Exists(routesPath))
                    throw new FileNotFoundException("Saved routes layer missing. Recover existing layer before SAVE.", routesPath);
                if (loadedDiskText == null || !string.Equals(
                        File.ReadAllText(routesPath), loadedDiskText, StringComparison.Ordinal))
                    throw new InvalidDataException("The route file changed since EDIT was loaded. RELOAD the graph; do not overwrite newer saved data.");
                string corePath = routesPath.Substring(0,
                    routesPath.Length - ".routes.json".Length) + ".servermap.json";
                if (!File.Exists(corePath))
                    throw new FileNotFoundException("Server core map missing; do not save over an orphaned route layer.", corePath);
                ServerMapSnapshot core = JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(corePath));
                if (core == null || !string.Equals(core.contentHash,
                    layer.coreContentHash, StringComparison.Ordinal))
                    throw new InvalidDataException("World geometry hash mismatch. Restore the matching map/layer before SAVING.");

                // Hand-placed scene markers/positions are part of authoring. Save the scene
                // first; a .routes.json edit must not depend on unsaved scene objects.
                if (scene.isDirty && !EditorSceneManager.SaveScene(scene))
                    throw new IOException("Could not save the Unity scene. Route layer remains unchanged.");

                string json = JsonUtility.ToJson(layer, true);
                string token = Guid.NewGuid().ToString("N");
                temporary = routesPath + "." + token + ".tmp";
                backup = routesPath + "." + token + ".previous";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                Layer staged = JsonUtility.FromJson<Layer>(File.ReadAllText(temporary));
                if (staged == null || !string.Equals(
                    JsonUtility.ToJson(staged, false), JsonUtility.ToJson(layer, false), StringComparison.Ordinal))
                    throw new InvalidDataException("Staged routes layer did not round-trip correctly. Nothing published.");

                // Atomic replace with a retained rollback copy until verification succeeds.
                File.Replace(temporary, routesPath, backup);
                temporary = null;
                published = true;
                if (!TryVerifyPublishedGraph(out string details))
                    throw new InvalidDataException("Published layer verification failed: " + details);

                if (File.Exists(backup)) File.Delete(backup);
                backup = null;
                loadedDiskText = File.ReadAllText(routesPath);
                dirty = false;
                sceneMovedNodes.Clear();
                lastError = string.Empty;
                lastSaveSucceeded = true;
                lastSaveStatus = $"SAVE VERIFIED: {details}. BAKE then restart GameServer.";
                Debug.Log($"[Population Baked Graph] {lastSaveStatus} Path: {routesPath}");
                Repaint();
                return true;
            }
            catch (Exception ex)
            {
                if (published && backup != null && File.Exists(backup))
                {
                    try
                    {
                        File.Copy(backup, routesPath, true);
                        Debug.LogError("[Population Baked Graph] Restored previous routes layer after failed verification.");
                    }
                    catch (Exception rollbackEx)
                    {
                        Debug.LogError("[Population Baked Graph] CRITICAL rollback failed: " + rollbackEx);
                    }
                }
                lastSaveSucceeded = false;
                lastError = ex.Message;
                lastSaveStatus = "SAVE FAILED: " + ex.Message;
                Debug.LogError("[Population Baked Graph] " + lastSaveStatus);
                EditorUtility.DisplayDialog("Baked Graph Not Saved", ex.Message, "OK");
                Repaint();
                return false;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex) { Debug.LogWarning("[Population Baked Graph] Temp cleanup: " + ex.Message); }
                }
            }
        }

        private void DrawScene(SceneView view)
        {
            if (layer == null || scene != SceneManager.GetActiveScene() || view?.camera == null) return;
            SyncSceneNodes();
            ServerPopulationRouteNode[] nodes = layer.populationNodes;
            var lookup = nodes.Where(n => n != null).ToDictionary(n => n.stableId);
            Vector3 lift = Vector3.up * 0.22f; // visual offset only; baked positions remain unchanged
            Color automaticEdge = new Color(0.20f, 0.78f, 1f, 0.78f);
            Color manualEdge = new Color(0.30f, 1f, 0.38f, 0.94f);
            Color nodeAColor = new Color(1f, 0.91f, 0.14f, 1f);
            Color nodeBColor = new Color(1f, 0.26f, 0.96f, 1f);

            // Background edges respect scene depth; selected connections draw over geometry
            // so the user can identify the exact endpoints, including unsafe links.
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            foreach (ServerPopulationRouteEdge edge in layer.populationEdges)
            {
                if (edge == null || !lookup.TryGetValue(edge.fromNodeId, out ServerPopulationRouteNode a) ||
                    !lookup.TryGetValue(edge.toNodeId, out ServerPopulationRouteNode b)) continue;
                bool incidentA = selectedA != 0 &&
                    (edge.fromNodeId == selectedA || edge.toNodeId == selectedA);
                bool incidentB = selectedB != 0 &&
                    (edge.fromNodeId == selectedB || edge.toNodeId == selectedB);
                bool manual = layer.editorEdits.manualEdges.Any(x =>
                    Connects(x, edge.fromNodeId, edge.toNodeId));
                bool mostRecent = lastPairAdded && Connects(edge, lastPairedA, lastPairedB);
                bool highlighted = incidentA || incidentB || mostRecent;
                Color tint = incidentA ? nodeAColor : incidentB ? nodeBColor :
                    mostRecent ? new Color(0.20f, 1f, 0.28f, 1f) :
                    manual ? manualEdge : automaticEdge;
                if (fadeUnselectedEdges && (selectedA != 0 || selectedB != 0) && !highlighted)
                    tint.a = 0.13f;
                Handles.color = tint;
                if (highlighted || manual)
                    Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
                Handles.DrawAAPolyLine(mostRecent ? 7f : highlighted ? 5f : manual ? 3.5f : 2f,
                    Point(a.pose) + lift, Point(b.pose) + lift);
                Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            }

            // Sphere handles must remain visible and clickable above roads/sidewalks;
            // previously LessEqual depth testing hid nodes beneath road meshes.
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            if (numberStyle == null)
            {
                numberStyle = new GUIStyle(EditorStyles.boldLabel);
                numberStyle.normal.textColor = Color.white;
                numberStyle.fontSize = 11;
            }
            if (focusedStyle == null)
            {
                focusedStyle = new GUIStyle(EditorStyles.helpBox);
                focusedStyle.normal.textColor = Color.white;
                focusedStyle.fontStyle = FontStyle.Bold;
                focusedStyle.fontSize = 12;
            }

            // Hover selection tells the user exactly which endpoint they are pointing at.
            long newHover = 0;
            float bestDistSq = 24f * 24f;
            Vector2 mouse = Event.current.mousePosition;
            for (int i = 0; i < nodes.Length; ++i)
            {
                ServerPopulationRouteNode node = nodes[i];
                if (node == null) continue;
                Vector3 p = Point(node.pose) + lift;
                Vector3 viewport = view.camera.WorldToViewportPoint(p);
                if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f ||
                    viewport.y < 0f || viewport.y > 1f) continue;
                Vector2 screen = HandleUtility.WorldToGUIPoint(p);
                float distanceSq = (screen - mouse).sqrMagnitude;
                if (distanceSq < bestDistSq)
                {
                    newHover = node.stableId;
                    bestDistSq = distanceSq;
                }
            }
            if (hoveredNode != newHover)
            {
                hoveredNode = newHover;
                if (Event.current.type == EventType.MouseMove) view.Repaint();
            }

            for (int i = 0; i < nodes.Length; ++i)
            {
                ServerPopulationRouteNode node = nodes[i];
                if (node == null) continue;
                Vector3 original = Point(node.pose);
                Vector3 point = original + lift;
                bool isA = node.stableId == selectedA;
                bool isB = node.stableId == selectedB;
                bool hover = node.stableId == hoveredNode;
                float size = Mathf.Clamp(HandleUtility.GetHandleSize(point) * 0.14f, 0.18f, 1.25f);
                Handles.color = isA ? nodeAColor : isB ? nodeBColor :
                    hover ? new Color(1f, 0.8f, 0.3f, 1f) : Color.white;
                if (Handles.Button(point, Quaternion.identity, size, size * 1.7f, Handles.SphereHandleCap))
                    Choose(node.stableId);
                if (isA || isB)
                {
                    Handles.color = isA ? nodeAColor : nodeBColor;
                    Handles.DrawWireDisc(point, view.camera.transform.forward, size * 1.65f);
                    Handles.DrawLine(point, original + Vector3.up * 1.35f);
                }

                // Numbers are the primary locator when dozens of prefab markers share
                // identical labels. Full ID is always shown in the selected node readout.
                if (showNodeNumbers && !isA && !isB &&
                    Vector3.Distance(view.camera.transform.position, original) < 150f)
                {
                    Handles.color = Color.white;
                    Handles.Label(point + Vector3.up * (size * 0.8f), $"#{i + 1}", numberStyle);
                }
                if (isA || isB || hover || showAllNodeLabels)
                {
                    string prefix = isA ? "A " : isB ? "B " : hover ? "HOVER " : "";
                    string title = $"{prefix}#{i + 1}  {node.label}  " +
                        $"({original.x:0.0}, {original.y:0.0}, {original.z:0.0})";
                    Handles.color = Color.white;
                    Handles.Label(point + Vector3.up * (size + 0.48f), title, focusedStyle);
                }
            }

            if (moveSelected && selectedA > 0 && lookup.TryGetValue(selectedA, out ServerPopulationRouteNode selected))
            {
                EditorGUI.BeginChangeCheck();
                Vector3 next = Handles.PositionHandle(Point(selected.pose), Quaternion.identity);
                if (EditorGUI.EndChangeCheck()) MoveNode(selectedA, next);
            }
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;

            // Fixed HUD remains legible even if selected node is outside the camera view.
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 56f, 425f, 285f), EditorStyles.helpBox);
            GUILayout.Label("EDIT GRAPH — A > B > PAIR / CLEAR", EditorStyles.boldLabel);
            GUILayout.Label("A (yellow): " + (selectedA == 0 ? "Click first node" :
                $"#{Number(selectedA)} {Node(selectedA)?.label} ({ConnectionCount(selectedA)} links)"));
            GUILayout.Label("B (magenta): " + (selectedB == 0 ? "Click second node" :
                $"#{Number(selectedB)} {Node(selectedB)?.label} ({ConnectionCount(selectedB)} links)"));
            bool validPair = Node(selectedA) != null && Node(selectedB) != null;
            GUILayout.Label(!validPair ? "Pick A, then B (each action resets A + B)." :
                SelectedPairConnected() ? "Already connected: PAIR makes no change" : "Not connected: PAIR adds a link");
            GUILayout.BeginHorizontal();
            GUI.enabled = validPair;
            if (GUILayout.Button("PAIR A + B", GUILayout.Height(28f))) Connect();
            if (GUILayout.Button("CLEAR A-B LINK", GUILayout.Height(28f))) Disconnect();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(lastPairResult))
            {
                Color prior = GUI.color;
                GUI.color = lastPairMessageType == MessageType.Warning
                    ? new Color(1f, 0.69f, 0.55f, 1f)
                    : lastPairAdded ? new Color(0.50f, 1f, 0.62f, 1f) : Color.white;
                GUILayout.Label(lastPairResult, EditorStyles.wordWrappedLabel);
                GUI.color = prior;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("New Pair")) NewPair();
            GUI.enabled = Node(selectedA) != null;
            if (GUILayout.Button("Frame A")) FrameNode(selectedA);
            GUI.enabled = Node(selectedB) != null;
            if (GUILayout.Button("Frame B")) FrameNode(selectedB);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled = dirty;
            if (GUILayout.Button("SAVE GRAPH", GUILayout.Height(27f))) Save();
            GUI.enabled = true;
            GUILayout.Label(dirty ? "UNSAVED changes" : "Saved / no pending edits");
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            Handles.EndGUI();
        }

    }
}
