using System.Collections.Generic;
using LiteNetLibManager;
using Player.Networking;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// One render-frame presentation loop for every PlayerEntity proxy owned by the same
    /// LiteNetLib manager. This avoids one MonoBehaviour.Update callback per remote entity.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerEntityPresentationSystem : MonoBehaviour
    {
        private readonly List<PlayerEntityClient> _clients = new List<PlayerEntityClient>(256);
        private readonly Dictionary<PlayerEntityClient, int> _indices =
            new Dictionary<PlayerEntityClient, int>(256);

        private double _nextTelemetryPublishAt;

        public int RegisteredClients => _clients.Count;

        public static PlayerEntityPresentationSystem GetOrCreate(LiteNetLibManager.LiteNetLibManager manager)
        {
            if (manager == null)
                return null;

            PlayerEntityPresentationSystem system =
                manager.GetComponent<PlayerEntityPresentationSystem>();

            if (system == null)
                system = manager.gameObject.AddComponent<PlayerEntityPresentationSystem>();

            return system;
        }

        public void Register(PlayerEntityClient client)
        {
            if (client == null || _indices.ContainsKey(client))
                return;

            _indices[client] = _clients.Count;
            _clients.Add(client);
        }

        public void Unregister(PlayerEntityClient client)
        {
            if (client == null || !_indices.TryGetValue(client, out int index))
                return;

            int lastIndex = _clients.Count - 1;
            PlayerEntityClient last = _clients[lastIndex];

            _clients[index] = last;
            _clients.RemoveAt(lastIndex);
            _indices.Remove(client);

            if (index < _clients.Count && last != null)
                _indices[last] = index;
        }

        private void Update()
        {
#if !UNITY_SERVER
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.0001f, 0.1f);
            double now = Time.realtimeSinceStartupAsDouble;
            bool captureTelemetry = now >= _nextTelemetryPublishAt;

            int activeOwned = 0;
            int activeRemote = 0;
            int extrapolating = 0;
            int activeCount = 0;

            float interpolationDelaySum = 0f;
            float interpolationDelayMax = 0f;
            float jitterSum = 0f;
            float jitterMax = 0f;
            float extrapolationMax = 0f;
            float errorSum = 0f;
            float errorMax = 0f;
            int bufferedSum = 0;
            int bufferedMax = 0;

            for (int i = 0; i < _clients.Count; ++i)
            {
                PlayerEntityClient client = _clients[i];
                if (client == null || !client.isActiveAndEnabled)
                    continue;

                client.PresentationUpdate(dt, now);

                if (!captureTelemetry)
                    continue;

                activeCount++;
                if (client.IsOwnerClient)
                    activeOwned++;
                else
                    activeRemote++;

                float delay = client.InterpolationDelayMilliseconds;
                interpolationDelaySum += delay;
                if (delay > interpolationDelayMax)
                    interpolationDelayMax = delay;

                float jitter = client.EstimatedJitterMilliseconds;
                jitterSum += jitter;
                if (jitter > jitterMax)
                    jitterMax = jitter;

                if (client.IsExtrapolating)
                    extrapolating++;

                float extrapolation = client.ExtrapolationMilliseconds;
                if (extrapolation > extrapolationMax)
                    extrapolationMax = extrapolation;

                float error = client.PresentationError;
                errorSum += error;
                if (error > errorMax)
                    errorMax = error;

                int buffered = client.BufferedSnapshots;
                bufferedSum += buffered;
                if (buffered > bufferedMax)
                    bufferedMax = buffered;
            }

            if (captureTelemetry)
            {
                float divisor = Mathf.Max(1, activeCount);

                MMORemotePresentationTelemetry.PublishAggregate(
                    activeOwned,
                    activeRemote,
                    interpolationDelaySum / divisor,
                    interpolationDelayMax,
                    jitterSum / divisor,
                    jitterMax,
                    extrapolating,
                    extrapolationMax,
                    errorSum / divisor,
                    errorMax,
                    bufferedSum / divisor,
                    bufferedMax,
                    now);

                _nextTelemetryPublishAt = now + 0.25;
            }
#endif
        }

        private void OnDestroy()
        {
            _indices.Clear();
            _clients.Clear();
        }
    }
}
