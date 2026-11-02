import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';
import { sha256 } from 'k6/crypto';

// PERF/E02 — canonical production API offered load.
//
// The scenario uses k6's open-model constant-arrival-rate executor: iteration
// starts are independent of SUT response duration, so a slow system cannot
// quietly reduce the offered rate. The target endpoint must be the canonical
// authenticated POST /trips surface; this script never falls back to a
// Testing-only or unauthenticated demo route.
//
// Every iteration is classified into exactly one semantic outcome:
// success, expected domain conflict, authentication failure, system failure,
// or unexpected protocol outcome. Domain conflicts are never merged into
// system errors.

const tokenFile = __ENV.K6_TOKEN_FILE;
if (!tokenFile) {
  throw new Error('K6_TOKEN_FILE is required');
}
const tokens = open(tokenFile)
  .split(/\r?\n/)
  .map((value) => value.trim())
  .filter((value) => value.length > 0);
if (tokens.length === 0) {
  throw new Error('K6_TOKEN_FILE contains no usable bearer tokens');
}

const baseUrl = (__ENV.K6_BASE_URL || 'http://gateway:8080').replace(/\/+$/, '');
const endpoint = __ENV.K6_ENDPOINT || '/trips';
const rate = Number.parseInt(__ENV.K6_RATE || '10', 10);
const duration = __ENV.W13_MEASUREMENT_DURATION || '30s';
const preAllocatedVUs = Number.parseInt(__ENV.K6_PREALLOC_VUS || '30', 10);
const maxVUs = Number.parseInt(__ENV.K6_MAX_VUS || '80', 10);
const seed = __ENV.K6_SEED || '1302';

export const options = {
  scenarios: {
    offered_load: {
      executor: 'constant-arrival-rate',
      rate: rate,
      timeUnit: '1s',
      duration: duration,
      preAllocatedVUs: preAllocatedVUs,
      maxVUs: maxVUs,
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(50)', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],
  // No invented SOTA thresholds before the baseline exists. Interpretation is
  // owned by the W13 analyzer and the experiment record.
  thresholds: {},
};

const success = new Counter('w13_success');
const domainConflicts = new Counter('w13_domain_conflicts');
const authFailures = new Counter('w13_auth_failures');
const systemErrors = new Counter('w13_system_errors');
const unexpected = new Counter('w13_unexpected');
const latencySuccess = new Trend('w13_latency_success_ms', true);
const latencyDomainConflict = new Trend('w13_latency_domain_conflict_ms', true);
const latencyAuthFailure = new Trend('w13_latency_auth_failure_ms', true);
const latencySystemError = new Trend('w13_latency_system_error_ms', true);
const latencyUnexpected = new Trend('w13_latency_unexpected_ms', true);

function hexUuid(input) {
  const hex = sha256(input, 'hex');
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    '4' + hex.slice(13, 16),
    '8' + hex.slice(17, 20),
    hex.slice(20, 32),
  ].join('-');
}

function coordinates(vu, iter) {
  const hex = sha256(`${seed}:coord:${vu}:${iter}`, 'hex');
  const latOffset = (Number.parseInt(hex.slice(0, 6), 16) / 0xffffff - 0.5) * 0.02;
  const lonOffset = (Number.parseInt(hex.slice(6, 12), 16) / 0xffffff - 0.5) * 0.02;
  return {
    pickup: { latitude: 10.7769 + latOffset, longitude: 106.7009 + lonOffset },
    dropoff: { latitude: 10.7769 - latOffset, longitude: 106.7009 - lonOffset },
  };
}

function parseBody(value) {
  if (!value) {
    return null;
  }
  try {
    return JSON.parse(value);
  } catch (error) {
    return null;
  }
}

export default function () {
  const vu = __VU;
  const iter = __ITER;
  const token = tokens[(vu + iter) % tokens.length];
  const points = coordinates(vu, iter);
  const key = `w13-e02-${seed}-${vu}-${iter}`;
  const response = http.post(
    `${baseUrl}${endpoint}`,
    JSON.stringify(points),
    {
      headers: {
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/json',
        Accept: 'application/json',
        'Idempotency-Key': key,
      },
      tags: { endpoint: 'create-ride' },
    },
  );
  const body = parseBody(response.body);
  const code = body && typeof body === 'object' ? body.code : undefined;
  const duration = response.timings.duration;
  if (response.status === 200 || response.status === 201) {
    success.add(1);
    latencySuccess.add(duration);
  } else if (response.status === 409) {
    domainConflicts.add(1, { code: code || 'DOMAIN_CONFLICT' });
    latencyDomainConflict.add(duration);
  } else if (response.status === 401 || response.status === 403) {
    authFailures.add(1, { status: String(response.status) });
    latencyAuthFailure.add(duration);
  } else if (response.status === 0 || response.status === 429 || response.status >= 500) {
    systemErrors.add(1, { status: String(response.status) });
    latencySystemError.add(duration);
  } else {
    unexpected.add(1, { status: String(response.status) });
    latencyUnexpected.add(duration);
  }
}
