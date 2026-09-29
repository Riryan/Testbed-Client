using UnityEngine;

namespace Player.Client.Presentation
{
    /// <summary>
    /// Purely visual projectile flight. The authoritative hit, miss, damage and timing are
    /// resolved on the standalone GameServer; this component only moves a client FX object.
    /// </summary>
    public sealed class ClientVisibleProjectilePresentation : MonoBehaviour
    {
        private Transform _target;
        private float _speed;
        private float _expiresAt;

        public void Initialize(Transform target, float speed, float maximumLifetime)
        {
            _target = target;
            _speed = Mathf.Max(0.1f, speed);
            _expiresAt = Time.unscaledTime + Mathf.Max(0.1f, maximumLifetime);
        }

        private void Update()
        {
            if (_target == null || Time.unscaledTime >= _expiresAt)
            {
                Destroy(gameObject);
                return;
            }

            Vector3 destination = _target.position;
            Vector3 delta = destination - transform.position;
            float step = _speed * Mathf.Max(0f, Time.deltaTime);
            if (delta.sqrMagnitude <= step * step)
            {
                transform.position = destination;
                Destroy(gameObject);
                return;
            }

            if (delta.sqrMagnitude > 0.000001f)
                transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
            transform.position += delta.normalized * step;
        }
    }
}
