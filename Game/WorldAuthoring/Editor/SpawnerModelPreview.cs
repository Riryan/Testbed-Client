#if UNITY_EDITOR && !UNITY_SERVER
using System;
using Game.Client.Presentation.Characters;
using Game.Shared.World;
using Game.Server.Application.World;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Temporary humanoid placement preview for a selected Population spawn or doorway.
    /// Reuses the same editable-base prefab and modular appearance path as Player/Population
    /// presentation. Never becomes a scene asset, a ServerSpawn, or a networked actor.
    /// </summary>
    [InitializeOnLoad]
    internal static class SpawnerModelPreview
    {
        private const string PreviewName = "__SpawnerHumanoidPlacementPreview";
        private static GameObject preview;
        private static Transform anchor;
        private static CharacterVisualProfile visualProfile;
        private static int selectionKey;
        private static Vector3 lastPosition;
        private static Quaternion lastRotation;
        // Status material is private to the temporary preview instance. Never recolor
        // the shared Player/Population humanoid prefab or its source materials.
        private static Material statusMaterial;
        private static Renderer[] statusRenderers = Array.Empty<Renderer>();
        private static bool? lastTintGood;

        // The preview uses the identical static scene geometry exported by ServerWorldBake.
        // It is cached per scene; repositioning a spawn only rechecks one capsule.
        private const float FootHeightTolerance = 0.10f;
        private static ServerCollisionWorld placementWorld;
        private static ServerMap placementMap;
        private static int worldSceneHandle = -1;
        private static bool worldDirty = true;
        private static string worldError = string.Empty;
        private static Vector3 checkedPosition = new Vector3(float.NaN, 0f, 0f);
        private static float checkedRadius = -1f;
        private static float checkedHeight = -1f;
        private static float checkedSnap = -1f;
        private static bool checkedPortal;
        private static bool placementOk;
        private static string placementMessage = "Placement not checked";
        private static double lastCheckTime = double.NegativeInfinity;
        private static ServerSpawn cachedTemplate;
        private static PopulationPortal cachedTemplatePortal;
        private static bool cachedTemplateResolved;
        private static bool constructingPreview;
        private static bool pendingRecheck;

        static SpawnerModelPreview()
        {
            Selection.selectionChanged += Refresh;
            EditorApplication.projectChanged += OnGeometryChanged;
            EditorApplication.hierarchyChanged += OnGeometryChanged;
            Undo.postprocessModifications += OnPostprocessModifications;
            SceneView.duringSceneGui += DrawPlacement;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
            EditorApplication.update += Update;
            EditorApplication.delayCall += Refresh;
        }

        private static void OnGeometryChanged()
        {
            if (constructingPreview)
                return;
            worldDirty = true;
            cachedTemplate = null;
            cachedTemplatePortal = null;
            cachedTemplateResolved = false;
            checkedPosition.x = float.NaN;
            SceneView.RepaintAll();
        }

        private static void OnSceneSaved(Scene scene) => OnGeometryChanged();

        private static UndoPropertyModification[] OnPostprocessModifications(
            UndoPropertyModification[] changes)
        {
            // Moving the selected placement anchor only needs one cheap capsule check.
            // Scene geometry modifications invalidate the baked collision preview.
            Transform selectedAnchor = ResolveSelectedAnchor(out ServerSpawn selectedSpawn);
            for (int i = 0; i < changes.Length; i++)
            {
                Object changed = changes[i].currentValue.target;
                if (changed == null || changed == selectedAnchor || changed == selectedSpawn ||
                    changed is PopulationPortal || changed is ServerSpawn)
                    continue;
                OnGeometryChanged();
                break;
            }
            checkedPosition.x = float.NaN;
            return changes;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            Clear();
            if (state == PlayModeStateChange.EnteredEditMode)
                EditorApplication.delayCall += Refresh;
        }

        private static void Refresh()
        {
            Clear();
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                Update();
        }

        private static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (preview != null)
                    Clear();
                return;
            }

            Transform wantedAnchor = ResolveSelectedAnchor(out ServerSpawn selectedSpawn);
            if (wantedAnchor == null)
            {
                if (preview != null)
                    Clear();
                return;
            }

            int wantedKey = Selection.activeInstanceID;
            CharacterVisualProfile wantedProfile = ChooseProfile(selectedSpawn);
            if (wantedProfile == null || wantedProfile.EditableBasePrefab == null)
            {
                if (preview != null)
                    Clear();
                return;
            }

            if (preview == null || anchor != wantedAnchor || selectionKey != wantedKey || visualProfile != wantedProfile)
            {
                Clear();
                CreatePreview(wantedAnchor, wantedProfile, wantedKey);
                return;
            }

            FollowAnchor();
            // A drag may stop during the short validation throttle. Repaint once the
            // next check is due so a newly valid position does not remain red.
            if (pendingRecheck && EditorApplication.timeSinceStartup >= lastCheckTime + 0.08d)
            {
                pendingRecheck = false;
                SceneView.RepaintAll();
            }
        }

        private static Transform ResolveSelectedAnchor(out ServerSpawn selectedSpawn)
        {
            selectedSpawn = null;
            GameObject selected = Selection.activeGameObject;
            if (selected == null || !selected.scene.IsValid())
                return null;

            ServerSpawn spawn = selected.GetComponentInParent<ServerSpawn>();
            if (spawn != null && spawn.kind == ServerSpawnKind.Population &&
                spawn.enabledForServer && spawn.gameObject.activeInHierarchy)
            {
                selectedSpawn = spawn;
                // A portal-backed spawn is baked at the portal's transform, NOT at the
                // optional template ServerSpawn. Show the exact position the server uses.
                if (spawn.populationPortal != null && spawn.populationPortal.gameObject.activeInHierarchy &&
                    spawn.populationPortal.gameObject.scene == spawn.gameObject.scene)
                    return spawn.populationPortal.transform;
                return spawn.transform;
            }

            PopulationPortal portal = selected.GetComponentInParent<PopulationPortal>();
            if (portal != null && portal.gameObject.activeInHierarchy &&
                portal.mode != Game.Shared.Population.PopulationPortalMode.DespawnOnly)
                return portal.transform;

            return null;
        }

        private static CharacterVisualProfile ChooseProfile(ServerSpawn spawn)
        {
            // Police Population uses the existing Police visual profile in the client.
            // All other ambient humanoids use the same default profile as Player visuals.
            if (spawn != null &&
                !string.IsNullOrEmpty(spawn.archetypeId) &&
                spawn.archetypeId.IndexOf("police", StringComparison.OrdinalIgnoreCase) >= 0 &&
                CharacterVisualProfileRegistry.TryResolve(2, out CharacterVisualProfile police) &&
                police != null && police.EditableBasePrefab != null)
                return police;

            return CharacterVisualProfileRegistry.Default;
        }

        private static void CreatePreview(Transform target, CharacterVisualProfile profile, int key)
        {
            GameObject visual = null;
            constructingPreview = true;
            try
            {
                // EditableBasePrefab is the lightweight humanoid MODEL, not PlayerEntity.
                visual = Object.Instantiate(profile.EditableBasePrefab);
                if (visual == null)
                    return;

                visual.name = PreviewName;
                visual.SetActive(false);
                // HideFlags.DontSave excludes this object from saved scenes, builds and
                // the normal scene object searches performed by server-world baking.
                // The extra EditorOnly tag is a secondary safeguard for scene tools.
                Transform[] transforms = visual.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    GameObject go = transforms[i].gameObject;
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.tag = "EditorOnly";
                    go.layer = 2; // Ignore Raycast: placement handles keep selecting the spawner.
                }

                // It is a model preview, not a live character. No colliders, logic,
                // Animator, camera, audio or other simulation systems should run.
                Behaviour[] behaviours = visual.GetComponentsInChildren<Behaviour>(true);
                for (int i = 0; i < behaviours.Length; i++)
                    if (behaviours[i] != null)
                        behaviours[i].enabled = false;

                Collider[] colliders = visual.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                    colliders[i].enabled = false;

                ModularCharacterAppearancePresenter presenter =
                    visual.GetComponent<ModularCharacterAppearancePresenter>();
                if (presenter == null)
                    presenter = visual.AddComponent<ModularCharacterAppearancePresenter>();

                presenter.enabled = false; // Method calls below are explicit, no LateUpdate.
                presenter.Configure(profile);
                presenter.ApplyAppearance(profile.CreateDefaultRecipe());
                if (!BuildStatusMaterial(visual))
                {
                    Debug.LogWarning("[Spawner Model Preview] Could not find a tint-compatible shader; preview will use its original appearance.");
                }

                // Keep the prefab's authored scale; root position and yaw equal the
                // baked spawn anchor. No capsule/ground-snap approximation is applied.
                preview = visual;
                anchor = target;
                visualProfile = profile;
                selectionKey = key;
                lastPosition = new Vector3(float.NaN, 0f, 0f);
                FollowAnchor();
                visual.SetActive(true);
                SceneView.RepaintAll();
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[Spawner Model Preview] Could not construct humanoid preview: {error.Message}");
                if (visual != null)
                    Object.DestroyImmediate(visual);
                preview = null;
                anchor = null;
                visualProfile = null;
                selectionKey = 0;
                DestroyStatusMaterial();
            }
            finally
            {
                constructingPreview = false;
            }
        }

        private static void FollowAnchor()
        {
            if (preview == null || anchor == null)
                return;

            Vector3 position = anchor.position;
            Quaternion rotation = Quaternion.Euler(0f, anchor.eulerAngles.y, 0f);
            if (position == lastPosition && rotation == lastRotation)
                return;

            preview.transform.SetPositionAndRotation(position, rotation);
            lastPosition = position;
            lastRotation = rotation;
            SceneView.RepaintAll();
        }

        private static void DrawPlacement(SceneView view)
        {
            if (preview == null || EditorApplication.isPlayingOrWillChangePlaymode ||
                Event.current.type != EventType.Repaint)
                return;

            Transform selected = ResolveSelectedAnchor(out ServerSpawn selectedSpawn);
            if (selected == null || selected != anchor || !selected.gameObject.scene.IsValid())
                return;

            PopulationPortal portal = selected.GetComponent<PopulationPortal>();
            bool portalBacked = portal != null &&
                portal.mode != Game.Shared.Population.PopulationPortalMode.DespawnOnly;

            // The doorway's transform is the baked spawn pose. In portal-only selection,
            // resolve its authored Spawn template (if present) to get real capsule size.
            if (portalBacked && selectedSpawn == null)
            {
                if (cachedTemplatePortal != portal || !cachedTemplateResolved)
                {
                    cachedTemplatePortal = portal;
                    cachedTemplate = null;
                    cachedTemplateResolved = true;
                    ServerSpawn[] sources = Object.FindObjectsByType<ServerSpawn>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None);
                    for (int i = 0; i < sources.Length; ++i)
                    {
                        ServerSpawn source = sources[i];
                        if (source != null && source.kind == ServerSpawnKind.Population &&
                            source.enabledForServer && source.gameObject.activeInHierarchy &&
                            source.gameObject.scene == portal.gameObject.scene &&
                            (source.populationPortal == portal ||
                             (source.populationPortal == null && source.portalId > 0 &&
                              source.portalId == ServerWorldBake.StableAuthoringId(portal.BakeId))))
                        {
                            cachedTemplate = source;
                            break;
                        }
                    }
                }
                selectedSpawn = cachedTemplate;
            }

            float radius = selectedSpawn != null ? Mathf.Max(0.2f, selectedSpawn.capsuleRadius) : 0.35f;
            float height = selectedSpawn != null
                ? Mathf.Max(selectedSpawn.capsuleHeight, radius * 2f)
                : 1.8f;
            float snap = selectedSpawn != null
                ? Mathf.Max(0.05f, selectedSpawn.maximumGroundSnap)
                : 0.65f;
            CheckPlacement(selected.gameObject.scene, selected.position, radius, height, snap, portalBacked);
            SetStatusTint(placementOk);
        }

        private static bool BuildStatusMaterial(GameObject visual)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("HDRP/Unlit");
            if (shader == null) return false;

            statusMaterial = new Material(shader)
            {
                name = "__SpawnerPlacementStatusMaterial",
                hideFlags = HideFlags.HideAndDontSave,
            };
            statusRenderers = visual.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < statusRenderers.Length; ++i)
            {
                Renderer renderer = statusRenderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;
                // Match every submesh: do not hide a body part that had multiple materials.
                int count = Mathf.Max(1, renderer.sharedMaterials.Length);
                var replacement = new Material[count];
                for (int slot = 0; slot < count; ++slot)
                    replacement[slot] = statusMaterial;
                renderer.sharedMaterials = replacement;
            }

            lastTintGood = null;
            SetStatusTint(false); // Fail closed until the first placement check completes.
            return true;
        }

        private static void SetStatusTint(bool good)
        {
            if (statusMaterial == null || lastTintGood == good)
                return;
            Color tint = good
                ? new Color(0.12f, 0.95f, 0.24f, 1f)
                : new Color(1f, 0.08f, 0.10f, 1f);
            if (statusMaterial.HasProperty("_BaseColor"))
                statusMaterial.SetColor("_BaseColor", tint);
            if (statusMaterial.HasProperty("_Color"))
                statusMaterial.SetColor("_Color", tint);
            lastTintGood = good;
            SceneView.RepaintAll();
        }

        private static void DestroyStatusMaterial()
        {
            statusRenderers = Array.Empty<Renderer>();
            lastTintGood = null;
            if (statusMaterial != null)
                Object.DestroyImmediate(statusMaterial);
            statusMaterial = null;
        }

        private static void CheckPlacement(
            Scene scene, Vector3 position, float radius, float height, float snap,
            bool portalBacked)
        {
            if (worldSceneHandle != scene.handle || worldDirty)
            {
                worldSceneHandle = scene.handle;
                worldDirty = false;
                placementWorld = null;
                placementMap = null;
                worldError = string.Empty;
                try
                {
                    if (!ServerWorldBake.TryBuildSpawnPlacementWorld(
                            scene, out placementMap, out placementWorld, out worldError))
                    {
                        placementWorld = null;
                    }
                }
                catch (Exception error)
                {
                    worldError = "Collision preview failed: " + error.Message;
                    placementWorld = null;
                }
                checkedPosition.x = float.NaN;
            }

            if (placementWorld == null)
            {
                placementOk = false;
                placementMessage = string.IsNullOrWhiteSpace(worldError)
                    ? "No baked collision preview"
                    : worldError;
                return;
            }

            // Dragging remains responsive and rechecks a single actor, not the whole map.
            // A pending check is intentionally NOT shown as green for the new location.
            bool changed = checkedPosition != position || checkedRadius != radius ||
                checkedHeight != height || checkedSnap != snap || checkedPortal != portalBacked;
            if (!changed)
                return;

            if (EditorApplication.timeSinceStartup < lastCheckTime + 0.08d)
            {
                placementOk = false;
                placementMessage = "Checking placement";
                pendingRecheck = true;
                return;
            }

            lastCheckTime = EditorApplication.timeSinceStartup;
            pendingRecheck = false;
            checkedPosition = position;
            checkedRadius = radius;
            checkedHeight = height;
            checkedSnap = snap;
            checkedPortal = portalBacked;

            // Same collision world, capsule and slope policy the GameServer consumes.
            // Ground corrections are allowed by the server, but an authored point that
            // needs more than 10cm correction is NOT marked green: visual placement
            // should be nearly exact, especially for portal activation.
            // Server runtime uses a 55-degree limit for direct spawn admission.
            float slope = Mathf.Min(55f, Mathf.Clamp(placementMap.NavAgentMaxSlope, 0f, 89f));
            if (portalBacked)
            {
                snap = Mathf.Max(0.65f, placementMap.NavAgentMaxClimb + 0.25f);
                slope = Mathf.Min(55f, slope);
            }
            bool grounded = placementWorld.TryValidateSpawn(
                new ServerPose(position.x, position.y, position.z, 0f),
                new ServerCapsule(radius, height), snap, slope,
                out ServerPose resolved, out string failure);
            if (!grounded)
            {
                placementOk = false;
                placementMessage = string.IsNullOrWhiteSpace(failure)
                    ? "Ground/slope/clearance invalid"
                    : failure;
                return;
            }

            float heightError = Mathf.Abs(resolved.y - position.y);
            placementOk = heightError <= FootHeightTolerance;
            placementMessage = placementOk
                ? $"grounded; foot offset {heightError:0.000} m"
                : $"adjust feet height by {heightError:0.000} m (max {FootHeightTolerance:0.00} m)";
        }

        private static void Clear()
        {
            if (preview != null)
                Object.DestroyImmediate(preview);
            DestroyStatusMaterial();
            preview = null;
            anchor = null;
            visualProfile = null;
            selectionKey = 0;
        }
    }
}
#endif
