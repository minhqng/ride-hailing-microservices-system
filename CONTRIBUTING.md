# Contributing

## Branches

- `main`
- `feature/<issue-id>-<short-name>`
- `fix/<issue-id>-<short-name>`
- `experiment/<id>-<short-name>`

No long-lived member branches.

## Commits

Prefixes: `feat:`, `fix:`, `test:`, `docs:`, `refactor:`, `chore:`, `perf:`. Small PRs, one approving review, CI green.

Keep the subject semantic: describe the behavior or artifact changed, not the
week, member, agent, model, or integration session. Preserve meaningful version
identifiers such as `V1` and `schema v2`.

## Commit attribution

Author records the original creator; Committer records the person who applied
the object. A replay must preserve the original author and must not use
`--reset-author` on someone else's work.

When landing a stacked or replayed contribution:

```bash
git cherry-pick -x <source-sha>          # preserve author and record provenance
git log --format='%h %an <%ae> | %cn <%ce> %s' <base>..HEAD
git diff --stat <base>...HEAD
```

If the source author metadata is already wrong, correct it at the source with
the contributor's confirmation before integration. Do not infer authorship
from the service directory, PR creator, branch name, or the integrator. Use
`Co-authored-by` only for verified shared implementation; review, testing, or
integration alone does not qualify.

An owner-directed **credit allocation** is a separate act from an authorship
correction: the displayed identity is a team decision, not evidence about who
executed the work. Record it as a decision, keep the original author and
committer in the provenance map, and keep the old objects reachable.

Do not force-push `main` or an active successor branch to correct attribution
unless the repository owner has authorized that specific correction. An
authorized correction needs an isolated clone, a complete SHA/evidence/PR
impact map, an immutable `archive/...` tag published and verified before the
rewrite, tree-equality proof, and a `--force-with-lease` push pinned to the
exact expected old SHA. See the published-history protocol in
[AGENTS.md](AGENTS.md).

## PR checklist

Mention: issue, canonical doc section, DSI/DSL where relevant, tests, evidence path, reviewer focus. Use `.github/PULL_REQUEST_TEMPLATE.md`.

No CODEOWNERS (no usernames supplied; Doc 04 §3.3 defines reviewer matrix).
