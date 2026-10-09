using System.Collections.Generic;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.WorldAuthoring.Editor
{
    [InitializeOnLoad]
    internal static class PopulationSpawnAuthoringSummary
    {
        internal readonly struct SceneSummary
        {
            public readonly int SpawnSources;
            public readonly int TotalActors;
            public readonly int PortalBackedActors;
            public readonly int DirectActors;
            public readonly int ActivePortals;

            public SceneSummary(
                int spawnSources,
                int totalActors,
                int portalBackedActors,
                int directActors,
                int activePortals)
            {
                SpawnSources = spawnSources;
                TotalActors = totalActors;
                PortalBackedActors = portalBackedActors;
                DirectActors = directActors;
                ActivePortals = activePortals;
            }
        }

        private readonly struct PortalSummary
        {
            public readonly int Actors;
            public readonly int Sources;

            public PortalSummary(int actors, int sources)
            {
                Actors = actors;
                Sources = sources;
            }
        }

        private static readonly Dictionary<int, PortalSummary> PortalCache =
            new Dictionary<int, PortalSummary>();
        private static readonly Dictionary<int, int> SpawnCountCache =
            new Dictionary<int, int>();
        private static readonly Dictionary<int, SceneSummary> SceneCache =
            new Dictionary<int, SceneSummary>();

        private static bool rebuildScheduled;
        private static bool hasCache;

        private static GUIStyle labelStyle;

        static PopulationSpawnAuthoringSummary()
        {
            EditorApplication.hierarchyChanged += MarkDirty;
            EditorApplication.projectChanged += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
            Selection.selectionChanged += MarkDirty;
            MarkDirty();
        }

        internal static void MarkDirty()
        {
            if (rebuildScheduled)
                return;

            rebuildScheduled = true;
            EditorApplication.delayCall += RebuildCache;
        }

        internal static void RefreshNow()
        {
            rebuildScheduled = false;
            RebuildCache();
            SceneView.RepaintAll();
        }

        internal static bool TryGetSceneSummary(Scene scene, out SceneSummary summary)
        {
            if (!hasCache)
                RebuildCache();

            return SceneCache.TryGetValue(scene.handle, out summary);
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active)]
        private static void DrawSpawnLabel(ServerSpawn spawn, GizmoType gizmoType)
        {
            if (spawn == null ||
                !spawn.gameObject.activeInHierarchy ||
                !spawn.enabledForServer ||
                spawn.kind != ServerSpawnKind.Population ||
                spawn.populationPortal != null ||
                spawn.portalId > 0)
            {
                return;
            }

            if (!SpawnCountCache.TryGetValue(spawn.GetInstanceID(), out int count))
                count = Mathf.Max(1, spawn.populationCount);

            Vector3 position = spawn.transform.position + Vector3.up * 0.55f;
            Handles.Label(position, $"POP x{count:n0}", GetLabelStyle());
        }

        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Active)]
        private static void DrawPortalLabel(PopulationPortal portal, GizmoType gizmoType)
        {
            if (portal == null || !portal.gameObject.activeInHierarchy)
                return;

            int activeCap = portal.mode == PopulationPortalMode.DespawnOnly
                ? 0
                : Mathf.Max(1, portal.maximumActiveInWorld);
            int nearbyCap = Mathf.Max(1, portal.maximumActiveNearby);
            int burst = Mathf.Max(1, portal.spawnBurstLimit);
            float minInterval = Mathf.Max(0f, portal.minimumSpawnInterval);
            float maxInterval = Mathf.Max(minInterval, portal.maximumSpawnInterval);

            Vector3 position = portal.EffectiveInterior.position + Vector3.up * 0.8f;
            string text =
                $"ACTIVE {activeCap:n0}  •  NEARBY {nearbyCap:n0}\n" +
                $"RELEASE {burst:n0}  •  {minInterval:0.##}–{maxInterval:0.##}s";

            Handles.Label(position, text, GetLabelStyle());
        }

        private static GUIStyle GetLabelStyle()
        {
            if (labelStyle != null)
                return labelStyle;

            // EditorStyles is not safe during static type initialization. Build this lazily
            // only when Unity is actually drawing an editor GUI/gizmo event.
            labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
            };
            labelStyle.normal.textColor = Color.white;
            return labelStyle;
        }

        private static void RebuildCache()
        {
            rebuildScheduled = false;
            hasCache = true;

            PortalCache.Clear();
            SpawnCountCache.Clear();
            SceneCache.Clear();

            PopulationPortal[] portals = Object.FindObjectsByType<PopulationPortal>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            var portalByStableId = new Dictionary<long, PopulationPortal>();
            var scenePortalCounts = new Dictionary<int, int>();
            var scenePortalCapacity = new Dictionary<int, int>();

            for (int i = 0; i < portals.Length; ++i)
            {
                PopulationPortal portal = portals[i];
                if (portal == null)
                    continue;

                PortalCache[portal.GetInstanceID()] = new PortalSummary(0, 0);

                long stableId = ServerWorldBake.StableAuthoringId(portal.BakeId);
                if (stableId > 0 && !portalByStableId.ContainsKey(stableId))
                    portalByStableId.Add(stableId, portal);

                if (!portal.gameObject.activeInHierarchy)
                    continue;

                int sceneHandle = portal.gameObject.scene.handle;
                scenePortalCounts.TryGetValue(sceneHandle, out int portalCount);
                scenePortalCounts[sceneHandle] = portalCount + 1;

                if (portal.mode != PopulationPortalMode.DespawnOnly)
                {
                    scenePortalCapacity.TryGetValue(sceneHandle, out int capacity);
                    scenePortalCapacity[sceneHandle] =
                        capacity + Mathf.Max(1, portal.maximumActiveInWorld);
                }
            }

            var sceneDirectSources = new Dictionary<int, int>();
            var sceneDirectActors = new Dictionary<int, int>();

            ServerSpawn[] spawns = Object.FindObjectsByType<ServerSpawn>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < spawns.Length; ++i)
            {
                ServerSpawn spawn = spawns[i];
                if (spawn == null ||
                    !spawn.gameObject.activeInHierarchy ||
                    !spawn.enabledForServer ||
                    spawn.kind != ServerSpawnKind.Population)
                {
                    continue;
                }

                PopulationPortal portal = spawn.populationPortal;
                if (portal == null && spawn.portalId > 0)
                    portalByStableId.TryGetValue(spawn.portalId, out portal);

                bool portalBacked =
                    portal != null &&
                    portal.gameObject.scene == spawn.gameObject.scene &&
                    portal.mode != PopulationPortalMode.DespawnOnly;

                if (portalBacked)
                    continue;

                int count = Mathf.Max(1, spawn.populationCount);
                SpawnCountCache[spawn.GetInstanceID()] = count;

                int sceneHandle = spawn.gameObject.scene.handle;
                sceneDirectSources.TryGetValue(sceneHandle, out int sources);
                sceneDirectSources[sceneHandle] = sources + 1;

                sceneDirectActors.TryGetValue(sceneHandle, out int direct);
                sceneDirectActors[sceneHandle] = direct + count;
            }

            for (int i = 0; i < SceneManager.sceneCount; ++i)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                int h = scene.handle;
                scenePortalCounts.TryGetValue(h, out int portalCount);
                scenePortalCapacity.TryGetValue(h, out int portalCapacity);
                sceneDirectSources.TryGetValue(h, out int directSources);
                sceneDirectActors.TryGetValue(h, out int directActors);

                SceneCache[h] = new SceneSummary(
                    portalCount + directSources,
                    portalCapacity + directActors,
                    portalCapacity,
                    directActors,
                    portalCount);
            }
        }
    }
}
