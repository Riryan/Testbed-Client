using System;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace LiteNetLibManager.Tests
{
    public class HostileInputBoundsTests
    {
        [Test]
        public void ArrayCount_AboveLimit_IsRejectedBeforeAllocation()
        {
            var writer = new NetDataWriter();
            writer.Put(NetDataReaderExtension.MaxCollectionElements + 1);
            var reader = new NetDataReader(writer.CopyData());
            Assert.Throws<InvalidOperationException>(() => reader.GetArrayObject(typeof(int)));
        }

        [Test]
        public void NegativeCollectionCount_IsRejected()
        {
            var writer = new NetDataWriter();
            writer.Put(-1);
            var reader = new NetDataReader(writer.CopyData());
            Assert.Throws<InvalidOperationException>(() => reader.GetList<int>());
        }

        [Test]
        public void ClaimedFixedWidthArrayWithoutBytes_IsRejected()
        {
            var writer = new NetDataWriter();
            writer.Put(100);
            var reader = new NetDataReader(writer.CopyData());
            Assert.Throws<InvalidOperationException>(() => reader.GetArrayObject(typeof(float)));
        }

        [Test]
        public void RpcArrayLimit_IsStricterThanGenericLimit()
        {
            Assert.LessOrEqual(NetDataReaderExtension.MaxRpcArrayElements, NetDataReaderExtension.MaxCollectionElements);
        }
    }
}
