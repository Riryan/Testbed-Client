using NUnit.Framework;

namespace LiteNetLibManager.Tests
{
    public class RpcAndRequestAuthorityTests
    {
        [TestCase(RPCReceivers.Server, RPCReceiverMask.Server, true)]
        [TestCase(RPCReceivers.All, RPCReceiverMask.Server, false)]
        [TestCase(RPCReceivers.Target, RPCReceiverMask.Server, false)]
        [TestCase(RPCReceivers.All, RPCReceiverMask.All, true)]
        [TestCase(RPCReceivers.Server, RPCReceiverMask.All, false)]
        [TestCase(RPCReceivers.Target, RPCReceiverMask.Target, true)]
        public void RpcDescriptor_EnforcesReceiverContract(
            RPCReceivers receiver,
            RPCReceiverMask allowed,
            bool expected)
        {
            var rpc = new LiteNetLibRPC(() => { })
            {
                AllowedReceivers = allowed,
            };

            Assert.AreEqual(expected, rpc.AllowsReceiver(receiver));
        }

        [Test]
        public void ElasticReceiverMask_AllowsAllDirections()
        {
            var rpc = new LiteNetLibRPC(() => { })
            {
                AllowedReceivers = RPCReceiverMask.Any,
            };

            Assert.IsTrue(rpc.AllowsReceiver(RPCReceivers.Server));
            Assert.IsTrue(rpc.AllowsReceiver(RPCReceivers.All));
            Assert.IsTrue(rpc.AllowsReceiver(RPCReceivers.Target));
        }

        [Test]
        public void RequestCallback_BindsToExpectedPeer()
        {
            var callback = new LiteNetLibRequestCallback(
                1432u,
                null,
                null,
                null,
                42L);

            Assert.IsTrue(callback.AcceptsResponseFrom(42L));
            Assert.IsFalse(callback.AcceptsResponseFrom(7L));
        }

        [Test]
        public void ReceiveTickComparison_IsWrapSafe()
        {
            Assert.IsTrue(
                LiteNetLibSyncField.IsReceiveTickNewer(
                    0u,
                    uint.MaxValue));

            Assert.IsFalse(
                LiteNetLibSyncField.IsReceiveTickNewer(
                    uint.MaxValue,
                    0u));

            Assert.IsFalse(
                LiteNetLibSyncField.IsReceiveTickNewer(
                    123u,
                    123u));
        }
        [Test]
        public void RpcDescriptor_EnforcesOriginContract()
        {
            var rpc = new LiteNetLibRPC(() => { })
            {
                AllowedOrigins = RPCOriginMask.OwnerClient,
            };

            Assert.IsTrue(rpc.AllowsOrigin(RPCOrigin.OwnerClient));
            Assert.IsFalse(rpc.AllowsOrigin(RPCOrigin.Server));
            Assert.IsFalse(rpc.AllowsOrigin(RPCOrigin.OtherClient));
        }

        [Test]
        public void ServerRpc_DefaultOrigin_IsOwnerClientOnly()
        {
            RPCOriginMask origins =
                LiteNetLibRPC.DefaultOriginsForReceiver(
                    RPCReceivers.Server,
                    false);

            Assert.AreEqual(RPCOriginMask.OwnerClient, origins);
        }

        [Test]
        public void AllRpc_DefaultOrigin_IsServerOnly()
        {
            RPCOriginMask origins =
                LiteNetLibRPC.DefaultOriginsForReceiver(
                    RPCReceivers.All,
                    false);

            Assert.AreEqual(RPCOriginMask.Server, origins);
        }

        [Test]
        public void AllRpc_CanCallByEveryone_ExplicitlyAllowsAnyClient()
        {
            RPCOriginMask origins =
                LiteNetLibRPC.DefaultOriginsForReceiver(
                    RPCReceivers.All,
                    true);

            Assert.AreEqual(
                RPCOriginMask.Server | RPCOriginMask.AnyClient,
                origins);
        }

        [Test]
        public void TargetRpc_DefaultOrigin_IsServerOrOwnerClient()
        {
            RPCOriginMask origins =
                LiteNetLibRPC.DefaultOriginsForReceiver(
                    RPCReceivers.Target,
                    false);

            Assert.AreEqual(
                RPCOriginMask.Server | RPCOriginMask.OwnerClient,
                origins);
        }

        [Test]
        public void NonOwnerClient_RequiresExplicitOriginPermission()
        {
            var rpc = new LiteNetLibRPC(() => { })
            {
                AllowedOrigins = RPCOriginMask.OwnerClient,
            };

            Assert.IsTrue(rpc.AllowsRemoteClient(42L, 42L));
            Assert.IsFalse(rpc.AllowsRemoteClient(7L, 42L));

            rpc.AllowedOrigins |= RPCOriginMask.OtherClient;
            Assert.IsTrue(rpc.AllowsRemoteClient(7L, 42L));
        }

        [Test]
        public void OtherClientOnly_LocalOrigin_DeniesOwnerButAllowsNonOwner()
        {
            const RPCOriginMask allowed =
                RPCOriginMask.OtherClient;

            Assert.IsFalse(
                LiteNetLibRPC.CanCallAsOtherClient(
                    true,
                    true,
                    allowed),
                "An owning client must not fall through as OtherClient.");

            Assert.IsTrue(
                LiteNetLibRPC.CanCallAsOtherClient(
                    false,
                    true,
                    allowed),
                "A connected non-owner client may use explicit OtherClient permission.");

            Assert.IsFalse(
                LiteNetLibRPC.CanCallAsOtherClient(
                    false,
                    false,
                    allowed),
                "A disconnected client cannot emit an OtherClient RPC.");
        }


        [Test]
        public void ElasticRpc_DefaultAuthoredOrigins_AreExplicitAndSafe()
        {
            var attribute = new ElasticRpcAttribute();
            Assert.AreEqual(
                RPCOriginMask.Server | RPCOriginMask.OwnerClient,
                attribute.allowedOrigins);
        }

    }
}
