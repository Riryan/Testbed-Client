using Game.Shared.Abilities;
using LiteNetLib.Utils;
using NUnit.Framework;
using Player.Networking;
using Player.Shared;
using UnityEngine;

namespace Testing.Player.Tests
{
    public sealed class CombatWireEfficiencyTests
    {
        private static PlayerTargetReferenceWire Ref(uint objectId, ushort generation) =>
            new PlayerTargetReferenceWire { objectId = objectId, generation = generation };


        [Test]
        public void TargetlessMeleeCombatActionIntent_SmallSequence_IsTwoBytes()
        {
            var value = new PlayerCombatActionIntentMessage
            {
                sequence = 7,
                inputKind = (byte)BasicAttackInputKind.Primary,
                hasPrecisionAim = false,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(2, writer.Length, "Melee/unarmed attack intent must carry only sequence + input control.");
        }

        [Test]
        public void TargetlessPrecisionCombatActionIntent_SmallSequence_IsFourBytes()
        {
            var value = new PlayerCombatActionIntentMessage
            {
                sequence = 7,
                inputKind = (byte)BasicAttackInputKind.Primary,
                hasPrecisionAim = true,
                packedAim = PlayerCombatAimEncoding.Encode(123.4f, 12.5f),
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(4, writer.Length, "Precision-ranged attack intent keeps the existing 16-bit packed aim extension.");
        }


        [Test]
        public void MovementCommand_SmallSequence_IsSixBytes()
        {
            var value = new MovementCommand
            {
                sequence = 7,
                inputX = 12,
                inputZ = -34,
                yaw = PlayerEntityQuantization.QuantizeYaw(271.3f),
                flags = 2,
                combatFlags = 1,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(6, writer.Length, "8-bit movement yaw must remove one byte from the compact movement body.");
        }

        [Test]
        public void MovementYaw8_HasAtMostHalfStepCircularError()
        {
            float[] samples = { 0f, 0.4f, 45.2f, 89.9f, 180.3f, 271.3f, 359.8f, -0.2f };
            float maxError = PlayerEntityQuantization.YawStepDegrees * 0.5f + 0.001f;
            for (int i = 0; i < samples.Length; ++i)
            {
                byte packed = PlayerEntityQuantization.QuantizeYaw(samples[i]);
                float decoded = PlayerEntityQuantization.DequantizeYaw(packed);
                Assert.LessOrEqual(Mathf.Abs(Mathf.DeltaAngle(samples[i], decoded)), maxError);
            }
        }

        [Test]
        public void AttackAim16_PreservesTenBitYawAndSixBitPitchPrecision()
        {
            float[] yawSamples = { 0f, 12.34f, 123.4f, 271.3f, 359.9f };
            float[] pitchSamples = { -60f, -37.4f, 0f, 18.6f, 60f };
            float maxYawError = (360f / PlayerCombatAimEncoding.YawSteps) * 0.5f + 0.001f;
            float maxPitchError =
                ((PlayerCombatInputEncoding.MaximumAimPitchDegrees - PlayerCombatInputEncoding.MinimumAimPitchDegrees) /
                 PlayerCombatAimEncoding.PitchSteps) * 0.5f + 0.001f;

            for (int i = 0; i < yawSamples.Length; ++i)
            {
                ushort packed = PlayerCombatAimEncoding.Encode(yawSamples[i], pitchSamples[i]);
                Assert.LessOrEqual(
                    Mathf.Abs(Mathf.DeltaAngle(yawSamples[i], PlayerCombatAimEncoding.DecodeYawDegrees(packed))),
                    maxYawError);
                Assert.LessOrEqual(
                    Mathf.Abs(pitchSamples[i] - PlayerCombatAimEncoding.DecodePitchDegrees(packed)),
                    maxPitchError);
            }
        }

        [Test]
        public void CombatDamageWire_RemainsTwentySixBytes()
        {
            var value = new CombatDamageWire
            {
                eventSequence = 123,
                source = Ref(11, 2),
                target = Ref(12, 3),
                damageTypeId = 4,
                amount = 27,
                resultCode = 1,
                cause = 2,
                flags = 3,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(26, writer.Length, "Participant damage result must stay compact and must not grow into an observer payload.");
        }

        [Test]
        public void TargetedCombatPresentationCue_RemainsEighteenBytes()
        {
            var value = new CombatPresentationCueWire
            {
                kind = (byte)CombatPresentationCueKind.Action,
                source = Ref(11, 2),
                target = Ref(12, 3),
                semanticId = 21,
                sequence = 99,
                flags = (byte)CombatPresentationCueFlags.HasTarget,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(18, writer.Length);
        }

        [Test]
        public void TargetlessCombatPresentationCue_RemainsTwelveBytes()
        {
            var value = new CombatPresentationCueWire
            {
                kind = (byte)CombatPresentationCueKind.Action,
                source = Ref(11, 2),
                semanticId = 21,
                sequence = 99,
                flags = 0,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(12, writer.Length);
        }

        [Test]
        public void StatusDelta_RemainsSeventeenBytes()
        {
            var value = new PlayerStatusEffectDeltaMessage
            {
                statusRevision = 18,
                kind = 1,
                statusWireId = 7,
                stacks = 2,
                remainingMilliseconds = 5000,
                reason = 3,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(17, writer.Length);
        }


        [Test]
        public void CompactFireCycle_SmallObjectId_RemainsEightBytes()
        {
            CombatFireCycleWire value = CombatFireCycleWire.Create(
                11, 2, 21, 3, 3, true, FirearmFireMode.FullAutomatic, 0.1f);
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(8, writer.Length, "Compact fire-cycle records are the dense-AOI combat budget boundary.");
        }

        [Test]
        public void FortyNineCompactFireCycles_RemainThreeHundredNinetyThreeBytes()
        {
            var message = new PlayerCombatFireCycleBatchMessage
            {
                cycles = new CombatFireCycleWire[49],
            };
            for (int i = 0; i < message.cycles.Length; ++i)
            {
                message.cycles[i] = CombatFireCycleWire.Create(
                    (uint)(i + 1), 1, 21, (byte)i, 3, true, FirearmFireMode.FullAutomatic, 0.1f);
            }
            var writer = new NetDataWriter();
            message.Serialize(writer);
            Assert.AreEqual(393, writer.Length, "49 mutually visible firing actors must stay below 4 KB/s of application payload at 10 Hz.");
        }

        [Test]
        public void BeginAbilityRequest_RemainsEighteenBytes()
        {
            var value = new PlayerBeginAbilityRequestMessage
            {
                target = CombatTargetReferenceWire.Player(12, 3),
                abilityWireId = 7,
                rank = 1,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(18, writer.Length);
        }

        [Test]
        public void AbilityCastState_RemainsTwentySevenBytes()
        {
            var value = new PlayerAbilityCastStateMessage
            {
                success = true,
                failure = 0,
                phase = 1,
                castSequence = 44,
                abilityWireId = 7,
                source = Ref(11, 2),
                target = Ref(12, 3),
                remainingMilliseconds = 1500,
                totalAffected = 1,
            };
            var writer = new NetDataWriter();
            value.Serialize(writer);
            Assert.AreEqual(27, writer.Length);
        }
    }
}
