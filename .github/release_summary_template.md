# NewsQuack vX.Y — Release Summary

<!-- Metadata line: fill all three fields before committing. "Previous release" is the last tag on master, or "none — first release". -->
> SP branch: `sp/NN` · Merge date: <YYYY-MM-DD, at tag time> · Previous release: vA.B (or none — first release)

## Overview

<!-- 1–2 sentences: what this release is and its main theme. Mirror the README's positioning; no jargon. -->

## Highlights

<!-- Up to ~8 user-facing bullets. What a reader of the GitHub Release should take away in 30 seconds.
     No internal task IDs, no architecture terms — describe outcomes, not implementation. -->

- …

## New features & capabilities

<!-- Grouped bullets (e.g. config/ops, fetching/parsing, pipeline/delivery, target(s), persistence/state, containerization).
     Reference issues where meaningful (#NN). Every bullet must be verifiable from the PR list / progress log — no invented features. -->

**<group>**

- …

## Fixes

<!-- Notable bug fixes only (not every chore/test tweak), each with its issue/PR reference.
     One line per fix: what was broken → what changed now. -->

- …

## Usage & configuration

<!-- Install/run essentials (image, mounts, volumes) plus anything NEW or CHANGED in this release's config
     (new keys, new env vars, changed defaults). End with one pointer line to the README for the full key/env-var tables. -->

…

<!-- Use an ABSOLUTE GitHub URL here (not a relative path): this file also renders as the GitHub Release notes, where relative links do not resolve. -->
See the [README](https://github.com/TheBrutalX/NewsQuack/blob/master/README.md) for the complete configuration reference and environment-variable table.

## Upgrade notes

<!-- Migration from the previous release: DB migrations, config changes, breaking changes, what to do before upgrading.
     Write "None — first release." when there is nothing to migrate. Never omit this section. -->

…

## Known limitations

<!-- Be honest — stated limitations build trust. One bullet each; reference a follow-up issue if one is planned. -->

- …

## Verification

<!-- How the release was verified: CI green on every PR, full-suite runs on both SDKs (local + CI parity) with final test
     counts and coverage gates, integration smoke / soak tests (what was exercised, real or simulated targets, result). -->

- …

## Change log

<!-- Every PR merged into this SP since the previous tag: PR number + one-line title, grouped by area.
     Generate the base list with (see .github/RELEASE-SUMMARY.md):
       git log --oneline master..sp/NN
       gh pr list --base sp/NN --state merged --limit 100
     If further PRs land on the SP after this file is committed, append them here in a small docs commit before merging. -->

**<group>**

- PR #NN — <one-line title>
