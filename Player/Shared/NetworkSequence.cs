namespace Player.Shared
{
    /// <summary>Wrap-safe ordering for uint protocol sequence/tick values. Valid when
    /// compared values are less than 2^31 apart, which is the standard serial-number rule.</summary>
    public static class NetworkSequence
    {
        public static bool IsNewer(uint value, uint reference) => unchecked((int)(value - reference)) > 0;
        public static bool IsOlder(uint value, uint reference) => unchecked((int)(value - reference)) < 0;
        public static uint ForwardDistance(uint newer, uint older) => unchecked(newer - older);
    }
}
