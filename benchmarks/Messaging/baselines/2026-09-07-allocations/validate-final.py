import hashlib, json, pathlib
root = pathlib.Path('/tmp/foundatio-allocations')
expected = {'aws-1024': 18, 'aws-16384': 18, 'memory': 12, 'redis-16384': 4, 'aws-soak': 4, 'memory-repeat': 10, 'redis-repeat': 6, 'aws-final-1024': 2, 'aws-final-16384': 2}
runs = []
for profile, count in expected.items():
    files = sorted((root / profile).glob('*/*.json'))
    assert len(files) == count, (profile, len(files), count)
    assert not list((root / profile).glob('*/*-failure.txt')), profile
    for file in files:
        result = json.loads(file.read_text())
        measurement = result['Measurement']
        options = result['Options']
        environment = result['Environment']
        assert result['Success'] and result['Error'] is None, file
        assert not any(measurement[k] for k in ('Missing', 'Duplicates', 'Invalid', 'HitTrackingLimit')), file
        assert measurement['Error'] is None and measurement['Inputs'] > 0, file
        copies = options['Subscribers'] if options['Scenario'] == 'pubsub' else 1
        assert measurement['Deliveries'] == copies * measurement['Inputs'], file
        assert options['MaxMessages'] == 20_000_000 and options['MaxOutstanding'] == 1024, file
        if options['Transport'] == 'sqs':
            assert environment['AwsMode'] == 'localstack' and environment['AwsRegion'] == 'us-east-1', file
        runs.append((file, result))
for key in ('Runtime', 'CoreClrSha256', 'MassTransit', 'SqsSdk', 'SnsSdk'):
    assert len({r['Environment'][key] for _, r in runs}) == 1, key
assert len({r['ResourcePrefix'] for _, r in runs}) == len(runs)
manifest = json.loads((root / 'candidate-binaries.json').read_text())
for name, digest in manifest.items():
    assert hashlib.sha256((root / '0b3dfdc8-binaries' / name).read_bytes()).hexdigest() == digest, name
final_manifest = json.loads((root / 'final-binaries.json').read_text())
for name, digest in final_manifest.items():
    assert hashlib.sha256((root / 'e677cf9a-binaries' / name).read_bytes()).hexdigest() == digest, name
for name in ('before-1024', 'before-16384', 'masstransit-16384', 'after-1024', 'after-16384'):
    trace = json.loads((root / (name + '.nettrace.allocations.json')).read_text())
    assert trace['EventsLost'] == 0 and trace['SamplesWithoutStacks'] == 0 and trace['Samples'] > 0, name
    run = json.loads((root / (name + '.json')).read_text())
    assert run['Success'] and all(run['Measurement'][k] == 0 for k in ('Missing','Duplicates','Invalid')), name
result = {
    'Trials': len(runs), 'Profiles': expected,
    'Inputs': sum(r['Measurement']['Inputs'] for _, r in runs),
    'Deliveries': sum(r['Measurement']['Deliveries'] for _, r in runs),
    'Missing': 0, 'Duplicates': 0, 'Invalid': 0, 'WorkerFailures': 0,
    'UniquePrefixes': len(runs), 'CandidateBinaryFilesVerified': len(manifest), 'FinalBinaryFilesVerified': len(final_manifest), 'ValidDiagnosticCaptures': 5,
    'Runtime': runs[0][1]['Environment']['Runtime'],
    'CoreClrSha256': runs[0][1]['Environment']['CoreClrSha256'],
}
(root / 'final-validation.json').write_text(json.dumps(result, indent=2))
print(json.dumps(result, indent=2))
