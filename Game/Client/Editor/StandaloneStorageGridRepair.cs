#if UNITY_EDITOR
using System;
using Game.Client.UI.Root;
using Game.Client.UI.Standalone;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Focused prefab repair for the existing Standalone Storage window.
    /// It preserves the canonical StandaloneSocialEconomyUI bindings and simply hides the
    /// old list scrolls, then authors inventory-style icon grids over the same window.
    /// </summary>
    public static class StandaloneStorageGridRepair
    {
        private const string PrefabPath =
            "Assets/Game/Client/UI/Standalone/Prefabs/StandaloneClientUI.prefab";

        private static readonly Color Panel =
            new Color32(15, 18, 24, 225);
        private static readonly Color Raised =
            new Color32(26, 31, 40, 245);
        private static readonly Color Field =
            new Color32(10, 13, 18, 238);
        private static readonly Color Accent =
            new Color32(55, 119, 190, 255);
        private static readonly Color Border =
            new Color32(67, 78, 96, 255);
        private static readonly Color TextPrimary =
            new Color32(239, 242, 246, 255);
        private static readonly Color TextSecondary =
            new Color32(167, 178, 194, 255);

        [MenuItem("MMO Tools/UI/Standalone UI/Repair Storage Grid + Quantity V1")]
        public static void Repair()
        {
            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog(
                    "Storage Grid",
                    "StandaloneClientUI.prefab was not found.",
                    "OK");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                StandaloneSocialEconomyUI social =
                    root.GetComponent<StandaloneSocialEconomyUI>();
                if (social == null)
                {
                    throw new InvalidOperationException(
                        "StandaloneSocialEconomyUI is missing from the prefab root.");
                }

                SerializedObject serialized = new SerializedObject(social);
                GameObject storageWindow =
                    serialized.FindProperty("storageWindowRoot")?.objectReferenceValue
                    as GameObject;
                Text statusText =
                    serialized.FindProperty("storageStatusText")?.objectReferenceValue
                    as Text;

                if (storageWindow == null)
                {
                    throw new InvalidOperationException(
                        "The authored StorageWindow binding is missing.");
                }

                Transform inventoryColumn =
                    FindDescendant(storageWindow.transform, "InventoryColumn");
                Transform storageColumn =
                    FindDescendant(storageWindow.transform, "StorageColumn");

                if (inventoryColumn == null || storageColumn == null)
                {
                    throw new InvalidOperationException(
                        "StorageWindow is missing InventoryColumn or StorageColumn.");
                }

                // Preserve the old authored content and serialized references for rollback/
                // validation, but make its row-list presentation invisible.
                HideLegacyScroll(inventoryColumn);
                HideLegacyScroll(storageColumn);

                BuildGridColumn(
                    inventoryColumn,
                    "InventoryGridScroll",
                    out Transform inventoryContent,
                    out StandaloneStorageSlotView inventoryTemplate);

                BuildGridColumn(
                    storageColumn,
                    "StorageGridScroll",
                    out Transform storageContent,
                    out StandaloneStorageSlotView storageTemplate);

                StandaloneHudTooltipPanel tooltip =
                    root.GetComponentInChildren<StandaloneHudTooltipPanel>(true);
                StandaloneDragGhost dragGhost =
                    root.GetComponentInChildren<StandaloneDragGhost>(true);

                if (tooltip == null || dragGhost == null)
                {
                    throw new InvalidOperationException(
                        "The existing shared HUD tooltip or drag ghost could not be resolved.");
                }

                StandaloneStorageQuantityPicker picker =
                    BuildQuantityPicker(storageWindow.transform);

                StandaloneStorageGridView gridView =
                    storageWindow.GetComponent<StandaloneStorageGridView>();
                if (gridView == null)
                    gridView = storageWindow.AddComponent<StandaloneStorageGridView>();

                gridView.ConfigureForEditor(
                    storageWindow,
                    inventoryContent,
                    inventoryTemplate,
                    storageContent,
                    storageTemplate,
                    tooltip,
                    dragGhost,
                    picker,
                    statusText);

                EditorUtility.SetDirty(gridView);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject =
                AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            EditorGUIUtility.PingObject(Selection.activeObject);

            Debug.Log(
                "[StandaloneUI] Storage Grid + Quantity V1 repaired. " +
                "Existing storage authority/requests remain unchanged.");
        }

        private static void HideLegacyScroll(Transform column)
        {
            Transform legacy = column.Find("Scroll");
            if (legacy == null)
                legacy = FindDescendant(column, "Scroll");
            if (legacy != null)
                legacy.gameObject.SetActive(false);
        }

        private static void BuildGridColumn(
            Transform column,
            string rootName,
            out Transform content,
            out StandaloneStorageSlotView template)
        {
            Transform previous = column.Find(rootName);
            if (previous != null)
                UnityEngine.Object.DestroyImmediate(previous.gameObject);

            GameObject scrollGo = PanelObject(rootName, column, Field);
            RectTransform sr = scrollGo.GetComponent<RectTransform>();
            sr.anchorMin = Vector2.zero;
            sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(8f, 8f);
            sr.offsetMax = new Vector2(-8f, -38f);

            ScrollRect scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 22f;

            GameObject viewport = new GameObject(
                "Viewport",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            Stretch(viewport.GetComponent<RectTransform>());
            Image viewportImage = viewport.GetComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewportImage.raycastTarget = true;
            scroll.viewport = viewport.GetComponent<RectTransform>();

            GameObject contentGo =
                new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(viewport.transform, false);
            RectTransform cr = contentGo.GetComponent<RectTransform>();
            cr.anchorMin = new Vector2(0f, 1f);
            cr.anchorMax = new Vector2(1f, 1f);
            cr.pivot = new Vector2(0.5f, 1f);
            cr.anchoredPosition = Vector2.zero;
            cr.sizeDelta = Vector2.zero;

            GridLayoutGroup grid =
                contentGo.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(82f, 82f);
            grid.spacing = new Vector2(8f, 8f);
            grid.padding = new RectOffset(4, 4, 4, 4);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 5;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.childAlignment = TextAnchor.UpperLeft;

            ContentSizeFitter fitter =
                contentGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = cr;
            content = contentGo.transform;
            template = BuildSlotTemplate(content);
            template.gameObject.SetActive(false);
        }

        private static StandaloneStorageSlotView BuildSlotTemplate(
            Transform parent)
        {
            Font font =
                Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                Resources.GetBuiltinResource<Font>("Arial.ttf");

            GameObject go =
                PanelObject("StorageSlotTemplate", parent, Raised);
            RectTransform rr = go.GetComponent<RectTransform>();
            rr.sizeDelta = new Vector2(82f, 82f);
            AddOutline(go);

            Image highlight =
                ImageObject("DropHighlight", go.transform, Accent);
            Stretch(highlight.rectTransform);
            highlight.raycastTarget = false;
            highlight.enabled = false;

            Image icon =
                ImageObject("ItemIcon", go.transform, Color.white);
            icon.raycastTarget = false;
            RectTransform ir = icon.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0.5f, 0.5f);
            ir.pivot = new Vector2(0.5f, 0.5f);
            ir.anchoredPosition = Vector2.zero;
            ir.sizeDelta = new Vector2(56f, 56f);

            Text quantity =
                TextObject(
                    "Quantity",
                    go.transform,
                    font,
                    13,
                    TextPrimary,
                    TextAnchor.LowerRight,
                    string.Empty);
            Stretch(quantity.rectTransform);
            quantity.rectTransform.offsetMin = new Vector2(5f, 4f);
            quantity.rectTransform.offsetMax = new Vector2(-5f, -4f);

            StandaloneStorageSlotView view =
                go.AddComponent<StandaloneStorageSlotView>();
            view.ConfigureForEditor(icon, quantity, highlight);
            return view;
        }

        private static StandaloneStorageQuantityPicker BuildQuantityPicker(
            Transform storageWindow)
        {
            Transform old = storageWindow.Find("StorageQuantityPicker");
            if (old != null)
                UnityEngine.Object.DestroyImmediate(old.gameObject);

            Font font =
                Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                Resources.GetBuiltinResource<Font>("Arial.ttf");

            GameObject pickerRoot =
                PanelObject("StorageQuantityPicker", storageWindow, Panel);
            RectTransform rr = pickerRoot.GetComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0.5f, 0.5f);
            rr.pivot = new Vector2(0.5f, 0.5f);
            rr.anchoredPosition = Vector2.zero;
            rr.sizeDelta = new Vector2(390f, 190f);
            AddOutline(pickerRoot);

            Text title =
                TextObject(
                    "Title",
                    pickerRoot.transform,
                    font,
                    15,
                    TextPrimary,
                    TextAnchor.MiddleLeft,
                    "Quantity");
            SetRect(
                title.rectTransform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
                new Vector2(14f, -10f),
                new Vector2(-28f, 30f));

            GameObject inputGo =
                PanelObject("QuantityInput", pickerRoot.transform, Field);
            SetRect(
                inputGo.GetComponent<RectTransform>(),
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(0f, 70f),
                new Vector2(-150f, 38f));
            RectTransform inputRect =
                inputGo.GetComponent<RectTransform>();
            inputRect.offsetMin = new Vector2(14f, 70f);
            inputRect.offsetMax = new Vector2(-150f, 108f);

            InputField input =
                inputGo.AddComponent<InputField>();
            input.contentType = InputField.ContentType.IntegerNumber;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 9;
            input.targetGraphic = inputGo.GetComponent<Image>();

            Text placeholder =
                TextObject(
                    "Placeholder",
                    inputGo.transform,
                    font,
                    14,
                    TextSecondary,
                    TextAnchor.MiddleLeft,
                    "1");
            Stretch(placeholder.rectTransform);
            placeholder.rectTransform.offsetMin = new Vector2(10f, 2f);
            placeholder.rectTransform.offsetMax = new Vector2(-10f, -2f);

            Text value =
                TextObject(
                    "Text",
                    inputGo.transform,
                    font,
                    14,
                    TextPrimary,
                    TextAnchor.MiddleLeft,
                    string.Empty);
            Stretch(value.rectTransform);
            value.rectTransform.offsetMin = new Vector2(10f, 2f);
            value.rectTransform.offsetMax = new Vector2(-10f, -2f);

            input.placeholder = placeholder;
            input.textComponent = value;
            inputGo.AddComponent<ClientUiTextInputCapture>();

            Button max =
                ButtonObject(
                    "MaxButton",
                    pickerRoot.transform,
                    font,
                    "MAX",
                    Raised);
            RectTransform mr = max.GetComponent<RectTransform>();
            mr.anchorMin = mr.anchorMax = new Vector2(1f, 0f);
            mr.pivot = new Vector2(1f, 0f);
            mr.anchoredPosition = new Vector2(-14f, 70f);
            mr.sizeDelta = new Vector2(120f, 38f);

            Button confirm =
                ButtonObject(
                    "ConfirmButton",
                    pickerRoot.transform,
                    font,
                    "TRANSFER",
                    Accent);
            RectTransform cfr = confirm.GetComponent<RectTransform>();
            cfr.anchorMin = cfr.anchorMax = new Vector2(1f, 0f);
            cfr.pivot = new Vector2(1f, 0f);
            cfr.anchoredPosition = new Vector2(-14f, 14f);
            cfr.sizeDelta = new Vector2(150f, 38f);

            Button cancel =
                ButtonObject(
                    "CancelButton",
                    pickerRoot.transform,
                    font,
                    "CANCEL",
                    Raised);
            RectTransform car = cancel.GetComponent<RectTransform>();
            car.anchorMin = car.anchorMax = new Vector2(0f, 0f);
            car.pivot = new Vector2(0f, 0f);
            car.anchoredPosition = new Vector2(14f, 14f);
            car.sizeDelta = new Vector2(120f, 38f);

            StandaloneStorageQuantityPicker picker =
                pickerRoot.AddComponent<StandaloneStorageQuantityPicker>();
            picker.ConfigureForEditor(
                pickerRoot,
                title,
                input,
                max,
                confirm,
                cancel);

            pickerRoot.SetActive(false);
            return picker;
        }

        private static Transform FindDescendant(
            Transform root,
            string name)
        {
            if (root == null)
                return null;

            for (int i = 0; i < root.childCount; ++i)
            {
                Transform child = root.GetChild(i);
                if (string.Equals(
                    child.name,
                    name,
                    StringComparison.Ordinal))
                {
                    return child;
                }

                Transform nested = FindDescendant(child, name);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static GameObject PanelObject(
            string name,
            Transform parent,
            Color color)
        {
            GameObject go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = color.a > 0.01f;
            return go;
        }

        private static Image ImageObject(
            string name,
            Transform parent,
            Color color)
        {
            return PanelObject(name, parent, color).GetComponent<Image>();
        }

        private static Text TextObject(
            string name,
            Transform parent,
            Font font,
            int size,
            Color color,
            TextAnchor alignment,
            string value)
        {
            GameObject go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.text = value;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Button ButtonObject(
            string name,
            Transform parent,
            Font font,
            string label,
            Color normal)
        {
            GameObject go = PanelObject(name, parent, normal);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();

            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = Accent;
            colors.pressedColor = new Color32(40, 84, 129, 255);
            colors.selectedColor = Accent;
            colors.disabledColor =
                new Color(normal.r, normal.g, normal.b, 0.35f);
            button.colors = colors;

            Text text =
                TextObject(
                    "Text",
                    go.transform,
                    font,
                    12,
                    TextPrimary,
                    TextAnchor.MiddleCenter,
                    label);
            Stretch(text.rectTransform);
            return button;
        }

        private static void AddOutline(GameObject go)
        {
            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
        }
    }
}
#endif
