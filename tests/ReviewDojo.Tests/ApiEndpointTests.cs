using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewDojo.Api;
using ReviewDojo.Core;
using ReviewDojo.Data;
using ReviewDojo.Generator;
using Xunit;

public class ApiEndpointTests : IClassFixture<ApiEndpointTests.DojoFactory>
{
    private readonly DojoFactory _factory;
    public ApiEndpointTests(DojoFactory factory) => _factory = factory;

    // The API serializes enums as strings (JsonStringEnumConverter); match that on read.
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // A trivial fake so the app boots without ANTHROPIC_API_KEY. /diffs/next is not
    // exercised by these tests, but the fake returns a parseable empty-files payload.
    private sealed class FakeAnthropicClient : IAnthropicClient
    {
        public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
            => Task.FromResult("{\"files\":[]}");
    }

    public sealed class DojoFactory : WebApplicationFactory<Program>
    {
        // Unique temp sqlite file per factory instance; app + seeding share it.
        public readonly string DbPath =
            Path.Combine(Path.GetTempPath(), $"reviewdojo-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Db", $"Data Source={DbPath}");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnthropicClient>();
                services.AddScoped<IAnthropicClient, FakeAnthropicClient>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    // Seed one session + one buggy diff with a single manifest entry. Returns diff id.
    private int SeedDiff()
    {
        // Booting the client triggers Database.Migrate(), creating the schema.
        _ = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewDojoContext>();

        var session = new Session
        {
            TargetRepoPath = "/tmp/repo",
            DifficultyTier = DifficultyTier.Easy,
            CleanRate = 0.2,
            Seed = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var diff = new Diff
        {
            Ordinal = 0,
            UnifiedDiffText = "--- a/a.cs\n+++ b/a.cs\n",
            IsClean = false,
            SizeLines = 10,
            Seed = 1,
            GeneratedAt = DateTime.UtcNow,
            Manifest =
            {
                new ManifestEntry
                {
                    FilePath = "a.cs",
                    LineStart = 5,
                    LineEnd = 5,
                    Category = BugCategory.Mechanical,
                    Severity = Severity.High,
                    Description = "off-by-one",
                },
            },
        };
        session.Diffs.Add(diff);
        db.Sessions.Add(session);
        db.SaveChanges();
        return diff.Id;
    }

    [Fact]
    public async Task Reveal_BeforeSubmit_Returns403()
    {
        int diffId = SeedDiff();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/diffs/{diffId}/reveal");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Submit_ScoresAndRevealsManifest()
    {
        int diffId = SeedDiff();
        var client = _factory.CreateClient();

        var submit = new SubmitRequest(
            Verdict.RequestChanges,
            new[] { new FindingDto("a.cs", 5, BugCategory.Mechanical, "found it") });

        var submitResp = await client.PostAsJsonAsync($"/diffs/{diffId}/submit", submit);
        Assert.Equal(HttpStatusCode.OK, submitResp.StatusCode);

        var reveal = await submitResp.Content.ReadFromJsonAsync<RevealDto>(JsonOpts);
        Assert.NotNull(reveal);
        Assert.NotEmpty(reveal!.Manifest);
        Assert.True(reveal.Score.Hits >= 1);
        Assert.True(reveal.Score.VerdictCorrect);

        // Gate opens post-submit.
        var revealResp = await client.GetAsync($"/diffs/{diffId}/reveal");
        Assert.Equal(HttpStatusCode.OK, revealResp.StatusCode);
    }
}
