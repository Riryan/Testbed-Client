#if UNITY_EDITOR
using System;
using Game.Client.Presentation.Characters;
using Game.Client.UI.CharacterCreation;
using Game.Client.UI.CharacterSelect;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Reusable Character Creator authoring utility. Production UI remains stored in
    /// ClientUIRoot.prefab; runtime code never constructs the creator hierarchy.
    /// </summary>
    public static class CharacterCreatorAuthoredUiRepair
    {
        private const string MasterPrefabPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";

        [MenuItem("MMO Tools/UI/Frontend/Rebuild Character Creator Surface")]
        public static void InstallOrRepair()
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(MasterPrefabPath);
            if (contents == null)
                throw new InvalidOperationException($"Could not load {MasterPrefabPath}");

            try
            {
                CharacterSelectShell shell = contents.GetComponentInChildren<CharacterSelectShell>(true);
                if (shell == null)
                    throw new InvalidOperationException("ClientUIRoot.prefab does not contain CharacterSelectShell.");

                Repair(shell);
                PrefabUtility.SaveAsPrefabAsset(contents, MasterPrefabPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            Validate();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(MasterPrefabPath);
            Debug.Log("[CharacterCreator] Authored Character Creator installed/repaired in ClientUIRoot.prefab.");
        }

        [MenuItem("MMO Tools/UI/Frontend/Validate Character Creator")]
        public static void Validate()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MasterPrefabPath);
            if (prefab == null)
                throw new InvalidOperationException($"Missing {MasterPrefabPath}");

            CharacterSelectShell frontend = prefab.GetComponentInChildren<CharacterSelectShell>(true);
            CharacterCreatorShell creator = prefab.GetComponentInChildren<CharacterCreatorShell>(true);
            if (frontend == null || creator == null)
                throw new InvalidOperationException("Authored Character Creator is missing from ClientUIRoot.prefab.");

            SerializedObject so = new SerializedObject(creator);
            SerializedProperty version = so.FindProperty("authoredUiVersion");
            if (version == null || version.intValue < CharacterCreatorShell.AuthoredUiSchemaVersion)
                throw new InvalidOperationException("Character Creator authored UI schema is stale.");

            string[] refs =
            {
                "catalogGroup", "movementGroup", "subcategoryGroup", "colorPanel",
                "subcategoryContent", "catalogContent", "catalogScroll", "colorContent",
                "categoryTitle", "statusText", "nameInput", "movementSlider", "createButton", "backButton",
                "bodyButton", "faceButton", "hairButton", "movementButton",
                "idlePreviewButton", "walkPreviewButton", "runPreviewButton", "preview",
                "subcategoryTemplate", "meshGroupTemplate", "meshOptionTemplate", "morphRowTemplate",
                "colorChannelTemplate", "colorSwatchTemplate", "infoRowTemplate"
            };
            for (int i = 0; i < refs.Length; ++i)
                RequireReference(so, refs[i]);

            SerializedObject frontendSo = new SerializedObject(frontend);
            SerializedProperty creatorRef = frontendSo.FindProperty("_characterCreator");
            if (creatorRef == null || creatorRef.objectReferenceValue != creator)
                throw new InvalidOperationException("CharacterSelectShell is not bound to the authored Character Creator.");

            Debug.Log("[CharacterCreator] Authored Character Creator validation passed.", prefab);
        }

        internal static void Repair(CharacterSelectShell frontend)
        {
            if (frontend == null)
                throw new ArgumentNullException(nameof(frontend));

            CharacterCreatorShell existing = frontend.GetComponentInChildren<CharacterCreatorShell>(true);
            if (existing != null)
            {
                SerializedObject existingSo = new SerializedObject(existing);
                SerializedProperty version = existingSo.FindProperty("authoredUiVersion");
                if (version != null && version.intValue >= CharacterCreatorShell.AuthoredUiSchemaVersion &&
                    HasRequiredReferences(existingSo))
                {
                    BindFrontend(frontend, existing);
                    return;
                }

                if (existing.gameObject == frontend.gameObject)
                    UnityEngine.Object.DestroyImmediate(existing);
                else
                    UnityEngine.Object.DestroyImmediate(existing.gameObject);
            }

            CharacterCreatorShell creator = BuildCreator(frontend.transform);
            BindFrontend(frontend, creator);
            creator.gameObject.SetActive(false);
            EditorUtility.SetDirty(frontend);
            EditorUtility.SetDirty(creator);
        }

        private static CharacterCreatorShell BuildCreator(Transform parent)
        {
            GameObject root = CreateUiObject("Character Creator Panel", parent);
            Stretch(root.GetComponent<RectTransform>());
            Image bg = root.AddComponent<Image>();
            bg.color = new Color(0.008f, 0.010f, 0.014f, 1f);
            CharacterCreatorShell creator = root.AddComponent<CharacterCreatorShell>();

            // Stage-first composition: the character owns the center of the screen. UI is an
            // overlay around the stage rather than a preview box embedded inside a menu.
            GameObject stageSurface = CreatePanel("Character Creation Stage", root.transform, new Color(0.025f, 0.11f, 0.135f, 1f));
            SetNormalizedRect(stageSurface.GetComponent<RectTransform>(), new Vector2(0.135f, 0.075f), new Vector2(0.785f, 0.955f), Vector2.zero, Vector2.zero);
            RawImage previewImage = CreateUiObject("Stage Image", stageSurface.transform).AddComponent<RawImage>();
            previewImage.color = Color.white;
            Stretch(previewImage.rectTransform);

            CharacterSelectPreviewController preview = stageSurface.AddComponent<CharacterSelectPreviewController>();
            SerializedObject previewSo = new SerializedObject(preview);
            previewSo.FindProperty("previewImage").objectReferenceValue = previewImage;
            previewSo.FindProperty("previewNameText").objectReferenceValue = null;
            previewSo.ApplyModifiedPropertiesWithoutUndo();

            CharacterCreatorStageInteraction stageInteraction = previewImage.gameObject.AddComponent<CharacterCreatorStageInteraction>();
            stageInteraction.Configure(preview, creator);

            Text stageHint = CreateText(
                "Stage Hint",
                stageSurface.transform,
                "Drag the character to sculpt  •  Drag empty stage to rotate  •  Mouse wheel to zoom",
                12,
                TextAnchor.MiddleCenter);
            stageHint.color = new Color(0.78f, 0.82f, 0.87f, 0.95f);
            AnchorFromBottomLeft(stageHint.rectTransform, new Vector2(28f, 10f), new Vector2(760f, 28f));

            Text title = CreateText("Title", root.transform, "CHARACTER CREATION", 28, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(title.rectTransform, new Vector2(24f, 16f), new Vector2(520f, 38f));
            Text subtitle = CreateText(
                "Subtitle", root.transform,
                "Shape the character directly or use the precision controls.",
                13, TextAnchor.MiddleLeft);
            subtitle.color = new Color(0.68f, 0.73f, 0.80f, 1f);
            AnchorFromTopLeft(subtitle.rectTransform, new Vector2(24f, 52f), new Vector2(700f, 26f));

            // Left category rail floats over the stage edge like the supplied Character Customizer.
            GameObject rail = CreatePanel("Category Rail", root.transform, new Color(0.012f, 0.016f, 0.022f, 0.94f));
            SetNormalizedRect(rail.GetComponent<RectTransform>(), new Vector2(0.018f, 0.18f), new Vector2(0.145f, 0.86f), Vector2.zero, Vector2.zero);
            Text railTitle = CreateText("Rail Title", rail.transform, "CUSTOMIZE", 12, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(railTitle.rectTransform, new Vector2(14f, 14f), new Vector2(190f, 24f));
            railTitle.color = new Color(0.58f, 0.64f, 0.72f, 1f);
            Button body = CreateButton("Body Button", rail.transform, "BODY", Vector2.zero, new Vector2(190f, 48f));
            Button face = CreateButton("Face Button", rail.transform, "FACE", Vector2.zero, new Vector2(190f, 48f));
            Button hair = CreateButton("Hair Button", rail.transform, "HAIR", Vector2.zero, new Vector2(190f, 48f));
            Button movement = CreateButton("Movement Button", rail.transform, "STANCE / GAIT", Vector2.zero, new Vector2(190f, 48f));
            Anchor(body.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, -82f), new Vector2(190f, 48f));
            Anchor(face.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, -142f), new Vector2(190f, 48f));
            Anchor(hair.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, -202f), new Vector2(190f, 48f));
            Anchor(movement.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, -262f), new Vector2(190f, 48f));

            // Context editor. At 1920x1080 this is ~390 px wide: four 82 px tiles fit with
            // breathing room and remain four columns after CanvasScaler resizing.
            GameObject editor = CreatePanel("Editor Panel", root.transform, new Color(0.012f, 0.016f, 0.022f, 0.965f));
            SetNormalizedRect(editor.GetComponent<RectTransform>(), new Vector2(0.785f, 0.095f), new Vector2(0.985f, 0.94f), Vector2.zero, Vector2.zero);
            Text categoryTitle = CreateText("Category Title", editor.transform, "BODY", 22, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(categoryTitle.rectTransform, new Vector2(18f, 14f), new Vector2(320f, 34f));

            GameObject appearanceStack = CreateUiObject("Appearance Stack", editor.transform);
            RectTransform stackRect = appearanceStack.GetComponent<RectTransform>();
            stackRect.anchorMin = new Vector2(0.035f, 0.035f);
            stackRect.anchorMax = new Vector2(0.965f, 0.91f);
            stackRect.offsetMin = Vector2.zero;
            stackRect.offsetMax = Vector2.zero;
            VerticalLayoutGroup stackLayout = appearanceStack.AddComponent<VerticalLayoutGroup>();
            stackLayout.spacing = 8f;
            stackLayout.childControlHeight = true;
            stackLayout.childControlWidth = true;
            stackLayout.childForceExpandHeight = false;
            stackLayout.childForceExpandWidth = true;

            GameObject subcategories = CreatePanel("Subcategories", appearanceStack.transform, new Color(0.018f, 0.024f, 0.032f, 0.96f));
            LayoutElement subLayoutElement = subcategories.AddComponent<LayoutElement>();
            subLayoutElement.preferredHeight = 72f;
            subLayoutElement.minHeight = 72f;
            GridLayoutGroup subGrid = subcategories.AddComponent<GridLayoutGroup>();
            subGrid.padding = new RectOffset(8, 8, 8, 8);
            subGrid.spacing = new Vector2(6f, 6f);
            subGrid.cellSize = new Vector2(106f, 25f);
            subGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            subGrid.constraintCount = 3;
            subGrid.childAlignment = TextAnchor.UpperLeft;

            GameObject catalog = CreatePanel("Catalog", appearanceStack.transform, new Color(0.006f, 0.010f, 0.015f, 0.94f));
            LayoutElement catalogLayout = catalog.AddComponent<LayoutElement>();
            catalogLayout.minHeight = 260f;
            catalogLayout.flexibleHeight = 1f;
            ScrollRect catalogScroll = CreateScroll("Catalog Scroll", catalog.transform, out RectTransform catalogContent);

            // Fixed color tray: separate from the option scroll and always fully visible.
            GameObject colors = CreatePanel("Colors Panel", appearanceStack.transform, new Color(0.010f, 0.022f, 0.024f, 0.98f));
            LayoutElement colorsLayout = colors.AddComponent<LayoutElement>();
            colorsLayout.preferredHeight = 190f;
            colorsLayout.minHeight = 190f;
            Text colorsTitle = CreateText("Colors Title", colors.transform, "COLORS", 12, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(colorsTitle.rectTransform, new Vector2(10f, 8f), new Vector2(180f, 22f));
            GameObject colorContentGo = CreateUiObject("Color Content", colors.transform);
            RectTransform colorContent = colorContentGo.GetComponent<RectTransform>();
            colorContent.anchorMin = Vector2.zero;
            colorContent.anchorMax = Vector2.one;
            colorContent.offsetMin = new Vector2(8f, 8f);
            colorContent.offsetMax = new Vector2(-8f, -32f);
            VerticalLayoutGroup colorLayout = colorContentGo.AddComponent<VerticalLayoutGroup>();
            colorLayout.spacing = 4f;
            colorLayout.childControlHeight = true;
            colorLayout.childControlWidth = true;
            colorLayout.childForceExpandHeight = false;
            colorLayout.childForceExpandWidth = true;

            GameObject movementGroup = CreatePanel("Movement Controls", editor.transform, new Color(0.010f, 0.016f, 0.024f, 0.98f));
            RectTransform movementRect = movementGroup.GetComponent<RectTransform>();
            movementRect.anchorMin = new Vector2(0.035f, 0.035f);
            movementRect.anchorMax = new Vector2(0.965f, 0.91f);
            movementRect.offsetMin = Vector2.zero;
            movementRect.offsetMax = Vector2.zero;
            Text moveInfo = CreateText(
                "Movement Explanation", movementGroup.transform,
                "Choose how the character carries and moves their body. Appearance is unchanged.",
                13, TextAnchor.UpperLeft);
            moveInfo.horizontalOverflow = HorizontalWrapMode.Wrap;
            AnchorFromTopLeft(moveInfo.rectTransform, new Vector2(16f, 18f), new Vector2(330f, 62f));
            Text moveLabel = CreateText("Movement Label", movementGroup.transform, "MOVEMENT STYLE", 12, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(moveLabel.rectTransform, new Vector2(16f, 98f), new Vector2(250f, 24f));
            Slider moveSlider = CreateSlider("Movement Style", movementGroup.transform);
            RectTransform moveSliderRect = moveSlider.GetComponent<RectTransform>();
            moveSliderRect.anchorMin = new Vector2(0f, 1f);
            moveSliderRect.anchorMax = new Vector2(1f, 1f);
            moveSliderRect.pivot = new Vector2(0.5f, 1f);
            moveSliderRect.anchoredPosition = new Vector2(0f, -142f);
            moveSliderRect.sizeDelta = new Vector2(-32f, 34f);
            Text style1 = CreateText("Style 1", movementGroup.transform, "STYLE 1", 10, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(style1.rectTransform, new Vector2(16f, 178f), new Vector2(100f, 22f));
            Text style2 = CreateText("Style 2", movementGroup.transform, "STYLE 2", 10, TextAnchor.MiddleRight);
            AnchorFromTopRight(style2.rectTransform, new Vector2(16f, 178f), new Vector2(100f, 22f));
            Text previewLabel = CreateText("Preview Label", movementGroup.transform, "PREVIEW", 12, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(previewLabel.rectTransform, new Vector2(16f, 236f), new Vector2(160f, 24f));
            Button idle = CreateButton("Idle Button", movementGroup.transform, "IDLE", Vector2.zero, new Vector2(100f, 40f));
            Button walk = CreateButton("Walk Button", movementGroup.transform, "WALK", Vector2.zero, new Vector2(100f, 40f));
            Button run = CreateButton("Run Button", movementGroup.transform, "RUN", Vector2.zero, new Vector2(100f, 40f));
            Anchor(idle.GetComponent<RectTransform>(), new Vector2(0.18f, 1f), new Vector2(0f, -292f), new Vector2(100f, 40f));
            Anchor(walk.GetComponent<RectTransform>(), new Vector2(0.50f, 1f), new Vector2(0f, -292f), new Vector2(100f, 40f));
            Anchor(run.GetComponent<RectTransform>(), new Vector2(0.82f, 1f), new Vector2(0f, -292f), new Vector2(100f, 40f));

            // Bottom strip stays compact and does not compete with the stage.
            GameObject footer = CreatePanel("Creator Footer", root.transform, new Color(0.010f, 0.014f, 0.020f, 0.96f));
            SetNormalizedRect(footer.GetComponent<RectTransform>(), new Vector2(0.135f, 0.010f), new Vector2(0.985f, 0.085f), Vector2.zero, Vector2.zero);
            Text status = CreateText("Status", footer.transform, " ", 10, TextAnchor.MiddleLeft);
            status.color = new Color(0.62f, 0.68f, 0.76f, 1f);
            AnchorFromTopLeft(status.rectTransform, new Vector2(12f, 4f), new Vector2(650f, 18f));
            Text nameLabel = CreateText("Name Label", footer.transform, "NAME", 11, TextAnchor.MiddleLeft);
            AnchorFromBottomLeft(nameLabel.rectTransform, new Vector2(12f, 7f), new Vector2(54f, 34f));
            InputField nameInput = CreateInput("Character Name", footer.transform, "Character name");
            nameInput.characterLimit = 32;
            AnchorFromBottomLeft(nameInput.GetComponent<RectTransform>(), new Vector2(68f, 6f), new Vector2(270f, 36f));
            Button back = CreateButton("Back Button", footer.transform, "BACK", Vector2.zero, new Vector2(130f, 38f));
            Button create = CreateButton("Create Character Button", footer.transform, "CREATE CHARACTER", Vector2.zero, new Vector2(210f, 38f));
            AnchorFromBottomRight(back.GetComponent<RectTransform>(), new Vector2(232f, 5f), new Vector2(130f, 38f));
            AnchorFromBottomRight(create.GetComponent<RectTransform>(), new Vector2(10f, 5f), new Vector2(210f, 38f));

            GameObject templates = CreateUiObject("Templates", root.transform);
            templates.SetActive(false);
            CharacterCreatorSubcategoryView subTemplate = BuildSubcategoryTemplate(templates.transform);
            CharacterCreatorMeshGroupView meshGroupTemplate = BuildMeshGroupTemplate(templates.transform);
            CharacterCreatorMeshOptionView meshOptionTemplate = BuildMeshOptionTemplate(templates.transform);
            CharacterCreatorMorphRowView morphTemplate = BuildMorphTemplate(templates.transform);
            CharacterCreatorColorChannelView colorChannelTemplate = BuildColorChannelTemplate(templates.transform);
            CharacterCreatorColorSwatchView colorSwatchTemplate = BuildColorSwatchTemplate(templates.transform);
            CharacterCreatorInfoRowView infoTemplate = BuildInfoTemplate(templates.transform);

            SerializedObject creatorSo = new SerializedObject(creator);
            creatorSo.FindProperty("authoredUiVersion").intValue = CharacterCreatorShell.AuthoredUiSchemaVersion;
            SetRef(creatorSo, "catalogGroup", catalog);
            SetRef(creatorSo, "movementGroup", movementGroup);
            SetRef(creatorSo, "subcategoryGroup", subcategories);
            SetRef(creatorSo, "colorPanel", colors);
            SetRef(creatorSo, "subcategoryContent", subcategories.GetComponent<RectTransform>());
            SetRef(creatorSo, "catalogContent", catalogContent);
            SetRef(creatorSo, "catalogScroll", catalogScroll);
            SetRef(creatorSo, "colorContent", colorContent);
            SetRef(creatorSo, "categoryTitle", categoryTitle);
            SetRef(creatorSo, "statusText", status);
            SetRef(creatorSo, "nameInput", nameInput);
            SetRef(creatorSo, "movementSlider", moveSlider);
            SetRef(creatorSo, "createButton", create);
            SetRef(creatorSo, "backButton", back);
            SetRef(creatorSo, "bodyButton", body);
            SetRef(creatorSo, "faceButton", face);
            SetRef(creatorSo, "hairButton", hair);
            SetRef(creatorSo, "movementButton", movement);
            SetRef(creatorSo, "idlePreviewButton", idle);
            SetRef(creatorSo, "walkPreviewButton", walk);
            SetRef(creatorSo, "runPreviewButton", run);
            SetRef(creatorSo, "preview", preview);
            SetRef(creatorSo, "subcategoryTemplate", subTemplate);
            SetRef(creatorSo, "meshGroupTemplate", meshGroupTemplate);
            SetRef(creatorSo, "meshOptionTemplate", meshOptionTemplate);
            SetRef(creatorSo, "morphRowTemplate", morphTemplate);
            SetRef(creatorSo, "colorChannelTemplate", colorChannelTemplate);
            SetRef(creatorSo, "colorSwatchTemplate", colorSwatchTemplate);
            SetRef(creatorSo, "infoRowTemplate", infoTemplate);
            creatorSo.ApplyModifiedPropertiesWithoutUndo();

            subcategories.SetActive(false);
            colors.SetActive(false);
            movementGroup.SetActive(false);
            return creator;
        }

        private static CharacterCreatorSubcategoryView BuildSubcategoryTemplate(Transform parent)
        {
            Button button = CreateButton("Subcategory Template", parent, "OPTION", Vector2.zero, new Vector2(126f, 31f));
            Outline outline = button.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.18f, 0.70f, 1f, 1f);
            outline.effectDistance = new Vector2(2f, -2f);
            outline.enabled = false;
            CharacterCreatorSubcategoryView view = button.gameObject.AddComponent<CharacterCreatorSubcategoryView>();
            SerializedObject so = new SerializedObject(view);
            SetRef(so, "button", button);
            SetRef(so, "label", button.GetComponentInChildren<Text>(true));
            SetRef(so, "selectedOutline", outline);
            so.ApplyModifiedPropertiesWithoutUndo();
            button.gameObject.SetActive(false);
            return view;
        }

        private static CharacterCreatorMeshGroupView BuildMeshGroupTemplate(Transform parent)
        {
            GameObject root = CreatePanel("Mesh Group Template", parent, new Color(0.022f, 0.031f, 0.042f, 0.96f));
            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 9, 10);
            layout.spacing = 8f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = root.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Text label = CreateText("Label", root.transform, "Options", 15, TextAnchor.MiddleLeft);
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 24f;
            GameObject grid = CreateUiObject("Options", root.transform);
            GridLayoutGroup gridLayout = grid.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = new Vector2(82f, 82f);
            gridLayout.spacing = new Vector2(6f, 6f);
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = 4;
            gridLayout.childAlignment = TextAnchor.UpperLeft;
            ContentSizeFitter gridFitter = grid.AddComponent<ContentSizeFitter>();
            gridFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            CharacterCreatorMeshGroupView view = root.AddComponent<CharacterCreatorMeshGroupView>();
            SerializedObject so = new SerializedObject(view);
            SetRef(so, "label", label);
            SetRef(so, "optionContent", grid.GetComponent<RectTransform>());
            so.ApplyModifiedPropertiesWithoutUndo();
            root.SetActive(false);
            return view;
        }

        private static CharacterCreatorMeshOptionView BuildMeshOptionTemplate(Transform parent)
        {
            Button button = CreateButton("Mesh Option Template", parent, string.Empty, Vector2.zero, new Vector2(82f, 82f));
            Outline outline = button.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.18f, 0.70f, 1f, 1f);
            outline.effectDistance = new Vector2(3f, -3f);
            outline.enabled = false;
            Image icon = CreateUiObject("Icon", button.transform).AddComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            RectTransform iconRect = icon.rectTransform;
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(7f, 7f);
            iconRect.offsetMax = new Vector2(-7f, -7f);
            Text label = CreateText("Fallback Label", button.transform, "Option", 10, TextAnchor.MiddleCenter);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(6f, 6f);
            labelRect.offsetMax = new Vector2(-6f, -6f);
            CharacterCreatorMeshOptionView view = button.gameObject.AddComponent<CharacterCreatorMeshOptionView>();
            SerializedObject so = new SerializedObject(view);
            SetRef(so, "button", button);
            SetRef(so, "icon", icon);
            SetRef(so, "fallbackLabel", label);
            SetRef(so, "selectedOutline", outline);
            so.ApplyModifiedPropertiesWithoutUndo();
            button.gameObject.SetActive(false);
            return view;
        }

        private static CharacterCreatorMorphRowView BuildMorphTemplate(Transform parent)
        {
            GameObject root = CreatePanel("Morph Row Template", parent, new Color(0.022f, 0.031f, 0.042f, 0.96f));
            LayoutElement le = root.AddComponent<LayoutElement>();
            le.preferredHeight = 64f;
            le.minHeight = 64f;
            Text label = CreateText("Label", root.transform, "Shape", 14, TextAnchor.MiddleLeft);
            AnchorFromTopLeft(label.rectTransform, new Vector2(10f, 8f), new Vector2(245f, 22f));
            Text value = CreateText("Value", root.transform, "50%", 12, TextAnchor.MiddleRight);
            AnchorFromTopRight(value.rectTransform, new Vector2(10f, 8f), new Vector2(70f, 22f));
            Slider slider = CreateSlider("Slider", root.transform);
            RectTransform sliderRect = slider.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0f, 0f);
            sliderRect.anchorMax = new Vector2(1f, 0f);
            sliderRect.offsetMin = new Vector2(10f, 8f);
            sliderRect.offsetMax = new Vector2(-10f, 34f);
            CharacterCreatorMorphRowView view = root.AddComponent<CharacterCreatorMorphRowView>();
            SerializedObject so = new SerializedObject(view);
            SetRef(so, "label", label);
            SetRef(so, "valueText", value);
            SetRef(so, "slider", slider);
            so.ApplyModifiedPropertiesWithoutUndo();
            root.SetActive(false);
            return view;
        }

        private static CharacterCreatorColorChannelView BuildColorChannelTemplate(Transform parent)
        {
            GameObject root = CreatePanel("Color Channel Template", parent, new Color(0.012f, 0.022f, 0.024f, 0.98f));
            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 8);
            layout.spacing = 5f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = root.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Text label = CreateText("Label", root.transform, "Color", 13, TextAnchor.MiddleLeft);
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;
            GameObject grid = CreateUiObject("Swatches", root.transform);
            GridLayoutGroup gridLayout = grid.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = new Vector2(44f, 26f);
            gridLayout.spacing = new Vector2(6f, 6f);
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = 8;
            gridLayout.childAlignment = TextAnchor.UpperLeft;
            ContentSizeFitter gridFitter = grid.AddComponent<ContentSizeFitter>();
            gridFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            CharacterCreatorColorChannelView view = root.AddComponent<CharacterCreatorColorChannelView>();
            SerializedObject so = new SerializedObject(view);
            SetRef(so, "label", label);
            SetRef(so, "swatchContent", grid.GetComponent<RectTransform>());
            so.ApplyModifiedPropertiesWithoutUndo();
            root.SetActive(false);
            return view;
        }

        private static CharacterCreatorColorSwatchView BuildColorSwatchTemplate(Transform parent)
        {
            Button button = CreateButton("Color Swatch Template", parent, string.Empty, Vector2.zero, new Vector2(44f, 26f));
            Outline outline = button.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.18f, 0.70f, 1f, 1f);
            outline.effectDistance = new Vector2(2f, -2f);
            outline.enabled = false;
            Image swatch = CreateUiObject("Swatch", button.transform).AddComponent<Image>();
            swatch.raycastTarget = false;
            RectTransform swatchRect = swatch.rectTransform;
            swatchRect.anchorMin = Vector2.zero;
            swatchRect.anchorMax = Vector2.one;
            swatchRect.offsetMin = new Vector2(4f, 4f);
            swatchRect.offsetMax = new Vector2(-4f, -4f);
            Text label = CreateText("Label", button.transform, "ORIGINAL", 9, TextAnchor.MiddleCenter);
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(2f, 2f);
            labelRect.offsetMax = new Vector2(-2f, -2f);
            CharacterCreatorColorSwatchView view = button.gameObject.AddComponent<CharacterCreatorColorSwatchView>();
            SerializedObject so = new SerializedObject(view);
            SetRef(so, "button", button);
            SetRef(so, "swatchImage", swatch);
            SetRef(so, "label", label);
            SetRef(so, "selectedOutline", outline);
            so.ApplyModifiedPropertiesWithoutUndo();
            button.gameObject.SetActive(false);
            return view;
        }

        private static CharacterCreatorInfoRowView BuildInfoTemplate(Transform parent)
        {
            GameObject root = CreatePanel("Info Row Template", parent, new Color(0.022f, 0.031f, 0.042f, 0.96f));
            LayoutElement le = root.AddComponent<LayoutElement>();
            le.preferredHeight = 82f;
            Text message = CreateText("Message", root.transform, "Info", 13, TextAnchor.UpperLeft);
            message.horizontalOverflow = HorizontalWrapMode.Wrap;
            RectTransform rect = message.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(12f, 8f);
            rect.offsetMax = new Vector2(-12f, -8f);
            CharacterCreatorInfoRowView view = root.AddComponent<CharacterCreatorInfoRowView>();
            SerializedObject so = new SerializedObject(view);
            SetRef(so, "messageText", message);
            so.ApplyModifiedPropertiesWithoutUndo();
            root.SetActive(false);
            return view;
        }

        private static ScrollRect CreateScroll(string name, Transform parent, out RectTransform content)
        {
            GameObject root = CreateUiObject(name, parent);
            RectTransform rootRect = root.GetComponent<RectTransform>();
            Stretch(rootRect);
            ScrollRect scroll = root.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            GameObject viewport = CreateUiObject("Viewport", root.transform);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            Stretch(viewportRect);
            Image viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewport.AddComponent<Mask>().showMaskGraphic = false;

            GameObject contentGo = CreateUiObject("Content", viewport.transform);
            content = contentGo.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            VerticalLayoutGroup layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 6, 8);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            scroll.viewport = viewportRect;
            scroll.content = content;
            return scroll;
        }

        private static GameObject CreatePanel(string name, Transform parent, Color color)
        {
            GameObject go = CreateUiObject(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0)
                go.layer = uiLayer;
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Text CreateText(string name, Transform parent, string textValue, int size, TextAnchor alignment)
        {
            GameObject go = CreateUiObject(name, parent);
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null)
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.text = textValue;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(string name, Transform parent, string label, Vector2 position, Vector2 size)
        {
            GameObject go = CreateUiObject(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.045f, 0.145f, 0.245f, 1f);
            Button button = go.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.80f, 0.90f, 1f, 1f);
            colors.pressedColor = new Color(0.65f, 0.78f, 0.92f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.55f);
            button.colors = colors;
            SetRect(go.GetComponent<RectTransform>(), position, size);
            Text text = CreateText("Label", go.transform, label, 13, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
            return button;
        }

        private static InputField CreateInput(string name, Transform parent, string placeholderValue)
        {
            GameObject go = CreateUiObject(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.012f, 0.020f, 0.030f, 1f);
            InputField input = go.AddComponent<InputField>();
            Text text = CreateText("Text", go.transform, string.Empty, 14, TextAnchor.MiddleLeft);
            Text placeholder = CreateText("Placeholder", go.transform, placeholderValue, 14, TextAnchor.MiddleLeft);
            placeholder.color = new Color(0.52f, 0.57f, 0.64f, 1f);
            RectTransform tr = text.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(12f, 4f);
            tr.offsetMax = new Vector2(-12f, -4f);
            RectTransform pr = placeholder.rectTransform;
            pr.anchorMin = Vector2.zero;
            pr.anchorMax = Vector2.one;
            pr.offsetMin = new Vector2(12f, 4f);
            pr.offsetMax = new Vector2(-12f, -4f);
            input.textComponent = text;
            input.placeholder = placeholder;
            return input;
        }

        private static Slider CreateSlider(string name, Transform parent)
        {
            GameObject root = CreateUiObject(name, parent);
            Slider slider = root.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;

            GameObject background = CreateUiObject("Background", root.transform);
            Image backgroundImage = background.AddComponent<Image>();
            backgroundImage.color = new Color(0.17f, 0.20f, 0.25f, 1f);
            RectTransform bg = background.GetComponent<RectTransform>();
            bg.anchorMin = new Vector2(0f, 0.35f);
            bg.anchorMax = new Vector2(1f, 0.65f);
            bg.offsetMin = Vector2.zero;
            bg.offsetMax = Vector2.zero;

            GameObject fillArea = CreateUiObject("Fill Area", root.transform);
            RectTransform fa = fillArea.GetComponent<RectTransform>();
            fa.anchorMin = new Vector2(0f, 0.35f);
            fa.anchorMax = new Vector2(1f, 0.65f);
            fa.offsetMin = Vector2.zero;
            fa.offsetMax = Vector2.zero;
            GameObject fill = CreateUiObject("Fill", fillArea.transform);
            Image fillImage = fill.AddComponent<Image>();
            fillImage.color = new Color(0.16f, 0.58f, 0.88f, 1f);
            Stretch(fill.GetComponent<RectTransform>());

            GameObject handleArea = CreateUiObject("Handle Slide Area", root.transform);
            Stretch(handleArea.GetComponent<RectTransform>());
            GameObject handle = CreateUiObject("Handle", handleArea.transform);
            Image handleImage = handle.AddComponent<Image>();
            handleImage.color = Color.white;
            RectTransform hr = handle.GetComponent<RectTransform>();
            hr.sizeDelta = new Vector2(18f, 34f);

            slider.fillRect = fill.GetComponent<RectTransform>();
            slider.handleRect = hr;
            slider.targetGraphic = handleImage;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            return slider;
        }

        private static void BindFrontend(CharacterSelectShell frontend, CharacterCreatorShell creator)
        {
            SerializedObject so = new SerializedObject(frontend);
            SerializedProperty prop = so.FindProperty("_characterCreator");
            if (prop == null)
                throw new InvalidOperationException("CharacterSelectShell._characterCreator serialized field is missing.");
            prop.objectReferenceValue = creator;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool HasRequiredReferences(SerializedObject so)
        {
            string[] refs =
            {
                "catalogGroup", "movementGroup", "subcategoryGroup", "colorPanel", "subcategoryContent",
                "catalogContent", "catalogScroll", "colorContent", "categoryTitle", "statusText", "nameInput",
                "movementSlider", "createButton", "backButton", "bodyButton", "faceButton", "hairButton",
                "movementButton", "idlePreviewButton", "walkPreviewButton", "runPreviewButton", "preview",
                "subcategoryTemplate", "meshGroupTemplate", "meshOptionTemplate", "morphRowTemplate",
                "colorChannelTemplate", "colorSwatchTemplate", "infoRowTemplate"
            };
            for (int i = 0; i < refs.Length; ++i)
            {
                SerializedProperty p = so.FindProperty(refs[i]);
                if (p == null || p.objectReferenceValue == null)
                    return false;
            }
            return true;
        }

        private static void RequireReference(SerializedObject so, string propertyName)
        {
            SerializedProperty property = so.FindProperty(propertyName);
            if (property == null || property.objectReferenceValue == null)
                throw new InvalidOperationException($"Character Creator is missing authored reference '{propertyName}'.");
        }

        private static void SetRef(SerializedObject so, string propertyName, UnityEngine.Object value)
        {
            SerializedProperty property = so.FindProperty(propertyName);
            if (property == null)
                throw new InvalidOperationException($"Missing serialized property '{propertyName}'.");
            property.objectReferenceValue = value;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetNormalizedRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void AnchorFromTopLeft(RectTransform rect, Vector2 inset, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(inset.x, -inset.y);
            rect.sizeDelta = size;
        }

        private static void AnchorFromTopRight(RectTransform rect, Vector2 inset, Vector2 size)
        {
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-inset.x, -inset.y);
            rect.sizeDelta = size;
        }

        private static void AnchorFromBottomLeft(RectTransform rect, Vector2 inset, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = inset;
            rect.sizeDelta = size;
        }

        private static void AnchorFromBottomRight(RectTransform rect, Vector2 inset, Vector2 size)
        {
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-inset.x, inset.y);
            rect.sizeDelta = size;
        }

        private static void Anchor(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}
#endif
