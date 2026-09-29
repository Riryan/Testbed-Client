using NUnit.Framework;

namespace LiteNetLibManager.Tests
{
    public class RegressionClosureTests
    {
        [Test]
        public void SpatialAoiType_IsAvailableAsFrameworkFallback()
        {
            Assert.IsNotNull(typeof(SpatialInterestManager));
            Assert.IsTrue(
                typeof(BaseInterestManager)
                    .IsAssignableFrom(typeof(SpatialInterestManager)));
        }

        [Test]
        public void ObserverAnchor_IsAStandaloneUnityComponentType()
        {
            Assert.AreEqual(
                "InterestObserverAnchor",
                typeof(InterestObserverAnchor).Name);
            Assert.IsTrue(
                typeof(UnityEngine.MonoBehaviour)
                    .IsAssignableFrom(typeof(InterestObserverAnchor)));
        }
        [Test]
        public void AlwaysVisible_BypassesPerTargetCandidateHardLimit()
        {
            Assert.IsFalse(
                SpatialInterestManager.CandidateHardLimitApplies(true),
                "AlwaysVisible must continue scanning past the spatial candidate cap.");

            Assert.IsTrue(
                SpatialInterestManager.CandidateHardLimitApplies(false));
        }

        [Test]
        public void AlwaysVisible_FiveThousandObservers_IsNotTruncatedAt4096()
        {
            const int observerCount = 5000;
            const int oldSpatialLimit = 4096;

            int considered = 0;
            for (int i = 0; i < observerCount; ++i)
            {
                if (!SpatialInterestManager.CandidateHardLimitApplies(true))
                    considered++;
                else if (i >= oldSpatialLimit)
                    break;
            }

            Assert.AreEqual(observerCount, considered);
        }

    }
}
