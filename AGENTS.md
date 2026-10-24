# AGENTS.md — Development Subagent Brief

> This file is the **only project documentation** a development subagent needs, together with its own task file.
> If something you need is not stated here or in your task, stop and ask — do not go looking elsewhere.

---

## 1. What this system is

Newsquack is a self-hosted **.NET 10** service that polls RSS/Atom feeds and YouTube channels and delivers every new post to **pluggable notification targets**. It is an improved reimplementation of NewsQuack. Discord (webhook: embed / forum / markdown) is the only target in v1.0; Telegram, Slack, Rocket Chat and generic webhooks are planned for later releases. It runs as a single Docker container in **multi-instance mode**: one config file describes N instances, each with its own feeds, check interval and webhook URL.

How a post flows through the system:

1. **Scheduling** — a per-instance scheduler using *absolute* slots (`next = last + interval`, no drift) triggers a fetch of all feeds in parallel under a single global HTTP throttle (one `SemaphoreSlim`, balanced wait/release on every path).
2. **Fetch & parse** — feed XML is fetched through named `IHttpClientFactory` clients, parsed with **per-item error isolation** (a broken item is logged and dropped; it never kills the feed or the host). Every date is normalized to UTC with `CultureInfo.InvariantCulture`. The result of parsing + platform builder (generic / YouTube / Reddit / GitLab strategies) is a canonical `Post` record.
3. **Post-processing pipeline** — each post passes through an ordered chain of stages (`IPostStage`) sharing one mutable `PostContext`:

   | Order | Stage | Notes |
   |---|---|---|
   | 10 | dedup check | item-ID already delivered or pending (in-flight) → `ctx.Skip = true`, no further work on this item |
   | 20 | filters (negative / regex / time range) | reserved slot, v2.0 — do not add a stage here in v1.0 tasks unless your task says so |
   | 30 | image resolution | XML first, page scraping fallback; only new posts reach it (dedup skipped the rest) |
   | 40 | LLM tagging (JEV / OpenAI-compatible) | reserved slot, optional per instance, v2.x — `FailurePolicy = ContinueDegraded` when implemented |
   | 100 | send stage | enqueues into the per-instance send queue (`ISendQueue`) |

   A stage may set `ctx.Skip = true` to stop processing of that item; every stage declares a `FailurePolicy` (`IsolateItem` by default, `ContinueDegraded` only for optional enrichment). The runner orders stages by `Order`, checks `Skip` after each stage, and applies the per-stage policy on failure.
4. **Delivery** — a per-instance bounded `Channel<T>` queue + single-reader **target-agnostic** worker dispatches to the instance's `INotificationTarget`. The worker owns the retry policy: exponential backoff with jitter, honoring `Retry-After` (header or body) on HTTP 429, max attempts → persistent dead-letter. Adding a new target never touches the worker.
5. **State** — delivery is confirmed **only after** the target answers HTTP 204 (`Delivered`): an atomic EF Core transaction marks the item delivered (clearing its pending marker) and advances feed state in one commit. Pending markers are wiped wholesale at startup (they always belong to a dead process), preserving at-least-once across crashes. Semantics are **at-least-once**: a crash between send and confirm may produce one duplicate post, never a lost one.

Persistence is **EF Core + SQLite provider (WAL mode)**:
- one `AppDbContext` per operation, created via `IDbContextFactory<AppDbContext>` — **never** a singleton or shared context;
- short transactions; versioned migrations applied at startup before any worker starts;
- entities live in Infrastructure with fluent configuration only; the core talks to persistence through the `IStateStore` contract and never references EF Core.

For architectural details beyond this overview: refer to the task file assigned to you — **do not look for documentation beyond what is listed in section 2**.

---

## 2. Access model (fundamental rule)

A development subagent sees **exactly three** sources of truth:

1. This `AGENTS.md`.
2. Its own task file in `tasks/` (e.g. `tasks/t-024-post-pipeline-runner.md`).
3. Git history and existing code (`git log`, `git show <commit>`, the working tree).

It must **not know the future**:

- do not read `docs/plan/` (PM-level planning: WBS, milestones, roadmap);
- do not open other task files in `tasks/`;
- do not assume features, pipeline stages, targets or entities that are not in your own task;
- if your task references a contract (`Post`, `IPostStage`, `IStateStore`, …), read its current definition in the code — contracts change only through an explicit decision recorded in a commit.

If something you need is missing from your task file, stop and ask the coordinator — do not guess from later milestones.

**Analysis subagents** (planning, review, investigation) instead have full access to the repository and to `~/newsquack-analysis/`.

---

## 3. Working rules

- **One task at a time.** Never mix changes from two tasks in one branch or one commit.
- Every task has a **plan + impact assessment + test plan** (see `docs/TASK-TEMPLATE.md`). If scope changed during implementation, update the task file in the same PR that changed the scope.
- **Commits follow Conventional Commits, always in English**, optional scope:

  ```
  feat(store): add DeliveredItem entity with EF Core mapping
  fix(pipeline): skip enrichment when item already delivered
  test(dedup): cover crash between send and confirm (at-least-once)
  docs(readme): document env var overrides for webhook tokens
  chore(ci): cache NuGet packages in build-test job
  ```

- **One commit = one complete, tested logical unit.** No WIP commits, no "fix tests" follow-up commits left behind; if a change is not finished and green, it does not get committed.
- **Pull requests are in English** and close the task issue with `Closes #<issue>` (template: `.github/pull_request_template.md`).
- Merges happen only through PRs on GitHub — never direct push to `master` or an SP branch (see section 5).

---

## 4. Code rules (binding summary)

These rules are enforced in code review; a PR violating them is rejected. They derive from concrete defects found in the original project, not from taste.

- **Nullable enabled** on every project (`<Nullable>enable</Nullable>`). No `!` suppression without a comment explaining why the value cannot be null.
- **Dates: UTC only.** Only `DateTime` with `Kind = Utc` or `DateTimeOffset` in core code and at boundaries (config, store, payload). Parsing always with `CultureInfo.InvariantCulture`. Forbidden: `DateTime.Now`, `DateTime.Today`, any use of `CurrentCulture` in core, and silent fallback to "now" for unparseable dates (log and drop the item instead).
- **No catch-all that swallows exceptions without a structured log.** Every `catch (Exception)` must log with message template + placeholders, and the exception is always passed as an argument to the logger overload (never interpolated via `ToString()`), so the stack trace survives. A parse error must become observable state (error count + warning), never a silent empty list.
- **`CancellationToken` propagated end-to-end**: scheduler → fetch → parse → send. Every public async method exposes `CancellationToken ct = default`. No `Task.Delay` without a token in work loops; no unsupervised fire-and-forget tasks.
- **No sync-over-async**: `.Result`, `.Wait()`, `GetAwaiter().GetResult()` are forbidden outside synchronous `Main`.
- **EF Core rules**: fluent configuration only, in dedicated `IEntityTypeConfiguration<T>` classes (no attribute scattering on entities); no raw SQL in production code (LINQ queries); short transactions; one context per operation via the factory.
- **Tests are mandatory for every task** — xUnit, naming convention `Method_Scenario_Expected`, e.g.:

  ```csharp
  TryAdvanceState_AlreadyDeliveredItem_DoesNotResend()
  ParseDate_DeDeCulture_LosAngelesTz_ReturnsExpectedUtc()
  SendWorker_Webhook429WithRetryAfter_WaitsAtLeastRetryAfterSeconds()
  ```

- Solution layout, project dependencies (`App → Core ← Infrastructure`, core never references EF Core or Infrastructure), NuGet central package management and HTTP client naming are fixed by the scaffold — do not restructure them inside a task unless the task says so.

---

## 5. Branch model (summary)

```
master  ←  sp/NN  ←  dev branches
```

- `master` is the release line: only tagged releases live there, merged via release PRs from an SP branch.
- `sp/01`, `sp/02`, … are **service pack** branches — one per milestone (iteration line v1.x, v2.x). An SP branch is created from `master` at the start of a milestone.
- Dev branches are named `dev/<sp>-<task-id>-<slug>` (e.g. `dev/sp01-t-024-post-pipeline-runner`), always branched **from their own SP** and merged **back into their own SP** via PR. Never a dev branch directly to master, never cross-SP porting inside a milestone.

Full rules, lifecycle and rationale: `docs/PROCESS.md`.
