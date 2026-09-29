using System;

namespace Player.Networking
{
    public struct MMORemotePresentationSnapshot
    {
        public int activeOwned;
        public int activeRemote;

        public long ownedSpawns;
        public long ownedDestroys;
        public long remoteEnters;
        public long remoteExits;
        public long generationResets;

        public float averageInterpolationDelayMs;
        public float maximumInterpolationDelayMs;
        public float averageJitterMs;
        public float maximumJitterMs;

        public int extrapolating;
        public float maximumExtrapolationMs;

        public float averagePresentationError;
        public float maximumPresentationError;

        public float averageBufferedSnapshots;
        public int maximumBufferedSnapshots;

        public double publishedAt;
    }

    /// <summary>
    /// Process-local client presentation telemetry.
    ///
    /// Remote PlayerEntity spawn/despawn is also the client's observable AOI
    /// enter/leave lifecycle, so remoteEnters/remoteExits are useful for AOI and
    /// reconnect validation without coupling the Networking assembly to Client.
    /// </summary>
    public static class MMORemotePresentationTelemetry
    {
        private static MMORemotePresentationSnapshot s_snapshot;

        public static MMORemotePresentationSnapshot GetSnapshot()
        {
            return s_snapshot;
        }

        public static void Reset()
        {
            s_snapshot = default;
        }

        public static void RecordSpawn(bool owner)
        {
            if (owner)
                s_snapshot.ownedSpawns++;
            else
                s_snapshot.remoteEnters++;
        }

        public static void RecordDestroy(bool owner)
        {
            if (owner)
                s_snapshot.ownedDestroys++;
            else
                s_snapshot.remoteExits++;
        }

        public static void RecordGenerationReset()
        {
            s_snapshot.generationResets++;
        }

        public static void PublishAggregate(
            int activeOwned,
            int activeRemote,
            float averageInterpolationDelayMs,
            float maximumInterpolationDelayMs,
            float averageJitterMs,
            float maximumJitterMs,
            int extrapolating,
            float maximumExtrapolationMs,
            float averagePresentationError,
            float maximumPresentationError,
            float averageBufferedSnapshots,
            int maximumBufferedSnapshots,
            double publishedAt)
        {
            s_snapshot.activeOwned = Math.Max(0, activeOwned);
            s_snapshot.activeRemote = Math.Max(0, activeRemote);

            s_snapshot.averageInterpolationDelayMs = Math.Max(0f, averageInterpolationDelayMs);
            s_snapshot.maximumInterpolationDelayMs = Math.Max(0f, maximumInterpolationDelayMs);
            s_snapshot.averageJitterMs = Math.Max(0f, averageJitterMs);
            s_snapshot.maximumJitterMs = Math.Max(0f, maximumJitterMs);

            s_snapshot.extrapolating = Math.Max(0, extrapolating);
            s_snapshot.maximumExtrapolationMs = Math.Max(0f, maximumExtrapolationMs);

            s_snapshot.averagePresentationError = Math.Max(0f, averagePresentationError);
            s_snapshot.maximumPresentationError = Math.Max(0f, maximumPresentationError);

            s_snapshot.averageBufferedSnapshots = Math.Max(0f, averageBufferedSnapshots);
            s_snapshot.maximumBufferedSnapshots = Math.Max(0, maximumBufferedSnapshots);
            s_snapshot.publishedAt = publishedAt;
        }
    }
}
