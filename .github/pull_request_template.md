## Task

Closes #<!-- issue number of this task -->

## Summary

<!-- What this PR does and why, in a few sentences. Reference the task file (tasks/t-xxx-….md). -->

## Impact & risk assessment

| Aspect | Details |
|---|---|
| Modules touched | <!-- e.g. Core/Dedup, Infrastructure/Store, App/Scheduler -->
| Files / contracts touched | <!-- list; flag if a frozen contract (T-001) is modified — requires lead approval -->
| Risk | <!-- what could break: other stages, store schema, config validation… -->
| Rollback | <!-- how to revert safely: single commit? migration? data impact? -->

## Testing

<!-- What tests were added/modified and how to run them. -->

- New / modified tests: <!-- list test classes or methods (Method_Scenario_Expected) -->
- How to run: `dotnet test --filter "FullyQualifiedName~<TestClass>"`
- Manual verification (if any): <!-- e.g. soak check, WireMock scenario, log inspection -->

## Checklist

- [ ] Commits follow Conventional Commits and are written in English
- [ ] One commit = one complete, tested logical unit (no WIP commits)
- [ ] Task plan updated in the same PR if scope changed during implementation
- [ ] No changes outside the task scope (no drive-by refactors, no unrelated formatting)
- [ ] `dotnet build -c Release --warn-as-error` and `dotnet test` are green locally
- [ ] Coverage thresholds not degraded (core ≥ 90%, Core ≥ 80%, Infrastructure ≥ 70%)
