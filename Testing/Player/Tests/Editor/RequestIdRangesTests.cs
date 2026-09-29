using System.Collections.Generic;
using LiteNetLibManager;
using NUnit.Framework;
using Player.Networking;

namespace Testing.Player.Tests
{
    public sealed class RequestIdRangesTests
    {
        [Test]
        public void RequestIdRanges_AreContiguousNonOverlappingAndCoverUShort()
        {
            RequestIdRange[] ranges = RequestIdRanges.All;
            Assert.IsNotEmpty(ranges);
            Assert.AreEqual(0, (int)ranges[0].Start);
            Assert.AreEqual((int)ushort.MaxValue, (int)ranges[ranges.Length - 1].End);

            for (int i = 0; i < ranges.Length; ++i)
            {
                Assert.LessOrEqual(ranges[i].Start, ranges[i].End, ranges[i].Name);
                if (i == 0)
                    continue;

                Assert.AreEqual(
                    (int)ranges[i - 1].End + 1,
                    (int)ranges[i].Start,
                    $"Gap or overlap between {ranges[i - 1]} and {ranges[i]}");
            }
        }

        [Test]
        public void ExistingRequestIds_AreUniqueAndStayInTheirGrandfatheredRanges()
        {
            var seen = new HashSet<ushort>();

            Add(seen, GameReqTypes.EnterGame, RequestIdRanges.Core.Start, RequestIdRanges.Core.End, "EnterGame");
            Add(seen, GameReqTypes.ClientReady, RequestIdRanges.Core.Start, RequestIdRanges.Core.End, "ClientReady");
            Add(seen, GameReqTypes.ClientNotReady, RequestIdRanges.Core.Start, RequestIdRanges.Core.End, "ClientNotReady");

            Add(seen, CharacterSessionRequestTypes.CharacterList, RequestIdRanges.CharacterAccount.Start, RequestIdRanges.CharacterAccount.End, "CharacterList");
            Add(seen, CharacterSessionRequestTypes.EnterCharacter, RequestIdRanges.CharacterAccount.Start, RequestIdRanges.CharacterAccount.End, "EnterCharacter");
            Add(seen, CharacterSessionRequestTypes.AuthenticateAdmission, RequestIdRanges.CharacterAccount.Start, RequestIdRanges.CharacterAccount.End, "AuthenticateAdmission");
            Add(seen, CharacterSessionRequestTypes.ReservedLegacyCreateAccount, RequestIdRanges.CharacterAccount.Start, RequestIdRanges.CharacterAccount.End, "ReservedLegacyCreateAccount");
            Add(seen, CharacterSessionRequestTypes.CreateCharacter, RequestIdRanges.CharacterAccount.Start, RequestIdRanges.CharacterAccount.End, "CreateCharacter");
            Add(seen, CharacterSessionRequestTypes.DeleteCharacter, RequestIdRanges.CharacterAccount.Start, RequestIdRanges.CharacterAccount.End, "DeleteCharacter");

            Add(seen, PlayerItemRequestTypes.Snapshot, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "ItemSnapshot");
            Add(seen, PlayerItemRequestTypes.MoveInventory, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "MoveInventory");
            Add(seen, PlayerItemRequestTypes.Equip, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "Equip");
            Add(seen, PlayerItemRequestTypes.Unequip, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "Unequip");
            Add(seen, PlayerItemRequestTypes.Use, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "UseItem");
            Add(seen, PlayerItemRequestTypes.Drop, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "DropItem");
            Add(seen, WorldItemRequestTypes.Snapshot, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "WorldItemSnapshot");
            Add(seen, WorldItemRequestTypes.Loot, RequestIdRanges.InventoryEquipmentWorldItems.Start, RequestIdRanges.InventoryEquipmentWorldItems.End, "WorldItemLoot");

            Add(seen, PlayerResourceRequestTypes.Snapshot, RequestIdRanges.CharacterVitals.Start, RequestIdRanges.CharacterVitals.End, "ResourceSnapshot");
            Add(seen, ProgressionRequestTypes.Snapshot, RequestIdRanges.Progression.Start, RequestIdRanges.Progression.End, "ProgressionSnapshot");
            Add(seen, PlayerStatusEffectRequestTypes.Snapshot, RequestIdRanges.StatusEffects.Start, RequestIdRanges.StatusEffects.End, "StatusEffectSnapshot");

            Add(seen, PlayerGameplayActionRequestTypes.BeginAbility, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "BeginAbility");
            Add(seen, PlayerGameplayActionRequestTypes.CancelAbility, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "CancelAbility");
            Add(seen, PlayerGameplayActionRequestTypes.Interaction, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "LegacyInteraction");
            Add(seen, PlayerGameplayActionRequestTypes.Respawn, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "Respawn");
            Add(seen, PlayerGameplayActionRequestTypes.InteractionMenu, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "LegacyInteractionMenu");
            Add(seen, PlayerGameplayActionRequestTypes.ContextInteraction, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "LegacyContextInteraction");
            Add(seen, PlayerGameplayActionRequestTypes.WorldLootOpen, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "WorldLootOpen");
            Add(seen, PlayerGameplayActionRequestTypes.WorldLootTake, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "WorldLootTake");
            Add(seen, PlayerGameplayActionRequestTypes.WorldLootTakeAll, RequestIdRanges.CharacterActions.Start, RequestIdRanges.CharacterActions.End, "WorldLootTakeAll");

            Add(seen, CraftingRequestTypes.Craft, RequestIdRanges.Crafting.Start, RequestIdRanges.Crafting.End, "Craft");

            Add(seen, StaffRequestTypes.Status, RequestIdRanges.Staff.Start, RequestIdRanges.Staff.End, "StaffStatus");
            Add(seen, StaffRequestTypes.SetVisibility, RequestIdRanges.Staff.Start, RequestIdRanges.Staff.End, "StaffVisibility");
            Add(seen, StaffRequestTypes.Spectate, RequestIdRanges.Staff.Start, RequestIdRanges.Staff.End, "StaffSpectate");
            Add(seen, StaffRequestTypes.StopSpectate, RequestIdRanges.Staff.Start, RequestIdRanges.Staff.End, "StaffStopSpectate");
        }

        [Test]
        public void NewInteractionProtocolRange_IsReservedOutsideLegacyCharacterActionIds()
        {
            Assert.AreEqual(1300, RequestIdRanges.Interactions.Start);
            Assert.AreEqual(1399, RequestIdRanges.Interactions.End);
            Assert.AreEqual(RequestIdRanges.CharacterActions.Start, RequestIdRanges.Find(PlayerGameplayActionRequestTypes.Interaction).Start);
            Assert.IsFalse(RequestIdRanges.Interactions.Start <= PlayerGameplayActionRequestTypes.Interaction &&
                           PlayerGameplayActionRequestTypes.Interaction <= RequestIdRanges.Interactions.End);
        }

        private static void Add(HashSet<ushort> seen, ushort id, ushort start, ushort end, string name)
        {
            Assert.That(id, Is.InRange(start, end), $"{name} ({id}) is outside its assigned request-id range");
            Assert.IsTrue(seen.Add(id), $"Duplicate request id {id} detected at {name}");
        }
    }
}
