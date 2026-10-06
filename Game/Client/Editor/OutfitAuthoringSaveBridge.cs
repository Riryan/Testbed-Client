#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Game.Client.OutfitAuthoring;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Client.Editor
{
    [InitializeOnLoad]
    public static class OutfitAuthoringSaveBridge
    {
        [Serializable]
        private sealed class ArmorValidationDocument
        {
            public ArmorValidationItem[] items = Array.Empty<ArmorValidationItem>();
        }

        [Serializable]
        private sealed class ArmorValidationItem
        {
            public string definitionId;
        }

        private const string ScenePath = "Assets/Game/Client/Editor/OutfitAuthoring/OutfitAuthoring.unity";
        private const string ServerContentRootPreference = "MMO.OutfitAuthoring.ServerContentRoot";
        private const string ManifestFileName = "GameplayContent.json";
        private const string ArmorRelativePath = "Items/Equipment/Armor.json";

        private static readonly Regex PresentationIdRegex =
            new Regex("\\\"presentationId\\\"\\s*:\\s*(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DefinitionIdRegex =
            new Regex("\\\"definitionId\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RevisionRegex =
            new Regex("(\\\"revision\\\"\\s*:\\s*)(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [Serializable]
        private sealed class ManifestDocument
        {
            public long revision;
            public EquipmentSlotRecord[] equipmentSlots = Array.Empty<EquipmentSlotRecord>();
        }

        [Serializable]
        private sealed class EquipmentSlotRecord
        {
            public ushort dataId;
            public string slotId;
            public string displayName;
            public int order;
            public ushort presentationSlotId;
        }

        internal readonly struct ReviewPaths
        {
            public readonly string contentRoot;
            public readonly string manifestPath;
            public readonly string armorPath;
            public readonly long revision;

            public ReviewPaths(string contentRoot, string manifestPath, string armorPath, long revision)
            {
                this.contentRoot = contentRoot;
                this.manifestPath = manifestPath;
                this.armorPath = armorPath;
                this.revision = revision;
            }
        }

        static OutfitAuthoringSaveBridge()
        {
            OutfitAuthoringController.EditorSaveHandler = Save;
            OutfitAuthoringController.EditorEquipmentSlotProvider = LoadEquipmentSlots;
            OutfitAuthoringController.EditorEnsurePrimaryDyeHandler =
                CharacterVisualPaletteAuthoringWindow.EnsurePrimaryDyeForOutfit;
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open Play Mode Outfit Builder")]
        private static void OpenScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            TryResolveServerContentRoot(true, out _);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open And Play")]
        private static void OpenAndPlay()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            TryResolveServerContentRoot(true, out _);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Set Server Content Root")]
        private static void SetServerContentRoot()
        {
            string current = EditorPrefs.GetString(ServerContentRootPreference, string.Empty);
            string start = Directory.Exists(current) ? current : ProjectRoot();
            string selected = EditorUtility.OpenFolderPanel(
                "Select standalone server Content folder (contains GameplayContent.json)",
                start,
                string.Empty);
            if (string.IsNullOrWhiteSpace(selected))
                return;

            if (!TryNormalizeContentRoot(selected, out string normalized))
            {
                EditorUtility.DisplayDialog(
                    "Server Content Root Not Set",
                    "Choose the Content folder containing GameplayContent.json, or the server repository root containing Content/GameplayContent.json.",
                    "OK");
                return;
            }

            EditorPrefs.SetString(ServerContentRootPreference, normalized);
            Debug.Log($"[OutfitAuthoring] Server Content Root = {normalized}");
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Clear Server Content Root")]
        private static void ClearServerContentRoot() => EditorPrefs.DeleteKey(ServerContentRootPreference);

        internal static bool TryGetReviewPaths(out ReviewPaths paths)
        {
            paths = default;
            if (!TryResolveServerContentRoot(false, out string contentRoot))
                return false;

            string manifestPath = Path.Combine(contentRoot, ManifestFileName);
            string armorPath = Path.Combine(contentRoot, ArmorRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(manifestPath) || !File.Exists(armorPath))
                return false;

            long revision = 0;
            try
            {
                Match m = RevisionRegex.Match(File.ReadAllText(manifestPath));
                if (m.Success)
                    long.TryParse(m.Groups[2].Value, out revision);
            }
            catch { }

            paths = new ReviewPaths(contentRoot, manifestPath, armorPath, revision);
            return true;
        }

        internal static OutfitAuthoringEquipmentSlotOption[] GetEquipmentSlotsForReview() => LoadEquipmentSlots();

        private static OutfitAuthoringEquipmentSlotOption[] LoadEquipmentSlots()
        {
            if (!TryResolveServerContentRoot(false, out string contentRoot))
                return Array.Empty<OutfitAuthoringEquipmentSlotOption>();

            string manifestPath = Path.Combine(contentRoot, ManifestFileName);
            try
            {
                string json = File.ReadAllText(manifestPath);
                ManifestDocument manifest = JsonUtility.FromJson<ManifestDocument>(json);
                EquipmentSlotRecord[] slots = manifest?.equipmentSlots ?? Array.Empty<EquipmentSlotRecord>();
                return slots
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.slotId))
                    .OrderBy(s => s.order)
                    .ThenBy(s => s.displayName ?? s.slotId, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new OutfitAuthoringEquipmentSlotOption
                    {
                        dataId = s.dataId,
                        slotId = s.slotId,
                        displayName = s.displayName,
                        order = s.order,
                        presentationSlotId = s.presentationSlotId,
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OutfitAuthoring] Failed to read server equipment slots: {ex.Message}");
                return Array.Empty<OutfitAuthoringEquipmentSlotOption>();
            }
        }

        private static OutfitAuthoringSaveResult Save(OutfitAuthoringSaveRequest request)
        {
            request ??= new OutfitAuthoringSaveRequest();

            string itemName = (request.itemName ?? string.Empty).Trim();
            string slotId = (request.equipmentSlotId ?? string.Empty).Trim();
            if (itemName.Length == 0)
                return Fail("Item name is required.");
            if (slotId.Length == 0)
                return Fail("A valid server Equipment Slot is required.");

            if (!TryGetReviewPaths(out ReviewPaths paths))
                return Fail("Server Content Root is not configured or its Armor/manifest files are missing.");

            OutfitAuthoringEquipmentSlotOption serverSlot = LoadEquipmentSlots()
                .FirstOrDefault(s => string.Equals(s.slotId, slotId, StringComparison.Ordinal));
            if (serverSlot == null)
                return Fail("Selected Equipment Slot no longer exists in the server manifest. Reopen/reselect it.");

            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(
                CharacterVisualCatalogBuilder.OutputAssetPath);
            if (profile == null)
                return Fail("CharacterVisualProfile.asset was not found.");

            string originalArmor;
            string originalManifest;
            try
            {
                originalArmor = File.ReadAllText(paths.armorPath);
                originalManifest = File.ReadAllText(paths.manifestPath);
            }
            catch (Exception ex)
            {
                return Fail($"Could not read server content files: {ex.Message}");
            }

            CharacterWearableSetDefinition[] originalWearables =
                profile.WearableSets != null ? profile.WearableSets.ToArray() : Array.Empty<CharacterWearableSetDefinition>();

            string definitionId;
            ushort presentationId;
            int wearableIndex = -1;
            string existingServerObject = null;

            if (request.updateExisting)
            {
                definitionId =
                    (request.existingDefinitionId ?? string.Empty).Trim();

                if (definitionId.Length == 0)
                    return Fail(
                        "Existing-item edit mode has no server definition identity.");

                for (int i = 0; i < originalWearables.Length; ++i)
                {
                    CharacterWearableSetDefinition w = originalWearables[i];
                    if (w != null &&
                        string.Equals(
                            w.definitionId,
                            definitionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        wearableIndex = i;
                        break;
                    }
                }

                if (wearableIndex < 0 &&
                    request.existingPresentationId != 0)
                {
                    for (int i = 0; i < originalWearables.Length; ++i)
                    {
                        CharacterWearableSetDefinition w = originalWearables[i];
                        if (w != null &&
                            w.presentationId ==
                            request.existingPresentationId)
                        {
                            wearableIndex = i;
                            break;
                        }
                    }
                }

                if (!TryFindItemObject(
                        originalArmor,
                        definitionId,
                        out _,
                        out _,
                        out string serverObject))
                {
                    return Fail(
                        $"Server Armor item '{definitionId}' no longer exists. Update aborted.");
                }

                existingServerObject = serverObject;

                ushort currentServerPresentation = 0;
                Match serverPresentation = PresentationIdRegex.Match(serverObject);
                if (serverPresentation.Success)
                {
                    ushort.TryParse(
                        serverPresentation.Groups[1].Value,
                        out currentServerPresentation);
                }

                if (currentServerPresentation != 0)
                {
                    presentationId = currentServerPresentation;

                    if (request.existingPresentationId != 0 &&
                        request.existingPresentationId != presentationId)
                    {
                        return Fail(
                            $"Server Armor item '{definitionId}' presentationId changed. Refresh before updating.");
                    }

                    if (wearableIndex >= 0 &&
                        originalWearables[wearableIndex].presentationId != presentationId)
                    {
                        return Fail(
                            $"Client wearable '{definitionId}' presentationId does not match server presentation {presentationId}.");
                    }
                }
                else if (wearableIndex >= 0 &&
                         originalWearables[wearableIndex].presentationId != 0)
                {
                    presentationId =
                        originalWearables[wearableIndex].presentationId;
                }
                else
                {
                    try
                    {
                        presentationId =
                            NextPresentationId(
                                paths.contentRoot,
                                originalWearables);
                    }
                    catch (Exception ex)
                    {
                        return Fail(ex.Message);
                    }
                }
            }
            else
            {
                for (int i = 0; i < originalWearables.Length; ++i)
                {
                    CharacterWearableSetDefinition w = originalWearables[i];
                    if (w != null && string.Equals((w.displayName ?? string.Empty).Trim(), itemName, StringComparison.OrdinalIgnoreCase))
                        return Fail($"An authored outfit named '{itemName}' already exists. Load it from Existing Item and use UPDATE EXISTING.");
                }

                try
                {
                    presentationId = NextPresentationId(paths.contentRoot, originalWearables);
                    definitionId = NextDefinitionId(paths.contentRoot, itemName);
                }
                catch (Exception ex)
                {
                    return Fail(ex.Message);
                }
            }

            CharacterWearablePartSelection[] parts = request.parts ?? Array.Empty<CharacterWearablePartSelection>();
            var colorValues = new List<ushort>((request.allowedColorIds ?? Array.Empty<ushort>())
                .Where(id => id > 0)
                .Distinct());

            ushort primary = request.defaultPrimaryColorId;
            ushort secondary = request.defaultSecondaryColorId;

            // A tested default is also a valid color for this wearable. Do not invent
            // palette IDs when the author has not selected any color.
            if (primary > 0 && !colorValues.Contains(primary))
                colorValues.Add(primary);
            if (secondary > 0 && !colorValues.Contains(secondary))
                colorValues.Add(secondary);

            ushort[] colors = colorValues
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            CharacterWearableRegion region = Enum.IsDefined(typeof(CharacterWearableRegion), request.visualRegion)
                ? request.visualRegion
                : CharacterWearableRegion.Upper;

            var saved = new CharacterWearableSetDefinition
            {
                definitionId = definitionId,
                displayName = itemName,
                presentationId = presentationId,
                region = region,
                playerEligible = true,
                populationEligible = true,
                defaultPrimaryColorId = primary,
                defaultSecondaryColorId = secondary,
                colorUsageReviewed = colors.Length > 0 || primary > 0 || secondary > 0,
                playerAllowedColorIds = (ushort[])colors.Clone(),
                populationAllowedColorIds = (ushort[])colors.Clone(),
                parts = (CharacterWearablePartSelection[])parts.Clone(),
                icon = wearableIndex >= 0 ? originalWearables[wearableIndex].icon : null,
            };

            string serverItemJson = request.updateExisting
                ? UpdateArmorItemJsonPreservingDetails(
                    existingServerObject,
                    itemName,
                    presentationId,
                    slotId)
                : BuildArmorItemJson(
                    definitionId,
                    itemName,
                    presentationId,
                    slotId);

            string updatedArmor;
            try
            {
                updatedArmor = request.updateExisting
                    ? ReplaceItemInCatalog(originalArmor, definitionId, serverItemJson)
                    : AppendItemToCatalog(originalArmor, serverItemJson);
            }
            catch (Exception ex)
            {
                return Fail($"Could not prepare server Armor content: {ex.Message}");
            }

            string updatedManifest;
            try { updatedManifest = IncrementManifestRevision(originalManifest); }
            catch (Exception ex) { return Fail(ex.Message); }

            bool armorWritten = false;
            bool manifestWritten = false;
            try
            {
                var values =
                    new List<CharacterWearableSetDefinition>(originalWearables);

                if (request.updateExisting && wearableIndex >= 0)
                    values[wearableIndex] = saved;
                else
                    values.Add(saved);

                Undo.RecordObject(profile, request.updateExisting ? "Update Existing Outfit Item" : "Create Outfit Item");
                profile.SetWearableSets(values.ToArray());
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
                CharacterVisualProfileRegistry.ResetForTestsOrReload();

                ValidateArmorJsonBeforeWrite(updatedArmor);
                WriteFileSafely(paths.armorPath, updatedArmor);
                armorWritten = true;
                WriteFileSafely(paths.manifestPath, updatedManifest);
                manifestWritten = true;
            }
            catch (Exception ex)
            {
                try
                {
                    profile.SetWearableSets(originalWearables);
                    EditorUtility.SetDirty(profile);
                    AssetDatabase.SaveAssets();
                    CharacterVisualProfileRegistry.ResetForTestsOrReload();
                }
                catch { }

                try
                {
                    if (armorWritten) WriteFileSafely(paths.armorPath, originalArmor);
                    if (manifestWritten) WriteFileSafely(paths.manifestPath, originalManifest);
                }
                catch { }

                return Fail($"Save rolled back because one side failed: {ex.Message}");
            }

            string verb = request.updateExisting ? "Updated" : "Created";
            return new OutfitAuthoringSaveResult(
                true,
                $"{verb} {itemName} | {definitionId} | presentation {presentationId}. Server manifest revision advanced; the existing gameplay-settings cache will refresh through its normal revision/snapshot path.",
                definitionId,
                presentationId,
                request.updateExisting);
        }

        private static OutfitAuthoringSaveResult Fail(string message) => new OutfitAuthoringSaveResult(false, message);

        private static ushort NextPresentationId(string contentRoot, IReadOnlyList<CharacterWearableSetDefinition> wearables)
        {
            int max = 0;
            string itemsRoot = Path.Combine(contentRoot, "Items");
            if (Directory.Exists(itemsRoot))
            {
                string[] files = Directory.GetFiles(itemsRoot, "*.json", SearchOption.AllDirectories);
                for (int f = 0; f < files.Length; ++f)
                {
                    MatchCollection matches = PresentationIdRegex.Matches(File.ReadAllText(files[f]));
                    for (int i = 0; i < matches.Count; ++i)
                        if (int.TryParse(matches[i].Groups[1].Value, out int value) && value > max)
                            max = value;
                }
            }

            if (wearables != null)
                for (int i = 0; i < wearables.Count; ++i)
                    if (wearables[i] != null && wearables[i].presentationId > max)
                        max = wearables[i].presentationId;

            if (max >= ushort.MaxValue)
                throw new InvalidOperationException("No free ushort presentation ID remains.");
            return (ushort)Math.Max(1, max + 1);
        }

        private static string NextDefinitionId(string contentRoot, string itemName)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string itemsRoot = Path.Combine(contentRoot, "Items");
            if (Directory.Exists(itemsRoot))
            {
                string[] files = Directory.GetFiles(itemsRoot, "*.json", SearchOption.AllDirectories);
                for (int f = 0; f < files.Length; ++f)
                {
                    MatchCollection matches = DefinitionIdRegex.Matches(File.ReadAllText(files[f]));
                    for (int i = 0; i < matches.Count; ++i)
                        if (!string.IsNullOrWhiteSpace(matches[i].Groups[1].Value))
                            used.Add(matches[i].Groups[1].Value);
                }
            }

            string slug = Slug(itemName);
            string baseId = "item.armor." + (slug.Length > 0 ? slug : "authored");
            if (!used.Contains(baseId))
                return baseId;
            for (int i = 2; i < 100000; ++i)
            {
                string candidate = baseId + "." + i;
                if (!used.Contains(candidate))
                    return candidate;
            }
            throw new InvalidOperationException("Could not allocate a unique server item definitionId.");
        }

        private static string Slug(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var builder = new StringBuilder(value.Length);
            bool separator = false;
            for (int i = 0; i < value.Length; ++i)
            {
                char c = char.ToLowerInvariant(value[i]);
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    separator = false;
                }
                else if (!separator && builder.Length > 0)
                {
                    builder.Append('_');
                    separator = true;
                }
            }
            return builder.ToString().Trim('_');
        }

        private static string UpdateArmorItemJsonPreservingDetails(
            string existingJson,
            string displayName,
            ushort presentationId,
            string equipmentSlotId)
        {
            if (string.IsNullOrWhiteSpace(existingJson))
                throw new InvalidOperationException(
                    "Existing server Armor item JSON is unavailable.");

            string updated = existingJson;

            updated = Regex.Replace(
                updated,
                "(\\\"displayName\\\"\\s*:\\s*)\\\"[^\\\"]*\\\"",
                match =>
                    match.Groups[1].Value +
                    "\"" +
                    JsonEscape(displayName) +
                    "\"",
                RegexOptions.CultureInvariant);

            updated = Regex.Replace(
                updated,
                "(\\\"presentationId\\\"\\s*:\\s*)\\d+",
                match =>
                    match.Groups[1].Value +
                    presentationId.ToString(CultureInfo.InvariantCulture),
                RegexOptions.CultureInvariant);

            string slotArray =
                "\"allowedEquipmentSlots\": [\"" +
                JsonEscape(equipmentSlotId) +
                "\"]";

            Regex slotRegex = new Regex(
                "\\\"allowedEquipmentSlots\\\"\\s*:\\s*\\[[^\\]]*\\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);

            if (slotRegex.IsMatch(updated))
            {
                updated = slotRegex.Replace(
                    updated,
                    slotArray,
                    1);
            }
            else
            {
                throw new InvalidOperationException(
                    "Existing Armor item has no allowedEquipmentSlots array.");
            }

            return updated;
        }

        private static string BuildArmorItemJson(string definitionId, string displayName, ushort presentationId, string equipmentSlotId)
        {
            string qDefinition = JsonEscape(definitionId);
            string qName = JsonEscape(displayName);
            string qSlot = JsonEscape(equipmentSlotId);
            return
                "{\n" +
                $"  \"definitionId\": \"{qDefinition}\",\n" +
                $"  \"displayName\": \"{qName}\",\n" +
                "  \"kind\": \"Equipment\",\n" +
                "  \"subtype\": \"Armor\",\n" +
                $"  \"presentationId\": {presentationId},\n" +
                "  \"maxStack\": 1,\n" +
                "  \"weight\": 0.0,\n" +
                "  \"maxDurability\": 0,\n" +
                "  \"tags\": [],\n" +
                "  \"equipment\": {\n" +
                $"    \"allowedEquipmentSlots\": [\"{qSlot}\"],\n" +
                "    \"statModifiers\": []\n" +
                "  }\n" +
                "}";
        }

        private static string JsonEscape(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string AppendItemToCatalog(string json, string itemJson)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Armor.json is empty.");
            if (!TryFindArray(json, "items", out int openIndex, out int closeIndex))
                throw new InvalidOperationException("Armor.json does not contain an items array.");

            string body = json.Substring(openIndex + 1, closeIndex - openIndex - 1);
            bool hasItems = DefinitionIdRegex.IsMatch(body);
            string closingIndent = LineIndentAt(json, closeIndex);
            string itemIndent = closingIndent + "  ";
            string indentedItem = IndentBlock(itemJson, itemIndent);
            string before = json.Substring(0, closeIndex).TrimEnd();
            string after = json.Substring(closeIndex);
            return before + (hasItems ? "," : string.Empty) + Environment.NewLine + indentedItem + Environment.NewLine + closingIndent + after;
        }

        private static string ReplaceItemInCatalog(string json, string definitionId, string replacementJson)
        {
            if (!TryFindItemObject(json, definitionId, out int start, out int endExclusive, out _))
                throw new InvalidOperationException($"Armor.json does not contain item '{definitionId}'.");

            string indent = LineIndentAt(json, start);
            string replacement = IndentBlock(replacementJson, indent);
            return json.Substring(0, start) + replacement + json.Substring(endExclusive);
        }

        private static bool TryFindItemObject(string json, string definitionId, out int start, out int endExclusive, out string objectJson)
        {
            start = -1;
            endExclusive = -1;
            objectJson = string.Empty;
            if (!TryFindArray(json, "items", out int open, out int close))
                return false;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            int objectStart = -1;

            for (int i = open + 1; i < close; ++i)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{')
                {
                    if (depth == 0) objectStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        int objectEnd = i + 1;
                        string candidate = json.Substring(objectStart, objectEnd - objectStart);
                        Match match = DefinitionIdRegex.Match(candidate);
                        if (match.Success && string.Equals(match.Groups[1].Value, definitionId, StringComparison.OrdinalIgnoreCase))
                        {
                            start = objectStart;
                            endExclusive = objectEnd;
                            objectJson = candidate;
                            return true;
                        }
                        objectStart = -1;
                    }
                }
            }
            return false;
        }

        private static bool TryFindArray(string json, string propertyName, out int openIndex, out int closeIndex)
        {
            openIndex = -1;
            closeIndex = -1;
            Match property = Regex.Match(json, "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\[", RegexOptions.CultureInvariant);
            if (!property.Success) return false;
            openIndex = json.IndexOf('[', property.Index + property.Length - 1);
            if (openIndex < 0) return false;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            for (int i = openIndex; i < json.Length; ++i)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeIndex = i;
                        return true;
                    }
                }
            }
            return false;
        }

        private static string IncrementManifestRevision(string json)
        {
            Match match = RevisionRegex.Match(json ?? string.Empty);
            if (!match.Success || !long.TryParse(match.Groups[2].Value, out long revision))
                throw new InvalidOperationException("GameplayContent.json does not contain a numeric revision.");
            if (revision == long.MaxValue)
                throw new InvalidOperationException("Gameplay content revision cannot be incremented further.");
            return json.Substring(0, match.Groups[2].Index) + (revision + 1) + json.Substring(match.Groups[2].Index + match.Groups[2].Length);
        }

        private static string LineIndentAt(string text, int index)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int i = lineStart;
            while (i < text.Length && i < index && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(lineStart, i - lineStart);
        }

        private static string IndentBlock(string value, string indent)
        {
            string[] lines = (value ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            return string.Join(Environment.NewLine, lines.Select(line => indent + line));
        }

        private static void ValidateArmorJsonBeforeWrite(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException(
                    "Generated Armor.json is empty.");

            ArmorValidationDocument document;
            try
            {
                document = JsonUtility.FromJson<ArmorValidationDocument>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Generated Armor.json is invalid JSON. Save was blocked before writing: " +
                    ex.Message);
            }

            ArmorValidationItem[] items =
                document?.items ?? Array.Empty<ArmorValidationItem>();

            if (items.Length == 0)
                throw new InvalidOperationException(
                    "Generated Armor.json contains no readable items. Save was blocked.");

            for (int i = 0; i < items.Length; ++i)
            {
                if (items[i] == null ||
                    string.IsNullOrWhiteSpace(items[i].definitionId))
                {
                    throw new InvalidOperationException(
                        $"Generated Armor.json item {i + 1} has no definitionId. Save was blocked.");
                }
            }
        }

        private static void WriteFileSafely(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Target file has no directory.");
            Directory.CreateDirectory(directory);

            string temp = path + ".outfit-authoring.tmp";
            string backup = path + ".outfit-authoring.bak";
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    try { File.Replace(temp, path, backup, true); }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(path, backup, true);
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static bool TryResolveServerContentRoot(bool interactive, out string contentRoot)
        {
            string saved = EditorPrefs.GetString(ServerContentRootPreference, string.Empty);
            if (TryNormalizeContentRoot(saved, out contentRoot))
                return true;

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string projectRoot = ProjectRoot();
            AddCandidate(candidates, Path.Combine(projectRoot, "Server", "Content"));

            DirectoryInfo parent = Directory.GetParent(projectRoot);
            if (parent != null && parent.Exists)
            {
                AddCandidate(candidates, Path.Combine(parent.FullName, "Testbed-Server-Source", "Content"));
                DirectoryInfo[] siblings;
                try { siblings = parent.GetDirectories(); }
                catch { siblings = Array.Empty<DirectoryInfo>(); }
                for (int i = 0; i < siblings.Length; ++i)
                    AddCandidate(candidates, Path.Combine(siblings[i].FullName, "Content"));
            }

            if (candidates.Count == 1)
            {
                contentRoot = candidates.First();
                EditorPrefs.SetString(ServerContentRootPreference, contentRoot);
                return true;
            }

            if (interactive)
            {
                string selected = EditorUtility.OpenFolderPanel(
                    "Select standalone server Content folder (contains GameplayContent.json)",
                    parent?.FullName ?? projectRoot,
                    string.Empty);
                if (TryNormalizeContentRoot(selected, out contentRoot))
                {
                    EditorPrefs.SetString(ServerContentRootPreference, contentRoot);
                    return true;
                }
            }

            contentRoot = string.Empty;
            return false;
        }

        private static void AddCandidate(HashSet<string> values, string path)
        {
            if (TryNormalizeContentRoot(path, out string normalized))
                values.Add(normalized);
        }

        private static bool TryNormalizeContentRoot(string path, out string contentRoot)
        {
            contentRoot = string.Empty;
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(Path.Combine(full, ManifestFileName)))
                {
                    contentRoot = full;
                    return true;
                }
                string nested = Path.Combine(full, "Content");
                if (File.Exists(Path.Combine(nested, ManifestFileName)))
                {
                    contentRoot = Path.GetFullPath(nested);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }
}
#endif
