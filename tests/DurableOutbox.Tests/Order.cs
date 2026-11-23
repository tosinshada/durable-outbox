namespace DurableOutbox.Tests;

/// <summary>The business change a message is written with.</summary>
public sealed class Order
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = null!;
}
