using System.Collections.Generic;
using Game.Shared.Characters;
using UnityEngine;
using Player.Client.Presentation;
using Player.Networking;
using UnityEngine.UI;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only Character Select preview. The actor is never network spawned and has
    /// no gameplay authority. Missing local appearance cache deliberately shows the basic
    /// profile/default character, making clean installs safe without a login appearance blob.
    /// </summary>
    public sealed class CharacterSelectPreviewController : MonoBehaviour
    {
        private const string DefaultStageResourcePath = "MMO/Characters/CharacterCreatorPreviewStage";
        [SerializeField] private RawImage previewImage;
        [SerializeField] private Text previewNameText;
        [SerializeField] private GameObject stagePrefab;
        [SerializeField] private GameObject defaultPreviewPrefab;
        [Tooltip("Optional creator-only visual profile. Zero auto-detects from the preview prefab and falls back to the default profile.")]
        [SerializeField] private ushort creatorVisualProfileIdOverride;
        [SerializeField, Range(128, 2048)] private int renderTextureSize = 768;
        [SerializeField] private int previewLayer = 31;
        [SerializeField] private float rotationDegreesPerPixel = 0.35f;
        [SerializeField] private Vector3 previewWorldOrigin = new Vector3(10000f, 10000f, 10000f);

        private static int _nextPreviewInstanceSlot;
        private static CharacterSelectPreviewController _activePreviewOwner;
        private int _previewInstanceSlot = -1;
        private string _lastDisplayName = string.Empty;

        private GameObject _stageRoot;
        private GameObject _actor;
        private Camera _camera;
        private RenderTexture _texture;
        private CharacterPreviewStage _stage;
        private GameObject _creatorSculptRoot;
        private readonly HashSet<string> _creatorDirectSculptSemantics =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        private Vector3 _orbitTarget;
        private float _orbitDistance;
        private float _orbitYaw;
        private float _orbitPitch;
        private bool _orbitReady;
        private CharacterCreatorCategory _focusedCreatorCategory = CharacterCreatorCategory.Body;
        private long _characterId;

        public long CharacterId => _characterId;
        public GameObject CurrentActor => _actor;
        public Camera PreviewCamera => _camera;


        /// <summary>
        /// Resolves which client visual capability profile the Character Creator should expose.
        /// This is presentation-only: it does not create a second appearance state or network path.
        /// </summary>
        public ushort ResolveCreatorVisualProfileId()
        {
            if (creatorVisualProfileIdOverride != 0 &&
                CharacterVisualProfileRegistry.TryResolve(creatorVisualProfileIdOverride, out CharacterVisualProfile explicitProfile))
                return explicitProfile.VisualProfileId;

            if (TryResolveProfileIdFromPrefab(defaultPreviewPrefab, out ushort prefabProfileId))
                return prefabProfileId;

            CharacterVisualProfile fallback = CharacterVisualProfileRegistry.Default;
            return fallback != null ? fallback.VisualProfileId : (ushort)0;
        }

        private static bool TryResolveProfileIdFromPrefab(GameObject prefab, out ushort profileId)
        {
            profileId = 0;
            if (prefab == null)
                return false;

            MonoBehaviour[] behaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; ++i)
            {
                if (behaviours[i] is ICharacterAppearancePresenter presenter && presenter.VisualProfileId != 0 &&
                    CharacterVisualProfileRegistry.TryResolve(presenter.VisualProfileId, out _))
                {
                    profileId = presenter.VisualProfileId;
                    return true;
                }
            }
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetPreviewInstanceSlots()
        {
            _nextPreviewInstanceSlot = 0;
            _activePreviewOwner = null;
        }

        private void Awake()
        {
            if (_previewInstanceSlot < 0)
                _previewInstanceSlot = _nextPreviewInstanceSlot++;
        }

        private Vector3 EffectivePreviewWorldOrigin()
        {
            if (_previewInstanceSlot < 0)
                _previewInstanceSlot = _nextPreviewInstanceSlot++;
            // Character Select and Character Creator can exist at the same time. Their
            // cameras use the same preview layer, so isolate each preview stage spatially
            // beyond the camera far clip instead of allowing one camera to see both actors.
            return previewWorldOrigin + new Vector3(_previewInstanceSlot * 64f, 0f, 0f);
        }

        public void ConfigureRuntimeSurface(RawImage image, Text nameText)
        {
            previewImage = image;
            previewNameText = nameText;
            if (_texture != null && previewImage != null)
                previewImage.texture = _texture;
        }

        private void OnDisable()
        {
            if (_camera != null)
                _camera.enabled = false;
        }

        private void OnEnable()
        {
            if (_camera != null)
            {
                _camera.enabled = _actor != null;
                return;
            }

            // Character Select can regain ownership after the Creator closes. The Creator
            // deliberately destroys the old Select preview stage while it owns preview
            // presentation, so rebuild the selected character when this surface becomes
            // active again.
            if (_characterId > 0)
                ShowCharacter(_characterId, _lastDisplayName);
        }

        private void OnDestroy() => ReleaseRuntimeObjects();

        public void ShowCharacter(long characterId, string displayName)
        {
            _characterId = characterId;
            _lastDisplayName = displayName ?? string.Empty;
            EnsureStage();
            if (_stageRoot == null)
                return;
            if (_camera != null)
                _camera.enabled = true;
            DestroyActor();
            ClearStalePreviewActors();

            CharacterAppearanceRecipe appearance;
            if (!CharacterSelectAppearanceCache.TryLoad(characterId, out appearance))
                appearance = CharacterVisualProfileRegistry.CreateDefaultRecipeOrFallback();
            appearance = CharacterVisualProfileRegistry.NormalizeForPresentation(appearance);

            _actor = CreatePreviewActor(appearance);
            _actor.name = string.IsNullOrWhiteSpace(displayName)
                ? "CharacterPreviewActor"
                : "CharacterPreviewActor " + displayName;
            SetLayerRecursively(_actor, EffectivePreviewLayer());

            Transform anchor = _stage != null ? _stage.CharacterAnchor : _stageRoot.transform;
            _actor.transform.SetParent(anchor, false);
            _actor.transform.localPosition = Vector3.zero;
            _actor.transform.localRotation = Quaternion.identity;

            CharacterPresentationPreferences presentation;
            if (!CharacterSelectPresentationCache.TryLoad(characterId, out presentation))
                presentation = CharacterPresentationPreferences.CreateDefault();

            ApplyAppearanceToActor(_actor, appearance);
            ConfigurePreviewRenderers(_actor);

            PlayerEquipmentVisualSelection[] equipmentVisuals;
            if (!CharacterVisualLocalCache.TryLoadEquipment(
                    characterId,
                    out _,
                    out equipmentVisuals))
            {
                equipmentVisuals = System.Array.Empty<PlayerEquipmentVisualSelection>();
            }
            ApplyEquipmentToActor(_actor, appearance, equipmentVisuals);
            ConfigurePreviewRenderers(_actor);
            ApplyPresentationToActor(_actor, presentation);
            if (previewNameText != null)
                previewNameText.text = displayName ?? string.Empty;
        }

        public void ShowWorkingCharacter(
            string displayName,
            CharacterAppearanceRecipe appearance,
            CharacterPresentationPreferences presentation)
        {
            _characterId = 0;
            _lastDisplayName = displayName ?? string.Empty;
            EnsureStage();
            if (_stageRoot == null)
                return;
            if (_camera != null)
                _camera.enabled = true;
            DestroyActor();
            ClearStalePreviewActors();

            CharacterAppearanceRecipe effectiveAppearance =
                CharacterVisualProfileRegistry.NormalizeForPresentation(
                    appearance ?? CharacterVisualProfileRegistry.CreateDefaultRecipeOrFallback());

            _actor = CreatePreviewActor(effectiveAppearance);
            _actor.name = string.IsNullOrWhiteSpace(displayName)
                ? "CharacterCreatorPreviewActor"
                : "CharacterCreatorPreviewActor " + displayName;
            SetLayerRecursively(_actor, EffectivePreviewLayer());

            Transform anchor = _stage != null ? _stage.CharacterAnchor : _stageRoot.transform;
            _actor.transform.SetParent(anchor, false);
            _actor.transform.localPosition = Vector3.zero;
            _actor.transform.localRotation = Quaternion.identity;

            ApplyAppearanceToActor(_actor, effectiveAppearance);
            ConfigurePreviewRenderers(_actor);
            BuildCreatorSculptRegions(_actor);
            ApplyPresentationToActor(
                _actor,
                presentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault());
            if (previewNameText != null)
                previewNameText.text = displayName ?? string.Empty;
        }

        public void ApplyWorkingAppearance(CharacterAppearanceRecipe appearance)
        {
            if (_actor == null)
                return;
            ApplyAppearanceToActor(
                _actor,
                CharacterVisualProfileRegistry.NormalizeForPresentation(appearance));
            ConfigurePreviewRenderers(_actor);
        }

        public void SetPreviewDisplayName(string displayName)
        {
            _lastDisplayName = displayName ?? string.Empty;
            if (previewNameText != null)
                previewNameText.text = _lastDisplayName;
        }

        public void ApplyWorkingPresentation(CharacterPresentationPreferences presentation)
        {
            if (_actor == null)
                return;
            ApplyPresentationToActor(
                _actor,
                presentation?.Clone() ?? CharacterPresentationPreferences.CreateDefault());
        }

        public void PreviewIdle() => DispatchLocomotionPreview(0);
        public void PreviewWalk() => DispatchLocomotionPreview(1);
        public void PreviewRun() => DispatchLocomotionPreview(2);

        public void ClearPreview()
        {
            _characterId = 0;
            _lastDisplayName = string.Empty;
            if (previewNameText != null)
                previewNameText.text = string.Empty;

            // A closed/cleared preview must not leave an independent camera/stage/actor
            // alive in the scene. Character Select and Character Creator are mutually
            // exclusive consumers of one preview presentation slot.
            ReleaseRuntimeObjects();
        }

        public bool TryPickCreatorSculptRegion(
            Vector2 screenPosition,
            Camera eventCamera,
            out CharacterCreatorSculptRegion region)
        {
            region = null;
            if (_camera == null || _texture == null || previewImage == null || _actor == null)
                return false;

            RectTransform rect = previewImage.rectTransform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPosition, eventCamera, out Vector2 local))
                return false;

            Rect bounds = rect.rect;
            if (bounds.width <= 1f || bounds.height <= 1f)
                return false;
            float u = Mathf.InverseLerp(bounds.xMin, bounds.xMax, local.x);
            float v = Mathf.InverseLerp(bounds.yMin, bounds.yMax, local.y);
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return false;

            Ray ray = _camera.ViewportPointToRay(new Vector3(u, v, 0f));
            int layerMask = 1 << EffectivePreviewLayer();
            RaycastHit[] hits = Physics.RaycastAll(ray, 20f, layerMask, QueryTriggerInteraction.Collide);
            if (hits == null || hits.Length == 0)
                return false;

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; ++i)
            {
                CharacterCreatorSculptRegion candidate = hits[i].collider != null
                    ? hits[i].collider.GetComponent<CharacterCreatorSculptRegion>()
                    : null;
                if (candidate == null && hits[i].collider != null)
                    candidate = hits[i].collider.GetComponentInParent<CharacterCreatorSculptRegion>();
                if (candidate == null)
                    continue;
                region = candidate;
                return true;
            }
            return false;
        }

        public void Rotate(float pointerDeltaX)
        {
            Orbit(pointerDeltaX, 0f);
        }

        /// <summary>
        /// Orbits the preview camera around the authored focus target. The character model
        /// itself remains at its authored transform, matching the higher-fidelity creator
        /// workflow and avoiding presentation-state changes just to inspect another angle.
        /// </summary>
        public void Orbit(float pointerDeltaX, float pointerDeltaY)
        {
            if (_camera == null || _actor == null)
                return;

            if (!_orbitReady)
                CaptureOrbitFromCurrentCamera();
            if (!_orbitReady)
                return;

            _orbitYaw -= pointerDeltaX * rotationDegreesPerPixel;
            _orbitPitch = Mathf.Clamp(
                _orbitPitch + pointerDeltaY * rotationDegreesPerPixel,
                -35f,
                78f);
            ApplyOrbitPose();
        }

        public void Zoom(float scrollDelta)
        {
            if (_camera == null || Mathf.Approximately(scrollDelta, 0f))
                return;
            _camera.fieldOfView = Mathf.Clamp(_camera.fieldOfView - scrollDelta * 2.25f, 18f, 42f);
        }

        public void FocusCreatorCategory(CharacterCreatorCategory category)
        {
            EnsureStage();
            if (_camera == null || _stageRoot == null)
                return;

            _focusedCreatorCategory = category;
            if (_stage != null && _stage.TryGetCreatorView(category, out Transform cameraAnchor, out Transform target, out float fov))
            {
                _camera.transform.SetPositionAndRotation(cameraAnchor.position, cameraAnchor.rotation);
                _camera.transform.LookAt(target.position);
                _camera.fieldOfView = fov;
                CaptureOrbit(target.position);
                return;
            }

            // Compatibility fallback for older serialized stage assets. The final prefab
            // already contains authored anchors; this path does not rebuild or mutate it.
            if (category == CharacterCreatorCategory.Face || category == CharacterCreatorCategory.Hair)
                ApplyCameraView(new Vector3(0f, 1.63f, 2.2f), new Vector3(0f, 1.58f, 0f), 25f);
            else
                ApplyCameraView(new Vector3(0f, 1.2f, 4.6f), new Vector3(0f, 1.05f, 0f), 30f);
        }

        private void ApplyCameraView(Vector3 localPosition, Vector3 localTarget, float fieldOfView)
        {
            if (_camera == null || _stageRoot == null)
                return;
            Vector3 target = _stageRoot.transform.TransformPoint(localTarget);
            _camera.transform.position = _stageRoot.transform.TransformPoint(localPosition);
            _camera.transform.LookAt(target);
            _camera.fieldOfView = fieldOfView;
            CaptureOrbit(target);
        }

        private void CaptureOrbitFromCurrentCamera()
        {
            if (_stage != null && _stage.TryGetCreatorView(
                    _focusedCreatorCategory,
                    out _,
                    out Transform target,
                    out _))
            {
                CaptureOrbit(target.position);
                return;
            }

            if (_stageRoot != null)
                CaptureOrbit(_stageRoot.transform.TransformPoint(new Vector3(0f, 1.10f, 0f)));
        }

        private void CaptureOrbit(Vector3 target)
        {
            if (_camera == null)
            {
                _orbitReady = false;
                return;
            }

            Vector3 offset = _camera.transform.position - target;
            float distance = offset.magnitude;
            if (distance <= 0.05f)
            {
                _orbitReady = false;
                return;
            }

            _orbitTarget = target;
            _orbitDistance = distance;
            _orbitYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            _orbitPitch = Mathf.Asin(Mathf.Clamp(offset.y / distance, -1f, 1f)) * Mathf.Rad2Deg;
            _orbitReady = true;
        }

        private void ApplyOrbitPose()
        {
            if (_camera == null || !_orbitReady)
                return;

            Quaternion orbit = Quaternion.Euler(-_orbitPitch, _orbitYaw, 0f);
            _camera.transform.position = _orbitTarget + orbit * (Vector3.forward * _orbitDistance);
            _camera.transform.LookAt(_orbitTarget);
        }

        private void EnsureStage()
        {
            if (_stageRoot != null)
            {
                _activePreviewOwner = this;
                return;
            }

            if (_activePreviewOwner != null && _activePreviewOwner != this)
                _activePreviewOwner.ReleaseRuntimeObjects();
            _activePreviewOwner = this;

            int layer = EffectivePreviewLayer();
            Vector3 stageOrigin = EffectivePreviewWorldOrigin();
            GameObject prefab = stagePrefab != null
                ? stagePrefab
                : Resources.Load<GameObject>(DefaultStageResourcePath);
            if (prefab == null)
            {
                Debug.LogError(
                    "[CharacterPreview] Canonical preview stage prefab is missing. Restore " +
                    "Assets/Game/Client/Resources/MMO/Characters/CharacterCreatorPreviewStage.prefab.");
                _activePreviewOwner = null;
                return;
            }

            _stageRoot = Instantiate(prefab);
            _stageRoot.name = "CharacterSelectPreviewStage_Runtime";
            _stageRoot.transform.position = stageOrigin;
            _stage = _stageRoot.GetComponentInChildren<CharacterPreviewStage>(true);
            if (_stage == null)
            {
                Debug.LogError("[CharacterPreview] Preview stage prefab has no CharacterPreviewStage component.");
                Destroy(_stageRoot);
                _stageRoot = null;
                _activePreviewOwner = null;
                return;
            }

            SetLayerRecursively(_stageRoot, layer);
            NormalizeAuthoredStageLightMasks(layer);
            BuildCamera(layer);
        }

        private void NormalizeAuthoredStageLightMasks(int layer)
        {
            if (_stageRoot == null)
                return;

            int mask = 1 << layer;
            Light[] lights = _stageRoot.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; ++i)
            {
                Light light = lights[i];
                if (light == null)
                    continue;
                light.enabled = true;
                light.cullingMask = mask;
                light.shadows = LightShadows.None;
            }
        }

        private void BuildCamera(int layer)
        {
            if (_camera != null)
                return;

            GameObject cameraGo = new GameObject("CharacterSelectPreviewCamera", typeof(Camera));
            cameraGo.transform.SetParent(_stageRoot.transform, false);
            Transform cameraAnchor = _stage != null ? _stage.FullBodyCameraAnchor : null;
            Transform target = _stage != null ? _stage.FullBodyCameraTarget : null;
            cameraGo.transform.position = cameraAnchor != null
                ? cameraAnchor.position
                : _stageRoot.transform.position + new Vector3(0f, 1.2f, 4.6f);
            cameraGo.transform.rotation = Quaternion.identity;
            if (target != null)
                cameraGo.transform.LookAt(target.position);

            _camera = cameraGo.GetComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.035f, 0.235f, 0.285f, 1f);
            _camera.cullingMask = 1 << layer;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 30f;
            _camera.fieldOfView = _stage != null ? _stage.FullBodyFieldOfView : 30f;
            _camera.allowHDR = true;
            _camera.allowMSAA = true;

            int maxDimension = Mathf.Clamp(renderTextureSize, 128, 2048);
            float aspect = 1f;
            if (previewImage != null)
            {
                Canvas.ForceUpdateCanvases();
                Rect surface = previewImage.rectTransform.rect;
                if (surface.width > 1f && surface.height > 1f)
                    aspect = Mathf.Clamp(surface.width / surface.height, 0.5f, 2.5f);
            }
            int textureWidth;
            int textureHeight;
            if (aspect >= 1f)
            {
                textureWidth = maxDimension;
                textureHeight = Mathf.Max(128, Mathf.RoundToInt(maxDimension / aspect));
            }
            else
            {
                textureHeight = maxDimension;
                textureWidth = Mathf.Max(128, Mathf.RoundToInt(maxDimension * aspect));
            }
            _texture = new RenderTexture(textureWidth, textureHeight, 24, RenderTextureFormat.ARGBHalf)
            {
                name = "CharacterSelectPreviewRT",
                antiAliasing = 2,
                useMipMap = false,
                autoGenerateMips = false,
            };
            _texture.Create();
            _camera.targetTexture = _texture;
            if (previewImage != null)
                previewImage.texture = _texture;
        }

        private GameObject CreatePreviewActor(CharacterAppearanceRecipe appearance)
        {
            if (appearance != null &&
                CharacterVisualProfileRegistry.TryResolve(appearance.visualProfileId, out CharacterVisualProfile profile) &&
                profile.EditableBasePrefab != null)
            {
                GameObject actor = Instantiate(profile.EditableBasePrefab);
                ModularCharacterAppearancePresenter presenter =
                    actor.GetComponent<ModularCharacterAppearancePresenter>();
                if (presenter == null)
                    presenter = actor.AddComponent<ModularCharacterAppearancePresenter>();
                presenter.Configure(profile);
                return actor;
            }

            if (defaultPreviewPrefab != null)
                return Instantiate(defaultPreviewPrefab);

            var fallback = new GameObject("BasicCharacterPreview");
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(fallback.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale = new Vector3(0.72f, 1f, 0.72f);
            Collider collider = body.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);
            return fallback;
        }

        private static void ApplyAppearanceToActor(GameObject actor, CharacterAppearanceRecipe appearance)
        {
            if (actor == null || appearance == null)
                return;

            if (CharacterVisualProfileRegistry.TryResolve(appearance.visualProfileId, out CharacterVisualProfile profile))
            {
                ModularCharacterAppearancePresenter modular =
                    actor.GetComponent<ModularCharacterAppearancePresenter>();
                if (modular == null && profile.EditableBasePrefab != null)
                    modular = actor.AddComponent<ModularCharacterAppearancePresenter>();
                modular?.Configure(profile);
                modular?.ApplyAppearance(appearance.Clone());
            }

            MonoBehaviour[] behaviours = actor.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; ++i)
            {
                if (behaviours[i] is ModularCharacterAppearancePresenter)
                    continue;
                if (behaviours[i] is ICharacterAppearancePresenter presenter &&
                    presenter.VisualProfileId == appearance.visualProfileId)
                {
                    presenter.ApplyAppearance(appearance.Clone());
                }
            }
        }

        private static void ApplyEquipmentToActor(
            GameObject actor,
            CharacterAppearanceRecipe appearance,
            PlayerEquipmentVisualSelection[] equipmentVisuals)
        {
            if (actor == null || appearance == null ||
                !CharacterVisualProfileRegistry.TryResolve(appearance.visualProfileId, out CharacterVisualProfile profile))
                return;

            ModularCharacterAppearancePresenter modular =
                actor.GetComponent<ModularCharacterAppearancePresenter>();
            if (modular == null)
                return;

            CharacterMeshSelection[] overrides =
                PlayerEquipmentVisualCatalog.ResolveMeshOverrides(
                    profile,
                    equipmentVisuals ?? System.Array.Empty<PlayerEquipmentVisualSelection>());
            modular.ApplyEquipmentMeshOverrides(overrides);
        }

        private static void ApplyPresentationToActor(
            GameObject actor,
            CharacterPresentationPreferences presentation)
        {
            if (actor == null || presentation == null)
                return;

            MonoBehaviour[] behaviours = actor.GetComponentsInChildren<MonoBehaviour>(true);
            bool applied = false;
            for (int i = 0; i < behaviours.Length; ++i)
            {
                if (behaviours[i] is ICharacterPresentationPreferencesPresenter presenter)
                {
                    presenter.ApplyPresentationPreferences(presentation.Clone());
                    applied = true;
                }
            }

            // Migration convenience for the first modular humanoid hookup: if the actor
            // already has the canonical Animator but no explicit adapter yet, attach the
            // client-only adapter locally. No server or shared code learns Animator names.
            if (!applied && actor.GetComponentInChildren<Animator>(true) != null)
            {
                HumanoidAnimatorPresentationAdapter adapter =
                    actor.GetComponent<HumanoidAnimatorPresentationAdapter>();
                if (adapter == null)
                    adapter = actor.AddComponent<HumanoidAnimatorPresentationAdapter>();
                adapter.ApplyPresentationPreferences(presentation.Clone());
            }
        }

        private void DispatchLocomotionPreview(int mode)
        {
            if (_actor == null)
                return;

            MonoBehaviour[] behaviours = _actor.GetComponentsInChildren<MonoBehaviour>(true);
            bool applied = false;
            for (int i = 0; i < behaviours.Length; ++i)
            {
                if (!(behaviours[i] is ICharacterCreatorLocomotionPreview preview))
                    continue;

                applied = true;
                if (mode == 1)
                    preview.PreviewWalk();
                else if (mode == 2)
                    preview.PreviewRun();
                else
                    preview.PreviewIdle();
            }

            if (!applied && _actor.GetComponentInChildren<Animator>(true) != null)
            {
                HumanoidAnimatorPresentationAdapter adapter =
                    _actor.GetComponent<HumanoidAnimatorPresentationAdapter>();
                if (adapter == null)
                    adapter = _actor.AddComponent<HumanoidAnimatorPresentationAdapter>();
                if (mode == 1)
                    adapter.PreviewWalk();
                else if (mode == 2)
                    adapter.PreviewRun();
                else
                    adapter.PreviewIdle();
            }
        }

        public bool SupportsDirectSculptSemantic(string semanticName)
        {
            return !string.IsNullOrWhiteSpace(semanticName) &&
                   _creatorDirectSculptSemantics.Contains(semanticName);
        }

        public bool HasDirectSculptForCategory(CharacterCreatorCategory category)
        {
            if (_creatorDirectSculptSemantics.Count == 0)
                return false;

            CharacterVisualProfile profile = null;
            if (_actor != null)
            {
                MonoBehaviour[] behaviours = _actor.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < behaviours.Length; ++i)
                {
                    if (behaviours[i] is ICharacterAppearancePresenter presenter &&
                        CharacterVisualProfileRegistry.TryResolve(presenter.VisualProfileId, out profile))
                        break;
                }
            }
            if (profile == null)
                profile = CharacterVisualProfileRegistry.Default;
            if (profile == null)
                return false;

            IReadOnlyList<CharacterVisualMorphDefinition> morphs = profile.Morphs;
            for (int i = 0; i < morphs.Count; ++i)
            {
                CharacterVisualMorphDefinition morph = morphs[i];
                if (morph != null && morph.category == category && morph.visibleInCreator &&
                    morph.persistInAppearance && SupportsDirectSculptSemantic(morph.semanticName))
                    return true;
            }
            return false;
        }

        private void BuildCreatorSculptRegions(GameObject actor)
        {
            if (_creatorSculptRoot != null)
                Destroy(_creatorSculptRoot);
            _creatorSculptRoot = null;
            _creatorDirectSculptSemantics.Clear();
            if (actor == null)
                return;

            _creatorSculptRoot = new GameObject("__CreatorSculptRegions");
            _creatorSculptRoot.layer = EffectivePreviewLayer();
            _creatorSculptRoot.transform.SetParent(actor.transform, false);

            Animator animator = actor.GetComponentInChildren<Animator>(true);
            Transform head = GetHumanoidOrNamed(animator, HumanBodyBones.Head, actor.transform, "head", "CC_Base_Head");
            Transform neck = GetHumanoidOrNamed(animator, HumanBodyBones.Neck, actor.transform, "neck_01", "neck");
            Transform spine1 = GetHumanoidOrNamed(animator, HumanBodyBones.Spine, actor.transform, "spine_01", "spine");
            Transform spine2 = GetHumanoidOrNamed(animator, HumanBodyBones.Chest, actor.transform, "spine_02", "chest");
            Transform spine3 = GetHumanoidOrNamed(animator, HumanBodyBones.UpperChest, actor.transform, "spine_03", "upperchest", "upper_chest");
            if (spine3 == null) spine3 = spine2 != null ? spine2 : spine1;
            Transform pelvis = GetHumanoidOrNamed(animator, HumanBodyBones.Hips, actor.transform, "pelvis", "hips");
            Transform clavicleL = GetHumanoidOrNamed(animator, HumanBodyBones.LeftShoulder, actor.transform, "clavicle_l", "leftshoulder");
            Transform clavicleR = GetHumanoidOrNamed(animator, HumanBodyBones.RightShoulder, actor.transform, "clavicle_r", "rightshoulder");
            Transform upperArmL = GetHumanoidOrNamed(animator, HumanBodyBones.LeftUpperArm, actor.transform, "upperarm_l", "leftupperarm");
            Transform upperArmR = GetHumanoidOrNamed(animator, HumanBodyBones.RightUpperArm, actor.transform, "upperarm_r", "rightupperarm");
            Transform lowerArmL = GetHumanoidOrNamed(animator, HumanBodyBones.LeftLowerArm, actor.transform, "lowerarm_l", "leftlowerarm");
            Transform lowerArmR = GetHumanoidOrNamed(animator, HumanBodyBones.RightLowerArm, actor.transform, "lowerarm_r", "rightlowerarm");
            Transform handL = GetHumanoidOrNamed(animator, HumanBodyBones.LeftHand, actor.transform, "hand_l", "lefthand");
            Transform handR = GetHumanoidOrNamed(animator, HumanBodyBones.RightHand, actor.transform, "hand_r", "righthand");
            Transform thighL = GetHumanoidOrNamed(animator, HumanBodyBones.LeftUpperLeg, actor.transform, "thigh_l", "leftupleg");
            Transform thighR = GetHumanoidOrNamed(animator, HumanBodyBones.RightUpperLeg, actor.transform, "thigh_r", "rightupleg");
            Transform calfL = GetHumanoidOrNamed(animator, HumanBodyBones.LeftLowerLeg, actor.transform, "calf_l", "leftleg");
            Transform calfR = GetHumanoidOrNamed(animator, HumanBodyBones.RightLowerLeg, actor.transform, "calf_r", "rightleg");
            Transform footL = GetHumanoidOrNamed(animator, HumanBodyBones.LeftFoot, actor.transform, "foot_l", "leftfoot");
            Transform footR = GetHumanoidOrNamed(animator, HumanBodyBones.RightFoot, actor.transform, "foot_r", "rightfoot");

            AddBoxSculptRegion("Head", head, head, new Vector3(0.50f, 0.52f, 0.44f),
                new[] { "headSize" }, System.Array.Empty<string>());
            AddBoxSculptRegion("Shoulders", clavicleL, clavicleR, new Vector3(1.10f, 0.34f, 0.44f),
                new[] { "shoulderWidth" }, new[] { "torsoLength" });
            AddBoxSculptRegion("Torso", spine1, spine3, new Vector3(0.72f, 0.64f, 0.46f),
                new[] { "waistSize" }, new[] { "torsoLength" });
            AddBoxSculptRegion("Hips", pelvis, pelvis, new Vector3(0.76f, 0.36f, 0.48f),
                new[] { "hipWidth" }, new[] { "height" });
            AddBoxSculptRegion("UpperArmL", upperArmL, lowerArmL, new Vector3(0.30f, 0.52f, 0.32f),
                new[] { "upperArmSize" }, new[] { "armLength" });
            AddBoxSculptRegion("UpperArmR", upperArmR, lowerArmR, new Vector3(0.30f, 0.52f, 0.32f),
                new[] { "upperArmSize" }, new[] { "armLength" });
            AddBoxSculptRegion("ForearmL", lowerArmL, handL, new Vector3(0.26f, 0.48f, 0.28f),
                new[] { "forearmSize" }, new[] { "forearmLength" });
            AddBoxSculptRegion("ForearmR", lowerArmR, handR, new Vector3(0.26f, 0.48f, 0.28f),
                new[] { "forearmSize" }, new[] { "forearmLength" });
            AddBoxSculptRegion("ThighL", thighL, calfL, new Vector3(0.34f, 0.62f, 0.38f),
                new[] { "thighSize" }, new[] { "legLength" });
            AddBoxSculptRegion("ThighR", thighR, calfR, new Vector3(0.34f, 0.62f, 0.38f),
                new[] { "thighSize" }, new[] { "legLength" });
            AddBoxSculptRegion("CalfL", calfL, footL, new Vector3(0.30f, 0.58f, 0.34f),
                new[] { "calfSize" }, new[] { "lowerLegLength" });
            AddBoxSculptRegion("CalfR", calfR, footR, new Vector3(0.30f, 0.58f, 0.34f),
                new[] { "calfSize" }, new[] { "lowerLegLength" });
            AddBoxSculptRegion("Neck", neck, neck, new Vector3(0.38f, 0.30f, 0.34f),
                new[] { "neckSize" }, new[] { "neckLength" });

            // Face interaction intentionally uses broad semantic candidates. High-poly profiles
            // can expose dedicated structural channels (jawWidth/browDepth/chinWidth); Synty
            // automatically falls back to the closest semantic it actually supports.
            AddBoxSculptRegion("Face_Brow", head, head, new Vector3(0.44f, 0.15f, 0.34f),
                new[] { "browDepth", "browOuterUp" }, new[] { "browHeight", "browRaise", "browInnerUp" },
                new Vector3(0f, 0.115f, 0.08f));
            AddBoxSculptRegion("Face_Eyes", head, head, new Vector3(0.40f, 0.11f, 0.32f),
                new[] { "eyesNarrow", "eyeballFlatten", "irisSmall" }, new[] { "eyesHeight", "eyeWide" },
                new Vector3(0f, 0.055f, 0.105f));
            AddBoxSculptRegion("Face_Cheeks", head, head, new Vector3(0.46f, 0.15f, 0.36f),
                new[] { "cheekboneSize", "cheeksSize", "cheekPuff", "cheekHollow" }, new[] { "cheekSquint" },
                new Vector3(0f, -0.015f, 0.09f));
            AddBoxSculptRegion("Face_Nose", head, head, new Vector3(0.22f, 0.15f, 0.30f),
                new[] { "noseWidth", "noseSize" }, new[] { "noseHeight", "noseOut" },
                new Vector3(0f, -0.015f, 0.135f));
            AddBoxSculptRegion("Face_Mouth", head, head, new Vector3(0.30f, 0.12f, 0.30f),
                new[] { "mouthSize" }, new[] { "mouthHeight", "mouthDepth" },
                new Vector3(0f, -0.085f, 0.115f));
            AddBoxSculptRegion("Face_Jaw", head, head, new Vector3(0.42f, 0.14f, 0.34f),
                new[] { "jawWidth", "jawForward" }, new[] { "jawHeight", "chinSize", "chinWidth" },
                new Vector3(0f, -0.155f, 0.07f));
        }

        private void AddBoxSculptRegion(
            string name,
            Transform followA,
            Transform followB,
            Vector3 size,
            string[] horizontalChannels,
            string[] verticalChannels,
            Vector3 localOffset = default)
        {
            if (_creatorSculptRoot == null || followA == null)
                return;

            GameObject go = new GameObject(name);
            go.layer = EffectivePreviewLayer();
            go.transform.SetParent(_creatorSculptRoot.transform, false);
            BoxCollider collider = go.AddComponent<BoxCollider>();
            collider.size = size;
            collider.isTrigger = true;
            CharacterCreatorSculptRegion region = go.AddComponent<CharacterCreatorSculptRegion>();
            region.Configure(horizontalChannels, verticalChannels, 1f, 1f, localOffset);
            region.Follow(followA, followB, _actor != null ? _actor.transform : null, localOffset);

            AddDirectSculptSemantics(horizontalChannels);
            AddDirectSculptSemantics(verticalChannels);
        }

        private void AddDirectSculptSemantics(string[] semantics)
        {
            if (semantics == null)
                return;
            for (int i = 0; i < semantics.Length; ++i)
            {
                if (!string.IsNullOrWhiteSpace(semantics[i]))
                    _creatorDirectSculptSemantics.Add(semantics[i]);
            }
        }

        private static Transform GetHumanoidOrNamed(Animator animator, HumanBodyBones humanBone, Transform root, params string[] names)
        {
            if (animator != null && animator.isHuman)
            {
                Transform human = animator.GetBoneTransform(humanBone);
                if (human != null)
                    return human;
            }

            if (names != null)
            {
                for (int i = 0; i < names.Length; ++i)
                {
                    Transform found = FindDescendant(root, names[i]);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;
            Transform[] values = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < values.Length; ++i)
            {
                Transform value = values[i];
                if (value != null && string.Equals(value.name, name, System.StringComparison.OrdinalIgnoreCase))
                    return value;
            }
            return null;
        }

        private void ClearStalePreviewActors()
        {
            Transform anchor = _stage != null ? _stage.CharacterAnchor : null;
            if (anchor == null)
                return;

            for (int i = anchor.childCount - 1; i >= 0; --i)
            {
                GameObject child = anchor.GetChild(i).gameObject;
                if (child == null)
                    continue;
                child.SetActive(false);
                child.transform.SetParent(null, false);
                Destroy(child);
            }
        }

        private void DestroyActor()
        {
            _creatorSculptRoot = null;
            _creatorDirectSculptSemantics.Clear();
            if (_actor != null)
            {
                // Hide immediately so a replacement created in the same frame can never
                // render on top of the outgoing preview actor while Destroy is deferred.
                _actor.SetActive(false);
                _actor.transform.SetParent(null, false);
                Destroy(_actor);
            }
            _actor = null;
        }

        private void ReleaseRuntimeObjects()
        {
            DestroyActor();
            if (_camera != null)
                _camera.targetTexture = null;
            if (previewImage != null && previewImage.texture == _texture)
                previewImage.texture = null;
            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
            }
            if (_stageRoot != null)
            {
                _stageRoot.SetActive(false);
                Destroy(_stageRoot);
            }
            _camera = null;
            _texture = null;
            _stageRoot = null;
            _stage = null;
            _orbitReady = false;
            if (_activePreviewOwner == this)
                _activePreviewOwner = null;
        }

        private int EffectivePreviewLayer() => Mathf.Clamp(previewLayer, 0, 31);

        private static Transform NewAnchor(string name, Transform parent, Vector3 localPosition)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static void ConfigurePreviewRenderers(GameObject actor)
        {
            if (actor == null)
                return;

            // Preview actors are isolated on a private layer and there is only one of them,
            // so favor correctness over culling micro-optimizations. Some modular hair meshes
            // have source bounds that are valid in the catalog prefab but become too tight once
            // rebound to the editable skeleton. That manifests as hair disappearing at specific
            // rotation angles. Keep preview skinning active and give each renderer a conservative
            // local bound; this does not affect normal in-world player renderers.
            SkinnedMeshRenderer[] renderers = actor.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; ++i)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                renderer.updateWhenOffscreen = true;
                renderer.forceRenderingOff = false;
                renderer.allowOcclusionWhenDynamic = false;
                // Preview rotation is a diagnostic/customization surface, not a production
                // world renderer. Eliminate bounds-based culling entirely here so catalog
                // hairstyles cannot blink out at certain yaw angles because of imported or
                // rebound SkinnedMeshRenderer bounds. World actors keep normal authored bounds.
                renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 100f);
            }

            Animator[] animators = actor.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; ++i)
            {
                if (animators[i] != null)
                    animators[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null)
                return;
            root.layer = layer;
            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; ++i)
                SetLayerRecursively(transform.GetChild(i).gameObject, layer);
        }
    }
}
