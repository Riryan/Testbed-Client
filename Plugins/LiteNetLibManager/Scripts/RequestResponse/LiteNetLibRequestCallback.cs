using LiteNetLib.Utils;

namespace LiteNetLibManager
{
    public class LiteNetLibRequestCallback
    {
        public uint RequestId { get; private set; }
        public TransportHandler TransportHandler { get; private set; }
        public ILiteNetLibResponseHandler ResponseHandler { get; private set; }
        public ResponseDelegate<INetSerializable> ResponseDelegate { get; private set; }
        public long ExpectedConnectionId { get; private set; }

        public LiteNetLibRequestCallback(
            uint requestId,
            TransportHandler transportHandler,
            ILiteNetLibResponseHandler responseHandler,
            ResponseDelegate<INetSerializable> responseDelegate)
            : this(requestId, transportHandler, responseHandler, responseDelegate, -1)
        {
        }

        public LiteNetLibRequestCallback(
            uint requestId,
            TransportHandler transportHandler,
            ILiteNetLibResponseHandler responseHandler,
            ResponseDelegate<INetSerializable> responseDelegate,
            long expectedConnectionId)
        {
            RequestId = requestId;
            TransportHandler = transportHandler;
            ResponseHandler = responseHandler;
            ResponseDelegate = responseDelegate;
            ExpectedConnectionId = expectedConnectionId;
        }

        public bool AcceptsResponseFrom(long connectionId)
        {
            return connectionId == ExpectedConnectionId;
        }

        public void ResponseTimeout()
        {
            ResponseHandler.InvokeResponse(
                new ResponseHandlerData(
                    RequestId,
                    TransportHandler,
                    ExpectedConnectionId,
                    null),
                AckResponseCode.Timeout,
                ResponseDelegate);
        }

        public void ResponseDisconnected()
        {
            ResponseHandler.InvokeResponse(
                new ResponseHandlerData(
                    RequestId,
                    TransportHandler,
                    ExpectedConnectionId,
                    null),
                AckResponseCode.Error,
                ResponseDelegate);
        }

        public void Response(long connectionId, NetDataReader reader, AckResponseCode responseCode)
        {
            ResponseHandler.InvokeResponse(new ResponseHandlerData(RequestId, TransportHandler, connectionId, reader), responseCode, ResponseDelegate);
        }
    }
}
