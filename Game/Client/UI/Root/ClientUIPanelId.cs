namespace Game.Client.UI.Root
{
    /// <summary>
    /// Stable semantic IDs for the authored master UI. These IDs are intentionally
    /// independent from GameObject names and hierarchy paths so artists can freely
    /// reorganize ClientUIRoot.prefab without breaking gameplay bindings.
    ///
    /// The scaffold is deliberately broader than the currently wired gameplay. If a
    /// planned surface is ultimately unused it may be removed later; missing future
    /// surfaces should not force another master-canvas rebuild.
    /// </summary>
    public enum ClientUIPanelId
    {
        None = 0,

        // Frontend / character lifecycle
        FrontendConnect = 100,
        FrontendLogin = 101,
        FrontendCharacterSelect = 102,
        FrontendCharacterCreate = 103,
        FrontendCharacterOrigin = 104,
        FrontendCharacterTraits = 105,
        FrontendMovementStyle = 106,
        FrontendServerStatus = 107,
        FrontendLoading = 108,

        // HUD surfaces
        HudPlayerVitals = 200,
        HudExperience = 201,
        HudPlayerCastBar = 202,
        HudTargetFrame = 203,
        HudTargetCastBar = 204,
        HudBuffs = 205,
        HudDebuffs = 206,
        HudStatusEffects = 207,
        HudActionBar = 208,
        HudActionCombatAim = 209,
        HudPartyFrames = 210,
        HudPetFrame = 211,
        HudObjectiveTracker = 212,
        HudMinimap = 213,
        HudClock = 214,
        HudLatency = 215,
        HudInteractionPrompt = 216,
        HudCombatFeedback = 217,
        HudHeatWanted = 218,
        HudStatusOrbs = 219,
        HudPortrait = 220,
        HudVoiceChat = 221,

        // Main gameplay windows
        CharacterWindow = 300,
        InventoryWindow = 301,
        SkillsWindow = 302,
        QuestsWindow = 303,
        CraftingWindow = 304,
        ProfessionsWindow = 305,
        StorageWindow = 306,
        SocialWindow = 307,
        TradeWindow = 308,
        DuelWindow = 309,
        JournalWindow = 310,
        CodexWindow = 311,
        WorldMapWindow = 312,
        HousingWindow = 313,
        PlaceablesWindow = 314,
        HarvestingWindow = 315,
        MainMenuWindow = 316,
        SettingsWindow = 317,
        HelpWindow = 318,
        ReportPlayerWindow = 319,
        GameMasterWindow = 320,
        MusicPlayerWindow = 321,
        FactionTransitionWindow = 322,
        LootWindow = 323,
        PetsWindow = 324,
        MountsWindow = 325,
        HeatBountyWindow = 326,
        EquipmentWindow = 327,
        AttributesWindow = 328,
        FriendsWindow = 329,
        RelationshipsWindow = 330,
        PartyWindow = 331,
        GuildWindow = 332,

        // NPC/service windows
        NpcDialogueWindow = 400,
        NpcMerchantWindow = 401,
        NpcRepairWindow = 402,
        NpcQuestWindow = 403,
        NpcStorageWindow = 404,
        NpcCraftingWindow = 405,
        NpcGuildWindow = 406,
        NpcReviveWindow = 407,
        NpcGenericServiceWindow = 408,
        PopulationInteractionWindow = 409,

        // Interaction/session UI
        InteractionContextActions = 500,
        InteractionConsent = 501,
        InteractionParticipantSelection = 502,
        InteractionSessionHud = 503,
        InteractionPrivateSessionControls = 504,
        InteractionWorldPrompts = 505,
        InteractionForcedFeedingBreakFree = 506,

        // Popups / request modals
        PopupGeneric = 600,
        PopupConfirmation = 601,
        PopupYesNo = 602,
        PopupAcceptDecline = 603,
        PopupTextInput = 604,
        PopupQuantity = 605,
        PopupItemConfirm = 606,
        PopupError = 607,
        PopupWarning = 608,
        PopupInformation = 609,
        PopupPartyInvite = 610,
        PopupGuildInvite = 611,
        PopupFriendInvite = 612,
        PopupCoupleInvite = 613,
        PopupTradeRequest = 614,
        PopupDuelRequest = 615,

        // Notifications / transient presentation
        NotificationToasts = 700,
        NotificationSystemMessages = 701,
        NotificationLoot = 702,
        NotificationQuest = 703,
        NotificationAchievement = 704,
        NotificationCenterScreen = 705,

        // Death / spectator
        DeathOverlay = 800,
        RespawnWindow = 801,
        SpectatorOverlay = 802,

        // Chat
        ChatWindow = 900,

        // System overlays
        SystemLoadingOverlay = 1000,
        SystemServiceNotice = 1001,
        SystemReconnectingOverlay = 1002,
        SystemDisconnectOverlay = 1003,
        SystemMaintenanceOverlay = 1004,
        SystemServerErrorOverlay = 1005,

        // Development/staff/debug
        DebugDevelopmentHud = 1100,
        DebugNetworkDiagnostics = 1101,
        DebugActor = 1102,
        DebugMovement = 1103,
        DebugInteraction = 1104,
        DebugServerMap = 1105,
        DebugStatsOverlay = 1106
    }
}
