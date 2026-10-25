using FluentAssertions;
using Tideline.Core;

namespace Tideline.Core.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(5, "$0.05")]
    [InlineData(4512, "$45.12")]
    [InlineData(-4512, "-$45.12")]
    [InlineData(123456789, "$1,234,567.89")]
    [InlineData(long.MinValue, "-$92,233,720,368,547,758.08")]
    public void Formats_as_us_dollars_with_cents(long cents, string expected) =>
        new Money(cents).ToString().Should().Be(expected);

    [Fact]
    public void Arithmetic_stays_in_whole_cents()
    {
        (new Money(1000) - new Money(1) + new Money(2)).Cents.Should().Be(1001);
        (-new Money(250)).Cents.Should().Be(-250);
        new[] { new Money(10), new Money(-4), new Money(1) }.Sum().Cents.Should().Be(7);
    }

    [Fact]
    public void Overflow_throws_instead_of_wrapping() =>
        FluentActions.Invoking(() => new Money(long.MaxValue) + new Money(1))
            .Should().Throw<OverflowException>();
}

public class LineTypeTests
{
    [Theory]
    [InlineData(LineType.Income, "income")]
    [InlineData(LineType.Bill, "bill")]
    [InlineData(LineType.Required, "required")]
    [InlineData(LineType.Extra, "extra")]
    [InlineData(LineType.OneTime, "one_time")]
    [InlineData(LineType.Savings, "savings")]
    public void Round_trips_through_snake_case(LineType type, string db)
    {
        type.ToDb().Should().Be(db);
        LineTypeExtensions.FromDb(db).Should().Be(type);
    }

    [Fact]
    public void Unknown_db_value_is_rejected() =>
        FluentActions.Invoking(() => LineTypeExtensions.FromDb("oneTime")).Should().Throw<ArgumentException>();
}
