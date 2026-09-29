using System.Diagnostics;

namespace LiteNetLibManager
{
    public class LogicUpdater
    {
        private const int DefaultMaxTicksPerUpdate = 5;
        private const int DefaultMaxBacklogTicks = 8;

        public bool IsRunning => _stopwatch != null && _stopwatch.IsRunning;

        /// <summary>
        /// Local tick count
        /// </summary>
        public uint LocalTick { get; private set; } = 0;

        /// <summary>
        /// Tick count
        /// </summary>
        public uint Tick => (uint)(LocalTick + _tickOffsets);

        /// <summary>
        /// Fixed delta time
        /// </summary>
        public double DeltaTime { get; private set; }

        /// <summary>
        /// Fixed delta time (float for less precision)
        /// </summary>
        public float DeltaTimeF { get; private set; }

        /// <summary>
        /// Last governed Update() wall-clock cost.
        /// </summary>
        public double LastUpdateMilliseconds { get; private set; }

        /// <summary>
        /// Number of fixed ticks discarded because the bounded backlog cap was exceeded.
        /// </summary>
        public long DroppedBacklogTicks { get; private set; }

        /// <summary>
        /// Number of Update calls that could not drain all due ticks because a tick/time budget was reached.
        /// </summary>
        public long BudgetHitCount { get; private set; }

        public event LogicUpdateDelegate OnTick;

        private long _deltaTimeTicks = 0;
        private long _accumulator = 0;
        private long _lastTime = 0;
        private uint _latestSyncedTick = 0;
        private int _tickOffsets = 0;

        private readonly Stopwatch _stopwatch;

        public LogicUpdater(double deltaTime)
        {
            _stopwatch = new Stopwatch();
            SetDeltaTime(deltaTime);
        }

        public LogicUpdater() : this(1.0 / 30)
        {
        }

        public void SetDeltaTime(double deltaTime)
        {
            DeltaTime = deltaTime;
            DeltaTimeF = (float)DeltaTime;
            _deltaTimeTicks = (long)(DeltaTime * Stopwatch.Frequency);
            if (_deltaTimeTicks < 1)
                _deltaTimeTicks = 1;
        }

        public void Start()
        {
            Reset();
        }

        public void Stop()
        {
            _stopwatch.Stop();
        }

        public void Reset()
        {
            LocalTick = 0;
            _accumulator = 0;
            _lastTime = 0;
            LastUpdateMilliseconds = 0.0;
            _stopwatch.Restart();
        }

        protected virtual void InvokeOnTick()
        {
            if (OnTick != null)
                OnTick.Invoke(this);
        }

        /// <summary>
        /// Backward-compatible update. Uses bounded catch-up but no wall-clock budget.
        /// </summary>
        public void Update()
        {
            Update(DefaultMaxTicksPerUpdate, 0.0, DefaultMaxBacklogTicks);
        }

        /// <summary>
        /// Governed fixed-tick update.
        /// maxTicksPerUpdate and maxBacklogTicks are always clamped to at least 1.
        /// maxMilliseconds <= 0 means no local wall-clock limit; the caller may still
        /// impose an outer frame budget.
        ///
        /// A running OnTick callback cannot be safely pre-empted on Unity's main thread.
        /// The budget is therefore checked between ticks. If one tick overruns, no
        /// additional catch-up ticks are executed in this update.
        /// </summary>
        public int Update(int maxTicksPerUpdate, double maxMilliseconds, int maxBacklogTicks)
        {
            if (!IsRunning)
                return 0;

            if (maxTicksPerUpdate < 1)
                maxTicksPerUpdate = 1;
            if (maxBacklogTicks < 1)
                maxBacklogTicks = 1;

            long updateStart = Stopwatch.GetTimestamp();
            long elapsedTime = _stopwatch.ElapsedTicks;
            long ticksDelta = elapsedTime - _lastTime;
            if (ticksDelta < 0)
                ticksDelta = 0;
            _accumulator += ticksDelta;
            _lastTime = elapsedTime;

            long maxAccumulator = _deltaTimeTicks * (long)maxBacklogTicks;
            if (_accumulator > maxAccumulator)
            {
                long droppedTicks = (_accumulator - maxAccumulator) / _deltaTimeTicks;
                if (droppedTicks < 1)
                    droppedTicks = 1;
                DroppedBacklogTicks += droppedTicks;
                _accumulator = maxAccumulator;
            }

            int updates = 0;
            bool budgetHit = false;
            while (_accumulator >= _deltaTimeTicks)
            {
                if (updates >= maxTicksPerUpdate)
                {
                    budgetHit = true;
                    break;
                }

                if (maxMilliseconds > 0.0 && ElapsedMilliseconds(updateStart) >= maxMilliseconds)
                {
                    budgetHit = true;
                    break;
                }

                InvokeOnTick();
                LocalTick++;
                _accumulator -= _deltaTimeTicks;
                updates++;

                if (maxMilliseconds > 0.0 && ElapsedMilliseconds(updateStart) >= maxMilliseconds && _accumulator >= _deltaTimeTicks)
                {
                    budgetHit = true;
                    break;
                }
            }

            LastUpdateMilliseconds = ElapsedMilliseconds(updateStart);
            if (budgetHit)
                BudgetHitCount++;
            return updates;
        }

        private static double ElapsedMilliseconds(long startTimestamp)
        {
            long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
            return elapsed * 1000.0 / Stopwatch.Frequency;
        }

        public static uint TimeToTick(long milliseconds, double deltaTime)
        {
            return (uint)(milliseconds / 1000 / deltaTime);
        }

        public static uint TimeToTickF(float milliseconds, float deltaTime)
        {
            return (uint)(milliseconds / 1000f / deltaTime);
        }

        public static uint TimeInSecondsToTick(long seconds, double deltaTime)
        {
            return (uint)(seconds / deltaTime);
        }

        public static uint TimeInSecondsToTickF(float seconds, float deltaTime)
        {
            return (uint)(seconds / deltaTime);
        }

        public void OnSyncTick(uint tick, long rtt)
        {
            if (_latestSyncedTick > tick)
                return;
            _latestSyncedTick = tick;
            uint newTick = tick + TimeToTick(rtt / 2, DeltaTime);
            _tickOffsets = (int)newTick - (int)LocalTick;
        }
    }
}
