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

When a file mixes durable knowledge with session narration, extract the
durable part into the appropriate existing location and leave the rest
untracked. A path under `docs/`, `evidence/` or `reports/` is not automatically
durable — content and canonical authority decide, not the directory name.

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
  reuse old logs to support changed code.

## Before opening/pushing a PR

- Compare the full logical diff against the intended base. Inspect every added
  file — including Markdown, JSON, shell, and dotfiles/hidden directories.
- Confirm no unrelated member's work or future-week implementation leaked in.
- `scripts/test/hygiene-check.sh <base>` must pass (CI runs it as the
  `hygiene` job; it rejects known transient paths from the changed-file list).

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

`plans/` (agent planning journals), `logs/`, `TestResults/`, and regenerated
evidence outputs (`evidence/**/outputs/repro-*/`) are ignored. If generated
junk is already tracked, removing it from tracking is a code change that needs
its own review — `.gitignore` alone does not untrack.
