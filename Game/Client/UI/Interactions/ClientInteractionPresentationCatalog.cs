using System;
using System.Collections.Generic;
using Game.Shared.Interactions;

namespace Game.Client.UI.Interactions
{
    /// <summary>
    /// Client presentation metadata only. It does not grant actions. Availability is
    /// supplied by current client providers and every executed action is revalidated by
    /// the authoritative GameServer.
    /// </summary>
    public static class ClientInteractionPresentationCatalog
    {
        private readonly struct Entry
        {
            public readonly string Label;
            public readonly string Category;
            public readonly string Description;
            public readonly InteractionConsentMode Consent;
            public readonly InteractionContentLevel Content;
            public readonly int Sort;

            public Entry(
                string label,
                string category,
                string description,
                InteractionConsentMode consent = InteractionConsentMode.None,
                InteractionContentLevel content = InteractionContentLevel.General,
                int sort = 0)
            {
                Label = label;
                Category = category;
                Description = description;
                Consent = consent;
                Content = content;
                Sort = sort;
            }
        }

        private static readonly Dictionary<InteractionActionId, Entry> Entries =
            new Dictionary<InteractionActionId, Entry>
            {
                { InteractionActionId.Inspect, new Entry("Inspect", "General", "Inspect the selected target.", sort: 10) },
                { InteractionActionId.Talk, new Entry("Talk", "General", "Begin a conversation with the target.", sort: 20) },
                { InteractionActionId.Use, new Entry("Use", "General", "Use the selected world object.", sort: 30) },
                { InteractionActionId.Open, new Entry("Open", "General", "Open the selected object.", sort: 40) },
                { InteractionActionId.Loot, new Entry("Loot", "World", "Pick up the selected world item.", sort: 10) },
                { InteractionActionId.OpenContainer, new Entry("Open Container", "World", "Open a container and inspect its contents.", sort: 20) },
                { InteractionActionId.Unlock, new Entry("Unlock", "World", "Attempt to unlock the selected object.", sort: 30) },
                { InteractionActionId.Examine, new Entry("Examine", "World", "Examine the selected object more closely.", sort: 40) },
                { InteractionActionId.Harvest, new Entry("Harvest", "World", "Gather resources from the selected node.", sort: 50) },

                { InteractionActionId.TradeRequest, new Entry("Trade", "Social", "Request a player-to-player trade.", InteractionConsentMode.TargetAcceptance, sort: 10) },
                { InteractionActionId.PartyInvite, new Entry("Invite to Party", "Social", "Invite the player to your party.", InteractionConsentMode.TargetAcceptance, sort: 20) },
                { InteractionActionId.GuildInvite, new Entry("Invite to Guild", "Social", "Invite the player to your guild.", InteractionConsentMode.TargetAcceptance, sort: 30) },
                { InteractionActionId.DuelRequest, new Entry("Duel", "Social", "Challenge the player to a duel.", InteractionConsentMode.TargetAcceptance, sort: 40) },
                { InteractionActionId.Whisper, new Entry("Whisper", "Social", "Open a private message to the player.", sort: 50) },
                { InteractionActionId.ReportPlayer, new Entry("Report Player", "Social", "Open the player-report flow.", InteractionConsentMode.Confirmation, sort: 90) },
                { InteractionActionId.AddFriend, new Entry("Add Friend", "Social/Friends", "Send a friend request.", InteractionConsentMode.TargetAcceptance, sort: 10) },
                { InteractionActionId.RemoveFriend, new Entry("Remove Friend", "Social/Friends", "Remove this player from your friends list.", InteractionConsentMode.Confirmation, sort: 20) },
                { InteractionActionId.GiftFriend, new Entry("Send Gift", "Social/Friends", "Send an eligible gift to this friend.", InteractionConsentMode.Confirmation, sort: 30) },
                { InteractionActionId.CoupleInvite, new Entry("Couple Request", "Social/Relationship", "Request a persistent couple relationship.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 10) },
                { InteractionActionId.Divorce, new Entry("End Relationship", "Social/Relationship", "End the current couple relationship.", InteractionConsentMode.Confirmation, InteractionContentLevel.Mature, 20) },

                { InteractionActionId.OpenStorage, new Entry("Storage", "Services", "Open available character storage.", sort: 10) },
                { InteractionActionId.OpenQuests, new Entry("Quests", "Services", "Open available quests or objectives.", sort: 20) },
                { InteractionActionId.Teleport, new Entry("Travel", "Services", "Use an available travel or teleport service.", InteractionConsentMode.Confirmation, sort: 30) },
                { InteractionActionId.Revive, new Entry("Revive", "Services", "Attempt to revive an eligible target.", sort: 40) },
                { InteractionActionId.Craft, new Entry("Craft", "Services", "Use the selected crafting service or station.", sort: 50) },

                { InteractionActionId.SenseBlood, new Entry("Sense Blood", "Vampire/Blood", "Sense blood-related information about the target.", sort: 10) },
                { InteractionActionId.InspectBlood, new Entry("Inspect Blood", "Vampire/Blood", "Inspect blood quality or state.", sort: 20) },
                { InteractionActionId.RequestBlood, new Entry("Request Blood", "Vampire/Blood", "Request blood from an eligible target.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 30) },
                { InteractionActionId.OfferBlood, new Entry("Offer Blood", "Vampire/Blood", "Offer blood to an eligible target.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 40) },
                { InteractionActionId.OfferProtection, new Entry("Offer Protection", "Vampire/Social", "Offer protection to the target.", InteractionConsentMode.TargetAcceptance, sort: 10) },
                { InteractionActionId.Feed, new Entry("Feed", "Vampire/Blood", "Begin an eligible feeding interaction.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 50) },
                { InteractionActionId.Intimidate, new Entry("Intimidate", "Vampire/Influence", "Attempt to intimidate the target.", sort: 10) },
                { InteractionActionId.Charm, new Entry("Charm", "Vampire/Influence", "Attempt to charm the target.", sort: 20) },
                { InteractionActionId.Dominate, new Entry("Dominate", "Vampire/Influence", "Attempt a domination action.", sort: 30) },
                { InteractionActionId.Recruit, new Entry("Recruit", "Vampire/Retainers", "Recruit an eligible target.", sort: 10) },
                { InteractionActionId.Enthrall, new Entry("Enthrall", "Vampire/Retainers", "Enthrall an eligible target.", sort: 20) },
                { InteractionActionId.BloodBond, new Entry("Blood Bond", "Vampire/Retainers", "Create or advance a blood bond.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 30) },
                { InteractionActionId.CreateGhoul, new Entry("Create Ghoul", "Vampire/Retainers", "Create a ghoul from an eligible target.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 40) },
                { InteractionActionId.ReleaseBond, new Entry("Release Bond", "Vampire/Retainers", "Release an existing bond.", InteractionConsentMode.Confirmation, sort: 50) },
                { InteractionActionId.OfferTurning, new Entry("Offer Turning", "Vampire/Progeny", "Offer an eligible target the turning process.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 10) },
                { InteractionActionId.TurnIntoVampire, new Entry("Turn", "Vampire/Progeny", "Complete an authorized turning interaction.", InteractionConsentMode.MutualOptIn, InteractionContentLevel.Mature, 20) },
                { InteractionActionId.MentorProgeny, new Entry("Mentor", "Vampire/Progeny", "Mentor your progeny.", sort: 30) },
                { InteractionActionId.CommandProgeny, new Entry("Command Progeny", "Vampire/Progeny", "Issue an eligible command to progeny.", sort: 40) },
                { InteractionActionId.ReleaseProgeny, new Entry("Release Progeny", "Vampire/Progeny", "Release progeny from an existing relationship.", InteractionConsentMode.Confirmation, sort: 50) },
                { InteractionActionId.CommandFollow, new Entry("Follow", "Vampire/Commands", "Command the target to follow.", sort: 10) },
                { InteractionActionId.CommandStay, new Entry("Stay", "Vampire/Commands", "Command the target to stay.", sort: 20) },
                { InteractionActionId.CommandGuard, new Entry("Guard", "Vampire/Commands", "Command the target to guard.", sort: 30) },
                { InteractionActionId.CommandWork, new Entry("Work", "Vampire/Commands", "Command the target to work.", sort: 40) },
                { InteractionActionId.CommandFeed, new Entry("Command Feed", "Vampire/Commands", "Command an eligible servant to feed.", sort: 50) },
                { InteractionActionId.CommandReturnHome, new Entry("Return Home", "Vampire/Commands", "Command the target to return home.", sort: 60) },
                { InteractionActionId.DismissServant, new Entry("Dismiss", "Vampire/Commands", "Dismiss an eligible servant.", InteractionConsentMode.Confirmation, sort: 70) },
                { InteractionActionId.Rescue, new Entry("Rescue", "Vampire/Social", "Rescue an eligible target.", sort: 20) },
                { InteractionActionId.VampireEmbrace, new Entry("Vampire Embrace", "Vampire/Social", "Begin an eligible vampire embrace interaction.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 30) },

                { InteractionActionId.Scan, new Entry("Scan", "Hunter/Investigation", "Scan the target for useful evidence.", sort: 10) },
                { InteractionActionId.CollectEvidence, new Entry("Collect Evidence", "Hunter/Investigation", "Collect eligible evidence from the target or scene.", sort: 20) },
                { InteractionActionId.CollectSample, new Entry("Collect Sample", "Hunter/Investigation", "Collect an eligible sample.", sort: 30) },
                { InteractionActionId.PhotographEvidence, new Entry("Photograph", "Hunter/Investigation", "Photograph evidence for later use.", sort: 40) },
                { InteractionActionId.Question, new Entry("Question", "Hunter/Investigation", "Question the target.", sort: 50) },
                { InteractionActionId.InterviewWitness, new Entry("Interview Witness", "Hunter/Investigation", "Interview an eligible witness.", sort: 60) },
                { InteractionActionId.MarkSuspect, new Entry("Mark Suspect", "Hunter/Investigation", "Mark the target as a suspect.", sort: 70) },
                { InteractionActionId.BeginTracking, new Entry("Begin Tracking", "Hunter/Investigation", "Begin tracking an eligible target.", sort: 80) },
                { InteractionActionId.SearchTarget, new Entry("Search", "Hunter/Control", "Search an eligible target.", sort: 10) },
                { InteractionActionId.Restrain, new Entry("Restrain", "Hunter/Control", "Restrain an eligible target.", sort: 20) },
                { InteractionActionId.ReleaseRestraint, new Entry("Release", "Hunter/Control", "Release an existing restraint.", sort: 30) },
                { InteractionActionId.ApplyWard, new Entry("Apply Ward", "Hunter/Control", "Apply an eligible ward.", sort: 40) },
                { InteractionActionId.TestSupernaturalTrace, new Entry("Test Trace", "Hunter/Control", "Test a supernatural trace.", sort: 50) },
                { InteractionActionId.ConfiscateEvidence, new Entry("Confiscate Evidence", "Hunter/Control", "Confiscate eligible evidence.", sort: 60) },
                { InteractionActionId.TransferToCell, new Entry("Transfer to Cell", "Hunter/Control", "Transfer an eligible restrained target.", sort: 70) },
                { InteractionActionId.RecruitInformant, new Entry("Recruit Informant", "Hunter/Intel", "Recruit an eligible informant.", sort: 10) },
                { InteractionActionId.RequestCooperation, new Entry("Request Cooperation", "Hunter/Intel", "Request cooperation from the target.", InteractionConsentMode.TargetAcceptance, sort: 20) },
                { InteractionActionId.ShareIntel, new Entry("Share Intel", "Hunter/Intel", "Share intelligence with an eligible target.", InteractionConsentMode.TargetAcceptance, sort: 30) },
                { InteractionActionId.ReportToCell, new Entry("Report to Cell", "Hunter/Intel", "Report intelligence to your cell.", sort: 40) },

                { InteractionActionId.OpenEmotes, new Entry("Emotes", "Emotes", "Open available contextual emotes.", sort: 10) },
                { InteractionActionId.PlayTargetedEmote, new Entry("Targeted Emote", "Emotes", "Play an eligible emote toward the target.", sort: 20) },
                { InteractionActionId.PartnerDance, new Entry("Partner Dance", "Emotes/Partner", "Request a synchronized partner dance.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.General, 10) },
                { InteractionActionId.Hug, new Entry("Hug", "Emotes/Partner", "Request a synchronized hug.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.General, 20) },
                { InteractionActionId.Kiss, new Entry("Kiss", "Emotes/Partner", "Request a synchronized kiss.", InteractionConsentMode.TargetAcceptance, InteractionContentLevel.Mature, 30) },
                { InteractionActionId.RequestAdultSession, new Entry("Private Interaction", "Private", "Request an eligible private adult interaction session.", InteractionConsentMode.MutualOptIn, InteractionContentLevel.Adult, 10) },
                { InteractionActionId.EndAdultSession, new Entry("End Private Interaction", "Private", "End the current private interaction session.", InteractionConsentMode.None, InteractionContentLevel.Adult, 20) },
            };

        public static string Label(InteractionActionId actionId) =>
            Entries.TryGetValue(actionId, out Entry entry) ? entry.Label : Humanize(actionId.ToString());

        public static InteractionCategoryId CategoryId(InteractionActionId actionId) =>
            InteractionCategoryCatalog.DefaultForAction(actionId);

        public static string Category(InteractionActionId actionId) =>
            InteractionCategoryCatalog.Label(CategoryId(actionId));

        public static string Description(InteractionActionId actionId) =>
            Entries.TryGetValue(actionId, out Entry entry) ? entry.Description : string.Empty;

        public static InteractionConsentMode Consent(InteractionActionId actionId) =>
            Entries.TryGetValue(actionId, out Entry entry) ? entry.Consent : InteractionConsentMode.None;

        public static InteractionContentLevel Content(InteractionActionId actionId) =>
            Entries.TryGetValue(actionId, out Entry entry) ? entry.Content : InteractionContentLevel.General;

        public static int SortOrder(InteractionActionId actionId) =>
            Entries.TryGetValue(actionId, out Entry entry) ? entry.Sort : 1000 + (int)actionId;

        private static string Humanize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = new List<char>(value.Length + 8) { value[0] };
            for (int i = 1; i < value.Length; ++i)
            {
                char c = value[i];
                if (char.IsUpper(c) && !char.IsUpper(value[i - 1])) chars.Add(' ');
                chars.Add(c);
            }
            return new string(chars.ToArray());
        }
    }
}
