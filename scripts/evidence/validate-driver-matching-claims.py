"""Recalculate retained measurements; never transfer acceptance to another tree.

W13 original engine streams remain local-only. Complete curated samples and
owning-state snapshots are checked here, including their committed digests.
W14 strict validation is reused without bypassing its artifact or state checks.
"""
import argparse
import csv
import hashlib
import json
import math
import statistics
import subprocess
import sys
from pathlib import Path
from collections import Counter, defaultdict
from datetime import datetime
ROOT = Path(__file__).resolve().parents[2]
W13 = ROOT / 'evidence/team/w13-integration'
W14 = ROOT / 'evidence/team/w14-integration'


def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))

def rows(p):
    with p.open(encoding='utf-8-sig', newline='') as stream:
        return list(csv.DictReader(stream))

def sha256(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()

def timestamp(s):
    return datetime.fromisoformat(s.replace('Z', '+00:00'))

def quantile(v, p, linear=False):
    v = sorted(map(float, v))
    assert v and all(math.isfinite(x) and x >= 0 for x in v), 'invalid latency samples'
    if not linear:
        return v[max(0, math.ceil(len(v) * p) - 1)]
    pos = (len(v) - 1) * p
    lower = math.floor(pos)
    upper = math.ceil(pos)
    return v[lower] + (v[upper] - v[lower]) * (pos - lower)

def check_close(a, b):
    assert math.isclose(a, b, rel_tol=1e-09, abs_tol=1e-05), (a, b)

def validate_digests(provenance, results):
    for label, base, files in [('W13 curated', W13, provenance['curatedFiles']), ('W14 root', W14, read(W14 / 'artifact-manifest.json')['files']), ('W14 E04', ROOT, read(W14 / 'e04/artifact-manifest.json')['files'])]:
        assert files, (label, 'empty digest inventory')
        missing = []
        bad = []
        for f in files:
            p = (base / f['path']).resolve()
            assert p.is_relative_to(base.resolve()), (label, 'artifact path escapes bundle')
            if not p.exists():
                missing.append(str(p))
            elif sha256(p) != f['sha256']:
                bad.append(str(p))
        results['digests'][label] = {'count': len(files), 'missing': missing, 'bad': bad}
        assert not missing and (not bad), (label, missing, bad)
    results['digests']['W13 original streams'] = {'count': len(provenance['rawFiles']), 'available': sum(((ROOT / f['path']).exists() for f in provenance['rawFiles']))}


def validate_w13(provenance, results):
    manifests = [(p, read(p)) for p in sorted((W13 / 'runs').glob('*/*/manifest.json'))]
    expected_runs = {r['runId']: r['experiment'] for r in provenance['runs']}
    observed_runs = {m['runId']: m['experiment'] for _, m in manifests}
    assert len(expected_runs) == len(provenance['runs']) == 24, 'invalid W13 run inventory'
    assert len(observed_runs) == len(manifests) and observed_runs == expected_runs, 'missing or duplicate W13 runs'
    groups = defaultdict(list)
    for path, m in manifests:
        source = m['source']
        assert source['commit'] == provenance['sourceCommit'] and source['tree'] == provenance['sourceTree'], 'W13 run source differs from curated provenance'
        assert subprocess.check_output(['git', 'rev-parse', source['commit'] + '^{tree}'], cwd=ROOT, text=True).strip() == source['tree']
        rid = m['runId']
        exp = m['experiment']
        a = m['workload']['achieved']
        cfg = m['workload']['configured']
        run = path.parent
        if exp == 'E01':
            rr = rows(W13 / 'samples' / f'{rid}.csv')
            for n in [1, 10, 25, 50, 100]:
                ss = [r for r in rr if int(r['n']) == n]
                out = next((x for x in a['runs'] if x['n'] == n))
                assert len(ss) == n and Counter((r['outcome'] for r in ss)) == Counter(success=1, domainConflict=n - 1)
                p50 = quantile([r['latencyMs'] for r in ss], 0.5)
                p95 = quantile([r['latencyMs'] for r in ss], 0.95)
                check_close(p50, out['p50Ms'])
                check_close(p95, out['p95Ms'])
                final = read(run / 'final-state' / f'e01-n{n}.json')
                assert final['final']['driver']['status'] == 'RESERVED' and final['afterExactRelease']['driver']['status'] == 'AVAILABLE' and (not final['afterExactRelease']['activeReservationRows'])
                groups[exp, n].append({'n': len(ss), 'p50': p50, 'p95': p95})
        elif exp == 'E02':
            ss = rows(W13 / 'samples' / f'{rid}.csv')
            counts = Counter((r['outcome'] for r in ss))
            for kind, key in [('success', 'successCount'), ('domainConflict', 'domainConflictCount'), ('authFailure', 'authFailureCount'), ('systemError', 'systemErrorCount'), ('unexpected', 'unexpectedCount')]:
                assert counts[kind] == a[key]
            p50 = quantile([r['latencyMs'] for r in ss], 0.5, True)
            p95 = quantile([r['latencyMs'] for r in ss], 0.95, True)
            check_close(p50, a['p50Ms'])
            check_close(p95, a['p95Ms'])
            outcome_quantiles = {kind: {'n': counts[kind], 'p50': quantile([r['latencyMs'] for r in ss if r['outcome'] == kind], 0.5, True), 'p95': quantile([r['latencyMs'] for r in ss if r['outcome'] == kind], 0.95, True)} for kind in ('success', 'domainConflict')}
            groups[exp, cfg['configuredRatePerSecond']].append({'n': len(ss), 'success': counts['success'], 'conflict': counts['domainConflict'], 'p50': p50, 'p95': p95, 'rate': a['achievedRatePerSecond'], 'dropped': a['droppedIterations'], 'outcomeQuantiles': outcome_quantiles})
        elif exp == 'E03':
            ss = rows(W13 / 'samples' / f'{rid}-intakes.csv')
            dd = rows(W13 / 'samples' / f'{rid}-deliveries.csv')
            qq = rows(W13 / 'broker' / f'{rid}-queue.csv')
            final = read(run / 'final-state/e03-matching-state.json')
            authority = {x['input_message_id']: x for x in final['rounds']}
            assert len(ss) == len(authority) == len(final['rounds']) == 400 and len(dd) == 440 and (len(set((r['messageId'] for r in dd))) == 400)
            latency = []
            for r in ss:
                x = authority[r['messageId']]
                assert x['trip_id'] == r['tripId']
                v = (timestamp(x['created_at']) - timestamp(r['publishedAtUtc'])).total_seconds() * 1000
                check_close(v, float(r['storedCreationProxyMs']))
                latency.append(v)
            p50 = quantile(latency, 0.5)
            p95 = quantile(latency, 0.95)
            check_close(p50, a['p50IntakeLatencyMs'])
            check_close(p95, a['p95IntakeLatencyMs'])
            ack = int(final['queueAfter']['message_stats']['ack']) - int(qq[0]['ack'])
            assert ack == 440
            elapsed = a['durationSeconds'] + a['drainSeconds'] + a['brokerAckStatsWaitSeconds']
            check_close(elapsed, a['brokerAckObservationSeconds'])
            rate = ack / elapsed
            check_close(rate, a['observedAcknowledgementRatePerSecond'])
            groups[exp, cfg['replicas']].append({'n': 400, 'deliveries': 440, 'ack': ack, 'p50': p50, 'p95': p95, 'observedAckRate': rate, 'elapsed': elapsed, 'ackFirst': int(qq[0]['ack']), 'ackLast': int(qq[-1]['ack'])})
    expected_points = {'E01': {1, 10, 25, 50, 100}, 'E02': {10, 25, 50, 100}, 'E03': {1, 2, 4}}
    assert set(groups) == {(e, point) for e, points in expected_points.items() for point in points}
    assert all(len(runs) == 3 for runs in groups.values()), 'each W13 point requires three repeats'
    for (e, k), runs in groups.items():
        row = {'point': k, 'runs': len(runs), 'samples': sum((r['n'] for r in runs)), 'p50': statistics.median((r['p50'] for r in runs)), 'p95': statistics.median((r['p95'] for r in runs)), 'p95Range': [min((r['p95'] for r in runs)), max((r['p95'] for r in runs))], 'individual': runs}
        for key in ('success', 'conflict', 'deliveries', 'ack'):
            if key in runs[0]:
                row[key] = sum((r[key] for r in runs))
        for key in ('rate', 'observedAckRate'):
            if key in runs[0]:
                row[key] = {'median': statistics.median((r[key] for r in runs)), 'range': [min((r[key] for r in runs)), max((r[key] for r in runs))]}
        results[e].append(row)


def validate_w14(results):
    sys.path.insert(0, str(ROOT / 'scripts/perf'))
    import w14_analyze as w14
    accepted = read(W14 / 'e04/analysis.json')
    manifests = sorted((W14 / 'e04').glob('W14-*/manifest.json'))
    assert accepted['status'] == 'ACCEPTED' and not accepted['errors']
    assert len(manifests) == len(accepted['runs']) == 3, 'E04 requires three accepted runs'
    repeats = set()
    for p in manifests:
        m = read(p)
        repeat = m['repeat']
        assert type(repeat) is int and 1 <= repeat <= 3 and repeat not in repeats, 'invalid E04 repeat'
        repeats.add(repeat)
        s = read(p.parent / 'samples.json')
        r = read(p.parent / 'receipts.json')
        f = read(p.parent / 'final-state.json')
        start = m['interruption']['start']
        end = m['interruption']['end']
        steady = m['steadyElapsed']
        rec = [x for x in s if x['phase'] == 'recovery' and x['elapsed'] <= steady]
        duration = end - start
        lam = sum((start <= x['sentElapsed'] < end for x in r)) / duration
        mu = (rec[-1]['terminal'] - rec[0]['terminal']) / (rec[-1]['elapsed'] - rec[0]['elapsed'])
        vals = {'lambdaOutage': lam, 'T': duration, 'mu': mu, 'observedSteadySeconds': steady - end, 'peakReady': max((x['ready'] for x in s)), 'peakOutstandingBusinessWork': max((max(0, sum((x['elapsed'] <= z['elapsed'] for x in r)) - z['terminal']) for z in s)), 'confirmedPublications': len(r), 'finalAck': s[-1]['ack'], 'finalQuiescenceSeconds': s[-1]['elapsed'] - end, 'confirmP50': quantile([x['latencyMs'] for x in r], 0.5), 'confirmP95': quantile([x['latencyMs'] for x in r], 0.95)}
        for key in ('lambdaOutage', 'T', 'mu', 'observedSteadySeconds', 'peakReady', 'peakOutstandingBusinessWork', 'confirmedPublications'):
            check_close(vals[key], accepted['runs'][repeat - 1][key])
        assert len(f['rounds']) == len(f['matchingOutbox']) == len(r) and {x['input_message_id'] for x in f['rounds']} == {x['messageId'] for x in r} and (len(set((x['messageId'] for x in r))) == len(r))
        assert all((x['status'] == 'NO_DRIVER' and x['auto_execute'] and (x['selected_driver_id'] is None) for x in f['rounds'])) and all((x['status'] == 'PUBLISHED' for x in f['matchingOutbox']))
        w14.validate_run(p.parent)
        vals['strictValidator'] = 'PASS'
        results['E04'].append({'runId': m['runId'], **vals})


def validate_sources(provenance, results):
    for source, tree in [(provenance['sourceCommit'], provenance['sourceTree']), (read(W14 / 'g5-summary.json')['measurementSource'], read(W14 / 'g5-summary.json')['measurementTree'])]:
        actual = subprocess.check_output(['git', 'rev-parse', source + '^{tree}'], cwd=ROOT, text=True).strip()
        assert actual == tree
        drift = subprocess.check_output(['git', 'diff', '--name-only', source, 'HEAD', '--', '.', ':(exclude)evidence'], cwd=ROOT, text=True).splitlines()
        results['source'].append({'commit': source, 'tree': tree, 'matches': True, 'nonEvidenceDriftToCurrent': drift})


def main():
    parser = argparse.ArgumentParser(description='Recalculate historical accepted Driver/Matching samples without relabeling their source.')
    parser.add_argument('--output', type=Path, default=ROOT / 'plans/reports/claims-validation.json')
    args = parser.parse_args()
    if not __debug__:
        raise SystemExit('Run without Python optimization: assertions are evidence checks')

    results = {'digests': {}, 'source': [], 'E01': [], 'E02': [], 'E03': [], 'E04': []}
    provenance = read(W13 / 'data-provenance.json')
    validate_digests(provenance, results)
    validate_w13(provenance, results)
    validate_w14(results)
    validate_sources(provenance, results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    digest_count = sum(v['count'] for v in results['digests'].values() if 'missing' in v)
    print(f'PASS: accepted sample calculations and {digest_count} digest checks; historical source scope retained')


if __name__ == "__main__":
    main()
