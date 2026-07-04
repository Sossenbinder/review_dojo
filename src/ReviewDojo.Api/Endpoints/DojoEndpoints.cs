using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReviewDojo.Core;
using ReviewDojo.Core.Scoring;
using ReviewDojo.Data;
using ReviewDojo.Generator;

namespace ReviewDojo.Api;

public static class DojoEndpoints
{
    public static void MapDojo(this WebApplication app)
    {
        // Create a session.
        app.MapPost("/sessions", async (CreateSessionRequest req, ReviewDojoContext db) =>
        {
            var session = new Session
            {
                TargetRepoPath = req.TargetRepoPath,
                DifficultyTier = req.Tier,
                CleanRate = req.CleanRate ?? 0.2,
                Seed = req.Seed ?? Environment.TickCount,
                CreatedAt = DateTime.UtcNow,
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            return Results.Ok(new { session.Id });
        });

        // Generate and persist the next diff for a session. Never returns the manifest.
        app.MapPost("/sessions/{id:int}/diffs/next", async (int id, ReviewDojoContext db, DiffGenerator gen) =>
        {
            var session = await db.Sessions.Include(s => s.Diffs).FirstOrDefaultAsync(s => s.Id == id);
            if (session is null) return Results.NotFound();

            int ordinal = session.Diffs.Count;
            int diffSeed = HashCode.Combine(session.Seed, ordinal);
            var generated = await gen.GenerateAsync(session.TargetRepoPath, session.DifficultyTier, diffSeed);

            var diff = new Diff
            {
                SessionId = session.Id,
                Ordinal = ordinal,
                UnifiedDiffText = generated.UnifiedText,
                IsClean = generated.IsClean,
                SizeLines = generated.ChangedLineCount,
                Seed = diffSeed,
                GeneratedAt = DateTime.UtcNow,
                Manifest = generated.Manifest.Select(b => new ManifestEntry
                {
                    FilePath = b.FilePath,
                    LineStart = b.LineStart,
                    LineEnd = b.LineEnd,
                    Category = b.Category,
                    Severity = b.Severity,
                    Description = b.Description,
                }).ToList(),
            };
            db.Diffs.Add(diff);
            await db.SaveChangesAsync();
            return Results.Ok(ToDiffDto(diff));
        });

        // Fetch a diff (no manifest).
        app.MapGet("/diffs/{id:int}", async (int id, ReviewDojoContext db) =>
        {
            var diff = await db.Diffs.FirstOrDefaultAsync(d => d.Id == id);
            return diff is null ? Results.NotFound() : Results.Ok(ToDiffDto(diff));
        });

        // Mark the review clock as started.
        app.MapPost("/diffs/{id:int}/start", async (int id, ReviewDojoContext db) =>
        {
            var diff = await db.Diffs.FirstOrDefaultAsync(d => d.Id == id);
            if (diff is null) return Results.NotFound();
            diff.StartedAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Submit findings + verdict, score, persist, and reveal the manifest.
        app.MapPost("/diffs/{id:int}/submit", async (int id, SubmitRequest req, ReviewDojoContext db) =>
        {
            var diff = await db.Diffs
                .Include(d => d.Manifest)
                .Include(d => d.Findings)
                .Include(d => d.Score)
                .FirstOrDefaultAsync(d => d.Id == id);
            if (diff is null) return Results.NotFound();
            if (diff.SubmittedAt is not null) return Results.Conflict();

            var submittedAt = DateTime.UtcNow;
            diff.SubmittedAt = submittedAt;
            diff.Verdict = req.Verdict;

            foreach (var f in req.Findings)
                diff.Findings.Add(new FindingRecord
                {
                    FilePath = f.FilePath,
                    Line = f.Line,
                    Category = f.Category,
                    Comment = f.Comment,
                    CreatedAt = submittedAt,
                });

            var bugs = diff.Manifest.Select(m =>
                new ManifestBug(m.FilePath, m.LineStart, m.LineEnd, m.Category, m.Severity, m.Description)).ToList();
            var findings = diff.Findings.Select(f =>
                new ReviewFinding(f.FilePath, f.Line, f.Category, f.Comment)).ToList();

            var matches = new FindingMatcher().Match(bugs, findings);
            long elapsedMs = (long)(submittedAt - (diff.StartedAt ?? diff.GeneratedAt)).TotalMilliseconds;
            var card = ScoreCalculator.Compute(matches, req.Verdict, diff.IsClean, elapsedMs);

            diff.Score = new ScoreRecord
            {
                Recall = card.Recall,
                Precision = card.Precision,
                SeverityWeightedRecall = card.SeverityWeightedRecall,
                FalsePositiveRate = card.FalsePositiveRate,
                VerdictCorrect = card.VerdictCorrect,
                TimeMs = card.ElapsedMs,
                MatchesJson = JsonSerializer.Serialize(matches.Select(m =>
                    new MatchDto(m.Bug?.FilePath, m.Bug?.LineStart, m.Finding?.FilePath, m.Finding?.Line, m.Credit))
                    .ToList()),
            };
            await db.SaveChangesAsync();
            return Results.Ok(ToReveal(diff, card, matches));
        });

        // Gated reveal: 403 until the diff has been submitted.
        app.MapGet("/diffs/{id:int}/reveal", async (int id, ReviewDojoContext db) =>
        {
            var diff = await db.Diffs
                .Include(d => d.Manifest)
                .Include(d => d.Score)
                .FirstOrDefaultAsync(d => d.Id == id);
            if (diff is null) return Results.NotFound();
            if (diff.SubmittedAt is null) return Results.StatusCode(403);

            var s = diff.Score;
            var card = new ScoreCard(
                s?.Recall ?? 0, s?.Precision ?? 0, s?.SeverityWeightedRecall ?? 0,
                s?.FalsePositiveRate ?? 0, s?.VerdictCorrect ?? false, s?.TimeMs ?? 0,
                Hits: 0, Misses: 0, FalsePositives: 0);

            var bugs = diff.Manifest.Select(m =>
                new ManifestBug(m.FilePath, m.LineStart, m.LineEnd, m.Category, m.Severity, m.Description)).ToList();
            var matches = new FindingMatcher().Match(bugs, Array.Empty<ReviewFinding>());
            return Results.Ok(ToReveal(diff, card, matches));
        });
    }

    static DiffDto ToDiffDto(Diff d) =>
        new(d.Id, d.Ordinal, d.SizeLines, d.UnifiedDiffText, d.StartedAt, d.SubmittedAt);

    static RevealDto ToReveal(Diff d, ScoreCard card, IReadOnlyList<MatchResult> matches)
    {
        var manifest = d.Manifest.Select(m =>
            new ManifestBugDto(m.FilePath, m.LineStart, m.LineEnd, m.Category, m.Severity, m.Description)).ToList();
        var score = new ScoreDto(
            card.Recall, card.Precision, card.SeverityWeightedRecall, card.FalsePositiveRate,
            card.VerdictCorrect, card.ElapsedMs, card.Hits, card.Misses, card.FalsePositives);
        var matchDtos = matches.Select(m =>
            new MatchDto(m.Bug?.FilePath, m.Bug?.LineStart, m.Finding?.FilePath, m.Finding?.Line, m.Credit)).ToList();
        return new RevealDto(d.Id, manifest, score, matchDtos);
    }
}
