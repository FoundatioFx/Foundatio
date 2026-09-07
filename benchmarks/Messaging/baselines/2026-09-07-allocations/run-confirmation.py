import json, pathlib, subprocess, sys
root = pathlib.Path('/tmp/foundatio-allocations')
profiles = [
    ('memory-repeat', ['--transport', 'memory', '--variants', 'before,after', '--workloads', 'queue', '--seconds', '30', '--warmup', '5', '--repetitions', '5']),
    ('redis-repeat', ['--transport', 'redis', '--variants', 'before,after', '--workloads', 'fanout', '--payload', '16384', '--seconds', '20', '--warmup', '5', '--repetitions', '3']),
    ('aws-final-1024', ['--variants', 'after', '--repetitions', '1']),
    ('aws-final-16384', ['--variants', 'after', '--payload', '16384', '--repetitions', '1']),
]
(root / 'confirmation-profiles.json').write_text(json.dumps(profiles, indent=2))
for name, args in profiles:
    print('PROFILE', name, flush=True)
    result = subprocess.run([sys.executable, str(root / 'compare-final.py'), '--dotnet', '/tmp/foundatio-fastest/official-dotnet/dotnet', '--output', str(root / name), '--seconds', '15', *args])
    if result.returncode: sys.exit(result.returncode)
