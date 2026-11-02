# TEAM W14 integration — acceptance pending

Base: verified W13 main `9fe6012ce8d3ee80ad3c3a04cee1b3a51084e980`,
tree `47b18da9e4abe62621c16fcbd1fce5fca901878e`; fresh main CI
[41469700053](https://github.com/minhqng/ride-hailing-microservices-system/actions/runs/41469700053)
passed all four jobs with downloaded checkout and test evidence verified.

Weekly source mapping (original branches preserved):

| Owner | Source | Exact W14 delta | Integration |
|---|---|---|---|
| A | [#48](https://github.com/minhqng/ride-hailing-microservices-system/pull/48), `6dfd301e6939b7b37b839836aa87a58b2360a442` | two commits after `b875d1c` | imported with original authors |
| B | [#46](https://github.com/minhqng/ride-hailing-microservices-system/pull/46), `a86ceb550fa46fd0357c50f582e880763224d40b` | nine commits after `16dc175` | eight imported; `25c4327` certificate-verdict fix already represented in W13 |
| C | [#53](https://github.com/minhqng/ride-hailing-microservices-system/pull/53), `c88ce66dc7faaa5c63703518ee91117e69b8d229` | three commits after `9d57c4c` | imported with original authors |

No cumulative W10–W13 implementations or W15/W16 changes were imported.
Human peer review/rerun requirements are explicitly WAIVED by the project lead.
This does not assert a human execution, technical acceptance or GitHub approval.

Current acceptance: IMPLEMENTED_UNVERIFIED. Historical B functional smoke and
C script/configuration validation remain historical observations, not G5 proof.
Current candidate needs fresh G4, repeated E04, live three-node REL-15/16,
all-owner authoritative recovery audits, clean-clone rehearsal, exact-tree Linux
CI and fresh post-merge main CI. Optional A-W14-07 remains outside mandatory
scope. The campaign must stop the merge if any mandatory proof is absent.

Integration corrections: preserve cancellation queue validation; wire Trip to
the shared broker endpoint resolver; bootstrap real Identity Driver ownership
in the recovery runner; retain strict dirty-source G4 checks; redact URI
userinfo and Erlang cookies; initialize a disposable cookie without rotating
an existing authentication key pair.
