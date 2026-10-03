#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Client.OutfitAuthoring;
using Game.Client.Presentation.Characters;
using Game.Shared.Characters;
using UnityEditor;
using UnityEngine;

namespace Game.Client.Editor
{
    public sealed class OutfitAuthoringIntegrityOverlay : EditorWindow
    {
        private enum AuditState
        {
            Matched,
            ServerOnly,
            ClientOnly,
            IdMismatch,
            DuplicateServerDefinition,
            DuplicateServerPresentation,
            DuplicateClientDefinition,
            DuplicateClientPresentation,
            BrokenPresentation,
        }

        private sealed class ServerItem
        {
            public string definitionId;
            public string displayName;
            public ushort presentationId;
            public string equipmentSlotId;
        }

        private sealed class Row
        {
            public string key;
            public ServerItem server;
            public CharacterWearableSetDefinition client;
            public AuditState state;
            public string detail;
        }

        private static readonly Regex DefinitionIdRegex =
            new Regex("\"definitionId\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DisplayNameRegex =
            new Regex("\"displayName\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PresentationIdRegex =
            new Regex("\"presentationId\"\\s*:\\s*(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex EquipmentSlotsRegex =
            new Regex("\"allowedEquipmentSlots\"\\s*:\\s*\\[\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly List<Row> _rows = new List<Row>();
        private Vector2 _scroll;
        private int _selectedIndex = -1;
        private bool _problemsOnly;
        private string _filter = string.Empty;
        private string _summary = "Not scanned.";
        private string _sourceStatus = string.Empty;

        [MenuItem("MMO Tools/Characters/Outfit Builder/Open Integrity Overlay")]
        public static void Open()
        {
            GetWindow<OutfitAuthoringIntegrityOverlay>("Outfit Integrity");
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("OUTFIT / ITEM INTEGRITY", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Server authoritative Armor catalog ↔ client wearable/presentation catalog",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh / Validate All", GUILayout.Height(26)))
                    Refresh();

                _problemsOnly = GUILayout.Toggle(_problemsOnly, "Problems Only", "Button", GUILayout.Width(110));
            }

            _filter = EditorGUILayout.TextField("Filter", _filter ?? string.Empty);
            EditorGUILayout.HelpBox(_summary + "\n" + _sourceStatus, MessageType.Info);

            DrawSelectedInspector();

            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            IReadOnlyList<Row> visible = VisibleRows();
            for (int i = 0; i < visible.Count; ++i)
            {
                Row row = visible[i];
                bool selected = _selectedIndex >= 0 &&
                                _selectedIndex < _rows.Count &&
                                ReferenceEquals(_rows[_selectedIndex], row);

                using (new EditorGUILayout.VerticalScope(selected ? EditorStyles.helpBox : GUI.skin.box))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(StateLabel(row.state), GUILayout.Width(150)))
                            Select(row);

                        string name = row.server?.displayName;
                        if (string.IsNullOrWhiteSpace(name))
                            name = row.client?.displayName;
                        if (string.IsNullOrWhiteSpace(name))
                            name = row.key;

                        if (GUILayout.Button(name ?? "(unnamed)", EditorStyles.label))
                            Select(row);
                    }

                    string server = row.server == null
                        ? "Server: MISSING"
                        : $"Server: {row.server.definitionId} | P{row.server.presentationId} | Slot {row.server.equipmentSlotId}";
                    string client = row.client == null
                        ? "Client Cache/Presentation: MISSING"
                        : $"Client Cache/Presentation: {row.client.definitionId} | P{row.client.presentationId} | {row.client.region}";

                    EditorGUILayout.LabelField(server, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(client, EditorStyles.miniLabel);
                    if (!string.IsNullOrWhiteSpace(row.detail))
                        EditorGUILayout.LabelField(row.detail, EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSelectedInspector()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _rows.Count)
                return;

            Row row = _rows[_selectedIndex];
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Selected Item", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Server", row.server != null ? "✓ Present" : "✗ Missing");
                EditorGUILayout.LabelField("Client Cache / Presentation", row.client != null ? "✓ Present" : "✗ Missing");

                if (row.server != null && row.client != null)
                {
                    EditorGUILayout.LabelField(
                        "Definition ID",
                        string.Equals(row.server.definitionId, row.client.definitionId, StringComparison.OrdinalIgnoreCase)
                            ? $"✓ {row.server.definitionId}"
                            : $"✗ {row.server.definitionId} / {row.client.definitionId}");

                    EditorGUILayout.LabelField(
                        "Presentation ID",
                        row.server.presentationId == row.client.presentationId
                            ? $"✓ {row.server.presentationId}"
                            : $"✗ {row.server.presentationId} / {row.client.presentationId}");

                    EditorGUILayout.LabelField("Server Equipment Slot", row.server.equipmentSlotId ?? "(none)");
                    EditorGUILayout.LabelField("Client Visual Region", row.client.region.ToString());
                    EditorGUILayout.LabelField(
                        "Meshes",
                        HasBrokenParts(row.client, CharacterVisualProfileRegistry.Default, out string broken)
                            ? "✗ " + broken
                            : "✓ Valid");
                }

                using (new EditorGUI.DisabledScope(row.client == null || !EditorApplication.isPlaying))
                {
                    if (GUILayout.Button("Load Client Wearable On Mannequin", GUILayout.Height(28)))
                        LoadSelectedOnMannequin(row);
                }

                if (!EditorApplication.isPlaying && row.client != null)
                    EditorGUILayout.LabelField("Enter Play Mode to load this item onto the mannequin.", EditorStyles.miniLabel);
            }
        }

        private void LoadSelectedOnMannequin(Row row)
        {
            if (row?.client == null)
                return;

            OutfitAuthoringController controller = FindObjectOfType<OutfitAuthoringController>();
            if (controller == null)
            {
                ShowNotification(new GUIContent("Outfit Builder controller not found in the active Play Mode scene."));
                return;
            }

            string slotId = row.server?.equipmentSlotId ?? string.Empty;
            ushort slotPresentationId = 0;

            OutfitAuthoringEquipmentSlotOption[] slots = OutfitAuthoringController.EditorEquipmentSlotProvider != null
                ? OutfitAuthoringController.EditorEquipmentSlotProvider.Invoke()
                : Array.Empty<OutfitAuthoringEquipmentSlotOption>();

            for (int i = 0; i < slots.Length; ++i)
            {
                if (slots[i] != null && string.Equals(slots[i].slotId, slotId, StringComparison.Ordinal))
                {
                    slotPresentationId = slots[i].presentationSlotId;
                    break;
                }
            }

            if (!controller.LoadExistingWearable(row.client, slotId, slotPresentationId))
                ShowNotification(new GUIContent("Could not load the selected wearable."));
        }

        private void Select(Row row)
        {
            _selectedIndex = _rows.IndexOf(row);
            Repaint();
        }

        private IReadOnlyList<Row> VisibleRows()
        {
            IEnumerable<Row> query = _rows;
            if (_problemsOnly)
                query = query.Where(r => r.state != AuditState.Matched);

            if (!string.IsNullOrWhiteSpace(_filter))
            {
                string f = _filter.Trim();
                query = query.Where(r =>
                    Contains(r.key, f) ||
                    Contains(r.server?.displayName, f) ||
                    Contains(r.server?.definitionId, f) ||
                    Contains(r.server?.equipmentSlotId, f) ||
                    Contains(r.client?.displayName, f) ||
                    Contains(r.client?.definitionId, f));
            }

            return query.ToArray();
        }

        private static bool Contains(string value, string filter) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private void Refresh()
        {
            _rows.Clear();
            _selectedIndex = -1;

            CharacterVisualProfile profile = AssetDatabase.LoadAssetAtPath<CharacterVisualProfile>(
                CharacterVisualCatalogBuilder.OutputAssetPath);
            if (profile == null)
            {
                _summary = "Client CharacterVisualProfile.asset was not found.";
                _sourceStatus = string.Empty;
                Repaint();
                return;
            }

            List<ServerItem> serverItems = new List<ServerItem>();
            if (OutfitAuthoringSaveBridge.TryGetReviewPaths(out string contentRoot, out string armorPath))
            {
                try
                {
                    serverItems = ReadServerItems(armorPath);
                    _sourceStatus = $"Server Content: {contentRoot}";
                }
                catch (Exception ex)
                {
                    _sourceStatus = "Server read failed: " + ex.Message;
                }
            }
            else
            {
                _sourceStatus = "Server Content Root is not configured.";
            }

            CharacterWearableSetDefinition[] clientItems =
                (profile.WearableSets ?? Array.Empty<CharacterWearableSetDefinition>())
                .Where(w => w != null && !string.IsNullOrWhiteSpace(w.definitionId))
                .ToArray();

            BuildRows(serverItems, clientItems, profile);

            int matched = _rows.Count(r => r.state == AuditState.Matched);
            int problems = _rows.Count - matched;
            _summary = $"{_rows.Count} total | {matched} matched | {problems} problem(s)";
            Repaint();
        }

        private void BuildRows(
            List<ServerItem> serverItems,
            CharacterWearableSetDefinition[] clientItems,
            CharacterVisualProfile profile)
        {
            var serverByDef = serverItems
                .GroupBy(x => x.definitionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var serverByPresentation = serverItems
                .GroupBy(x => x.presentationId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var clientByDef = clientItems
                .GroupBy(x => x.definitionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var clientByPresentation = clientItems
                .GroupBy(x => x.presentationId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var keys = new HashSet<string>(serverByDef.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(clientByDef.Keys);

            foreach (string key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                List<ServerItem> sList = serverByDef.TryGetValue(key, out List<ServerItem> sv) ? sv : null;
                List<CharacterWearableSetDefinition> cList =
                    clientByDef.TryGetValue(key, out List<CharacterWearableSetDefinition> cv) ? cv : null;

                ServerItem server = sList?.FirstOrDefault();
                CharacterWearableSetDefinition client = cList?.FirstOrDefault();

                AuditState state;
                string detail = string.Empty;

                if (sList != null && sList.Count > 1)
                {
                    state = AuditState.DuplicateServerDefinition;
                    detail = $"Server has {sList.Count} records with definitionId {key}.";
                }
                else if (cList != null && cList.Count > 1)
                {
                    state = AuditState.DuplicateClientDefinition;
                    detail = $"Client has {cList.Count} wearables with definitionId {key}.";
                }
                else if (server == null)
                {
                    state = AuditState.ClientOnly;
                    detail = "Client wearable exists without a matching server Armor definition.";
                }
                else if (client == null)
                {
                    state = AuditState.ServerOnly;
                    detail = "Server Armor definition exists without a matching client wearable.";
                }
                else if (serverByPresentation.TryGetValue(server.presentationId, out List<ServerItem> sp) && sp.Count > 1)
                {
                    state = AuditState.DuplicateServerPresentation;
                    detail = $"Server presentationId {server.presentationId} is duplicated.";
                }
                else if (clientByPresentation.TryGetValue(client.presentationId, out List<CharacterWearableSetDefinition> cp) && cp.Count > 1)
                {
                    state = AuditState.DuplicateClientPresentation;
                    detail = $"Client presentationId {client.presentationId} is duplicated.";
                }
                else if (server.presentationId != client.presentationId)
                {
                    state = AuditState.IdMismatch;
                    detail = $"Presentation mismatch: server {server.presentationId}, client {client.presentationId}.";
                }
                else if (HasBrokenParts(client, profile, out string broken))
                {
                    state = AuditState.BrokenPresentation;
                    detail = broken;
                }
                else
                {
                    state = AuditState.Matched;
                    detail = "Server identity and client wearable presentation match.";
                }

                _rows.Add(new Row
                {
                    key = key,
                    server = server,
                    client = client,
                    state = state,
                    detail = detail,
                });
            }

            _rows.Sort((a, b) =>
            {
                int byState = a.state.CompareTo(b.state);
                if (byState != 0) return byState;
                string an = a.server?.displayName ?? a.client?.displayName ?? a.key;
                string bn = b.server?.displayName ?? b.client?.displayName ?? b.key;
                return string.Compare(an, bn, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static bool HasBrokenParts(
            CharacterWearableSetDefinition wearable,
            CharacterVisualProfile profile,
            out string detail)
        {
            detail = string.Empty;
            if (wearable == null || profile == null)
                return false;

            CharacterWearablePartSelection[] parts = wearable.parts ?? Array.Empty<CharacterWearablePartSelection>();
            for (int i = 0; i < parts.Length; ++i)
            {
                CharacterWearablePartSelection part = parts[i];
                if (part.slotId == 0 || part.optionId == 0 ||
                    !profile.TryGetOption(part.slotId, part.optionId, out CharacterVisualOptionDefinition option) ||
                    option == null || option.mesh == null)
                {
                    detail = $"Broken wearable mesh reference: slot {part.slotId}, option {part.optionId}.";
                    return true;
                }
            }
            return false;
        }

        private static List<ServerItem> ReadServerItems(string armorPath)
        {
            string json = File.ReadAllText(armorPath);
            List<string> objects = ExtractObjectsFromItemsArray(json);
            var result = new List<ServerItem>(objects.Count);

            for (int i = 0; i < objects.Count; ++i)
            {
                string item = objects[i];
                Match def = DefinitionIdRegex.Match(item);
                Match pres = PresentationIdRegex.Match(item);
                if (!def.Success || !pres.Success || !ushort.TryParse(pres.Groups[1].Value, out ushort presentationId))
                    continue;

                Match name = DisplayNameRegex.Match(item);
                Match slot = EquipmentSlotsRegex.Match(item);
                result.Add(new ServerItem
                {
                    definitionId = def.Groups[1].Value,
                    displayName = name.Success ? name.Groups[1].Value : def.Groups[1].Value,
                    presentationId = presentationId,
                    equipmentSlotId = slot.Success ? slot.Groups[1].Value : string.Empty,
                });
            }

            return result;
        }

        private static List<string> ExtractObjectsFromItemsArray(string json)
        {
            var result = new List<string>();
            Match items = Regex.Match(json ?? string.Empty, "\"items\"\\s*:\\s*\\[", RegexOptions.CultureInvariant);
            if (!items.Success)
                return result;

            int start = (json ?? string.Empty).IndexOf('[', items.Index);
            if (start < 0)
                return result;

            bool inString = false;
            bool escaped = false;
            int depth = 0;
            int objectStart = -1;

            for (int i = start + 1; i < json.Length; ++i)
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
                        result.Add(json.Substring(objectStart, i - objectStart + 1));
                        objectStart = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }

            return result;
        }

        private static string StateLabel(AuditState state)
        {
            switch (state)
            {
                case AuditState.Matched: return "✓ MATCHED";
                case AuditState.ServerOnly: return "SERVER ONLY";
                case AuditState.ClientOnly: return "CLIENT ONLY";
                case AuditState.IdMismatch: return "ID MISMATCH";
                case AuditState.DuplicateServerDefinition: return "SERVER DEF DUP";
                case AuditState.DuplicateServerPresentation: return "SERVER PID DUP";
                case AuditState.DuplicateClientDefinition: return "CLIENT DEF DUP";
                case AuditState.DuplicateClientPresentation: return "CLIENT PID DUP";
                case AuditState.BrokenPresentation: return "BROKEN VISUAL";
                default: return state.ToString();
            }
        }
    }
}
#endif
