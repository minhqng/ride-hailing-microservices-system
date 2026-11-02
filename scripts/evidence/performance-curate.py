"""Curate executed performance samples; preserve original data hashes and validate counts."""
import argparse
from collections import Counter
import csv
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def read(path):
    return json.loads(path.read_text(encoding='utf-8'))

def digest(path):
    result=hashlib.sha256()
    with path.open('rb') as handle:
        for block in iter(lambda:handle.read(1024*1024),b''):
            result.update(block)
    return result.hexdigest()

def percentile(values,p,linear=False):
    values=sorted(values)
    assert values and all(math.isfinite(v) and v>=0 for v in values)
    if not linear:
        return values[max(0,math.ceil(len(values)*p)-1)]
    position=(len(values)-1)*p
    lower=math.floor(position)
    upper=math.ceil(position)
    return values[lower]+(values[upper]-values[lower])*(position-lower)

def equal(actual,expected):
    assert math.isclose(actual,float(expected),rel_tol=1e-9,abs_tol=1e-5),(actual,expected)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runs-root',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    runs=args.runs_root.resolve()
    output=args.output.resolve()
    output.relative_to(ROOT/'evidence')
    assert read(output/'analysis/campaign-analysis.json')['acceptance'], 'Complete accepted analysis is required before curation'
    paths=sorted(runs.glob('*/*/manifest.json'))
    assert len(paths)==24,'Expected 3 E01, 12 E02 and 9 E03 manifests'
    manifests=[read(path) for path in paths]
    assert Counter(d['experiment'] for d in manifests)==Counter(E01=3,E02=12,E03=9)
    sources={(d['source']['commit'],d['source']['tree']) for d in manifests}
    assert len(sources)==1,'Measurement source differs between experiments'
    receipt={'schemaVersion':1,'sourceCommit':next(iter(sources))[0],'sourceTree':next(iter(sources))[1],
             'curationScriptSha256':digest(Path(__file__)),'policy':'Curated complete measured samples, selected observed metrics and tool summaries are portable. Full engine streams remain local-only under repository hygiene; their exact hashes are retained.',
             'rawFiles':[],'curatedFiles':[],'runs':[]}
    def record(path,**metadata):
        receipt['curatedFiles'].append({'path':path.relative_to(output).as_posix(),'sha256':digest(path),'bytes':path.stat().st_size,**metadata})
    def write_csv(path,rows,columns):
        path.parent.mkdir(parents=True,exist_ok=True)
        with path.open('w',encoding='utf-8',newline='') as handle:
            writer=csv.DictWriter(handle,fieldnames=columns,extrasaction='ignore',lineterminator='\n')
            writer.writeheader()
            writer.writerows(rows)
        record(path,sampleCount=len(rows))
    for path,d in zip(paths,manifests):
        assert d['reportGrade'] and not d.get('synthetic') and d['result']['status']=='PASS' and d['auditor']['status']=='PASS'
        run=path.parent
        run_id=d['runId']
        raw=run/'raw'
        for original in sorted(raw.iterdir()):
            assert 'token' not in original.name.lower(),'Credentials cannot be evidence'
            if original.is_file():
                receipt['rawFiles'].append({'path':original.relative_to(ROOT).as_posix(),'sha256':digest(original),'bytes':original.stat().st_size})
        for name in ('prometheus.json','warmup-prometheus.json'):
            original=raw/name
            if original.exists():
                snapshot=read(original)
                assert snapshot['verdict']=='PASS'
                target=output/'metrics'/f'{run_id}-{name}'
                target.parent.mkdir(parents=True,exist_ok=True)
                target.write_text(json.dumps(read(original),indent=2,allow_nan=False)+'\n',encoding='utf-8',newline='\n')
                record(target)
        if d['experiment']=='E01':
            rows=[]
            for original in sorted(raw.glob('e01-n*-samples.json')):
                data=read(original)
                n=data['n']
                samples=data['samples']
                assert len(samples)==n and Counter(s['outcome'] for s in samples)==Counter(success=1,domainConflict=n-1)
                state=read(run/'final-state'/f'e01-n{n}.json')
                assert state['final']['driver']['status']=='RESERVED' and state['afterExactRelease']['driver']['status']=='AVAILABLE'
                assert len(state['final']['reserveOutbox'])==len(state['afterExactRelease']['reserveOutbox'])==1
                assert not state['afterExactRelease']['activeReservationRows']
                rows.extend({**sample,'n':n,'driverId':data['driverId']} for sample in samples)
            assert len(rows)==d['result']['sampleCount']==186
            write_csv(output/'samples'/f'{run_id}.csv',rows,['n','index','outcome','status','code','latencyMs','barrierSpreadMs','driverId','tripId','reservationId'])
        elif d['experiment']=='E02':
            mapping={'w13_latency_success_ms':'success','w13_latency_domain_conflict_ms':'domainConflict',
                     'w13_latency_auth_failure_ms':'authFailure','w13_latency_system_error_ms':'systemError','w13_latency_unexpected_ms':'unexpected'}
            rows=[]
            with (raw/'k6-points.json').open(encoding='utf-8') as handle:
                for line in handle:
                    point=json.loads(line)
                    if point.get('type')=='Point' and point.get('metric') in mapping:
                        rows.append({'timestampUtc':point['data']['time'],'outcome':mapping[point['metric']], 'latencyMs':float(point['data']['value'])})
            a=d['workload']['achieved']
            expected=Counter({kind:a[field] for kind,field in [('success','successCount'),('domainConflict','domainConflictCount'),('authFailure','authFailureCount'),('systemError','systemErrorCount'),('unexpected','unexpectedCount')]})
            assert Counter(row['outcome'] for row in rows)==expected
            assert len(rows)==a['iterations']==d['result']['sampleCount']
            values=[row['latencyMs'] for row in rows]
            equal(percentile(values,.5,True),a['p50Ms'])
            equal(percentile(values,.95,True),a['p95Ms'])
            write_csv(output/'samples'/f'{run_id}.csv',rows,['timestampUtc','outcome','latencyMs'])
            target=output/'load-generator'/f'{run_id}-summary.json'
            target.parent.mkdir(parents=True,exist_ok=True)
            target.write_text(json.dumps(read(raw/'k6-summary.json'),indent=2,allow_nan=False)+'\n',encoding='utf-8',newline='\n')
            record(target)
        else:
            data=read(raw/'e03-deliveries.json')
            final=read(run/'final-state/e03-matching-state.json')
            rows_by_id={item['input_message_id']:item for item in final['rounds']}
            assert len(rows_by_id)==len(final['rounds'])==len(data['unique'])==400
            assert len(data['deliveries'])==d['workload']['achieved']['measuredBrokerAcknowledgements']==440
            intakes=[]
            for sent in data['unique']:
                created=rows_by_id[sent['messageId']]['created_at']
                latency=(datetime.fromisoformat(created.replace('Z','+00:00'))-datetime.fromisoformat(sent['sentAtUtc'].replace('Z','+00:00'))).total_seconds()*1000
                intakes.append({'messageId':sent['messageId'],'tripId':sent['tripId'],'publishedAtUtc':sent['sentAtUtc'],'storedCreatedAtUtc':created,'storedCreationProxyMs':latency})
            a=d['workload']['achieved']
            equal(percentile([row['storedCreationProxyMs'] for row in intakes],.5),a['p50IntakeLatencyMs'])
            equal(percentile([row['storedCreationProxyMs'] for row in intakes],.95),a['p95IntakeLatencyMs'])
            write_csv(output/'samples'/f'{run_id}-intakes.csv',intakes,['messageId','tripId','publishedAtUtc','storedCreatedAtUtc','storedCreationProxyMs'])
            write_csv(output/'samples'/f'{run_id}-deliveries.csv',data['deliveries'],['sequence','messageId','tripId','duplicate','sentAtUtc','sentMono','routed','outcome','scheduleLatenessMs'])
            polls=read(raw/'e03-queue-samples.json')
            write_csv(output/'broker'/f'{run_id}-queue.csv',polls,['atUtc','messagesReady','messagesUnacked','messages','consumers','publish','deliver','ack','redeliver','error'])
        receipt['runs'].append({'runId':run_id,'experiment':d['experiment'],'sampleCount':d['result']['sampleCount'],'verified':True})
    target=output/'data-provenance.json'
    target.write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8',newline='\n')
    print(json.dumps({'verifiedRuns':len(receipt['runs']),'curatedFiles':len(receipt['curatedFiles']),'rawReceipts':len(receipt['rawFiles']),'sourceCommit':receipt['sourceCommit']}))

if __name__=='__main__':
    main()
