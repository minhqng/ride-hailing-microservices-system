# B12 auditor regression on the repair commit

Focused B-owned auditor regression executed against the final repair source:

```text
source commit: b4cf93c2e318b32fd191bd8ef9dae8030ec3335d
source tree:   109f8c7aee39259d68f309236706436c2e420e05
source status: clean
audit tests:   18/18 PASS
generated:     2026-10-30T00:19:16Z
```

Aggregate check disposition (all runs):

```text
PASS 56, LOCAL_PROXY_PASS 0, FAIL 11, NOT RUN 57, BLOCKED 4
positive scenarios only: PASS 31, FAIL 0, NOT RUN 17, BLOCKED 0
negative/control runs: FAIL 11 (expected detections),
                      BLOCKED 4 (expected honesty), PASS 25, NOT RUN 40
```

Owner boundaries are unchanged:

- AUD-01/02/04/06/07/08 PASS on real positive scenarios.
- AUD-03 is A-owned Trip authority (NOT RUN by B).
- AUD-05 is C-owned Payment authority (NOT RUN by B).
- FAIL counts are expected negative-control detections, not product failures.
- BLOCKED counts are expected unreachable-authority controls, not verdicts.

This is a focused auditor regression, not an overall G4 green claim. The
historical W12 auditor bundle remains bound to its original audit commit; this
file records only the final-source regression. Raw auditor JSON/Markdown output
was regenerated locally and is intentionally not duplicated here.
