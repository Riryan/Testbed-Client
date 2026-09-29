using Cysharp.Threading.Tasks;
using Cysharp.Text;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteNetLibManager
{
    public class LiteNetLibClient : TransportHandler
    {
        public LiteNetLibManager Manager { get; protected set; }
        public override string LogTag
        {
            get
            {
                using (var stringBuilder = ZString.CreateStringBuilder(false))
                {
                    if (Manager != null)
                    {
                        stringBuilder.Append(Manager.LogTag);
                    }
                    else
                    {
                        stringBuilder.Append(LiteNetLibManager.TAG_NULL);
                    }
                    stringBuilder.Append('.');
                    stringBuilder.Append('C');
                    stringBuilder.Append('_');
                    stringBuilder.Append(GetType().Name);
                    return stringBuilder.ToString();
                }
            }
        }
        private bool _isNetworkActive;
        public override bool IsNetworkActive { get { return _isNetworkActive; } }
        private byte[] _disconnectData;

        public LiteNetLibClient(LiteNetLibManager manager) : base()
        {
            Manager = manager;
        }

        public LiteNetLibClient(ITransport transport) : base(transport)
        {

        }

        public void SetDisconnectData(byte[] data)
        {
            _disconnectData = data;
        }

        public void Update()
        {
            Update(0, 0f);
        }

        public void Update(int maxEvents, float maxMilliseconds)
        {
            if (!IsNetworkActive)
                return;

            int processed = 0;
            long startTimestamp = maxMilliseconds > 0f ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            double timestampToMilliseconds = maxMilliseconds > 0f ? 1000.0 / System.Diagnostics.Stopwatch.Frequency : 0.0;

            while ((maxEvents <= 0 || processed < maxEvents) && Transport.ClientReceive(out TransportEventData tempEventData))
            {
                try
                {
                    OnClientReceive(tempEventData);
                }
                finally
                {
                    if (tempEventData.reader is NetPacketReader packetReader)
                        packetReader.Recycle();
                }

                ++processed;
                if (maxMilliseconds > 0f &&
                    (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * timestampToMilliseconds >= maxMilliseconds)
                    break;
            }
        }

        public bool StartClient(string address, int port)
        {
            if (IsNetworkActive)
            {
                Logging.LogWarning(LogTag, "Cannot Start Client, network already active");
                return false;
            }
            // Clear and reset request Id
            _requestCallbacks.Clear();
            if (_isNetworkActive = Transport.StartClient(address, port))
            {
                OnStartClient();
                return true;
            }
            return false;
        }

        protected virtual void OnStartClient() { }

        public void StopClient()
        {
            Transport.StopClient();
            _isNetworkActive = false;
            OnStopClient();
        }

        protected virtual void OnStopClient() { }

        public virtual void OnClientReceive(TransportEventData eventData)
        {
            switch (eventData.type)
            {
                case ENetworkEvent.ConnectEvent:
                    if (Manager.LogInfo) Logging.Log(LogTag, "OnClientConnected");
                    Manager.OnClientConnected();
                    break;
                case ENetworkEvent.DataEvent:
                    ReadPacket(-1, eventData.reader);
                    break;
                case ENetworkEvent.DisconnectEvent:
                    if (Manager.LogInfo) Logging.Log(LogTag, $"OnClientDisconnected peer. disconnectInfo.Reason: {eventData.disconnectInfo.Reason}");
                    Manager.OnClientDisconnected(eventData.disconnectInfo.Reason, eventData.disconnectInfo.SocketErrorCode, _disconnectData);
                    Manager.StopClient();
                    _disconnectData = null;
                    break;
                case ENetworkEvent.ErrorEvent:
                    if (Manager.LogError) Logging.LogError(LogTag, $"OnClientNetworkError endPoint: {eventData.endPoint} socketErrorCode {eventData.socketError} errorMessage {eventData.errorMessage}");
                    Manager.OnClientNetworkError(eventData.endPoint, eventData.socketError);
                    break;
            }
        }

        public override void SendMessage(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            Transport.ClientSend(dataChannel, deliveryMethod, writer);
        }

        public void SendMessage(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            SendMessage(-1, dataChannel, deliveryMethod, writer);
        }

        public void SendPacket(byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType, SerializerDelegate serializer)
        {
            WritePacket(s_Writer, msgType, serializer);
            // Send packet to server, so connection id will not being used
            SendMessage(dataChannel, deliveryMethod, s_Writer);
        }

        public bool SendRequest<TRequest>(ushort requestType, TRequest request, ResponseDelegate<INetSerializable> responseDelegate = null, int millisecondsTimeout = 30000, SerializerDelegate extraSerializer = null)
            where TRequest : INetSerializable, new()
        {
            if (!CreateAndWriteRequest(s_Writer, requestType, request, responseDelegate, millisecondsTimeout, extraSerializer))
                return false;
            // Send request to server, so connection id will not being used
            SendMessage(0, DeliveryMethod.ReliableUnordered, s_Writer);
            return true;
        }

        public async UniTask<AsyncResponseData<TResponse>> SendRequestAsync<TRequest, TResponse>(ushort requestType, TRequest request, int millisecondsTimeout = 30000, SerializerDelegate extraSerializer = null)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
            var completion = new UniTaskCompletionSource<AsyncResponseData<TResponse>>();
            bool created = CreateAndWriteRequest(s_Writer, requestType, request, (requestHandler, responseCode, response) =>
            {
                TResponse typed = response is TResponse valid ? valid : default;
                completion.TrySetResult(new AsyncResponseData<TResponse>(requestHandler, responseCode, typed));
            }, millisecondsTimeout, extraSerializer);

            if (!created)
                return await completion.Task; // failure callback completes synchronously

            SendMessage(0, DeliveryMethod.ReliableUnordered, s_Writer);
            return await completion.Task;
        }
    }
}
