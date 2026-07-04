# Review Dojo — Design

**Date:** 2026-07-04
**Status:** Approved (design), pending implementation plan

## Purpose

A training app for deliberate practice of code-review skills against AI-generated
changes, with ground truth. An agent generates realistic diffs against a real git
repo with a controlled number of seeded mistakes (possibly zero); the user reviews
the diff in a web UI and submits findings + a verdict; the app reveals the hidden
manifest, scores precision/recall and calibration, and tracks skill over time.

Non-goal: this is not a leetcode/algorithm-puzzle app. Diffs must come from a real
codebase.

## Stack

- **Backend:** ASP.NET Core Web API (.NET), EF Core + SQLite, migrations.
- **Frontend:** Blazor WebAssembly client calling the API.
- **Diff viewer:** diff2html via JS interop (supports unified **and** side-by-side).
- **LLM:** Anthropic HTTP API. Model configurable; default `claude-sonnet-4-6`
  for the generator, overridable to `claude-opus-4-8` for the hardest tiers.
  API key from `ANTHROPIC_API_KEY`; `.env` is gitignored, never committed.

## Solution layout

Each project has one clear responsibility and a well-defined interface:

- `ReviewDojo.Core` — domain types, the full 5-category taxonomy enum, and the
  scoring/matching logic. Pure, no I/O — directly unit-testable.
- `ReviewDojo.Data` — EF Core `DbContext`, SQLite, migrations.
- `ReviewDojo.Generator` — Anthropic client, two-step generator, diff computation
  (DiffPlex), and the anchor resolver.
- `ReviewDojo.Api` — ASP.NET Core Web API. Owns manifest-gating.
- `ReviewDojo.Client` — Blazor WASM UI; diff2html JS interop.
- `ReviewDojo.Cli` — corpus miner + generator harness commands.
- `ReviewDojo.Tests` — xUnit; determinism harness, scoring tests, fixture repo.

## Bug taxonomy (enum includes all 5; MVP seeds 1–3)

1. **Mechanical** — off-by-one, wrong operator, inverted condition, wrong variable.
2. **EdgeCase** — missing null/empty/boundary handling, unhandled error path.
3. **Contextual** — locally correct code that violates an unstated invariant of the
   surrounding system (wrong transaction scope, missing cache invalidation, race).
4. **Abstraction** — plausible-but-wrong abstraction, subtle behavior change hidden
   in a refactor, API contract drift. *(enum only in MVP)*
5. **AgentTypical** — confident wrong fix, hallucinated helper/API usage, silent
   semantic change, over-eager cleanup that removes load-bearing code. *(enum only)*

Severity is recorded per manifest entry and drives severity-weighted recall.

## Generator (the heart)

Runs server-side. **Read-only** on the target repo; **never executes** repo code.
Deterministic given a seed.

1. **Select locus** — walk the repo (read-only), pick a plausible change area.
   Optionally seed the prompt with corpus few-shot examples (see Corpus miner).
2. **Step 1 — legitimate change (Claude call):** the model returns the *full
   after-contents* of the touched files (a plausible feature/refactor/fix). **We
   compute the diff ourselves with DiffPlex** — line numbers are always ours, never
   the model's.
3. **Decide M** — draw the mistake count from the difficulty distribution. With the
   default 20% clean rate, `M == 0` skips Step 2 entirely; clean diffs are
   indistinguishable from seeded ones in size and change type.
4. **Step 2 — inject M mistakes (Claude call):** the model returns modified
   after-contents plus a manifest where each entry carries a **code anchor snippet**
   (not a line number) + category + severity + description. We re-diff the modified
   contents, then **locate each anchor within the produced diff to assign the
   authoritative line-range.**

**Why anchors:** we never trust model-reported line numbers. Resolving anchors
against the actual generated diff guarantees every manifest entry maps to lines that
truly exist in the diff, and `M == 0` yields an empty manifest — which is exactly
what the determinism harness asserts.

### Difficulty tiers

Difficulty scales by bug **subtlety** and **diff size**, not just count. A tier
configures: the mistake-count distribution, which taxonomy categories are eligible,
and the median diff size.

### Diff size distribution

Log-normal, median ~150 changed lines, tail to ~800. The difficulty tier scales the
median. One tunable knob; matches realistic PR size distribution.

## Data model

- **Session** — `targetRepoPath`, `difficultyTier`, `cleanRate`, `seed`,
  `createdAt`, `status`. A session is a run of N diffs.
- **Diff** — `sessionId`, `ordinal`, `unifiedDiffText`, `isClean`, `sizeLines`,
  `seed`, `generatedAt`, `startedAt`, `submittedAt`, `verdict` (approve /
  request-changes).
- **ManifestEntry** — `diffId`, `filePath`, `lineStart`, `lineEnd`, `category`,
  `severity`, `description`. **Server-only. Never serialized to the client before
  submission.**
- **Finding** — `diffId`, `filePath`, `line`, `category`, `comment` (optional but
  always stored), `createdAt`.
- **ScoreResult** — `diffId`, `recall`, `precision`, `severityWeightedRecall`,
  `falsePositiveRate`, `verdictCorrect`, `timeMs`, `matchesJson`. Persisted at
  submit so the stats view queries without recomputation.
- **BugCorpusEntry** — `category`, `beforeSnippet`, `afterSnippet`, `commitSha`,
  `message`. Feeds the generator's few-shot examples.

## Manifest-gating (hard security requirement)

- Pre-submit DTOs **physically cannot** contain manifest fields: distinct
  `DiffDto` (no ground truth) vs. `RevealDto` (with manifest) types.
- `POST /diffs/{id}/submit` is the **only** path that scores and returns the
  manifest.
- `GET /diffs/{id}/reveal` returns 403 until the diff is submitted.

## API surface (manifest-gated)

- `POST /sessions` — create a session (target repo, difficulty, clean rate, size).
- `POST /sessions/{id}/diffs/next` — generate the next diff; returns `DiffDto` only.
- `GET /diffs/{id}` — returns `DiffDto` (no manifest).
- `POST /diffs/{id}/start` — mark timer start.
- `POST /diffs/{id}/submit` — body: findings + verdict → server scores → returns
  `ScoreResult` + `RevealDto` (manifest now revealed).
- `GET /diffs/{id}/reveal` — `RevealDto`; 403 until submitted.
- `GET /stats` — aggregates for the persistent stats view.

## Scoring

Match findings to manifest entries by file + line proximity (fuzzy window) +
category. Category mismatch within the proximity window = **half credit**. Metrics:

- Recall, precision, severity-weighted recall, false-positive rate.
- Verdict correctness, emphasized on clean diffs.
- Median time per diff.
- **Calibration:** approval rate on zero-bug diffs vs. rejection rate on seeded
  diffs.

Stats view breaks recall/precision down **per bug category** and shows the trend
over time.

## Review UI

Blazor WASM: diff viewer (unified + side-by-side toggle via diff2html), inline
findings with a category picker and optional comment, a running timer, and submit
(approve / request-changes). On submit, transition to the reveal screen: manifest
overlaid on the diff with hits / misses / false-positives color-coded, plus session
metrics.

## CLI

- `dojo mine <repo>` — mine fix/revert commits from git history into the bug corpus
  (`BugCorpusEntry`), used as generator few-shots so injected bugs stay anchored to
  the real distribution rather than drifting toward pattern-detectable synthetic
  mistakes.
- `dojo gen` — generator harness (used by the determinism test): run the generator
  N× against a fixture repo and assert every manifest entry maps to lines that exist
  in the produced diff, and that zero-bug runs produce empty manifests.

## Security requirements (restated)

- Generator has **read access + patch output only**; never executes target-repo code.
- Manifests are **never** sent to the client before submission (enforced by DTO
  split, not discipline).
- Anthropic API key from environment; `.env` gitignored; never committed.

## Build order (MVP; each step ends runnable with a smoke test)

1. Repo scaffold + data model + migrations (SQLite).
2. Generator worker (target repo + difficulty in → diff + manifest out) +
   determinism harness (10× fixture run).
3. Review UI: diff viewer, inline findings + category picker, timer, submit.
4. Reveal + scoring screen: manifest overlay, color-coded hits/misses/FPs, metrics.
5. Persistent stats with per-category breakdown view.
6. CLI corpus miner (fix/revert commits → corpus → generator few-shots).

## Definition of done

Point the app at a real repo, generate a session of 5 diffs, review them in the
browser, submit, see the score with the manifest revealed, and view cumulative
stats — all smoke tests green, README covering setup and generator config.

## Explicitly deferred (do NOT build)

Multi-user, runtime/trace verification of injected bugs, adaptive difficulty, spaced
repetition, LLM-graded free-text comments.
