# W13 analysis — E01

Verdict: **SMOKE_PASS**
Acceptance: **NO**
Runs: 1 · reportGrade: False · G4 green: False

## Groups
```json
[
  {
    "n": 1,
    "runCount": 1,
    "sampleCount": 1,
    "winnerCounts": [
      1
    ],
    "conflictCounts": [
      0
    ],
    "systemErrorCounts": [
      0
    ],
    "aud01": [
      "PASS"
    ],
    "assertionsAllPass": true,
    "p50Ms": {
      "count": 1,
      "median": 6.2689000042155385,
      "min": 6.2689000042155385,
      "max": 6.2689000042155385,
      "range": 0.0
    },
    "p95Ms": {
      "count": 1,
      "median": 6.2689000042155385,
      "min": 6.2689000042155385,
      "max": 6.2689000042155385,
      "range": 0.0
    },
    "wallMs": {
      "count": 1,
      "median": 7.430900004692376,
      "min": 7.430900004692376,
      "max": 7.430900004692376,
      "range": 0.0
    },
    "barrierSpreadMs": {
      "count": 1,
      "median": 0.0,
      "min": 0.0,
      "max": 0.0,
      "range": 0.0
    }
  },
  {
    "n": 2,
    "runCount": 1,
    "sampleCount": 2,
    "winnerCounts": [
      1
    ],
    "conflictCounts": [
      1
    ],
    "systemErrorCounts": [
      0
    ],
    "aud01": [
      "PASS"
    ],
    "assertionsAllPass": true,
    "p50Ms": {
      "count": 1,
      "median": 8.631000062450767,
      "min": 8.631000062450767,
      "max": 8.631000062450767,
      "range": 0.0
    },
    "p95Ms": {
      "count": 1,
      "median": 15.529499971307814,
      "min": 15.529499971307814,
      "max": 15.529499971307814,
      "range": 0.0
    },
    "wallMs": {
      "count": 1,
      "median": 16.836500028148293,
      "min": 16.836500028148293,
      "max": 16.836500028148293,
      "range": 0.0
    },
    "barrierSpreadMs": {
      "count": 1,
      "median": 0.0851,
      "min": 0.0851,
      "max": 0.0851,
      "range": 0.0
    }
  },
  {
    "n": 10,
    "runCount": 1,
    "sampleCount": 10,
    "winnerCounts": [
      1
    ],
    "conflictCounts": [
      9
    ],
    "systemErrorCounts": [
      0
    ],
    "aud01": [
      "PASS"
    ],
    "assertionsAllPass": true,
    "p50Ms": {
      "count": 1,
      "median": 13.514899997971952,
      "min": 13.514899997971952,
      "max": 13.514899997971952,
      "range": 0.0
    },
    "p95Ms": {
      "count": 1,
      "median": 19.23410000745207,
      "min": 19.23410000745207,
      "max": 19.23410000745207,
      "range": 0.0
    },
    "wallMs": {
      "count": 1,
      "median": 22.59159996174276,
      "min": 22.59159996174276,
      "max": 22.59159996174276,
      "range": 0.0
    },
    "barrierSpreadMs": {
      "count": 1,
      "median": 0.7346,
      "min": 0.7346,
      "max": 0.7346,
      "range": 0.0
    }
  }
]
```

## Validity notes
- Domain conflicts are reported separately from system errors.
- P50/P95 are nearest-rank percentiles with sample counts.
- Smoke evidence can never become an acceptance result.
