# Task Template

Copy this file to `tasks/t-<nnn>-<slug>.md` (lowercase ID + kebab-case slug) for every task. The completed file is the **entire** brief a development subagent receives, so it must be self-contained: everything needed to implement and test the task, nothing about the future.

---

# T-<NNN> — <Task title>

## ID & title

- **ID:** `T-<NNN>`
- **Title:** <short imperative name, e.g. "PostPipelineRunner + v1.0 stages">
- **Dev branch (when started):** `dev/<sp>-t-<nnn>-<slug>`
- **GitHub issue:** #<n>

## Milestone / SP

- **Milestone:** M<n> (<release line, e.g. v1.x>)
- **Service pack:** `sp/<NN>`
- **Workstream:** WS<n>
- **Dependencies (tasks that must be merged first):** T-xxx, … — or "none"

## Goal

<One paragraph: what is being built and the observable outcome when it works. No implementation detail here — that belongs below.>

## Context

<Only what the developer needs to understand the surrounding system for THIS task:
the contracts involved (read them in code), which modules exist today, relevant behavior of neighboring components.
No future features, no reserved slots, no later milestones. If a contract is frozen, say so and where it lives.>

## Impact assessment

| Aspect | Details |
|---|---|
| Modules / files touched | <e.g. `src/NewsQuack.Core/Pipeline/`, new file `PostPipelineRunner.cs`> |
| Contracts touched | <none / which one, and whether the change needs lead approval> |
| Risks | <what could break: other stages, store schema, config validation, existing tests…> |
| What could go wrong at runtime | <e.g. concurrent writers on SQLite, cancellation mid-drain> |

## Implementation notes

<Binding constraints for this task from the plan: patterns to use (Channel<T>, IDbContextFactory…), limits/thresholds with values, error-handling expectations (which failure policy, which log level), explicit non-goals ("do NOT add …").>

## Test plan

- **Unit tests:** <what behavior is covered, expected naming Method_Scenario_Expected>
- **Integration tests:** <e.g. WireMock scenario for 429 + Retry-After; SQLite temp file for store>
- **Regression coverage:** <which known defect does this prevent, e.g. "R8 unbalanced semaphore">
- **How to run:**

  ```bash
  dotnet test --filter "FullyQualifiedName~<TestClass>"
  ```

## Definition of Done

- [ ] Implementation complete per Goal and Implementation notes
- [ ] All tests from the Test plan added and green; coverage thresholds held (core ≥ 90%, Core ≥ 80%, Infrastructure ≥ 70%)
- [ ] No changes outside the task scope
- [ ] Task file updated if scope changed during implementation (same commit as the scope change)
- [ ] PR dev → SP opened with the template checklist filled in; `Closes #<issue>` present
- [ ] CI green on the PR
