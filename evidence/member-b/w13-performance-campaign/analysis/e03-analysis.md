# W13 analysis — E03

Verdict: **SMOKE_PASS**
Acceptance: **NO**
Runs: 2 · reportGrade: False · G4 green: False

## Groups
```json
[
  {
    "replicas": 1,
    "runCount": 1,
    "sampleCount": 25,
    "configuredRatePerSecond": {
      "count": 1,
      "median": 5.0,
      "min": 5.0,
      "max": 5.0,
      "range": 0.0
    },
    "achievedSendRatePerSecond": {
      "count": 1,
      "median": 5.186937862960608,
      "min": 5.186937862960608,
      "max": 5.186937862960608,
      "range": 0.0
    },
    "achievedConsumeRatePerSecond": {
      "count": 1,
      "median": 5.186937862960608,
      "min": 5.186937862960608,
      "max": 5.186937862960608,
      "range": 0.0
    },
    "p50IntakeLatencyMs": {
      "count": 1,
      "median": 20.355,
      "min": 20.355,
      "max": 20.355,
      "range": 0.0
    },
    "p95IntakeLatencyMs": {
      "count": 1,
      "median": 46.828,
      "min": 46.828,
      "max": 46.828,
      "range": 0.0
    },
    "queuePeakMessages": {
      "count": 1,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "redeliveryDelta": {
      "count": 1,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "perReplicaNonzero": true,
    "workerMode": [
      "disabled"
    ]
  },
  {
    "replicas": 2,
    "runCount": 1,
    "sampleCount": 25,
    "configuredRatePerSecond": {
      "count": 1,
      "median": 5.0,
      "min": 5.0,
      "max": 5.0,
      "range": 0.0
    },
    "achievedSendRatePerSecond": {
      "count": 1,
      "median": 5.181646987696378,
      "min": 5.181646987696378,
      "max": 5.181646987696378,
      "range": 0.0
    },
    "achievedConsumeRatePerSecond": {
      "count": 1,
      "median": 5.181646987696378,
      "min": 5.181646987696378,
      "max": 5.181646987696378,
      "range": 0.0
    },
    "p50IntakeLatencyMs": {
      "count": 1,
      "median": 22.273,
      "min": 22.273,
      "max": 22.273,
      "range": 0.0
    },
    "p95IntakeLatencyMs": {
      "count": 1,
      "median": 26.549,
      "min": 26.549,
      "max": 26.549,
      "range": 0.0
    },
    "queuePeakMessages": {
      "count": 1,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "redeliveryDelta": {
      "count": 1,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "perReplicaNonzero": true,
    "workerMode": [
      "disabled"
    ]
  }
]
```

## Validity notes
- Domain conflicts are reported separately from system errors.
- P50/P95 are nearest-rank percentiles with sample counts.
- Smoke evidence can never become an acceptance result.
