"""Recalculate retained measurements; never transfer acceptance to another tree.

W13 original engine streams remain local-only. Complete curated samples and
owning-state snapshots are checked here, including their committed digests.
W14 strict validation is reused without bypassing its artifact or state checks.
"""
import csv,json,math,statistics,hashlib,subprocess,sys
from pathlib import Path
from collections import Counter,defaultdict
from datetime import datetime
R=Path(__file__).resolve().parents[2]; B=R/'evidence/team/w13-integration'; C=R/'evidence/team/w14-integration'
import argparse
parser=argparse.ArgumentParser(description='Recalculate historical accepted Driver/Matching samples without relabeling their source.')
parser.add_argument('--output', type=Path, default=R/'plans/reports/claims-validation.json')
args=parser.parse_args()
if not __debug__:
 raise SystemExit('Run without Python optimization: assertions are evidence checks')
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def rows(p): return list(csv.DictReader(p.open(encoding='utf-8-sig')))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def dt(s): return datetime.fromisoformat(s.replace('Z','+00:00'))
def q(v,p,linear=False):
 v=sorted(map(float,v)); pos=(len(v)-1)*p
 return v[math.floor(pos)]+(v[math.ceil(pos)]-v[math.floor(pos)])*(pos-math.floor(pos)) if linear else v[max(0,math.ceil(len(v)*p)-1)]
def check(a,b): assert math.isclose(a,b,rel_tol=1e-9,abs_tol=1e-5),(a,b)
res={'digests':{},'source':[],'E01':[],'E02':[],'E03':[],'E04':[]}
prov=read(B/'data-provenance.json')
for label,base,files in [('W13 curated',B,prov['curatedFiles']),('W14 root',C,read(C/'artifact-manifest.json')['files']),('W14 E04',R,read(C/'e04/artifact-manifest.json')['files'])]:
 missing=[]; bad=[]
 for f in files:
  p=base/f['path']
  if not p.exists(): missing.append(str(p))
  elif sha(p)!=f['sha256']: bad.append(str(p))
 res['digests'][label]={'count':len(files),'missing':missing,'bad':bad}
 assert not missing and not bad, (label, missing, bad)
res['digests']['W13 original streams']={'count':len(prov['rawFiles']),'available':sum((R/f['path']).exists() for f in prov['rawFiles'])}
M=[(p,read(p)) for p in sorted((B/'runs').glob('*/*/manifest.json'))]
groups=defaultdict(list)
for path,m in M:
 source=m['source']; assert subprocess.check_output(['git','rev-parse',source['commit']+'^{tree}'],cwd=R,text=True).strip()==source['tree']
 rid=m['runId']; exp=m['experiment']; a=m['workload']['achieved']; cfg=m['workload']['configured']; run=path.parent
 if exp=='E01':
  rr=rows(B/'samples'/f'{rid}.csv')
  for n in [1,10,25,50,100]:
   ss=[r for r in rr if int(r['n'])==n]; out=next(x for x in a['runs'] if x['n']==n)
   assert len(ss)==n and Counter(r['outcome'] for r in ss)==Counter(success=1,domainConflict=n-1)
   p50=q([r['latencyMs'] for r in ss],.5); p95=q([r['latencyMs'] for r in ss],.95)
   check(p50,out['p50Ms']);check(p95,out['p95Ms'])
   final=read(run/'final-state'/f'e01-n{n}.json');assert final['final']['driver']['status']=='RESERVED' and final['afterExactRelease']['driver']['status']=='AVAILABLE' and not final['afterExactRelease']['activeReservationRows']
   groups[(exp,n)].append({'n':len(ss),'p50':p50,'p95':p95})
 elif exp=='E02':
  ss=rows(B/'samples'/f'{rid}.csv'); counts=Counter(r['outcome'] for r in ss)
  for kind,key in [('success','successCount'),('domainConflict','domainConflictCount'),('authFailure','authFailureCount'),('systemError','systemErrorCount'),('unexpected','unexpectedCount')]:assert counts[kind]==a[key]
  p50=q([r['latencyMs'] for r in ss],.5,True);p95=q([r['latencyMs'] for r in ss],.95,True)
  check(p50,a['p50Ms']);check(p95,a['p95Ms'])
  outcome_quantiles={kind:{'n':counts[kind], 'p50':q([r['latencyMs'] for r in ss if r['outcome']==kind],.5,True), 'p95':q([r['latencyMs'] for r in ss if r['outcome']==kind],.95,True)} for kind in ('success','domainConflict')}
  groups[(exp,cfg['configuredRatePerSecond'])].append({'n':len(ss),'success':counts['success'],'conflict':counts['domainConflict'],'p50':p50,'p95':p95,'rate':a['achievedRatePerSecond'],'dropped':a['droppedIterations'],'outcomeQuantiles':outcome_quantiles})
 elif exp=='E03':
  ss=rows(B/'samples'/f'{rid}-intakes.csv'); dd=rows(B/'samples'/f'{rid}-deliveries.csv'); qq=rows(B/'broker'/f'{rid}-queue.csv')
  final=read(run/'final-state/e03-matching-state.json'); authority={x['input_message_id']:x for x in final['rounds']}
  assert len(ss)==len(authority)==len(final['rounds'])==400 and len(dd)==440 and len(set(r['messageId'] for r in dd))==400
  latency=[]
  for r in ss:
   x=authority[r['messageId']]; assert x['trip_id']==r['tripId']
   v=(dt(x['created_at'])-dt(r['publishedAtUtc'])).total_seconds()*1000;check(v,float(r['storedCreationProxyMs']));latency.append(v)
  p50=q(latency,.5);p95=q(latency,.95);check(p50,a['p50IntakeLatencyMs']);check(p95,a['p95IntakeLatencyMs'])
  ack=int(final['queueAfter']['message_stats']['ack'])-int(qq[0]['ack']); assert ack==440
  elapsed=a['durationSeconds']+a['drainSeconds']+a['brokerAckStatsWaitSeconds']; check(elapsed,a['brokerAckObservationSeconds']);rate=ack/elapsed;check(rate,a['observedAcknowledgementRatePerSecond'])
  groups[(exp,cfg['replicas'])].append({'n':400,'deliveries':440,'ack':ack,'p50':p50,'p95':p95,'observedAckRate':rate,'elapsed':elapsed,'ackFirst':int(qq[0]['ack']),'ackLast':int(qq[-1]['ack'])})
for (e,k),runs in groups.items():
 row={'point':k,'runs':len(runs),'samples':sum(r['n'] for r in runs),'p50':statistics.median(r['p50'] for r in runs),'p95':statistics.median(r['p95'] for r in runs),'p95Range':[min(r['p95'] for r in runs),max(r['p95'] for r in runs)],'individual':runs}
 for key in ('success','conflict','deliveries','ack'): 
  if key in runs[0]:row[key]=sum(r[key] for r in runs)
 for key in ('rate','observedAckRate'):
  if key in runs[0]: row[key]={'median':statistics.median(r[key] for r in runs),'range':[min(r[key] for r in runs),max(r[key] for r in runs)]}
 res[e].append(row)
sys.path.insert(0,str(R/'scripts/perf'));import w14_analyze as w14
accepted=read(C/'e04/analysis.json')
for i,p in enumerate(sorted((C/'e04').glob('W14-*/manifest.json'))):
 m=read(p); s=read(p.parent/'samples.json');r=read(p.parent/'receipts.json');f=read(p.parent/'final-state.json')
 start=m['interruption']['start'];end=m['interruption']['end'];steady=m['steadyElapsed'];rec=[x for x in s if x['phase']=='recovery' and x['elapsed']<=steady];duration=end-start
 lam=sum(start<=x['sentElapsed']<end for x in r)/duration
 mu=(rec[-1]['terminal']-rec[0]['terminal'])/(rec[-1]['elapsed']-rec[0]['elapsed'])
 vals={'lambdaOutage':lam,'T':duration,'mu':mu,'observedSteadySeconds':steady-end,'peakReady':max(x['ready'] for x in s),'peakOutstandingBusinessWork':max(max(0,sum(x['elapsed']<=z['elapsed'] for x in r)-z['terminal']) for z in s),'confirmedPublications':len(r),'finalAck':s[-1]['ack'],'finalQuiescenceSeconds':s[-1]['elapsed']-end,'confirmP50':q([x['latencyMs'] for x in r],.5),'confirmP95':q([x['latencyMs'] for x in r],.95)}
 for key in ('lambdaOutage','T','mu','observedSteadySeconds','peakReady','peakOutstandingBusinessWork','confirmedPublications'): check(vals[key],accepted['runs'][i][key])
 assert len(f['rounds'])==len(f['matchingOutbox'])==len(r) and {x['input_message_id'] for x in f['rounds']}=={x['messageId'] for x in r} and len(set(x['messageId'] for x in r))==len(r)
 assert all(x['status']=='NO_DRIVER' and x['auto_execute'] and x['selected_driver_id'] is None for x in f['rounds']) and all(x['status']=='PUBLISHED' for x in f['matchingOutbox'])
 w14.validate_run(p.parent)
 vals['strictValidator']='PASS'
 res['E04'].append({'runId':m['runId'],**vals})
for source,tree in [(prov['sourceCommit'],prov['sourceTree']),(read(C/'g5-summary.json')['measurementSource'],read(C/'g5-summary.json')['measurementTree'])]:
 actual=subprocess.check_output(['git','rev-parse',source+'^{tree}'],cwd=R,text=True).strip();assert actual==tree
 drift=subprocess.check_output(['git','diff','--name-only',source,'HEAD','--','.',':(exclude)evidence'],cwd=R,text=True).splitlines()
 res['source'].append({'commit':source,'tree':tree,'matches':True,'nonEvidenceDriftToCurrent':drift})
args.output.parent.mkdir(parents=True,exist_ok=True)
args.output.write_text(json.dumps(res,indent=2)+'\n',encoding='utf-8')
print('PASS: accepted sample calculations and 174 digest checks (12 overlap); historical source scope retained')

