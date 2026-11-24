using Microsoft.EntityFrameworkCore;

namespace DurableOutbox;

/// <summary>
/// The checked settings registered for <typeparamref name="TContext"/>, kept whether or not the host runs
/// workers, so the test kit can run the handlers on demand.
/// </summary>
internal sealed record OutboxRegistration<TContext>(OutboxSettings Settings) where TContext : DbContext;
