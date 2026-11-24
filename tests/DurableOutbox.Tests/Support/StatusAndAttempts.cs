namespace DurableOutbox.Tests;

/// <summary>Two columns read with raw SQL, for a table the EF Core model no longer matches.</summary>
public sealed record StatusAndAttempts(string Status, int Attempts);
