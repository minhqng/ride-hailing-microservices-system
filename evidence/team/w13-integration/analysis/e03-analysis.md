# W13 analysis — E03

Verdict: **ACCEPTED**

Acceptance: **YES**

Runs: 9 · reportGrade: True · G4 green: True

## Groups
```json
[
  {
    "replicas": 1,
    "runCount": 3,
    "sampleCount": 1320,
    "configuredRatePerSecond": {
      "count": 3,
      "median": 20.0,
      "min": 20.0,
      "max": 20.0,
      "range": 0.0
    },
    "achievedSendRatePerSecond": {
      "count": 3,
      "median": 21.949580277512464,
      "min": 21.93787949393637,
      "max": 21.950801887242495,
      "range": 0.012922393306123325
    },
    "achievedConsumeRatePerSecond": {
      "count": 3,
      "median": 19.939951832118027,
      "min": 19.933237706336296,
      "max": 19.952571540111965,
      "range": 0.019333833775668552
    },
    "p50IntakeLatencyMs": {
      "count": 3,
      "median": 22.909,
      "min": 22.355,
      "max": 23.91,
      "range": 1.5549999999999997
    },
    "p95IntakeLatencyMs": {
      "count": 3,
      "median": 43.224,
      "min": 42.376,
      "max": 44.117000000000004,
      "range": 1.7410000000000068
    },
    "queuePeakMessages": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 1.0,
      "range": 1.0
    },
    "redeliveryDelta": {
      "count": 3,
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
    "runCount": 3,
    "sampleCount": 1320,
    "configuredRatePerSecond": {
      "count": 3,
      "median": 20.0,
      "min": 20.0,
      "max": 20.0,
      "range": 0.0
    },
    "achievedSendRatePerSecond": {
      "count": 3,
      "median": 21.931386779125496,
      "min": 21.925236259773,
      "max": 21.952706187395222,
      "range": 0.02746992762222078
    },
    "achievedConsumeRatePerSecond": {
      "count": 3,
      "median": 19.929507140868683,
      "min": 19.929014047519434,
      "max": 19.934543232806163,
      "range": 0.005529185286729188
    },
    "p50IntakeLatencyMs": {
      "count": 3,
      "median": 23.033,
      "min": 22.970000000000002,
      "max": 23.3,
      "range": 0.3299999999999983
    },
    "p95IntakeLatencyMs": {
      "count": 3,
      "median": 42.514,
      "min": 42.114,
      "max": 43.693000000000005,
      "range": 1.5790000000000077
    },
    "queuePeakMessages": {
      "count": 3,
      "median": 1.0,
      "min": 0.0,
      "max": 1.0,
      "range": 1.0
    },
    "redeliveryDelta": {
      "count": 3,
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
    "replicas": 4,
    "runCount": 3,
    "sampleCount": 1320,
    "configuredRatePerSecond": {
      "count": 3,
      "median": 20.0,
      "min": 20.0,
      "max": 20.0,
      "range": 0.0
    },
    "achievedSendRatePerSecond": {
      "count": 3,
      "median": 21.951543394719824,
      "min": 21.924366525966818,
      "max": 21.952282104921874,
      "range": 0.027915578955056475
    },
    "achievedConsumeRatePerSecond": {
      "count": 3,
      "median": 15.933284533336279,
      "min": 15.91646979348725,
      "max": 19.93560907886862,
      "range": 4.019139285381369
    },
    "p50IntakeLatencyMs": {
      "count": 3,
      "median": 22.44,
      "min": 22.356,
      "max": 22.767,
      "range": 0.4109999999999978
    },
    "p95IntakeLatencyMs": {
      "count": 3,
      "median": 42.431000000000004,
      "min": 42.083000000000006,
      "max": 42.982,
      "range": 0.8989999999999938
    },
    "queuePeakMessages": {
      "count": 3,
      "median": 1.0,
      "min": 0.0,
      "max": 1.0,
      "range": 1.0
    },
    "redeliveryDelta": {
      "count": 3,
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
