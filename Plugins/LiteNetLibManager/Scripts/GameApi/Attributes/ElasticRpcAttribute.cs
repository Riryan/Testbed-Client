using System;

namespace LiteNetLibManager
{
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class ElasticRpcAttribute : RpcAttribute
    {
        /// <summary>
        /// Elastic RPCs are intentionally flexible in destination, so their sender
        /// contract must be authored explicitly. The safe compatibility default
        /// permits server and owning-client origins, never arbitrary clients.
        /// </summary>
        public RPCOriginMask allowedOrigins =
            RPCOriginMask.Server | RPCOriginMask.OwnerClient;
    }
}
