# Repository conventions for humans and coding agents

Durable engineering rules for this repository. Branch/commit/PR mechanics live
in [CONTRIBUTING.md](CONTRIBUTING.md); canonical requirements live in
[docs/](docs/) (Doc 03 §25 defines the evidence-bundle format). This file adds
the change-hygiene rules every contributor — human, agent, or reviewer — must
apply to every PR, independent of who implements a member's weekly work.

## What belongs in the repository

| Class | Examples | Action |
| --- | --- | --- |
| Product artifact | service/test source, EF migrations + model snapshot, compose/deploy config, shared contracts | Keep |
| Canonical evidence | `manifest.json`, scenario README, curated `test-output.txt`/summaries, `final-state/`, `trace-reference.txt`, `metrics/` per Doc 03 §25 | Keep; must reference the exact code SHA they verify |
| Durable documentation | service READMEs, `docs/`, migration/deployment instructions, maintainer troubleshooting | Keep or update |
| Transient agent output | progress diaries, session transcripts, self-review narratives, scratch plans, prompt dumps, handoff notes | Never commit (`plans/` is local-only) |
| Generated runtime junk | `bin/`, `obj/`, `TestResults/`, `.trx`, coverage dumps, logs, local `.env`, IDE state | Never commit |

**Execution output is local/ephemeral by default; canonical evidence must be
intentionally curated.** Raw build/test output is never committed just because
it was produced: re-running a `reproduce.sh` writes its logs under
`evidence/**/outputs/` (gitignored), and the contributor curates the durable
result — counts, per-project totals, lane verdicts, final-state assertions —
into the bundle's `test-output.txt`/`final-state/` files and anchors them in
`manifest.json`. A `.txt`, `.json` or `.sh` extension grants no exemption in
either direction: canonical proof files are always acceptable, and raw run
dumps under `evidence/*/outputs/` never are. The extension rule also protects
EF migrations, fixtures and test source — the guard classifies by role, not
file type.

When a file mixes durable knowledge with session narration, extract the
durable part into the appropriate existing location and leave the rest
untracked. A path under `docs/`, `evidence/` or `reports/` is not automatically
durable — content and canonical authority decide, not the directory name.

## Authorship and integration provenance

Author is the person who created the logical change; Committer is the person
who applied the Git object. A replay, cherry-pick, rebase, or squash must not
silently replace the original author with the integrator.

- Preserve the original author when replaying a contribution. Use
  `git cherry-pick -x` (or `git am`) and verify the result with
  `git log --format='%h %an <%ae> | %cn <%ce> %s' <base>..HEAD`.
- Do not use `--reset-author` on someone else's commit, and do not infer
  authorship from the service directory, PR creator, branch name, or the
  person who happened to run the integration.
- If a source commit already carries the wrong author, stop and correct it at
  the source with the contributor's confirmation before integration. Do not
  "fix" it by assigning the integrated commit to a different teammate.
- The Committer is the person who actually applied the object. Do not assign a
  teammate as Committer merely because they own the affected service.
- Use `Co-authored-by` only for verified shared implementation. Review,
  testing, or integration alone does not justify co-authorship.
- Keep subjects semantic: describe the patch, not the process. Do not put week
  labels (`W09`), member labels (`member-b`), agent/model names, or
  integration-session labels in a subject. Preserve meaningful version
  identifiers (`V1`, `schema v2`, migration protocol versions).

### Verified authorship versus credit allocation

Two different acts must never be recorded as if they were the same thing:

- **Authorship correction** fixes a wrong identity field on a commit whose
  actual creator is independently established (source PR, branch, session or
  contributor confirmation). The corrected Author is a verified claim.
- **Credit allocation** is a team/owner decision to display a different
  contributor on work that was executed by someone else. The Git identity may
  be reassigned, but the record must state that the assignment is a decision,
  not an authorship finding, and the original author/committer stays
  recoverable through the archive ref and the provenance map.

Never describe reallocated credit as "verified authorship", and never infer
original execution from the new display identity. When a credit allocation
touches a published range, write down the old and new identity, the deciding
authority, and the unchanged Committer.

### Published-history correction protocol

`main` is force-pushed only under an explicit owner authorization for a
specific correction. A correction must:

1. work in an isolated full clone, never the primary checkout;
2. keep the old tip reachable through an immutable `archive/...` tag published
   *before* the rewrite, and confirm the tag resolves the old tip on the remote;
3. prove tree equality (same tree object) and patch coverage for every
   rewritten commit, plus a complete old→new SHA map;
4. publish with `--force-with-lease=refs/heads/main:<exact-old-sha>` after
   re-fetching and confirming that SHA — never an unconditional `--force`;
5. leave successor branches, PR refs and evidence tags untouched.

Without that authorization, do not force-push `main`, rewrite W11/W12
successors, or disable protection to make a correction land.

## Before staging

- Inspect `git status --short`; classify every untracked path against the
  table above. Never `git add -A` / `git add .` blindly.
- Stage explicit accepted paths only.

## Before commit

- Re-check staged paths (`git diff --cached --name-status`) and staged content
  (`git diff --cached`). Reject agent diaries, review narratives, raw run
  dumps, credentials, IDE files, and absolute developer-home paths presented
  as mandatory prerequisites (scripts must resolve tools via `PATH` with
  documented fallbacks, not a hardcoded `$HOME`).
- Evidence files must anchor to the exact code revision they verified; never
  reuse old logs to support changed code. Reproduction scripts prove source
  equivalence with `scripts/test/verify-source-anchor.sh` (symmetric diff over
  everything outside `evidence/`: added, modified, deleted, renamed and
  mode-changed paths are all drift).

## Before opening/pushing a PR

- Compare the full logical diff against the intended base. Inspect every added
  file — including Markdown, JSON, shell, and dotfiles/hidden directories.
- Confirm no unrelated member's work or future-week implementation leaked in.
- `scripts/test/hygiene-check.sh <base>` must pass (CI runs it as the
  `hygiene` job; it rejects known transient paths from the changed-file list —
  deletions are always allowed so cleanup PRs are not penalized). The guard's
  pattern verdicts and the source-anchor verifier's git-fixture tests run in
  the same CI job (`--self-test`, `verify-source-anchor.test.sh`).

## Before merge (reviewer)

- Re-run the changed-file classification on the final PR head; remove
  remaining transient artifacts.
- Verify every evidence `manifest.json` references a real SHA present in the
  PR history and that the referenced reproduction procedure runs from a plain
  checkout of the PR head.
- Confirm PR title/body match the actual diff, authorship is truthful
  (`Co-authored-by` only for real joint implementation), and CI — including
  `hygiene` — is green.

## Local-only scratch

`plans/` (agent planning journals), `logs/`, `TestResults/`, and all
regenerated evidence execution output (`evidence/**/outputs/`) are ignored. If
generated junk is already tracked, removing it from tracking is a code change
that needs its own review — `.gitignore` alone does not untrack.
