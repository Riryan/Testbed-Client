using System;
using Cysharp.Threading.Tasks;
using Game.Shared.Interactions;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<ContextInteractionResponseMessage> ContextInteractionResultReceived;
        public event Action<WorldInteractableStateMessage> WorldInteractableStateReceived;
        public event Action<PlayerHarvestEventMessage> HarvestEventReceived;

        private bool _contextInteractionRequestInFlight;
        private double _nextContextInteractionRequestAt;

        private void RegisterContextInteractionMessages()
        {
            RegisterRequestToServer<ContextInteractionRequestMessage, ContextInteractionResponseMessage>(
                PlayerGameplayActionRequestTypes.ContextInteraction,
                HandleContextInteractionRequest);
            RegisterClientMessage(
                PlayerGameplayActionMessageTypes.WorldInteractableState,
                HandleWorldInteractableState);
            RegisterClientMessage(
                PlayerHarvestMessageTypes.HarvestEvent,
                HandleHarvestEvent);
            RegisterWorldLootMessages();
        }

        public async UniTask<ContextInteractionResponseMessage> RequestContextInteractionAsync(
            InteractionTargetReferenceWire target,
            InteractionCategoryId categoryId,
            InteractionActionId actionId,
            uint sequence,
            int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected)
                return ContextInteractionResponseMessage.Failed(sequence, target, categoryId, actionId, InteractionResultCode.InvalidState, "client is not connected");
            if (!target.IsValid)
                return ContextInteractionResponseMessage.Failed(sequence, target, categoryId, actionId, InteractionResultCode.InvalidTarget, "interaction target is invalid");

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (_contextInteractionRequestInFlight || now < _nextContextInteractionRequestAt)
                return ContextInteractionResponseMessage.Failed(sequence, target, categoryId, actionId, InteractionResultCode.Rejected, "interaction is locally pending or rate-limited");

            _contextInteractionRequestInFlight = true;
            _nextContextInteractionRequestAt = now + 0.10d;
            try
            {
                AsyncResponseData<ContextInteractionResponseMessage> response =
                    await ClientSendRequestAsync<ContextInteractionRequestMessage, ContextInteractionResponseMessage>(
                        PlayerGameplayActionRequestTypes.ContextInteraction,
                        new ContextInteractionRequestMessage
                        {
                            target = target,
                            categoryId = (ushort)categoryId,
                            actionId = (ushort)actionId,
                            sequence = sequence,
                        },
                        millisecondsTimeout);

                ContextInteractionResponseMessage result = response.IsSuccess
                    ? response.Response
                    : ContextInteractionResponseMessage.Failed(sequence, target, categoryId, actionId, InteractionResultCode.Rejected, $"interaction request failed: {response.ResponseCode}");
                // Correlated request fields are not repeated in the response payload.
                result.sequence = sequence;
                result.target = target;
                result.categoryId = (ushort)categoryId;
                result.actionId = (ushort)actionId;
                ContextInteractionResultReceived?.Invoke(result);
                return result;
            }
            finally
            {
                _contextInteractionRequestInFlight = false;
            }
        }


        private void ResetClientContextInteractionRequests()
        {
            _contextInteractionRequestInFlight = false;
            _nextContextInteractionRequestAt = 0d;
        }

        private static bool SameInteractionTarget(
            InteractionTargetReferenceWire left,
            InteractionTargetReferenceWire right) =>
            left.kind == right.kind &&
            left.objectId == right.objectId &&
            left.generation == right.generation &&
            left.primaryId == right.primaryId &&
            left.secondaryId == right.secondaryId;

        private void HandleWorldInteractableState(MessageHandlerData handler)
        {
            WorldInteractableStateMessage message = handler.ReadMessage<WorldInteractableStateMessage>();
            WorldInteractableStateReceived?.Invoke(message);
        }


        private void HandleHarvestEvent(MessageHandlerData handler)
        {
            PlayerHarvestEventMessage message = handler.ReadMessage<PlayerHarvestEventMessage>();
            HarvestEventReceived?.Invoke(message);
        }

        private UniTaskVoid HandleContextInteractionRequest(
            RequestHandlerData handler,
            ContextInteractionRequestMessage request,
            RequestProceedResultDelegate<ContextInteractionResponseMessage> result)
        {
            // Standalone GameServer owns authoritative execution.
            result(AckResponseCode.Success, ContextInteractionResponseMessage.Failed(
                request.sequence,
                request.target,
                request.CategoryId,
                request.ActionId,
                InteractionResultCode.Unsupported,
                "standalone GameServer owns context interaction execution"));
            return default;
        }
    }
}
