namespace DurableOutbox;

/// <summary>Values of the <c>status</c> column that code compares against. The SQL writes the same names as text.</summary>
internal static class OutboxStatus
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
    public const string Parked = "Parked";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
}
