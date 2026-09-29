using System;
using LiteNetLib.Utils;
using NUnit.Framework;
using Player.Networking;

namespace Testing.Player.Tests
{
    public sealed class PlayerItemNetworkMessageTests
    {
        private const ushort ChestSlotDataId = 11;
        private const ushort MainHandSlotDataId = 12;
        private const ushort AmmoItemDataId = 101;
        private const ushort ChestItemDataId = 102;

        [SetUp]
        public void SetUp()
        {
            PlayerGameplaySettingsRuntime.Reset();
            Assert.IsTrue(PlayerGameplaySettingsRuntime.ApplySnapshot(
                new GameplaySettingsSnapshotMessage
                {
                    success = true,
                    revision = 1,
                    resourceRates = Array.Empty<GameplayResourceRateWire>(),
                    abilities = Array.Empty<GameplayAbilityReferenceWire>(),
                    statuses = Array.Empty<GameplayStatusReferenceWire>(),
                    equipmentSlots = new[]
                    {
                        new GameplayEquipmentSlotReferenceWire
                        {
                            dataId = ChestSlotDataId,
                            slotId = "Chest",
                            displayName = "Chest",
                            order = 20,
                        },
                        new GameplayEquipmentSlotReferenceWire
                        {
                            dataId = MainHandSlotDataId,
                            slotId = "MainHand",
                            displayName = "Main Hand",
                            order = 10,
                        },
                    },
                    items = new[]
                    {
                        new GameplayItemReferenceWire
                        {
                            dataId = AmmoItemDataId,
                            definitionId = "item.ammo.9mm.standard",
                            displayName = "9mm Standard Ammo",
                            unitWeight = 0.012f,
                            allowedSlotDataIds = Array.Empty<ushort>(),
                        },
                        new GameplayItemReferenceWire
                        {
                            dataId = ChestItemDataId,
                            definitionId = "item.armor.leather_chest",
                            displayName = "Leather Chest Armor",
                            maxDurability = 120,
                            unitWeight = 5f,
                            allowedSlotDataIds = new[] { ChestSlotDataId },
                        },
                    },
                }));
        }

        [TearDown]
        public void TearDown() => PlayerGameplaySettingsRuntime.Reset();

        [Test]
        public void PlayerItemsResponse_RoundTripsAuthoritativeInventoryAndEquipmentState()
        {
            var source = new PlayerItemsResponseMessage
            {
                success = true,
                status = 1,
                error = string.Empty,
                contentRevision = 12,
                inventoryRevision = 7,
                equipmentRevision = 3,
                inventoryCapacity = 40,
                inventoryWeight = 5.5f,
                armor = 25f,
                attackPower = 4f,
                inventory = new[]
                {
                    new PlayerItemWire
                    {
                        inventorySlot = 2,
                        itemInstanceId = 1001,
                        itemDataId = AmmoItemDataId,
                        quantity = 30,
                        durability = 0,
                    },
                },
                equipment = new[]
                {
                    new EquipmentSlotWire
                    {
                        slotDataId = ChestSlotDataId,
                        hasItem = true,
                        item = new PlayerItemWire
                        {
                            inventorySlot = -1,
                            equipmentSlotDataId = ChestSlotDataId,
                            itemInstanceId = 1002,
                            itemDataId = ChestItemDataId,
                            quantity = 1,
                            durability = 120,
                        },
                    },
                },
            };

            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new PlayerItemsResponseMessage();
            copy.Deserialize(reader);

            Assert.IsTrue(copy.success);
            Assert.AreEqual(12, copy.contentRevision);
            Assert.AreEqual(40, copy.inventoryCapacity);
            Assert.AreEqual(1, copy.inventory.Length);
            Assert.AreEqual(1001, copy.inventory[0].itemInstanceId);
            Assert.AreEqual(AmmoItemDataId, copy.inventory[0].itemDataId);
            Assert.AreEqual("item.ammo.9mm.standard", copy.inventory[0].definitionId);
            Assert.AreEqual(30, copy.inventory[0].quantity);
            Assert.AreEqual(1, copy.equipment.Length);
            Assert.AreEqual(ChestSlotDataId, copy.equipment[0].slotDataId);
            Assert.AreEqual("Chest", copy.equipment[0].slotId);
            Assert.IsTrue(copy.equipment[0].hasItem);
            Assert.AreEqual(ChestItemDataId, copy.equipment[0].item.itemDataId);
            Assert.AreEqual("Chest", copy.equipment[0].item.equipmentSlotId);
            Assert.AreEqual(25f, copy.armor);
        }

        [Test]
        public void EquipRequest_RoundTripsServerValidatedSlotIntent()
        {
            var source = new EquipItemRequestMessage
            {
                inventoryIndex = 17,
                equipmentSlotDataId = MainHandSlotDataId,
                equipmentSlotId = "MainHand",
            };

            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new EquipItemRequestMessage();
            copy.Deserialize(reader);

            Assert.AreEqual(17, copy.inventoryIndex);
            Assert.AreEqual(MainHandSlotDataId, copy.equipmentSlotDataId);
            Assert.AreEqual(string.Empty, copy.equipmentSlotId);
        }
    }
}
