# W01–W16 contribution and attribution mapping

Status: **factual mapping complete for W15/W16; identity ambiguities remain for
human confirmation.** This document was produced by the 2026-11-03/04
remediation campaign (AUD-11). It does **not** rewrite any published commit.

## Sources

- Frozen source-to-integration maps:
  `evidence/team/w15-integration/source-mapping.json` and
  `evidence/team/w16-integration/source-mapping.json`.
- Machine-readable remediation map (generated from those maps plus
  `git show` identity data):
  `evidence/integration/aud-remediation/contribution-map.json`.
- Earlier W09/W10 metadata correction and credit-allocation record:
  `docs/06-history-attribution-correction.md`.
- Actual `main` commit graph and GitHub PR history (authoritative for
  Author/Committer fields).
- Member/team reports and evidence bundles under `evidence/` and `docs/`.

`patchEquivalent` below is the frozen mapping's logical-equivalence flag: the
integrated commit preserves the original patch/content. A `false` value means
the replay carried different hunk context or an integration repair; it is not
evidence of a different author.

## W15 mapping (source PR → integrated commits)

| Member | Source PR | Branch | Mapped commits | Actual integrated author(s) | Committer(s) | Patch-equivalent |
| --- | --- | --- | --- | --- | --- | --- |
| A | #49 | `feature/A-W15-freeze-report` | 2 | `Nerduckk` (author field `vitquaybarking <leanhviet2005@gmail.com>`) | `minhqng` | 2/2 |
| B | #62 | `feature/B-W15-freeze-report` | 6 | `minhqng` | `minhqng` | 6/6 |
| C | #54 | `feature/C-W15-freeze-report` | 5 mapped, 3 integrated | `Nerduckk` (author field `vitquaybarking`) | `minhqng` | 2 true, 3 false (context/repair) |

W15 acceptance anchor: team evidence
`evidence/team/w15-integration/` (CI run `41648524236`, exact-tree candidate
tree `72807e2e60d77c2d322acafaa7d1b880d9bcc7f5`, 1223 passed / 0 failed /
1 opt-in Identity skip). Two C-mapped commits were not imported in the frozen
map; the map records them explicitly.

## W16 mapping (source PR → integrated commits)

| Member | Source PR | Branch | Mapped commits | Actual integrated author(s) | Committer(s) | Patch-equivalent |
| --- | --- | --- | --- | --- | --- | --- |
| A | #50 | `feature/A-W16-release-demo` | 2 | `Nerduckk` (author field `vitquaybarking`) | `minhqng` | 2/2 |
| B | #63 | `feature/B-W16-release-demo` | 5 | `minhqng` | `minhqng` | 3 true, 2 false (context/repair) |
| C | #55 | `feature/C-W16-release-demo` | 7 | `Nerduckk` (author field `vitquaybarking`) | `minhqng` | 7/7 |

W16 acceptance anchor: team evidence
`evidence/team/w16-integration/` (release candidate, integrated journeys,
security, exact-tree CI and the automated B trials; human rehearsals
`NOT PERFORMED`).

## Attribution findings (do not infer authorship)

The source PR/branch mapping and the Git Author fields disagree in W15/W16:

- Member A's W15/W16 integrated commits carry the author identity
  `vitquaybarking <leanhviet2005@gmail.com>`, which `.mailmap` canonically maps
  to `Nerduckk` (Member C), not Member A.
- Member B's W15/W16 integrated commits carry `minhqng
  <minhnq.181005@gmail.com>` as Author, while the source PRs are attributed to
  Member B.
- Every W15/W16 integrated commit was committed by `minhqng`; a rebase/merge
  can legitimately assign a different Committer than Author.

These are recorded as **facts**, not resolved claims. The source PR mapping is
the team's attribution; the Git field is what the object actually says. The
campaign did not rewrite either field. Human confirmation is required to
decide whether these are misconfigured local identities or owner-directed
credit allocations. If a credit allocation is intended, record it per
`AGENTS.md` (deciding authority, old/new identity, unchanged Committer, archive
ref and old→new map); never describe it as verified authorship.

`patchEquivalent=false` entries are preserved from the frozen maps; they are
integration repairs/rebases, not evidence that a different person wrote the
change.

## Remediation provenance (AUD-01..AUD-11)

The remediation PRs are separate integrator work, not member W15/W16 commits.
Each PR is history-preserving (rebase merge) and keeps its own Author/Committer
fields. The exact PR list, merge SHAs, final main SHA/tree and post-merge CI
run are recorded in `evidence/integration/aud-remediation/manifest.json` and
the final release report.

## Human/course gates (not fabricated)

- Independent human clean clone of W15 evidence (`TEAM-W15-02`,
  A/C clean-clone execution): **PROCESS_PENDING**.
- Three human team rehearsals, oral defense and peer sign-off:
  **PROCESS_PENDING**.
- Final `v1.0.0` tag/GitHub Release: **NOT CREATED**; requires separate
  project-lead authorization.

The automated clean clones, exact-tree CI, release smoke and B trials are real
technical evidence; they are not substitutes for the human acts above.
