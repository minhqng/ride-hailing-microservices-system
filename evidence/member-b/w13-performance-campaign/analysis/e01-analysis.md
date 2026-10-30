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
      "median": 7.696899934671819,
      "min": 7.696899934671819,
      "max": 7.696899934671819,
      "range": 0.0
    },
    "p95Ms": {
      "count": 1,
      "median": 7.696899934671819,
      "min": 7.696899934671819,
      "max": 7.696899934671819,
      "range": 0.0
    },
    "wallMs": {
      "count": 1,
      "median": 19.89210001192987,
      "min": 19.89210001192987,
      "max": 19.89210001192987,
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
      "median": 8.390899980440736,
      "min": 8.390899980440736,
      "max": 8.390899980440736,
      "range": 0.0
    },
    "p95Ms": {
      "count": 1,
      "median": 16.390700009651482,
      "min": 16.390700009651482,
      "max": 16.390700009651482,
      "range": 0.0
    },
    "wallMs": {
      "count": 1,
      "median": 17.459499998949468,
      "min": 17.459499998949468,
      "max": 17.459499998949468,
      "range": 0.0
    },
    "barrierSpreadMs": {
      "count": 1,
      "median": 0.0906,
      "min": 0.0906,
      "max": 0.0906,
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
      "median": 16.961000044830143,
      "min": 16.961000044830143,
      "max": 16.961000044830143,
      "range": 0.0
    },
    "p95Ms": {
      "count": 1,
      "median": 23.60970003064722,
      "min": 23.60970003064722,
      "max": 23.60970003064722,
      "range": 0.0
    },
    "wallMs": {
      "count": 1,
      "median": 43.80039998795837,
      "min": 43.80039998795837,
      "max": 43.80039998795837,
      "range": 0.0
    },
    "barrierSpreadMs": {
      "count": 1,
      "median": 0.8877,
      "min": 0.8877,
      "max": 0.8877,
      "range": 0.0
    }
  }
]
```

## Validity notes
- Domain conflicts are reported separately from system errors.
- P50/P95 are nearest-rank percentiles with sample counts.
- Smoke evidence can never become an acceptance result.
