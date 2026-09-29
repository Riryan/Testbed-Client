using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace LiteNetLibManager
{
    public class LiteNetLibManager : MonoBehaviour
    {
        public const string TAG_NULL = "M?";
        public LiteNetLibClient Client { get; protected set; }
        public LiteNetLibServer Server { get; protected set; }
        public bool IsServer { get; private set; }
        public bool IsClient { get; private set; }
        public bool IsClientConnected { get { return Client != null && Client.IsNetworkActive; } }
        public bool IsNetworkActive { get { return (Server != null && Server.IsNetworkActive) || (Client != null && Client.IsNetworkActive); } }
        public bool LogDev { get { return currentLogLevel.IsLogDev(); } }
        public bool LogDebug { get { return currentLogLevel.IsLogDebug(); } }
        public bool LogInfo { get { return currentLogLevel.IsLogInfo(); } }
        public bool LogWarn { get { return currentLogLevel.IsLogWarn(); } }
        public bool LogError { get { return currentLogLevel.IsLogError(); } }
        public bool LogFatal { get { return currentLogLevel.IsLogFatal(); } }

        [Header("Client & Server Settings")]
        public ELogLevel currentLogLevel = ELogLevel.Info;
        public string networkAddress = "localhost";
        public int networkPort = 7770;
        public byte updateFps = 20;

        [Header("Server Only Settings")]
        public int maxConnections = 4;

        [Header("Receive Processing Budgets")]
        [Tooltip("Maximum server network events processed in one Unity frame. Set to 0 for unlimited.")]
        [Min(0)]
        public int maxServerReceiveEventsPerFrame = 8192;
        [Tooltip("Maximum milliseconds spent processing server network events in one Unity frame. Set to 0 for unlimited.")]
        [Min(0f)]
        public float maxServerReceiveMillisecondsPerFrame = 5f;
        [Tooltip("Maximum client network events processed in one Unity frame. Set to 0 for unlimited.")]
        [Min(0)]
        public int maxClientReceiveEventsPerFrame = 4096;
        [Tooltip("Maximum milliseconds spent processing client network events in one Unity frame. Set to 0 for unlimited.")]
        [Min(0f)]
        public float maxClientReceiveMillisecondsPerFrame = 4f;


        [Header("Core Runtime Governance")]
        [Tooltip("Built-in authoritative server scheduler/governor. All recurring server gameplay systems should register here instead of using their own Update/InvokeRepeating loops.")]
        public CoreRuntimeSchedulerSettings coreRuntimeSettings = new CoreRuntimeSchedulerSettings();
        [Tooltip("Maximum fixed network ticks allowed to catch up in one governed server frame.")]
        [Min(1)]
        public int maxNetworkTicksPerFrame = 3;
        [Tooltip("Maximum fixed network ticks retained as backlog. Older overdue ticks are discarded instead of creating a catch-up spiral.")]
        [Min(1)]
        public int maxNetworkTickBacklogTicks = 4;
        [Tooltip("Maximum milliseconds spent executing fixed network ticks in one governed server frame. The aggregate core frame budget can reduce this further.")]
        [Min(0.1f)]
        public float maxNetworkTickMillisecondsPerFrame = 4f;

        [Header("Server Foundation / Hardening")]
        [Tooltip("Bounded server-wide infrastructure: admission/rate limits, bandwidth governance, workers, telemetry, soak monitoring, performance gates, chaos testing, synthetic benchmarks and replication LOD policy.")]
        public CoreServerFoundationSettings serverFoundationSettings = new CoreServerFoundationSettings();

        [Header("Transport Layer Settings")]
        [SerializeField]
        private BaseTransportFactory transportFactory;
        public BaseTransportFactory TransportFactory
        {
            get { return transportFactory; }
            set { transportFactory = value; }
        }

        private ITransport _offlineTransport;
        private ITransport _clientTransport;
        public ITransport ClientTransport
        {
            get { return IsOfflineConnection ? _offlineTransport : _clientTransport; }
        }
        private ITransport _serverTransport;
        public ITransport ServerTransport
        {
            get { return IsOfflineConnection ? _offlineTransport : _serverTransport; }
        }

        public bool IsOfflineConnection { get; protected set; }

        public virtual string LogTag
        {
            get
            {
                using (var stringBuilder = ZString.CreateStringBuilder(false))
                {
                    if (this != null)
                    {
                        stringBuilder.Append('M');
                        stringBuilder.Append('_');
                        stringBuilder.Append(GetType().Name);
                        stringBuilder.Append('_');
                        stringBuilder.Append(name);
                    }
                    else
                    {
                        stringBuilder.Append(TAG_NULL);
                    }
                    return stringBuilder.ToString();
                }
            }
        }

        protected LogicUpdater _logicUpdater;
        public LogicUpdater LogicUpdater
        {
            get { return _logicUpdater; }
        }


        protected CoreRuntimeScheduler _coreScheduler;
        /// <summary>
        /// Built-in authoritative server scheduler/governor. This is available before
        /// Start() through lazy initialization so derived managers can register systems
        /// from Awake(). Unknown/unconfigured channels fail closed.
        /// </summary>
        public CoreRuntimeScheduler CoreScheduler
        {
            get
            {
                if (_coreScheduler == null)
                    _coreScheduler = new CoreRuntimeScheduler(coreRuntimeSettings);
                return _coreScheduler;
            }
        }

        protected CoreServerFoundation _serverFoundation;
        public CoreServerFoundation ServerFoundation
        {
            get
            {
                if (_serverFoundation == null)
                    _serverFoundation = new CoreServerFoundation(this, serverFoundationSettings);
                return _serverFoundation;
            }
        }

        public CoreServerTelemetry Telemetry
        {
            get { return ServerFoundation.Telemetry; }
        }

        public CoreBoundedWorkerPool WorkerPool
        {
            get { return ServerFoundation.WorkerPool; }
        }

        public CoreGlobalBroadcaster GlobalBroadcast
        {
            get { return ServerFoundation.GlobalBroadcast; }
        }

        public CoreReplicationLodPolicy ReplicationLod
        {
            get { return ServerFoundation.ReplicationLod; }
        }

        private bool _isApplicationQuitted = false;

        protected virtual void Start()
        {
            InitTransportAndHandlers();
        }

        protected void PrepareTransportFactory()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogError(
                "[LiteNetLibManager] WebGL is not supported by this MMO testbed. " +
                "The production transport surface is LiteNetLib UDP only.");
#endif

            // Deliberately keep the shipping transport surface to LiteNetLib UDP.
            // Old serialized references to WebSocket/Mix/custom factories are ignored.
            if (transportFactory != null &&
                !(transportFactory is LiteNetLibTransportFactory))
            {
                if (LogWarn)
                {
                    Logging.LogWarning(
                        LogTag,
                        $"Ignoring unsupported transport factory '{transportFactory.GetType().Name}'. " +
                        "This MMO runtime supports LiteNetLibTransportFactory only.");
                }

                transportFactory = null;
            }

            if (transportFactory == null)
                transportFactory = GetComponent<LiteNetLibTransportFactory>();

            if (transportFactory == null)
                transportFactory = gameObject.AddComponent<LiteNetLibTransportFactory>();
        }

        public void PrepareClientTransport()
        {
            PrepareTransportFactory();
            if (_clientTransport != null)
                _clientTransport.Destroy();
            _clientTransport = transportFactory.Build();
        }

        public void PrepareServerTransport()
        {
            PrepareTransportFactory();
            if (_serverTransport != null)
                _serverTransport.Destroy();
            _serverTransport = transportFactory.Build();
        }

        protected void InitTransportAndHandlers()
        {
            _offlineTransport = new OfflineTransport();
            Client = new LiteNetLibClient(this);
            Server = new LiteNetLibServer(this);
            serverFoundationSettings.ClampUnsafeValues();
            Client.MaxOutstandingRequests = serverFoundationSettings.network.maxOutstandingRequests;
            Server.MaxOutstandingRequests = serverFoundationSettings.network.maxOutstandingRequests;
            Server.InboundMessageGate = (connectionId, messageType, remainingBytes) =>
                ServerFoundation.NetworkGovernor.AllowMessage(connectionId, messageType, remainingBytes);
            Server.MalformedPacketCallback = connectionId =>
                ServerFoundation.NetworkGovernor.ReportMalformedPacket(connectionId);
            _logicUpdater = new LogicUpdater(1.0 / updateFps);
            // Preserve registrations made by derived managers during Awake().
            if (_coreScheduler == null)
                _coreScheduler = new CoreRuntimeScheduler(coreRuntimeSettings);
            RegisterMessages();
        }

        protected virtual void Update()
        {
            // Dedicated/host server path is governed by one aggregate core budget.
            // Network receive is processed before simulation so newly arrived intent can
            // participate in the next authoritative tick. If any stage overruns, lower
            // priority work is skipped for this frame rather than compounding the spike.
            if (IsServer)
            {
                CoreRuntimeScheduler scheduler = CoreScheduler;
                if (!scheduler.IsRunning)
                    scheduler.Start();
                scheduler.BeginFrame();

                double serverReceiveBudget = scheduler.ClampToRemainingFrameBudget(maxServerReceiveMillisecondsPerFrame);
                if (serverReceiveBudget > 0.0)
                    Server.Update(maxServerReceiveEventsPerFrame, (float)serverReceiveBudget);

                if (IsClient)
                {
                    double clientReceiveBudget = scheduler.ClampToRemainingFrameBudget(maxClientReceiveMillisecondsPerFrame);
                    if (clientReceiveBudget > 0.0)
                        Client.Update(maxClientReceiveEventsPerFrame, (float)clientReceiveBudget);
                }

                if ((IsServer || IsClient) && !scheduler.IsFrameBudgetExhausted)
                {
                    double tickBudget = scheduler.ClampToRemainingFrameBudget(maxNetworkTickMillisecondsPerFrame);
                    if (tickBudget > 0.0)
                        _logicUpdater.Update(maxNetworkTicksPerFrame, tickBudget, maxNetworkTickBacklogTicks);
                }

                if (!scheduler.IsFrameBudgetExhausted)
                    scheduler.UpdateScheduledSystems();
                if (!scheduler.IsFrameBudgetExhausted)
                    scheduler.ProcessDelayedTasks();

                scheduler.EndFrame();
                if (_serverFoundation != null && _serverFoundation.IsStarted)
                    _serverFoundation.CaptureFrame();
                return;
            }

            // Pure clients keep the existing lightweight path. Server governance is not
            // allowed to unnecessarily throttle presentation-only clients.
            if (_coreScheduler != null && _coreScheduler.IsRunning)
                _coreScheduler.Stop(true);
            if (IsClient)
            {
                _logicUpdater.Update();
                Client.Update(maxClientReceiveEventsPerFrame, maxClientReceiveMillisecondsPerFrame);
            }
        }

        protected virtual void OnServerUpdate(LogicUpdater updater)
        {
        }

        protected virtual void OnClientUpdate(LogicUpdater updater)
        {
        }

        protected virtual void OnDestroy()
        {
            if (_isApplicationQuitted)
                return;
            StopHost();
            if (_serverFoundation != null)
            {
                _serverFoundation.Dispose();
                _serverFoundation = null;
            }
            /*
            if (_clientTransport != null)
                _clientTransport.Destroy();
            if (_serverTransport != null)
                _serverTransport.Destroy();
            */
        }

        protected virtual void OnApplicationQuit()
        {
            _isApplicationQuitted = true;
            StopHost();
            /*
            if (_clientTransport != null)
                _clientTransport.Destroy();
            if (_serverTransport != null)
                _serverTransport.Destroy();
            */
        }

        /// <summary>
        /// Override this function to register messages
        /// </summary>
        protected virtual void RegisterMessages() { }

        public virtual bool StartServer()
        {
            if (IsServer)
            {
                if (LogError) Logging.LogError(LogTag, "Cannot start server because it was started.");
                return false;
            }
            PrepareServerTransport();
            Server.Transport = ServerTransport;
            if (ServerTransport is LiteNetLibTransport liteNetServerTransport)
                liteNetServerTransport.ConnectionAdmissionPredicate = ServerFoundation.NetworkGovernor.AllowConnectionAttempt;
            if (!Server.StartServer(networkPort, maxConnections))
            {
                if (LogError) Logging.LogError(LogTag, $"Cannot start server at port: {networkPort}.");
                return false;
            }
            _logicUpdater.OnTick -= OnServerUpdate;
            _logicUpdater.OnTick += OnServerUpdate;
            if (!_logicUpdater.IsRunning)
                _logicUpdater.Start();
            IsServer = true;
            CoreScheduler.Start();
            CoreScheduler.BeginStartupGrace();
            ServerFoundation.Start();
            ServerFoundation.ApplyNetworkSimulation(ServerTransport);
            OnStartServer();
            return true;
        }

        public virtual bool StartClient()
        {
            return StartClient(networkAddress, networkPort);
        }

        public virtual bool StartClient(string networkAddress, int networkPort)
        {
            if (IsClient)
            {
                if (LogError) Logging.LogError(LogTag, "Cannot start client because it was started.");
                return false;
            }
            this.networkAddress = networkAddress;
            this.networkPort = networkPort;
            if (LogDev) Logging.Log(LogTag, $"Connecting to {networkAddress}:{networkPort}.");
            PrepareClientTransport();
            Client.Transport = ClientTransport;
            if (!Client.StartClient(networkAddress, networkPort))
            {
                if (LogError) Logging.LogError(LogTag, $"Cannot connect to {networkAddress}:{networkPort}.");
                return false;
            }
            ServerFoundation.ApplyNetworkSimulation(ClientTransport);
            _logicUpdater.OnTick -= OnClientUpdate;
            _logicUpdater.OnTick += OnClientUpdate;
            if (!IsServer && !_logicUpdater.IsRunning)
                _logicUpdater.Start();
            IsClient = true;
            OnStartClient(Client);
            return true;
        }

        public virtual bool StartHost(bool isOfflineConnection = false)
        {
            IsOfflineConnection = isOfflineConnection;
            if (StartServer() && ConnectLocalClient())
            {
                OnStartHost();
                return true;
            }
            return false;
        }

        protected virtual bool ConnectLocalClient()
        {
            return StartClient("localhost", Server.ServerPort);
        }

        public void StopHost()
        {
            OnStopHost();
            StopClient();
            StopServer();
        }

        public void StopServer()
        {
            if (!IsServer)
                return;

            if (LogInfo) Logging.Log(LogTag, "StopServer");
            _logicUpdater.OnTick -= OnServerUpdate;
            if (_logicUpdater.IsRunning)
                _logicUpdater.Stop();
            IsServer = false;
            if (_serverFoundation != null)
                _serverFoundation.Stop();
            if (_coreScheduler != null)
                _coreScheduler.Stop(true);
            if (Server != null)
                Server.StopServer();
            OnStopServer();

            if (IsOfflineConnection && IsClient)
            {
                StopClient();
                IsOfflineConnection = false;
            }
        }

        public void StopClient()
        {
            if (!IsClient)
                return;

            if (LogInfo) Logging.Log(LogTag, "StopClient");
            _logicUpdater.OnTick -= OnClientUpdate;
            if (!IsServer && _logicUpdater.IsRunning)
                _logicUpdater.Stop();
            IsClient = false;
            if (Client != null)
                Client.StopClient();
            OnStopClient();

            if (IsOfflineConnection && IsServer)
            {
                StopServer();
                IsOfflineConnection = false;
            }
        }

        public bool ContainsConnectionId(long connectionId)
        {
            return Server.ConnectionIds.Contains(connectionId);
        }

        public HashSet<long> GetConnectionIds()
        {
            return Server.ConnectionIds;
        }

        #region Packets send / read
        public void ClientSendMessage(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            Client.SendMessage(dataChannel, deliveryMethod, writer);
        }

        public void ClientSendPacket(byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType, SerializerDelegate serializer)
        {
            Client.SendPacket(dataChannel, deliveryMethod, msgType, serializer);
        }

        public void ClientSendPacket<T>(byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType, T messageData, SerializerDelegate extraSerializer = null) where T : INetSerializable
        {
            NetDataWriter writer = Client.s_Writer;
            TransportHandler.WritePacket(writer, msgType);
            messageData.Serialize(writer);
            if (extraSerializer != null)
                extraSerializer.Invoke(writer);
            Client.SendMessage(dataChannel, deliveryMethod, writer);
        }

        public void ClientSendPacket(byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType)
        {
            ClientSendPacket(dataChannel, deliveryMethod, msgType, null);
        }

        public void ServerSendMessage(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            Server.SendMessage(connectionId, dataChannel, deliveryMethod, writer);
        }

        public void ServerSendPacket(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType, SerializerDelegate serializer)
        {
            Server.SendPacket(connectionId, dataChannel, deliveryMethod, msgType, serializer);
        }

        public void ServerSendPacket<T>(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType, T messageData, SerializerDelegate extraSerializer = null) where T : INetSerializable
        {
            NetDataWriter writer = Server.s_Writer;
            TransportHandler.WritePacket(writer, msgType);
            messageData.Serialize(writer);
            if (extraSerializer != null)
                extraSerializer.Invoke(writer);
            Server.SendMessage(connectionId, dataChannel, deliveryMethod, writer);
        }

        public void ServerSendPacket(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType)
        {
            ServerSendPacket(connectionId, dataChannel, deliveryMethod, msgType, null);
        }

        public bool ClientSendRequest<TRequest>(ushort requestType, TRequest request, ResponseDelegate<INetSerializable> responseDelegate = null, int millisecondsTimeout = 30000, SerializerDelegate extraRequestSerializer = null)
            where TRequest : INetSerializable, new()
        {
            return Client.SendRequest(requestType, request, responseDelegate, millisecondsTimeout, extraRequestSerializer);
        }

        public bool ServerSendRequest<TRequest>(long connectionId, ushort requestType, TRequest request, ResponseDelegate<INetSerializable> responseDelegate = null, int millisecondsTimeout = 30000, SerializerDelegate extraRequestSerializer = null)
            where TRequest : INetSerializable, new()
        {
            return Server.SendRequest(connectionId, requestType, request, responseDelegate, millisecondsTimeout, extraRequestSerializer);
        }

        public bool ClientSendRequest<TRequest, TResponse>(ushort requestType, TRequest request, ResponseDelegate<TResponse> responseDelegate, int millisecondsTimeout = 30000, SerializerDelegate extraRequestSerializer = null)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
            return Client.SendRequest(requestType, request, (requestHandler, responseCode, response) =>
            {
                if (!(response is TResponse))
                    response = default(TResponse);
                responseDelegate.Invoke(requestHandler, responseCode, (TResponse)response);
            }, millisecondsTimeout, extraRequestSerializer);
        }

        public UniTask<AsyncResponseData<TResponse>> ClientSendRequestAsync<TRequest, TResponse>(ushort requestType, TRequest request, int millisecondsTimeout = 30000, SerializerDelegate extraRequestSerializer = null)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
            return Client.SendRequestAsync<TRequest, TResponse>(requestType, request, millisecondsTimeout, extraRequestSerializer);
        }

        public bool ServerSendRequest<TRequest, TResponse>(long connectionId, ushort requestType, TRequest request, ResponseDelegate<TResponse> responseDelegate, int millisecondsTimeout = 30000, SerializerDelegate extraRequestSerializer = null)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
            return Server.SendRequest(connectionId, requestType, request, (requestHandler, responseCode, response) =>
            {
                if (!(response is TResponse))
                    response = default(TResponse);
                responseDelegate.Invoke(requestHandler, responseCode, (TResponse)response);
            }, millisecondsTimeout, extraRequestSerializer);
        }

        public UniTask<AsyncResponseData<TResponse>> ServerSendRequestAsync<TRequest, TResponse>(long connectionId, ushort requestType, TRequest request, int millisecondsTimeout = 30000, SerializerDelegate extraRequestSerializer = null)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
            return Server.SendRequestAsync<TRequest, TResponse>(connectionId, requestType, request, millisecondsTimeout, extraRequestSerializer);
        }
        #endregion

        #region Relates components functions
        public void ServerSendMessageToAllConnections(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            Server.SendMessageToAllConnections(dataChannel, deliveryMethod, writer);
        }

        public void ServerSendPacketToAllConnections(byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType, SerializerDelegate serializer)
        {
            Server.SendPacketToAllConnections(dataChannel, deliveryMethod, msgType, serializer);
        }

        public void ServerSendPacketToAllConnections<T>(byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType, T messageData, SerializerDelegate extraSerializer = null) where T : INetSerializable
        {
            NetDataWriter writer = Server.s_Writer;
            TransportHandler.WritePacket(writer, msgType);
            messageData.Serialize(writer);
            if (extraSerializer != null)
                extraSerializer.Invoke(writer);
            Server.SendMessageToAllConnections(dataChannel, deliveryMethod, writer);
        }

        public void ServerSendPacketToAllConnections(byte dataChannel, DeliveryMethod deliveryMethod, ushort msgType)
        {
            Server.SendPacketToAllConnections(dataChannel, deliveryMethod, msgType, null);
        }

        public void RegisterServerMessage(ushort msgType, MessageHandlerDelegate handlerDelegate)
        {
            Server.RegisterMessageHandler(msgType, handlerDelegate);
        }

        public void UnregisterServerMessage(ushort msgType)
        {
            Server.UnregisterMessageHandler(msgType);
        }

        public void RegisterClientMessage(ushort msgType, MessageHandlerDelegate handlerDelegate)
        {
            Client.RegisterMessageHandler(msgType, handlerDelegate);
        }

        public void UnregisterClientMessage(ushort msgType)
        {
            Client.UnregisterMessageHandler(msgType);
        }

        public bool EnableRequestResponse(ushort requestMessageType, ushort responseMessageType)
        {
            return Client.EnableRequestResponse(requestMessageType, responseMessageType) &&
                Server.EnableRequestResponse(requestMessageType, responseMessageType);
        }

        public void DisableRequestResponse()
        {
            Client.DisableRequestResponse();
            Server.DisableRequestResponse();
        }

        public void RegisterRequestToServer<TRequest, TResponse>(ushort reqType, RequestDelegate<TRequest, TResponse> requestHandlerDelegate, ResponseDelegate<TResponse> responseHandlerDelegate = null)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
            Server.RegisterRequestHandler(reqType, requestHandlerDelegate);
            Client.RegisterResponseHandler<TRequest, TResponse>(reqType, responseHandlerDelegate);
        }

        public void UnregisterRequestToServer(ushort reqType)
        {
            Server.UnregisterRequestHandler(reqType);
            Client.UnregisterResponseHandler(reqType);
        }

        public void RegisterRequestToClient<TRequest, TResponse>(ushort reqType, RequestDelegate<TRequest, TResponse> requestHandlerDelegate, ResponseDelegate<TResponse> responseHandlerDelegate = null)
            where TRequest : INetSerializable, new()
            where TResponse : INetSerializable, new()
        {
            Client.RegisterRequestHandler(reqType, requestHandlerDelegate);
            Server.RegisterResponseHandler<TRequest, TResponse>(reqType, responseHandlerDelegate);
        }

        public void UnregisterRequestToClient(ushort reqType)
        {
            Client.UnregisterRequestHandler(reqType);
            Server.UnregisterResponseHandler(reqType);
        }
        #endregion

        #region Network Events Callbacks
        /// <summary>
        /// This event will be called at server when there are any network error
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="socketError"></param>
        public virtual void OnPeerNetworkError(IPEndPoint endPoint, SocketError socketError) { }

        /// <summary>
        /// This event will be called at server when any client connected
        /// </summary>
        /// <param name="connectionId"></param>
        public virtual void OnPeerConnected(long connectionId) { }

        /// <summary>
        /// This event will be called at server when any client disconnected
        /// </summary>
        /// <param name="connectionId"></param>
        /// <param name="reason"></param>
        /// <param name="socketError"></param>
        public virtual void OnPeerDisconnected(long connectionId, DisconnectReason reason, SocketError socketError) { }

        /// <summary>
        /// This event will be called at client when there are any network error
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="socketError"></param>
        public virtual void OnClientNetworkError(IPEndPoint endPoint, SocketError socketError) { }

        /// <summary>
        /// This event will be called at client when connected to server
        /// </summary>
        public virtual void OnClientConnected() { }

        /// <summary>
        /// This event will be called at client when disconnected from server
        /// </summary>
        /// <param name="reason"></param>
        /// <param name="socketError"></param>
        /// <param name="data"></param>
        public virtual void OnClientDisconnected(DisconnectReason reason, SocketError socketError, byte[] data) { }
        #endregion

        #region Start / Stop Callbacks
        // Since there are multiple versions of StartServer, StartClient and StartHost, to reliably customize
        // their functionality, users would need override all the versions. Instead these callbacks are invoked
        // from all versions, so users only need to implement this one case.
        /// <summary>
        /// This hook is invoked when a host is started.
        /// </summary>
        public virtual void OnStartHost()
        {
            if (LogInfo) Logging.Log(LogTag, "OnStartHost");
        }

        /// <summary>
        /// This hook is invoked when a server is started - including when a host is started.
        /// </summary>
        public virtual void OnStartServer()
        {
            if (LogInfo) Logging.Log(LogTag, "OnStartServer");
        }

        /// <summary>
        /// This is a hook that is invoked when the client is started.
        /// </summary>
        /// <param name="client"></param>
        public virtual void OnStartClient(LiteNetLibClient client)
        {
            if (LogInfo) Logging.Log(LogTag, "OnStartClient");
        }

        /// <summary>
        /// This hook is called when a server is stopped - including when a host is stopped.
        /// </summary>
        public virtual void OnStopServer()
        {
            if (LogInfo) Logging.Log(LogTag, "OnStopServer");
        }

        /// <summary>
        /// This hook is called when a client is stopped.
        /// </summary>
        public virtual void OnStopClient()
        {
            if (LogInfo) Logging.Log(LogTag, "OnStopClient");
        }

        /// <summary>
        /// This hook is called when a host is stopped.
        /// </summary>
        public virtual void OnStopHost()
        {
            if (LogInfo) Logging.Log(LogTag, "OnStopHost");
        }
        #endregion

        #region Test functions
#if UNITY_EDITOR

        [ContextMenu("Test Client Disconnect - ConnectionFailed")]
        public void TestClientDisconnect_ConnectionFailed()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.ConnectionFailed,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - Timeout")]
        public void TestClientDisconnect_Timeout()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.Timeout,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - HostUnreachable")]
        public void TestClientDisconnect_HostUnreachable()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.HostUnreachable,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - NetworkUnreachable")]
        public void TestClientDisconnect_NetworkUnreachable()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.NetworkUnreachable,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - RemoteConnectionClose")]
        public void TestClientDisconnect_RemoteConnectionClose()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.RemoteConnectionClose,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - DisconnectPeerCalled")]
        public void TestClientDisconnect_DisconnectPeerCalled()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.DisconnectPeerCalled,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - ConnectionRejected")]
        public void TestClientDisconnect_ConnectionRejected()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.ConnectionRejected,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - InvalidProtocol")]
        public void TestClientDisconnect_InvalidProtocol()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.InvalidProtocol,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - UnknownHost")]
        public void TestClientDisconnect_UnknownHost()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.UnknownHost,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - Reconnect")]
        public void TestClientDisconnect_Reconnect()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.Reconnect,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - PeerToPeerConnection")]
        public void TestClientDisconnect_PeerToPeerConnection()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.PeerToPeerConnection,
                },
            });
        }

        [ContextMenu("Test Client Disconnect - PeerNotFound")]
        public void TestClientDisconnect_PeerNotFound()
        {
            Client.OnClientReceive(new TransportEventData()
            {
                type = ENetworkEvent.DisconnectEvent,
                disconnectInfo = new DisconnectInfo()
                {
                    SocketErrorCode = SocketError.Success,
                    Reason = DisconnectReason.PeerNotFound,
                },
            });
        }
#endif
        #endregion
    }
}
