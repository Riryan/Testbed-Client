using System;
using System.Collections.Generic;
using Game.Shared.Interactions;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only mapping from compact interaction presentation ids to the existing
    /// PlayerHumanoid Animator contract. The server never knows Animator layer/state/trigger
    /// names and no animation asset name crosses the wire.
    ///
    /// This intentionally does not rebuild or normalize PlayerHumanoid.controller.
    /// Bind entries to states/triggers that already exist in the canonical controller.
    /// </summary>
    [CreateAssetMenu(
        fileName = "InteractionAnimationCatalog",
        menuName = "MMO/Presentation/Interaction Animation Catalog")]
    public sealed class InteractionAnimationCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Binding
        {
            [Range(1, 255)] public int presentationId = 1;
            public string label = string.Empty;
            public string layerName = string.Empty;
            public string startState = string.Empty;
            public string startTrigger = string.Empty;
            public string stopState = string.Empty;
            public string stopTrigger = string.Empty;
            [Range(0f, 0.5f)] public float transitionSeconds = 0.08f;

            public byte Id => (byte)Mathf.Clamp(presentationId, 1, 255);

            public Binding Clone() => new Binding
            {
                presentationId = presentationId,
                label = label,
                layerName = layerName,
                startState = startState,
                startTrigger = startTrigger,
                stopState = stopState,
                stopTrigger = stopTrigger,
                transitionSeconds = transitionSeconds,
            };
        }

        [SerializeField] private Binding[] bindings = Array.Empty<Binding>();

        public IReadOnlyList<Binding> Bindings => bindings ?? Array.Empty<Binding>();

        public bool TryGet(byte presentationId, out Binding binding)
        {
            Binding[] values = bindings ?? Array.Empty<Binding>();
            for (int i = 0; i < values.Length; ++i)
            {
                Binding candidate = values[i];
                if (candidate != null && candidate.Id == presentationId)
                {
                    binding = candidate;
                    return true;
                }
            }

            binding = null;
            return false;
        }

        /// <summary>
        /// Runtime fallback keeps the framework usable before an authored catalog exists.
        /// Exact project mappings belong in the Resources catalog, not on the network.
        /// </summary>
        public static Binding CreateRuntimeFallback(byte presentationId)
        {
            if (InteractionPresentationWire.IsHarvest(presentationId))
            {
                return new Binding
                {
                    presentationId = presentationId,
                    label = InteractionPresentationWire.DebugLabel(presentationId),
                    layerName = string.Empty,
                    // Variant-specific authored state can override this in the catalog.
                    startState = presentationId == InteractionPresentationWire.HarvestGeneric
                        ? "CP Harvest"
                        : string.Empty,
                    startTrigger = "Harvest",
                    stopState = string.Empty,
                    stopTrigger = "HarvestCancel",
                    transitionSeconds = 0.08f,
                };
            }

            string label = InteractionPresentationWire.DebugLabel(presentationId);
            return new Binding
            {
                presentationId = presentationId,
                label = label,
                layerName = string.Empty,
                startState = "CP " + label,
                startTrigger = string.Empty,
                stopState = string.Empty,
                stopTrigger = "InteractionCancel",
                transitionSeconds = 0.08f,
            };
        }

#if UNITY_EDITOR
        public void SetBindingsForEditor(Binding[] values)
        {
            bindings = values ?? Array.Empty<Binding>();
            UnityEditor.EditorUtility.SetDirty(this);
        }

        public static Binding[] CreateDefaultBindingsForEditor()
        {
            var result = new List<Binding>(32);

            // Reserve a small authored Harvest starter range. The runtime still supports
            // every compact Harvest presentation id 1..127.
            for (int id = 1; id <= 16; ++id)
            {
                Binding binding = CreateRuntimeFallback((byte)id);
                binding.label = id == 1 ? "Harvest Generic" : $"Harvest Presentation {id}";
                result.Add(binding);
            }

            byte[] pairIds =
            {
                InteractionPresentationWire.GenericInitiator,
                InteractionPresentationWire.GenericReceiver,
                InteractionPresentationWire.InspectInitiator,
                InteractionPresentationWire.InspectReceiver,
                InteractionPresentationWire.TradeInitiator,
                InteractionPresentationWire.TradeReceiver,
                InteractionPresentationWire.PartyInviteInitiator,
                InteractionPresentationWire.PartyInviteReceiver,
                InteractionPresentationWire.GuildInviteInitiator,
                InteractionPresentationWire.GuildInviteReceiver,
                InteractionPresentationWire.FeedInitiator,
                InteractionPresentationWire.FeedReceiver,
                InteractionPresentationWire.HugInitiator,
                InteractionPresentationWire.HugReceiver,
                InteractionPresentationWire.KissInitiator,
                InteractionPresentationWire.KissReceiver,
                InteractionPresentationWire.PartnerDanceInitiator,
                InteractionPresentationWire.PartnerDanceReceiver,
                InteractionPresentationWire.DuelInitiator,
                InteractionPresentationWire.DuelReceiver,
                InteractionPresentationWire.FriendInitiator,
                InteractionPresentationWire.FriendReceiver,
                InteractionPresentationWire.CoupleInitiator,
                InteractionPresentationWire.CoupleReceiver,
                InteractionPresentationWire.TargetedEmoteInitiator,
                InteractionPresentationWire.TargetedEmoteReceiver,
                InteractionPresentationWire.VampireInitiator,
                InteractionPresentationWire.VampireReceiver,
                InteractionPresentationWire.HunterInitiator,
                InteractionPresentationWire.HunterReceiver,
            };

            for (int i = 0; i < pairIds.Length; ++i)
                result.Add(CreateRuntimeFallback(pairIds[i]));

            return result.ToArray();
        }
#endif
    }
}
