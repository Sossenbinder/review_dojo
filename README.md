# ReviewDojo

## What it is

ReviewDojo is a training ground for **deliberate practice at reviewing AI-generated code**. It takes a real
git repository you point it at, uses an LLM to generate a plausible code change against it, and — most of the
time — injects a small number of realistic bugs of the kind an AI coding assistant tends to make. You review
the resulting diff exactly as you would a pull request: flag findings (file, line, category) and give a verdict
(approve / request changes). Because every injected bug is recorded in a hidden **ground-truth manifest**, the
app can score you objectively — recall, precision, false-positive rate, verdict correctness, calibration — and
track those metrics over time. Some diffs are deliberately clean, so you also practice *not* crying wolf.

## Prerequisites

- **.NET 10 SDK** — the whole solution targets `net10.0` (developed and verified on `net10.0`).
- An **`ANTHROPIC_API_KEY`** in your environment. It is only needed when a diff is actually generated
  (the `POST /sessions/{id}/diffs/next` endpoint and the CLI `gen` harness). The API boots and migrates its
  database without one.
- A **local target git repository** to point ReviewDojo at. The generator reads source files from it
  (read-only) to build realistic changes.

## Setup

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

The SQLite database (`reviewdojo.db`, created in the API's working directory) **auto-migrates on API boot** —
`Program.cs` calls `Database.Migrate()` at startup, so there is no manual migration step. If you want to run
migrations yourself instead (optional), you can use `dotnet ef` against `src/ReviewDojo.Data` (the migration is
`InitialCreate`); this is not required for normal use.

The connection string defaults to `Data Source=reviewdojo.db`. Override it with the config key
`ConnectionStrings:Db` (env var `ConnectionStrings__Db`).

## Running

You need two terminals: one for the API, one for the Blazor WASM client. **Use the `https` launch profile in
both** so the URLs line up with the built-in config defaults (API on `https://localhost:7001`, client on
`https://localhost:7002`).

Terminal 1 — API:

```bash
export ANTHROPIC_API_KEY=sk-ant-...
dotnet run --project src/ReviewDojo.Api --launch-profile https
# -> Now listening on: https://localhost:7001
```

Terminal 2 — client:

```bash
dotnet run --project src/ReviewDojo.Client --launch-profile https
# -> serves the Blazor app on https://localhost:7002
```

Why `--launch-profile https`: `dotnet run` picks the *first* profile in `launchSettings.json` by default, which
is the `http` profile (API `http://localhost:5228`, client `http://localhost:5271`). The client's compiled-in
`ApiBase` default is `https://localhost:7001/`, and the API's CORS `ClientOrigin` default is
`https://localhost:7002`. Selecting the `https` profile makes the running ports match those defaults so the two
talk to each other with zero extra configuration.

If you prefer different ports, keep them consistent by overriding both config values, e.g.:

```bash
# API: allow the client's real origin through CORS
ClientOrigin=http://localhost:5271 dotnet run --project src/ReviewDojo.Api --launch-profile http
# Client: point at the API's real base URL (note the trailing slash)
ApiBase=http://localhost:5228/ dotnet run --project src/ReviewDojo.Client --launch-profile http
```

(Env-var forms: `ClientOrigin`, `ApiBase`. For the client — a WASM app — you can also set `ApiBase` in
`wwwroot/appsettings.json`.)

### Demo mode (no API key)

To try the whole app with **no Anthropic API key and no network**, run the API with the `demo` launch profile:

```bash
# Terminal 1 — API with the offline mock generator (no ANTHROPIC_API_KEY needed)
dotnet run --project src/ReviewDojo.Api --launch-profile demo
# Terminal 2 — client, exactly as normal
dotnet run --project src/ReviewDojo.Client --launch-profile https
```

The `demo` profile sets `Anthropic__UseMock=true`, so the API wires up an in-process `MockAnthropicClient`
instead of the real HTTP client — the real `AnthropicClient` is never constructed, so no key is required. The
mock drives the *real* generation pipeline (locus selection, diff building, anchor resolution, scoring); it just
injects **simple operator/boundary flips** (e.g. `==`→`=`, `<=`→`<`) so you can exercise the review UI end to
end. For realistic, varied diffs, run without the mock and set `ANTHROPIC_API_KEY` (real mode, above).

**The review loop** (in the client, `https://localhost:7002`):

1. On the Home page (`/`), enter a **real repo path** on disk, choose a **difficulty** (Easy / Medium / Hard),
   and click **Start** — this creates a session.
2. You are taken to the Review page (`/review/{sessionId}/{ordinal}`). The API generates the next diff on
   demand. Review each of the **5 diffs**: read the rendered unified diff, add findings (file / line /
   category / optional comment), and choose a verdict.
3. **Submit** each diff. Submission scores it and unlocks the reveal.
4. See the **revealed manifest** (the ground-truth bugs, with your matches highlighted) and your **score**
   on the Reveal page (`/reveal/{sessionId}/{ordinal}`).
5. After the run, open **Stats** (`/stats`) for cumulative metrics across every diff you've scored.

> Generating a diff calls the Anthropic API and spends tokens. The API only constructs the Anthropic client the
> first time `/diffs/next` is hit, which is why the server boots fine without a key.

## Generator configuration

The generator is a **two-step** LLM pipeline: step 1 asks the model for one realistic, correct change to a
selected file (or two, on Hard); step 2 injects exactly *N* bugs into that change and returns a manifest with an
anchor substring per bug, which is then resolved to concrete diff line numbers.

- **Model** — config key `Anthropic:Model`, default **`claude-sonnet-4-6`**. Override via `appsettings.json` or
  env var `Anthropic__Model`.
- **Difficulty tiers** scale bug subtlety, count, and diff size:
  | Tier   | Bug categories offered            | Bug count (when not clean) | Size median |
  |--------|-----------------------------------|----------------------------|-------------|
  | Easy   | Mechanical                        | 1                          | ~80 lines   |
  | Medium | Mechanical, EdgeCase              | 1–2                        | ~150 lines  |
  | Hard   | Mechanical, EdgeCase, Contextual  | 1–3                        | ~300 lines  |
- **Clean-diff rate** — default **20%** of diffs are generated bug-free (verdict should be *approve*). This is
  wired into the generator's mistake-count policy (`cleanRate: 0.2`).
- **Diff size distribution** — log-normal, sampled per diff from a seed (`SizeSampler`): median ~150 changed
  lines overall, with a long tail clamped up to ~800 lines. (The per-tier medians above feed the same
  log-normal draw.)

Config precedence is standard ASP.NET Core: `appsettings.json` < environment variables. Example env overrides:

```bash
Anthropic__Model=claude-sonnet-4-6 dotnet run --project src/ReviewDojo.Api --launch-profile https
```

## CLI

The `dojo` CLI (`src/ReviewDojo.Cli`) has two commands:

```bash
# Mine fix/revert/bug/patch/hotfix commits from a repo into the bug corpus (SQLite).
dotnet run --project src/ReviewDojo.Cli -- mine <repoPath>

# Dev harness: generate one diff + manifest and print it (needs ANTHROPIC_API_KEY).
dotnet run --project src/ReviewDojo.Cli -- gen <repoPath> --seed 1
```

`mine` scans commit history for messages matching fix-style keywords and stores the before/after snippets and
messages as **corpus entries**. The generator pulls the most recent corpus entries as **few-shot examples**
when injecting bugs (`/diffs/next` takes the latest 3), so mining a real project's bug-fix history makes the
injected bugs more like that project's actual failure modes. `gen` is a local harness for eyeballing generator
output without running the full API/client stack.

## Bug taxonomy

Five categories, ascending in difficulty (`BugCategory` enum):

1. **Mechanical** — local, syntactic-ish slips (e.g. `=` vs `==`, off-by-one, wrong operator).
2. **EdgeCase** — missing null/empty/boundary handling.
3. **Contextual** — code that's locally fine but wrong given surrounding context or intent.
4. **Abstraction** — wrong abstraction / interface / responsibility choices.
5. **AgentTypical** — mistakes characteristic of AI coding assistants.

The **MVP seeds categories 1–3** (the injection prompt offers Mechanical → EdgeCase → Contextual across the
Easy/Medium/Hard tiers). All **five exist in the enum** and in the scoring taxonomy so categories 4–5 can be
enabled later without a schema change.

## Scoring

On submit, findings are matched against the manifest and a scorecard is computed:

- **Recall** — fraction of seeded bugs you caught.
- **Precision** — fraction of your findings that hit a real bug.
- **Severity-weighted recall** — recall weighted by bug severity (Low 1, Medium 2, High 3, Critical 5), so
  missing a Critical costs more than missing a Low.
- **False-positive rate** — fraction of findings that matched no bug.
- **Verdict correctness** — did you approve clean diffs and request changes on buggy ones? (Getting the verdict
  right on **clean diffs** is a first-class signal, not an afterthought.)
- **Median review time** — across scored diffs.
- **Per-category recall** — recall broken down by bug category.
- **Calibration** — clean-diff **approval rate** vs seeded-diff **rejection rate**, surfaced in Stats.

**Matching is fuzzy** (`FindingMatcher`): a finding matches a bug when the **file** is the same and the finding's
**line is within a proximity window** (±3 lines) of the bug's line span. Category is then checked — a **category
mismatch scores half credit** (0.5), an exact category match scores full credit (1.0). Matching is greedy and
one-to-one (each bug and each finding is used at most once), preferring full-credit, closest matches first.

## Security notes

- The generator has **read-only** access to the target repo — it reads source files to build a change and
  **never executes the repo's code**.
- **Manifests are never sent to the client before submission.** This is enforced two ways: a **DTO split**
  (`DiffDto`, the pre-submit shape, physically has no manifest field; `RevealDto` is only produced post-submit),
  and a **reveal gate** — `GET /diffs/{id}/reveal` returns **403 until the diff has been submitted**.
- **`ANTHROPIC_API_KEY` is read from the environment** (`AnthropicClient` reads it at request time and throws if
  it's unset). It is never hard-coded. **`.env` is gitignored** (see `.gitignore`) and must never be committed.

## Development

Run the full test suite:

```bash
dotnet test    # 30 tests, all green
```

The suite includes a **determinism harness** (`GeneratorDeterminismTests`) that exercises the generator against
a fixture repo with a fake LLM client: across ten runs it asserts that **every manifest entry maps to a real
line in the produced diff** (the injected `if (n = 0)` anchor is always present in the unified diff) and that
**clean runs produce an empty manifest**. It also asserts the generator throws when the model returns an unknown
file path, rather than silently diffing against an empty file.

## Known issues

- **`NU1903` transitive advisories** surface as build/restore warnings: `SQLitePCLRaw.lib.e_sqlite3` (pulled in
  transitively by EF Core SQLite) and `Microsoft.OpenApi` (pulled in by the webapi template). These are
  **transitive / dev-time** dependencies, do not affect the app's runtime behavior, and are slated for a
  dependency bump. They do not fail the build.
- The installed **.NET 10 SDK** (10.0.108) ships without ASP.NET Core prune package data, so the API csproj sets
  **`AllowMissingPrunePackageData`** (the SDK-recommended `NETSDK1226` escape hatch). Prune data is a build-size
  optimization only and has no correctness impact.

## Deferred (not built in the MVP)

- Multi-user accounts / auth.
- Runtime or trace-based verification that injected bugs actually change behavior.
- Adaptive difficulty (auto-tuning tier to your performance).
- Spaced repetition of missed categories.
- LLM-graded free-text review comments.
