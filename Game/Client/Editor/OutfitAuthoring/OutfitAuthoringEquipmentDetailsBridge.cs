#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Game.Client.OutfitAuthoring;
using Player.Networking;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    [InitializeOnLoad]
    internal static class OutfitAuthoringEquipmentDetailsBridge
    {
        private static readonly Regex DefinitionIdRegex =
            new Regex("\"definitionId\"\\s*:\\s*\"([^\"]+)\"",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DisplayNameRegex =
            new Regex("\"displayName\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex PresentationIdRegex =
            new Regex("\"presentationId\"\\s*:\\s*(\\d+)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex MaxStackRegex =
            new Regex("\"maxStack\"\\s*:\\s*(-?\\d+)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex WeightRegex =
            new Regex("\"weight\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DurabilityRegex =
            new Regex("\"maxDurability\"\\s*:\\s*(-?\\d+)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SlotRegex =
            new Regex("\"allowedEquipmentSlots\"\\s*:\\s*\\[\\s*\"([^\"]+)\"",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex TagRegex =
            new Regex("\"tags\"\\s*:\\s*\\[(.*?)\\]",
                RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex StatIdRegex =
            new Regex("\"statId\"\\s*:\\s*\"([^\"]+)\"",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex MaximumStatIdRegex =
            new Regex("\"maximumStatId\"\\s*:\\s*\"([^\"]+)\"",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DefenseStatIdRegex =
            new Regex("\"defenseStatId\"\\s*:\\s*\"([^\"]+)\"",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex QuotedValueRegex =
            new Regex("\"([^\"]+)\"",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static Func<OutfitAuthoringSaveRequest, OutfitAuthoringSaveResult> _baseSave;

        static OutfitAuthoringEquipmentDetailsBridge()
        {
            OutfitAuthoringEquipmentDetailsPanel.EditorOptionsProvider =
                LoadOptions;
            OutfitAuthoringEquipmentDetailsPanel.EditorExistingItemProvider =
                LoadExisting;
            OutfitAuthoringEquipmentDetailsPanel.EditorCacheStatusProvider =
                CheckCache;

            EditorApplication.delayCall += InstallSaveWrapper;
        }

        private static void InstallSaveWrapper()
        {
            Func<OutfitAuthoringSaveRequest, OutfitAuthoringSaveResult> current =
                OutfitAuthoringController.EditorSaveHandler;

            if (current == null)
            {
                EditorApplication.delayCall += InstallSaveWrapper;
                return;
            }

            if (current == SaveWithEquipmentDetails)
                return;

            _baseSave = current;
            OutfitAuthoringController.EditorSaveHandler =
                SaveWithEquipmentDetails;
        }

        private static OutfitAuthoringSaveResult SaveWithEquipmentDetails(
            OutfitAuthoringSaveRequest request)
        {
            if (_baseSave == null)
                return new OutfitAuthoringSaveResult(
                    false,
                    "Base OutfitAuthoring save handler is unavailable.");

            OutfitAuthoringEquipmentDetailsPanel panel =
                UnityEngine.Object.FindObjectsOfType<OutfitAuthoringEquipmentDetailsPanel>(true)
                    .FirstOrDefault();

            OutfitAuthoringEquipmentDetailsData details =
                panel != null
                    ? panel.Capture()
                    : new OutfitAuthoringEquipmentDetailsData();

            OutfitAuthoringSaveResult result = _baseSave(request);
            if (!result.success)
                return result;

            try
            {
                PatchSavedServerItem(
                    result.definitionId,
                    result.presentationId,
                    request,
                    details);

                panel?.NotifySaved(
                    result.definitionId,
                    request?.equipmentSlotId ?? string.Empty);

                return new OutfitAuthoringSaveResult(
                    true,
                    result.message +
                    " Equipment details written using the current flat ItemDefinition schema.",
                    result.definitionId,
                    result.presentationId,
                    result.updatedExisting);
            }
            catch (Exception ex)
            {
                return new OutfitAuthoringSaveResult(
                    false,
                    result.message +
                    " Equipment detail write failed after base save: " +
                    ex.Message,
                    result.definitionId,
                    result.presentationId,
                    result.updatedExisting);
            }
        }

        private static OutfitAuthoringEquipmentAuthoringOptions LoadOptions()
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            var stats = new HashSet<string>(StringComparer.Ordinal)
            {
                "Health.Max",
                "Mana.Max",
                "Stamina.Max",
                "Armor",
                "AttackPower",
            };

            if (!OutfitAuthoringSaveBridge.TryGetReviewPaths(
                    out OutfitAuthoringSaveBridge.ReviewPaths paths))
            {
                return new OutfitAuthoringEquipmentAuthoringOptions
                {
                    tags = tags.ToArray(),
                    statIds = stats.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                };
            }

            string[] files = Directory.Exists(paths.contentRoot)
                ? Directory.GetFiles(
                    paths.contentRoot,
                    "*.json",
                    SearchOption.AllDirectories)
                : Array.Empty<string>();

            for (int i = 0; i < files.Length; ++i)
            {
                string json;
                try { json = File.ReadAllText(files[i]); }
                catch { continue; }

                CollectArrayStrings(json, TagRegex, tags);
                CollectMatches(json, StatIdRegex, stats);
                CollectMatches(json, MaximumStatIdRegex, stats);
                CollectMatches(json, DefenseStatIdRegex, stats);
            }

            return new OutfitAuthoringEquipmentAuthoringOptions
            {
                tags = tags
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray(),
                statIds = stats
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        private static OutfitAuthoringEquipmentDetailsData LoadExisting(
            string definitionId)
        {
            if (string.IsNullOrWhiteSpace(definitionId) ||
                !OutfitAuthoringSaveBridge.TryGetReviewPaths(
                    out OutfitAuthoringSaveBridge.ReviewPaths paths))
            {
                return null;
            }

            string armor = File.ReadAllText(paths.armorPath);
            if (!TryFindItemObject(
                    armor,
                    definitionId,
                    out _,
                    out _,
                    out string item))
            {
                return null;
            }

            var result = new OutfitAuthoringEquipmentDetailsData
            {
                definitionId = definitionId,
                equipmentSlotId = MatchString(SlotRegex, item),
                maxStack = Math.Max(1, MatchInt(MaxStackRegex, item, 1)),
                weight = Math.Max(0f, MatchFloat(WeightRegex, item, 0f)),
                maxDurability = Math.Max(0, MatchInt(DurabilityRegex, item, 0)),
                tags = ParseTags(item),
                statModifiers = ParseModifiers(item),
            };

            return result;
        }

        private static OutfitAuthoringEquipmentCacheStatus CheckCache(
            string definitionId)
        {
            if (string.IsNullOrWhiteSpace(definitionId))
                return new OutfitAuthoringEquipmentCacheStatus
                {
                    label = "NOT APPLICABLE",
                };

            OutfitAuthoringEquipmentDetailsData server =
                LoadExisting(definitionId);

            if (server == null)
            {
                return new OutfitAuthoringEquipmentCacheStatus
                {
                    label = "SERVER MISSING",
                };
            }

            GameplayItemReferenceWire cache;
            long cacheRevision = PlayerGameplaySettingsRuntime.Revision;

            if (cacheRevision > 0)
            {
                if (!PlayerGameplaySettingsRuntime.TryGetItem(
                        definitionId,
                        out cache))
                {
                    return new OutfitAuthoringEquipmentCacheStatus
                    {
                        label = "CACHE MISSING",
                    };
                }
            }
            else
            {
                if (!PlayerEntityGameManager.TryReadLatestGameplaySettingsCacheForEditor(
                        out GameplaySettingsSnapshotMessage snapshot,
                        out _,
                        out string detail))
                {
                    return new OutfitAuthoringEquipmentCacheStatus
                    {
                        label = "CACHE NOT LOADED",
                        detail = detail,
                    };
                }

                cacheRevision = snapshot.revision;
                cache = default;
                GameplayItemReferenceWire[] items =
                    snapshot.items ?? Array.Empty<GameplayItemReferenceWire>();

                bool found = false;
                for (int i = 0; i < items.Length; ++i)
                {
                    if (string.Equals(
                        items[i].definitionId,
                        definitionId,
                        StringComparison.Ordinal))
                    {
                        cache = items[i];
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return new OutfitAuthoringEquipmentCacheStatus
                    {
                        label = "CACHE MISSING",
                    };
                }
            }

            if (OutfitAuthoringSaveBridge.TryGetReviewPaths(
                    out OutfitAuthoringSaveBridge.ReviewPaths paths) &&
                paths.revision > 0 &&
                cacheRevision > 0 &&
                cacheRevision < paths.revision)
            {
                return new OutfitAuthoringEquipmentCacheStatus
                {
                    label = "STALE",
                    detail = $"cache {cacheRevision} / server {paths.revision}",
                };
            }

            Match presentationMatch = PresentationIdRegex.Match(
                File.ReadAllText(
                    OutfitAuthoringSaveBridge.TryGetReviewPaths(
                        out OutfitAuthoringSaveBridge.ReviewPaths p)
                        ? p.armorPath
                        : string.Empty));

            // Compare only values actually present in the client gameplay cache.
            if (server.maxDurability != cache.maxDurability)
            {
                return new OutfitAuthoringEquipmentCacheStatus
                {
                    label = "MISMATCH",
                    detail = $"durability server/cache {server.maxDurability}/{cache.maxDurability}",
                };
            }

            if (Math.Abs(server.weight - cache.unitWeight) > 0.0001f)
            {
                return new OutfitAuthoringEquipmentCacheStatus
                {
                    label = "MISMATCH",
                    detail = $"weight server/cache {server.weight:0.###}/{cache.unitWeight:0.###}",
                };
            }

            return new OutfitAuthoringEquipmentCacheStatus
            {
                label = "MATCHED",
                detail = "cache-visible equipment fields agree",
            };
        }

        private static void PatchSavedServerItem(
            string definitionId,
            ushort presentationId,
            OutfitAuthoringSaveRequest request,
            OutfitAuthoringEquipmentDetailsData details)
        {
            if (string.IsNullOrWhiteSpace(definitionId))
                throw new InvalidOperationException(
                    "Saved item has no definitionId.");

            if (!OutfitAuthoringSaveBridge.TryGetReviewPaths(
                    out OutfitAuthoringSaveBridge.ReviewPaths paths))
            {
                throw new InvalidOperationException(
                    "Server Content Root is unavailable.");
            }

            string armor = File.ReadAllText(paths.armorPath);
            if (!TryFindItemObject(
                    armor,
                    definitionId,
                    out int start,
                    out int endExclusive,
                    out string existing))
            {
                throw new InvalidOperationException(
                    $"Saved server item '{definitionId}' could not be found.");
            }

            string displayName =
                MatchString(DisplayNameRegex, existing);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = request?.itemName ?? definitionId;

            string slotId =
                request?.equipmentSlotId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(slotId))
                slotId = MatchString(SlotRegex, existing);

            string replacement = BuildCanonicalArmorItem(
                definitionId,
                displayName,
                presentationId,
                slotId,
                details ?? new OutfitAuthoringEquipmentDetailsData());

            string indent = LineIndentAt(armor, start);
            replacement = IndentBlock(replacement, indent);

            string updated =
                armor.Substring(0, start) +
                replacement +
                armor.Substring(endExclusive);

            WriteFileSafely(paths.armorPath, updated);
        }

        private static string BuildCanonicalArmorItem(
            string definitionId,
            string displayName,
            ushort presentationId,
            string equipmentSlotId,
            OutfitAuthoringEquipmentDetailsData details)
        {
            int maxStack = Math.Max(1, details.maxStack);
            float weight = Math.Max(0f, details.weight);
            int durability = Math.Max(0, details.maxDurability);

            string[] tags =
                (details.tags ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            OutfitAuthoringStatModifierValue[] mods =
                (details.statModifiers ??
                    Array.Empty<OutfitAuthoringStatModifierValue>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.statId))
                .ToArray();

            var builder = new StringBuilder(768);
            builder.AppendLine("{");
            builder.Append("  \"definitionId\": \"")
                .Append(JsonEscape(definitionId))
                .AppendLine("\",");
            builder.Append("  \"displayName\": \"")
                .Append(JsonEscape(displayName))
                .AppendLine("\",");
            builder.AppendLine("  \"kind\": \"Equipment\",");
            builder.AppendLine("  \"subtype\": \"Armor\",");
            builder.Append("  \"presentationId\": ")
                .Append(presentationId.ToString(CultureInfo.InvariantCulture))
                .AppendLine(",");
            builder.Append("  \"maxStack\": ")
                .Append(maxStack.ToString(CultureInfo.InvariantCulture))
                .AppendLine(",");
            builder.Append("  \"weight\": ")
                .Append(weight.ToString("0.###", CultureInfo.InvariantCulture))
                .AppendLine(",");
            builder.Append("  \"maxDurability\": ")
                .Append(durability.ToString(CultureInfo.InvariantCulture))
                .AppendLine(",");

            builder.AppendLine("  \"tags\": [");
            for (int i = 0; i < tags.Length; ++i)
            {
                builder.Append("    \"")
                    .Append(JsonEscape(tags[i]))
                    .Append("\"");
                if (i + 1 < tags.Length)
                    builder.Append(",");
                builder.AppendLine();
            }
            builder.AppendLine("  ],");

            builder.AppendLine("  \"allowedEquipmentSlots\": [");
            if (!string.IsNullOrWhiteSpace(equipmentSlotId))
            {
                builder.Append("    \"")
                    .Append(JsonEscape(equipmentSlotId))
                    .AppendLine("\"");
            }
            builder.AppendLine("  ],");

            builder.AppendLine("  \"statModifiers\": [");
            for (int i = 0; i < mods.Length; ++i)
            {
                OutfitAuthoringStatModifierValue mod = mods[i];
                builder.AppendLine("    {");
                builder.Append("      \"statId\": \"")
                    .Append(JsonEscape(mod.statId))
                    .AppendLine("\",");
                builder.Append("      \"additive\": ")
                    .Append(mod.additive.ToString("0.###", CultureInfo.InvariantCulture))
                    .AppendLine(",");
                builder.Append("      \"multiplier\": ")
                    .Append(mod.multiplier.ToString("0.###", CultureInfo.InvariantCulture))
                    .AppendLine();
                builder.Append("    }");
                if (i + 1 < mods.Length)
                    builder.Append(",");
                builder.AppendLine();
            }
            builder.AppendLine("  ]");
            builder.Append("}");

            return builder.ToString();
        }

        private static string[] ParseTags(string item)
        {
            Match match = TagRegex.Match(item ?? string.Empty);
            if (!match.Success)
                return Array.Empty<string>();

            var values = new List<string>();
            MatchCollection strings =
                QuotedValueRegex.Matches(match.Groups[1].Value);

            for (int i = 0; i < strings.Count; ++i)
            {
                string value = strings[i].Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(value) &&
                    !values.Contains(value))
                {
                    values.Add(value);
                }
            }

            return values.ToArray();
        }

        private static OutfitAuthoringStatModifierValue[] ParseModifiers(
            string item)
        {
            int propertyIndex =
                (item ?? string.Empty).IndexOf(
                    "\"statModifiers\"",
                    StringComparison.Ordinal);

            if (propertyIndex < 0)
                return Array.Empty<OutfitAuthoringStatModifierValue>();

            int open =
                item.IndexOf('[', propertyIndex);
            if (open < 0)
                return Array.Empty<OutfitAuthoringStatModifierValue>();

            int close = FindMatchingArrayClose(item, open);
            if (close < 0)
                return Array.Empty<OutfitAuthoringStatModifierValue>();

            string array =
                item.Substring(open + 1, close - open - 1);

            List<string> objects = ExtractTopLevelObjects(array);
            var values =
                new List<OutfitAuthoringStatModifierValue>(objects.Count);

            for (int i = 0; i < objects.Count; ++i)
            {
                string statId = MatchString(StatIdRegex, objects[i]);
                if (string.IsNullOrWhiteSpace(statId))
                    continue;

                values.Add(new OutfitAuthoringStatModifierValue
                {
                    statId = statId,
                    additive = MatchNamedFloat(objects[i], "additive", 0f),
                    multiplier = MatchNamedFloat(objects[i], "multiplier", 1f),
                });
            }

            return values.ToArray();
        }

        private static void CollectArrayStrings(
            string json,
            Regex arrayRegex,
            HashSet<string> output)
        {
            MatchCollection arrays = arrayRegex.Matches(json ?? string.Empty);
            for (int i = 0; i < arrays.Count; ++i)
            {
                MatchCollection strings =
                    QuotedValueRegex.Matches(arrays[i].Groups[1].Value);

                for (int s = 0; s < strings.Count; ++s)
                {
                    string value = strings[s].Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(value))
                        output.Add(value);
                }
            }
        }

        private static void CollectMatches(
            string json,
            Regex regex,
            HashSet<string> output)
        {
            MatchCollection values = regex.Matches(json ?? string.Empty);
            for (int i = 0; i < values.Count; ++i)
            {
                string value = values[i].Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(value))
                    output.Add(value);
            }
        }

        private static string MatchString(Regex regex, string input)
        {
            Match match = regex.Match(input ?? string.Empty);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static int MatchInt(
            Regex regex,
            string input,
            int fallback)
        {
            Match match = regex.Match(input ?? string.Empty);
            return match.Success &&
                   int.TryParse(
                       match.Groups[1].Value,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out int value)
                ? value
                : fallback;
        }

        private static float MatchFloat(
            Regex regex,
            string input,
            float fallback)
        {
            Match match = regex.Match(input ?? string.Empty);
            return match.Success &&
                   float.TryParse(
                       match.Groups[1].Value,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out float value)
                ? value
                : fallback;
        }

        private static float MatchNamedFloat(
            string input,
            string name,
            float fallback)
        {
            var regex = new Regex(
                "\"" + Regex.Escape(name) +
                "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)",
                RegexOptions.CultureInvariant);

            return MatchFloat(regex, input, fallback);
        }

        private static bool TryFindItemObject(
            string json,
            string definitionId,
            out int start,
            out int endExclusive,
            out string objectJson)
        {
            start = -1;
            endExclusive = -1;
            objectJson = string.Empty;

            Match items =
                Regex.Match(
                    json ?? string.Empty,
                    "\"items\"\\s*:\\s*\\[",
                    RegexOptions.CultureInvariant);

            if (!items.Success)
                return false;

            int arrayStart = json.IndexOf('[', items.Index);
            if (arrayStart < 0)
                return false;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            int objectStart = -1;

            for (int i = arrayStart + 1; i < json.Length; ++i)
            {
                char c = json[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    if (depth == 0)
                        objectStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        int end = i + 1;
                        string candidate =
                            json.Substring(
                                objectStart,
                                end - objectStart);

                        if (string.Equals(
                            MatchString(DefinitionIdRegex, candidate),
                            definitionId,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            start = objectStart;
                            endExclusive = end;
                            objectJson = candidate;
                            return true;
                        }

                        objectStart = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }

            return false;
        }

        private static int FindMatchingArrayClose(
            string input,
            int openIndex)
        {
            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = openIndex; i < input.Length; ++i)
            {
                char c = input[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '[')
                    depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static List<string> ExtractTopLevelObjects(string input)
        {
            var result = new List<string>();
            bool inString = false;
            bool escaped = false;
            int depth = 0;
            int start = -1;

            for (int i = 0; i < (input ?? string.Empty).Length; ++i)
            {
                char c = input[i];

                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    if (depth == 0)
                        start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(
                            input.Substring(
                                start,
                                i - start + 1));
                        start = -1;
                    }
                }
            }

            return result;
        }

        private static string LineIndentAt(string text, int index)
        {
            int lineStart =
                text.LastIndexOf('\n', Math.Max(0, index - 1));

            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            int i = lineStart;
            while (i < text.Length &&
                   i < index &&
                   (text[i] == ' ' || text[i] == '\t'))
            {
                i++;
            }

            return text.Substring(
                lineStart,
                i - lineStart);
        }

        private static string IndentBlock(
            string value,
            string indent)
        {
            string[] lines =
                (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Split('\n');

            return string.Join(
                Environment.NewLine,
                lines.Select(line => indent + line));
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static void WriteFileSafely(
            string path,
            string contents)
        {
            string temp = path + ".equipment-details.tmp";
            File.WriteAllText(
                temp,
                contents,
                new UTF8Encoding(false));

            if (File.Exists(path))
                File.Delete(path);

            File.Move(temp, path);
        }
    }
}
#endif
