import collections,csv,json,pathlib,statistics,sys
base=pathlib.Path('/tmp/foundatio-allocations')
paths=[base/n for n in sys.argv[1:]] if len(sys.argv)>1 else sorted(base.glob('confirmed-*'))
rows=[]
for root in paths:
 if not root.is_dir(): continue
 groups=collections.defaultdict(list)
 for p in root.glob('*/*.json'):
  r=json.loads(p.read_text())
  if 'Measurement' not in r: continue
  o=r['Options']; m=r.get('Measurement')
  name=p.stem.partition('-')[2]
  groups[(p.parent.name,name)].append((r,m))
 for (variant,name),runs in sorted(groups.items()):
  ms=[m for r,m in runs if r['Success'] and m]
  if len(ms)!=len(runs): print('FAILED',root.name,variant,name,file=sys.stderr)
  if not ms: continue
  median=lambda f:statistics.median(f(m) for m in ms)
  rates=[m['InputsPerSecond'] for m in ms]
  row={'Profile':root.name,'Variant':variant,'Workload':name,'Trials':len(ms),'Inputs':sum(m['Inputs'] for m in ms),'Deliveries':sum(m['Deliveries'] for m in ms),'InputsPerSecond':statistics.median(rates),'Minimum':min(rates),'Maximum':max(rates),'P50Milliseconds':median(lambda m:m['DeliveryLatency']['P50Milliseconds']),'P99Milliseconds':median(lambda m:m['DeliveryLatency']['P99Milliseconds']),'AllocatedBytesPerInput':median(lambda m:m['AllocatedBytesPerInput']),'MinimumAllocatedBytesPerInput':min(m['AllocatedBytesPerInput'] for m in ms),'MaximumAllocatedBytesPerInput':max(m['AllocatedBytesPerInput'] for m in ms),'CpuMillisecondsPerInput':median(lambda m:m['CpuMilliseconds']/m['Inputs']),'GcPauseMillisecondsPerThousandInputs':median(lambda m:m['GcPauseMilliseconds']*1000/m['Inputs']),'Gen0CollectionsPerMillionInputs':median(lambda m:m['Collections'][0]*1000000/m['Inputs']),'PeakWorkingSetMiB':median(lambda m:m['PeakWorkingSetBytes']/1024**2),'Duplicates':sum(m['Duplicates'] for m in ms),'Missing':sum(m['Missing'] for m in ms),'Invalid':sum(m['Invalid'] for m in ms)}
  rows.append(row)
  print(f"{root.name:24} {variant:12} {name:11} n={len(ms)} rate={row['InputsPerSecond']:,.0f} ({min(rates):,.0f}-{max(rates):,.0f}) p99={row['P99Milliseconds']:,.2f} alloc={row['AllocatedBytesPerInput']:,.0f}")
if rows:
 with (base/'summary.csv').open('w') as stream:
  writer=csv.DictWriter(stream,fieldnames=list(rows[0]),lineterminator="\n"); writer.writeheader();writer.writerows(rows)
 (base/'summary.json').write_text(json.dumps(rows,indent=2))
