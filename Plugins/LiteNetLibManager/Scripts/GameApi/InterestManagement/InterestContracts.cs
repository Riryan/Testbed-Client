namespace LiteNetLibManager
{
    /// <summary>
    /// Optional logical world/instance partition. If absent, AOI uses map=0 and
    /// instance=0 (plus Unity scene when requireSameUnityScene is enabled).
    /// </summary>
    public interface IMMOInterestPartitionProvider
    {
        int InterestMapId { get; }
        int InterestInstanceId { get; }
    }

    /// <summary>
    /// Optional per-network-object observer-cap override. Values <= 0 use the
    /// AOI default. Overrides are still clamped by the manager hard cap.
    /// </summary>
    public interface IMMOObserverCapPolicy
    {
        int ObserverCapOverride { get; }
    }
}
