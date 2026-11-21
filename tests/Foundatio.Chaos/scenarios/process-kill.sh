#!/usr/bin/env bash
# Any transport: SIGKILL consumers in the middle of handlers (some of them slower than the lease) and restart them,
# and SIGKILL the publisher mid-batch and start another. Usage: process-kill.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-200}"
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration" --slow-every 300 --slow-ms 25000
for i in 1 2 3 4 5 6; do
  sleep 20
  victim="c$(( (i % 2) + 1 ))"
  note "fault: SIGKILL $victim (cycle $i)"
  stop "$victim" KILL
  if [ "$i" = 3 ]; then
    note "fault: SIGKILL p1 mid-batch"
    stop p1 KILL
    start p2 publish --duration $(( duration - 70 )) --slow-every 300 --slow-ms 25000
  fi
  sleep 5
  note "heal: restart $victim (cycle $i)"
  start "$victim" consume
done
wait_exit p2
note "publisher finished"
verify --wait 600
stop c1; stop c2
