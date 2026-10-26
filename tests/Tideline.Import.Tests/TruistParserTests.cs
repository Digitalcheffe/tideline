using FluentAssertions;
using Tideline.Import.Truist;

namespace Tideline.Import.Tests;

public class TruistParserTests
{
    // Made-up data. Deposit format has 10 columns; card format has 6.
    private const string DepositHeader =
        "Posted Date,Transaction Date,Transaction Type,Check/Serial #,Full description,Merchant name,Category name,Sub-category name,Amount,Daily Posted Balance";

    private const string CardHeader = "Posted Date,Transaction Date,Transaction Type,Check/Serial #,Description,Amount";

    [Theory]
    [InlineData("$12.34", 1234)]
    [InlineData("(12.34)", -1234)]
    [InlineData("-12.34", -1234)]
    [InlineData("$1,500.00", 150000)]
    [InlineData("($1,500.00)", -150000)]
    [InlineData("0.005", 1)]      // half rounds away from zero
    [InlineData("-0.005", -1)]
    [InlineData("", 0)]
    public void Amounts_parse_to_cents(string text, long cents) =>
        TruistParser.ParseAmountCents(text).Should().Be(cents);

    [Fact]
    public void Garbage_amount_is_rejected() => TruistParser.ParseAmountCents("twelve").Should().BeNull();

    [Theory]
    [InlineData("03/09/2026", 2026, 3, 9)]
    [InlineData("3/9/26", 2026, 3, 9)]
    [InlineData("2026-03-09", 2026, 3, 9)]
    public void Dates_parse_in_all_three_formats(string text, int y, int m, int d) =>
        TruistParser.ParseDate(text).Should().Be(new DateOnly(y, m, d));

    [Fact]
    public void Deposit_file_parses_with_sign_balance_and_raw_json()
    {
        var csv = DepositHeader + "\n" +
                  "03/09/2026,03/08/2026,Check,1042,Check #1042,,,,($50.00),\"$1,950.00\"\n" +
                  "03/10/2026,03/10/2026,Deposit,,PAYROLL ACME,Acme,Income,Paycheck,\"$2,000.00\",\"$3,950.00\"\n";

        var file = TruistParser.Parse("acct_1111_03_01_2026_to_03_31_2026.csv", csv);

        file.BankNumber.Should().Be("1111");
        file.Format.Should().Be(TruistFormat.Deposit);
        file.Rows.Should().HaveCount(2);

        var check = file.Rows[0];
        check.PostedOn.Should().Be(new DateOnly(2026, 3, 9));
        check.AmountCents.Should().Be(-5000);
        check.CheckNumber.Should().Be("1042");
        check.BankBalanceCents.Should().Be(195000);
        check.RawJson.Should().Contain("\"Full description\":\"Check #1042\"");

        file.Rows[1].AmountCents.Should().Be(200000);
        file.Rows[1].Merchant.Should().Be("Acme");
        file.Rows[1].TruistCategory.Should().Be("Income");
    }

    [Fact]
    public void Card_file_flips_the_sign()
    {
        var csv = CardHeader + "\n" +
                  "03/09/2026,03/08/2026,Purchase,,GROCERY STORE,$42.10\n" +
                  "03/12/2026,03/12/2026,Payment,,PAYMENT THANK YOU,($42.10)\n";

        var file = TruistParser.Parse("ACCT_2222_03_01_2026_to_03_31_2026.csv", csv);

        file.Format.Should().Be(TruistFormat.Card);
        file.Rows.Select(r => r.AmountCents).Should().Equal(-4210, 4210);
        file.Rows.Should().OnlyContain(r => r.BankBalanceCents == null);
    }

    [Fact]
    public void Byte_order_mark_quotes_and_empty_posted_dates_are_handled()
    {
        var csv = "﻿" + CardHeader + "\r\n" +
                  "03/09/2026,03/08/2026,Purchase,,\"SHOP, THE \"\"BIG\"\" ONE\",$5.00\r\n" +
                  ",,,,,\r\n" +
                  "\r\n";

        var file = TruistParser.Parse("acct_2222_x.csv", csv);

        file.Rows.Should().ContainSingle();
        file.Rows[0].Description.Should().Be("SHOP, THE \"BIG\" ONE");
    }

    [Fact]
    public void Same_day_repeats_in_one_file_are_kept_with_increasing_occurrence()
    {
        var csv = CardHeader + "\n" +
                  "03/09/2026,,Purchase,,SPOTIFY USA,$21.19\n" +
                  "03/09/2026,,Purchase,,SPOTIFY USA,$21.19\n" +
                  "03/09/2026,,Purchase,,SPOTIFY USA,$9.99\n";

        var rows = TruistParser.Parse("acct_3333_x.csv", csv).Rows;

        rows.Select(r => r.Occurrence).Should().Equal(0, 1, 0);
        rows.Select(r => r.ExternalId).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public void Overlapping_exports_produce_the_same_external_ids()
    {
        var march = CardHeader + "\n03/09/2026,,Purchase,,SPOTIFY USA,$21.19\n03/10/2026,,Purchase,,COFFEE,$4.00\n";
        var overlap = CardHeader + "\n03/10/2026,,Purchase,,COFFEE,$4.00\n03/11/2026,,Purchase,,GAS,$30.00\n";

        var a = TruistParser.Parse("acct_3333_a.csv", march).Rows.Select(r => r.ExternalId);
        var b = TruistParser.Parse("acct_3333_b.csv", overlap).Rows.Select(r => r.ExternalId);

        a.Intersect(b).Should().ContainSingle();
    }

    [Theory]
    [InlineData("statement.csv")]
    [InlineData("acct_12_x.csv")]
    public void File_name_must_carry_the_account_number(string name) =>
        FluentActions.Invoking(() => TruistParser.Parse(name, CardHeader + "\n"))
            .Should().Throw<ImportFileException>().WithMessage("*acct_1234*");

    [Fact]
    public void Unknown_layout_and_bad_values_fail_with_the_file_and_line()
    {
        FluentActions.Invoking(() => TruistParser.Parse("acct_1111_x.csv", "Date,Thing\n1,2\n"))
            .Should().Throw<ImportFileException>().WithMessage("*not a Truist*");

        FluentActions.Invoking(() => TruistParser.Parse("acct_1111_x.csv", CardHeader + "\nnot-a-date,,,,X,$1.00\n"))
            .Should().Throw<ImportFileException>().WithMessage("*acct_1111_x.csv line 2*date*");

        FluentActions.Invoking(() => TruistParser.Parse("acct_1111_x.csv", CardHeader + "\n03/09/2026,,,,X,abc\n"))
            .Should().Throw<ImportFileException>().WithMessage("*line 2*amount*");
    }
}

public class ExternalIdTests
{
    // Vectors from docs/ARCHITECTURE.md (taken from the reference implementation).
    [Theory]
    [InlineData("1111", 2026, 3, 9, "Check #1042", -500000, 0, "truist:1fb866c87f8dbb056929")]
    [InlineData("3333", 2026, 1, 5, "SPOTIFY USA", -2119, 1, "truist:5175eb7b9face68d9eb0")]
    public void Matches_the_spec_vectors(string bank, int y, int m, int d, string description, long cents, int occurrence, string expected) =>
        ExternalIds.Truist(bank, new DateOnly(y, m, d), description, cents, occurrence).Should().Be(expected);

    [Fact]
    public void Other_sources_use_their_own_prefixes()
    {
        ExternalIds.SimpleFin("c1", "a2", "t3").Should().Be("sfin:c1:a2:t3");
        ExternalIds.Manual().Should().StartWith("manual:").And.HaveLength(7 + 32);
    }
}

public class FileCheckTests
{
    private static TruistRow Row(int day, long cents, long? balance) =>
        new("1111", new DateOnly(2026, 3, day), "X", cents, "", "", balance, "", "f.csv", 0, "{}");

    [Fact]
    public void A_consistent_file_passes_and_reports_start_and_end()
    {
        // opening 1,000.00; -50.00 -> 950.00; then +20.00 and -5.00 on one day -> 965.00 (balance on the last row)
        var rows = new[] { Row(1, -5000, 95000), Row(2, 2000, 97000), Row(2, -500, 96500) };

        var result = FileCheck.Run(rows)!;

        result.Passed.Should().BeTrue();
        result.StartCents.Should().Be(100000);
        result.EndCents.Should().Be(96500);
    }

    [Fact]
    public void A_missing_row_fails_the_file()
    {
        var rows = new[] { Row(1, -5000, 95000), Row(3, -500, 90000) }; // a -45.00 row is missing

        var result = FileCheck.Run(rows)!;

        result.Passed.Should().BeFalse();
        result.BadDays.Should().Be(1);
        result.FailureMessage.Should().Contain("1 day(s)");
    }

    [Fact]
    public void One_cent_of_drift_is_tolerated_two_is_not()
    {
        FileCheck.Run([Row(1, -5000, 95000), Row(2, 1000, 96001)])!.Passed.Should().BeTrue();
        FileCheck.Run([Row(1, -5000, 95000), Row(2, 1000, 96002)])!.Passed.Should().BeFalse();
    }

    [Fact]
    public void Card_files_and_empty_files_are_not_checked()
    {
        FileCheck.Run([Row(1, -5000, null)]).Should().BeNull();
        FileCheck.Run([]).Should().BeNull();
    }
}
