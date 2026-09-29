using System.Threading;

namespace Player.Networking
{
    /// <summary>
    /// Local-only UI/input coordination point shared by Game.Client presentation and
    /// Player.Client input without introducing a Client -> Client assembly reference.
    /// It has no network authority and never crosses the wire.
    ///
    /// IMPORTANT: pointer ownership and gameplay capture are intentionally independent.
    /// A normal mouse-driven window may unlock/show the pointer without disabling movement,
    /// combat, interaction shortcuts, or other gameplay input. Only explicit exclusive
    /// captures (text entry, modal consent/popup flows, etc.) use Acquire()/Release().
    /// </summary>
    public static class LocalClientInputGate
    {
        private static int _captureCount;
        private static int _pointerUiCaptureCount;

        /// <summary>
        /// True only while an explicit exclusive input owner is active. Ordinary pointer UI
        /// does not contribute to this count.
        /// </summary>
        public static bool IsGameplayInputBlocked => Volatile.Read(ref _captureCount) > 0;

        /// <summary>
        /// True while at least one UI surface needs a visible/unlocked OS pointer. This is
        /// presentation/cursor state only and must not be interpreted as a gameplay lock.
        /// </summary>
        public static bool IsPointerUiActive => Volatile.Read(ref _pointerUiCaptureCount) > 0;

        public static void Acquire()
        {
            Interlocked.Increment(ref _captureCount);
        }

        public static void Release()
        {
            int next = Interlocked.Decrement(ref _captureCount);
            if (next < 0)
                Interlocked.Exchange(ref _captureCount, 0);
        }

        /// <summary>
        /// Acquires pointer ownership only. This deliberately does not increment the exclusive
        /// gameplay-capture count. Modal/text surfaces that truly need exclusive input must
        /// acquire that separately through Acquire().
        /// </summary>
        public static void AcquirePointerUi()
        {
            Interlocked.Increment(ref _pointerUiCaptureCount);
        }

        public static void ReleasePointerUi()
        {
            int next = Interlocked.Decrement(ref _pointerUiCaptureCount);
            if (next < 0)
                Interlocked.Exchange(ref _pointerUiCaptureCount, 0);
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _captureCount, 0);
            Interlocked.Exchange(ref _pointerUiCaptureCount, 0);
        }
    }
}
