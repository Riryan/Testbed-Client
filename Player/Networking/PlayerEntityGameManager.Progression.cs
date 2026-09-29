using System;
using Cysharp.Threading.Tasks;
using Game.Server.Application.Sessions;
using Game.Shared.Progression;
using LiteNetLibManager;

namespace Player.Networking
{
    public sealed partial class PlayerEntityGameManager
    {
        public event Action<ProgressionSnapshotMessage> ProgressionSnapshotReceived;
        public event Action<ProgressionDeltaMessage> ProgressionDeltaReceived;

        private ProgressionSnapshotMessage _latestProgression;
        private bool _progressionSnapshotRequestInFlight;
        private bool _progressionReconciliationPending;
        public ProgressionSnapshotMessage LatestProgression => _latestProgression;

        private void RegisterProgressionMessages()
        {
            RegisterRequestToServer<ProgressionSnapshotRequestMessage, ProgressionSnapshotMessage>(
                ProgressionRequestTypes.Snapshot,
                HandleProgressionSnapshotRequest);
            RegisterClientMessage(ProgressionMessageTypes.Snapshot, HandleProgressionSnapshotPush);
            RegisterClientMessage(ProgressionMessageTypes.Delta, HandleProgressionDelta);
        }

        public async UniTask<ProgressionSnapshotMessage> RequestProgressionAsync(int millisecondsTimeout = 10000)
        {
            if (!IsClientConnected) return ProgressionSnapshotMessage.Failed("client is not connected");
            if (_progressionSnapshotRequestInFlight) return _latestProgression.success ? _latestProgression : ProgressionSnapshotMessage.Failed("progression request already pending");
            _progressionSnapshotRequestInFlight = true;
            try
            {
                AsyncResponseData<ProgressionSnapshotMessage> response =
                    await ClientSendRequestAsync<ProgressionSnapshotRequestMessage, ProgressionSnapshotMessage>(
                        ProgressionRequestTypes.Snapshot,
                        new ProgressionSnapshotRequestMessage
                        {
                            knownContentRevision = _latestProgression.success ? _latestProgression.contentRevision : 0L,
                            knownRevision = _latestProgression.success ? _latestProgression.revision : 0L,
                        },
                        millisecondsTimeout);
                ProgressionSnapshotMessage result = response.IsSuccess ? response.Response : ProgressionSnapshotMessage.Failed($"progression snapshot request failed: {response.ResponseCode}");
                if (result.IsNotModified && _latestProgression.success)
                    return _latestProgression;
                if (result.success && (!_latestProgression.success || result.revision >= _latestProgression.revision)) ApplyProgressionSnapshot(result);
                return result;
            }
            finally { _progressionSnapshotRequestInFlight = false; }
        }

        private UniTaskVoid HandleProgressionSnapshotRequest(
            RequestHandlerData handler,
            ProgressionSnapshotRequestMessage request,
            RequestProceedResultDelegate<ProgressionSnapshotMessage> result)
        {
            if (!TryGetInWorldPlayerSession(handler.ConnectionId, out PlayerSessionHandle handle) || _characterSessionRuntimeHost == null)
            {
                result(AckResponseCode.Success, ProgressionSnapshotMessage.Failed("character is not in world"));
                return default;
            }
            CharacterProgressionState state = _characterSessionRuntimeHost.GetPlayerProgression(handle);
            result(AckResponseCode.Success, BuildProgressionWire(state, _characterSessionRuntimeHost.Content?.Revision ?? 0));
            return default;
        }

        private void HandleProgressionSnapshotPush(MessageHandlerData handler)
        {
            ProgressionSnapshotMessage snapshot = handler.ReadMessage<ProgressionSnapshotMessage>();
            if (!snapshot.success || (_latestProgression.success && snapshot.revision < _latestProgression.revision)) return;
            ApplyProgressionSnapshot(snapshot);
        }

        private void ApplyProgressionSnapshot(ProgressionSnapshotMessage snapshot)
        {
            _latestProgression = snapshot;
            _progressionReconciliationPending = false;
            ProgressionSnapshotReceived?.Invoke(snapshot);
        }

        private void HandleProgressionDelta(MessageHandlerData handler)
        {
            ProgressionDeltaMessage delta = handler.ReadMessage<ProgressionDeltaMessage>();
            if (!_latestProgression.success)
            {
                ReconcileProgressionAsync().Forget();
                return;
            }
            if (delta.revision < _latestProgression.revision) return;
            if (!ApplyProgressionDelta(ref _latestProgression, delta))
            {
                ReconcileProgressionAsync().Forget();
                return;
            }
            ProgressionDeltaReceived?.Invoke(delta);
            ProgressionSnapshotReceived?.Invoke(_latestProgression);
        }

        private static bool ApplyProgressionDelta(ref ProgressionSnapshotMessage state, ProgressionDeltaMessage delta)
        {
            state.revision = delta.revision;
            switch ((Game.Server.Application.Progression.ProgressionDeltaKind)delta.kind)
            {
                case Game.Server.Application.Progression.ProgressionDeltaKind.Experience:
                    state.experience = delta.value; state.level = (int)delta.auxiliary; return true;
                case Game.Server.Application.Progression.ProgressionDeltaKind.Faction:
                    state.factionDataId = delta.dataId; return true;
                case Game.Server.Application.Progression.ProgressionDeltaKind.KnownRecipe:
                    if (delta.value == 0) return false;
                    ushort[] known = state.knownRecipeDataIds ?? Array.Empty<ushort>();
                    if (Array.BinarySearch(known, delta.dataId) >= 0) return true;
                    var nextKnown = new ushort[known.Length + 1]; Array.Copy(known, nextKnown, known.Length); nextKnown[known.Length] = delta.dataId; Array.Sort(nextKnown); state.knownRecipeDataIds = nextKnown; return true;
                case Game.Server.Application.Progression.ProgressionDeltaKind.KnownAbility:
                    if (delta.value == 0) return false;
                    ushort[] knownAbilities = state.knownAbilityWireIds ?? Array.Empty<ushort>();
                    if (Array.BinarySearch(knownAbilities, delta.dataId) >= 0) return true;
                    var nextAbilities = new ushort[knownAbilities.Length + 1]; Array.Copy(knownAbilities, nextAbilities, knownAbilities.Length); nextAbilities[knownAbilities.Length] = delta.dataId; Array.Sort(nextAbilities); state.knownAbilityWireIds = nextAbilities; return true;
                case Game.Server.Application.Progression.ProgressionDeltaKind.Track:
                    ProgressTrackWire[] tracks = state.tracks ?? Array.Empty<ProgressTrackWire>();
                    for (int i=0;i<tracks.Length;++i) if (tracks[i].dataId == delta.dataId) { tracks[i].value=(int)delta.value; state.tracks=tracks; return true; }
                    var trackNext = new ProgressTrackWire[tracks.Length + 1]; Array.Copy(tracks, trackNext, tracks.Length);
                    trackNext[tracks.Length] = new ProgressTrackWire { dataId = delta.dataId, value = (int)delta.value };
                    state.tracks = trackNext; return true;
                case Game.Server.Application.Progression.ProgressionDeltaKind.Reputation:
                    ReputationWire[] rep = state.reputation ?? Array.Empty<ReputationWire>();
                    for (int i=0;i<rep.Length;++i) if(rep[i].factionDataId==delta.dataId){rep[i].value=(int)delta.value;state.reputation=rep;return true;}
                    var repNext=new ReputationWire[rep.Length+1];Array.Copy(rep,repNext,rep.Length);repNext[rep.Length]=new ReputationWire{factionDataId=delta.dataId,value=(int)delta.value};state.reputation=repNext;return true;
                case Game.Server.Application.Progression.ProgressionDeltaKind.Heat:
                    HeatWire[] heat = state.heat ?? Array.Empty<HeatWire>();
                    for(int i=0;i<heat.Length;++i) if(heat[i].jurisdictionDataId==delta.dataId){heat[i].value=(int)delta.value;heat[i].bounty=delta.auxiliary;heat[i].evidence=delta.extra;state.heat=heat;return true;}
                    var heatNext=new HeatWire[heat.Length+1];Array.Copy(heat,heatNext,heat.Length);heatNext[heat.Length]=new HeatWire{jurisdictionDataId=delta.dataId,value=(int)delta.value,bounty=delta.auxiliary,evidence=delta.extra};state.heat=heatNext;return true;
                default: return false;
            }
        }

        private async UniTaskVoid ReconcileProgressionAsync()
        {
            if (_progressionReconciliationPending || !IsClientConnected) return;
            _progressionReconciliationPending = true;
            try { await RequestProgressionAsync(); }
            finally { _progressionReconciliationPending = false; }
        }


        private void ResetClientProgression()
        {
            _latestProgression = default;
            _progressionSnapshotRequestInFlight = false;
            _progressionReconciliationPending = false;
        }

        private static ProgressionSnapshotMessage BuildProgressionWire(CharacterProgressionState state, long contentRevision)
        {
            state = state ?? CharacterProgressionState.CreateDefault();
            ProgressTrackState[] tracks = state.tracks ?? Array.Empty<ProgressTrackState>(); int tc=0; for(int i=0;i<tracks.Length;++i) if(tracks[i]!=null && tracks[i].value!=0) tc++; var tw = new ProgressTrackWire[tc]; for(int i=0,w=0;i<tracks.Length;++i){if(tracks[i]==null || tracks[i].value==0) continue;tw[w++]=new ProgressTrackWire{dataId=tracks[i].dataId,value=tracks[i].value};}
            ReputationState[] rep = state.reputation ?? Array.Empty<ReputationState>(); var rw=new ReputationWire[rep.Length]; for(int i=0;i<rep.Length;++i) rw[i]=new ReputationWire{factionDataId=rep[i].factionDataId,value=rep[i].value};
            HeatState[] heat=state.heat??Array.Empty<HeatState>();var hw=new HeatWire[heat.Length];for(int i=0;i<heat.Length;++i)hw[i]=new HeatWire{jurisdictionDataId=heat[i].jurisdictionDataId,value=heat[i].value,bounty=heat[i].bounty,evidence=heat[i].evidence};
            return new ProgressionSnapshotMessage{success=true,error=string.Empty,contentRevision=contentRevision,revision=state.revision,experience=state.experience,level=state.level,factionDataId=state.factionDataId,tracks=tw,reputation=rw,heat=hw,knownRecipeDataIds=state.knownRecipeDataIds??Array.Empty<ushort>(),knownAbilityWireIds=state.knownAbilityWireIds??Array.Empty<ushort>()};
        }
    }
}
