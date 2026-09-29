using System;

namespace LiteNetLibManager
{
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public abstract class RpcAttribute : Attribute
    {
        /// <summary>
        /// Legacy convenience switch. When true, OtherClient is added to the
        /// authoritative AllowedOrigins mask. Destination permissions remain
        /// controlled independently by AllowedReceivers.
        /// </summary>
        public bool canCallByEveryone = false;
    }
}
