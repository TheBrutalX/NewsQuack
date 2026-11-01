# NewsQuack 🦆

> *The duck that never misses a feed.*

NewsQuack is a self-hosted [.NET 10](https://dotnet.microsoft.com) service that polls RSS/Atom feeds and YouTube channels and delivers every new post to pluggable notification targets — Discord in v1.0; Telegram, Slack, Rocket Chat and generic webhooks are planned for later releases. It is built around **at-least-once delivery**: a post may be delivered twice after a crash, but it is never lost.

NewsQuack runs as a single Docker container in **multi-instance mode**: one config file describes N instances, each with its own feeds, check interval and webhook URL.

## Features

- **Multi-instance, single container** — one `newsquack.json` describes any number of polling instances; each gets its own scheduler, post pipeline and send worker.
- **At-least-once delivery** — a post is marked delivered only after the target answers HTTP 204; a crash between send and confirm can produce one duplicate post, never a lost one.
- **Persistent item-ID deduplication** — delivered (and in-flight) item IDs are stored in SQLite, so restarts never re-send old posts.
- **Client-side send pacing** — requests to the same webhook URL are spaced by a configurable minimum interval (default 3 s ⇒ at most 21 requests per closed 60 s window, sustained rate exactly 20/min — strictly below Discord's ~30 req/min per-webhook limit by construction).
- **429/Retry-After-aware retries** — exponential backoff with full jitter that honors the target's `Retry-After` (header or body) as an absolute floor; after a bounded retry budget the post lands in a persistent dead-letter store.
- **Dead-letter store with classified errors** — failed posts are persisted with an error category (`429`, `5xx`, `timeout`, `400-payload`) and can be pushed to a system webhook for alerting.
- **Feed error tracking** — per-feed failure counters with exponential backoff (30 s doubling, capped at 6 h) and auto-removal of persistently failing feeds for the rest of the process run.
- **Anti-SSRF redirect validation (default on)** — redirect hops to loopback/link-local/private addresses are rejected unless explicitly disabled.
- **Health endpoints** — `/healthz` and `/readyz` for orchestrators and reverse proxies.
- **Graceful shutdown** — schedulers stop, send queues drain within a bounded window, then the SQLite WAL is checkpointed so on-disk state is complete after stop.
- **Environment-variable overrides** — every config value can be overridden with a `NEWSQUACK_*` env var (handy for keeping webhook secrets out of the config file).

## Quick start (Docker)

The image is published to GitHub Container Registry as `ghcr.io/thebrutalx/newsquack` (multi-arch: `linux/amd64`, `linux/arm64`), tagged per release — pin a versioned tag such as `v1.0`. While the repository is private, the package is too: run `docker login ghcr.io` with a GitHub token that has read access to packages before pulling.

```bash
docker run -d \
  --name newsquack \
  -v /host/path/newsquack.json:/config/newsquack.json \
  -v newsquack-data:/data \
  ghcr.io/thebrutalx/newsquack:v1.0
```

- The config file is mounted at `/config/newsquack.json` — see [Configuration](#configuration) for a full example and the key reference. To keep the webhook URL out of the file, pass it in the environment instead: `-e NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL="https://discord.com/api/webhooks/…"` (env vars always win over the config file).
- `/data` holds the SQLite state database (delivered-item records, feed error counters, dead letters). Keep this volume across upgrades.
- The health listener runs inside the container on port 8081 by default (plain HTTP, no TLS or auth — it is meant for orchestrators and reverse proxies). Publish it with `-p 8081:8081` if you need to reach it from outside, or disable it with `health.enabled = false`.

## Configuration

The app looks for the config file in this order (first hit wins): first CLI argument → `$NEWSQUACK_CONFIG` → `./newsquack.json` → `/config/newsquack.json`. The file must exist; boot-time validation collects **every** error into one friendly list (each message names the exact JSON path) and exits with code 1 — no stack traces.

Example `newsquack.json` — only `instances` is required, every other section and property is optional:

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

Unknown JSON properties are tolerated (forward compatibility).

### Key reference

| Key | Type | Default | Meaning |
|---|---|---|---|
| `instances` | array of objects | — (required, ≥ 1) | One polling instance per entry. Every other top-level section is optional. |
| `instances[*].id` | string, non-empty | — (required) | Instance identifier; must be unique across all instances. Used in log lines and dead-letter entries. |
| `instances[*].checkIntervalMinutes` | integer, 1..1440 | — (required) | How often this instance polls its feeds, in minutes. Scheduling uses absolute slots (`next = last + interval`), so there is no drift. |
| `instances[*].feeds` | array of objects | — (required, ≥ 1) | The feeds this instance polls; URLs must be unique within the instance. |
| `instances[*].feeds[*].url` | http(s) URL with a host | — (required) | RSS/Atom feed or YouTube channel feed URL. |
| `instances[*].discord` | object | — (required in v1.0) | The Discord target for this instance — the only notification target in v1.0. |
| `instances[*].discord.webhookUrl` | string, `https://(discord\|discordapp).com/api/webhooks/<id>/<token>` | — (required) | Discord webhook URL. The token is a secret and is masked in every error message (see below). |
| `instances[*].discord.mode` | `embed` \| `forum` \| `markdown`, case-insensitive | `embed` | Payload mode for this instance's posts. See [Discord target](#discord-target). |
| `instances[*].discord.color` | integer, 0..16777215 (0xRRGGBB) | omitted | Embed color (embed/forum modes only). |
| `instances[*].discord.fallbackImageUrl` | http(s) URL with a host | omitted | Image attached to posts that have no image of their own. |
| `instances[*].discord.minSendIntervalSeconds` | integer, 1..60 | 3 (when omitted) | Minimum interval between two requests to this webhook URL. Shared across instances that use the same URL — the strictest (smallest) value wins. See [Discord target](#discord-target). |
| `http.feedTimeoutSeconds` | integer > 0 | 30 | Timeout for feed fetches, in seconds. |
| `http.discordTimeoutSeconds` | integer > 0 | 15 | Timeout for Discord webhook requests, in seconds. |
| `http.imageTimeoutSeconds` | integer > 0 | 15 | Timeout for image resolution (page scraping), in seconds. |
| `http.blockPrivateNetworkRedirects` | boolean | `true` | Anti-SSRF switch: reject redirect hops to loopback/link-local/private addresses. Set `false` only if a feed legitimately redirects into a private network. |
| `health.enabled` | boolean | `true` | Enable the health listener (`/healthz`, `/readyz`). When disabled, no HTTP listener is started at all. |
| `health.port` | integer, 1..65535 | 8081 | TCP port of the health listener (binds all interfaces inside the container; plain HTTP, no TLS or auth). |
| `logging.level` | `Trace` \| `Debug` \| `Information` \| `Warning` \| `Error` \| `Critical`, case-insensitive | `Information` | Minimum log level. Logs are JSON lines on stdout (see [Operations](#operations)). |

### Environment variable overrides

Every value in the schema can be overridden from an environment variable, and **environment variables always win over the config file**. The naming scheme: prefix `NEWSQUACK_`, sections separated by `__` (double underscore), list entries addressed by **numeric index** — the standard .NET JSON + environment configuration pipeline. Examples:

| Env var | Overrides |
|---|---|
| `NEWSQUACK_HEALTH__PORT` | `health.port` |
| `NEWSQUACK_LOGGING__LEVEL` | `logging.level` |
| `NEWSQUACK_HTTP__BLOCKPRIVATENETWORKREDIRECTS` | `http.blockPrivateNetworkRedirects` |
| `NEWSQUACK_INSTANCES__0__DISCORD__WEBHOOKURL` | `instances[0].discord.webhookUrl` |
| `NEWSQUACK_INSTANCES__1__CHECKINTERVALMINUTES` | `instances[1].checkIntervalMinutes` |

Rules that apply to every override:

- **Empty values mean "not set"** — an env var set to an empty string is ignored, so the file value (or schema default) wins.
- A wrong-typed value (e.g. `NEWSQUACK_HEALTH__PORT=abc`) surfaces as a friendly validation error naming the config path — not a crash or stack trace.
- **Instances are addressed by index, not by id** — if the file defines instances `[a, b]`, then `NEWSQUACK_INSTANCES__0__…` targets `a` and `NEWSQUACK_INSTANCES__1__…` targets `b`, regardless of their ids. An index beyond the file's list creates a new (mostly empty) instance that fails validation until its required keys are provided.
- In v1.0 env vars override values **inside** an existing config file; they do not replace the file on their own.

### Operational environment variables

A few `NEWSQUACK_*` variables control deployment rather than schema values:

| Env var | Purpose | Default when omitted |
|---|---|---|
| `NEWSQUACK_CONFIG` | Config file *location* (search order above) | the search order itself |
| `NEWSQUACK_DATABASE` | SQLite database file path; its parent directory is created when missing | `/data/newsquack.db` (the Docker mount point) |
| `NEWSQUACK_DEADLETTER_WEBHOOK` | Absolute http(s) webhook that receives one JSON notification per dead letter; missing/empty ⇒ disabled, present but not an absolute http(s) URL ⇒ disabled with a startup warning | unset (disabled) |
| `NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS` | Shutdown drain window in seconds: on stop, each send worker keeps processing its queue for at most this long before dropping remaining posts unconfirmed (they are re-delivered on the next start); missing/empty ⇒ 30 s, invalid or non-positive ⇒ 30 s + startup warning | 30 |

### Webhook tokens are masked in error messages

The last path segment of a Discord webhook URL (`<token>`) is a secret: any error message that echoes a webhook URL renders it as `https://discord.com/api/webhooks/<id>/…` plus only the **last 4 characters** of the token — e.g.

```
Instances[0].Discord.WebhookUrl: Must be a Discord webhook URL of the form https://discord.com/api/webhooks/<id>/<token> (got 'https://discord.com/api/webhooks/123456789012345678/…UvWx').
```

Feed URLs and image URLs are not secrets and are printed as-is.

## How it works

Each configured instance runs the same loop:

1. **Scheduling** — a per-instance scheduler triggers a fetch of all its feeds on *absolute* slots (`next = last + interval`, so there is no drift; the first cycle runs immediately at startup). All feed fetches share one global HTTP throttle, so many instances never stampede the network together.
2. **Fetch & parse** — feed XML is fetched and parsed with **per-item error isolation**: a broken item is logged and dropped, it never kills the feed or the host. Every date is normalized to UTC, and platform-specific builders (generic / YouTube / Reddit / GitLab) turn raw items into canonical posts.
3. **Post pipeline** — each post passes through an ordered chain of stages: a dedup check (skip if the item ID was already delivered or is still in flight), image resolution (feed XML first, page scraping as fallback), and a send stage that enqueues the post into the instance's bounded send queue.
4. **Delivery** — a single-reader worker per instance takes posts off the queue and sends them to the target. Transient failures are retried with exponential backoff (full jitter, 2 s base, 60 s cap) that honors `Retry-After` on HTTP 429 as an absolute floor; after 5 attempts — or immediately on a permanent failure such as a Discord payload rejection — the post is written to the persistent dead-letter store with a classified error.
5. **State** — delivery is confirmed only after the target answers HTTP 204, in one atomic transaction that also records the delivered item ID. In-flight markers are wiped at startup (they always belong to a dead process). The net semantics are **at-least-once**: a crash between send and confirm may produce one duplicate post, never a lost one.

For the deeper development-oriented version of this walkthrough (stage contracts, failure policies, persistence rules), see [.github/AGENTS.md](.github/AGENTS.md).

## Discord target

Discord is the only notification target in v1.0; each instance's `discord` section configures it.

**Modes** (`discord.mode`, case-insensitive):

- **`embed`** (default) — a rich embed card: title, plain-text description, link, image, timestamp and a "Feed:" footer. Use it for standard text/announcement channels.
- **`forum`** — the same embed plus a thread name, so each post opens its own thread in a forum-type channel.
- **`markdown`** — a plain message (bold title, link, plain-text description, image URL and feed attribution) for channels where you do not want an embed card.

**Channel-type mismatch:** if Discord answers 400 with a channel-type error (code `50_071` / "Channel type not supported"), NewsQuack resends the post **once** in the other mode (forum → embed, anything else → forum) and never tries a third time.

**Send pacing:** every request to a webhook URL is spaced by at least `minSendIntervalSeconds` (default 3 s when omitted ⇒ at most 21 requests per closed 60 s window, sustained rate exactly 20/min — strictly below Discord's documented ~30 req/min per-webhook limit by construction). Pacing state is shared across instances that use the same webhook URL; if they configure different intervals, the strictest (smallest) wins. Lower the value to send faster (1 s is allowed), raise it to be gentler — `Retry-After` handling remains the safety net for whatever still gets through.

**Dead letters:** when a post is dead-lettered (retry budget exhausted or permanent failure), it is persisted with its full payload, the attempts made and an error category (`429`, `5xx`, `timeout`, `400-payload`), and one structured warning log line is emitted. Set the operational env var `NEWSQUACK_DEADLETTER_WEBHOOK` to an absolute http(s) webhook to receive one JSON notification per dead letter (instance, feed URL, item id, title, error category, last error, UTC failure time) — a broken ops webhook never affects delivery itself.

## Operations

**Logs.** One JSON line per entry on stdout — ISO 8601 UTC timestamps (`@timestamp` with `Z` suffix), plus `level`, `logger`, `message`, and (when present) `props` and an `exception` object; worker log lines additionally carry top-level `instanceId` and `url`. Set the minimum level with `logging.level`.

**State.** All state lives in a single SQLite database (WAL mode) at `/data/newsquack.db` by default — override the path with `NEWSQUACK_DATABASE`. Schema migrations are applied automatically at startup, before any worker starts; stale in-flight markers left by a previous process are cleared in the same boot step. Delivered-item records older than 30 days are purged daily to keep the database small.

**Health endpoints.** `GET /healthz` → 200 `{"status":"ok"}` while the listener answers; `GET /readyz` → 200 `{"status":"ready"}` once startup is complete, or 503 with the missing components until then (e.g. while the database is being migrated). Plain HTTP on `health.port`, no TLS or auth — front it with your reverse proxy if you expose it.

**Graceful shutdown.** On stop: the schedulers stop first, then each send worker drains its queue within a bounded window (default 30 s, tunable via `NEWSQUACK_SHUTDOWN_DRAIN_TIMEOUT_SECONDS`), and finally the WAL is checkpointed so the on-disk database is complete. Posts still queued after the drain window are dropped unconfirmed and re-delivered on the next start (at-least-once).

**Upgrading.** Replace the image tag and restart, keeping the `/data` volume — schema migrations run automatically at boot, so an existing state store is upgraded in place.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```bash
dotnet build NewsQuack.sln -c Release
dotnet test
```

CI additionally builds with warnings-as-errors and enforces per-project coverage thresholds on every pull request. The `Dockerfile` is a two-stage build (SDK image → ASP.NET runtime, running as a non-root user); the container images published to GHCR are built from it.

## Contributing

Development rules and a system overview live in [.github/AGENTS.md](.github/AGENTS.md); the branch model and task lifecycle are documented in [.github/docs/PROCESS.md](.github/docs/PROCESS.md). Pull requests follow the template at `.github/pull_request_template.md`.
