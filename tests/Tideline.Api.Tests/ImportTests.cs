using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "truist", name));

    private static MultipartFormDataContent File_(string name, string text)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(part, "file", name);
        return content;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Upload(HttpClient client, string name, string text)
    {
        var response = await client.PostAsync("/api/v1/imports/upload", File_(name, text));
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    [Fact]
    public async Task The_page_lists_what_came_in_and_whether_each_account_agrees_with_the_bank()
    {
        var page = await _api.Get("/api/v1/imports");

        page.GetProperty("batches").GetArrayLength().Should().Be(3, "the three fixture files were imported");
        page.GetProperty("batches")[0].GetProperty("status").GetString().Should().Be("done");
        var checks = page.GetProperty("balanceChecks").EnumerateArray().ToList();
        checks.Should().NotBeEmpty();
        checks.Should().OnlyContain(c => c.GetProperty("matches").GetBoolean(), "the fixtures are consistent with their own bank balances");
        page.GetProperty("inbox").GetProperty("configured").GetBoolean().Should().BeFalse();
        page.GetProperty("maxUploadBytes").GetInt64().Should().Be(5 * 1024 * 1024);
    }

    [Fact]
    public async Task Uploading_a_file_again_adds_nothing_and_is_audited()
    {
        var before = _api.Scalar<long>("SELECT COUNT(*) FROM transactions");

        var (status, body) = await Upload(_api.Client, "acct_1111_03_01_2026_to_03_31_2026.csv", Fixture("acct_1111_03_01_2026_to_03_31_2026.csv"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("status").GetString().Should().Be("done");
        body.GetProperty("rowsInserted").GetInt32().Should().Be(0);
        body.GetProperty("rowsExisting").GetInt32().Should().BeGreaterThan(0);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(before);
        (await _api.Get("/api/v1/imports")).GetProperty("batches").GetArrayLength().Should().Be(4);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'import.upload'").Should().Be(1);
    }

    [Fact]
    public async Task A_file_that_is_not_a_bank_export_fails_with_a_reason_and_is_kept_in_the_list()
    {
        var (status, body) = await Upload(_api.Client, "notes.csv", "this,is,not\na,bank,file\n");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("status").GetString().Should().Be("failed");
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        (await _api.Get("/api/v1/imports")).GetProperty("batches").EnumerateArray().Should().Contain(b => b.GetProperty("status").GetString() == "failed");
    }

    [Fact]
    public async Task Only_one_csv_under_five_megabytes_is_accepted()
    {
        (await Upload(_api.Client, "notes.txt", "x")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await Upload(_api.Client, "empty.csv", "")).Status.Should().Be(HttpStatusCode.BadRequest);
        (await Upload(_api.Client, "huge.csv", new string('a', 5 * 1024 * 1024 + 1))).Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var two = new MultipartFormDataContent();
        two.Add(new StringContent("a"), "file", "a.csv");
        two.Add(new StringContent("b"), "file", "b.csv");
        (await _api.Client.PostAsync("/api/v1/imports/upload", two)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _api.Client.PostAsync("/api/v1/imports/upload", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_an_owner_can_see_or_use_it()
    {
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);

        (await member.GetAsync("/api/v1/imports")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsync("/api/v1/imports/upload", File_("a.csv", "x"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _api.Anonymous.GetAsync("/api/v1/imports")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
