# Rejected controlled-environment attempt

Source: `005735022accaa6fdf72e34bbb1e97d18a3b3324`.

All three E01 runs passed their business-state and finite-metric checks, but
their Driver and Identity application image identities differed. Configured
services and observed host allocation matched. Prior image contents were no
longer inspectable after subsequent rebuilds, so equivalence is unverified.
The analyzer correctly returned INCOMPLETE. No performance acceptance is claimed.

The controller was stopped during E02 and its isolated stack removed. Every
completed manifest, raw sample, final-state artifact and partial directory is
retained here. The original prospective protocol is `protocol.json`. These
setup-invalid observations are excluded from accepted statistical summaries;
they are not discarded system failures or selected best runs.

Correction: build all required application images once, freeze their recorded
identities, use `--no-build` for every reset, and repeat the entire protocol.
No authorization, correctness assertion or environment-comparability gate was
relaxed. Main remains the verified W12 baseline.
