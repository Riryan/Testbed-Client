using System;
using System.Collections.Generic;
using System.IO;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Visualizes the actual baked .servermap.json consumed by the standalone GameServer.
    /// This is intentionally Editor-only so authoritative map geometry is not added to
    /// normal client builds just for debugging.
    /// </summary>
    public sealed class ServerMapDebug : EditorWindow
    {
        private const string ShowPref = "MMO.ServerMapDebug.Show";
        private const string WalkFillPref = "MMO.ServerMapDebug.WalkFill";
        private const string WalkBoundsPref = "MMO.ServerMapDebug.WalkBounds";
        private const string WalkTrianglesPref = "MMO.ServerMapDebug.WalkTriangles";
        private const string SolidPref = "MMO.ServerMapDebug.Solid";
        private const string SpawnPref = "MMO.ServerMapDebug.Spawns";
        private const string InteractablePref = "MMO.ServerMapDebug.Interactables";
        private const string BlockerPref = "MMO.ServerMapDebug.Blockers";
        private const string BoundsPref = "MMO.ServerMapDebug.Bounds";
        private const string LabelsPref = "MMO.ServerMapDebug.Labels";
        private const string XrayPref = "MMO.ServerMapDebug.Xray";
        private const string WalkAlphaPref = "MMO.ServerMapDebug.WalkAlpha";
        private const string MarkerScalePref = "MMO.ServerMapDebug.MarkerScale";
        private const string LabelDistancePref = "MMO.ServerMapDebug.LabelDistance";

        private static readonly Color WalkColor = new Color(0.10f, 1f, 0.22f, 1f);
        private static readonly Color WalkBoundaryColor = new Color(0.20f, 1f, 0.35f, 1f);
        private static readonly Color SolidColor = new Color(1f, 0.20f, 0.14f, 1f);
        private static readonly Color SpawnColor = new Color(1f, 0.85f, 0.10f, 1f);
        private static readonly Color InteractableColor = new Color(0.10f, 0.95f, 1f, 1f);
        private static readonly Color BlockerColor = new Color(1f, 0.45f, 0.05f, 1f);
        private static readonly Color BoundsColor = new Color(1f, 0.15f, 0.95f, 1f);

        private static ServerMapDebug _window;
        private static ServerMapSnapshot _snapshot;
        private static SharedWorldSnapshot _sharedWorld;
        private static Mesh _walkMesh;
        private static Material _walkMaterial;
        private static Bounds _bounds;
        private static bool _hasBounds;
        private static string _loadedPath = string.Empty;
        private static DateTime _loadedWriteTimeUtc;
        private static string _status = "No baked map loaded.";
        private static MessageType _statusType = MessageType.Info;
        private static double _nextFilePoll;

        private static readonly List<LineSegment> WalkBoundarySegments = new List<LineSegment>(4096);
        private static readonly List<LineSegment> SolidSegments = new List<LineSegment>(4096);

        private bool _show;
        private bool _walkFill;
        private bool _walkBounds;
        private bool _walkTriangles;
        private bool _solid;
        private bool _spawns;
        private bool _interactables;
        private bool _blockers;
        private bool _worldBounds;
        private bool _labels;
        private bool _xray;
        private float _walkAlpha;
        private float _markerScale;
        private float _labelDistance;
        private Vector2 _scroll;

        [MenuItem("MMO Tools/World/Server Map Debug")]
        public static void Open()
        {
            _window = GetWindow<ServerMapDebug>("Server Map Debug");
            _window.minSize = new Vector2(390f, 520f);
            _window.Show();
        }

        private void OnEnable()
        {
            _window = this;
            _show = EditorPrefs.GetBool(ShowPref, true);

            // V2 defaults prioritize movement-authoring readability.
            _walkFill = EditorPrefs.GetBool(WalkFillPref, false);
            _walkBounds = EditorPrefs.GetBool(WalkBoundsPref, true);
            _walkTriangles = EditorPrefs.GetBool(WalkTrianglesPref, false);
            _solid = EditorPrefs.GetBool(SolidPref, false);
            _spawns = EditorPrefs.GetBool(SpawnPref, true);
            _interactables = EditorPrefs.GetBool(InteractablePref, false);
            _blockers = EditorPrefs.GetBool(BlockerPref, true);
            _worldBounds = EditorPrefs.GetBool(BoundsPref, false);
            _labels = EditorPrefs.GetBool(LabelsPref, false);
            _xray = EditorPrefs.GetBool(XrayPref, false);
            _walkAlpha = EditorPrefs.GetFloat(WalkAlphaPref, 0.08f);
            _markerScale = EditorPrefs.GetFloat(MarkerScalePref, 0.055f);
            _labelDistance = EditorPrefs.GetFloat(LabelDistancePref, 35f);

            SceneView.duringSceneGui += DrawScene;
            EditorSceneManagerBridge.Subscribe(SceneChanged);
            Reload();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DrawScene;
            EditorSceneManagerBridge.Unsubscribe(SceneChanged);
            SavePrefs();
            if (_window == this)
                _window = null;
        }

        private void OnDestroy()
        {
            if (_window == this)
                _window = null;
        }

        private void OnInspectorUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextFilePoll)
                return;

            _nextFilePoll = EditorApplication.timeSinceStartup + 0.75d;
            string expected = ExpectedBakePath();
            if (!string.Equals(expected, _loadedPath, StringComparison.OrdinalIgnoreCase))
            {
                Reload();
                return;
            }

            if (!string.IsNullOrWhiteSpace(expected) && File.Exists(expected))
            {
                DateTime write = File.GetLastWriteTimeUtc(expected);
                if (write != _loadedWriteTimeUtc)
                    Reload();
            }

            Repaint();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Server Map Debug", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Shows the baked server-world data that the standalone GameServer actually loads. " +
                "The default Movement view emphasizes walk boundaries instead of painting the whole scene.",
                MessageType.Info);

            DrawPresets();

            bool changed = false;
            changed |= Toggle(ref _show, "Show in Scene View");

            EditorGUILayout.Space(5f);
            using (new EditorGUI.DisabledScope(!_show))
            {
                EditorGUILayout.LabelField("Movement Surface", EditorStyles.boldLabel);
                changed |= Toggle(ref _walkBounds, "Walkable boundaries");
                changed |= Toggle(ref _walkFill, "Walkable fill");
                if (_walkFill)
                {
                    float alpha = EditorGUILayout.Slider("Fill opacity", _walkAlpha, 0.02f, 0.35f);
                    if (!Mathf.Approximately(alpha, _walkAlpha))
                    {
                        _walkAlpha = alpha;
                        UpdateWalkMaterial();
                        changed = true;
                    }
                }

                changed |= Toggle(ref _walkTriangles, "Walk triangle grid");

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("World Layers", EditorStyles.boldLabel);
                changed |= Toggle(ref _solid, "Solid / non-walkable collision wireframe");
                changed |= Toggle(ref _spawns, "Player / recovery spawns");
                changed |= Toggle(ref _interactables, "Interactables + slots");
                changed |= Toggle(ref _blockers, "Dynamic blockers");
                changed |= Toggle(ref _worldBounds, "Baked world bounds");

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Readability", EditorStyles.boldLabel);
                changed |= Toggle(ref _labels, "Labels");

                float markerScale = EditorGUILayout.Slider("Marker size", _markerScale, 0.025f, 0.12f);
                if (!Mathf.Approximately(markerScale, _markerScale))
                {
                    _markerScale = markerScale;
                    changed = true;
                }

                using (new EditorGUI.DisabledScope(!_labels))
                {
                    float labelDistance = EditorGUILayout.Slider("Label distance", _labelDistance, 5f, 150f);
                    if (!Mathf.Approximately(labelDistance, _labelDistance))
                    {
                        _labelDistance = labelDistance;
                        changed = true;
                    }
                }

                changed |= Toggle(ref _xray, "X-Ray overlay (draw through scene)");
            }

            if (changed)
            {
                SavePrefs();
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(8f);
            DrawBakeInfo();

            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload Bake"))
                    Reload();

                using (new EditorGUI.DisabledScope(!_hasBounds))
                {
                    if (GUILayout.Button("Frame Baked World"))
                        FrameBounds();
                }
            }

            ServerMap map = ServerWorldBake.FindMapInActiveScene();
            using (new EditorGUI.DisabledScope(map == null))
            {
                if (GUILayout.Button("Bake Current Scene"))
                {
                    if (ServerWorldBake.TryBakeCurrentScene(true, true, out _))
                        Reload();
                }
            }

            EditorGUILayout.Space(8f);
            DrawLegend();
            EditorGUILayout.EndScrollView();
        }

        private void DrawPresets()
        {
            EditorGUILayout.LabelField("Views", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Movement"))
                    ApplyMovementPreset();
                if (GUILayout.Button("Collision"))
                    ApplyCollisionPreset();
                if (GUILayout.Button("Interaction"))
                    ApplyInteractionPreset();
            }
        }

        private void ApplyMovementPreset()
        {
            _show = true;
            _walkBounds = true;
            _walkFill = false;
            _walkTriangles = false;
            _solid = false;
            _spawns = true;
            _interactables = false;
            _blockers = true;
            _worldBounds = false;
            _labels = false;
            _xray = false;
            SavePrefs();
            SceneView.RepaintAll();
        }

        private void ApplyCollisionPreset()
        {
            _show = true;
            _walkBounds = true;
            _walkFill = false;
            _walkTriangles = false;
            _solid = true;
            _spawns = false;
            _interactables = false;
            _blockers = true;
            _worldBounds = true;
            _labels = false;
            _xray = false;
            SavePrefs();
            SceneView.RepaintAll();
        }

        private void ApplyInteractionPreset()
        {
            _show = true;
            _walkBounds = true;
            _walkFill = false;
            _walkTriangles = false;
            _solid = false;
            _spawns = false;
            _interactables = true;
            _blockers = true;
            _worldBounds = false;
            _labels = true;
            _xray = false;
            SavePrefs();
            SceneView.RepaintAll();
        }

        private static bool Toggle(ref bool field, string label)
        {
            bool value = EditorGUILayout.ToggleLeft(label, field);
            if (value == field)
                return false;
            field = value;
            return true;
        }

        private void DrawBakeInfo()
        {
            Scene scene = SceneManager.GetActiveScene();
            ServerMap map = ServerWorldBake.FindMapInActiveScene();

            EditorGUILayout.LabelField("Active Scene", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path,
                EditorStyles.wordWrappedMiniLabel);

            if (map == null)
            {
                EditorGUILayout.HelpBox("No ServerMap is authored in the active scene.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("Map ID", map.MapId);
            EditorGUILayout.HelpBox(_status, _statusType);

            if (_snapshot == null)
                return;

            ServerCollisionTriangle[] supportTriangles = _sharedWorld?.supportTriangles ??
                Array.Empty<ServerCollisionTriangle>();
            ServerCollisionTriangle[] solidTriangles = _sharedWorld?.collisionTriangles ??
                (_snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>());

            EditorGUILayout.LabelField("Bake Revision", _snapshot.bakeRevision.ToString());
            EditorGUILayout.LabelField("Format", _snapshot.formatVersion.ToString());
            EditorGUILayout.LabelField("Precise Support Triangles", supportTriangles.Length.ToString("n0"));
            EditorGUILayout.LabelField("Exact Surface Edges", (_sharedWorld?.surfaceEdges?.Length ?? WalkBoundarySegments.Count).ToString("n0"));
            EditorGUILayout.LabelField("Client-Safe Collision Triangles", solidTriangles.Length.ToString("n0"));
            EditorGUILayout.LabelField("Interactables", (_snapshot.interactables?.Length ?? 0).ToString("n0"));
            EditorGUILayout.LabelField("Blockers", (_snapshot.dynamicBlockers?.Length ?? 0).ToString("n0"));
            EditorGUILayout.LabelField("Spawns", (_snapshot.spawnAnchors?.Length ?? 0).ToString("n0"));

            string hash = _snapshot.contentHash ?? string.Empty;
            if (hash.Length > 16)
                hash = hash.Substring(0, 16) + "…";
            EditorGUILayout.LabelField("Content Hash", hash);

            if (_snapshot.bakedUtcTicks > 0)
            {
                try
                {
                    DateTime baked = new DateTime(_snapshot.bakedUtcTicks, DateTimeKind.Utc).ToLocalTime();
                    EditorGUILayout.LabelField("Baked", baked.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                catch
                {
                    // Bad timestamp should not break the debug view.
                }
            }

            EditorGUILayout.LabelField("File", _loadedPath, EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawLegend()
        {
            EditorGUILayout.LabelField("Legend", EditorStyles.boldLabel);
            Legend(WalkBoundaryColor, "Walkable boundary / authoritative movement edge");
            Legend(WalkColor, "Walkable surface");
            Legend(SolidColor, "Solid collision not marked walkable");
            Legend(SpawnColor, "Spawn / recovery anchor");
            Legend(InteractableColor, "Interactable / interaction slot");
            Legend(BlockerColor, "Dynamic blocker");
            Legend(BoundsColor, "Baked world bounds");
        }

        private static void Legend(Color color, string label)
        {
            Rect row = EditorGUILayout.GetControlRect(false, 18f);
            Rect swatch = new Rect(row.x, row.y + 2f, 28f, 14f);
            EditorGUI.DrawRect(swatch, color);
            EditorGUI.LabelField(
                new Rect(row.x + 36f, row.y, row.width - 36f, row.height),
                label);
        }

        private static void DrawScene(SceneView sceneView)
        {
            if (_window == null || !_window._show || _snapshot == null)
                return;

            CompareFunction zTest =
                _window._xray ? CompareFunction.Always : CompareFunction.LessEqual;

            if (_window._walkFill)
                DrawWalkMesh(zTest);

            if (_window._walkBounds)
                DrawSegments(WalkBoundarySegments, WalkBoundaryColor, 3f, zTest);

            if (_window._walkTriangles)
                DrawWalkTriangleGrid(zTest);

            if (_window._solid)
                DrawSegments(SolidSegments, new Color(SolidColor.r, SolidColor.g, SolidColor.b, 0.85f), 1.5f, zTest);

            if (_window._spawns)
                DrawSpawns(sceneView, zTest);

            if (_window._interactables)
                DrawInteractables(sceneView, zTest);

            if (_window._blockers)
                DrawBlockers(zTest);

            if (_window._worldBounds)
                DrawBounds(zTest);
        }

        private static void DrawWalkMesh(CompareFunction zTest)
        {
            if (_walkMesh == null || _walkMesh.vertexCount == 0)
                return;

            Material material = WalkMaterial;
            if (material == null)
                return;

            material.SetInt("_ZTest", (int)zTest);
            material.SetPass(0);

            // Lift the overlay slightly to avoid z-fighting with the source floor.
            Graphics.DrawMeshNow(
                _walkMesh,
                Matrix4x4.Translate(Vector3.up * 0.012f));
        }

        private static void DrawSegments(
            List<LineSegment> segments,
            Color color,
            float width,
            CompareFunction zTest)
        {
            if (segments == null || segments.Count == 0)
                return;

            Handles.zTest = zTest;
            Handles.color = color;
            for (int i = 0; i < segments.Count; ++i)
            {
                LineSegment segment = segments[i];
                Handles.DrawAAPolyLine(
                    width,
                    segment.A + Vector3.up * 0.015f,
                    segment.B + Vector3.up * 0.015f);
            }
        }

        private static void DrawWalkTriangleGrid(CompareFunction zTest)
        {
            ServerCollisionTriangle[] triangles =
                _sharedWorld?.supportTriangles ?? (_snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>());

            int walkCount = triangles.Length;

            int stride = walkCount > 25000
                ? Mathf.CeilToInt(walkCount / 25000f)
                : 1;

            Handles.zTest = zTest;
            Handles.color = new Color(WalkColor.r, WalkColor.g, WalkColor.b, 0.32f);

            int seen = 0;
            for (int i = 0; i < triangles.Length; ++i)
            {
                ServerCollisionTriangle t = triangles[i];
                if (_sharedWorld == null && (t.flags & ServerSurfaceFlags.Walkable) == 0)
                    continue;

                if ((seen++ % stride) != 0)
                    continue;

                Vector3 lift = Vector3.up * 0.014f;
                Vector3 a = new Vector3(t.ax, t.ay, t.az) + lift;
                Vector3 b = new Vector3(t.bx, t.by, t.bz) + lift;
                Vector3 c = new Vector3(t.cx, t.cy, t.cz) + lift;
                Handles.DrawLine(a, b);
                Handles.DrawLine(b, c);
                Handles.DrawLine(c, a);
            }
        }

        private static void DrawSpawns(SceneView sceneView, CompareFunction zTest)
        {
            ServerSpawnAnchor[] spawns =
                _snapshot.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();

            Handles.zTest = zTest;
            Handles.color = SpawnColor;

            for (int i = 0; i < spawns.Length; ++i)
            {
                ServerSpawnAnchor spawn = spawns[i];
                if (spawn == null || !spawn.enabled)
                    continue;

                Vector3 p = Position(spawn.pose);
                float size = HandleUtility.GetHandleSize(p) * _window._markerScale;

                // Compact floor marker instead of the V1 sphere, which could dominate
                // small test maps at some Scene View zoom levels.
                Handles.DrawWireDisc(p + Vector3.up * 0.015f, Vector3.up, size);
                Handles.ArrowHandleCap(
                    0,
                    p + Vector3.up * 0.02f,
                    Quaternion.Euler(0f, spawn.pose.yaw, 0f),
                    size * 2.2f,
                    EventType.Repaint);

                if (ShouldDrawLabel(sceneView, p))
                {
                    Handles.Label(
                        p + Vector3.up * size * 1.8f,
                        string.IsNullOrWhiteSpace(spawn.label)
                            ? spawn.kind.ToString()
                            : spawn.label);
                }
            }
        }

        private static void DrawInteractables(SceneView sceneView, CompareFunction zTest)
        {
            ServerWorldInteractableDefinition[] defs =
                _snapshot.interactables ?? Array.Empty<ServerWorldInteractableDefinition>();

            Handles.zTest = zTest;
            Handles.color = InteractableColor;

            for (int i = 0; i < defs.Length; ++i)
            {
                ServerWorldInteractableDefinition def = defs[i];
                if (def == null || !def.enabledByDefault)
                    continue;

                Vector3 root = Position(def.pose);
                float size = HandleUtility.GetHandleSize(root) * _window._markerScale;

                Handles.CubeHandleCap(
                    0,
                    root,
                    Quaternion.Euler(0f, def.pose.yaw, 0f),
                    size,
                    EventType.Repaint);

                if (ShouldDrawLabel(sceneView, root))
                {
                    Handles.Label(
                        root + Vector3.up * size * 1.6f,
                        string.IsNullOrWhiteSpace(def.label)
                            ? "Interactable"
                            : def.label);
                }

                ServerInteractionSlotDefinition[] slots =
                    def.slots ?? Array.Empty<ServerInteractionSlotDefinition>();

                for (int s = 0; s < slots.Length; ++s)
                {
                    ServerInteractionSlotDefinition slot = slots[s];
                    if (slot == null)
                        continue;

                    Vector3 anchor = Position(slot.anchor);
                    Handles.DrawDottedLine(root, anchor, 3f);
                    Handles.DrawWireDisc(
                        anchor + Vector3.up * 0.01f,
                        Vector3.up,
                        size * 0.45f);

                    if (slot.hasApproach)
                    {
                        Vector3 approach = Position(slot.approach);
                        Handles.DrawDottedLine(approach, anchor, 4f);
                    }

                    if (slot.hasExit)
                    {
                        Vector3 exit = Position(slot.exit);
                        Handles.DrawDottedLine(anchor, exit, 4f);
                    }
                }
            }
        }

        private static bool ShouldDrawLabel(SceneView sceneView, Vector3 position)
        {
            if (_window == null || !_window._labels || sceneView == null || sceneView.camera == null)
                return false;

            float maxDistance = Mathf.Max(5f, _window._labelDistance);
            return (sceneView.camera.transform.position - position).sqrMagnitude <=
                   maxDistance * maxDistance;
        }

        private static void DrawBlockers(CompareFunction zTest)
        {
            ServerDynamicBlocker[] blockers =
                _snapshot.dynamicBlockers ?? Array.Empty<ServerDynamicBlocker>();

            Handles.zTest = zTest;
            for (int i = 0; i < blockers.Length; ++i)
            {
                ServerDynamicBlocker blocker = blockers[i];
                if (blocker == null)
                    continue;

                Matrix4x4 matrix = Matrix4x4.TRS(
                    Position(blocker.pose),
                    Quaternion.Euler(0f, blocker.pose.yaw, 0f),
                    Vector3.one);

                using (new Handles.DrawingScope(BlockerColor, matrix))
                {
                    Handles.DrawWireCube(
                        Vector3.zero,
                        new Vector3(blocker.sizeX, blocker.sizeY, blocker.sizeZ));
                }
            }
        }

        private static void DrawBounds(CompareFunction zTest)
        {
            if (!_hasBounds)
                return;

            Handles.zTest = zTest;
            Handles.color = new Color(BoundsColor.r, BoundsColor.g, BoundsColor.b, 0.9f);
            Handles.DrawWireCube(_bounds.center, _bounds.size);
        }

        private static Vector3 Position(ServerPose pose) =>
            new Vector3(pose.x, pose.y, pose.z);

        private static void FrameBounds()
        {
            if (!_hasBounds || SceneView.lastActiveSceneView == null)
                return;

            SceneView.lastActiveSceneView.Frame(_bounds, false);
        }

        private static void SceneChanged()
        {
            Reload();
        }

        private static void Reload()
        {
            ReleaseMeshes();
            WalkBoundarySegments.Clear();
            SolidSegments.Clear();

            _snapshot = null;
            _sharedWorld = null;
            _loadedPath = ExpectedBakePath();
            _loadedWriteTimeUtc = default;
            _hasBounds = false;

            Scene scene = SceneManager.GetActiveScene();
            ServerMap map = ServerWorldBake.FindMapInActiveScene();
            if (map == null)
            {
                SetStatus(
                    "Active scene has no ServerMap component.",
                    MessageType.Warning);
                SceneView.RepaintAll();
                return;
            }

            if (string.IsNullOrWhiteSpace(_loadedPath) || !File.Exists(_loadedPath))
            {
                SetStatus(
                    "No baked server map exists for this scene yet. Bake the current scene first.",
                    MessageType.Warning);
                SceneView.RepaintAll();
                return;
            }

            try
            {
                string json = File.ReadAllText(_loadedPath);
                _snapshot = JsonUtility.FromJson<ServerMapSnapshot>(json);
                if (_snapshot == null)
                    throw new InvalidDataException(
                        "JsonUtility returned no ServerMapSnapshot.");

                _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(_loadedPath);
                LoadSharedWorldDebugData();
                BuildDebugGeometry();
                EvaluateFreshness(scene, map);
            }
            catch (Exception ex)
            {
                _snapshot = null;
                _sharedWorld = null;
                SetStatus(
                    $"Could not load baked server map: {ex.Message}",
                    MessageType.Error);
            }

            SceneView.RepaintAll();
            _window?.Repaint();
        }

        private static void LoadSharedWorldDebugData()
        {
            _sharedWorld = null;
            if (_snapshot?.sharedWorld == null || string.IsNullOrWhiteSpace(_snapshot.sharedWorld.fileName))
                return;

            string directory = Path.GetDirectoryName(_loadedPath) ?? ServerWorldBake.ServerMapsFolder;
            string path = Path.Combine(directory, Path.GetFileName(_snapshot.sharedWorld.fileName));
            if (!File.Exists(path))
                return;

            SharedWorldSnapshot shared = JsonUtility.FromJson<SharedWorldSnapshot>(File.ReadAllText(path));
            if (shared != null && shared.formatVersion == SharedWorldFormat.Version)
                _sharedWorld = shared;
        }

        private static void EvaluateFreshness(Scene scene, ServerMap map)
        {
            if (_snapshot.formatVersion != ServerMapFormat.Version)
            {
                SetStatus(
                    $"Bake format is V{_snapshot.formatVersion}, but the current code expects V{ServerMapFormat.Version}. Re-bake this scene.",
                    MessageType.Error);
                return;
            }

            if (!string.Equals(
                    ServerMap.NormalizeId(_snapshot.mapId),
                    ServerMap.NormalizeId(map.MapId),
                    StringComparison.Ordinal))
            {
                SetStatus(
                    "Baked map ID does not match the active ServerMap. Re-bake this scene.",
                    MessageType.Error);
                return;
            }

            if (scene.isDirty)
            {
                SetStatus(
                    "Scene has unsaved changes. The current baked server map may be stale.",
                    MessageType.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(scene.path))
            {
                string sceneFile =
                    Path.Combine(ServerWorldBake.ProjectRoot, scene.path);

                if (File.Exists(sceneFile) && _snapshot.bakedUtcTicks > 0)
                {
                    DateTime sceneWrite = File.GetLastWriteTimeUtc(sceneFile);
                    DateTime baked =
                        new DateTime(_snapshot.bakedUtcTicks, DateTimeKind.Utc);

                    if (sceneWrite > baked.AddSeconds(1))
                    {
                        SetStatus(
                            "Scene was saved after this server map bake. Re-bake before testing authoritative movement.",
                            MessageType.Warning);
                        return;
                    }
                }
            }

            SetStatus(
                "Baked server map is current for the active scene.",
                MessageType.Info);
        }

        private static string ExpectedBakePath()
        {
            ServerMap map = ServerWorldBake.FindMapInActiveScene();
            if (map == null || string.IsNullOrWhiteSpace(map.MapId))
                return string.Empty;

            string suffix = string.IsNullOrWhiteSpace(map.InstanceId)
                ? string.Empty
                : "__" + ServerMap.NormalizeId(map.InstanceId);

            return Path.Combine(
                ServerWorldBake.ServerMapsFolder,
                ServerMap.NormalizeId(map.MapId) +
                suffix +
                ".servermap.json");
        }

        private static void BuildDebugGeometry()
        {
            ServerCollisionTriangle[] supportTriangles =
                _sharedWorld?.supportTriangles ?? Array.Empty<ServerCollisionTriangle>();
            ServerCollisionTriangle[] solidTriangles =
                _sharedWorld?.collisionTriangles ?? (_snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>());

            var walkVertices = new List<Vector3>();
            var walkIndices = new List<int>();
            var walkEdges = new Dictionary<EdgeKey, EdgeInfo>();
            var solidEdges = new HashSet<EdgeKey>();

            // V2 debug view deliberately shows the pre-agent-erosion support surface. The
            // old NavMesh-derived walk triangles are only a fallback for pre-V4 bakes.
            if (supportTriangles.Length == 0)
            {
                ServerCollisionTriangle[] legacy = _snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>();
                var legacyWalk = new List<ServerCollisionTriangle>();
                for (int i = 0; i < legacy.Length; ++i)
                    if ((legacy[i].flags & ServerSurfaceFlags.Walkable) != 0)
                        legacyWalk.Add(legacy[i]);
                supportTriangles = legacyWalk.ToArray();
            }

            for (int i = 0; i < supportTriangles.Length; ++i)
            {
                ServerCollisionTriangle t = supportTriangles[i];
                Vector3 a = new Vector3(t.ax, t.ay, t.az);
                Vector3 b = new Vector3(t.bx, t.by, t.bz);
                Vector3 c = new Vector3(t.cx, t.cy, t.cz);
                IncludeBounds(a); IncludeBounds(b); IncludeBounds(c);
                AddTriangle(walkVertices, walkIndices, a, b, c);
                CountEdge(walkEdges, a, b);
                CountEdge(walkEdges, b, c);
                CountEdge(walkEdges, c, a);
            }

            for (int i = 0; i < solidTriangles.Length; ++i)
            {
                ServerCollisionTriangle t = solidTriangles[i];
                Vector3 a = new Vector3(t.ax, t.ay, t.az);
                Vector3 b = new Vector3(t.bx, t.by, t.bz);
                Vector3 c = new Vector3(t.cx, t.cy, t.cz);
                IncludeBounds(a); IncludeBounds(b); IncludeBounds(c);
                AddUniqueEdge(solidEdges, SolidSegments, a, b);
                AddUniqueEdge(solidEdges, SolidSegments, b, c);
                AddUniqueEdge(solidEdges, SolidSegments, c, a);
            }

            if (_sharedWorld?.surfaceEdges != null && _sharedWorld.surfaceEdges.Length > 0)
            {
                for (int i = 0; i < _sharedWorld.surfaceEdges.Length; ++i)
                {
                    ServerSurfaceEdge edge = _sharedWorld.surfaceEdges[i];
                    WalkBoundarySegments.Add(new LineSegment(
                        new Vector3(edge.ax, edge.ay, edge.az),
                        new Vector3(edge.bx, edge.by, edge.bz)));
                }
            }
            else
            {
                foreach (KeyValuePair<EdgeKey, EdgeInfo> pair in walkEdges)
                {
                    if (pair.Value.Count == 1)
                        WalkBoundarySegments.Add(new LineSegment(pair.Value.A, pair.Value.B));
                }
            }

            ServerSpawnAnchor[] spawns =
                _snapshot.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
            for (int i = 0; i < spawns.Length; ++i)
            {
                if (spawns[i] != null)
                    IncludeBounds(Position(spawns[i].pose));
            }

            ServerWorldInteractableDefinition[] interactables =
                _snapshot.interactables ??
                Array.Empty<ServerWorldInteractableDefinition>();
            for (int i = 0; i < interactables.Length; ++i)
            {
                if (interactables[i] != null)
                    IncludeBounds(Position(interactables[i].pose));
            }

            _walkMesh = CreateMesh(
                "Server Map Debug - Walkable",
                walkVertices,
                walkIndices);
        }

        private static void CountEdge(
            Dictionary<EdgeKey, EdgeInfo> edges,
            Vector3 a,
            Vector3 b)
        {
            EdgeKey key = new EdgeKey(a, b);
            if (edges.TryGetValue(key, out EdgeInfo existing))
            {
                existing.Count++;
                edges[key] = existing;
                return;
            }

            edges.Add(key, new EdgeInfo(a, b, 1));
        }

        private static void AddUniqueEdge(
            HashSet<EdgeKey> edges,
            List<LineSegment> segments,
            Vector3 a,
            Vector3 b)
        {
            EdgeKey key = new EdgeKey(a, b);
            if (edges.Add(key))
                segments.Add(new LineSegment(a, b));
        }

        private static void AddTriangle(
            List<Vector3> vertices,
            List<int> indices,
            Vector3 a,
            Vector3 b,
            Vector3 c)
        {
            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            indices.Add(start);
            indices.Add(start + 1);
            indices.Add(start + 2);
        }

        private static Mesh CreateMesh(
            string name,
            List<Vector3> vertices,
            List<int> indices)
        {
            if (vertices.Count == 0)
                return null;

            var mesh = new Mesh
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat =
                    vertices.Count > 65535
                        ? IndexFormat.UInt32
                        : IndexFormat.UInt16,
            };

            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void IncludeBounds(Vector3 point)
        {
            if (!_hasBounds)
            {
                _bounds = new Bounds(point, Vector3.zero);
                _hasBounds = true;
                return;
            }

            _bounds.Encapsulate(point);
        }

        private static Material WalkMaterial
        {
            get
            {
                if (_walkMaterial == null)
                    _walkMaterial = CreateWalkMaterial();
                return _walkMaterial;
            }
        }

        private static Material CreateWalkMaterial()
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                return null;

            var material = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_Cull", (int)CullMode.Off);
            material.SetInt("_ZWrite", 0);
            UpdateWalkMaterial(material);
            return material;
        }

        private static void UpdateWalkMaterial()
        {
            if (_walkMaterial != null)
                UpdateWalkMaterial(_walkMaterial);
        }

        private static void UpdateWalkMaterial(Material material)
        {
            if (material == null)
                return;

            float alpha =
                _window != null
                    ? Mathf.Clamp(_window._walkAlpha, 0.02f, 0.35f)
                    : 0.08f;

            material.color =
                new Color(WalkColor.r, WalkColor.g, WalkColor.b, alpha);
        }

        private static void ReleaseMeshes()
        {
            if (_walkMesh != null)
                DestroyImmediate(_walkMesh);
            if (_walkMaterial != null)
                DestroyImmediate(_walkMaterial);

            _walkMesh = null;
            _walkMaterial = null;
        }

        private static void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
        }

        private void SavePrefs()
        {
            EditorPrefs.SetBool(ShowPref, _show);
            EditorPrefs.SetBool(WalkFillPref, _walkFill);
            EditorPrefs.SetBool(WalkBoundsPref, _walkBounds);
            EditorPrefs.SetBool(WalkTrianglesPref, _walkTriangles);
            EditorPrefs.SetBool(SolidPref, _solid);
            EditorPrefs.SetBool(SpawnPref, _spawns);
            EditorPrefs.SetBool(InteractablePref, _interactables);
            EditorPrefs.SetBool(BlockerPref, _blockers);
            EditorPrefs.SetBool(BoundsPref, _worldBounds);
            EditorPrefs.SetBool(LabelsPref, _labels);
            EditorPrefs.SetBool(XrayPref, _xray);
            EditorPrefs.SetFloat(WalkAlphaPref, _walkAlpha);
            EditorPrefs.SetFloat(MarkerScalePref, _markerScale);
            EditorPrefs.SetFloat(LabelDistancePref, _labelDistance);
        }

        private readonly struct LineSegment
        {
            public readonly Vector3 A;
            public readonly Vector3 B;

            public LineSegment(Vector3 a, Vector3 b)
            {
                A = a;
                B = b;
            }
        }

        private struct EdgeInfo
        {
            public Vector3 A;
            public Vector3 B;
            public int Count;

            public EdgeInfo(Vector3 a, Vector3 b, int count)
            {
                A = a;
                B = b;
                Count = count;
            }
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            private readonly Vector3Int _a;
            private readonly Vector3Int _b;

            public EdgeKey(Vector3 a, Vector3 b)
            {
                Vector3Int qa = Quantize(a);
                Vector3Int qb = Quantize(b);

                if (Compare(qa, qb) <= 0)
                {
                    _a = qa;
                    _b = qb;
                }
                else
                {
                    _a = qb;
                    _b = qa;
                }
            }

            public bool Equals(EdgeKey other) =>
                _a == other._a && _b == other._b;

            public override bool Equals(object obj) =>
                obj is EdgeKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_a.GetHashCode() * 397) ^ _b.GetHashCode();
                }
            }

            private static Vector3Int Quantize(Vector3 value)
            {
                // One millimeter is enough to merge shared baked triangle vertices
                // while keeping genuinely separate floors/ledges distinct.
                return new Vector3Int(
                    Mathf.RoundToInt(value.x * 1000f),
                    Mathf.RoundToInt(value.y * 1000f),
                    Mathf.RoundToInt(value.z * 1000f));
            }

            private static int Compare(Vector3Int a, Vector3Int b)
            {
                if (a.x != b.x)
                    return a.x.CompareTo(b.x);
                if (a.y != b.y)
                    return a.y.CompareTo(b.y);
                return a.z.CompareTo(b.z);
            }
        }

        /// <summary>
        /// Keeps scene-change event plumbing in one place and avoids making the main
        /// debug window depend on callback signature details throughout the file.
        /// </summary>
        private static class EditorSceneManagerBridge
        {
            private static Action _callback;

            public static void Subscribe(Action callback)
            {
                UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode += OnSceneChanged;
                _callback = callback;
            }

            public static void Unsubscribe(Action callback)
            {
                UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode -= OnSceneChanged;
                if (_callback == callback)
                    _callback = null;
            }

            private static void OnSceneChanged(Scene oldScene, Scene newScene)
            {
                _callback?.Invoke();
            }
        }
    }
}
