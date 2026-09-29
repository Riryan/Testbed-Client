using System.Collections.Generic;
using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Client-only humanoid ragdoll presentation. Only rigidbodies/colliders that live
    /// directly on humanoid bones are managed, so weapon/prop rigidbodies are not pulled
    /// into the ragdoll automatically. Authoritative death remains server-owned.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumanoidRagdollPresentationController : MonoBehaviour
    {
        public bool autoFollowAuthoritativeDeath = true;
        public bool disableAnimatorWhileRagdolled = true;

        private Animator _animator;
        private Rigidbody[] _bodies = System.Array.Empty<Rigidbody>();
        private Collider[] _colliders = System.Array.Empty<Collider>();
        private bool _ragdollActive;
        private bool _initialized;

        public bool RagdollActive => _ragdollActive;
        public bool HasRagdoll => _bodies.Length > 0;

        private void Awake()
        {
            Initialize();
        }

        public void RebuildRagdollCache()
        {
            _initialized = false;
            Initialize();
        }

        public void SetRagdollActive(bool active)
        {
            Initialize();
            if (_ragdollActive == active)
                return;

            _ragdollActive = active;
            ApplyState();
        }

        private void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            _animator = GetComponentInChildren<Animator>(true);
            if (_animator == null || !_animator.isHuman)
            {
                _bodies = System.Array.Empty<Rigidbody>();
                _colliders = System.Array.Empty<Collider>();
                return;
            }

            var humanoidBones = new HashSet<Transform>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; ++i)
            {
                Transform bone = _animator.GetBoneTransform((HumanBodyBones)i);
                if (bone != null)
                    humanoidBones.Add(bone);
            }

            Rigidbody[] allBodies = GetComponentsInChildren<Rigidbody>(true);
            var bodies = new List<Rigidbody>(allBodies.Length);
            for (int i = 0; i < allBodies.Length; ++i)
            {
                Rigidbody body = allBodies[i];
                if (body != null && humanoidBones.Contains(body.transform))
                    bodies.Add(body);
            }

            var managedBodyTransforms = new HashSet<Transform>();
            for (int i = 0; i < bodies.Count; ++i)
                managedBodyTransforms.Add(bodies[i].transform);

            Collider[] allColliders = GetComponentsInChildren<Collider>(true);
            var colliders = new List<Collider>(allColliders.Length);
            for (int i = 0; i < allColliders.Length; ++i)
            {
                Collider collider = allColliders[i];
                if (collider != null && managedBodyTransforms.Contains(collider.transform))
                    colliders.Add(collider);
            }

            _bodies = bodies.ToArray();
            _colliders = colliders.ToArray();

            _ragdollActive = false;
            ApplyState();
        }

        private void ApplyState()
        {
            for (int i = 0; i < _bodies.Length; ++i)
            {
                Rigidbody body = _bodies[i];
                if (body == null)
                    continue;

                body.isKinematic = !_ragdollActive;
                body.detectCollisions = _ragdollActive;
                if (!_ragdollActive)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }

            for (int i = 0; i < _colliders.Length; ++i)
            {
                if (_colliders[i] != null)
                    _colliders[i].enabled = _ragdollActive;
            }

            if (_animator != null && disableAnimatorWhileRagdolled && HasRagdoll)
                _animator.enabled = !_ragdollActive;
        }
    }
}
