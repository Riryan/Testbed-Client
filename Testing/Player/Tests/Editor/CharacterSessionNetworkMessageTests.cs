using Game.Shared.Characters;
using LiteNetLib.Utils;
using NUnit.Framework;
using Player.Networking;

namespace Testing.Player.Tests
{
    public sealed class CharacterSessionNetworkMessageTests
    {
        [Test]
        public void CharacterListResponse_RoundTripsBoundedSummaries()
        {
            var source = new CharacterListResponseMessage
            {
                success = true,
                sessionState = 2,
                error = string.Empty,
                characters = new[]
                {
                    new CharacterSessionCharacterSummary(11, "Alpha", "map-a"),
                    new CharacterSessionCharacterSummary(12, "Bravo", "map-b"),
                },
            };

            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new CharacterListResponseMessage();
            copy.Deserialize(reader);

            Assert.IsTrue(copy.success);
            Assert.AreEqual(2, copy.characters.Length);
            Assert.AreEqual(11, copy.characters[0].characterId);
            Assert.AreEqual("Alpha", copy.characters[0].name);
            Assert.AreEqual("map-b", copy.characters[1].mapId);
        }


        [Test]
        public void CreateCharacterRequest_RoundTripsCompactInitialAppearance()
        {
            var source = new CreateCharacterRequestMessage
            {
                name = "Visual Hero",
                initialAppearance = new CharacterAppearanceRecipe
                {
                    schemaVersion = CharacterAppearanceRecipe.CurrentSchemaVersion,
                    visualProfileId = 2,
                    revision = 0,
                    meshes = new[] { new CharacterMeshSelection(1, 35), new CharacterMeshSelection(7, 4) },
                    morphs = new[] { new CharacterMorphSelection(3, 192) },
                    colors = new[] { new CharacterColorSelection(2, CharacterColorEncoding.PaletteIndex, 12) },
                },
                initialPresentation = new CharacterPresentationPreferences
                {
                    schemaVersion = CharacterPresentationPreferences.CurrentSchemaVersion,
                    revision = 0,
                    movementStyle = 192,
                },
            };

            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new CreateCharacterRequestMessage();
            copy.Deserialize(reader);

            Assert.AreEqual("Visual Hero", copy.name);
            Assert.AreEqual(2, copy.initialAppearance.visualProfileId);
            Assert.AreEqual(2, copy.initialAppearance.meshes.Length);
            Assert.AreEqual(35, copy.initialAppearance.meshes[0].optionId);
            Assert.AreEqual(192, copy.initialAppearance.morphs[0].value);
            Assert.AreEqual(12u, copy.initialAppearance.colors[0].value);
            Assert.AreEqual(192, copy.initialPresentation.movementStyle);
            Assert.Less(writer.Length, 72, "Basic appearance creation request should remain a bytes-scale payload.");
        }

        [Test]
        public void PlayerEntityAppearance_RoundTripsWithoutJsonPresentationPayload()
        {
            var source = new PlayerEntityAppearance
            {
                generation = 7,
                sequence = 12,
                characterId = 9988,
                equipmentVersion = 4,
                displayName = "Alpha",
                equipmentVisuals = new[]
                {
                    new PlayerEquipmentVisualSelection(2, 201),
                },
                guildName = "Guild",
                appearance = new CharacterAppearanceRecipe
                {
                    schemaVersion = CharacterAppearanceRecipe.CurrentSchemaVersion,
                    visualProfileId = 2,
                    revision = 9,
                    meshes = new[] { new CharacterMeshSelection(1, 2) },
                    morphs = new[] { new CharacterMorphSelection(4, 128) },
                    colors = new[] { new CharacterColorSelection(5, CharacterColorEncoding.Rgba32, 0xFF8844FFu) },
                },
                presentation = new CharacterPresentationPreferences
                {
                    schemaVersion = CharacterPresentationPreferences.CurrentSchemaVersion,
                    revision = 3,
                    movementStyle = 224,
                },
            };

            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new PlayerEntityAppearance();
            copy.Deserialize(reader);

            Assert.AreEqual(source.characterId, copy.characterId);
            Assert.AreEqual(1, copy.equipmentVisuals.Length);
            Assert.AreEqual(2, copy.equipmentVisuals[0].equipmentSlotPresentationId);
            Assert.AreEqual(201, copy.equipmentVisuals[0].itemPresentationId);
            Assert.AreEqual(9u, copy.appearance.revision);
            Assert.AreEqual(128, copy.appearance.morphs[0].value);
            Assert.AreEqual(0xFF8844FFu, copy.appearance.colors[0].value);
            Assert.AreEqual(224, copy.presentation.movementStyle);
            Assert.Less(writer.Length, 112, "Small in-world appearance definitions with equipment should remain bytes-scale.");
        }

        [Test]
        public void EnterCharacterRequest_RoundTripsExactCharacterId()
        {
            var source = new EnterCharacterRequestMessage { characterId = 99887766 };
            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new EnterCharacterRequestMessage();
            copy.Deserialize(reader);

            Assert.AreEqual(source.characterId, copy.characterId);
        }

        [Test]
        public void AdmissionAuthenticationRequest_RoundTripsOpaqueTokenOnly()
        {
            var source = new AdmissionAuthenticationRequestMessage
            {
                admissionToken = "opaque-one-time-token",
            };
            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new AdmissionAuthenticationRequestMessage();
            copy.Deserialize(reader);

            Assert.AreEqual(source.admissionToken, copy.admissionToken);
        }

        [Test]
        public void AdmissionAuthenticationResponse_RoundTripsAccountIdAndSessionState()
        {
            var source = new AdmissionAuthenticationResponseMessage
            {
                success = true,
                accountId = 12345,
                sessionState = 2,
                error = string.Empty,
                rosterRevision = 987654321L,
            };
            var writer = new NetDataWriter();
            source.Serialize(writer);
            var reader = new NetDataReader(writer.CopyData());
            var copy = new AdmissionAuthenticationResponseMessage();
            copy.Deserialize(reader);

            Assert.IsTrue(copy.success);
            Assert.AreEqual(12345, copy.accountId);
            Assert.AreEqual(2, copy.sessionState);
            Assert.AreEqual(string.Empty, copy.error);
            Assert.AreEqual(987654321L, copy.rosterRevision);
        }

        [Test]
        public void AdmissionAuthenticationResponse_DeserializesLegacyPayloadWithoutRosterRevision()
        {
            var writer = new NetDataWriter();
            writer.Put(true);
            writer.Put(12345L);
            writer.Put((byte)2);
            writer.Put(string.Empty);

            var reader = new NetDataReader(writer.CopyData());
            var copy = new AdmissionAuthenticationResponseMessage();
            copy.Deserialize(reader);

            Assert.IsTrue(copy.success);
            Assert.AreEqual(12345L, copy.accountId);
            Assert.AreEqual(2, copy.sessionState);
            Assert.AreEqual(string.Empty, copy.error);
            Assert.AreEqual(0L, copy.rosterRevision);
        }

        [Test]
        public void CharacterRosterRevision_IsStableAcrossOrdering()
        {
            var a = new[]
            {
                new CharacterSessionCharacterSummary(10, "Alice", "world-a"),
                new CharacterSessionCharacterSummary(20, "Bob", "world-b"),
            };
            var b = new[]
            {
                new CharacterSessionCharacterSummary(20, "Bob", "world-b"),
                new CharacterSessionCharacterSummary(10, "Alice", "world-a"),
            };

            Assert.AreEqual(CharacterRosterStateRevision.Compute(a), CharacterRosterStateRevision.Compute(b));
        }

        [Test]
        public void CharacterRosterRevision_ChangesWhenVisibleRosterStateChanges()
        {
            var baseline = new[]
            {
                new CharacterSessionCharacterSummary(10, "Alice", "world-a"),
            };
            var renamed = new[]
            {
                new CharacterSessionCharacterSummary(10, "Alicia", "world-a"),
            };
            var moved = new[]
            {
                new CharacterSessionCharacterSummary(10, "Alice", "world-b"),
            };
            var removed = System.Array.Empty<CharacterSessionCharacterSummary>();

            long revision = CharacterRosterStateRevision.Compute(baseline);
            Assert.AreNotEqual(revision, CharacterRosterStateRevision.Compute(renamed));
            Assert.AreNotEqual(revision, CharacterRosterStateRevision.Compute(moved));
            Assert.AreNotEqual(revision, CharacterRosterStateRevision.Compute(removed));
            Assert.AreNotEqual(0L, CharacterRosterStateRevision.Compute(removed));
        }
    }
}
