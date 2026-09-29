using Player.Shared;
using NUnit.Framework;

namespace Testing.Player.Tests
{
    public class SequenceRolloverTests
    {
        [Test] public void Zero_IsNewerThanMax_AcrossRollover() => Assert.IsTrue(NetworkSequence.IsNewer(0u, uint.MaxValue));
        [Test] public void Max_IsOlderThanZero_AcrossRollover() => Assert.IsTrue(NetworkSequence.IsOlder(uint.MaxValue, 0u));
        [Test] public void ForwardDistance_CrossesRollover() => Assert.AreEqual(2u, NetworkSequence.ForwardDistance(1u, uint.MaxValue));
        [Test] public void SameSequence_IsNotNewer() => Assert.IsFalse(NetworkSequence.IsNewer(123u, 123u));
    }
}
