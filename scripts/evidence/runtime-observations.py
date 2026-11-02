"""Capture real cumulative Payment/consumer/database observations after the G4 journey."""
import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import subprocess
import time
import urllib.parse
import urllib.request

ROOT=Path(__file__).resolve().parents[2]

def command(arguments):
    return subprocess.check_output(arguments,cwd=ROOT,text=True,encoding='utf-8').strip()

def sql(query):
    raw=command(['docker','compose','exec','-T','-e','PGOPTIONS=-c default_transaction_read_only=on',
                 'postgres','psql','-U','postgres','-d','payment_db','-v','ON_ERROR_STOP=1','-XAtc',
                 "SELECT coalesce(json_agg(x),'[]') FROM ("+query+") x"])
    return json.loads(raw)

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prometheus',default='http://127.0.0.1:9090')
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    project=os.environ.get('COMPOSE_PROJECT_NAME','')
    if not project.startswith(('ride-ci-','team-w13-')):
        raise SystemExit('A dedicated ride-ci-* or team-w13-* project is required.')
    queries={
        'payment_operations':'payment_operation_operations_total',
        'payment_consumer_deliveries':'consumer_delivery_messages_total{exported_job="payment-service"}',
        'payment_consumer_acknowledgements':'consumer_ack_messages_total{exported_job="payment-service"}',
        'payment_consumer_processing_count':'consumer_processing_duration_milliseconds_count{exported_job="payment-service"}',
        'payment_consumer_processing_sum_ms':'consumer_processing_duration_milliseconds_sum{exported_job="payment-service"}',
        'payment_consumer_dedup':'dedup_processed_messages_total{exported_job="payment-service"}',
        'payment_queue_consumers':'rabbitmq_detailed_queue_consumers{queue="payment.commands.q"}',
        'payment_queue_ready':'rabbitmq_detailed_queue_messages_ready{queue="payment.commands.q"}',
        'payment_queue_unacked':'rabbitmq_detailed_queue_messages_unacked{queue="payment.commands.q"}',
    }
    deadline=time.monotonic()+180
    observed={}
    while True:
        missing=[]
        for name,query in queries.items():
            url=args.prometheus.rstrip('/')+'/api/v1/query?'+urllib.parse.urlencode({'query':query})
            with urllib.request.urlopen(url,timeout=10) as response:
                data=json.load(response)
            values=data.get('data',{}).get('result',[])
            valid=data.get('status')=='success' and bool(values)
            if valid:
                try:
                    numbers=[float(series['value'][1]) for series in values]
                    valid=all(math.isfinite(value) and value>=0 for value in numbers)
                    if name not in ('payment_queue_ready','payment_queue_unacked'):
                        valid=valid and sum(numbers)>0
                    else:
                        valid=valid and sum(numbers)==0
                except (KeyError,TypeError,ValueError,IndexError):
                    valid=False
            observed[name]={'query':query,'result':values,'usable':valid}
            if not valid:
                missing.append(name)
        if not missing or time.monotonic()>=deadline:
            break
        time.sleep(5)
    operations=sql('SELECT operation_type,state,count(*) AS count FROM payment_operations GROUP BY operation_type,state ORDER BY operation_type,state')
    provider=sql('SELECT count(*) AS count FROM provider_effects')
    database=sql("SELECT datname,numbackends,xact_commit,xact_rollback,blks_read,blks_hit,deadlocks FROM pg_stat_database WHERE datname='payment_db'")
    tables=sql('SELECT relname,n_live_tup,n_dead_tup,seq_scan,idx_scan FROM pg_stat_user_tables ORDER BY relname')
    assert operations and database,'Payment owning-database observations must be nonempty'
    evidence={'schemaVersion':1,'capturedAtUtc':datetime.now(timezone.utc).isoformat(),'project':project,
              'source':{'commit':command(['git','rev-parse','HEAD']),'tree':command(['git','rev-parse','HEAD^{tree}'])},
              'scope':'Functional cross-owner observability support after real G4 journeys; cumulative metric/database/consumer snapshots, not a Payment throughput or latency benchmark',
              'metricPolicy':'Every declared series must exist, be finite and nonnegative; exercised counters and consumer count must be positive; finished queue must be empty. No missing series is converted to zero.',
              'queries':observed,'missingOrInvalid':missing,'paymentOperations':operations,'providerEffects':provider,
              'database':database,'tables':tables,'verdict':'PASS' if not missing else 'FAIL'}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(evidence,indent=2)+'\n',encoding='utf-8')
    if missing:
        raise SystemExit('Missing/invalid exercised runtime observations: '+', '.join(missing))
    print('PASS: observed exercised Payment/consumer counters, empty live queue and read-only owning PostgreSQL statistics.')

if __name__=='__main__':
    main()
