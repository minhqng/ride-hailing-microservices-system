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
      "median": 4.976511364059575,
      "min": 4.976511364059575,
      "max": 4.976511364059575,
      "range": 0.0
    },
    "achievedConsumeRatePerSecond": {
      "count": 1,
      "median": 4.976511364059575,
      "min": 4.976511364059575,
      "max": 4.976511364059575,
      "range": 0.0
    },
    "p50IntakeLatencyMs": {
      "count": 1,
      "median": 19.456999999999997,
      "min": 19.456999999999997,
      "max": 19.456999999999997,
      "range": 0.0
    },
    "p95IntakeLatencyMs": {
      "count": 1,
      "median": 39.538,
      "min": 39.538,
      "max": 39.538,
      "range": 0.0
    },
    "queuePeakMessages": {
      "count": 1,
      "median": 1.0,
      "min": 1.0,
      "max": 1.0,
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
      "median": 4.975880711949845,
      "min": 4.975880711949845,
      "max": 4.975880711949845,
      "range": 0.0
    },
    "achievedConsumeRatePerSecond": {
      "count": 1,
      "median": 4.975880711949845,
      "min": 4.975880711949845,
      "max": 4.975880711949845,
      "range": 0.0
    },
    "p50IntakeLatencyMs": {
      "count": 1,
      "median": 22.783,
      "min": 22.783,
      "max": 22.783,
      "range": 0.0
    },
    "p95IntakeLatencyMs": {
      "count": 1,
      "median": 40.876999999999995,
      "min": 40.876999999999995,
      "max": 40.876999999999995,
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
