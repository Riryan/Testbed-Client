#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Game.Client.UI.CharacterSelect;
using Game.Client.UI.Interactions;
using Game.Client.UI.PlayerItems;
using Game.Client.UI.Recovery;
using Game.Client.UI.Root;
using Game.Client.UI.SocialEconomy;
using CanonicalSocialEconomyUI = Game.Client.UI.Social.ClientSocialEconomyUI;
using TradeStorageUI = Game.Client.UI.Social.ClientTradeStorageUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// Builds/repairs the complete authored ClientUIRoot scaffold once in the Editor.
    /// It never runs in a player build and never replaces an existing authored panel.
    /// The resulting prefab is the permanent source of truth artists edit directly.
    /// </summary>
    // RETIRED MENU: scaffold/migration helper. ClientUIRoot.prefab is edited directly now.
    public static class ClientUIFullScaffoldBuilder
    {
        private const string TargetPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";

        public static void BuildOrRepairFullMasterUi()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            // A pre-existing ClientUIRoot prefab may come from the older inventory/HUD-only
            // testbed. In that case simply "repairing" the scaffold is not enough because
            // the functional CharacterSelectShell has never been embedded. Run the canonical
            // migration first so Connect/Login/Character Select are real authored surfaces
            // inside the one master prefab.
            if (NeedsMasterMigration(prefab))
            {
                ClientUIRootPrefabBuilder.MigrateMasterPrefab();
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            }
            if (prefab == null)
                throw new InvalidOperationException($"Master ClientUIRoot prefab was not found: {TargetPath}");

            GameObject root = PrefabUtility.LoadPrefabContents(TargetPath);
            try
            {
                EnsureFullScaffold(root);
                PrefabUtility.SaveAsPrefabAsset(root, TargetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
                Debug.Log(
                    "[ClientUI] FULL Master UI scaffold is present. ClientUIRoot.prefab is now the long-lived authored canvas. " +
                    "Future gameplay code should bind existing panels instead of rebuilding the canvas.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void OpenFullMasterUi()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            if (prefab == null)
            {
                BuildOrRepairFullMasterUi();
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            }
            if (prefab != null)
            {
                Selection.activeObject = prefab;
                AssetDatabase.OpenAsset(prefab);
            }
        }

        public static void ValidateFullMasterUi()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            if (prefab == null)
                throw new InvalidOperationException($"Master ClientUIRoot prefab was not found: {TargetPath}");

            ClientUIPanelMarker[] markers = prefab.GetComponentsInChildren<ClientUIPanelMarker>(true);
            var found = new HashSet<ClientUIPanelId>();
            foreach (ClientUIPanelMarker marker in markers)
            {
                if (marker == null || marker.PanelId == ClientUIPanelId.None)
                    continue;
                if (!found.Add(marker.PanelId))
                    throw new InvalidOperationException($"Duplicate FULL Master UI panel ID: {marker.PanelId}");
            }

            foreach (ClientUIPanelId id in RequiredPanelIds)
            {
                if (!found.Contains(id))
                    throw new InvalidOperationException($"FULL Master UI is missing planned panel: {id}");
            }

            if (prefab.GetComponent<ClientWindowManager>() == null)
                throw new InvalidOperationException("FULL Master UI is missing ClientWindowManager.");
            if (prefab.GetComponentInChildren<ClientPopupController>(true) == null)
                throw new InvalidOperationException("FULL Master UI is missing ClientPopupController.");

            ClientRecoveryUI recovery = prefab.GetComponent<ClientRecoveryUI>();
            if (recovery == null || !recovery.HasAuthoredBindings)
                throw new InvalidOperationException("FULL Master UI is missing authored ClientRecoveryUI bindings.");

            CanonicalSocialEconomyUI socialEconomy = prefab.GetComponent<CanonicalSocialEconomyUI>();
            if (socialEconomy == null || !socialEconomy.HasAuthoredBindings)
                throw new InvalidOperationException("FULL Master UI is missing authored ClientSocialEconomyUI bindings.");

            TradeStorageUI tradeStorage = prefab.GetComponent<TradeStorageUI>();
            if (tradeStorage == null || !tradeStorage.HasAuthoredBindings)
                throw new InvalidOperationException("FULL Master UI is missing authored Trade/Storage bindings.");

            Debug.Log($"[ClientUI] FULL Master UI validated: {found.Count} stable panel surfaces.", prefab);
            Selection.activeObject = prefab;
        }

        public static void EnsureFullScaffold(GameObject root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            ClientUIRoot uiRoot = root.GetComponent<ClientUIRoot>();
            if (uiRoot == null)
                uiRoot = root.AddComponent<ClientUIRoot>();
            ClientWindowManager windows = root.GetComponent<ClientWindowManager>();
            if (windows == null)
                windows = root.AddComponent<ClientWindowManager>();

            // Ensure all currently working authored surfaces exist before reorganizing them.
            uiRoot.BuildPresentationForEditor();
            EnsureInventorySurface(root);
            PlayerInventoryShell inventory = root.GetComponent<PlayerInventoryShell>();
            inventory?.CaptureAuthoredReferencesForEditor();
            ClientInteractionUI interaction = root.GetComponent<ClientInteractionUI>();
            interaction?.BuildPresentationForEditor();

            EnsureFrontend(root, font);
            EnsureHud(root, font);
            EnsureGameplayWindows(root, font, inventory);
            EnsureRecoveryBindings(root, font);
            EnsureNpcWindows(root, font);
            EnsureInteractionSurfaces(root, font);
            EnsurePopupRoot(root, font);
            // Trade/Storage retain their existing authored shells and request/cache paths.
            // Friends/Social has one canonical binder layered on the same master prefab.
            EnsureSocialEconomyBindings(root, font);
            ClientSocialEconomyUIAuthoring.InstallOrRepairForRoot(root);
            EnsureNotifications(root, font);
            EnsureDeathRespawn(root, font);
            EnsureChat(root);
            EnsureSystemOverlays(root, font);
            EnsureDebug(root, font);

            // Capture the newly-created framework components into the master controller
            // after the full scaffold exists. This remains Editor-only authoring work.
            uiRoot.BuildPresentationForEditor();
            inventory?.CaptureAuthoredReferencesForEditor();
            CanonicalSocialEconomyUI socialEconomy = root.GetComponent<CanonicalSocialEconomyUI>();
            TradeStorageUI tradeStorage = root.GetComponent<TradeStorageUI>();
            tradeStorage?.CaptureAuthoredReferencesForEditor();

            windows.RebuildRegistry();
            EditorUtility.SetDirty(windows);
            EditorUtility.SetDirty(uiRoot);
            if (inventory != null) EditorUtility.SetDirty(inventory);
            if (interaction != null) EditorUtility.SetDirty(interaction);
            if (socialEconomy != null) EditorUtility.SetDirty(socialEconomy);
            if (tradeStorage != null) EditorUtility.SetDirty(tradeStorage);
        }

        private static bool NeedsMasterMigration(GameObject prefab)
        {
            if (prefab == null)
                return true;

            // The complete authored master requires the functional frontend to be embedded,
            // not just similarly named placeholder objects. Also migrate old testbed roots
            // that predate the canonical controller set.
            return prefab.GetComponent<ClientUIRoot>() == null
                || prefab.GetComponent<PlayerInventoryShell>() == null
                || prefab.GetComponent<ClientInteractionUI>() == null
                || prefab.GetComponentInChildren<CharacterSelectShell>(true) == null;
        }

        private static Transform GetSerializedPanelTransform(CharacterSelectShell shell, string propertyName)
        {
            if (shell == null || string.IsNullOrWhiteSpace(propertyName))
                return null;

            SerializedObject serializedShell = new SerializedObject(shell);
            SerializedProperty property = serializedShell.FindProperty(propertyName);
            GameObject referencedObject = property?.objectReferenceValue as GameObject;
            return referencedObject != null ? referencedObject.transform : null;
        }

        private static void EnsureFrontend(GameObject root, Font font)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "Frontend", ClientUIPanelId.FrontendConnect);
            CharacterSelectShell shell = root.GetComponentInChildren<CharacterSelectShell>(true);
            if (shell != null && shell.transform.parent != group)
                shell.transform.SetParent(group, false);

            if (shell != null)
            {
                // Bind the semantic panel markers to CharacterSelectShell's serialized
                // references instead of hierarchy paths. Artists are therefore free to
                // rename/reparent these panels after the one-time migration. The name
                // lookup remains only as a compatibility fallback for an older prefab
                // whose serialized references were never captured.
                Transform connection = GetSerializedPanelTransform(shell, "connectionPanel")
                    ?? FindAnywhere(shell.transform, "ConnectionPanel");
                Transform login = GetSerializedPanelTransform(shell, "loginPanel")
                    ?? FindAnywhere(shell.transform, "LoginPanel");
                Transform character = GetSerializedPanelTransform(shell, "characterPanel")
                    ?? FindAnywhere(shell.transform, "CharacterPanel");

                if (connection == null || login == null || character == null)
                {
                    throw new InvalidOperationException(
                        "The embedded CharacterSelectShell is missing one or more authored frontend panel references " +
                        "(connectionPanel, loginPanel, characterPanel). Repair the CharacterSelectShell prefab references " +
                        "before building the FULL Master UI; placeholder panels are intentionally not substituted.");
                }

                MarkExisting(connection, ClientUIPanelId.FrontendConnect, false, false, false, false);
                MarkExisting(login, ClientUIPanelId.FrontendLogin, false, false, false, false);
                MarkExisting(character, ClientUIPanelId.FrontendCharacterSelect, false, false, false, false);
            }

            GameObject create = EnsureWindow(group, "CharacterCreatePanel", ClientUIPanelId.FrontendCharacterCreate,
                "CHARACTER CREATION", false, false, font,
                "Identity", "Origin / Faction", "Appearance", "Body", "Face", "Hair", "Colors", "Traits", "Movement Style", "Preview", "Create / Back");
            EnsureSection(create.transform, "IdentityTab", font, "Name / identity / basic character data");
            EnsureSection(create.transform, "OriginFactionTab", font, "Human / Vampire / Hunter / Undead origin and faction presentation");
            EnsureSection(create.transform, "AppearanceTab", font, "Catalog-driven Synty appearance authoring");
            EnsureSection(create.transform, "TraitsTab", font, "Character traits");
            EnsureSection(create.transform, "MovementStyleTab", font, "Movement style selection");

            EnsureWindow(group, "CharacterOriginPanel", ClientUIPanelId.FrontendCharacterOrigin,
                "CHARACTER ORIGIN", false, false, font, "Origin choices", "Faction presentation", "Requirements", "Confirm / Back");
            EnsureWindow(group, "CharacterTraitsPanel", ClientUIPanelId.FrontendCharacterTraits,
                "CHARACTER TRAITS", false, false, font, "Trait list", "Selected traits", "Details", "Confirm / Back");
            EnsureWindow(group, "MovementStylePanel", ClientUIPanelId.FrontendMovementStyle,
                "MOVEMENT STYLE", false, false, font, "Style list", "Preview", "Confirm / Back");
            EnsureWindow(group, "ServerStatusPanel", ClientUIPanelId.FrontendServerStatus,
                "SERVER STATUS", false, false, font, "Gateway", "GameServer", "Maintenance / notices", "Retry");
            EnsureOverlay(group, "FrontendLoadingOverlay", ClientUIPanelId.FrontendLoading,
                "LOADING", false, true, font);
        }

        private static void EnsureHud(GameObject root, Font font)
        {
            Transform hud = EnsureTopLevelGroup(root.transform, "HUD", ClientUIPanelId.HudPlayerVitals);

            MarkExisting(hud.Find("Resources"), ClientUIPanelId.HudPlayerVitals, true, false, false, false);
            MarkExisting(hud.Find("TargetFrame"), ClientUIPanelId.HudTargetFrame, true, false, false, false);
            MarkExisting(hud.Find("StatusPanel"), ClientUIPanelId.HudStatusEffects, true, false, false, false);
            MarkExisting(hud.Find("ActionBar"), ClientUIPanelId.HudActionBar, true, false, false, false);
            MarkExisting(hud.Find("InteractionPrompt"), ClientUIPanelId.HudInteractionPrompt, true, false, false, false);

            EnsureHudWidget(hud, "Experience", ClientUIPanelId.HudExperience, "EXPERIENCE", font);
            EnsureHudWidget(hud, "PlayerCastBar", ClientUIPanelId.HudPlayerCastBar, "PLAYER CAST", font);
            EnsureHudWidget(hud, "TargetCastBar", ClientUIPanelId.HudTargetCastBar, "TARGET CAST", font);
            EnsureHudWidget(hud, "BuffContainer", ClientUIPanelId.HudBuffs, "BUFFS", font);
            EnsureHudWidget(hud, "DebuffContainer", ClientUIPanelId.HudDebuffs, "DEBUFFS", font);
            EnsureHudWidget(hud, "ActionCombatAim", ClientUIPanelId.HudActionCombatAim, "ACTION COMBAT AIM", font);
            EnsureHudWidget(hud, "PartyFrames", ClientUIPanelId.HudPartyFrames, "PARTY", font);
            EnsureHudWidget(hud, "PetFrame", ClientUIPanelId.HudPetFrame, "PET", font);
            EnsureHudWidget(hud, "ObjectiveTracker", ClientUIPanelId.HudObjectiveTracker, "OBJECTIVES", font);
            EnsureHudWidget(hud, "Minimap", ClientUIPanelId.HudMinimap, "MINIMAP", font);
            EnsureHiddenDirectionPlaceholder(hud, font);
            EnsureHudWidget(hud, "Clock", ClientUIPanelId.HudClock, "CLOCK", font);
            EnsureHudWidget(hud, "Latency", ClientUIPanelId.HudLatency, "LATENCY", font);
            EnsureHudWidget(hud, "CombatFeedback", ClientUIPanelId.HudCombatFeedback, "COMBAT FEEDBACK", font);
            EnsureHudWidget(hud, "HeatWanted", ClientUIPanelId.HudHeatWanted, "HEAT / WANTED", font);
            EnsureHudWidget(hud, "StatusOrbs", ClientUIPanelId.HudStatusOrbs, "STATUS ORBS", font);
            EnsureHudWidget(hud, "Portrait", ClientUIPanelId.HudPortrait, "PORTRAIT", font);
            EnsureHudWidget(hud, "VoiceChat", ClientUIPanelId.HudVoiceChat, "VOICE CHAT", font);
        }

        private static void EnsureRecoveryBindings(GameObject root, Font font)
        {
            Text skills = EnsureRecoveryText(root, "SkillsWindow", "RecoverySkillsState", font);
            Text professions = EnsureRecoveryText(root, "ProfessionsWindow", "RecoveryProfessionState", font);
            Text heat = EnsureRecoveryText(root, "HeatBountyWindow", "RecoveryHeatState", font);

            Transform craftingWindow = FindAnywhere(root.transform, "CraftingWindow");
            if (craftingWindow == null)
                throw new InvalidOperationException("FULL Master UI is missing CraftingWindow for recovery bindings.");
            Transform body = craftingWindow.Find("Body");
            if (body == null)
                throw new InvalidOperationException("CraftingWindow is missing its authored Body.");
            Transform outline = body.Find("PlannedSurfaceOutline");
            if (outline != null) outline.gameObject.SetActive(false);

            Transform existingContainer = body.Find("RecoveryCraftingState");
            GameObject container = existingContainer != null ? existingContainer.gameObject : NewUi("RecoveryCraftingState", body);
            RectTransform containerRect = container.GetComponent<RectTransform>();
            Stretch(containerRect);

            Text summary;
            Transform existingSummary = container.transform.Find("Summary");
            if (existingSummary != null && existingSummary.TryGetComponent(out summary))
            {
            }
            else
            {
                summary = NewText("Summary", container.transform, "Known recipes", font, 15, TextAnchor.UpperLeft);
            }
            RectTransform summaryRect = summary.rectTransform;
            summaryRect.anchorMin = new Vector2(0f, 1f);
            summaryRect.anchorMax = new Vector2(1f, 1f);
            summaryRect.pivot = new Vector2(0.5f, 1f);
            summaryRect.offsetMin = new Vector2(12f, -70f);
            summaryRect.offsetMax = new Vector2(-12f, -10f);

            Transform existingList = container.transform.Find("RecipeButtons");
            GameObject list = existingList != null ? existingList.gameObject : NewUi("RecipeButtons", container.transform);
            RectTransform listRect = list.GetComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0f, 0f);
            listRect.anchorMax = new Vector2(1f, 1f);
            listRect.offsetMin = new Vector2(12f, 12f);
            listRect.offsetMax = new Vector2(-12f, -82f);
            VerticalLayoutGroup layout = list.GetComponent<VerticalLayoutGroup>() ?? list.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            Transform existingTemplate = list.transform.Find("RecipeButtonTemplate");
            Button template = existingTemplate != null ? existingTemplate.GetComponent<Button>() : null;
            if (template == null)
            {
                template = NewButton("RecipeButtonTemplate", list.transform, "Recipe", font);
                LayoutElement element = template.GetComponent<LayoutElement>() ?? template.gameObject.AddComponent<LayoutElement>();
                element.preferredHeight = 38f;
            }
            template.gameObject.SetActive(false);

            ClientRecoveryUI recovery = root.GetComponent<ClientRecoveryUI>() ?? root.AddComponent<ClientRecoveryUI>();
            recovery.ConfigureAuthoredViewsForEditor(skills, professions, heat, summary, listRect, template);
            EditorUtility.SetDirty(recovery);
        }

        private static Text EnsureRecoveryText(GameObject root, string windowName, string viewName, Font font)
        {
            Transform window = FindAnywhere(root.transform, windowName);
            if (window == null)
                throw new InvalidOperationException($"FULL Master UI is missing {windowName} for recovery bindings.");
            Transform body = window.Find("Body");
            if (body == null)
                throw new InvalidOperationException($"{windowName} is missing its authored Body.");
            Transform outline = body.Find("PlannedSurfaceOutline");
            if (outline != null) outline.gameObject.SetActive(false);
            Transform existing = body.Find(viewName);
            Text text = existing != null ? existing.GetComponent<Text>() : null;
            if (text == null)
                text = NewText(viewName, body, string.Empty, font, 16, TextAnchor.UpperLeft);
            Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(10f, 10f);
            text.rectTransform.offsetMax = new Vector2(-10f, -10f);
            return text;
        }

        private static void EnsureGameplayWindows(GameObject root, Font font, PlayerInventoryShell inventory)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "Game Windows", ClientUIPanelId.CharacterWindow);

            if (inventory != null)
            {
                GameObject inventoryWindow = inventory.AuthoredWindowRootForEditor;
                if (inventoryWindow != null)
                {
                    if (inventoryWindow.transform.parent != group)
                        inventoryWindow.transform.SetParent(group, false);
                    inventoryWindow.name = "InventoryWindow";
                    MarkExisting(inventoryWindow.transform, ClientUIPanelId.InventoryWindow, true, false, true);
                }
            }

            GameObject character = EnsureWindow(group, "CharacterWindow", ClientUIPanelId.CharacterWindow,
                "CHARACTER", true, false, font,
                "Overview", "Equipment", "Attributes", "Stats", "Reputation", "Faction / Origin", "Currencies", "Gathering Skills");
            EnsureSection(character.transform, "OverviewTab", font, "Identity / level / origin / progression summary");
            EnsureSection(character.transform, "EquipmentTab", font, "Public and local equipment presentation");
            EnsureSection(character.transform, "AttributesTab", font, "Primary / extended attributes");
            EnsureSection(character.transform, "StatsTab", font, "Derived combat and gameplay stats");
            EnsureSection(character.transform, "ReputationTab", font, "Reputation / relationships / Heat-facing reputation");
            EnsureSection(character.transform, "FactionOriginTab", font, "Faction and origin state");
            EnsureSection(character.transform, "CurrenciesTab", font, "Gold / currencies / account-bound values");
            EnsureSection(character.transform, "GatheringSkillsTab", font, "Harvesting and gathering progression");

            GameObject skills = EnsureWindow(group, "SkillsWindow", ClientUIPanelId.SkillsWindow,
                "SKILLS / ABILITIES", true, false, font,
                "Ability list", "Categories", "Details", "Passives", "Loadout", "Cooldowns / requirements");
            EnsureSection(skills.transform, "AbilityList", font, "Combat / targeted / area / action abilities");
            EnsureSection(skills.transform, "AbilityDetails", font, "Targeting, resource, cooldown, movement and relationship requirements");
            EnsureSection(skills.transform, "Loadout", font, "Action-bar/loadout authoring");

            GameObject quests = EnsureWindow(group, "QuestsWindow", ClientUIPanelId.QuestsWindow,
                "QUESTS", true, false, font, "Active", "Completed", "Quest details", "Objectives", "Rewards");
            EnsureSection(quests.transform, "ActiveTab", font, "Active quests");
            EnsureSection(quests.transform, "CompletedTab", font, "Completed quests");
            EnsureSection(quests.transform, "QuestDetails", font, "Description / objectives / rewards");

            GameObject crafting = EnsureWindow(group, "CraftingWindow", ClientUIPanelId.CraftingWindow,
                "CRAFTING", true, false, font,
                "Recipe list", "Recipe details", "Ingredients", "Tools", "Profession requirement", "Craft progress", "Result");
            EnsureSection(crafting.transform, "RecipeList", font, "Learned and available recipes");
            EnsureSection(crafting.transform, "RecipeDetails", font, "Ingredients / tools / difficulty / output");
            EnsureSection(crafting.transform, "CraftProgress", font, "Timed authoritative craft progress");

            EnsureWindow(group, "ProfessionsWindow", ClientUIPanelId.ProfessionsWindow,
                "PROFESSIONS", true, false, font, "Profession list", "Level / XP", "Unlocked recipes", "Bonuses");
            EnsureWindow(group, "StorageWindow", ClientUIPanelId.StorageWindow,
                "STORAGE / BANK", true, false, font, "Stored items", "Capacity", "Stored currency", "Transfer controls");

            GameObject social = EnsureWindow(group, "SocialWindow", ClientUIPanelId.SocialWindow,
                "SOCIAL", true, false, font,
                "Friends", "Relationships", "Party", "Guild", "Ignore / Block", "Invites");
            EnsureSection(social.transform, "FriendsTab", font, "Friends / requests / gifting / relationship points");
            EnsureSection(social.transform, "RelationshipsTab", font, "Couple / relationship state");
            EnsureSection(social.transform, "PartyTab", font, "Party roster / invites / leadership");
            EnsureSection(social.transform, "GuildTab", font, "Guild roster / ranks / notices / management");
            EnsureSection(social.transform, "IgnoreTab", font, "Ignored / blocked players");

            EnsureWindow(group, "TradeWindow", ClientUIPanelId.TradeWindow,
                "PLAYER TRADE", true, true, font, "Your offer", "Their offer", "Currency", "Lock", "Accept", "Cancel");
            EnsureWindow(group, "DuelWindow", ClientUIPanelId.DuelWindow,
                "DUEL", true, true, font, "Opponent", "Rules", "Accept / decline", "Active duel state");
            EnsureWindow(group, "JournalWindow", ClientUIPanelId.JournalWindow,
                "JOURNAL", true, false, font, "Activity log", "Recent events", "Story / world notes");
            EnsureWindow(group, "CodexWindow", ClientUIPanelId.CodexWindow,
                "CODEX", true, false, font, "People", "Factions", "Creatures", "Items", "Lore");
            EnsureWindow(group, "WorldMapWindow", ClientUIPanelId.WorldMapWindow,
                "WORLD MAP", true, false, font, "Map viewport", "Markers", "Objectives", "Legend");

            GameObject housing = EnsureWindow(group, "HousingWindow", ClientUIPanelId.HousingWindow,
                "HOUSING", true, false, font, "Property", "Permissions", "Placeables", "Storage", "Upgrade / management");
            EnsureSection(housing.transform, "PlaceablesTab", font, "Owned / placed world objects");
            EnsureWindow(group, "PlaceablesWindow", ClientUIPanelId.PlaceablesWindow,
                "PLACEABLE OBJECT", true, false, font, "Spawn", "Pickup", "Upgrade", "Destroy", "Permissions");
            EnsureWindow(group, "HarvestingWindow", ClientUIPanelId.HarvestingWindow,
                "HARVESTING", true, false, font, "Gathering skills", "Node status", "Progress", "Rewards");

            EnsureWindow(group, "MainMenuWindow", ClientUIPanelId.MainMenuWindow,
                "MAIN MENU", true, true, font, "Resume", "Character", "Settings", "Help", "Logout", "Quit");
            GameObject settings = EnsureWindow(group, "SettingsWindow", ClientUIPanelId.SettingsWindow,
                "SETTINGS", true, false, font,
                "Gameplay", "Video", "Audio", "Controls", "UI", "Chat", "Accessibility");
            EnsureSection(settings.transform, "GameplayTab", font, "Gameplay preferences");
            EnsureSection(settings.transform, "VideoTab", font, "Graphics and display");
            EnsureSection(settings.transform, "AudioTab", font, "Audio / voice / music");
            EnsureSection(settings.transform, "ControlsTab", font, "Input bindings and sensitivity");
            EnsureSection(settings.transform, "UiTab", font, "UI scale / presentation / theme");
            EnsureSection(settings.transform, "ChatTab", font, "Chat presentation and filtering");
            EnsureSection(settings.transform, "AccessibilityTab", font, "Accessibility options");

            EnsureWindow(group, "HelpWindow", ClientUIPanelId.HelpWindow,
                "HELP", true, false, font, "Controls", "Gameplay help", "Support", "Known commands");
            EnsureWindow(group, "ReportPlayerWindow", ClientUIPanelId.ReportPlayerWindow,
                "REPORT PLAYER", true, true, font, "Player", "Reason", "Details", "Submit / cancel");

            GameObject gm = EnsureWindow(group, "GameMasterWindow", ClientUIPanelId.GameMasterWindow,
                "GAME MASTER / STAFF", true, false, font,
                "Player Search", "Inspect", "Observe", "Moderation", "Teleport", "Diagnostics", "Audit status");
            EnsureSection(gm.transform, "PlayerSearchTab", font, "Search online players / actor IDs");
            EnsureSection(gm.transform, "InspectPlayerTab", font, "Authoritative player/account state inspection");
            EnsureSection(gm.transform, "ObserveTab", font, "Visible / hidden observer / spectate controls");
            EnsureSection(gm.transform, "ModerationTab", font, "Mute / kick / staff actions subject to capabilities");
            EnsureSection(gm.transform, "TeleportTab", font, "Privileged movement tools; server-authorized only");
            EnsureSection(gm.transform, "DiagnosticsTab", font, "AOI / actor / server diagnostics");

            EnsureWindow(group, "MusicPlayerWindow", ClientUIPanelId.MusicPlayerWindow,
                "MUSIC PLAYER", true, false, font, "Track", "Playlist", "Volume", "Playback controls");
            EnsureWindow(group, "FactionTransitionWindow", ClientUIPanelId.FactionTransitionWindow,
                "FACTION / ORIGIN TRANSITION", true, true, font, "Current state", "Transition requirements", "Result", "Confirm");
            EnsureWindow(group, "LootWindow", ClientUIPanelId.LootWindow,
                "LOOT", true, false, font, "Loot source", "Item rows", "Take", "Take all", "Capacity feedback");
            EnsureWindow(group, "PetsWindow", ClientUIPanelId.PetsWindow,
                "PETS", true, false, font, "Owned pets", "Summon / dismiss", "Pet skills", "Pet status / behavior");
            EnsureWindow(group, "MountsWindow", ClientUIPanelId.MountsWindow,
                "MOUNTS", true, false, font, "Owned mounts", "Summon / dismiss", "Mount state", "Mount details");
            EnsureWindow(group, "HeatBountyWindow", ClientUIPanelId.HeatBountyWindow,
                "HEAT / BOUNTY", true, false, font, "Wanted level", "Known incidents", "Police / Hunter / Vampire bounty", "Evidence / witness state");
            EnsureWindow(group, "EquipmentWindow", ClientUIPanelId.EquipmentWindow,
                "EQUIPMENT", true, false, font, "Equipment slots", "Item details", "Durability / condition", "Public appearance state");
            EnsureWindow(group, "AttributesWindow", ClientUIPanelId.AttributesWindow,
                "ATTRIBUTES / STATS", true, false, font, "Primary attributes", "Extended attributes", "Derived stats", "Bonuses / penalties");
            EnsureWindow(group, "FriendsWindow", ClientUIPanelId.FriendsWindow,
                "FRIENDS", true, false, font, "Friends", "Pending requests", "Gift / relationship points", "Remove / block");
            EnsureWindow(group, "RelationshipsWindow", ClientUIPanelId.RelationshipsWindow,
                "RELATIONSHIPS", true, false, font, "Relationship state", "Couple actions", "History / points", "Requests");
            EnsureWindow(group, "PartyWindow", ClientUIPanelId.PartyWindow,
                "PARTY", true, false, font, "Members", "Invites", "Leadership", "Party chat", "Leave / dismiss");
            EnsureWindow(group, "GuildWindow", ClientUIPanelId.GuildWindow,
                "GUILD", true, false, font, "Roster", "Ranks", "Invites", "Notices", "Management", "Leave / disband");
        }

        private static void EnsureNpcWindows(GameObject root, Font font)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "NPC Windows", ClientUIPanelId.NpcDialogueWindow);
            EnsureWindow(group, "DialogueWindow", ClientUIPanelId.NpcDialogueWindow,
                "NPC DIALOGUE", true, false, font, "Portrait / name", "Dialogue", "Responses", "Available services");
            EnsureWindow(group, "MerchantWindow", ClientUIPanelId.NpcMerchantWindow,
                "MERCHANT", true, false, font, "Buy", "Sell", "Merchant inventory", "Player inventory", "Currency");
            EnsureWindow(group, "RepairWindow", ClientUIPanelId.NpcRepairWindow,
                "REPAIR", true, false, font, "Equipment", "Repair cost", "Repair selected / all");
            EnsureWindow(group, "QuestGiverWindow", ClientUIPanelId.NpcQuestWindow,
                "NPC QUESTS", true, false, font, "Available", "Turn-in", "Quest details", "Accept / complete");
            EnsureWindow(group, "StorageNpcWindow", ClientUIPanelId.NpcStorageWindow,
                "NPC STORAGE", true, false, font, "Player inventory", "Storage", "Currency", "Transfer");
            EnsureWindow(group, "CraftingNpcWindow", ClientUIPanelId.NpcCraftingWindow,
                "NPC CRAFTING", true, false, font, "Services", "Recipes", "Costs", "Result");
            EnsureWindow(group, "GuildNpcWindow", ClientUIPanelId.NpcGuildWindow,
                "GUILD MANAGEMENT", true, false, font, "Create guild", "Management", "Fees", "Notices");
            EnsureWindow(group, "ReviveNpcWindow", ClientUIPanelId.NpcReviveWindow,
                "REVIVE SERVICE", true, true, font, "Service details", "Cost", "Confirm / cancel");
            EnsureWindow(group, "GenericNpcServiceWindow", ClientUIPanelId.NpcGenericServiceWindow,
                "NPC SERVICE", true, false, font, "Service title", "Service body", "Action buttons");
            EnsureWindow(group, "PopulationInteractionWindow", ClientUIPanelId.PopulationInteractionWindow,
                "POPULATION INTERACTION", true, false, font, "Actor", "Context actions", "Relationship / reaction state");
        }


        private static void EnsureSocialEconomyBindings(GameObject root, Font font)
        {
            TradeStorageUI binding = root.GetComponent<TradeStorageUI>();
            if (binding == null)
                binding = root.AddComponent<TradeStorageUI>();

            GameObject social = FindPanelById(root.transform, ClientUIPanelId.SocialWindow);
            GameObject trade = FindPanelById(root.transform, ClientUIPanelId.TradeWindow);
            GameObject tradeRequest = FindPanelById(root.transform, ClientUIPanelId.PopupTradeRequest);
            GameObject storage = FindPanelById(root.transform, ClientUIPanelId.StorageWindow);

            if (social == null || trade == null || tradeRequest == null || storage == null)
                throw new InvalidOperationException("Social/economy UI binding requires the existing Social, Trade, TradeRequest, and Storage authored panels.");

            // Keep the Social/Friends tab surface authored, but the canonical Social binder owns
            // its state. Trade/Storage are bound by ClientTradeStorageUI.
            Transform socialFriendsTab = social.transform.Find("FriendsTab");
            if (socialFriendsTab == null)
                throw new InvalidOperationException("SocialWindow is missing its existing FriendsTab surface.");
            EnsureFriendsTabBinding(socialFriendsTab, font);
            EnsureTradeBinding(trade.transform, font);
            EnsureRequestPopupBinding(tradeRequest.transform, font, "Trade request");
            EnsureStorageBinding(storage.transform, font);

            binding.CaptureAuthoredReferencesForEditor();
            EditorUtility.SetDirty(binding);
        }

        private static void EnsureFriendsBinding(Transform window, Font font)
        {
            Transform body = window.Find("Body");
            if (body == null)
                throw new InvalidOperationException("FriendsWindow is missing Body.");
            DisablePlaceholder(body, "PlannedSurfaceOutline");

            RectTransform binding = EnsureSocialBindingRoot(body);
            Text status = EnsureSocialText(binding, "StatusText", "Friends have not been synchronized yet.", font, 15, TextAnchor.MiddleLeft);
            SetRect(status.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(12f, -40f), new Vector2(-150f, -8f));
            Button refresh = EnsureSocialButton(binding, "RefreshButton", "REFRESH", font);
            SetRect(refresh.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-132f, -40f), new Vector2(-12f, -8f));

            EnsureSocialScrollList(binding, "List", font,
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(8f, 8f), new Vector2(-8f, -52f));
        }

        private static void EnsureFriendsTabBinding(Transform tab, Font font)
        {
            DisablePlaceholder(tab, "AuthoringNote");
            RectTransform binding = EnsureSocialBindingRoot(tab);
            Text status = EnsureSocialText(binding, "StatusText", "Friends have not been synchronized yet.", font, 14, TextAnchor.MiddleLeft);
            SetRect(status.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -36f), new Vector2(-132f, -6f));
            Button refresh = EnsureSocialButton(binding, "RefreshButton", "REFRESH", font);
            SetRect(refresh.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-118f, -36f), new Vector2(-8f, -6f));
            EnsureSocialScrollList(binding, "List", font,
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(6f, 6f), new Vector2(-6f, -44f));
        }

        private static void EnsureRequestPopupBinding(Transform popup, Font font, string fallbackMessage)
        {
            Transform body = popup.Find("Body");
            if (body == null)
                throw new InvalidOperationException($"{popup.name} is missing Body.");
            DisablePlaceholder(body, "PlannedSurfaceOutline");
            RectTransform binding = EnsureSocialBindingRoot(body);

            Text message = EnsureSocialText(binding, "MessageText", fallbackMessage, font, 20, TextAnchor.MiddleCenter);
            SetRect(message.rectTransform, new Vector2(0.08f, 0.32f), new Vector2(0.92f, 0.9f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            Button accept = EnsureSocialButton(binding, "AcceptButton", "ACCEPT", font);
            SetRect(accept.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 24f), new Vector2(170f, 48f));
            Button decline = EnsureSocialButton(binding, "DeclineButton", "DECLINE", font);
            SetRect(decline.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 24f), new Vector2(170f, 48f));
        }

        private static void EnsureTradeBinding(Transform window, Font font)
        {
            Transform body = window.Find("Body");
            if (body == null)
                throw new InvalidOperationException("TradeWindow is missing Body.");
            DisablePlaceholder(body, "PlannedSurfaceOutline");
            RectTransform binding = EnsureSocialBindingRoot(body);

            Text partner = EnsureSocialText(binding, "PartnerText", "No active trade", font, 17, TextAnchor.MiddleLeft);
            SetRect(partner.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -34f), new Vector2(-140f, -4f));
            Text status = EnsureSocialText(binding, "StatusText", "Trade state unavailable.", font, 14, TextAnchor.MiddleLeft);
            SetRect(status.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -62f), new Vector2(-10f, -36f));
            Button refresh = EnsureSocialButton(binding, "RefreshButton", "REFRESH", font);
            SetRect(refresh.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-126f, -34f), new Vector2(-8f, -4f));

            EnsureColumnLabel(binding, "InventoryLabel", "INVENTORY", font, 0.00f, 0.325f);
            EnsureColumnLabel(binding, "OwnOfferLabel", "YOUR OFFER", font, 0.3375f, 0.6625f);
            EnsureColumnLabel(binding, "PartnerOfferLabel", "THEIR OFFER", font, 0.675f, 1.00f);

            EnsureSocialScrollList(binding, "InventoryList", font,
                new Vector2(0.00f, 0f), new Vector2(0.325f, 1f), new Vector2(6f, 66f), new Vector2(-4f, -94f));
            EnsureSocialScrollList(binding, "OwnOfferList", font,
                new Vector2(0.3375f, 0f), new Vector2(0.6625f, 1f), new Vector2(4f, 66f), new Vector2(-4f, -94f));
            EnsureSocialScrollList(binding, "PartnerOfferList", font,
                new Vector2(0.675f, 0f), new Vector2(1.00f, 1f), new Vector2(4f, 66f), new Vector2(-6f, -94f));

            Button lockButton = EnsureSocialButton(binding, "LockButton", "LOCK", font);
            SetRect(lockButton.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(8f, 10f), new Vector2(150f, 44f));
            Button confirmButton = EnsureSocialButton(binding, "ConfirmButton", "CONFIRM", font);
            SetRect(confirmButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(170f, 44f));
            Button cancelButton = EnsureSocialButton(binding, "CancelButton", "CANCEL", font);
            SetRect(cancelButton.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-8f, 10f), new Vector2(150f, 44f));
        }

        private static void EnsureStorageBinding(Transform window, Font font)
        {
            Transform body = window.Find("Body");
            if (body == null)
                throw new InvalidOperationException("StorageWindow is missing Body.");
            DisablePlaceholder(body, "PlannedSurfaceOutline");
            RectTransform binding = EnsureSocialBindingRoot(body);

            Text capacity = EnsureSocialText(binding, "CapacityText", "Capacity unavailable", font, 16, TextAnchor.MiddleLeft);
            SetRect(capacity.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -32f), new Vector2(-140f, -4f));
            Text status = EnsureSocialText(binding, "StatusText", "Storage is not currently authorized/open.", font, 14, TextAnchor.MiddleLeft);
            SetRect(status.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(10f, -60f), new Vector2(-10f, -34f));
            Button refresh = EnsureSocialButton(binding, "RefreshButton", "REFRESH", font);
            SetRect(refresh.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-126f, -32f), new Vector2(-8f, -4f));

            EnsureColumnLabel(binding, "InventoryLabel", "INVENTORY", font, 0.0f, 0.49f);
            EnsureColumnLabel(binding, "StorageLabel", "STORAGE", font, 0.51f, 1.0f);
            EnsureSocialScrollList(binding, "InventoryList", font,
                new Vector2(0f, 0f), new Vector2(0.49f, 1f), new Vector2(6f, 8f), new Vector2(-4f, -90f));
            EnsureSocialScrollList(binding, "StorageList", font,
                new Vector2(0.51f, 0f), new Vector2(1f, 1f), new Vector2(4f, 8f), new Vector2(-6f, -90f));
        }

        private static RectTransform EnsureSocialBindingRoot(Transform parent)
        {
            RectTransform binding = EnsureGroup(parent, "SocialEconomyBinding");
            Stretch(binding);
            return binding;
        }

        private static void DisablePlaceholder(Transform parent, string name)
        {
            Transform placeholder = parent != null ? parent.Find(name) : null;
            if (placeholder != null)
                placeholder.gameObject.SetActive(false);
        }

        private static Text EnsureSocialText(Transform parent, string name, string value, Font font, int size, TextAnchor anchor)
        {
            Transform existing = parent.Find(name);
            Text text = existing != null ? existing.GetComponent<Text>() : null;
            if (text == null)
            {
                if (existing != null)
                    text = existing.gameObject.AddComponent<Text>();
                else
                    text = NewText(name, parent, value, font, size, anchor);
            }
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            if (string.IsNullOrWhiteSpace(text.text))
                text.text = value;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static Button EnsureSocialButton(Transform parent, string name, string label, Font font)
        {
            Transform existing = parent.Find(name);
            Button button = existing != null ? existing.GetComponent<Button>() : null;
            if (button == null)
                button = existing != null ? existing.gameObject.AddComponent<Button>() : NewButton(name, parent, label, font);
            Text text = button.GetComponentInChildren<Text>(true);
            if (text == null)
            {
                text = NewText("Label", button.transform, label, font, 14, TextAnchor.MiddleCenter);
                Stretch(text.rectTransform);
            }
            text.text = label;
            text.font = font;
            text.fontSize = 14;
            text.raycastTarget = false;
            return button;
        }

        private static void EnsureColumnLabel(Transform parent, string name, string label, Font font, float minX, float maxX)
        {
            Text text = EnsureSocialText(parent, name, label, font, 13, TextAnchor.MiddleCenter);
            SetRect(text.rectTransform, new Vector2(minX, 1f), new Vector2(maxX, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(0f, -66f));
        }

        private static void EnsureSocialScrollList(
            Transform parent,
            string name,
            Font font,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            Transform existing = parent.Find(name);
            GameObject list = existing != null ? existing.gameObject : NewImage(name, parent, new Color(0.035f, 0.04f, 0.05f, 0.88f));
            RectTransform listRect = list.GetComponent<RectTransform>();
            listRect.anchorMin = anchorMin;
            listRect.anchorMax = anchorMax;
            listRect.offsetMin = offsetMin;
            listRect.offsetMax = offsetMax;

            ScrollRect scroll = list.GetComponent<ScrollRect>();
            if (scroll == null) scroll = list.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            Transform viewportTransform = list.transform.Find("Viewport");
            GameObject viewport = viewportTransform != null ? viewportTransform.gameObject : NewImage("Viewport", list.transform, new Color(0f, 0f, 0f, 0f));
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            Stretch(viewportRect);
            Image viewportImage = viewport.GetComponent<Image>();
            if (viewportImage != null) viewportImage.raycastTarget = true;
            if (viewport.GetComponent<RectMask2D>() == null) viewport.AddComponent<RectMask2D>();

            Transform contentTransform = viewport.transform.Find("Content");
            GameObject content = contentTransform != null ? contentTransform.gameObject : NewUi("Content", viewport.transform);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = Vector2.zero;

            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            if (layout == null) layout = content.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 4f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = content.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewportRect;
            scroll.content = contentRect;
            EnsureSocialRowTemplate(content.transform, font);
        }

        private static void EnsureSocialRowTemplate(Transform content, Font font)
        {
            Transform existing = content.Find("RowTemplate");
            GameObject row = existing != null ? existing.gameObject : NewImage("RowTemplate", content, new Color(0.09f, 0.10f, 0.125f, 0.96f));
            LayoutElement layout = row.GetComponent<LayoutElement>();
            if (layout == null) layout = row.AddComponent<LayoutElement>();
            layout.minHeight = 58f;
            layout.preferredHeight = 58f;
            layout.flexibleHeight = 0f;

            Text primary = EnsureSocialText(row.transform, "PrimaryText", "Entry", font, 14, TextAnchor.MiddleLeft);
            SetRect(primary.rectTransform, new Vector2(0f, 0.48f), new Vector2(0.48f, 1f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(-4f, 0f));
            Text secondary = EnsureSocialText(row.transform, "SecondaryText", "Details", font, 11, TextAnchor.MiddleLeft);
            secondary.color = new Color(0.72f, 0.76f, 0.82f, 1f);
            SetRect(secondary.rectTransform, new Vector2(0f, 0f), new Vector2(0.48f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(-4f, 0f));

            Button primaryButton = EnsureSocialButton(row.transform, "PrimaryButton", "ACTION", font);
            SetRect(primaryButton.GetComponent<RectTransform>(), new Vector2(0.49f, 0.15f), new Vector2(0.74f, 0.85f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Button secondaryButton = EnsureSocialButton(row.transform, "SecondaryButton", "OTHER", font);
            SetRect(secondaryButton.GetComponent<RectTransform>(), new Vector2(0.75f, 0.15f), new Vector2(0.99f, 0.85f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            ClientSocialEconomyRowView view = row.GetComponent<ClientSocialEconomyRowView>();
            if (view == null) view = row.AddComponent<ClientSocialEconomyRowView>();
            view.CaptureAuthoredReferencesForEditor();
            EditorUtility.SetDirty(view);
            row.SetActive(false);
        }

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 offsetOrPosition,
            Vector2 sizeOrOffsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            if (anchorMin == anchorMax)
            {
                rect.anchoredPosition = offsetOrPosition;
                rect.sizeDelta = sizeOrOffsetMax;
            }
            else
            {
                rect.offsetMin = offsetOrPosition;
                rect.offsetMax = sizeOrOffsetMax;
            }
        }

        private static GameObject FindPanelById(Transform root, ClientUIPanelId id)
        {
            ClientUIPanelMarker[] markers = root.GetComponentsInChildren<ClientUIPanelMarker>(true);
            for (int i = 0; i < markers.Length; ++i)
                if (markers[i] != null && markers[i].PanelId == id)
                    return markers[i].gameObject;
            return null;
        }

        private static void EnsureInteractionSurfaces(GameObject root, Font font)
        {
            Transform interaction = EnsureTopLevelGroup(root.transform, "Interaction UI", ClientUIPanelId.InteractionContextActions);

            MarkExisting(interaction.Find("ContextMenuLayer"), ClientUIPanelId.InteractionContextActions, true, false, true);
            MarkExisting(interaction.Find("ConsentLayer"), ClientUIPanelId.InteractionConsent, true, true, true);
            MarkExisting(interaction.Find("SessionStrip"), ClientUIPanelId.InteractionSessionHud, true, false, false, false);

            EnsureOverlay(interaction, "ParticipantSelectionWindow", ClientUIPanelId.InteractionParticipantSelection,
                "SELECT PARTICIPANTS / ROLES", true, true, font);
            EnsureOverlay(interaction, "PrivateSessionControls", ClientUIPanelId.InteractionPrivateSessionControls,
                "INTERACTION SESSION CONTROLS", true, false, font);
            EnsureGroupWithMarker(interaction, "WorldPromptContainer", ClientUIPanelId.InteractionWorldPrompts, true, false, false, false);
            EnsureOverlay(interaction, "ForcedFeedingBreakFree", ClientUIPanelId.InteractionForcedFeedingBreakFree,
                "BREAK FREE", true, false, font);
        }

        private static void EnsurePopupRoot(GameObject root, Font font)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "Popups", ClientUIPanelId.PopupGeneric);

            // The frontend uses an override-sorting canvas so sibling ordering alone cannot
            // place a popup above it. The popup layer owns a higher authored canvas order.
            Canvas popupCanvas = group.GetComponent<Canvas>();
            if (popupCanvas == null)
                popupCanvas = group.gameObject.AddComponent<Canvas>();
            popupCanvas.overrideSorting = true;
            Canvas frontendCanvas = root.GetComponentInChildren<CharacterSelectShell>(true)?.GetComponent<Canvas>();
            popupCanvas.sortingOrder = frontendCanvas != null
                ? Mathf.Max(popupCanvas.sortingOrder, frontendCanvas.sortingOrder + 100)
                : Mathf.Max(popupCanvas.sortingOrder, 200);
            if (group.GetComponent<GraphicRaycaster>() == null)
                group.gameObject.AddComponent<GraphicRaycaster>();

            Image blocker = EnsureImage(group, "ModalBlocker", new Color(0f, 0f, 0f, 0.55f));
            Stretch(blocker.rectTransform);
            blocker.raycastTarget = true;
            blocker.gameObject.SetActive(false);

            GameObject generic = EnsureWindow(group, "GenericPopupWindow", ClientUIPanelId.PopupGeneric,
                "POPUP", false, true, font, "Message", "Optional icon", "Optional input", "Up to three actions");
            ClientPopupController controller = group.GetComponent<ClientPopupController>();
            if (controller == null)
                controller = group.gameObject.AddComponent<ClientPopupController>();
            ConfigurePopupController(blocker.gameObject, generic, controller, font);

            EnsureWindow(group, "ConfirmationPopup", ClientUIPanelId.PopupConfirmation,
                "CONFIRM", false, true, font, "Message", "Confirm", "Cancel");
            EnsureWindow(group, "YesNoPopup", ClientUIPanelId.PopupYesNo,
                "YES / NO", false, true, font, "Message", "Yes", "No");
            EnsureWindow(group, "AcceptDeclinePopup", ClientUIPanelId.PopupAcceptDecline,
                "REQUEST", false, true, font, "Request details", "Accept", "Decline");
            EnsureWindow(group, "TextInputPopup", ClientUIPanelId.PopupTextInput,
                "INPUT", false, true, font, "Prompt", "Text input", "Confirm", "Cancel");
            EnsureWindow(group, "QuantityPopup", ClientUIPanelId.PopupQuantity,
                "QUANTITY", true, true, font, "Item", "Quantity", "Maximum", "Confirm", "Cancel");
            EnsureWindow(group, "ItemConfirmPopup", ClientUIPanelId.PopupItemConfirm,
                "ITEM CONFIRMATION", true, true, font, "Item icon", "Item details", "Confirm", "Cancel");
            EnsureWindow(group, "ErrorPopup", ClientUIPanelId.PopupError,
                "ERROR", false, true, font, "Error message", "OK");
            EnsureWindow(group, "WarningPopup", ClientUIPanelId.PopupWarning,
                "WARNING", false, true, font, "Warning message", "Continue", "Cancel");
            EnsureWindow(group, "InformationPopup", ClientUIPanelId.PopupInformation,
                "INFORMATION", false, true, font, "Message", "OK");

            RectTransform requests = EnsureGroup(group, "RequestPopups");
            EnsureWindow(requests, "PartyInvitePopup", ClientUIPanelId.PopupPartyInvite,
                "PARTY INVITE", true, true, font, "Inviter", "Accept", "Decline");
            EnsureWindow(requests, "GuildInvitePopup", ClientUIPanelId.PopupGuildInvite,
                "GUILD INVITE", true, true, font, "Guild / inviter", "Accept", "Decline");
            EnsureWindow(requests, "FriendInvitePopup", ClientUIPanelId.PopupFriendInvite,
                "FRIEND REQUEST", true, true, font, "Player", "Accept", "Decline");
            EnsureWindow(requests, "CoupleInvitePopup", ClientUIPanelId.PopupCoupleInvite,
                "RELATIONSHIP REQUEST", true, true, font, "Player", "Accept", "Decline");
            EnsureWindow(requests, "TradeRequestPopup", ClientUIPanelId.PopupTradeRequest,
                "TRADE REQUEST", true, true, font, "Player", "Accept", "Decline");
            EnsureWindow(requests, "DuelRequestPopup", ClientUIPanelId.PopupDuelRequest,
                "DUEL REQUEST", true, true, font, "Player", "Accept", "Decline");
        }

        private static void EnsureNotifications(GameObject root, Font font)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "Notifications", ClientUIPanelId.NotificationToasts);
            EnsureHudWidget(group, "ToastContainer", ClientUIPanelId.NotificationToasts, "TOASTS", font);
            EnsureHudWidget(group, "SystemMessageContainer", ClientUIPanelId.NotificationSystemMessages, "SYSTEM MESSAGES", font);
            EnsureHudWidget(group, "LootNotificationContainer", ClientUIPanelId.NotificationLoot, "LOOT", font);
            EnsureHudWidget(group, "QuestNotificationContainer", ClientUIPanelId.NotificationQuest, "QUEST UPDATE", font);
            EnsureHudWidget(group, "AchievementNotificationContainer", ClientUIPanelId.NotificationAchievement, "ACHIEVEMENT", font);
            EnsureHudWidget(group, "CenterScreenAnnouncement", ClientUIPanelId.NotificationCenterScreen, "ANNOUNCEMENT", font);
        }

        private static void EnsureDeathRespawn(GameObject root, Font font)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "Death & Respawn", ClientUIPanelId.DeathOverlay);
            EnsureOverlay(group, "DeathOverlay", ClientUIPanelId.DeathOverlay, "YOU DIED", true, true, font);
            EnsureWindow(group, "RespawnWindow", ClientUIPanelId.RespawnWindow,
                "RESPAWN", true, true, font, "Respawn location", "Respawn timer", "Respawn action");
            EnsureOverlay(group, "SpectatorOverlay", ClientUIPanelId.SpectatorOverlay,
                "SPECTATOR", true, false, font);
        }

        private static void EnsureChat(GameObject root)
        {
            Transform chat = EnsureTopLevelGroup(root.transform, "Chat", ClientUIPanelId.ChatWindow);
            MarkExisting(chat, ClientUIPanelId.ChatWindow, true, false, true, false);
        }

        private static void EnsureSystemOverlays(GameObject root, Font font)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "System Overlays", ClientUIPanelId.SystemDisconnectOverlay);

            Transform service = FindAnywhere(root.transform, "ServiceNotice");
            if (service != null && service.parent != group)
                service.SetParent(group, false);
            MarkExisting(service, ClientUIPanelId.SystemServiceNotice, false, false, false, false);

            Transform disconnect = FindAnywhere(root.transform, "DisconnectOverlay");
            if (disconnect != null && disconnect.parent != group)
                disconnect.SetParent(group, false);
            MarkExisting(disconnect, ClientUIPanelId.SystemDisconnectOverlay, false, true, false);

            EnsureOverlay(group, "LoadingOverlay", ClientUIPanelId.SystemLoadingOverlay,
                "LOADING", false, true, font);
            EnsureOverlay(group, "ReconnectingOverlay", ClientUIPanelId.SystemReconnectingOverlay,
                "RECONNECTING", false, true, font);
            EnsureOverlay(group, "MaintenanceOverlay", ClientUIPanelId.SystemMaintenanceOverlay,
                "SERVER MAINTENANCE", false, true, font);
            EnsureOverlay(group, "ServerErrorOverlay", ClientUIPanelId.SystemServerErrorOverlay,
                "SERVER ERROR", false, true, font);
        }

        private static void EnsureDebug(GameObject root, Font font)
        {
            RectTransform group = EnsureTopLevelGroup(root.transform, "Debug", ClientUIPanelId.DebugDevelopmentHud);
            EnsureWindow(group, "DevelopmentHud", ClientUIPanelId.DebugDevelopmentHud,
                "DEVELOPMENT HUD", true, false, font, "Build", "Server", "Player", "FPS / timings");
            EnsureWindow(group, "NetworkDiagnostics", ClientUIPanelId.DebugNetworkDiagnostics,
                "NETWORK DIAGNOSTICS", true, false, font, "RTT", "Bytes", "Packets", "AOI", "Snapshots / corrections");
            EnsureWindow(group, "ActorDebug", ClientUIPanelId.DebugActor,
                "ACTOR DEBUG", true, false, font, "Actor ID", "Kind", "State", "Health", "AI / goal", "LOD");
            EnsureWindow(group, "MovementDebug", ClientUIPanelId.DebugMovement,
                "MOVEMENT DEBUG", true, false, font, "Authoritative XYZ", "Client XYZ", "Grounded", "Mode", "Correction", "Ground surface");
            EnsureWindow(group, "InteractionDebug", ClientUIPanelId.DebugInteraction,
                "INTERACTION DEBUG", true, false, font, "Target", "Available actions", "Session", "Reservations / slots");
            EnsureWindow(group, "ServerMapDebug", ClientUIPanelId.DebugServerMap,
                "SERVER MAP DEBUG", true, false, font, "Ground", "Blockers", "Routes", "Portals", "Traversal", "Spawns");
            EnsureWindow(group, "StatsOverlay", ClientUIPanelId.DebugStatsOverlay,
                "STATS OVERLAY", true, false, font, "Runtime stats", "Server metrics", "Client metrics");
        }

        private static void EnsureInventorySurface(GameObject root)
        {
            PlayerInventoryShell shell = root.GetComponent<PlayerInventoryShell>();
            if (shell == null)
                shell = root.AddComponent<PlayerInventoryShell>();
            shell.CaptureAuthoredReferencesForEditor();
            if (shell.AuthoredWindowRootForEditor != null)
                return;

            string prefabPath = FindInventoryPrefabPath();
            if (string.IsNullOrEmpty(prefabPath))
            {
                Debug.LogWarning("[ClientUI] PlayerInventoryShell prefab was not found. Inventory placeholder will still be scaffolded, but current inventory runtime references remain incomplete.");
                return;
            }

            GameObject inventoryPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            GameObject temporary = PrefabUtility.InstantiatePrefab(inventoryPrefab, root.transform) as GameObject;
            if (temporary == null)
                return;
            if (PrefabUtility.IsPartOfPrefabInstance(temporary))
                PrefabUtility.UnpackPrefabInstance(temporary, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            Transform visual = temporary.transform.Find("WindowRoot");
            if (visual != null)
            {
                visual.SetParent(root.transform, false);
                visual.name = "WindowRoot";
            }
            UnityEngine.Object.DestroyImmediate(temporary);
            shell.CaptureAuthoredReferencesForEditor();
        }

        private static string FindInventoryPrefabPath()
        {
            foreach (string guid in AssetDatabase.FindAssets("PlayerInventoryShell t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<PlayerInventoryShell>() != null)
                    return path;
            }
            return string.Empty;
        }

        private static GameObject EnsureWindow(
            Transform parent,
            string name,
            ClientUIPanelId id,
            string title,
            bool gameplayOnly,
            bool modal,
            Font font,
            params string[] plannedSections)
        {
            Transform existing = parent.Find(name);
            GameObject panel;
            if (existing != null)
            {
                panel = existing.gameObject;
            }
            else
            {
                panel = NewImage(name, parent, new Color(0.055f, 0.06f, 0.075f, 0.97f));
                RectTransform rect = panel.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(820f, 620f);
                rect.anchoredPosition = Vector2.zero;

                GameObject header = NewImage("Header", panel.transform, new Color(0.10f, 0.11f, 0.14f, 1f));
                RectTransform headerRect = header.GetComponent<RectTransform>();
                headerRect.anchorMin = new Vector2(0f, 1f);
                headerRect.anchorMax = new Vector2(1f, 1f);
                headerRect.pivot = new Vector2(0.5f, 1f);
                headerRect.sizeDelta = new Vector2(0f, 54f);
                headerRect.anchoredPosition = Vector2.zero;

                Text titleText = NewText("Title", header.transform, title, font, 22, TextAnchor.MiddleLeft);
                RectTransform titleRect = titleText.rectTransform;
                titleRect.anchorMin = Vector2.zero;
                titleRect.anchorMax = Vector2.one;
                titleRect.offsetMin = new Vector2(20f, 0f);
                titleRect.offsetMax = new Vector2(-90f, 0f);

                Button close = NewButton("CloseButton", header.transform, "X", font);
                RectTransform closeRect = close.GetComponent<RectTransform>();
                closeRect.anchorMin = new Vector2(1f, 0.5f);
                closeRect.anchorMax = new Vector2(1f, 0.5f);
                closeRect.pivot = new Vector2(1f, 0.5f);
                closeRect.sizeDelta = new Vector2(48f, 40f);
                closeRect.anchoredPosition = new Vector2(-8f, 0f);
                if (close.GetComponent<ClientUIPanelCloseButton>() == null)
                    close.gameObject.AddComponent<ClientUIPanelCloseButton>();

                GameObject body = NewUi("Body", panel.transform);
                RectTransform bodyRect = body.GetComponent<RectTransform>();
                bodyRect.anchorMin = Vector2.zero;
                bodyRect.anchorMax = Vector2.one;
                bodyRect.offsetMin = new Vector2(18f, 18f);
                bodyRect.offsetMax = new Vector2(-18f, -72f);

                Text outline = NewText("PlannedSurfaceOutline", body.transform,
                    BuildOutlineText(plannedSections), font, 16, TextAnchor.UpperLeft);
                Stretch(outline.rectTransform);
                outline.color = new Color(0.72f, 0.76f, 0.82f, 1f);
                outline.raycastTarget = false;
                panel.SetActive(false);
            }

            ConfigureMarker(panel, id, gameplayOnly, modal, true);
            return panel;
        }

        private static string BuildOutlineText(string[] plannedSections)
        {
            if (plannedSections == null || plannedSections.Length == 0)
                return "AUTHORED SURFACE\n\nPlanned gameplay binding is not wired yet. Edit this prefab directly.";
            return "AUTHORED SURFACE OUTLINE\n\n• " + string.Join("\n• ", plannedSections) +
                   "\n\nThis panel intentionally exists before all gameplay bindings. Edit it directly; do not rebuild it from runtime code.";
        }

        private static void EnsureSection(Transform window, string name, Font font, string description)
        {
            if (window.Find(name) != null)
                return;
            GameObject section = NewUi(name, window);
            RectTransform rect = section.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(620f, 420f);
            rect.anchoredPosition = Vector2.zero;
            Text text = NewText("AuthoringNote", section.transform, description, font, 15, TextAnchor.UpperLeft);
            Stretch(text.rectTransform);
            text.color = new Color(0.65f, 0.69f, 0.75f, 1f);
            section.SetActive(false);
        }

        private static void EnsureHiddenDirectionPlaceholder(Transform parent, Font font)
        {
            // Preserve an already-authored compass exactly as the artist left it. This
            // retired repair utility only supplies a hidden placeholder when one is missing;
            // it must not restyle or disable the production HUD on a later repair pass.
            Transform existing = parent.Find("DirectionBar");
            if (existing != null)
                return;

            GameObject widget = NewImage("DirectionBar", parent, new Color(0.04f, 0.045f, 0.055f, 0.78f));
            RectTransform rect = widget.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(760f, 42f);
            rect.anchoredPosition = new Vector2(0f, -12f);

            Text label = NewText("Label", widget.transform, "DIRECTION / COMPASS", font, 14, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            label.color = new Color(0.75f, 0.78f, 0.84f, 1f);
            label.raycastTarget = false;
            widget.SetActive(false);
        }

        private static void EnsureHudWidget(Transform parent, string name, ClientUIPanelId id, string title, Font font)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
            {
                ConfigureMarker(existing.gameObject, id, true, false, false, false);
                if (id == ClientUIPanelId.HudPlayerCastBar)
                    EnsurePlayerCastBarPresentation(existing, title, font);
                if (id == ClientUIPanelId.HudMinimap)
                    existing.gameObject.SetActive(false);
                return;
            }

            GameObject widget = NewImage(name, parent, new Color(0.04f, 0.045f, 0.055f, 0.78f));
            RectTransform rect = widget.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(260f, 64f);
            rect.anchoredPosition = Vector2.zero;
            Text label = NewText("Label", widget.transform, title, font, 14, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            label.color = new Color(0.75f, 0.78f, 0.84f, 1f);
            label.raycastTarget = false;
            ConfigureMarker(widget, id, true, false, false, false);
            if (id == ClientUIPanelId.HudPlayerCastBar)
                EnsurePlayerCastBarPresentation(widget.transform, title, font);
            widget.SetActive(false);
        }

        private static void EnsurePlayerCastBarPresentation(Transform widget, string title, Font font)
        {
            if (widget == null) return;

            Transform progress = widget.Find("Progress");
            if (progress == null)
            {
                GameObject fill = NewImage("Progress", widget, new Color(0.24f, 0.48f, 0.78f, 0.82f));
                RectTransform fillRect = fill.GetComponent<RectTransform>();
                fillRect.anchorMin = Vector2.zero;
                fillRect.anchorMax = Vector2.one;
                fillRect.offsetMin = Vector2.zero;
                fillRect.offsetMax = Vector2.zero;
                fill.transform.SetAsFirstSibling();
                fill.GetComponent<Image>().raycastTarget = false;
            }

            Transform labelTransform = widget.Find("Label");
            Text label = labelTransform != null ? labelTransform.GetComponent<Text>() : null;
            if (label == null)
            {
                label = NewText("Label", widget, title, font, 14, TextAnchor.MiddleCenter);
                Stretch(label.rectTransform);
            }
            label.text = title;
            label.raycastTarget = false;
        }

        private static GameObject EnsureOverlay(
            Transform parent,
            string name,
            ClientUIPanelId id,
            string title,
            bool gameplayOnly,
            bool modal,
            Font font)
        {
            Transform existing = parent.Find(name);
            GameObject overlay = existing != null ? existing.gameObject : NewImage(name, parent, new Color(0f, 0f, 0f, 0.72f));
            RectTransform rect = overlay.GetComponent<RectTransform>();
            Stretch(rect);
            if (overlay.transform.Find("Title") == null)
            {
                Text text = NewText("Title", overlay.transform, title, font, 28, TextAnchor.MiddleCenter);
                Stretch(text.rectTransform);
                text.raycastTarget = false;
            }
            ConfigureMarker(overlay, id, gameplayOnly, modal, true);
            overlay.SetActive(false);
            return overlay;
        }

        private static RectTransform EnsureTopLevelGroup(
            Transform parent,
            string defaultDisplayName,
            ClientUIPanelId anchorPanelId)
        {
            // Existing authored surfaces are located by their semantic panel marker, not
            // by their hierarchy label. The display name is only used when a genuinely
            // missing group has to be created by this retired Editor repair tool.
            Transform existing = FindTopLevelGroupForPanel(parent, anchorPanelId);
            if (existing != null)
                return existing as RectTransform ?? existing.gameObject.GetComponent<RectTransform>();

            return EnsureGroup(parent, defaultDisplayName);
        }

        private static Transform FindTopLevelGroupForPanel(Transform parent, ClientUIPanelId panelId)
        {
            if (parent == null || panelId == ClientUIPanelId.None)
                return null;

            ClientUIPanelMarker[] markers = parent.GetComponentsInChildren<ClientUIPanelMarker>(true);
            foreach (ClientUIPanelMarker marker in markers)
            {
                if (marker == null || marker.PanelId != panelId)
                    continue;

                Transform current = marker.transform;
                while (current.parent != null && current.parent != parent)
                    current = current.parent;

                return current.parent == parent ? current : null;
            }

            return null;
        }

        private static RectTransform EnsureGroup(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
                return existing as RectTransform ?? existing.gameObject.GetComponent<RectTransform>();
            GameObject group = NewUi(name, parent);
            RectTransform rect = group.GetComponent<RectTransform>();
            Stretch(rect);
            return rect;
        }

        private static void EnsureGroupWithMarker(
            Transform parent,
            string name,
            ClientUIPanelId id,
            bool gameplayOnly,
            bool modal,
            bool closeOnEscape,
            bool managedWindow = true)
        {
            RectTransform group = EnsureGroup(parent, name);
            ConfigureMarker(group.gameObject, id, gameplayOnly, modal, closeOnEscape, managedWindow);
        }

        private static void MarkExisting(Transform transform, ClientUIPanelId id, bool gameplayOnly, bool modal, bool closeOnEscape, bool managedWindow = true)
        {
            if (transform == null)
                return;
            ConfigureMarker(transform.gameObject, id, gameplayOnly, modal, closeOnEscape, managedWindow);
        }

        private static void ConfigureMarker(GameObject gameObject, ClientUIPanelId id, bool gameplayOnly, bool modal, bool closeOnEscape, bool managedWindow = true)
        {
            ClientUIPanelMarker marker = gameObject.GetComponent<ClientUIPanelMarker>();
            if (marker == null)
                marker = gameObject.AddComponent<ClientUIPanelMarker>();
            marker.ConfigureForEditor(id, gameplayOnly, modal, closeOnEscape, managedWindow);
            EditorUtility.SetDirty(marker);
        }

        private static void ConfigurePopupController(GameObject blocker, GameObject popup, ClientPopupController controller, Font font)
        {
            Transform body = popup.transform.Find("Body");
            if (body == null)
                return;

            Text title = popup.transform.Find("Header/Title")?.GetComponent<Text>();
            Text message = body.Find("Message")?.GetComponent<Text>();
            if (message == null)
            {
                message = NewText("Message", body, "Popup message", font, 18, TextAnchor.UpperLeft);
                RectTransform rect = message.rectTransform;
                rect.anchorMin = new Vector2(0f, 0.35f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.offsetMin = new Vector2(10f, 0f);
                rect.offsetMax = new Vector2(-10f, -10f);
            }

            Image icon = body.Find("Icon")?.GetComponent<Image>();
            if (icon == null)
            {
                icon = EnsureImage(body, "Icon", new Color(0.2f, 0.22f, 0.26f, 1f));
                RectTransform rect = icon.rectTransform;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 0f);
                rect.pivot = Vector2.zero;
                rect.sizeDelta = new Vector2(72f, 72f);
                rect.anchoredPosition = new Vector2(10f, 72f);
            }

            InputField input = body.Find("OptionalInput")?.GetComponent<InputField>();
            if (input == null)
            {
                GameObject inputGo = NewImage("OptionalInput", body, new Color(0.09f, 0.10f, 0.12f, 1f));
                input = inputGo.AddComponent<InputField>();
                RectTransform rect = inputGo.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.sizeDelta = new Vector2(0f, 42f);
                rect.anchoredPosition = new Vector2(0f, 88f);
                Text text = NewText("Text", inputGo.transform, string.Empty, font, 16, TextAnchor.MiddleLeft);
                Stretch(text.rectTransform);
                text.rectTransform.offsetMin = new Vector2(10f, 0f);
                text.rectTransform.offsetMax = new Vector2(-10f, 0f);
                input.textComponent = text;
                inputGo.SetActive(false);
            }

            RectTransform buttons = EnsureGroup(body, "Buttons");
            buttons.anchorMin = new Vector2(0f, 0f);
            buttons.anchorMax = new Vector2(1f, 0f);
            buttons.pivot = new Vector2(0.5f, 0f);
            buttons.sizeDelta = new Vector2(0f, 54f);
            buttons.anchoredPosition = Vector2.zero;

            Button primary = EnsurePopupButton(buttons, "PrimaryButton", "OK", font, 0);
            Button secondary = EnsurePopupButton(buttons, "SecondaryButton", "CANCEL", font, 1);
            Button tertiary = EnsurePopupButton(buttons, "TertiaryButton", "OTHER", font, 2);
            Button close = popup.transform.Find("Header/CloseButton")?.GetComponent<Button>();
            if (close != null)
            {
                ClientUIPanelCloseButton genericClose = close.GetComponent<ClientUIPanelCloseButton>();
                if (genericClose != null)
                    UnityEngine.Object.DestroyImmediate(genericClose);
            }
            controller.ConfigureForEditor(
                blocker,
                popup,
                title,
                message,
                icon,
                input,
                close,
                primary,
                primary.GetComponentInChildren<Text>(true),
                secondary,
                secondary.GetComponentInChildren<Text>(true),
                tertiary,
                tertiary.GetComponentInChildren<Text>(true));
            EditorUtility.SetDirty(controller);
        }

        private static Button EnsurePopupButton(RectTransform parent, string name, string label, Font font, int index)
        {
            Transform existing = parent.Find(name);
            Button button = existing != null ? existing.GetComponent<Button>() : NewButton(name, parent, label, font);
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(150f, 42f);
            rect.anchoredPosition = new Vector2(-index * 160f, 0f);
            return button;
        }

        private static Image EnsureImage(Transform parent, string name, Color color)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
            {
                Image image = existing.GetComponent<Image>();
                if (image == null) image = existing.gameObject.AddComponent<Image>();
                return image;
            }
            return NewImage(name, parent, color).GetComponent<Image>();
        }

        private static Transform FindAnywhere(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; ++i)
            {
                Transform found = FindAnywhere(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static GameObject NewUi(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject NewImage(string name, Transform parent, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static Text NewText(string name, Transform parent, string value, Font font, int size, TextAnchor alignment)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = alignment;
            text.text = value;
            text.color = Color.white;
            return text;
        }

        private static Button NewButton(string name, Transform parent, string label, Font font)
        {
            GameObject go = NewImage(name, parent, new Color(0.14f, 0.15f, 0.18f, 1f));
            Button button = go.AddComponent<Button>();
            Text text = NewText("Label", go.transform, label, font, 16, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
            text.raycastTarget = false;
            return button;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static readonly ClientUIPanelId[] RequiredPanelIds =
        {
            ClientUIPanelId.FrontendConnect,
            ClientUIPanelId.FrontendLogin,
            ClientUIPanelId.FrontendCharacterSelect,
            ClientUIPanelId.FrontendCharacterCreate,
            ClientUIPanelId.FrontendCharacterOrigin,
            ClientUIPanelId.FrontendCharacterTraits,
            ClientUIPanelId.FrontendMovementStyle,
            ClientUIPanelId.FrontendServerStatus,
            ClientUIPanelId.FrontendLoading,
            ClientUIPanelId.HudPlayerVitals,
            ClientUIPanelId.HudExperience,
            ClientUIPanelId.HudPlayerCastBar,
            ClientUIPanelId.HudTargetFrame,
            ClientUIPanelId.HudTargetCastBar,
            ClientUIPanelId.HudBuffs,
            ClientUIPanelId.HudDebuffs,
            ClientUIPanelId.HudStatusEffects,
            ClientUIPanelId.HudActionBar,
            ClientUIPanelId.HudActionCombatAim,
            ClientUIPanelId.HudPartyFrames,
            ClientUIPanelId.HudPetFrame,
            ClientUIPanelId.HudObjectiveTracker,
            ClientUIPanelId.HudMinimap,
            ClientUIPanelId.HudClock,
            ClientUIPanelId.HudLatency,
            ClientUIPanelId.HudInteractionPrompt,
            ClientUIPanelId.HudCombatFeedback,
            ClientUIPanelId.HudHeatWanted,
            ClientUIPanelId.HudStatusOrbs,
            ClientUIPanelId.HudPortrait,
            ClientUIPanelId.HudVoiceChat,
            ClientUIPanelId.CharacterWindow,
            ClientUIPanelId.InventoryWindow,
            ClientUIPanelId.SkillsWindow,
            ClientUIPanelId.QuestsWindow,
            ClientUIPanelId.CraftingWindow,
            ClientUIPanelId.ProfessionsWindow,
            ClientUIPanelId.StorageWindow,
            ClientUIPanelId.SocialWindow,
            ClientUIPanelId.TradeWindow,
            ClientUIPanelId.DuelWindow,
            ClientUIPanelId.JournalWindow,
            ClientUIPanelId.CodexWindow,
            ClientUIPanelId.WorldMapWindow,
            ClientUIPanelId.HousingWindow,
            ClientUIPanelId.PlaceablesWindow,
            ClientUIPanelId.HarvestingWindow,
            ClientUIPanelId.MainMenuWindow,
            ClientUIPanelId.SettingsWindow,
            ClientUIPanelId.HelpWindow,
            ClientUIPanelId.ReportPlayerWindow,
            ClientUIPanelId.GameMasterWindow,
            ClientUIPanelId.MusicPlayerWindow,
            ClientUIPanelId.FactionTransitionWindow,
            ClientUIPanelId.LootWindow,
            ClientUIPanelId.PetsWindow,
            ClientUIPanelId.MountsWindow,
            ClientUIPanelId.HeatBountyWindow,
            ClientUIPanelId.EquipmentWindow,
            ClientUIPanelId.AttributesWindow,
            ClientUIPanelId.FriendsWindow,
            ClientUIPanelId.RelationshipsWindow,
            ClientUIPanelId.PartyWindow,
            ClientUIPanelId.GuildWindow,
            ClientUIPanelId.NpcDialogueWindow,
            ClientUIPanelId.NpcMerchantWindow,
            ClientUIPanelId.NpcRepairWindow,
            ClientUIPanelId.NpcQuestWindow,
            ClientUIPanelId.NpcStorageWindow,
            ClientUIPanelId.NpcCraftingWindow,
            ClientUIPanelId.NpcGuildWindow,
            ClientUIPanelId.NpcReviveWindow,
            ClientUIPanelId.NpcGenericServiceWindow,
            ClientUIPanelId.PopulationInteractionWindow,
            ClientUIPanelId.InteractionContextActions,
            ClientUIPanelId.InteractionConsent,
            ClientUIPanelId.InteractionParticipantSelection,
            ClientUIPanelId.InteractionSessionHud,
            ClientUIPanelId.InteractionPrivateSessionControls,
            ClientUIPanelId.InteractionWorldPrompts,
            ClientUIPanelId.InteractionForcedFeedingBreakFree,
            ClientUIPanelId.PopupGeneric,
            ClientUIPanelId.PopupConfirmation,
            ClientUIPanelId.PopupYesNo,
            ClientUIPanelId.PopupAcceptDecline,
            ClientUIPanelId.PopupTextInput,
            ClientUIPanelId.PopupQuantity,
            ClientUIPanelId.PopupItemConfirm,
            ClientUIPanelId.PopupError,
            ClientUIPanelId.PopupWarning,
            ClientUIPanelId.PopupInformation,
            ClientUIPanelId.PopupPartyInvite,
            ClientUIPanelId.PopupGuildInvite,
            ClientUIPanelId.PopupFriendInvite,
            ClientUIPanelId.PopupCoupleInvite,
            ClientUIPanelId.PopupTradeRequest,
            ClientUIPanelId.PopupDuelRequest,
            ClientUIPanelId.NotificationToasts,
            ClientUIPanelId.NotificationSystemMessages,
            ClientUIPanelId.NotificationLoot,
            ClientUIPanelId.NotificationQuest,
            ClientUIPanelId.NotificationAchievement,
            ClientUIPanelId.NotificationCenterScreen,
            ClientUIPanelId.DeathOverlay,
            ClientUIPanelId.RespawnWindow,
            ClientUIPanelId.SpectatorOverlay,
            ClientUIPanelId.ChatWindow,
            ClientUIPanelId.SystemLoadingOverlay,
            ClientUIPanelId.SystemServiceNotice,
            ClientUIPanelId.SystemReconnectingOverlay,
            ClientUIPanelId.SystemDisconnectOverlay,
            ClientUIPanelId.SystemMaintenanceOverlay,
            ClientUIPanelId.SystemServerErrorOverlay,
            ClientUIPanelId.DebugDevelopmentHud,
            ClientUIPanelId.DebugNetworkDiagnostics,
            ClientUIPanelId.DebugActor,
            ClientUIPanelId.DebugMovement,
            ClientUIPanelId.DebugInteraction,
            ClientUIPanelId.DebugServerMap,
            ClientUIPanelId.DebugStatsOverlay
        };
    }
}
#endif
