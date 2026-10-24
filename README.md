# Newsquack 🦆

> *The duck that never misses a feed.*

A self-hosted .NET 10 service that polls RSS/Atom feeds and YouTube channels and delivers every new post to pluggable notification targets (Discord in v1.0), with reliable at-least-once delivery, persistent item-ID dedup and a stage-based post pipeline. Newsquack is an improved reimplementation of [NewsQuack](https://github.com/NewsQuack).

## Status: M1 complete — v1.0 release pending

Milestone M1 (v1.x) is fully implemented on the `sp/01` branch and is being released to `master` as **v1.0**. Everything in this milestone has been merged with green CI: the solution scaffold, test infrastructure and CI; the v2 config schema with boot-time total validation and friendly error messages; environment variable overrides for every config value (below); per-instance polling (absolute-slot scheduling, one global HTTP throttle, per-feed error tracking with exponential backoff and auto-removal); per-item parsing with UTC date normalization and platform builders (generic / YouTube / Reddit / GitLab); the stage-based post pipeline (dedup check → image resolution → send) with persistent at-least-once delivery; the Discord target (`embed` / `forum` / `markdown`, one-shot channel-type swap, client-side send pacing at a per-webhook minimum interval, 429-aware retries, persistent dead-letter storage with classified errors and an optional system webhook); health endpoints (`/healthz`, `/readyz`); and graceful shutdown (schedulers stop → queues drain within a bounded window → WAL checkpoint). Client-side pacing spaces out every request to a webhook URL (default 2 s ⇒ at most ~30 req/min per webhook, shared across instances that use the same URL) so backlog bursts no longer produce 429 storms — Retry-After handling remains the safety net for whatever still gets through.

## Where to look

| File | Who it's for |
|---|---|
| [`AGENTS.md`](AGENTS.md) | Development subagents — system overview, access model, working & code rules (binding summary) |
| [`docs/PROCESS.md`](docs/PROCESS.md) | Everyone — branch model, task lifecycle, GitHub tracking, commit convention (full detail + rationale) |
| [`docs/TASK-TEMPLATE.md`](docs/TASK-TEMPLATE.md) | Coordinator — template for planning every task in `tasks/` |
| [`.github/pull_request_template.md`](.github/pull_request_template.md) | Everyone — PR format (task, impact & risk, testing, checklist) |

## Example config file

`newsquack.json` (the app looks for it in this order: CLI argument, `./newsquack.json`, `/config/newsquack.json`; override the location with `NEWSQUACK_CONFIG`):

```json
{
  "instances": [
    {
      "id": "news",
      "checkIntervalMinutes": 5,
      "feeds": [
        { "url": "https://feeds.bbci.co.uk/news/rss.xml" },
        { "url": "https://www.youtube.com/feeds/videos.xml?channel_id=UC..." }
      ],
      "discord": {
        "webhookUrl": "https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx",
        "mode": "embed"
      }
    }
  ],
  "health": { "enabled": true, "port": 8081 },
  "logging": { "level": "Information" }
}
```

Only `instances` is required — every other section and property is optional (defaults apply). Every value can be overridden from an environment variable: see below.

## Environment variables

Every value of the v2 config schema can be overridden from an environment variable, and **environment variables always win over the config file**. The naming scheme is frozen: prefix `NEWSQUACK_`, sections separated by `__` (double underscore), list entries addressed by **numeric index** — the standard .NET JSON + environment configuration pipeline.

| Env var | Overrides | Default when omitted |
|---|---|---|
| `NEWSQUACK_CONFIG` | config file *location* (not a value override) | first found of: CLI argument, `./newsquack.json`, `/config/newsquack.json` |
| `NEWSQUACK_DATABASE` | SQLite database file *path* (not a value override) — the state store migrates it at boot; its parent directory is created when missing | `/data/newsquack.db` (Docker mount point) |
| `NEWSQUACK_DEADLETTER_WEBHOOK` | dead-letter notification webhook URL (operational env var, not a schema key): when set, every dead letter also POSTs a JSON notification here; missing/empty ⇒ disabled, present but not an absolute http(s) URL ⇒ disabled with a startup Warning | unset (disabled) |
| `NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS` | shutdown drain window in seconds (operational env var, not a schema key): when the host stops, each send worker keeps processing its queue for at most this long before dropping remaining posts unconfirmed (they are re-delivered on the next start); missing/empty ⇒ 30 s default, invalid or non-positive ⇒ 30 s + startup Warning | 30 |
| `NEWSQUACK_HTTP__FEEDTIMEOUTSECONDS` | `Http.FeedTimeoutSeconds` — feed fetch timeout in seconds (> 0) | 30 |
| `NEWSQUACK_HTTP__DISCORDTIMEOUTSECONDS` | `Http.DiscordTimeoutSeconds` — Discord send timeout in seconds (> 0) | 15 |
| `NEWSQUACK_HTTP__IMAGETIMEOUTSECONDS` | `Http.ImageTimeoutSeconds` — image fetch timeout in seconds (> 0) | 15 |
| `NEWSQUACK_HTTP__BLOCKPRIVATENETWORKREDIRECTS` | `Http.BlockPrivateNetworkRedirects` — anti-SSRF switch (`true`/`false`) | false |
| `NEWSQUACK_HEALTH__ENABLED` | `Health.Enabled` — health endpoint on/off (`true`/`false`) | true |
| `NEWSQUACK_HEALTH__PORT` | `Health.Port` — health endpoint TCP port (1..65535) | 8081 |
| `NEWSQUACK_LOGGING__LEVEL` | `Logging.Level` — `Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` (case-insensitive) | Information |
| `NEWSQUACK_INSTANCES__<i>__ID` | `Instances[<i>].Id` — instance id (required, unique) | — (no default) |
| `NEWSQUACK_INSTANCES__<i>__CHECKINTERVALMINUTES` | `Instances[<i>].CheckIntervalMinutes` — poll interval in minutes (1..1440, required) | — (no default) |
| `NEWSQUACK_INSTANCES__<i>__FEEDS__<j>__URL` | `Instances[<i>].Feeds[<j>].Url` — feed URL, http(s) with host (required, at least one per instance) | — (no default) |
| `NEWSQUACK_INSTANCES__<i>__DISCORD__WEBHOOKURL` | `Instances[<i>].Discord.WebhookUrl` — Discord webhook URL (required) | — (no default) |
| `NEWSQUACK_INSTANCES__<i>__DISCORD__MODE` | `Instances[<i>].Discord.Mode` — `embed` / `forum` / `markdown` (case-insensitive) | embed |
| `NEWSQUACK_INSTANCES__<i>__DISCORD__COLOR` | `Instances[<i>].Discord.Color` — Discord embed color, 0..16777215 (optional) | — (omitted) |
| `NEWSQUACK_INSTANCES__<i>__DISCORD__FALLBACKIMAGEURL` | `Instances[<i>].Discord.FallbackImageUrl` — http(s) fallback image URL (optional) | — (omitted) |
| `NEWSQUACK_INSTANCES__<i>__DISCORD__MINSENDINTERVALSECONDS` | `Instances[<i>].Discord.MinSendIntervalSeconds` — minimum interval in seconds between two requests to this webhook URL (1..60); shared across instances that use the same webhook URL, strictest (smallest) wins on conflict | 2 |

### Instance addressing is by index, not by Id

If the file defines instances `[a, b]`, then `NEWSQUACK_INSTANCES__0__…` targets instance `a` and `NEWSQUACK_INSTANCES__1__…` targets instance `b` — regardless of their `Id`s. An index beyond the file's list creates a new (mostly empty) instance that fails validation until its required keys are provided.

### Empty values mean "not set"

An environment variable set to an empty string is treated as *not set*: the config file value (or schema default) wins. A wrong-typed value (e.g. `NEWSQUACK_HEALTH__PORT=abc`) surfaces as a friendly validation error naming the config path — not a crash or stack trace.

### Webhook tokens are masked in every error message

The last path segment of a Discord webhook URL (`<token>`) is a secret: any error message that echoes a webhook URL renders it as `https://discord.com/api/webhooks/<id>/…` plus only the **last 4 characters** of the token — e.g.

```
Instances[0].Discord.WebhookUrl: Must be a Discord webhook URL of the form https://discord.com/api/webhooks/<id>/<token> (got 'https://discord.com/api/webhooks/123456789012345678/…UvWx').
```

Feed URLs and image URLs are not secrets and are printed as-is.

### Env vars cannot replace a missing file (v1.0)

The config file must exist; env vars override values inside it, they do not provide the whole config on their own. (An env var at an index beyond the file's instance list creates a sparse instance that fails validation — see above.)

### Docker: webhook in env only

```bash
docker run -d \
  --name newsquack \
  -v /host/path/newsquack.json:/config/newsquack.json \
  -e NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL="https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx" \
  thebrutalx/newsquack
```

The mounted `newsquack.json` then contains no secret at all — the webhook URL lives only in the environment.
