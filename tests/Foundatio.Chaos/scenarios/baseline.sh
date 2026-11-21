#!/usr/bin/env bash
# No faults: two consumers and a publisher for DURATION seconds, then verify. Usage: baseline.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-60}"
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration"
wait_exit p1
verify --wait 300
stop c1; stop c2
