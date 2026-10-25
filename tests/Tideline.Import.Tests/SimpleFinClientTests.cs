using FluentAssertions;
using Tideline.Import.SimpleFin;

namespace Tideline.Import.Tests;

public class SimpleFinClientTests
{
    [Fact]
    public void Ninety_five_days_is_two_requests_the_second_starting_five_days_before_the_first_ended()
    {
        var end = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var start = end.AddDays(-95);

        var windows = SimpleFinClient.Windows(start, end);

        windows.Should().HaveCount(2);
        windows[0].Start.Should().Be(start);
        (windows[0].End - windows[0].Start).Should().Be(TimeSpan.FromDays(90));
        windows[1].Start.Should().Be(windows[0].End - TimeSpan.FromDays(5));
        windows[1].End.Should().Be(end);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(90)]
    [InlineData(91)]
    [InlineData(400)]
    public void No_window_is_longer_than_ninety_days_and_together_they_cover_everything_without_a_gap(int days)
    {
        var end = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var start = end.AddDays(-days);

        var windows = SimpleFinClient.Windows(start, end);

        windows.Should().OnlyContain(w => w.End - w.Start <= TimeSpan.FromDays(90));
        windows.First().Start.Should().Be(start);
        windows.Last().End.Should().Be(end);
        for (var i = 1; i < windows.Count; i++) windows[i].Start.Should().BeBefore(windows[i - 1].End, "each overlaps the last");
    }

    [Theory]
    [InlineData("-12.34", -1234)]
    [InlineData("1500.00", 150000)]
    [InlineData("0.005", 1)]
    [InlineData("-0.005", -1)]
    [InlineData("12", 1200)]
    public void Amounts_become_cents_with_the_sign_kept(string text, long cents) => SimpleFinClient.Cents(text).Should().Be(cents);

    [Fact]
    public void A_response_is_read_with_balances_dates_errors_and_pending_lines()
    {
        const string json = """
            {"errlist":[{"code":"gen.auth","msg":"Login needs attention"}],
             "accounts":[{"org":{"name":"Truist"},"id":"A1","name":"Checking 1111","currency":"USD","balance":"1895.51","balance-date":1775044800,
               "transactions":[{"id":"t1","posted":1775040000,"amount":"-21.99","description":"  SPOTIFY USA ","pending":false},
                               {"id":"t2","posted":1775040000,"amount":"5.00","description":"HOLD","pending":true}]}]}
            """;

        var r = SimpleFinClient.Parse(json);

        r.Errors.Should().Equal("Login needs attention");
        var a = r.Accounts.Single();
        a.Id.Should().Be("A1");
        a.Organization.Should().Be("Truist");
        a.BalanceCents.Should().Be(189_551);
        a.BalanceDate.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1775044800));
        a.Transactions.Select(t => (t.Id, t.AmountCents, t.Description, t.Pending)).Should().Equal(("t1", -2199, "SPOTIFY USA", false), ("t2", 500, "HOLD", true));
    }

    [Fact]
    public void Older_responses_with_a_plain_list_of_errors_are_read_too_and_junk_is_refused_in_words()
    {
        SimpleFinClient.Parse("""{"errors":["Something went wrong"],"accounts":[]}""").Errors.Should().Equal("Something went wrong");

        var act = () => SimpleFinClient.Parse("<html>Bad gateway</html>");
        act.Should().Throw<SimpleFinException>().WithMessage("*not data*");
    }
}
