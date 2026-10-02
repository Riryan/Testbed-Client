using System;
using Game.Shared.Interactions;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Drives the already-authored PlayerHumanoid interaction/action contract:
    /// CP_ActionActive + CP_ActionId + CP_ActionPhase, on the controller's existing
    /// zero-default-weight presentation layers.
    ///
    /// Client presentation only: no gameplay authority or network protocol.
    /// </summary>
    public static class CanonicalInteractionAnimatorDriver
    {
        public const int StartPhase = 1;
        public const int LoopPhase = 2;
        public const int FinishPhase = 3;

        public const float StartPhaseHoldSeconds = 0.55f;
        public const float FinishPhaseHoldSeconds = 0.55f;

        private const string ActionActiveParameter = "CP_ActionActive";
        private const string ActionIdParameter = "CP_ActionId";
        private const string ActionPhaseParameter = "CP_ActionPhase";

        private const string PartnerLayer = "Client Presentation Partner Actions";
        private const string VampireLayer = "Client Presentation Vampire Actions";
        private const string HunterLayer = "Client Presentation Hunter Actions";
        private const string GenericActionLayer = "Client Presentation Actions";

        private static readonly int ActionActiveHash = Animator.StringToHash(ActionActiveParameter);
        private static readonly int ActionIdHash = Animator.StringToHash(ActionIdParameter);
        private static readonly int ActionPhaseHash = Animator.StringToHash(ActionPhaseParameter);

        public static bool TryResolvePresentation(
            byte presentationId,
            out int controllerActionId)
        {
            switch (presentationId)
            {
                case InteractionPresentationWire.FeedInitiator:
                    controllerActionId = 400;
                    return true;
                case InteractionPresentationWire.FeedReceiver:
                    controllerActionId = 401;
                    return true;

                case InteractionPresentationWire.PartnerDanceInitiator:
                    controllerActionId = 600;
                    return true;
                case InteractionPresentationWire.PartnerDanceReceiver:
                    controllerActionId = 601;
                    return true;

                case InteractionPresentationWire.HugInitiator:
                    controllerActionId = 602;
                    return true;
                case InteractionPresentationWire.HugReceiver:
                    controllerActionId = 603;
                    return true;

                case InteractionPresentationWire.KissInitiator:
                    controllerActionId = 604;
                    return true;
                case InteractionPresentationWire.KissReceiver:
                    controllerActionId = 605;
                    return true;

                default:
                    controllerActionId = 0;
                    return false;
            }
        }

        public static bool TryResolveAction(
            InteractionActionId actionId,
            bool receiver,
            out int controllerActionId)
        {
            switch (actionId)
            {
                case InteractionActionId.Feed:
                    controllerActionId = receiver ? 401 : 400;
                    return true;

                case InteractionActionId.PartnerDance:
                    controllerActionId = receiver ? 601 : 600;
                    return true;

                case InteractionActionId.Hug:
                    controllerActionId = receiver ? 603 : 602;
                    return true;

                case InteractionActionId.Kiss:
                    controllerActionId = receiver ? 605 : 604;
                    return true;

                default:
                    return TryResolvePresentation(
                        InteractionPresentationWire.EncodeInteraction(actionId, receiver),
                        out controllerActionId);
            }
        }

        public static bool HasCanonicalContract(
            Animator animator,
            out string failure)
        {
            failure = string.Empty;
            if (animator == null || animator.runtimeAnimatorController == null)
            {
                failure = "Animator/controller is unavailable";
                return false;
            }

            bool active = false;
            bool id = false;
            bool phase = false;

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; ++i)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter == null)
                    continue;

                if (string.Equals(parameter.name, ActionActiveParameter, StringComparison.Ordinal) &&
                    parameter.type == AnimatorControllerParameterType.Bool)
                {
                    active = true;
                }
                else if (string.Equals(parameter.name, ActionIdParameter, StringComparison.Ordinal) &&
                         parameter.type == AnimatorControllerParameterType.Int)
                {
                    id = true;
                }
                else if (string.Equals(parameter.name, ActionPhaseParameter, StringComparison.Ordinal) &&
                         parameter.type == AnimatorControllerParameterType.Int)
                {
                    phase = true;
                }
            }

            if (active && id && phase)
                return true;

            failure = "canonical PlayerHumanoid action parameters are missing";
            return false;
        }

        public static bool Begin(
            Animator animator,
            int controllerActionId,
            out int layerIndex,
            out float previousLayerWeight,
            out string failure)
        {
            layerIndex = -1;
            previousLayerWeight = 0f;

            if (controllerActionId <= 0)
            {
                failure = "controller action id is invalid";
                return false;
            }

            if (!HasCanonicalContract(animator, out failure))
                return false;

            string layerName = ResolveLayerName(controllerActionId);
            layerIndex = animator.GetLayerIndex(layerName);
            if (layerIndex < 0)
            {
                failure = $"PlayerHumanoid layer '{layerName}' is missing";
                return false;
            }

            previousLayerWeight = animator.GetLayerWeight(layerIndex);

            // The supplied PlayerHumanoid.controller deliberately has these presentation layers
            // at default weight 0. The interaction presentation owner must raise the active layer.
            animator.SetLayerWeight(layerIndex, 1f);

            // Complete the semantic sample before enabling the action.
            animator.SetInteger(ActionIdHash, controllerActionId);
            animator.SetInteger(ActionPhaseHash, StartPhase);
            animator.SetBool(ActionActiveHash, true);
            return true;
        }

        public static bool SetPhase(Animator animator, int phase)
        {
            if (!HasCanonicalContract(animator, out _))
                return false;

            animator.SetInteger(
                ActionPhaseHash,
                Mathf.Clamp(phase, StartPhase, FinishPhase));
            return true;
        }

        public static void Clear(
            Animator animator,
            int layerIndex,
            float previousLayerWeight)
        {
            if (animator == null)
                return;

            if (HasCanonicalContract(animator, out _))
            {
                animator.SetBool(ActionActiveHash, false);
                animator.SetInteger(ActionPhaseHash, 0);
                animator.SetInteger(ActionIdHash, 0);
            }

            if (layerIndex >= 0 && layerIndex < animator.layerCount)
                animator.SetLayerWeight(layerIndex, Mathf.Clamp01(previousLayerWeight));
        }

        private static string ResolveLayerName(int controllerActionId)
        {
            if (controllerActionId >= 600 && controllerActionId <= 699)
                return PartnerLayer;

            if (controllerActionId >= 400 && controllerActionId <= 499)
                return VampireLayer;

            if (controllerActionId >= 500 && controllerActionId <= 599)
                return HunterLayer;

            return GenericActionLayer;
        }
    }
}
