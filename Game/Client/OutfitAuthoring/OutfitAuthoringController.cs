using System;
using System.Collections.Generic;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Client.OutfitAuthoring
{
    [Serializable]
    public sealed class OutfitAuthoringSaveRequest
    {
        public CharacterWearablePartSelection[] parts = Array.Empty<CharacterWearablePartSelection>();
        public ushort[] allowedColorIds = Array.Empty<ushort>();
    }

    public readonly struct OutfitAuthoringSaveResult
    {
        public readonly bool success;
        public readonly string message;

        public OutfitAuthoringSaveResult(bool success, string message)
        {
            this.success = success;
            this.message = message ?? string.Empty;
        }
    }

    /// <summary>
    /// Play Mode outfit authoring surface. It consumes the current CharacterVisualProfile and
    /// the same ModularCharacterAppearancePresenter used by the real Player presentation path.
    /// Saving is delegated to Game.Client.Editor so no UnityEditor dependency leaks into runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OutfitAuthoringController : MonoBehaviour
    {
        public static Func<OutfitAuthoringSaveRequest, OutfitAuthoringSaveResult> EditorSaveHandler;

        private sealed class SlotState
        {
            public readonly string label;
            public readonly ushort primarySlotId;
            public readonly List<CharacterVisualOptionDefinition> options = new List<CharacterVisualOptionDefinition>();
            public int optionIndex = -1; // -1 = canonical Default Base
            public Text valueText;

            public SlotState(string label, ushort primarySlotId)
            {
                this.label = label;
                this.primarySlotId = primarySlotId;
            }
        }

        private static readonly (string Label, ushort SlotId)[] AuthoredSlots =
        {
            ("Torso", 10),
            ("Upper Arms", 11),
            ("Lower Arms", 13),
            ("Hands", 15),
            ("Hips", 17),
            ("Lower Legs", 18),
            ("Feet", 20),
        };

        private readonly List<SlotState> _slotStates = new List<SlotState>();
        private readonly Dictionary<ushort, Toggle> _colorToggles = new Dictionary<ushort, Toggle>();

        private CharacterVisualProfile _profile;
        private CharacterAppearanceRecipe _recipe;
        private GameObject _mannequin;
        private ModularCharacterAppearancePresenter _presenter;
        private Camera _camera;
        private Text _statusText;
        private Font _font;

        private void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _profile = CharacterVisualProfileRegistry.Default;
            if (_profile == null || _profile.EditableBasePrefab == null)
            {
                BuildFailureUi("CharacterVisualProfile or EditableBasePrefab is missing.");
                return;
            }

            _recipe = _profile.CreateDefaultRecipe();
            BuildEnvironment();
            BuildMannequin();
            BuildSlotStates();
            BuildUi();
            ApplyPreview();
        }

        private void BuildEnvironment()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                GameObject cameraObject = new GameObject("Outfit Authoring Camera");
                _camera = cameraObject.AddComponent<Camera>();
                cameraObject.tag = "MainCamera";
            }
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.055f, 0.06f, 0.07f, 1f);
            _camera.fieldOfView = 35f;

            if (FindObjectOfType<Light>() == null)
            {
                GameObject keyObject = new GameObject("Outfit Authoring Key Light");
                Light key = keyObject.AddComponent<Light>();
                key.type = LightType.Directional;
                key.intensity = 1.15f;
                key.transform.rotation = Quaternion.Euler(38f, -28f, 0f);

                GameObject fillObject = new GameObject("Outfit Authoring Fill Light");
                Light fill = fillObject.AddComponent<Light>();
                fill.type = LightType.Directional;
                fill.intensity = 0.55f;
                fill.transform.rotation = Quaternion.Euler(25f, 150f, 0f);
            }

            if (FindObjectOfType<EventSystem>() == null)
            {
                GameObject eventObject = new GameObject("EventSystem");
                eventObject.AddComponent<EventSystem>();
                Type inputSystemModule = Type.GetType(
                    "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (inputSystemModule != null)
                    eventObject.AddComponent(inputSystemModule);
                else
                    eventObject.AddComponent<StandaloneInputModule>();
            }
        }

        private void BuildMannequin()
        {
            _mannequin = Instantiate(_profile.EditableBasePrefab);
            _mannequin.name = "Outfit Authoring Player";
            _mannequin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            _presenter = _mannequin.GetComponent<ModularCharacterAppearancePresenter>();
            if (_presenter == null)
                _presenter = _mannequin.AddComponent<ModularCharacterAppearancePresenter>();
            _presenter.Configure(_profile);
            _presenter.ApplyAppearance(_recipe);

            Bounds bounds = CalculateBounds(_mannequin);
            Vector3 center = bounds.center;
            float height = Mathf.Max(1.6f, bounds.size.y);
            float distance = Mathf.Max(3.0f, height * 1.65f);
            _camera.transform.position = center + new Vector3(0f, height * 0.03f, distance);
            _camera.transform.LookAt(center + Vector3.up * height * 0.02f);
            _camera.nearClipPlane = 0.05f;
        }

        private void BuildSlotStates()
        {
            _slotStates.Clear();
            for (int i = 0; i < AuthoredSlots.Length; ++i)
            {
                (string label, ushort slotId) = AuthoredSlots[i];
                if (!_profile.TryGetSlot(slotId, out CharacterVisualSlotDefinition slot) || slot == null)
                    continue;

                var state = new SlotState(label, slotId);
                CharacterVisualOptionDefinition[] values = slot.options ?? Array.Empty<CharacterVisualOptionDefinition>();
                for (int o = 0; o < values.Length; ++o)
                {
                    CharacterVisualOptionDefinition option = values[o];
                    if (option != null && option.mesh != null)
                        state.options.Add(option);
                }
                _slotStates.Add(state);
            }
        }

        private void BuildUi()
        {
            GameObject canvasObject = new GameObject(
                "Outfit Authoring UI",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            RectTransform root = canvas.GetComponent<RectTransform>();

            Text title = CreateText("Title", root, "OUTFIT BUILDER", 30, TextAnchor.MiddleCenter, FontStyle.Bold);
            SetRect(title.rectTransform, new Vector2(0.35f, 0.92f), new Vector2(0.65f, 0.985f), Vector2.zero, Vector2.zero);

            BuildColorPanel(root);
            BuildSlotPanel(root);
            BuildSaveArea(root);
        }

        private void BuildColorPanel(RectTransform root)
        {
            RectTransform panel = CreatePanel("Available Colors", root, new Color(0.08f, 0.09f, 0.105f, 0.94f));
            SetRect(panel, new Vector2(0.015f, 0.06f), new Vector2(0.23f, 0.95f), Vector2.zero, Vector2.zero);

            Text header = CreateText("Header", panel, "AVAILABLE COLORS", 22, TextAnchor.MiddleCenter, FontStyle.Bold);
            SetRect(header.rectTransform, new Vector2(0f, 0.925f), new Vector2(1f, 1f), new Vector2(8f, 0f), new Vector2(-8f, -4f));

            GameObject viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewportObject.transform.SetParent(panel, false);
            RectTransform viewport = viewportObject.GetComponent<RectTransform>();
            SetRect(viewport, new Vector2(0f, 0f), new Vector2(1f, 0.925f), new Vector2(8f, 8f), new Vector2(-8f, -4f));
            Image viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.03f);
            viewportObject.GetComponent<Mask>().showMaskGraphic = false;

            GameObject contentObject = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentObject.transform.SetParent(viewport, false);
            RectTransform content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            VerticalLayoutGroup layout = contentObject.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 3f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = panel.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            HashSet<ushort> available = BuildAvailableColorSet();
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            for (ushort id = 1; id <= palette.Length; ++id)
            {
                if (!available.Contains(id))
                    continue;
                CreateColorRow(content, id, palette[id - 1]);
            }
        }

        private void CreateColorRow(RectTransform parent, ushort id, Color color)
        {
            GameObject rowObject = new GameObject($"Color {id}", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            rowObject.transform.SetParent(parent, false);
            rowObject.GetComponent<Image>().color = new Color(0.12f, 0.13f, 0.15f, 0.92f);
            LayoutElement rowSize = rowObject.GetComponent<LayoutElement>();
            rowSize.preferredHeight = 34f;
            rowSize.minHeight = 34f;
            HorizontalLayoutGroup row = rowObject.GetComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(5, 5, 3, 3);
            row.spacing = 8f;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = false;
            row.childControlHeight = false;

            GameObject swatchObject = new GameObject("Swatch", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            swatchObject.transform.SetParent(rowObject.transform, false);
            swatchObject.GetComponent<Image>().color = color;
            LayoutElement swatchSize = swatchObject.GetComponent<LayoutElement>();
            swatchSize.preferredWidth = 28f;
            swatchSize.preferredHeight = 28f;

            Text label = CreateText("Label", rowObject.transform, $"{id:00}  {SidekickCharacterPaletteUtility.GetClothingPaletteName(id)}", 15, TextAnchor.MiddleLeft, FontStyle.Normal);
            LayoutElement labelSize = label.gameObject.AddComponent<LayoutElement>();
            labelSize.preferredWidth = 260f;
            labelSize.preferredHeight = 28f;

            Toggle toggle = CreateToggle(rowObject.transform);
            toggle.isOn = true;
            _colorToggles[id] = toggle;
        }

        private void BuildSlotPanel(RectTransform root)
        {
            RectTransform panel = CreatePanel("Mesh Slots", root, new Color(0.08f, 0.09f, 0.105f, 0.94f));
            SetRect(panel, new Vector2(0.73f, 0.17f), new Vector2(0.985f, 0.95f), Vector2.zero, Vector2.zero);

            Text header = CreateText("Header", panel, "PLAYER MESHES", 22, TextAnchor.MiddleCenter, FontStyle.Bold);
            SetRect(header.rectTransform, new Vector2(0f, 0.9f), new Vector2(1f, 1f), new Vector2(8f, 0f), new Vector2(-8f, -4f));

            GameObject contentObject = new GameObject("Slot Content", typeof(RectTransform), typeof(VerticalLayoutGroup));
            contentObject.transform.SetParent(panel, false);
            RectTransform content = contentObject.GetComponent<RectTransform>();
            SetRect(content, new Vector2(0f, 0f), new Vector2(1f, 0.9f), new Vector2(10f, 8f), new Vector2(-10f, -6f));
            VerticalLayoutGroup layout = contentObject.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 8, 8);
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            for (int i = 0; i < _slotStates.Count; ++i)
                CreateSlotRow(content, _slotStates[i]);
        }

        private void CreateSlotRow(RectTransform parent, SlotState state)
        {
            GameObject groupObject = new GameObject(state.label, typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            groupObject.transform.SetParent(parent, false);
            groupObject.GetComponent<Image>().color = new Color(0.12f, 0.13f, 0.15f, 0.95f);
            LayoutElement size = groupObject.GetComponent<LayoutElement>();
            size.preferredHeight = 72f;
            VerticalLayoutGroup vertical = groupObject.GetComponent<VerticalLayoutGroup>();
            vertical.padding = new RectOffset(8, 8, 4, 4);
            vertical.spacing = 3f;
            vertical.childControlWidth = true;
            vertical.childControlHeight = true;
            vertical.childForceExpandHeight = false;

            Text label = CreateText("Slot Label", groupObject.transform, state.label, 17, TextAnchor.MiddleLeft, FontStyle.Bold);
            LayoutElement labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.preferredHeight = 24f;

            GameObject controlsObject = new GameObject("Controls", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            controlsObject.transform.SetParent(groupObject.transform, false);
            LayoutElement controlsSize = controlsObject.GetComponent<LayoutElement>();
            controlsSize.preferredHeight = 34f;
            HorizontalLayoutGroup controls = controlsObject.GetComponent<HorizontalLayoutGroup>();
            controls.spacing = 6f;
            controls.childAlignment = TextAnchor.MiddleCenter;
            controls.childControlWidth = false;
            controls.childControlHeight = true;

            Button left = CreateButton(controlsObject.transform, "<", 38f, 32f);
            state.valueText = CreateText("Value", controlsObject.transform, "Default", 14, TextAnchor.MiddleCenter, FontStyle.Normal);
            LayoutElement valueSize = state.valueText.gameObject.AddComponent<LayoutElement>();
            valueSize.preferredWidth = 300f;
            valueSize.preferredHeight = 32f;
            Button right = CreateButton(controlsObject.transform, ">", 38f, 32f);

            left.onClick.AddListener(() => CycleSlot(state, -1));
            right.onClick.AddListener(() => CycleSlot(state, 1));
            RefreshSlotLabel(state);
        }

        private void BuildSaveArea(RectTransform root)
        {
            RectTransform panel = CreatePanel("Save Area", root, new Color(0.06f, 0.07f, 0.08f, 0.9f));
            SetRect(panel, new Vector2(0.34f, 0.015f), new Vector2(0.66f, 0.145f), Vector2.zero, Vector2.zero);

            Button save = CreateButton(panel, "SAVE OUTFIT", 360f, 54f);
            RectTransform saveRect = save.GetComponent<RectTransform>();
            saveRect.anchorMin = new Vector2(0.5f, 0.56f);
            saveRect.anchorMax = new Vector2(0.5f, 0.56f);
            saveRect.pivot = new Vector2(0.5f, 0.5f);
            saveRect.anchoredPosition = Vector2.zero;
            save.onClick.AddListener(SaveOutfit);

            _statusText = CreateText("Status", panel, "Ready", 14, TextAnchor.MiddleCenter, FontStyle.Normal);
            SetRect(_statusText.rectTransform, new Vector2(0.04f, 0.05f), new Vector2(0.96f, 0.38f), Vector2.zero, Vector2.zero);
            _statusText.color = new Color(0.78f, 0.82f, 0.86f, 1f);
        }

        private void CycleSlot(SlotState state, int delta)
        {
            int choiceCount = state.options.Count + 1; // Default + every catalog mesh
            if (choiceCount <= 1)
                return;
            int currentChoice = state.optionIndex + 1;
            int next = (currentChoice + delta) % choiceCount;
            if (next < 0)
                next += choiceCount;
            state.optionIndex = next - 1;
            RefreshSlotLabel(state);
            ApplyPreview();
        }

        private void RefreshSlotLabel(SlotState state)
        {
            if (state.valueText == null)
                return;
            if (state.optionIndex < 0 || state.optionIndex >= state.options.Count)
            {
                state.valueText.text = "Default";
                return;
            }

            CharacterVisualOptionDefinition option = state.options[state.optionIndex];
            string name = !string.IsNullOrWhiteSpace(option.displayName)
                ? option.displayName
                : option.mesh != null ? option.mesh.name : "Mesh";
            state.valueText.text = $"{name}   [{state.optionIndex + 1}/{state.options.Count}]";
        }

        private void ApplyPreview()
        {
            if (_profile == null || _presenter == null)
                return;

            _recipe = _profile.CreateDefaultRecipe();
            List<CharacterWearablePartSelection> parts = BuildPartSelections();
            for (int i = 0; i < parts.Count; ++i)
                SetRecipeMesh(_recipe, parts[i].slotId, parts[i].optionId);
            _presenter.ApplyAppearance(_recipe);
        }

        private List<CharacterWearablePartSelection> BuildPartSelections()
        {
            var parts = new List<CharacterWearablePartSelection>(14);
            for (int i = 0; i < _slotStates.Count; ++i)
            {
                SlotState state = _slotStates[i];
                if (state.optionIndex < 0 || state.optionIndex >= state.options.Count)
                    continue;

                CharacterVisualOptionDefinition option = state.options[state.optionIndex];
                parts.Add(new CharacterWearablePartSelection(state.primarySlotId, option.optionId));

                if (_profile.TryGetSlot(state.primarySlotId, out CharacterVisualSlotDefinition slot) &&
                    slot != null && slot.mirrorSlotId != 0 &&
                    _profile.TryGetMirrorOption(slot, option.optionId, out ushort mirrorOptionId) &&
                    mirrorOptionId != 0)
                {
                    parts.Add(new CharacterWearablePartSelection(slot.mirrorSlotId, mirrorOptionId));
                }
            }
            parts.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            return parts;
        }

        private void SaveOutfit()
        {
            if (EditorSaveHandler == null)
            {
                SetStatus("Editor save bridge is not loaded. Open this scene inside the Unity Editor.", false);
                return;
            }

            var allowed = new List<ushort>(_colorToggles.Count);
            foreach (KeyValuePair<ushort, Toggle> pair in _colorToggles)
            {
                if (pair.Value != null && pair.Value.isOn)
                    allowed.Add(pair.Key);
            }
            allowed.Sort();

            OutfitAuthoringSaveResult result = EditorSaveHandler(new OutfitAuthoringSaveRequest
            {
                parts = BuildPartSelections().ToArray(),
                allowedColorIds = allowed.ToArray(),
            });
            SetStatus(result.message, result.success);
        }

        private HashSet<ushort> BuildAvailableColorSet()
        {
            var result = new HashSet<ushort>();
            Color[] palette = SidekickCharacterPaletteUtility.ClothingPalette;
            IReadOnlyList<ushort> configured = _profile.PlayerClothingPaletteIds;
            if (_profile.PlayerClothingColorPoolReviewed && configured != null && configured.Count > 0)
            {
                for (int i = 0; i < configured.Count; ++i)
                    if (configured[i] > 0 && configured[i] <= palette.Length)
                        result.Add(configured[i]);
            }
            else
            {
                for (ushort id = 1; id <= palette.Length; ++id)
                    result.Add(id);
            }
            return result;
        }

        private static void SetRecipeMesh(CharacterAppearanceRecipe recipe, ushort slotId, ushort optionId)
        {
            if (recipe == null || slotId == 0)
                return;
            var values = new List<CharacterMeshSelection>(recipe.meshes ?? Array.Empty<CharacterMeshSelection>());
            for (int i = values.Count - 1; i >= 0; --i)
                if (values[i].slotId == slotId)
                    values.RemoveAt(i);
            if (optionId != 0)
                values.Add(new CharacterMeshSelection(slotId, optionId));
            values.Sort((a, b) => a.slotId.CompareTo(b.slotId));
            recipe.meshes = values.ToArray();
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root != null ? root.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
            bool has = false;
            Bounds result = new Bounds(root != null ? root.transform.position : Vector3.zero, Vector3.one);
            for (int i = 0; i < renderers.Length; ++i)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.bounds.size.sqrMagnitude <= 0.000001f)
                    continue;
                if (!has)
                {
                    result = renderer.bounds;
                    has = true;
                }
                else
                {
                    result.Encapsulate(renderer.bounds);
                }
            }
            return result;
        }

        private void BuildFailureUi(string message)
        {
            GameObject canvasObject = new GameObject(
                "Outfit Authoring Error UI",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Text error = CreateText("Error", canvas.transform, message, 24, TextAnchor.MiddleCenter, FontStyle.Bold);
            SetRect(error.rectTransform, new Vector2(0.15f, 0.35f), new Vector2(0.85f, 0.65f), Vector2.zero, Vector2.zero);
            error.color = new Color(1f, 0.45f, 0.4f, 1f);
        }

        private void SetStatus(string message, bool success)
        {
            if (_statusText == null)
                return;
            _statusText.text = string.IsNullOrWhiteSpace(message) ? (success ? "Saved" : "Save failed") : message;
            _statusText.color = success
                ? new Color(0.55f, 0.95f, 0.62f, 1f)
                : new Color(1f, 0.55f, 0.5f, 1f);
        }

        private RectTransform CreatePanel(string name, Transform parent, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go.GetComponent<RectTransform>();
        }

        private Text CreateText(string name, Transform parent, string value, int size, TextAnchor alignment, FontStyle style)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = new Color(0.92f, 0.94f, 0.96f, 1f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private Button CreateButton(Transform parent, string label, float width, float height)
        {
            GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = new Color(0.18f, 0.21f, 0.25f, 1f);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);
            LayoutElement layout = go.GetComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.preferredHeight = height;
            layout.minWidth = width;
            layout.minHeight = height;

            Text text = CreateText("Text", go.transform, label, 18, TextAnchor.MiddleCenter, FontStyle.Bold);
            SetRect(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go.GetComponent<Button>();
        }

        private Toggle CreateToggle(Transform parent)
        {
            GameObject root = new GameObject("Enabled", typeof(RectTransform), typeof(Toggle), typeof(LayoutElement));
            root.transform.SetParent(parent, false);
            LayoutElement layout = root.GetComponent<LayoutElement>();
            layout.preferredWidth = 26f;
            layout.preferredHeight = 26f;

            GameObject bgObject = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgObject.transform.SetParent(root.transform, false);
            Image background = bgObject.GetComponent<Image>();
            background.color = new Color(0.18f, 0.19f, 0.22f, 1f);
            SetRect(bgObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            GameObject checkObject = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            checkObject.transform.SetParent(bgObject.transform, false);
            Image check = checkObject.GetComponent<Image>();
            check.color = new Color(0.55f, 0.92f, 0.62f, 1f);
            SetRect(checkObject.GetComponent<RectTransform>(), new Vector2(0.22f, 0.22f), new Vector2(0.78f, 0.78f), Vector2.zero, Vector2.zero);

            Toggle toggle = root.GetComponent<Toggle>();
            toggle.targetGraphic = background;
            toggle.graphic = check;
            return toggle;
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
