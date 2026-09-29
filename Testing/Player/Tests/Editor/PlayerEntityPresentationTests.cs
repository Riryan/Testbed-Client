using System;
using System.Reflection;
using Player.Client;
using Player.Client.Presentation;
using Player.Shared;
using Game.Shared.Combat;
using NUnit.Framework;
using UnityEngine;

namespace Testing.Player.Tests
{
    public class PlayerEntityPresentationTests
    {
        [Test]
        public void SnapshotOrdering_RejectsDuplicateAndReordered_AcceptsWrap()
        {
            Assert.IsFalse(
                PlayerEntityPresentationMath.ShouldAcceptSnapshot(100u, 100u));
            Assert.IsFalse(
                PlayerEntityPresentationMath.ShouldAcceptSnapshot(99u, 100u));
            Assert.IsTrue(
                PlayerEntityPresentationMath.ShouldAcceptSnapshot(101u, 100u));
            Assert.IsTrue(
                PlayerEntityPresentationMath.ShouldAcceptSnapshot(0u, uint.MaxValue));
        }

        [Test]
        public void TickUnwrap_IsContinuousAcrossUintRollover()
        {
            double unwrapped =
                PlayerEntityPresentationMath.UnwrapTick(
                    5000.0,
                    0u,
                    uint.MaxValue);

            Assert.AreEqual(5001.0, unwrapped, 0.000001);
        }

        [Test]
        public void JitterEwma_TracksArrivalVariationDeterministically()
        {
            double jitter =
                PlayerEntityPresentationMath.UpdateJitterEwma(
                    0.0,
                    0.10,
                    0.05,
                    1.0 / 16.0);

            Assert.AreEqual(0.003125, jitter, 0.0000001);
        }

        [Test]
        public void AdaptiveDelay_IncreasesTowardJitterTarget_AndStaysBounded()
        {
            double desired;
            double next =
                PlayerEntityPresentationMath.ComputeAdaptiveDelay(
                    currentDelaySeconds: 0.10,
                    frameDelta: 0.05f,
                    baseDelaySeconds: 0.10,
                    maximumDelaySeconds: 0.50,
                    jitterSeconds: 0.05,
                    jitterMultiplier: 3.0,
                    extrapolationPenaltySeconds: 0.05,
                    increaseResponseSeconds: 0.10,
                    decreaseResponseSeconds: 3.0,
                    desiredDelaySeconds: out desired);

            Assert.AreEqual(0.30, desired, 0.000001);
            Assert.Greater(next, 0.10);
            Assert.Less(next, desired);
            Assert.LessOrEqual(next, 0.50);
        }

        [Test]
        public void HermiteDeviation_IsBoundedAroundAuthoritativeSegment()
        {
            Vector3 p0 = Vector3.zero;
            Vector3 p1 = new Vector3(1f, 0f, 0f);
            Vector3 linear = Vector3.Lerp(p0, p1, 0.5f);
            Vector3 raw = PlayerEntityPresentationMath.Hermite(
                p0,
                p1,
                new Vector3(0f, 100f, 0f),
                new Vector3(0f, -100f, 0f),
                0.5f,
                1f);

            Vector3 bounded =
                PlayerEntityPresentationMath.BoundHermiteDeviation(
                    p0,
                    p1,
                    linear,
                    raw,
                    0.75f);

            // For a one-meter segment the runtime bound is 0.45m.
            Assert.LessOrEqual(
                Vector3.Distance(bounded, linear),
                0.45001f);
        }

        [Test]
        public void Extrapolation_UsesFullVelocityThenFiniteCoast()
        {
            float full = 0.25f;
            float coast = 0.30f;

            Assert.AreEqual(
                0.10f,
                PlayerEntityPresentationMath.IntegratedPredictionTime(
                    0.10f, full, coast),
                0.00001f);

            float maximumIntegrated = full + coast * 0.5f;
            Assert.AreEqual(
                maximumIntegrated,
                PlayerEntityPresentationMath.IntegratedPredictionTime(
                    10f, full, coast),
                0.00001f);
        }

        [Test]
        public void AuthoritativeStop_StopsIdleAndZeroSpeedSnapshots()
        {
            PlayerEntitySnapshot idle = MakeSnapshot(
                10u,
                Vector3.zero,
                PlayerEntityMoveState.Idle,
                4f);

            PlayerEntitySnapshot zeroMoving = MakeSnapshot(
                11u,
                Vector3.zero,
                PlayerEntityMoveState.Moving,
                0f);

            PlayerEntitySnapshot moving = MakeSnapshot(
                12u,
                Vector3.zero,
                PlayerEntityMoveState.Moving,
                4f);

            Assert.IsTrue(
                PlayerEntityPresentationMath.IsAuthoritativelyStopped(
                    idle, 0.05f));
            Assert.IsTrue(
                PlayerEntityPresentationMath.IsAuthoritativelyStopped(
                    zeroMoving, 0.05f));
            Assert.IsFalse(
                PlayerEntityPresentationMath.IsAuthoritativelyStopped(
                    moving, 0.05f));
        }

        [Test]
        public void TeleportDetection_DistinguishesExplicit_Implausible_AndDelayedMovement()
        {
            PlayerEntitySnapshot explicitTeleport = MakeSnapshot(
                20u,
                new Vector3(1f, 0f, 0f),
                PlayerEntityMoveState.Moving,
                4f,
                PlayerEntityFlags.Teleport);

            Assert.IsTrue(
                PlayerEntityPresentationMath.IsTeleport(
                    explicitTeleport,
                    Vector3.zero,
                    0.05,
                    8f,
                    30f));

            PlayerEntitySnapshot jump = MakeSnapshot(
                21u,
                new Vector3(20f, 0f, 0f),
                PlayerEntityMoveState.Moving,
                4f);

            Assert.IsTrue(
                PlayerEntityPresentationMath.IsTeleport(
                    jump,
                    Vector3.zero,
                    0.10,
                    8f,
                    30f));

            // Same distance over enough authoritative time is delayed movement,
            // not a teleport.
            Assert.IsFalse(
                PlayerEntityPresentationMath.IsTeleport(
                    jump,
                    Vector3.zero,
                    1.0,
                    8f,
                    30f));
        }


        [Test]
        public void HermiteInterpolation_PreservesAuthoritativeEndpoints()
        {
            Vector3 p0 = new Vector3(2f, 1f, -3f);
            Vector3 p1 = new Vector3(7f, 1f, 4f);
            Vector3 v0 = new Vector3(4f, 0f, 2f);
            Vector3 v1 = new Vector3(1f, 0f, 5f);

            Assert.AreEqual(
                p0,
                PlayerEntityPresentationMath.Hermite(
                    p0, p1, v0, v1, 0f, 0.05f));

            Assert.AreEqual(
                p1,
                PlayerEntityPresentationMath.Hermite(
                    p0, p1, v0, v1, 1f, 0.05f));
        }

        [Test]
        public void StopHermite_RemainsMonotonicAndDoesNotOvershootEndpoint()
        {
            float previous = 0f;
            for (int i = 0; i <= 20; ++i)
            {
                float value = PlayerEntityPresentationMath.HermiteScalar(
                    0f,
                    1f,
                    1.5f,
                    0f,
                    i / 20f,
                    1f);

                value = Mathf.Clamp(value, 0f, 1f);
                Assert.GreaterOrEqual(value + 0.00001f, previous);
                Assert.LessOrEqual(value, 1.00001f);
                previous = value;
            }
        }

        [TestCase(false, 14.9f, 15f, false)]
        [TestCase(false, 15f, 15f, true)]
        [TestCase(true, 0f, 15f, true)]
        public void HardSnapPolicy_IsDeterministic(
            bool forced,
            float error,
            float threshold,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                PlayerEntityPresentationMath.ShouldHardSnap(
                    forced, error, threshold));
        }

        [TestCase(HumanoidCombatStance.Pistol, "Pistol_Primary", "Pistol_Primary")]
        [TestCase(HumanoidCombatStance.Rifle, "Rifle_Primary", "Rifle_Primary")]
        [TestCase(HumanoidCombatStance.Shotgun, "Firearm Expansion.Shotgun.Shotgun_Primary", "Shotgun_Primary")]
        public void CombatActionPresentation_UsesCanonicalPlayerHumanoidPrimaryStates(
            HumanoidCombatStance stance,
            string expectedPath,
            string expectedState)
        {
            Assert.IsTrue(
                HumanoidAnimatorPresentationAdapter.TryResolvePrimaryActionState(
                    stance,
                    out string path,
                    out string state));
            Assert.AreEqual(expectedPath, path);
            Assert.AreEqual(expectedState, state);
        }

        [TestCase(HumanoidCombatStance.Pistol, "Pistol_Reload", "Pistol_Reload")]
        [TestCase(HumanoidCombatStance.Rifle, "Rifle_Reload", "Rifle_Reload")]
        [TestCase(HumanoidCombatStance.Shotgun, "Firearm Expansion.Shotgun.Shotgun_Reload", "Shotgun_Reload")]
        public void CombatActionPresentation_UsesCanonicalPlayerHumanoidReloadStates(
            HumanoidCombatStance stance,
            string expectedPath,
            string expectedState)
        {
            Assert.IsTrue(
                HumanoidAnimatorPresentationAdapter.TryResolveReloadActionState(
                    stance,
                    out string path,
                    out string state));
            Assert.AreEqual(expectedPath, path);
            Assert.AreEqual(expectedState, state);
        }

        [TestCase(0, "Unarmed_Primary_Right")]
        [TestCase(1, "Unarmed_Primary_Right_B")]
        [TestCase(2, "Unarmed_Primary_Left")]
        [TestCase(3, "Unarmed_Primary_Right_C")]
        [TestCase(4, "Unarmed_Primary_Left_B")]
        [TestCase(5, "Unarmed_Primary_Left_C")]
        [TestCase(6, "Unarmed_Primary_Right")]
        public void CombatActionPresentation_UnarmedUsesAuthoredPunchVariants(
            ushort sequence,
            string expectedState)
        {
            Assert.IsTrue(
                HumanoidAnimatorPresentationAdapter.TryResolvePrimaryActionState(
                    HumanoidCombatStance.Unarmed,
                    sequence,
                    out string path,
                    out string state));
            Assert.AreEqual("Melee Actions.Unarmed." + expectedState, path);
            Assert.AreEqual(expectedState, state);
        }

        [Test]
        public void CombatActionPresentation_RejectsUnauthoredBatPrimary()
        {
            Assert.IsFalse(
                HumanoidAnimatorPresentationAdapter.TryResolvePrimaryActionState(
                    HumanoidCombatStance.Bat,
                    out _,
                    out _));
        }

        [Test]
        public void GenerationChange_ResetsSnapshotBuffer()
        {
            GameObject go = new GameObject("PresentationTest");
            try
            {
                PlayerEntityClient client =
                    go.AddComponent<PlayerEntityClient>();
                client.createDebugVisual = false;

                InvokeSnapshot(client, MakeSnapshot(100u, Vector3.zero), 1);
                InvokeSnapshot(client, MakeSnapshot(101u, Vector3.right), 1);
                Assert.AreEqual(2, client.BufferedSnapshots);

                InvokeSnapshot(client, MakeSnapshot(1u, Vector3.up), 2);
                Assert.AreEqual(
                    1,
                    client.BufferedSnapshots,
                    "A new generation must discard stale interpolation history.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DestroyLifecycle_ClearsBufferedPresentationState()
        {
            GameObject go = new GameObject("PresentationDestroyTest");
            try
            {
                PlayerEntityClient client =
                    go.AddComponent<PlayerEntityClient>();
                client.createDebugVisual = false;

                InvokeSnapshot(client, MakeSnapshot(200u, Vector3.zero), 1);
                Assert.AreEqual(1, client.BufferedSnapshots);

                client.OnNetworkDestroy(0);
                Assert.AreEqual(0, client.BufferedSnapshots);
                Assert.IsFalse(client.IsExtrapolating);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void InvokeSnapshot(
            PlayerEntityClient client,
            PlayerEntitySnapshot snapshot,
            ushort generation)
        {
            snapshot.entityId = 1;
            snapshot.generation = generation;

            MethodInfo method = typeof(PlayerEntityClient).GetMethod(
                "OnSnapshotChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(method);
            method.Invoke(
                client,
                new object[]
                {
                    false,
                    default(PlayerEntitySnapshot),
                    snapshot,
                });
        }

        private static PlayerEntitySnapshot MakeSnapshot(
            uint tick,
            Vector3 position,
            PlayerEntityMoveState state = PlayerEntityMoveState.Moving,
            float speed = 4f,
            PlayerEntityFlags flags = PlayerEntityFlags.None)
        {
            return new PlayerEntitySnapshot
            {
                entityId = 1,
                generation = 1,
                serverTick = tick,
                positionX = PlayerEntityQuantization.QuantizePosition(position.x),
                positionY = PlayerEntityQuantization.QuantizePosition(position.y),
                positionZ = PlayerEntityQuantization.QuantizePosition(position.z),
                moveSpeed = PlayerEntityQuantization.QuantizeUnsignedSpeed(speed),
                moveState = (byte)state,
                flags = (byte)flags,
            };
        }
    }
}
