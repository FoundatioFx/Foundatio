#!/usr/bin/env bash
# SQS on Floci (docker/compose.yml, profile sqs, on 26400). Each consumer reaches it through its own proxy (sqs-c1 on
# 26401, sqs-c2 on 26402), so one consumer can be cut off while the other still works: c1's connection stalls for
# 40 s, longer than the 15 s lease, while it is running handlers slower than the lease. Its messages become visible
# again and c2 receives them. Usage: sqs-partition.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-150}"
endpoint() { echo "ServiceUrl=http://127.0.0.1:$1;AccessKey=AKIDEXAMPLE;SecretKey=wJalrXUtnFEMI;Region=us-east-1"; }
tox_reset
start c1 consume --connection "$(endpoint 26401)"
start c2 consume --connection "$(endpoint 26402)"
sleep 10
start p1 publish --connection "$(endpoint 26400)" --duration "$duration" --slow-every 400 --slow-ms 25000
sleep 40
note "fault: c1 cut off from SQS for 40 s (every connection stalls)"
tox_add sqs-c1 stall-up timeout upstream '{"timeout":0}'
tox_add sqs-c1 stall-down timeout downstream '{"timeout":0}'
sleep 40
note "heal: c1 reconnected"
tox_reset
wait_exit p1
note "publisher finished"
verify --connection "$(endpoint 26400)" --wait 600
stop c1; stop c2
