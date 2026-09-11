import argparse, collections, datetime, json, os, pathlib, random, subprocess, time, hashlib
p=argparse.ArgumentParser()
p.add_argument('--output', required=True)
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--transport', default='sqs')
p.add_argument('--concurrency', type=int, default=0)
p.add_argument('--producer-count', type=int, default=0)
p.add_argument('--window', type=int, default=1024)
p.add_argument('--max-messages', type=int, default=20000000)
p.add_argument('--seconds', type=int, default=10)
p.add_argument('--warmup', type=int, default=3)
p.add_argument('--repetitions', type=int, default=3)
p.add_argument('--variants', default='before,after,masstransit')
p.add_argument('--workloads', default='queue,fanout')
p.add_argument('--rate', type=int, default=0)
p.add_argument('--payload', type=int, default=1024)
p.add_argument('--batch', type=int, default=1)
a=p.parse_args()
root=pathlib.Path(a.output)
root.mkdir(parents=True, exist_ok=False)
base=pathlib.Path('/tmp/foundatio-fastest')
paths={'before':base/'77c20ea3-binaries/Foundatio.Messaging.Benchmarks.dll','after':pathlib.Path('/tmp/foundatio-allocations/0b3dfdc8-binaries/Foundatio.Messaging.Benchmarks.dll'),'masstransit':pathlib.Path('/tmp/foundatio-allocations/0b3dfdc8-binaries/Foundatio.Messaging.Benchmarks.dll')}
paths['delay1']=base/'coherent-binaries/Foundatio.Messaging.Benchmarks.dll'
paths['pipeline']=base/'7bd7c4f8-binaries/Foundatio.Messaging.Benchmarks.dll'
paths['previous']=base/'d07031ff-binaries/Foundatio.Messaging.Benchmarks.dll'
env=dict(os.environ, PERF_AWS_MODE='localstack', PERF_AWS_URL='http://localhost:24566', PERF_AWS_REGION='us-east-1')
crash_capture={}
if a.transport == 'redis' and a.seconds >= 120:
    crash_capture={'DOTNET_DbgEnableMiniDump':'1','DOTNET_DbgMiniDumpType':'4','DOTNET_DbgMiniDumpName':'/tmp/foundatio-fastest/confirmed-crashes/%e_%p_%t.dmp','DOTNET_EnableCrashReport':'1'}
    env.update(crash_capture)
cases=[(r,v,w) for r in range(1,a.repetitions+1) for v in a.variants.split(',') for w in a.workloads.split(',')]
random.Random(534).shuffle(cases)
metadata={'before_revision':'77c20ea354919fd25ae300e49c5de7f3ed8da598','after_revision':subprocess.check_output(['git','-C','/tmp/foundatio-pr-533-review','rev-parse','HEAD'],text=True).strip(),'started_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'binaries':{v:{f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in paths[v].parent.glob('*.dll')} for v in a.variants.split(',')}}
metadata['crash_capture']=crash_capture
(root/'binary-manifest.json').write_text(json.dumps(metadata,indent=2))
(root/'run-options.json').write_text(json.dumps(vars(a),indent=2))
(root/'source.patch').write_text(subprocess.check_output(['git','-C','/tmp/foundatio-pr-533-review','diff','HEAD'],text=True))
(root/'compare.py').write_text(pathlib.Path(__file__).read_text())
failed=0
for index,(r,v,w) in enumerate(cases):
    target=root/v
    target.mkdir(exist_ok=True)
    name=f'round{r}-{w}'
    output=target/(name+'.json')
    scenario='pubsub' if w in ('fanout','pubsub-one') else 'queue'
    consumers=a.concurrency or (1 if w=='serial' else 8 if w=='fanout' else 32)
    producers=a.producer_count or (1 if w=='serial' else 8 if a.batch>1 else 32)
    args=[a.dotnet,str(paths[v]),'--engine','masstransit' if v=='masstransit' else 'foundatio','--transport',a.transport,'--scenario',scenario,'--seconds',str(a.seconds),'--warmup',str(a.warmup),'--producers',str(producers),'--consumers',str(consumers),'--prefetch',str(consumers),'--subscribers','4' if w=='fanout' else '1','--outstanding',str(a.window),'--max-messages',str(a.max_messages),'--payload',str(a.payload),'--batch',str(a.batch),'--rate',str(a.rate),'--output',str(output)]
    started=datetime.datetime.now(datetime.timezone.utc).isoformat()
    print(f'[{index+1}/{len(cases)}] {v} {w} round {r}',flush=True)
    with (target/(name+'.log')).open('w') as log:
        run=subprocess.run(args,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=a.seconds+300)
    log=subprocess.run(['docker','logs','--since',started,'foundatio-messaging-perf-localstack-1'],capture_output=True,text=True,check=True)
    counts=collections.Counter('.'.join(k) for k in __import__('re').findall(r'AWS (sqs|sns)\.(\w+) =>',log.stdout+log.stderr))
    (target/(name+'-requests.txt')).write_text(json.dumps(dict(counts),indent=2))
    if output.exists():
        result=json.loads(output.read_text())
        m=result.get('Measurement') or {}
        print(f"  success={result['Success']} inputs/s={m.get('InputsPerSecond',0):.0f} p99={m.get('DeliveryLatency',{}).get('P99Milliseconds',0):.2f} ms",flush=True)
    if run.returncode or not output.exists() or not result['Success']:
        failed+=1
        failure={'exit_code':run.returncode,'result_exists':output.exists(),'started_utc':started,'command':args}
        (target/(name+'-failure.txt')).write_text(json.dumps(failure,indent=2))
        print('  FAILED, log retained',flush=True)
(root/'run-options.json').write_text(json.dumps(vars(a),indent=2))
raise SystemExit(1 if failed else 0)
