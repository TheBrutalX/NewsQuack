# RELEASE-SUMMARY — How to produce a release summary

Standard process for every service-pack → master release, so any future `sp/NN → vX.Y` merge follows the same steps. The v1.0 example lives at [`docs/releases/v1.0.md`](../docs/releases/v1.0.md).

## When

At **every SP → master release** (the `vX.Y` tag), **before merging the release PR**. The summary is part of the release PR's diff and gets reviewed with it; the GitHub Release created after tagging uses this file as its notes.

## Naming rules

| Artifact | Name |
|---|---|
| Summary file (in repo) | `docs/releases/vX.Y.md` |
| Git tag (on `master`) | `vX.Y` |
| GitHub Release title | `NewsQuack vX.Y` |

## Steps

1. **Copy the template** — on the SP branch:
   ```bash
   cp .github/release_summary_template.md docs/releases/vX.Y.md
   ```

2. **Collect the data**:
   ```bash
   git log --oneline master..sp/NN                 # every commit landing in this release
   gh pr list --base sp/NN --state merged --limit 100
   gh issue list --state closed                    # issues closed by this milestone
   ```
   Use the milestone progress file (kept locally outside the repo, e.g. `newsquack-analysis/M1-PROGRESS.md`) as the narrative source — it records what each PR changed, deviations and verification evidence.

3. **Fill every section** of `docs/releases/vX.Y.md` in user-facing language. Keep the whole file under ~150 lines. Every claim must be verifiable from the PR list / progress log — no invented features; state known limitations honestly.

4. **Commit to the SP branch BEFORE merging the release PR**, so the summary is reviewed as part of that PR:
   ```bash
   git add docs/releases/vX.Y.md && git commit -m "docs(release): vX.Y release summary"
   ```
   If further PRs land on the SP after the summary is committed, append them to the Change log in a small docs commit before merging.

5. **After squash-merging the release PR and pushing tag `vX.Y`** (the CI `docker-push` job builds and pushes the image on `v*` tags), create the GitHub Release from `master`:
   ```bash
   gh release create vX.Y --title "NewsQuack vX.Y" --notes-file docs/releases/vX.Y.md
   ```

6. **Close out**: close the milestone epic with a comment linking the tag and the GitHub Release; update any PM planning doc status if one is maintained outside the repo.

## Quality bar

- **User-facing tone** — a reader of the GitHub Release should understand what they get without reading task files or code.
- **Verifiable claims** — every feature/fix bullet traceable to a PR or progress-log entry; no invented features, no vague "improvements".
- **Known limitations stated honestly** — they build trust and set correct expectations for the next milestone.
- **Upgrade notes never omitted** — write "None — first release." rather than skipping the section.
