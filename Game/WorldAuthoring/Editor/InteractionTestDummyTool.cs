using System;
using Game.Client.Presentation.Characters;
using Game.Client.UI.Interactions;
using Game.Shared.Interactions;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Small test-fixture authoring surface over the existing WorldObject + WorldInteractable path.
    /// It does not create a second interaction protocol or server-side test command.
    /// </summary>
    public static class InteractionTestDummyTool
    {
        internal const string DefinitionId = "interaction_test_dummy";
        private const string DefaultLabel = "Interaction Test Dummy";

        internal enum TestGroup
        {
            Basic = 0,
            SocialPlayer = 1,
            Vampire = 2,
            Hunter = 3,
        }

        private static readonly InteractionActionId[] BasicActions =
        {
            InteractionActionId.Inspect,
            InteractionActionId.Talk,
            InteractionActionId.Use,
        };

        private static readonly InteractionActionId[] SocialActions =
        {
            InteractionActionId.TradeRequest,
            InteractionActionId.PartyInvite,
            InteractionActionId.GuildInvite,
            InteractionActionId.DuelRequest,
            InteractionActionId.AddFriend,
            InteractionActionId.RemoveFriend,
            InteractionActionId.GiftFriend,
            InteractionActionId.CoupleInvite,
            InteractionActionId.Divorce,
            InteractionActionId.OpenEmotes,
            InteractionActionId.PlayTargetedEmote,
            InteractionActionId.PartnerDance,
            InteractionActionId.Hug,
            InteractionActionId.Kiss,
        };

        private static readonly InteractionActionId[] VampireActions =
        {
            InteractionActionId.SenseBlood,
            InteractionActionId.InspectBlood,
            InteractionActionId.RequestBlood,
            InteractionActionId.OfferBlood,
            InteractionActionId.OfferProtection,
            InteractionActionId.Feed,
            InteractionActionId.Intimidate,
            InteractionActionId.Charm,
            InteractionActionId.Dominate,
            InteractionActionId.Recruit,
            InteractionActionId.Enthrall,
            InteractionActionId.BloodBond,
            InteractionActionId.CreateGhoul,
            InteractionActionId.ReleaseBond,
            InteractionActionId.OfferTurning,
            InteractionActionId.TurnIntoVampire,
            InteractionActionId.MentorProgeny,
            InteractionActionId.CommandProgeny,
            InteractionActionId.ReleaseProgeny,
            InteractionActionId.CommandFollow,
            InteractionActionId.CommandStay,
            InteractionActionId.CommandGuard,
            InteractionActionId.CommandWork,
            InteractionActionId.CommandFeed,
            InteractionActionId.CommandReturnHome,
            InteractionActionId.DismissServant,
            InteractionActionId.Rescue,
            InteractionActionId.VampireEmbrace,
        };

        private static readonly InteractionActionId[] HunterActions =
        {
            InteractionActionId.Scan,
            InteractionActionId.CollectEvidence,
            InteractionActionId.CollectSample,
            InteractionActionId.PhotographEvidence,
            InteractionActionId.Question,
            InteractionActionId.InterviewWitness,
            InteractionActionId.MarkSuspect,
            InteractionActionId.BeginTracking,
            InteractionActionId.SearchTarget,
            InteractionActionId.Restrain,
            InteractionActionId.ReleaseRestraint,
            InteractionActionId.ApplyWard,
            InteractionActionId.TestSupernaturalTrace,
            InteractionActionId.ConfiscateEvidence,
            InteractionActionId.TransferToCell,
            InteractionActionId.RecruitInformant,
            InteractionActionId.RequestCooperation,
            InteractionActionId.ShareIntel,
            InteractionActionId.ReportToCell,
        };

        [MenuItem(
            "MMO Tools/World/Add World Interactable/Interaction Test Dummy",
            false,
            22)]
        private static void AddInteractionTestDummy()
        {
            CharacterVisualProfile visualProfile = CharacterVisualProfileRegistry.Default;
            if (visualProfile == null || visualProfile.EditableBasePrefab == null)
            {
                EditorUtility.DisplayDialog(
                    "Interaction Test Dummy",
                    "The default CharacterVisualProfile or EditableBasePrefab could not be resolved. " +
                    "The fixture intentionally reuses the existing Player visual instead of generating another model path.",
                    "OK");
                return;
            }

            GameObject root = new GameObject(DefaultLabel);
            Undo.RegisterCreatedObjectUndo(root, "Add Interaction Test Dummy");

            CapsuleCollider targetCollider = Undo.AddComponent<CapsuleCollider>(root);
            targetCollider.isTrigger = true;
            targetCollider.center = new Vector3(0f, 0.9f, 0f);
            targetCollider.height = 1.9f;
            targetCollider.radius = 0.58f;

            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            ConfigureBase(interactable, DefaultLabel);
            // Test fixture intentionally has no authored alignment slot. The player must
            // already be in valid interaction range; the GameServer validates range normally.
            interactable.slots = Array.Empty<WorldInteractable.Slot>();

            GameObject visual =
                PrefabUtility.InstantiatePrefab(visualProfile.EditableBasePrefab) as GameObject;
            if (visual == null)
                visual = UnityEngine.Object.Instantiate(visualProfile.EditableBasePrefab);

            if (visual == null)
            {
                Undo.DestroyObjectImmediate(root);
                EditorUtility.DisplayDialog(
                    "Interaction Test Dummy",
                    "The existing Player editable-base visual could not be instantiated.",
                    "OK");
                return;
            }

            Undo.RegisterCreatedObjectUndo(visual, "Add Interaction Test Dummy Visual");
            visual.name = "Receiver Player Visual";
            Undo.SetTransformParent(visual.transform, root.transform, "Parent Interaction Test Dummy Visual");
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            // The visual is presentation only. Keep the world root trigger as the single client
            // interaction target and keep the humanoid visual out of server world geometry.
            Collider[] visualColliders = visual.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < visualColliders.Length; ++i)
                visualColliders[i].enabled = false;

            if (visual.GetComponent<ServerBakeIgnore>() == null)
                Undo.AddComponent<ServerBakeIgnore>(visual);

            Undo.AddComponent<InteractionTestDummyPresentation>(root);

            ConfigureAction(interactable, InteractionActionId.Hug);

            PositionAtScenePivot(root.transform);
            EditorUtility.SetDirty(interactable);
            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            Debug.Log(
                "[Interaction Test Dummy] Added. Choose Test Group + Interaction in the Inspector, " +
                "save the scene, run Server World Bake V2, then test through the normal interaction UI.");
        }

        internal static bool IsFixture(WorldInteractable interactable) =>
            interactable != null &&
            string.Equals(
                interactable.definitionId,
                DefinitionId,
                StringComparison.OrdinalIgnoreCase);

        internal static TestGroup GroupFor(InteractionActionId actionId)
        {
            ushort raw = (ushort)actionId;
            if (raw >= 400 && raw <= 427)
                return TestGroup.Vampire;
            if (raw >= 500 && raw <= 543)
                return TestGroup.Hunter;

            for (int i = 0; i < SocialActions.Length; ++i)
                if (SocialActions[i] == actionId)
                    return TestGroup.SocialPlayer;

            return TestGroup.Basic;
        }

        internal static InteractionActionId[] ActionsFor(TestGroup group)
        {
            switch (group)
            {
                case TestGroup.SocialPlayer: return SocialActions;
                case TestGroup.Vampire: return VampireActions;
                case TestGroup.Hunter: return HunterActions;
                default: return BasicActions;
            }
        }

        internal static string GroupLabel(TestGroup group)
        {
            switch (group)
            {
                case TestGroup.SocialPlayer: return "Social / Player";
                case TestGroup.Vampire: return "Vampire";
                case TestGroup.Hunter: return "Hunter";
                default: return "Basic";
            }
        }

        internal static string ActionLabel(InteractionActionId actionId)
        {
            string value = actionId.ToString();
            if (string.IsNullOrEmpty(value))
                return "Interaction";

            System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length + 8);
            builder.Append(value[0]);
            for (int i = 1; i < value.Length; ++i)
            {
                char c = value[i];
                if (char.IsUpper(c) && !char.IsUpper(value[i - 1]))
                    builder.Append(' ');
                builder.Append(c);
            }
            return builder.ToString();
        }

        internal static void ConfigureBase(
            WorldInteractable interactable,
            string label)
        {
            if (interactable == null)
                return;

            interactable.definitionId = DefinitionId;
            interactable.kind = ServerWorldInteractableKind.Generic;
            interactable.gameplayProfileId = DefinitionId;
            interactable.lootTableId = string.Empty;
            interactable.craftingStationId = string.Empty;
            interactable.factionDefinitionId = string.Empty;
            interactable.label = string.IsNullOrWhiteSpace(label) ? DefaultLabel : label.Trim();
            interactable.surveillanceCamera = false;
            interactable.persistentState = false;
            interactable.enabledByDefault = true;
            interactable.dynamicBlocker = false;
        }

        internal static void ConfigureAction(
            WorldInteractable interactable,
            InteractionActionId actionId)
        {
            if (interactable == null)
                return;

            ConfigureBase(
                interactable,
                string.IsNullOrWhiteSpace(interactable.label)
                    ? DefaultLabel
                    : interactable.label);

            // IMPORTANT: the Interaction Test Dummy is deliberately an INSTANT authoritative
            // SceneObject interaction. The server validates range/action/state and returns the
            // normal ContextInteraction result, but does not create a timed world session and
            // therefore does not align/warp the Player to an authored slot.
            //
            // Presentation duration remains client-local test presentation after accepted success.
            InteractionFeature feature = FeatureFor(actionId);

            interactable.slots = Array.Empty<WorldInteractable.Slot>();

            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "interaction_test_" + actionId.ToString().ToLowerInvariant(),
                    categoryId = InteractionCategoryId.Use,
                    actionId = actionId,
                    displayLabel = ActionLabel(actionId),
                    maximumUseDistance = InteractionRangePolicy.WorldObjectUseRange,
                    maximumFacingAngle = 180f,
                    exclusiveOccupancy = false,
                    looping = false,
                    fixedDurationSeconds = 0f,
                    lockMovement = false,
                    lockRotation = false,
                    cancelOnDamage = false,
                    cancelOnMovement = false,
                    cancelOnTargetUnavailable = true,

                    // The dummy is the auto-accepting receiver for visual testing. Production
                    // Player<->Player consent semantics are not bypassed or modified.
                    consentMode = InteractionConsentMode.None,
                    contentLevel = ContentFor(actionId),
                    feature = feature,
                }
            };
        }

        internal static void Repair(WorldInteractable interactable)
        {
            if (interactable == null)
                return;

            Undo.RecordObject(interactable, "Repair Interaction Test Dummy");

            InteractionActionId actionId =
                interactable.interactions != null &&
                interactable.interactions.Length > 0 &&
                interactable.interactions[0] != null &&
                interactable.interactions[0].actionId != InteractionActionId.None
                    ? interactable.interactions[0].actionId
                    : InteractionActionId.Hug;

            ConfigureAction(interactable, actionId);

            if (interactable.GetComponent<WorldObject>() == null)
                Undo.AddComponent<WorldObject>(interactable.gameObject);

            if (interactable.GetComponent<InteractionTestDummyPresentation>() == null)
                Undo.AddComponent<InteractionTestDummyPresentation>(interactable.gameObject);
            // Upgrade older test fixtures that used a timed session + alignment slot.
            // Keeping slots empty is what guarantees the fixture never requests server alignment.
            interactable.slots = Array.Empty<WorldInteractable.Slot>();

            Transform legacyAnchor = interactable.transform.Find("Initiator Anchor");
            if (legacyAnchor != null)
                Undo.DestroyObjectImmediate(legacyAnchor.gameObject);

            EditorUtility.SetDirty(interactable);
        }

        internal static bool IsNoWarpConfigured(WorldInteractable interactable)
        {
            if (interactable == null ||
                interactable.interactions == null ||
                interactable.interactions.Length != 1 ||
                interactable.interactions[0] == null)
            {
                return false;
            }

            WorldInteractable.Interaction action = interactable.interactions[0];
            bool noSession =
                !action.looping &&
                action.fixedDurationSeconds <= 0f &&
                action.consentMode == InteractionConsentMode.None;

            bool noAlignmentSlot =
                interactable.slots == null ||
                interactable.slots.Length == 0;

            return noSession &&
                   noAlignmentSlot &&
                   !action.lockMovement &&
                   !action.lockRotation;
        }

        private static InteractionFeature FeatureFor(InteractionActionId actionId)
        {
            ushort raw = (ushort)actionId;
            if (raw >= 400 && raw <= 427)
                return InteractionFeature.Vampire;
            if (raw >= 500 && raw <= 543)
                return InteractionFeature.Hunter;

            switch (actionId)
            {
                case InteractionActionId.OpenEmotes:
                case InteractionActionId.PlayTargetedEmote:
                case InteractionActionId.PartnerDance:
                case InteractionActionId.Hug:
                case InteractionActionId.Kiss:
                    return InteractionFeature.Emotes;

                case InteractionActionId.AddFriend:
                case InteractionActionId.RemoveFriend:
                case InteractionActionId.GiftFriend:
                    return InteractionFeature.Friendship;

                case InteractionActionId.CoupleInvite:
                case InteractionActionId.Divorce:
                    return InteractionFeature.Marriage;

                case InteractionActionId.TradeRequest:
                case InteractionActionId.PartyInvite:
                case InteractionActionId.GuildInvite:
                case InteractionActionId.DuelRequest:
                    return InteractionFeature.PlayerSocial;

                default:
                    return InteractionFeature.Core;
            }
        }

        private static InteractionContentLevel ContentFor(InteractionActionId actionId)
        {
            switch (actionId)
            {
                case InteractionActionId.Feed:
                case InteractionActionId.BloodBond:
                case InteractionActionId.CreateGhoul:
                case InteractionActionId.OfferTurning:
                case InteractionActionId.TurnIntoVampire:
                case InteractionActionId.VampireEmbrace:
                case InteractionActionId.Kiss:
                    return InteractionContentLevel.Mature;

                default:
                    return InteractionContentLevel.General;
            }
        }

        private static void PositionAtScenePivot(Transform transform)
        {
            if (transform == null || SceneView.lastActiveSceneView == null)
                return;

            Vector3 pivot = SceneView.lastActiveSceneView.pivot;
            transform.position = new Vector3(
                pivot.x,
                Mathf.Max(0f, pivot.y),
                pivot.z);
        }
    }
}
