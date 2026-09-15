import json, pathlib, subprocess, sys
root = pathlib.Path('/tmp/foundatio-allocations')
profiles = [
    ('aws-1024', ['--payload', '1024']),
    ('aws-16384', ['--payload', '16384']),
    ('memory', ['--transport', 'memory', '--variants', 'before,after']),
    ('redis-16384', ['--transport', 'redis', '--variants', 'before,after', '--payload', '16384', '--repetitions', '1']),
    ('aws-soak', ['--variants', 'after,masstransit', '--seconds', '120', '--warmup', '5', '--repetitions', '1', '--payload', '16384']),
]
(root / 'profiles.json').write_text(json.dumps(profiles, indent=2))
for name, args in profiles:
    print('PROFILE', name, flush=True)
    result = subprocess.run([sys.executable, str(root / 'compare.py'), '--dotnet', '/tmp/foundatio-fastest/official-dotnet/dotnet', '--output', str(root / name), '--seconds', '15', *args])
    if result.returncode: sys.exit(result.returncode)
