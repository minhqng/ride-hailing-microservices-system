# History attribution correction (W09/W10)

Durable record of the 2026-10-26 metadata-only rewrite of `main` in the
W09/W10 integration range. Read this before resolving any W09/W10 commit SHA
from an older note, manifest, or review comment.

## What changed

| Item | Value |
|---|---|
| Rewrite baseline (unchanged) | `ba5bb40615564ab022ddb0ff5e933bde93ee15a1` |
| Previous `main` | `fbe3eb674bb716fb566fb6fbb09c31d028f8488c` |
| Current `main` (rewrite tip) | `c166a3a3961f349337d7062adbbce5d49ea256b6` |
| Rewritten commits | 31 (`ba5bb40..fbe3eb6`, linear, no merges) |
| Tree object before / after | `e5be68d72e114e15a7580d778a4b088d5f96d6e9` (identical) |
| Archive anchor | `archive/main-pre-attribution-correction-2026-10-26` → `fbe3eb6` |

Only two commit fields changed: the **Author name/email** on 29 of the 31
commits, and the **parent pointer** of every commit whose ancestor was
rewritten. Every other field is byte-identical:

- tree object of every commit (so every patch and the final source tree);
- commit message bytes, including trailers;
- Committer identity;
- author date and committer date, with original time zones;
- the baseline commit and everything before it (107 → 107 commits, still
  linear, still 0 merges);
- signed commits `7365f03`, `9ffa7ea`, `d007fe4`, `38ad204`, `12611bf` and the
  `evidence/w08-ci-38912031301` tag — all outside the rewrite range.

No subject was changed. The W09/W10 week labels were kept deliberately: the
same convention runs through W01–W08, so removing them from only this range
would make the history inconsistent and widen a metadata correction into a
rewrite of unrelated content. Subject cleanup, if wanted, is a separate
decision over the whole history.

The machine-readable map is
[`evidence/integration/w09-w10/attribution-rewrite.json`](../evidence/integration/w09-w10/attribution-rewrite.json):
every old SHA, new SHA, tree, author before/after, committer, both dates,
parent before/after, and the group each commit belongs to.

## Group A — verified authorship correction (8 commits)

These eight commits were integrated from Member B's work and were independently
traced to Member B's source PRs. The integrated objects are patch-identical
replays of the source-branch commits, and the source PRs were opened by
`Fried-toofuu`. The Git author field recorded `minhqng` instead — a
misconfigured local identity, not a different author.

| Integrated (old) | Replacement (new) | Source PR | Source commit |
|---|---|---|---|
| `53765b8` | `ab64077` | #37 | `0b1611c` |
| `cbe6c81` | `e0996a3` | #37 | `03c0438` |
| `59d0dac` | `49cc543` | #37 | `d825920` |
| `be654f9` | `6d974ae` | #37 | `66d7d1c` |
| `b3f464e` | `1e2cc04` | #39 | `b1435f7` |
| `3e6a893` | `ffce050` | #39 | `2d9a303` |
| `a1229d2` | `feb41c1` | #39 | `87b639f` |
| `34baae0` | `846930a` | #39 | `8bf0da6` |

New author: `Fried-toofuu <nghoangkhanh67@gmail.com>`. Committer stays
`minhqng <minhnq.181005@gmail.com>`, because that is who actually applied the
integrated objects.

This group is a **verified authorship correction**: the new Author is a claim
supported by the source PRs and by patch identity.

## Group B — owner-directed credit allocation (21 commits)

The remaining 21 commits of the integration batch carried
`minhqng <minhnq.181005@gmail.com>` as Author. The repository owner directed
that these commits display `Nerduckk <leanhviet2005@gmail.com>`.

This is a **credit-allocation decision, not an authorship finding**. It is not
evidence that Nerduckk wrote or committed those changes, and nothing in the
authorship audit established that. The Committer field is unchanged and still
records the person who applied each object, so the audit trail survives:

```
author    Nerduckk <leanhviet2005@gmail.com>      <- display/credit decision
committer minhqng <minhnq.181005@gmail.com>       <- actual applier
original author (pre-rewrite) minhqng <minhnq.181005@gmail.com>
```

The 21 commits and their original identities are listed in
`attribution-rewrite.json` under `groups.B.commits`, each with a
`creditAllocation` block naming the deciding authority and stating
`verifiedAuthorship: false`.

## Group C — untouched attribution (2 commits)

`5130b0f` (C-W09) and `5b07085` (C-W10-only) already carried the correct
author, `vitquaybarking <leanhviet2005@gmail.com>`. Their author was not
touched. `5130b0f` kept its SHA outright because its parent and author did not
change; `5b07085` moved only because its parent moved.

## Contribution statistics

Author counts over the rewrite range, raw (no mailmap):

| Identity | Before | After |
|---|---|---|
| `minhqng <minhnq.181005@gmail.com>` | 29 | 0 |
| `vitquaybarking <leanhviet2005@gmail.com>` | 2 | 2 |
| `Nerduckk <leanhviet2005@gmail.com>` | 0 | 21 |
| `Fried-toofuu <nghoangkhanh67@gmail.com>` | 0 | 8 |

Whole history, canonical identities via `.mailmap`:

| Identity | Before | After |
|---|---|---|
| `minhqng` | 50 | 21 |
| `Nerduckk` (incl. `vitquaybarking` aliases) | 32 | 53 |
| `Fried-toofuu` (incl. `scooby-dooby-doo-w`, `Khahn_nh`, `Member B`) | 25 | 33 |

The deltas are the 8 verified corrections and the 21 allocated credits. No
commit before `ba5bb40` was touched, so pre-integration leader contributions are
unchanged.

## Evidence anchors

`evidence/integration/w09-w10/manifest.json` records the verified code tip as
`b6d57b6aa22bd38d8e8d0a42ac2bea3f5fa3dd06` with tree
`5de1ff927e3deadf66ef89c998f4d43ee1b2b60c`. That claim is left exactly as it
was written: the verification ran against the pre-rewrite object. The object is
retained by the archive tag, and its replacement is `8b819eb` with the same
tree.

```bash
git fetch origin tag archive/main-pre-attribution-correction-2026-10-26
git rev-parse b6d57b6^{tree}          # 5de1ff9... (historical verified tree)
git rev-parse 8b819eb^{tree}          # 5de1ff9... (same tree on current main)
scripts/test/verify-source-anchor.sh b6d57b6 5de1ff927e3deadf66ef89c998f4d43ee1b2b60c
```

No test was re-run for this rewrite and no CI result is claimed: the source
tree is provably identical, so the existing run remains valid for the same
tree. GitHub Actions could not start (account billing), which is neither a pass
nor a failure of this change.

## Recovery

The previous history is one fetch away and is not dependent on any local
checkout or temporary clone:

```bash
git fetch origin tag archive/main-pre-attribution-correction-2026-10-26
git switch --detach fbe3eb674bb716fb566fb6fbb09c31d028f8488c
```

Restoring it on `main` would be a new owner decision and must repeat the
published-history protocol in [AGENTS.md](../AGENTS.md); the archive tag itself
must never be moved or deleted.

## Successor branches (W11/W12, PR #41/#42/#43)

`feature/A-W11-trip-fault-proofs`, `feature/A-W12-trip-observability-g4`,
`feature/C-W10-W11-payment-fault-platform`, `MemC-W9-Distributed`, and the
closed PR heads were not modified. Their merge-base with `main` is still
`ba5bb40`, and the effective three-dot diff against the new `main` is
byte-identical to the diff against the old `main` (95 files, +13212/−216 for
A-W11).

That is exactly why an unchanged merge-base is not proof of a clean future PR:
16 of A-W11's 20 commits and 16 of A-W12's 24 are patch-identical to W09/W10
commits already on `main`, so a naive merge would try to land the same work
twice. The restack requirement is unchanged by this rewrite and is documented
in `evidence/integration/w09-w10/attribution-rewrite.json` and the integration
bundle README.
