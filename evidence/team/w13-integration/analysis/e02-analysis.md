# W13 analysis — E02

Verdict: **ACCEPTED**

Acceptance: **YES**

Runs: 12 · reportGrade: True · G4 green: True

## Groups
```json
[
  {
    "configuredRatePerSecond": 10.0,
    "runCount": 3,
    "sampleCount": 903,
    "achievedRatePerSecond": {
      "count": 3,
      "median": 10.03186093001861,
      "min": 10.029906644146429,
      "max": 10.032111268355141,
      "range": 0.002204624208712147
    },
    "achievedRatio": {
      "count": 3,
      "median": 1.003186093001861,
      "min": 1.002990664414643,
      "max": 1.0032111268355142,
      "range": 0.0002204624208712591
    },
    "p50Ms": {
      "count": 3,
      "median": 4.251588,
      "min": 4.171346,
      "max": 11.525626,
      "range": 7.354280000000001
    },
    "p95Ms": {
      "count": 3,
      "median": 15.746796,
      "min": 14.754307,
      "max": 15.801048,
      "range": 1.046740999999999
    },
    "droppedIterations": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "domainConflicts": {
      "count": 3,
      "median": 164.0,
      "min": 138.0,
      "max": 172.0,
      "range": 34.0
    },
    "systemErrors": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "authFailures": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "unexpected": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "finalState": [
      {
        "tripCount": "137",
        "e02IdempotencyKeys": "137",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "163",
        "e02IdempotencyKeys": "163",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "129",
        "e02IdempotencyKeys": "129",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      }
    ],
    "successCounts": [
      137,
      163,
      129
    ],
    "latencyMethod": "k6 summary trend percentiles per run; not pooled"
  },
  {
    "configuredRatePerSecond": 25.0,
    "runCount": 3,
    "sampleCount": 2253,
    "achievedRatePerSecond": {
      "count": 3,
      "median": 25.023528633528848,
      "min": 25.022777180345127,
      "max": 25.030531760195085,
      "range": 0.007754579849958532
    },
    "achievedRatio": {
      "count": 3,
      "median": 1.0009411453411539,
      "min": 1.0009110872138052,
      "max": 1.0012212704078034,
      "range": 0.0003101831939982258
    },
    "p50Ms": {
      "count": 3,
      "median": 3.978627,
      "min": 3.960923,
      "max": 3.978873,
      "range": 0.01794999999999991
    },
    "p95Ms": {
      "count": 3,
      "median": 13.031970999999999,
      "min": 12.865595500000001,
      "max": 13.353595,
      "range": 0.48799949999999903
    },
    "droppedIterations": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "domainConflicts": {
      "count": 3,
      "median": 600.0,
      "min": 562.0,
      "max": 603.0,
      "range": 41.0
    },
    "systemErrors": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "authFailures": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "unexpected": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "finalState": [
      {
        "tripCount": "189",
        "e02IdempotencyKeys": "189",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "148",
        "e02IdempotencyKeys": "148",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "151",
        "e02IdempotencyKeys": "151",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      }
    ],
    "successCounts": [
      189,
      148,
      151
    ],
    "latencyMethod": "k6 summary trend percentiles per run; not pooled"
  },
  {
    "configuredRatePerSecond": 50.0,
    "runCount": 3,
    "sampleCount": 4502,
    "achievedRatePerSecond": {
      "count": 3,
      "median": 50.02631189366259,
      "min": 50.00170762998468,
      "max": 50.02971798579281,
      "range": 0.02801035580812794
    },
    "achievedRatio": {
      "count": 3,
      "median": 1.0005262378732518,
      "min": 1.0000341525996936,
      "max": 1.0005943597158562,
      "range": 0.0005602071161625588
    },
    "p50Ms": {
      "count": 3,
      "median": 3.761512,
      "min": 3.73555,
      "max": 3.781909,
      "range": 0.04635900000000026
    },
    "p95Ms": {
      "count": 3,
      "median": 12.037188,
      "min": 11.944662,
      "max": 13.0525989,
      "range": 1.1079369000000003
    },
    "droppedIterations": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "domainConflicts": {
      "count": 3,
      "median": 1351.0,
      "min": 1308.0,
      "max": 1368.0,
      "range": 60.0
    },
    "systemErrors": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "authFailures": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "unexpected": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "finalState": [
      {
        "tripCount": "133",
        "e02IdempotencyKeys": "133",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "150",
        "e02IdempotencyKeys": "150",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "192",
        "e02IdempotencyKeys": "192",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      }
    ],
    "successCounts": [
      133,
      150,
      192
    ],
    "latencyMethod": "k6 summary trend percentiles per run; not pooled"
  },
  {
    "configuredRatePerSecond": 100.0,
    "runCount": 3,
    "sampleCount": 9003,
    "achievedRatePerSecond": {
      "count": 3,
      "median": 100.02354418243364,
      "min": 100.02296539617551,
      "max": 100.02472985289798,
      "range": 0.0017644567224692764
    },
    "achievedRatio": {
      "count": 3,
      "median": 1.0002354418243364,
      "min": 1.000229653961755,
      "max": 1.0002472985289799,
      "range": 1.7644567224861518e-05
    },
    "p50Ms": {
      "count": 3,
      "median": 3.450826,
      "min": 3.445495,
      "max": 3.456177,
      "range": 0.010681999999999636
    },
    "p95Ms": {
      "count": 3,
      "median": 11.361219,
      "min": 11.064585,
      "max": 11.647112,
      "range": 0.5825270000000007
    },
    "droppedIterations": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "domainConflicts": {
      "count": 3,
      "median": 2757.0,
      "min": 2742.0,
      "max": 2805.0,
      "range": 63.0
    },
    "systemErrors": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "authFailures": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "unexpected": {
      "count": 3,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    },
    "finalState": [
      {
        "tripCount": "196",
        "e02IdempotencyKeys": "196",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "259",
        "e02IdempotencyKeys": "259",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      },
      {
        "tripCount": "244",
        "e02IdempotencyKeys": "244",
        "activeTripsPerRiderViolations": "0",
        "duplicateIdempotencyResources": "0"
      }
    ],
    "successCounts": [
      196,
      259,
      244
    ],
    "latencyMethod": "k6 summary trend percentiles per run; not pooled"
  }
]
```

## Validity notes
- Domain conflicts are reported separately from system errors.
- P50/P95 are nearest-rank percentiles with sample counts.
- Smoke evidence can never become an acceptance result.
