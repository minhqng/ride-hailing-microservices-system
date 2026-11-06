# Credit-balance attribution correction (2026-11-06)

Metadata-only author rewrite of `main`, authorized by the project lead. This
bundle is the machine-readable provenance record for the rewrite described in
[docs/08](../../../docs/08-credit-balance-correction-2026-11-06.md).

| Item | Value |
|---|---|
| Old published tip | `5bf86f65210292012a9918f5b36618c9bb77d350` |
| Rewrite tip (tree-identical) | `3e1550da92c38ead8498c2972c75b6ae3511aa8c` |
| Tree object | `63ead1745032f935177854a991e87d94eed96b5e` |
| Rewrite baseline | `7962c046fdc2d57108819ea953ddf80886888a51` |
| Commits rewritten | 152 |
| Author changes | 49 |
| Archive tag | `archive/main-pre-credit-balance-2026-11-06` → `5bf86f65210292012a9918f5b36618c9bb77d350` |

Groups:

- **A — verified source-PR corrections (4)**:
  W15/W16 Member A commits integrated under `vitquaybarking`/Nerduckk; source
  PRs #49 and #50 per `docs/07-contribution-mapping.md`.
- **B — credit allocation (34)**:
  Driver/Matching/performance/backlog-recovery commits reassigned from
  `minhqng` to `Fried-toofuu`. Not a verified authorship finding.
- **C — credit allocation (11)**:
  Payment/messaging/reliability and C-owned release/CI/security commits
  reassigned from `minhqng` to `Nerduckk`. Not a verified authorship finding.

Every entry in `attribution-rewrite.json` records the old/new SHA, tree,
author before/after, unchanged Committer, parent mapping and the group. The
Committer field and both dates are preserved, so the actual applier and the
original author remain recoverable.

Verification: 264 commits, 0 merges, per-commit tree/message/date/Committer
checks with 0 errors. No test was re-run because every tree object is
identical; no CI result is claimed.

Recovery:

```bash
git fetch origin tag archive/main-pre-credit-balance-2026-11-06
git switch --detach 5bf86f65210292012a9918f5b36618c9bb77d350
```

Restoring the old history on `main` is a new owner decision and must repeat
the published-history protocol in `AGENTS.md`. Never move or delete the archive
tag.
