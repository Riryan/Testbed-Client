#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Game.Client.OutfitAuthoring;
using Game.Client.Presentation.Characters;
using Player.Networking;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    /// <summary>
    /// Permanent editor-side bridge for the prefab-based Outfit Builder. It uses the existing
    /// configured server Content root, existing gameplay-settings cache and existing save handler.
    /// No MenuItem installer and no second cache/content format are introduced.
    /// </summary>
    [InitializeOnLoad]
    internal static class OutfitAuthoringEquipmentDetailsBridge
    {

        [Serializable]
        private sealed class ArmorCatalogDocument
        {
            public ArmorCatalogItem[] items = Array.Empty<ArmorCatalogItem>();
        }

        [Serializable]
        private sealed class ArmorCatalogItem
        {
            public string definitionId;
            public string displayName;
            public string kind;
            public string subtype;
            public ushort presentationId;
            public int maxStack = 1;
            public float weight;
            public int maxDurability;
            public string[] tags = Array.Empty<string>();
            public ArmorEquipmentBlock equipment;
        }

        [Serializable]
        private sealed class ArmorEquipmentBlock
        {
            public string[] allowedEquipmentSlots = Array.Empty<string>();
            public ArmorStatModifier[] statModifiers = Array.Empty<ArmorStatModifier>();
        }

        [Serializable]
        private sealed class ArmorStatModifier
        {
            public string statId;
            public float additive;
            public float multiplier = 1f;
        }

        private static readonly Regex DefinitionIdRegex = new Regex("\\\"definitionId\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DisplayNameRegex = new Regex("\\\"displayName\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PresentationIdRegex = new Regex("\\\"presentationId\\\"\\s*:\\s*(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DataIdRegex = new Regex("\\\"dataId\\\"\\s*:\\s*(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MaxStackRegex = new Regex("\\\"maxStack\\\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex WeightRegex = new Regex("\\\"weight\\\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DurabilityRegex = new Regex("\\\"maxDurability\\\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SlotRegex = new Regex("\\\"allowedEquipmentSlots\\\"\\s*:\\s*\\[\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TagsArrayRegex = new Regex("\\\"tags\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex StatIdRegex = new Regex("\\\"statId\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MaximumStatIdRegex = new Regex("\\\"maximumStatId\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DefenseStatIdRegex = new Regex("\\\"defenseStatId\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex QuotedValueRegex = new Regex("\\\"([^\\\"]+)\\\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static Func<OutfitAuthoringSaveRequest, OutfitAuthoringSaveResult> _baseSaveHandler;

        static OutfitAuthoringEquipmentDetailsBridge()
        {
            BindPermanentDelegates();
            EditorApplication.delayCall += TryWrapExistingSaveHandler;
        }

        private static void BindPermanentDelegates()
        {
            OutfitAuthoringEquipmentDetailsPanel.EditorServerItemsProvider = LoadServerItems;
            OutfitAuthoringEquipmentDetailsPanel.EditorOptionsProvider = LoadOptions;
            OutfitAuthoringEquipmentDetailsPanel.EditorCacheStatusProvider = CheckCache;
            OutfitAuthoringEquipmentDetailsPanel.EditorEquipmentUpdateHandler = UpdateEquipmentOnly;
            OutfitAuthoringEquipmentDetailsPanel.EditorSetDefaultBodyHandler = SetDefaultBody;
        }

        private static void TryWrapExistingSaveHandler()
        {
            BindPermanentDelegates();
            Func<OutfitAuthoringSaveRequest, OutfitAuthoringSaveResult> current = OutfitAuthoringController.EditorSaveHandler;
            if (current == null)
            {
                EditorApplication.delayCall += TryWrapExistingSaveHandler;
                return;
            }

            if (current == SaveWithWorkbenchDetails)
                return;

            _baseSaveHandler = current;
            OutfitAuthoringController.EditorSaveHandler = SaveWithWorkbenchDetails;
        }

        private static OutfitAuthoringSaveResult SaveWithWorkbenchDetails(
            OutfitAuthoringSaveRequest request)
        {
            if (_baseSaveHandler == null)
                return new OutfitAuthoringSaveResult(
                    false,
                    "Outfit Builder base save handler is unavailable.");

            // V7 recovery gate:
            // Do not perform a second Armor.json rewrite here.
            // The base save path owns the authoritative item update and now preserves
            // existing weight/durability/tags/statModifiers on UPDATE EXISTING.
            OutfitAuthoringSaveResult result =
                _baseSaveHandler(request);

            if (result.success)
                OutfitAuthoringEquipmentDetailsPanel.Active
                    ?.NotifyServerSaveCompleted(result.definitionId);

            return result;
        }

        private static OutfitWorkbenchServerItem[] LoadServerItems()
        {
            if (!OutfitAuthoringSaveBridge.TryGetReviewPaths(
                    out OutfitAuthoringSaveBridge.ReviewPaths paths))
            {
                throw new InvalidOperationException(
                    "Server Content Root is not configured, or GameplayContent.json / Items/Equipment/Armor.json is missing.");
            }

            if (!File.Exists(paths.armorPath))
                throw new FileNotFoundException("Armor.json was not found.", paths.armorPath);

            string json = File.ReadAllText(paths.armorPath);

            ArmorCatalogDocument document;
            try
            {
                document = JsonUtility.FromJson<ArmorCatalogDocument>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Armor.json is invalid JSON and could not be parsed: {ex.Message}");
            }

            ArmorCatalogItem[] source =
                document?.items ?? Array.Empty<ArmorCatalogItem>();

            if (source.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Armor.json was read but contains no parsed items: '{paths.armorPath}'.");
            }

            var result = new List<OutfitWorkbenchServerItem>(source.Length);

            for (int i = 0; i < source.Length; ++i)
            {
                ArmorCatalogItem item = source[i];
                if (item == null || string.IsNullOrWhiteSpace(item.definitionId))
                    continue;

                string slotId = string.Empty;
                if (item.equipment?.allowedEquipmentSlots != null &&
                    item.equipment.allowedEquipmentSlots.Length > 0)
                {
                    slotId = item.equipment.allowedEquipmentSlots[0] ?? string.Empty;
                }

                ArmorStatModifier[] sourceMods =
                    item.equipment?.statModifiers ??
                    Array.Empty<ArmorStatModifier>();

                var mods =
                    new OutfitWorkbenchStatModifier[sourceMods.Length];

                for (int m = 0; m < sourceMods.Length; ++m)
                {
                    ArmorStatModifier sourceMod = sourceMods[m];
                    mods[m] = new OutfitWorkbenchStatModifier
                    {
                        statId = sourceMod?.statId ?? string.Empty,
                        additive = sourceMod?.additive ?? 0f,
                        multiplier = sourceMod?.multiplier ?? 1f,
                    };
                }

                result.Add(new OutfitWorkbenchServerItem
                {
                    definitionId = item.definitionId,
                    displayName = string.IsNullOrWhiteSpace(item.displayName)
                        ? item.definitionId
                        : item.displayName,
                    presentationId = item.presentationId,
                    equipmentSlotId = slotId,
                    maxStack = Math.Max(1, item.maxStack),
                    weight = Math.Max(0f, item.weight),
                    maxDurability = Math.Max(0, item.maxDurability),
                    tags = item.tags ?? Array.Empty<string>(),
                    statModifiers = mods,
                });
            }

            if (result.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Armor.json contains item objects, but none have a definitionId: '{paths.armorPath}'.");
            }

            return result
                .OrderBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static OutfitWorkbenchOptions LoadOptions()
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            var stats = new HashSet<string>(StringComparer.Ordinal)
            {
                "Health.Max", "Mana.Max", "Stamina.Max", "Armor", "AttackPower"
            };

            if (!OutfitAuthoringSaveBridge.TryGetReviewPaths(out OutfitAuthoringSaveBridge.ReviewPaths paths))
                return new OutfitWorkbenchOptions { tags = Array.Empty<string>(), statIds = stats.OrderBy(x => x).ToArray() };

            string[] files = Directory.Exists(paths.contentRoot)
                ? Directory.GetFiles(paths.contentRoot, "*.json", SearchOption.AllDirectories)
                : Array.Empty<string>();

            for (int i = 0; i < files.Length; ++i)
            {
                string json;
                try { json = File.ReadAllText(files[i]); }
                catch { continue; }

                MatchCollection tagArrays = TagsArrayRegex.Matches(json);
                for (int a = 0; a < tagArrays.Count; ++a)
                {
                    MatchCollection values = QuotedValueRegex.Matches(tagArrays[a].Groups[1].Value);
                    for (int v = 0; v < values.Count; ++v)
                        if (!string.IsNullOrWhiteSpace(values[v].Groups[1].Value)) tags.Add(values[v].Groups[1].Value);
                }
                CollectMatches(json, StatIdRegex, stats);
                CollectMatches(json, MaximumStatIdRegex, stats);
                CollectMatches(json, DefenseStatIdRegex, stats);
            }

            return new OutfitWorkbenchOptions
            {
                tags = tags.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                statIds = stats.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            };
        }

        private static OutfitWorkbenchCacheStatus CheckCache(string definitionId)
        {
            OutfitWorkbenchServerItem server = LoadServerItems().FirstOrDefault(x =>
                string.Equals(x.definitionId, definitionId, StringComparison.OrdinalIgnoreCase));
            if (server == null)
                return new OutfitWorkbenchCacheStatus { state = "SERVER MISSING" };

            GameplayItemReferenceWire cache;
            long cacheRevision = PlayerGameplaySettingsRuntime.Revision;
            Dictionary<ushort, string> slotNames = new Dictionary<ushort, string>();

            if (cacheRevision > 0)
            {
                if (!PlayerGameplaySettingsRuntime.TryGetItem(definitionId, out cache))
                    return new OutfitWorkbenchCacheStatus { state = "CACHE MISSING" };
                ushort[] dataIds = cache.allowedSlotDataIds ?? Array.Empty<ushort>();
                for (int i = 0; i < dataIds.Length; ++i)
                    if (PlayerGameplaySettingsRuntime.TryGetEquipmentSlot(dataIds[i], out GameplayEquipmentSlotReferenceWire slot))
                        slotNames[dataIds[i]] = slot.slotId;
            }
            else
            {
                if (!PlayerEntityGameManager.TryReadLatestGameplaySettingsCacheForEditor(
                        out GameplaySettingsSnapshotMessage snapshot, out _, out string detail))
                    return new OutfitWorkbenchCacheStatus { state = "NOT LOADED", detail = detail };

                cacheRevision = snapshot.revision;
                GameplayItemReferenceWire[] items = snapshot.items ?? Array.Empty<GameplayItemReferenceWire>();
                bool found = false;
                cache = default;
                for (int i = 0; i < items.Length; ++i)
                {
                    if (string.Equals(items[i].definitionId, definitionId, StringComparison.Ordinal))
                    {
                        cache = items[i];
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return new OutfitWorkbenchCacheStatus { state = "CACHE MISSING" };

                GameplayEquipmentSlotReferenceWire[] slots = snapshot.equipmentSlots ?? Array.Empty<GameplayEquipmentSlotReferenceWire>();
                for (int i = 0; i < slots.Length; ++i)
                    if (slots[i].dataId != 0) slotNames[slots[i].dataId] = slots[i].slotId;
            }

            if (OutfitAuthoringSaveBridge.TryGetReviewPaths(out OutfitAuthoringSaveBridge.ReviewPaths paths) &&
                paths.revision > 0 && cacheRevision > 0 && cacheRevision < paths.revision)
                return new OutfitWorkbenchCacheStatus { state = "STALE", detail = $"cache {cacheRevision} / server {paths.revision}" };

            if (cache.presentationId != server.presentationId)
                return new OutfitWorkbenchCacheStatus { state = "MISMATCH", detail = $"presentation {server.presentationId}/{cache.presentationId}" };
            if (!string.Equals(cache.displayName ?? string.Empty, server.displayName ?? string.Empty, StringComparison.Ordinal))
                return new OutfitWorkbenchCacheStatus { state = "MISMATCH", detail = "display name differs" };
            if (cache.maxDurability != server.maxDurability)
                return new OutfitWorkbenchCacheStatus { state = "MISMATCH", detail = $"durability {server.maxDurability}/{cache.maxDurability}" };
            if (Math.Abs(cache.unitWeight - server.weight) > 0.0001f)
                return new OutfitWorkbenchCacheStatus { state = "MISMATCH", detail = $"weight {server.weight:0.###}/{cache.unitWeight:0.###}" };

            if (!string.IsNullOrWhiteSpace(server.equipmentSlotId))
            {
                bool slotMatched = false;
                ushort[] dataIds = cache.allowedSlotDataIds ?? Array.Empty<ushort>();
                for (int i = 0; i < dataIds.Length; ++i)
                    if (slotNames.TryGetValue(dataIds[i], out string slot) &&
                        string.Equals(slot, server.equipmentSlotId, StringComparison.Ordinal)) slotMatched = true;
                if (!slotMatched)
                    return new OutfitWorkbenchCacheStatus { state = "MISMATCH", detail = "equipment slot differs" };
            }

            return new OutfitWorkbenchCacheStatus { state = "MATCHED", detail = "cache-visible fields agree" };
        }

        private static bool UpdateEquipmentOnly(string definitionId, OutfitWorkbenchEquipmentEdit details)
        {
            OutfitWorkbenchServerItem item = LoadServerItems().FirstOrDefault(x =>
                string.Equals(x.definitionId, definitionId, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return false;
            var request = new OutfitAuthoringSaveRequest { itemName = item.displayName, equipmentSlotId = item.equipmentSlotId };
            return PatchSavedArmorItem(item.definitionId, item.presentationId, request, details, out _);
        }

        private static string SetDefaultBody(ushort[] slotIds, ushort[] optionIds)
        {
            if (slotIds == null || optionIds == null || slotIds.Length == 0 || slotIds.Length != optionIds.Length)
                return "ERROR: No resolved mannequin mesh selections were supplied.";

            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(
                CharacterVisualCatalogBuilder.OutputAssetPath);
            if (profile == null)
                return "ERROR: CharacterVisualProfile.asset was not found.";

            SerializedObject so = new SerializedObject(profile);
            SerializedProperty slots = so.FindProperty("slots");
            if (slots == null || !slots.isArray)
                return "ERROR: CharacterVisualProfile slots could not be serialized.";

            int changed = 0;
            Undo.RecordObject(profile, "Set Outfit Builder Unequipped Body Defaults");
            for (int i = 0; i < slotIds.Length; ++i)
            {
                ushort slotId = slotIds[i];
                ushort optionId = optionIds[i];
                if (slotId == 0 || optionId == 0)
                    continue;

                for (int s = 0; s < slots.arraySize; ++s)
                {
                    SerializedProperty entry = slots.GetArrayElementAtIndex(s);
                    SerializedProperty id = entry.FindPropertyRelative("slotId");
                    SerializedProperty def = entry.FindPropertyRelative("defaultOptionId");
                    if (id != null && def != null && id.intValue == slotId)
                    {
                        if (def.intValue != optionId)
                        {
                            def.intValue = optionId;
                            changed++;
                        }
                        break;
                    }
                }
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            CharacterVisualProfileRegistry.ResetForTestsOrReload();
            return changed > 0
                ? $"Saved {changed} canonical unequipped/default mesh slot(s) to CharacterVisualProfile."
                : "Unequipped/default mesh slots already match the current mannequin.";
        }

        private static bool PatchSavedArmorItem(
            string definitionId,
            ushort presentationId,
            OutfitAuthoringSaveRequest request,
            OutfitWorkbenchEquipmentEdit details,
            out string error)
        {
            error = string.Empty;
            if (!OutfitAuthoringSaveBridge.TryGetReviewPaths(out OutfitAuthoringSaveBridge.ReviewPaths paths))
            {
                error = "Server Content Root is unavailable.";
                return false;
            }

            string json = File.ReadAllText(paths.armorPath);
            if (!TryFindItemObject(json, definitionId, out int start, out int endExclusive, out string existing))
            {
                error = $"Armor.json does not contain '{definitionId}'.";
                return false;
            }

            string displayName = DefaultIfEmpty(MatchString(DisplayNameRegex, existing), request?.itemName ?? definitionId);
            string slotId = !string.IsNullOrWhiteSpace(request?.equipmentSlotId)
                ? request.equipmentSlotId
                : MatchString(SlotRegex, existing);
            int dataId = MatchInt(DataIdRegex, existing, 0);

            string replacement = BuildArmorJson(definitionId, displayName, presentationId, dataId, slotId,
                details ?? new OutfitWorkbenchEquipmentEdit());
            string indent = LineIndentAt(json, start);
            string updated = json.Substring(0, start) + IndentBlock(replacement, indent) + json.Substring(endExclusive);

            WriteSafely(paths.armorPath, updated);
            return true;
        }

        private static string BuildArmorJson(
            string definitionId, string displayName, ushort presentationId, int dataId,
            string equipmentSlotId, OutfitWorkbenchEquipmentEdit details)
        {
            string[] tags = (details.tags ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            OutfitWorkbenchStatModifier[] modifiers = (details.statModifiers ?? Array.Empty<OutfitWorkbenchStatModifier>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.statId)).ToArray();

            var b = new StringBuilder(768);
            b.AppendLine("{");
            if (dataId > 0) b.Append("  \\\"dataId\\\": ").Append(dataId).AppendLine(",");
            b.Append("  \\\"definitionId\\\": \\\"").Append(Escape(definitionId)).AppendLine("\\\",");
            b.Append("  \\\"displayName\\\": \\\"").Append(Escape(displayName)).AppendLine("\\\",");
            b.AppendLine("  \\\"kind\\\": \\\"Equipment\\\",");
            b.AppendLine("  \\\"subtype\\\": \\\"Armor\\\",");
            b.Append("  \\\"presentationId\\\": ").Append(presentationId).AppendLine(",");
            b.Append("  \\\"maxStack\\\": ").Append(Math.Max(1, details.maxStack)).AppendLine(",");
            b.Append("  \\\"weight\\\": ").Append(Math.Max(0f, details.weight).ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(",");
            b.Append("  \\\"maxDurability\\\": ").Append(Math.Max(0, details.maxDurability)).AppendLine(",");
            b.AppendLine("  \\\"tags\\\": [");
            for (int i = 0; i < tags.Length; ++i)
            {
                b.Append("    \\\"").Append(Escape(tags[i])).Append("\\\"");
                if (i + 1 < tags.Length) b.Append(",");
                b.AppendLine();
            }
            b.AppendLine("  ],");
            b.AppendLine("  \\\"allowedEquipmentSlots\\\": [");
            if (!string.IsNullOrWhiteSpace(equipmentSlotId))
                b.Append("    \\\"").Append(Escape(equipmentSlotId)).AppendLine("\\\"");
            b.AppendLine("  ],");
            b.AppendLine("  \\\"statModifiers\\\": [");
            for (int i = 0; i < modifiers.Length; ++i)
            {
                OutfitWorkbenchStatModifier mod = modifiers[i];
                b.AppendLine("    {");
                b.Append("      \\\"statId\\\": \\\"").Append(Escape(mod.statId)).AppendLine("\\\",");
                b.Append("      \\\"additive\\\": ").Append(mod.additive.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine(",");
                b.Append("      \\\"multiplier\\\": ").Append(mod.multiplier.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
                b.Append("    }");
                if (i + 1 < modifiers.Length) b.Append(",");
                b.AppendLine();
            }
            b.AppendLine("  ]");
            b.Append("}");
            return b.ToString().Replace("\\\\\"", "\\\"");
        }

        private static string[] ParseTags(string item)
        {
            Match match = TagsArrayRegex.Match(item ?? string.Empty);
            if (!match.Success) return Array.Empty<string>();
            return QuotedValueRegex.Matches(match.Groups[1].Value).Cast<Match>()
                .Select(x => x.Groups[1].Value).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        }

        private static OutfitWorkbenchStatModifier[] ParseModifiers(string item)
        {
            if (!TryFindArray(item, "statModifiers", out int open, out int close))
                return Array.Empty<OutfitWorkbenchStatModifier>();
            string body = item.Substring(open + 1, close - open - 1);
            List<string> objects = ExtractTopLevelObjects(body);
            var result = new List<OutfitWorkbenchStatModifier>();
            for (int i = 0; i < objects.Count; ++i)
            {
                string stat = MatchString(StatIdRegex, objects[i]);
                if (string.IsNullOrWhiteSpace(stat)) continue;
                result.Add(new OutfitWorkbenchStatModifier
                {
                    statId = stat,
                    additive = MatchNamedFloat(objects[i], "additive", 0f),
                    multiplier = MatchNamedFloat(objects[i], "multiplier", 1f),
                });
            }
            return result.ToArray();
        }

        private static List<string> ExtractItems(string json)
        {
            if (!TryFindArray(json, "items", out int open, out int close)) return new List<string>();
            return ExtractTopLevelObjects(json.Substring(open + 1, close - open - 1));
        }

        private static bool TryFindItemObject(string json, string definitionId, out int start, out int endExclusive, out string objectJson)
        {
            start = -1; endExclusive = -1; objectJson = string.Empty;
            if (!TryFindArray(json, "items", out int open, out int close)) return false;
            bool inString = false, escaped = false; int depth = 0, objectStart = -1;
            for (int i = open + 1; i < close; ++i)
            {
                char c = json[i];
                if (inString) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '\"') inString = false; continue; }
                if (c == '\"') { inString = true; continue; }
                if (c == '{') { if (depth == 0) objectStart = i; depth++; }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        int end = i + 1; string candidate = json.Substring(objectStart, end - objectStart);
                        if (string.Equals(MatchString(DefinitionIdRegex, candidate), definitionId, StringComparison.OrdinalIgnoreCase))
                        { start = objectStart; endExclusive = end; objectJson = candidate; return true; }
                        objectStart = -1;
                    }
                }
            }
            return false;
        }

        private static bool TryFindArray(string json, string property, out int open, out int close)
        {
            open = close = -1;
            Match match = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(property) + "\\\"\\s*:\\s*\\[", RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            open = json.IndexOf('[', match.Index); if (open < 0) return false;
            bool inString = false, escaped = false; int depth = 0;
            for (int i = open; i < json.Length; ++i)
            {
                char c = json[i];
                if (inString) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '\"') inString = false; continue; }
                if (c == '\"') { inString = true; continue; }
                if (c == '[') depth++; else if (c == ']') { depth--; if (depth == 0) { close = i; return true; } }
            }
            return false;
        }

        private static List<string> ExtractTopLevelObjects(string input)
        {
            var result = new List<string>(); bool inString = false, escaped = false; int depth = 0, start = -1;
            for (int i = 0; i < (input ?? string.Empty).Length; ++i)
            {
                char c = input[i];
                if (inString) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '\"') inString = false; continue; }
                if (c == '\"') { inString = true; continue; }
                if (c == '{') { if (depth == 0) start = i; depth++; }
                else if (c == '}') { depth--; if (depth == 0 && start >= 0) { result.Add(input.Substring(start, i - start + 1)); start = -1; } }
            }
            return result;
        }

        private static void CollectMatches(string json, Regex regex, HashSet<string> output)
        {
            MatchCollection matches = regex.Matches(json ?? string.Empty);
            for (int i = 0; i < matches.Count; ++i)
                if (!string.IsNullOrWhiteSpace(matches[i].Groups[1].Value)) output.Add(matches[i].Groups[1].Value);
        }

        private static string MatchString(Regex regex, string input)
        { Match m = regex.Match(input ?? string.Empty); return m.Success ? m.Groups[1].Value : string.Empty; }

        private static int MatchInt(Regex regex, string input, int fallback)
        { Match m = regex.Match(input ?? string.Empty); return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : fallback; }

        private static float MatchFloat(Regex regex, string input, float fallback)
        { Match m = regex.Match(input ?? string.Empty); return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback; }

        private static float MatchNamedFloat(string input, string name, float fallback)
        { return MatchFloat(new Regex("\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)"), input, fallback); }

        private static string DefaultIfEmpty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        private static int MathfClamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        private static string LineIndentAt(string text, int index)
        {
            int start = text.LastIndexOf('\n', Math.Max(0, index - 1)); start = start < 0 ? 0 : start + 1;
            int i = start; while (i < index && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(start, i - start);
        }

        private static string IndentBlock(string value, string indent)
        { return string.Join(Environment.NewLine, (value ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(x => indent + x)); }

        private static void WriteSafely(string path, string contents)
        {
            string temp = path + ".outfit-workbench.tmp";
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
#endif
