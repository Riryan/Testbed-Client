#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Game.Server.Application.World;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Editor-only incremental writer for the EXISTING authoring layers. Uses the
    /// published core collision snapshot and the saved shared route graph. No NavMesh,
    /// shared-world or geometry rebake, second route graph, or runtime contract.
    /// </summary>
    internal static class ServerWorldSpawnLayerBake
    {
        [Serializable]
        private sealed class CoreHeader
        {
            public int formatVersion;
            public int layerFormatVersion;
            public string mapId;
            public string instanceId;
            public string contentHash;
        }

        [Serializable]
        private sealed class SpawnLayer
        {
            public int formatVersion = 1;
            public string mapId;
            public string instanceId;
            public string coreContentHash;
            public ServerSpawnAnchor[] spawnAnchors = Array.Empty<ServerSpawnAnchor>();
        }

        internal static bool TryBake(Scene scene, out int totalAnchors, out int activeNpcActors,
            out string detail)
        {
            totalAnchors = 0;
            activeNpcActors = 0;
            detail = string.Empty;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                detail = "No loaded scene.";
                return false;
            }
            try
            {
                ServerMap[] maps = Object.FindObjectsByType<ServerMap>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(x => x != null && x.gameObject.scene == scene).ToArray();
                if (maps.Length != 1)
                    throw new InvalidDataException($"Expected one ServerMap, found {maps.Length}.");
                ServerMap map = maps[0];
                string routesPath = PopulationBakedGraphEditor.RoutePath(scene);
                if (string.IsNullOrWhiteSpace(routesPath) ||
                    !routesPath.EndsWith(".routes.json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid route-layer path.");
                string stem = routesPath.Substring(0, routesPath.Length - ".routes.json".Length);
                string corePath = stem + ".servermap.json";
                string spawnPath = stem + ".spawns.json";
                if (!File.Exists(corePath) || !File.Exists(routesPath) || !File.Exists(spawnPath))
                    throw new InvalidDataException(
                        "The core, routes, and spawns layers must exist first. Run a full bake or AUTO CONNECT once.");

                CoreHeader core = JsonUtility.FromJson<CoreHeader>(File.ReadAllText(corePath));
                PopulationBakedGraphEditor.Layer routes =
                    JsonUtility.FromJson<PopulationBakedGraphEditor.Layer>(File.ReadAllText(routesPath));
                SpawnLayer oldSpawns = JsonUtility.FromJson<SpawnLayer>(File.ReadAllText(spawnPath));
                if (core == null || routes == null || oldSpawns == null ||
                    core.layerFormatVersion != 1 || routes.formatVersion != 1 ||
                    oldSpawns.formatVersion != 1 || string.IsNullOrWhiteSpace(core.contentHash) ||
                    routes.populationNodes == null || routes.populationEdges == null ||
                    routes.populationPortals == null || oldSpawns.spawnAnchors == null ||
                    !string.Equals(core.mapId, map.MapId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(core.instanceId ?? "", map.InstanceId ?? "", StringComparison.Ordinal) ||
                    !string.Equals(routes.mapId, core.mapId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(oldSpawns.mapId, core.mapId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(routes.instanceId ?? "", core.instanceId ?? "", StringComparison.Ordinal) ||
                    !string.Equals(oldSpawns.instanceId ?? "", core.instanceId ?? "", StringComparison.Ordinal) ||
                    !string.Equals(routes.coreContentHash, core.contentHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(oldSpawns.coreContentHash, core.contentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Saved map/routes/spawns are inconsistent. Repair the last failed bake before publishing.");

                // Load the ALREADY baked geometry for spawn validation. This avoids all
                // scene triangle export, Detour NavMesh building and shared-world writes.
                ServerMapSnapshot snapshot =
                    JsonUtility.FromJson<ServerMapSnapshot>(File.ReadAllText(corePath));
                if (snapshot == null || snapshot.movementTriangles == null ||
                    snapshot.movementTriangles.Length == 0)
                    throw new InvalidDataException("Published map has no movement collision data.");
                var collision = new ServerCollisionWorld(snapshot);
                var nodes = (routes.populationNodes ?? Array.Empty<ServerPopulationRouteNode>())
                    .Where(x => x != null && x.stableId > 0)
                    .ToDictionary(x => x.stableId);
                var outgoing = new HashSet<long>();
                foreach (ServerPopulationRouteEdge edge in routes.populationEdges)
                {
                    if (edge == null || !nodes.ContainsKey(edge.fromNodeId) ||
                        !nodes.ContainsKey(edge.toNodeId) || !edge.validatedClear) continue;
                    outgoing.Add(edge.fromNodeId);
                    if (!edge.oneWay) outgoing.Add(edge.toNodeId);
                }
                var routePortals = routes.populationPortals
                    .Where(x => x != null && x.stableId > 0).ToDictionary(x => x.stableId);
                // Gather ALL authored spawn templates first. A route graph may
                // intentionally omit an invalid/unconnected scene portal. Such a
                // portal is not a spawn source unless an enabled ServerSpawn explicitly
                // requests it; do not make unrelated, unused portals block a spawn bake.
                ServerSpawn[] allSpawns = Object.FindObjectsByType<ServerSpawn>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(x => x != null && x.gameObject.scene == scene)
                    .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal).ToArray();
                var activeSpawns = allSpawns.Where(x => x.gameObject.activeInHierarchy &&
                    x.enabled && x.enabledForServer).ToArray();
                var allOwned = new Dictionary<long, List<ServerSpawn>>();
                foreach (ServerSpawn spawn in allSpawns)
                {
                    // Disabled templates are relevant only for ownership/absence.
                    // They do not need valid spawn IDs until re-enabled.
                    long portalId = LinkedPortalId(spawn);
                    if (spawn.kind != ServerSpawnKind.Population || portalId <= 0)
                        continue;
                    if (!allOwned.TryGetValue(portalId, out List<ServerSpawn> list))
                        allOwned.Add(portalId, list = new List<ServerSpawn>());
                    list.Add(spawn);
                }

                var portals = Object.FindObjectsByType<PopulationPortal>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(x => x != null && x.gameObject.scene == scene &&
                        x.gameObject.activeInHierarchy && x.enabled)
                    .OrderBy(x => HierarchyPath(x.transform), StringComparer.Ordinal).ToArray();
                var scenePortalsById = new Dictionary<long, PopulationPortal>();
                var spawnCapablePortals = new List<PopulationPortal>();
                int skippedUnpublishedPortals = 0;
                foreach (PopulationPortal p in portals)
                {
                    RequireBakeId(p.BakeId, $"portal '{p.name}'");
                    long id = StableId(p.BakeId);
                    if (!scenePortalsById.TryAdd(id, p))
                        throw new InvalidDataException($"Duplicate portal bake ID: {p.name}.");

                    allOwned.TryGetValue(id, out List<ServerSpawn> linkedSpawns);
                    int enabledLinked = linkedSpawns == null ? 0 : linkedSpawns.Count(x =>
                        x.gameObject.activeInHierarchy && x.enabled && x.enabledForServer);

                    // An enabled portal may be omitted by the saved route preflight
                    // because its entry is unconnected or obstructed. If it has no
                    // enabled spawn templates, it is irrelevant to this spawn-only
                    // publication. The existing saved route graph stays untouched.
                    if (!routePortals.TryGetValue(id, out ServerPopulationPortal baked))
                    {
                        if (enabledLinked > 0 && p.mode != PopulationPortalMode.DespawnOnly)
                            throw new InvalidDataException(
                                $"Portal '{HierarchyPath(p.transform)}' has {enabledLinked} enabled linked " +
                                "Population spawn(s), but is not in the saved route layer. " +
                                "Its route connection must be repaired in EDIT/AUTO CONNECT before " +
                                "these NPCs can be baked. No files were changed.");
                        if (enabledLinked > 0 && p.mode == PopulationPortalMode.DespawnOnly)
                            throw new InvalidDataException(
                                $"Portal '{HierarchyPath(p.transform)}' is DespawnOnly but has " +
                                $"{enabledLinked} enabled linked Population spawn(s). Disable or reassign them.");
                        skippedUnpublishedPortals++;
                        continue;
                    }

                    // No active template means zero portal-owned actors. The portal's
                    // saved connectivity and lifecycle settings remain as authored;
                    // this spawn-only pass has no reason to validate or edit them.
                    if (enabledLinked == 0)
                        continue;
                    if (p.mode == PopulationPortalMode.DespawnOnly)
                        throw new InvalidDataException(
                            $"Portal '{HierarchyPath(p.transform)}' is DespawnOnly but has " +
                            $"{enabledLinked} enabled linked Population spawn(s). Disable or reassign them.");
                    if (p.routeMarker == null ||
                        !nodes.ContainsKey(StableId(p.routeMarker.BakeId)) ||
                        baked.routeNodeId != StableId(p.routeMarker.BakeId) ||
                        baked.mode != p.mode ||
                        !Near(baked.exterior, p.EffectiveExterior.position))
                        throw new InvalidDataException(
                            $"Portal '{HierarchyPath(p.transform)}' has enabled linked NPCs " +
                            "but its route/position/mode differs from the saved route layer. " +
                            "Save or repair the route graph, then AUTO CONNECT.");
                    if (!outgoing.Contains(baked.routeNodeId))
                        throw new InvalidDataException(
                            $"Portal '{HierarchyPath(p.transform)}' has enabled linked NPCs " +
                            "but no usable outgoing route. Repair the route graph first.");
                    spawnCapablePortals.Add(p);
                }

                var anchors = new List<ServerSpawnAnchor>();
                var ids = new HashSet<long>();
                int disabledCount = 0;
                foreach (ServerSpawn spawn in activeSpawns)
                {
                    RequireBakeId(spawn.BakeId, $"active spawn '{spawn.name}'");
                    long portalId = LinkedPortalId(spawn);
                    bool portalLinked = spawn.kind == ServerSpawnKind.Population && portalId > 0;
                    if (portalLinked)
                    {
                        // An inactive or removed portal disables its linked spawns.
                        // A surviving portal generates its own capacity-based anchors.
                        continue;
                    }
                    int count = spawn.kind == ServerSpawnKind.Population
                        ? Mathf.Clamp(spawn.populationCount, 1, 4096) : 1;
                    long routeId = spawn.routeMarker != null
                        ? StableId(spawn.routeMarker.BakeId) : spawn.routeNodeId;
                    if (routeId > 0 && (!nodes.ContainsKey(routeId) ||
                        (spawn.kind == ServerSpawnKind.Population && !outgoing.Contains(routeId))))
                        throw new InvalidDataException(
                            $"Spawn '{spawn.name}' has an invalid/unconnected route attachment. Run AUTO CONNECT.");
                    if (IsNonPlayer(spawn.kind) &&
                        !ValidatePose(collision, map, spawn.transform.position,
                            spawn.transform.eulerAngles.y, spawn.capsuleRadius,
                            spawn.capsuleHeight, spawn.maximumGroundSnap))
                        throw new InvalidDataException(
                            $"Spawn '{spawn.name}' is not supported by the baked collision map. Re-bake Map if geometry changed.");
                    for (int i = 0; i < count; ++i)
                    {
                        long id = i == 0 ? StableId(spawn.BakeId) :
                            StableId(StableId(spawn.BakeId), $"|population:{i}");
                        AddAnchor(ids, anchors, new ServerSpawnAnchor
                        {
                            stableId = id,
                            label = count > 1 ? $"{spawn.label} #{i + 1}" : spawn.label,
                            kind = spawn.kind,
                            actorKind = spawn.actorKind,
                            archetypeId = spawn.archetypeId ?? "",
                            deathLootTableId = spawn.deathLootTableId ?? "",
                            pose = Pose(spawn.transform),
                            priority = spawn.priority,
                            enabled = true,
                            capsuleRadius = spawn.capsuleRadius,
                            capsuleHeight = spawn.capsuleHeight,
                            maximumGroundSnap = spawn.maximumGroundSnap,
                            tags = spawn.tags ?? Array.Empty<string>(),
                            routeNodeId = routeId,
                            portalId = portalId > 0 && scenePortalsById.ContainsKey(portalId)
                                ? portalId : 0,
                        });
                        if (IsNonPlayer(spawn.kind)) activeNpcActors++;
                    }
                }

                bool routesChanged = false;
                foreach (PopulationPortal portal in spawnCapablePortals)
                {
                    long portalId = StableId(portal.BakeId);
                    ServerPopulationPortal baked = routePortals[portalId];
                    int capacity = Mathf.Clamp(portal.maximumActiveInWorld, 1, 4096);
                    // Portal lifecycle/density settings live in the EXISTING route
                    // layer because the authoritative runtime reads them there. Keep
                    // them in sync without rebuilding the shared route topology.
                    if (baked.maximumActiveInWorld != capacity ||
                        baked.label != portal.label ||
                        baked.portalType != portal.portalType ||
                        baked.tags != portal.tags ||
                        baked.allowedNpcTypes != portal.allowedNpcTypes ||
                        baked.minimumRespawnDelay != portal.minimumRespawnDelay ||
                        baked.maximumRespawnDelay != portal.maximumRespawnDelay ||
                        baked.blockedRetryDelay != portal.blockedRetryDelay ||
                        baked.minimumSpawnInterval != portal.minimumSpawnInterval ||
                        baked.maximumSpawnInterval != portal.maximumSpawnInterval ||
                        baked.spawnBurstLimit != portal.spawnBurstLimit ||
                        baked.maximumActiveNearby != portal.maximumActiveNearby ||
                        baked.activeNearbyRadius != portal.activeNearbyRadius ||
                        baked.exitClearanceRadius != portal.exitClearanceRadius ||
                        baked.requireDifferentDestination != portal.requireDifferentDestination ||
                        baked.suppressWhenVisibleToPlayers != portal.suppressWhenVisibleToPlayers)
                    {
                        routesChanged = true;
                        baked.maximumActiveInWorld = capacity;
                        baked.label = portal.label;
                        baked.portalType = portal.portalType;
                        baked.tags = portal.tags;
                        baked.allowedNpcTypes = portal.allowedNpcTypes;
                        baked.minimumRespawnDelay = portal.minimumRespawnDelay;
                        baked.maximumRespawnDelay = portal.maximumRespawnDelay;
                        baked.blockedRetryDelay = portal.blockedRetryDelay;
                        baked.minimumSpawnInterval = portal.minimumSpawnInterval;
                        baked.maximumSpawnInterval = portal.maximumSpawnInterval;
                        baked.spawnBurstLimit = portal.spawnBurstLimit;
                        baked.maximumActiveNearby = portal.maximumActiveNearby;
                        baked.activeNearbyRadius = portal.activeNearbyRadius;
                        baked.exitClearanceRadius = portal.exitClearanceRadius;
                        baked.requireDifferentDestination = portal.requireDifferentDestination;
                        baked.suppressWhenVisibleToPlayers = portal.suppressWhenVisibleToPlayers;
                    }
                    allOwned.TryGetValue(portalId, out List<ServerSpawn> linked);
                    ServerSpawn[] templates = linked == null ? Array.Empty<ServerSpawn>() :
                        linked.Where(x => x.gameObject.activeInHierarchy && x.enabled && x.enabledForServer).ToArray();
                    // The portal's maximumActiveInWorld is a CAPACITY, not an instruction
                    // to synthesize actors. Only enabled, explicitly linked ServerSpawn
                    // authoring contributes Population identities. This makes disabling
                    // a spawn authoritative instead of silently creating generic civilians.
                    if (templates.Length == 0)
                    {
                        disabledCount += capacity;
                        continue;
                    }
                    var authoredSlots = new List<ServerSpawn>(capacity);
                    foreach (ServerSpawn source in templates)
                    {
                        int wanted = Mathf.Clamp(source.populationCount, 1, 4096);
                        for (int n = 0; n < wanted && authoredSlots.Count < capacity; ++n)
                            authoredSlots.Add(source);
                    }
                    if (authoredSlots.Count == 0) continue;
                    // Every generated actor must have an independently valid capsule.
                    foreach (ServerSpawn source in authoredSlots.Distinct())
                    {
                        if (!ValidatePose(collision, map, portal.EffectiveExterior.position,
                            portal.EffectiveExterior.eulerAngles.y, source.capsuleRadius,
                            source.capsuleHeight, source.maximumGroundSnap))
                            throw new InvalidDataException(
                                $"Portal '{portal.name}' has a linked spawn capsule not supported by the baked map. Re-bake Map if geometry changed.");
                    }
                    for (int i = 0; i < authoredSlots.Count; ++i)
                    {
                        ServerSpawn source = authoredSlots[i];
                        AddAnchor(ids, anchors, new ServerSpawnAnchor
                        {
                            stableId = StableId(portalId, $"|active:{i}"),
                            label = authoredSlots.Count > 1 ?
                                $"{portal.label} Active #{i + 1}" : $"{portal.label} Active",
                            kind = ServerSpawnKind.Population,
                            actorKind = AuthoritativeActorKind.Population,
                            archetypeId = source.archetypeId ?? "",
                            deathLootTableId = source.deathLootTableId ?? "",
                            pose = Pose(portal.EffectiveExterior),
                            priority = source.priority,
                            enabled = true,
                            capsuleRadius = source.capsuleRadius,
                            capsuleHeight = source.capsuleHeight,
                            maximumGroundSnap = source.maximumGroundSnap,
                            tags = source.tags ?? Array.Empty<string>(),
                            routeNodeId = baked.routeNodeId,
                            portalId = portalId,
                        });
                        activeNpcActors++;
                    }
                }

                var next = new SpawnLayer
                {
                    mapId = core.mapId,
                    instanceId = core.instanceId,
                    coreContentHash = core.contentHash,
                    spawnAnchors = anchors.ToArray(),
                };
                // Destructive changes to previously published actor identities are
                // explicit. Do not silently prune manually authored content.
                var newIds = new HashSet<long>(anchors.Select(x => x.stableId));
                ServerSpawnAnchor[] removed = oldSpawns.spawnAnchors
                    .Where(x => x != null && x.enabled && IsNonPlayer(x.kind) &&
                        !newIds.Contains(x.stableId)).ToArray();
                if (removed.Length > 0)
                {
                    string examples = string.Join(", ", removed.Take(6)
                        .Select(x => string.IsNullOrWhiteSpace(x.label) ? x.stableId.ToString() : x.label));
                    if (!EditorUtility.DisplayDialog("Confirm NPC Spawn Removal",
                        $"The current scene will remove {removed.Length} published NPC spawn identities.\n\n" +
                        $"Examples: {examples}\n\n" +
                        $"The new authoritative count will be {activeNpcActors}.\n" +
                        "Map geometry and shared route nodes will not change.\n\n" +
                        "Apply this spawn-layer change?", "Publish Changes", "Cancel"))
                    {
                        detail = "Spawn removal cancelled. No files were published.";
                        return false;
                    }
                }
                string updatedSpawns = JsonUtility.ToJson(next, true);
                string updatedRoutes = routesChanged ? JsonUtility.ToJson(routes, true) : null;
                bool spawnsChanged = !string.Equals(File.ReadAllText(spawnPath),
                    updatedSpawns, StringComparison.Ordinal);
                if (spawnsChanged || routesChanged)
                    Publish(spawnsChanged ? spawnPath : null, spawnsChanged ? updatedSpawns : null,
                        routesChanged ? routesPath : null, updatedRoutes);
                totalAnchors = anchors.Count;
                detail = $"Spawn layer {(spawnsChanged || routesChanged ? "published" : "already up to date")}: {anchors.Count} anchors, " +
                    $"{activeNpcActors} enabled NPC identities, {disabledCount} disabled/skipped, " +
                    $"unused/unpublished scene portals skipped={skippedUnpublishedPortals}; " +
                    $"active portal capacities {(routesChanged ? "synchronized" : "unchanged")}. " +
                    "Core/NavMesh/shared world were NOT rebuilt. Restart GameServer.";
                Debug.Log("[Server World Spawn Bake] " + detail);
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                Debug.LogError("[Server World Spawn Bake] FAILED: " + ex);
                return false;
            }
        }

        private static void Publish(string spawnsPath, string spawnsJson, string routesPath, string routesJson)
        {
            var paths = new List<string>(2);
            var jsons = new List<string>(2);
            if (spawnsPath != null) { paths.Add(spawnsPath); jsons.Add(spawnsJson); }
            if (routesPath != null) { paths.Add(routesPath); jsons.Add(routesJson); }
            string token = Guid.NewGuid().ToString("N");
            string[] temps = paths.Select(x => x + "." + token + ".tmp").ToArray();
            string[] backups = paths.Select(x => x + "." + token + ".bak").ToArray();
            bool keepBackups = false;
            try
            {
                // All originals are backed up before any publication starts.
                for (int i = 0; i < paths.Count; i++)
                {
                    // Catch a locked file before the first replacement. This
                    // limits the chance of ending with mixed route/spawn revisions.
                    using (FileStream exclusive = new FileStream(paths[i], FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None)) { }
                }
                for (int i = 0; i < paths.Count; i++)
                {
                    File.WriteAllText(temps[i], jsons[i], new UTF8Encoding(false));
                    File.Copy(paths[i], backups[i], false);
                }
                for (int i = 0; i < paths.Count; i++)
                {
                    // Keep the bounded retry inside the existing editor action; never
                    // retry indefinitely or block the server's simulation thread.
                    for (int attempt = 0; ; ++attempt)
                    {
                        try { File.Replace(temps[i], paths[i], null); break; }
                        catch (IOException) when (attempt < 2) { Thread.Sleep(75); }
                    }
                }
            }
            catch (Exception ex)
            {
                // A Replace may have completed even when a later operation fails.
                // Never discard a backup if its restoration encounters a lock.
                for (int i = paths.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (File.Exists(backups[i]))
                        {
                            File.Copy(backups[i], paths[i], true);
                        }
                    }
                    catch (Exception rollback)
                    {
                        keepBackups = true;
                        Debug.LogError($"[Server World Spawn Bake] CRITICAL rollback error for {paths[i]}. " +
                            $"Backup retained at {backups[i]}: {rollback}");
                    }
                }
                throw new IOException("Cannot safely publish spawn/route layers; originals retained where possible. " +
                    "Close the process holding the files and inspect backups before retrying.", ex);
            }
            finally
            {
                foreach (string temp in temps)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e)
                    { Debug.LogWarning($"Temporary bake file retained: {temp}: {e.Message}"); }
                }
                if (!keepBackups)
                {
                    foreach (string backup in backups)
                    {
                        try { if (File.Exists(backup)) File.Delete(backup); } catch (Exception e)
                        { Debug.LogWarning($"Bake backup retained: {backup}: {e.Message}"); }
                    }
                }
            }
        }

        private static bool IsNonPlayer(ServerSpawnKind kind) =>
            kind == ServerSpawnKind.Population || kind == ServerSpawnKind.Npc ||
            kind == ServerSpawnKind.Monster;

        private static void AddAnchor(HashSet<long> ids, List<ServerSpawnAnchor> result, ServerSpawnAnchor anchor)
        {
            if (anchor.stableId <= 0 || !ids.Add(anchor.stableId))
                throw new InvalidDataException($"Duplicate/invalid spawn stable ID {anchor.stableId}.");
            result.Add(anchor);
        }

        private static void RequireBakeId(string id, string label)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException($"Missing bake ID on {label}. Save/validate scene authoring first.");
        }

        private static long LinkedPortalId(ServerSpawn spawn)
        {
            if (spawn.populationPortal != null)
                return StableId(spawn.populationPortal.BakeId);
            return spawn.portalId;
        }

        private static ServerPose Pose(Transform transform) =>
            new ServerPose(transform.position.x, transform.position.y,
                transform.position.z, transform.eulerAngles.y);

        private static bool ValidatePose(ServerCollisionWorld collision, ServerMap map,
            Vector3 point, float yaw, float radius, float height, float snap)
        {
            return collision.TryValidateSpawn(
                new ServerPose(point.x, point.y, point.z, yaw),
                new ServerCapsule(Mathf.Max(0.1f, radius), Mathf.Max(height, radius * 2f)),
                Mathf.Max(0.1f, snap), Mathf.Clamp(map.NavAgentMaxSlope, 0f, 89f),
                out _, out _);
        }

        private static bool Near(ServerPose pose, Vector3 p) =>
            Vector3.SqrMagnitude(new Vector3(pose.x, pose.y, pose.z) - p) < 0.04f;

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "";
            var parts = new Stack<string>();
            for (; t != null; t = t.parent) parts.Push(t.name);
            return string.Join("/", parts);
        }

        private static long StableId(string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value ?? "");
            using SHA256 sha = SHA256.Create();
            long id = BitConverter.ToInt64(sha.ComputeHash(data), 0) & long.MaxValue;
            return id == 0 ? 1 : id;
        }
        private static long StableId(long baseId, string suffix) => StableId(baseId.ToString() + suffix);
    }
}
#endif
