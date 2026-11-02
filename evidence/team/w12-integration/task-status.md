# W12 canonical task accounting

Technical statuses are bound to the verified code tree in manifest.json. Human cross-explanations are waived and remain NOT_STARTED; no human execution is invented. Integrator corrections and reruns are credited separately from original member contributions. Final candidate CI remains a separate acceptance condition.

| Owner | Canonical task | Deliverable | Status | Evidence / merge impact |
|---|---|---|---|---|
| A | A-W12-01 | Instrument Trip domain spans/logs | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| A | A-W12-02 | Add Trip/Saga/Outbox metrics | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| A | A-W12-03 | Validate trace propagation across async path | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| A | A-W12-04 | Trip authz/invalid transition negative tests | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| A | A-W12-05 | Run A full regression | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| A | A-W12-06 | Run invariant auditor | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| A | A-W12-07 | Review G4 checklist | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| A | A-W12-08 | Cross-explain A mechanisms to B/C | NOT_STARTED | WAIVED by explicit project lead instruction. |
| A | A-W12-09 | Evidence gap repair | DONE_VERIFIED | CI 41432807889: Trip unit/integration regression; five-service traces; authenticated Trip negatives; runtime owning-authority auditor. |
| B | B-W12-01 | Instrument Driver metrics | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-02 | Instrument Matching metrics | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-03 | Redis projection metrics | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-04 | Trace Reserve/reconcile path | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-05 | Driver authz/actor spoofing negative tests | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-06 | Location invalid-data tests | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-07 | Run B full regression | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-08 | Run auditor | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-09 | G4 review | DONE_VERIFIED | CI 41432807889: Driver/Matching unit/integration and B auditor suites; real production restart/UNKNOWN/stage tests; 55 real boundary checks; authority snapshots. |
| B | B-W12-10 | Cross-explain B mechanisms to A/C | NOT_STARTED | WAIVED by explicit project lead instruction. |
| C | C-W12-01 | Finalize OTel instrumentation baseline | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-02 | Messaging propagation validation | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-03 | Build Rabbit dashboard | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-04 | Build Outbox/consumer dashboard | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-05 | Build Payment dashboard | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-06 | Run SEC auth/role baseline | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-07 | Run DB isolation SEC-08 | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-08 | Secret scan/config review | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-09 | Run C full regression | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-10 | G4 review | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |
| C | C-W12-11 | Explain C stack to A/B | NOT_STARTED | WAIVED by explicit project lead instruction. |
| C | C-W12-12 | Evidence gap repair | DONE_VERIFIED | CI 41432807889: Platform/Payment/Notification suites; seven real Identity negatives; 30 TCP DB denials; finite dashboard metrics; secret-path and dependency review. |

| Member | Canonical tasks | Technical verified | Human waived / not performed | Partial | Blocked |
|---|---:|---:|---:|---:|---:|
| A | 9 | 8 | 1 | 0 | 0 |
| B | 10 | 9 | 1 | 0 | 0 |
| C | 12 | 11 | 1 | 0 | 0 |
