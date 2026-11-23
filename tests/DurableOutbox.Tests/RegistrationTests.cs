using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.6: options are checked when the registration runs, in the line that is wrong.
public sealed class RegistrationTests
{
    [Fact]
    public void Invalid_options_are_refused_by_the_registration_call()
    {
        Assert.ThrowsAny<ArgumentException>(() => Register(outbox =>
        {
            outbox.AddHandler<TestHandler>("receipt.send");
            outbox.AddHandler<TestHandler>("receipt.send");
        }));
        Assert.ThrowsAny<ArgumentException>(() => Register(outbox =>
            outbox.AddHandler<TestHandler>("receipt.send", kind => kind.MaxConcurrency = 0)));
        Assert.ThrowsAny<ArgumentException>(() => Register(outbox =>
            outbox.AddHandler<TestHandler>("receipt.send", kind => kind.BackoffCap = TimeSpan.FromSeconds(1))));
    }

    private static void Register(Action<OutboxOptions> configure) =>
        new ServiceCollection().AddDurableOutbox<TestDbContext>(configure);
}
