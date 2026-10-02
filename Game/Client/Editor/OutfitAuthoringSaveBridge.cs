#if UNITY_EDITOR
using System;
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
    /// <summary>
    /// Editor-only persistence bridge for the Play Mode Outfit Builder.
    /// Client visual data stays in CharacterVisualProfile. Matching authoritative item content
    /// is appended to the standalone server's existing Content Catalog V2 Armor.json using the
    /// same presentation id. No new runtime/network message is introduced.
    /// </summary>
    [InitializeOnLoad]
    public static class OutfitAuthoringSaveBridge
    {
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

        static OutfitAuthoringSaveBridge()
        {
            OutfitAuthoringController.EditorSaveHandler = Save;
            OutfitAuthoringController.EditorEquipmentSlotProvider = LoadEquipmentSlots;
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open Play Mode Outfit Builder")]
        private static void OpenScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            TryResolveServerContentRoot(interactive: true, out _);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open And Play")]
        private static void OpenAndPlay()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            TryResolveServerContentRoot(interactive: true, out _);
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
                    "Choose the Content folder that contains GameplayContent.json, or the server repository root that contains Content/GameplayContent.json.",
                    "OK");
                return;
            }

            EditorPrefs.SetString(ServerContentRootPreference, normalized);
            Debug.Log($"[OutfitAuthoring] Server Content Root = {normalized}");
        }

        [MenuItem("MMO Tools/Characters/Outfit Builder/Clear Server Content Root")]
        private static void ClearServerContentRoot()
        {
            EditorPrefs.DeleteKey(ServerContentRootPreference);
        }

        private static OutfitAuthoringEquipmentSlotOption[] LoadEquipmentSlots()
        {
            if (!TryResolveServerContentRoot(interactive: false, out string contentRoot))
                return Array.Empty<OutfitAuthoringEquipmentSlotOption>();

            string manifestPath = Path.Combine(contentRoot, ManifestFileName);
            try
            {
                string json = File.ReadAllText(manifestPath);
                ManifestDocument manifest = JsonUtility.FromJson<ManifestDocument>(json);
                EquipmentSlotRecord[] slots = manifest?.equipmentSlots ?? Array.Empty<EquipmentSlotRecord>();
                return slots
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.slotId) && s.presentationSlotId != 0)
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
                return new OutfitAuthoringSaveResult(false, "Item name is required.");
            if (slotId.Length == 0 || request.equipmentSlotPresentationId == 0)
                return new OutfitAuthoringSaveResult(false, "A valid server Equipment Slot is required.");

            if (!TryResolveServerContentRoot(interactive: false, out string contentRoot))
                return new OutfitAuthoringSaveResult(false, "Server Content Root is not configured. Use MMO Tools > Characters > Outfit Builder > Set Server Content Root.");

            string manifestPath = Path.Combine(contentRoot, ManifestFileName);
            string armorPath = Path.Combine(contentRoot, ArmorRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(manifestPath))
                return new OutfitAuthoringSaveResult(false, $"Server manifest was not found: {manifestPath}");
            if (!File.Exists(armorPath))
                return new OutfitAuthoringSaveResult(false, $"Server armor catalog was not found: {armorPath}");

            OutfitAuthoringEquipmentSlotOption serverSlot = LoadEquipmentSlots()
                .FirstOrDefault(s => string.Equals(s.slotId, slotId, StringComparison.Ordinal));
            if (serverSlot == null || serverSlot.presentationSlotId != request.equipmentSlotPresentationId)
                return new OutfitAuthoringSaveResult(false, "The selected Equipment Slot no longer matches the current server content manifest. Reopen the builder and select it again.");

            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(
                CharacterVisualCatalogBuilder.OutputAssetPath);
            if (profile == null)
                return new OutfitAuthoringSaveResult(false, "CharacterVisualProfile.asset was not found. Rebuild the current visual catalog first.");

            CharacterWearablePartSelection[] parts = request.parts ?? Array.Empty<CharacterWearablePartSelection>();
            ushort[] colors = (request.allowedColorIds ?? Array.Empty<ushort>())
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            string originalArmor;
            string originalManifest;
            try
            {
                originalArmor = File.ReadAllText(armorPath);
                originalManifest = File.ReadAllText(manifestPath);
            }
            catch (Exception ex)
            {
                return new OutfitAuthoringSaveResult(false, $"Could not read server content files: {ex.Message}");
            }

            ushort presentationId;
            string definitionId;
            try
            {
                presentationId = NextPresentationId(contentRoot, profile.WearableSets);
                definitionId = NextDefinitionId(contentRoot, itemName);
            }
            catch (Exception ex)
            {
                return new OutfitAuthoringSaveResult(false, ex.Message);
            }

            string updatedArmor;
            string updatedManifest;
            try
            {
                string serverItemJson = BuildArmorItemJson(
                    definitionId,
                    itemName,
                    presentationId,
                    slotId);
                updatedArmor = AppendItemToCatalog(originalArmor, serverItemJson);
                updatedManifest = IncrementManifestRevision(originalManifest);
            }
            catch (Exception ex)
            {
                return new OutfitAuthoringSaveResult(false, $"Could not prepare server item content: {ex.Message}");
            }

            ushort primary = colors.Contains((ushort)1)
                ? (ushort)1
                : colors.Length > 0 ? colors[0] : (ushort)1;
            ushort secondary = colors.Contains((ushort)8)
                ? (ushort)8
                : colors.Length > 1 ? colors[1] : primary;

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
                colorUsageReviewed = true,
                playerAllowedColorIds = (ushort[])colors.Clone(),
                populationAllowedColorIds = (ushort[])colors.Clone(),
                parts = (CharacterWearablePartSelection[])parts.Clone(),
                icon = null,
            };

            CharacterWearableSetDefinition[] originalWearables = profile.WearableSets.ToArray();
            bool armorWritten = false;
            bool manifestWritten = false;
            try
            {
                var values = new List<CharacterWearableSetDefinition>(originalWearables) { saved };
                Undo.RecordObject(profile, "Save Play Mode Outfit Item");
                profile.SetWearableSets(values.ToArray());
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
                CharacterVisualProfileRegistry.ResetForTestsOrReload();

                WriteFileSafely(armorPath, updatedArmor);
                armorWritten = true;
                WriteFileSafely(manifestPath, updatedManifest);
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
                    if (armorWritten) WriteFileSafely(armorPath, originalArmor);
                    if (manifestWritten) WriteFileSafely(manifestPath, originalManifest);
                }
                catch { }

                return new OutfitAuthoringSaveResult(false, $"Save rolled back because one side failed: {ex.Message}");
            }

            return new OutfitAuthoringSaveResult(
                true,
                $"Saved {itemName} -> {slotId} | presentation {presentationId} | client wearable + server Armor catalog.");
        }

        private static ushort NextPresentationId(string contentRoot, IReadOnlyList<CharacterWearableSetDefinition> wearables)
        {
            int max = 0;
            string itemsRoot = Path.Combine(contentRoot, "Items");
            if (Directory.Exists(itemsRoot))
            {
                string[] files = Directory.GetFiles(itemsRoot, "*.json", SearchOption.AllDirectories);
                for (int f = 0; f < files.Length; ++f)
                {
                    string text = File.ReadAllText(files[f]);
                    MatchCollection matches = PresentationIdRegex.Matches(text);
                    for (int i = 0; i < matches.Count; ++i)
                        if (int.TryParse(matches[i].Groups[1].Value, out int value) && value > max)
                            max = value;
                }
            }

            if (wearables != null)
            {
                for (int i = 0; i < wearables.Count; ++i)
                    if (wearables[i] != null && wearables[i].presentationId > max)
                        max = wearables[i].presentationId;
            }

            if (max >= ushort.MaxValue)
                throw new InvalidOperationException("No free ushort presentation ID remains for authored equipment.");
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
                    string text = File.ReadAllText(files[f]);
                    MatchCollection matches = DefinitionIdRegex.Matches(text);
                    for (int i = 0; i < matches.Count; ++i)
                        if (!string.IsNullOrWhiteSpace(matches[i].Groups[1].Value))
                            used.Add(matches[i].Groups[1].Value);
                }
            }

            string slug = Slug(itemName);
            string baseId = "armor." + (slug.Length > 0 ? slug : "authored");
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
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
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

        private static string BuildArmorItemJson(
            string definitionId,
            string displayName,
            ushort presentationId,
            string equipmentSlotId)
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
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
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
            string separator = hasItems ? "," : string.Empty;
            return before + separator + Environment.NewLine + indentedItem + Environment.NewLine + closingIndent + after;
        }

        private static bool TryFindArray(string json, string propertyName, out int openIndex, out int closeIndex)
        {
            openIndex = -1;
            closeIndex = -1;
            Match property = Regex.Match(
                json,
                "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\[",
                RegexOptions.CultureInvariant);
            if (!property.Success)
                return false;
            openIndex = json.IndexOf('[', property.Index + property.Length - 1);
            if (openIndex < 0)
                return false;

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
            while (i < text.Length && i < index && (text[i] == ' ' || text[i] == '\t'))
                i++;
            return text.Substring(lineStart, i - lineStart);
        }

        private static string IndentBlock(string value, string indent)
        {
            string[] lines = (value ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            return string.Join(Environment.NewLine, lines.Select(line => indent + line));
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
                    try
                    {
                        File.Replace(temp, path, backup, true);
                    }
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
                if (File.Exists(temp))
                    File.Delete(temp);
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
            if (string.IsNullOrWhiteSpace(path))
                return false;
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

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }
}
#endif
