#!/usr/bin/env bash
# Any transport: c2's clock (the TimeProvider Foundatio and the transport use in that process) jumps 2 minutes
# forward, then 1 minute behind real time, then back; c1 keeps real time. The publisher's clock runs 30 s ahead
# throughout. Usage: clock-jump.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-180}"
echo 0 > "$RUN/c2.clock"
echo 30000 > "$RUN/p1.clock"
start c1 consume
start c2 consume --clock-file "$RUN/c2.clock"
sleep 8
start p1 publish --duration "$duration" --clock-file "$RUN/p1.clock" --slow-every 400 --slow-ms 20000
sleep 40
note "fault: c2 clock +120 s"
echo 120000 > "$RUN/c2.clock"
sleep 40
note "fault: c2 clock -60 s"
echo -60000 > "$RUN/c2.clock"
sleep 40
note "heal: c2 clock back to real time"
echo 0 > "$RUN/c2.clock"
wait_exit p1
note "publisher finished"
verify --wait 600
stop c1; stop c2
