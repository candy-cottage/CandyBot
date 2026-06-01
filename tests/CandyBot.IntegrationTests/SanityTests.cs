using FluentAssertions;
using Xunit;

namespace CandyBot.IntegrationTests;

public class SanityTests
{
    [Fact]
    public void True_should_be_true()
    {
        true.Should().BeTrue();
    }
}
