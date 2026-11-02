# Historical observations superseded for W14 release acceptance

The source26b81ec E04 group passed its bounded single-broker measurement protocol.
Its data are retained without being relabeled as failed performance results.
The full HA recovery later exposed destructive repeated join_cluster startup and
lost a previously confirmed release. Therefore this group and earlier functional
HA cases do not close the corrected release gate. The corrected724d6c6 E04 group
and corrected conservation/full-runtime proofs are the accepted closeout evidence.

Some core JSON was normalized to LF for portability. Original core digests are
retained in the corresponding runtime report. Raw original files remain local.
The source26b run manifests embed their actual earlier G4 certificate and source;
reproduction of historical analysis must use the corresponding historical code.
Never run this old HA startup against an existing production cluster.
