using Cysharp.Threading.Tasks;
using LiteNetLibManager;

namespace Player.Networking
{
    public partial class PlayerEntityGameManager
    {
        private void RegisterCraftingMessages()
        {
            // Crafting is authoritative only on the standalone GameServer.
            // Unity registers the response contract so requests can be serialized
            // and matched without introducing a client-side request handler.
            Client.RegisterResponseHandler<CraftRequestMessage, CraftResponseMessage>(
                CraftingRequestTypes.Craft);
        }

        public async UniTask<CraftResponseMessage> CraftAsync(
            long stationStableId,
            ushort recipeDataId,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return new CraftResponseMessage { success = false, error = "client is not connected" };
            if (stationStableId <= 0 || recipeDataId == 0)
                return new CraftResponseMessage { success = false, error = "craft request is invalid" };

            AsyncResponseData<CraftResponseMessage> response =
                await ClientSendRequestAsync<CraftRequestMessage, CraftResponseMessage>(
                    CraftingRequestTypes.Craft,
                    new CraftRequestMessage { stationStableId = stationStableId, recipeDataId = recipeDataId },
                    millisecondsTimeout);
            return response.IsSuccess
                ? response.Response
                : new CraftResponseMessage { success = false, error = $"craft request failed: {response.ResponseCode}" };
        }
    }
}
