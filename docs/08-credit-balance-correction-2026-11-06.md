# Credit-balance attribution correction (2026-11-06)

Status: **completed metadata-only rewrite, authorized by the project lead.**
This is the durable record required by `AGENTS.md` for a credit allocation. It
is not a claim that the reassigned authors wrote the changes, except Group A,
which is verified by the frozen W15/W16 source-PR map in
[`07-contribution-mapping.md`](07-contribution-mapping.md).

## What changed

| Item | Value |
|---|---|
| Old published tip | `5bf86f65210292012a9918f5b36618c9bb77d350` |
| Rewrite baseline (unchanged) | `7962c046fdc2d57108819ea953ddf80886888a51` |
| Rewrite tip (tree-identical) | `3e1550da92c38ead8498c2972c75b6ae3511aa8c` |
| Tree object before / after | `63ead1745032f935177854a991e87d94eed96b5e` (identical) |
| Rewritten commits | 152 (`7962c04..5bf86f6`, linear, 0 merges) |
| Author changes | 49 |
| Archive anchor | `archive/main-pre-credit-balance-2026-11-06` → `5bf86f6` |

Only two commit fields changed: the **Author name/email** on 49 commits, and
the **parent pointer** of every descendant whose ancestor was rewritten. Every
other field is byte-identical:

- tree object of every commit, so every patch and the final source tree;
- commit message bytes, including trailers;
- Committer identity and Committer date;
- author timestamp and timezone;
- the baseline commit and all 112 commits before it, which keep their SHAs;
- no signed commit was in the rewritten range.

The full old→new map, including each commit's tree, author before/after,
Committer and parent mapping, is
[`evidence/integration/credit-balance-2026-11-06/attribution-rewrite.json`](../evidence/integration/credit-balance-2026-11-06/attribution-rewrite.json).

## Group A — verified source-PR corrections (4 commits)

These are the W15/W16 Member A commits that `docs/07` maps to source PRs #49
and #50 but which were integrated under `vitquaybarking`, a canonical alias of
Nerduckk. The Author is corrected to Member A (`minhqng`). This group is a
**verified authorship correction**, supported by the source PR mapping, not a
balance preference.

| Old | New | Subject | Source PR | Author before → after |
|---|---|---|---|---|
| `0e3542a` | `ef8e98a` | docs(trip): freeze W15 Saga and Outbox report evidence | #49 | `vitquaybarking` → `minhqng` |
| `9a661cf` | `9e18bee` | docs(trip): record the A-stack warning honestly | #49 | `vitquaybarking` → `minhqng` |
| `9e8c425` | `d523516` | feat(trip): complete W16 release demo and Payment handoff | #50 | `vitquaybarking` → `minhqng` |
| `df5f748` | `3def089` | docs(trip): record stacked release warning accurately | #50 | `vitquaybarking` → `minhqng` |

The W15/W16 Member B commits were already corrected to Fried-toofuu on
2026-11-05, and the W15/W16 Member C commits already carried the Nerduckk
identity. Neither is touched here.

## Group B — credit allocation to Member B (34 commits)

`minhqng` → `Fried-toofuu` for 34 B-domain commits from W11 onward:
Driver/Matching/OCC/UNKNOWN, performance, backlog recovery, and their B
evidence bundles. The original Author was `minhqng`; the Committer field stays
unchanged for every commit. This is a **project-lead-directed credit
allocation** (`verifiedAuthorship: false`), made to make the visible member
contribution counts comparable after all PRs closed. It is not evidence that
Fried-toofuu wrote or committed these changes.

The 34 moved commits are listed in the map. Fifteen B-domain commits remain
with `minhqng` as integration/lead-adjacent:

| Old | Subject |
|---|---|
| `519932f` | test(driver): record reserve single-winner contention verification evidence |
| `f712515` | fix(matching): replay frozen candidate snapshot instead of rewriting it on Redis churn |
| `037294f` | fix(matching): enforce one attempt per (trip, round, driver) at the ledger |
| `4c9d5ed` | fix(driver,matching): carry matchingRound find-to-win lineage end-to-end |
| `f010231` | fix(reproducibility): isolate test output, pin images and coordinate startup DDL |
| `40cac76` | docs(recovery): close G5 with corrected real-provider evidence |
| `514053a` | docs(recovery): bind G4 readiness and canonical W14 task accounting |
| `979a723` | docs(evidence): retain source-bound functional recovery proof |
| `5653f22` | docs(evidence): correct W12 recovery scope and evidence hygiene |
| `4b571d8` | docs(evidence): rebind gated harness evidence to hardened source |
| `9fbc757` | docs(evidence): record gated performance harness readiness and smoke validation |
| `29137c4` | fix(perf): allow an isolated runs root for reproduction |
| `60993a5` | fix(perf): bind source dirtiness to non-evidence paths |
| `e20ff12` | docs(perf): document hardened gates and per-outcome measurement |
| `6f6bd59` | docs(evidence): bind B-REC-01/DSI-021 proofs to the repair head |

## Group C — credit allocation to Member C (11 commits)

`minhqng` → `Nerduckk` for 11 commits: six C-domain fixes (Payment, RabbitMQ,
fault/reliability) and five C-secondary release/CI/security commits. The
original Author was `minhqng`; the Committer field is unchanged. This is a
**project-lead-directed credit allocation** (`verifiedAuthorship: false`).

C-secondary portion: `fix(security): require explicit broker credentials...`,
`fix(release): start the Production overlay...`,
`test(release): restore the Trip project before external migrations`,
`fix(test): prove suite identity and real skip outcomes in TRX gate`, and
`ci(recovery): validate HA configuration and campaign entrypoint`.

## Contribution counts

Canonical identities via `.mailmap`:

| Identity | Before | After rewrite | After this record commit |
|---|---:|---:|---:|
| `minhqng` | 129 | 88 | 88 |
| `Nerduckk` | 81 | 88 | 88 |
| `Fried-toofuu` | 54 | 88 | 89 |
| **Total** | **264** | **264** | **265** |

The rewrite alone makes the three identities exactly equal. Recording this
correction necessarily adds one commit; it is authored by Fried-toofuu, so the
final difference is one commit. GitHub may display different totals when it
cannot resolve an email to an account; the canonical identity counts above are
the repository record.

## Preserved prior decisions

- The 2026-10-26 W09/W10 credit allocation to Nerduckk (21 commits) is
  preserved; those objects and their Authors are unchanged.
- The 2026-11-05 W15/W16 Member B corrections to Fried-toofuu are preserved.
- W15/W16 Member C commits already carried the Nerduckk identity and are not
  touched.

## Verification

- old and new tip trees are the same object: `63ead17...`;
- 264 commits before the record commit, 152 rewritten, 0 merges, 0 signed
  commits in range;
- for all 152 rewritten commits: tree, message bytes, author
  timestamp/timezone, Committer line and parent mapping compare equal; 0
  errors;
- the 112 commits before the baseline keep their SHAs;
- no test was re-run. Every rewritten commit keeps its exact tree object, so
  existing evidence remains valid for the same source tree. No CI result is
  claimed.

## Publication and recovery

The archive tag must be published **before** the rewrite:

```bash
git fetch origin tag archive/main-pre-credit-balance-2026-11-06
git switch --detach 5bf86f65210292012a9918f5b36618c9bb77d350
```

`main` is published with `--force-with-lease=refs/heads/main:5bf86f6...` after
re-fetching and confirming that exact SHA. The archive tag is immutable and
must never be moved or deleted. Restoring the old history on `main` is a new
owner decision and must repeat the published-history protocol in `AGENTS.md`.

## Decision record

- Deciding authority: Fried-toofuu (project lead), per the 2026-11-06
  correction request.
- Reason: balance the visible member contribution counts after all PRs closed,
  while preserving Committer identity and the original Author in this record.
- Reassignment type: 4 verified source-PR corrections (Group A) and 45
  credit allocations (Groups B/C); no credit allocation is described as a
  verified authorship finding.
