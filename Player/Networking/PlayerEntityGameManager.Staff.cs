using Cysharp.Threading.Tasks;
using Game.Shared.Staff;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public StaffStatusResponseMessage LatestStaffStatus { get; private set; }

        private bool _staffRequestInFlight;

        private void RegisterStaffMessages()
        {
            Client.RegisterResponseHandler<StaffStatusRequestMessage, StaffStatusResponseMessage>(StaffRequestTypes.Status);
            Client.RegisterResponseHandler<StaffSetVisibilityRequestMessage, StaffStatusResponseMessage>(StaffRequestTypes.SetVisibility);
            Client.RegisterResponseHandler<StaffSpectateRequestMessage, StaffStatusResponseMessage>(StaffRequestTypes.Spectate);
            Client.RegisterResponseHandler<StaffStopSpectateRequestMessage, StaffStatusResponseMessage>(StaffRequestTypes.StopSpectate);
        }

        public async UniTask<StaffStatusResponseMessage> RequestStaffStatusAsync(int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected) return StaffStatusResponseMessage.Failed("client is not connected");
            if (_staffRequestInFlight) return StaffStatusResponseMessage.Failed("staff request is already pending locally");
            _staffRequestInFlight = true;
            try
            {
                var response = await ClientSendRequestAsync<StaffStatusRequestMessage, StaffStatusResponseMessage>(
                    StaffRequestTypes.Status, new StaffStatusRequestMessage(), millisecondsTimeout);
                LatestStaffStatus = response.IsSuccess ? response.Response : StaffStatusResponseMessage.Failed($"request failed: {response.ResponseCode}");
                return LatestStaffStatus;
            }
            finally
            {
                _staffRequestInFlight = false;
            }
        }

        public async UniTask<StaffStatusResponseMessage> RequestStaffVisibilityAsync(StaffVisibilityMode mode, int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected) return StaffStatusResponseMessage.Failed("client is not connected");
            if (_staffRequestInFlight) return StaffStatusResponseMessage.Failed("staff request is already pending locally");
            _staffRequestInFlight = true;
            try
            {
                var response = await ClientSendRequestAsync<StaffSetVisibilityRequestMessage, StaffStatusResponseMessage>(
                    StaffRequestTypes.SetVisibility,
                    new StaffSetVisibilityRequestMessage { mode = (byte)mode },
                    millisecondsTimeout);
                LatestStaffStatus = response.IsSuccess ? response.Response : StaffStatusResponseMessage.Failed($"request failed: {response.ResponseCode}");
                return LatestStaffStatus;
            }
            finally
            {
                _staffRequestInFlight = false;
            }
        }

        public async UniTask<StaffStatusResponseMessage> RequestStaffSpectateAsync(long targetCharacterId, int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected) return StaffStatusResponseMessage.Failed("client is not connected");
            if (targetCharacterId <= 0) return StaffStatusResponseMessage.Failed("spectate target is invalid");
            if (_staffRequestInFlight) return StaffStatusResponseMessage.Failed("staff request is already pending locally");
            _staffRequestInFlight = true;
            try
            {
                var response = await ClientSendRequestAsync<StaffSpectateRequestMessage, StaffStatusResponseMessage>(
                    StaffRequestTypes.Spectate,
                    new StaffSpectateRequestMessage { targetCharacterId = targetCharacterId },
                    millisecondsTimeout);
                LatestStaffStatus = response.IsSuccess ? response.Response : StaffStatusResponseMessage.Failed($"request failed: {response.ResponseCode}");
                return LatestStaffStatus;
            }
            finally
            {
                _staffRequestInFlight = false;
            }
        }

        public async UniTask<StaffStatusResponseMessage> RequestStaffStopSpectateAsync(int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected) return StaffStatusResponseMessage.Failed("client is not connected");
            if (_staffRequestInFlight) return StaffStatusResponseMessage.Failed("staff request is already pending locally");
            _staffRequestInFlight = true;
            try
            {
                var response = await ClientSendRequestAsync<StaffStopSpectateRequestMessage, StaffStatusResponseMessage>(
                    StaffRequestTypes.StopSpectate,
                    new StaffStopSpectateRequestMessage(),
                    millisecondsTimeout);
                LatestStaffStatus = response.IsSuccess ? response.Response : StaffStatusResponseMessage.Failed($"request failed: {response.ResponseCode}");
                return LatestStaffStatus;
            }
            finally
            {
                _staffRequestInFlight = false;
            }
        }
        private void ResetClientStaffRequestAdmission()
        {
            _staffRequestInFlight = false;
        }
    }
}
