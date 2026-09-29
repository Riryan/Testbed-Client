using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Authored Character Select/Creator preview-stage contract. Runtime presentation only
    /// instantiates this prefab; lighting, camera framing and character placement are authored.
    /// </summary>
    public sealed class CharacterPreviewStage : MonoBehaviour
    {
        [SerializeField] private Transform characterAnchor;
        [SerializeField] private Transform fullBodyCameraAnchor;
        [SerializeField] private Transform fullBodyCameraTarget;
        [SerializeField, Range(15f, 75f)] private float fullBodyFieldOfView = 32f;

        [Header("Creator Camera Views")]
        [SerializeField] private Transform faceCameraAnchor;
        [SerializeField] private Transform faceCameraTarget;
        [SerializeField, Range(15f, 75f)] private float faceFieldOfView = 24f;
        [SerializeField] private Transform hairCameraAnchor;
        [SerializeField] private Transform hairCameraTarget;
        [SerializeField, Range(15f, 75f)] private float hairFieldOfView = 25f;
        [SerializeField] private Transform movementCameraAnchor;
        [SerializeField] private Transform movementCameraTarget;
        [SerializeField, Range(15f, 75f)] private float movementFieldOfView = 30f;

        [SerializeField] private int stageVersion = 1;

        public Transform CharacterAnchor => characterAnchor != null ? characterAnchor : transform;
        public Transform FullBodyCameraAnchor => fullBodyCameraAnchor;
        public Transform FullBodyCameraTarget => fullBodyCameraTarget;
        public float FullBodyFieldOfView => fullBodyFieldOfView;
        public int StageVersion => stageVersion;

        public void ConfigureRuntime(
            Transform actor,
            Transform fullCamera,
            Transform fullTarget,
            float fullFov,
            Transform faceCamera,
            Transform faceTarget,
            float faceFov,
            Transform hairCamera,
            Transform hairTarget,
            float hairFov,
            Transform movementCamera,
            Transform movementTarget,
            float movementFov,
            int version)
        {
            characterAnchor = actor;
            fullBodyCameraAnchor = fullCamera;
            fullBodyCameraTarget = fullTarget;
            fullBodyFieldOfView = fullFov;
            faceCameraAnchor = faceCamera;
            faceCameraTarget = faceTarget;
            faceFieldOfView = faceFov;
            hairCameraAnchor = hairCamera;
            hairCameraTarget = hairTarget;
            hairFieldOfView = hairFov;
            movementCameraAnchor = movementCamera;
            movementCameraTarget = movementTarget;
            movementFieldOfView = movementFov;
            stageVersion = version;
        }

        public bool TryGetCreatorView(
            CharacterCreatorCategory category,
            out Transform cameraAnchor,
            out Transform target,
            out float fieldOfView)
        {
            switch (category)
            {
                case CharacterCreatorCategory.Face:
                    cameraAnchor = faceCameraAnchor;
                    target = faceCameraTarget;
                    fieldOfView = faceFieldOfView;
                    break;
                case CharacterCreatorCategory.Hair:
                    cameraAnchor = hairCameraAnchor != null ? hairCameraAnchor : faceCameraAnchor;
                    target = hairCameraTarget != null ? hairCameraTarget : faceCameraTarget;
                    fieldOfView = hairFieldOfView;
                    break;
                case CharacterCreatorCategory.Movement:
                    cameraAnchor = movementCameraAnchor != null ? movementCameraAnchor : fullBodyCameraAnchor;
                    target = movementCameraTarget != null ? movementCameraTarget : fullBodyCameraTarget;
                    fieldOfView = movementFieldOfView;
                    break;
                default:
                    cameraAnchor = fullBodyCameraAnchor;
                    target = fullBodyCameraTarget;
                    fieldOfView = fullBodyFieldOfView;
                    break;
            }

            return cameraAnchor != null && target != null;
        }
    }
}
