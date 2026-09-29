using UnityEngine;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only hit target for direct Character Creator sculpting. A region may advertise
    /// several semantic candidates so the same authored interaction works across different
    /// humanoid models/profiles: the creator resolves the first semantic actually supported
    /// by the active CharacterVisualProfile. The canonical saved value remains
    /// CharacterMorphSelection; this component owns no appearance state and sends no traffic.
    /// </summary>
    public sealed class CharacterCreatorSculptRegion : MonoBehaviour
    {
        [SerializeField] private string[] horizontalChannels = System.Array.Empty<string>();
        [SerializeField] private string[] verticalChannels = System.Array.Empty<string>();
        [SerializeField] private float horizontalMultiplier = 1f;
        [SerializeField] private float verticalMultiplier = 1f;
        [SerializeField] private Vector3 localFollowOffset = Vector3.zero;

        private Transform _followA;
        private Transform _followB;
        private Transform _orientationRoot;

        public string[] HorizontalChannels => horizontalChannels ?? System.Array.Empty<string>();
        public string[] VerticalChannels => verticalChannels ?? System.Array.Empty<string>();
        public float HorizontalMultiplier => horizontalMultiplier;
        public float VerticalMultiplier => verticalMultiplier;

        public void Configure(
            string[] horizontal,
            string[] vertical,
            float horizontalScale = 1f,
            float verticalScale = 1f,
            Vector3 followOffset = default)
        {
            horizontalChannels = horizontal ?? System.Array.Empty<string>();
            verticalChannels = vertical ?? System.Array.Empty<string>();
            horizontalMultiplier = horizontalScale;
            verticalMultiplier = verticalScale;
            localFollowOffset = followOffset;
        }

        public void Configure(string horizontal, string vertical, float horizontalScale = 1f, float verticalScale = 1f)
        {
            Configure(
                string.IsNullOrWhiteSpace(horizontal) ? System.Array.Empty<string>() : new[] { horizontal },
                string.IsNullOrWhiteSpace(vertical) ? System.Array.Empty<string>() : new[] { vertical },
                horizontalScale,
                verticalScale,
                Vector3.zero);
        }

        public void Follow(Transform a, Transform b, Transform orientationRoot, Vector3 followOffset = default)
        {
            _followA = a;
            _followB = b != null ? b : a;
            _orientationRoot = orientationRoot;
            localFollowOffset = followOffset;
            SnapToFollowTarget();
        }

        private void LateUpdate() => SnapToFollowTarget();

        private void SnapToFollowTarget()
        {
            if (_followA == null)
                return;

            Vector3 a = _followA.position;
            Vector3 b = _followB != null ? _followB.position : a;
            Vector3 offset = localFollowOffset;
            if (_orientationRoot != null)
                offset = _orientationRoot.TransformVector(localFollowOffset);

            transform.position = (a + b) * 0.5f + offset;
            if (_orientationRoot != null)
                transform.rotation = _orientationRoot.rotation;
        }
    }
}
