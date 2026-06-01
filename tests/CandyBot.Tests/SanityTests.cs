using FluentAssertions;
using Xunit;

namespace CandyBot.Tests;

public class SanityTests
{
    [Fact]
    public void True_should_be_true()
    {
        true.Should().BeTrue();
    }
}
