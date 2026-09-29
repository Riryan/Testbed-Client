using NUnit.Framework;

namespace LiteNetLibManager.Tests
{
    public class ClientSyncAuthorizationTests
    {
        [Test]
        public void OwnerAndClientWritableElement_IsAuthorized()
        {
            Assert.IsTrue(LiteNetLibGameManager.IsClientSyncAuthorized(42, 42, true));
        }

        [Test]
        public void WrongOwner_IsRejected_EvenWhenElementAllowsClientWrites()
        {
            Assert.IsFalse(LiteNetLibGameManager.IsClientSyncAuthorized(7, 42, true));
        }

        [Test]
        public void ServerControlledElement_IsRejected_EvenForOwner()
        {
            Assert.IsFalse(LiteNetLibGameManager.IsClientSyncAuthorized(42, 42, false));
        }
    }
}
