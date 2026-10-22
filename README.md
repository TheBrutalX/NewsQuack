# Newsquack 🦆

> *The duck that never misses a feed.*

A self-hosted .NET 10 service that polls RSS/Atom feeds and YouTube channels and delivers every new post to pluggable notification targets (Discord in v1.0), with reliable at-least-once delivery, persistent item-ID dedup and a stage-based post pipeline. Newsquack is an improved reimplementation of [NewsQuack](https://github.com/NewsQuack).

## Status: PLANNING

No code yet — this repository currently holds only the development process and conventions. The full architecture plan and work breakdown live outside the repo (`~/newsquack-analysis/`); they enter this repository as per-task files when development starts.

## Where to look

| File | Who it's for |
|---|---|
| [`AGENTS.md`](AGENTS.md) | Development subagents — system overview, access model, working & code rules (binding summary) |
| [`docs/PROCESS.md`](docs/PROCESS.md) | Everyone — branch model, task lifecycle, GitHub tracking, commit convention (full detail + rationale) |
| [`docs/TASK-TEMPLATE.md`](docs/TASK-TEMPLATE.md) | Coordinator — template for planning every task in `tasks/` |
| [`.github/pull_request_template.md`](.github/pull_request_template.md) | Everyone — PR format (task, impact & risk, testing, checklist) |
