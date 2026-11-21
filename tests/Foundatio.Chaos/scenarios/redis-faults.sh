#!/usr/bin/env bash
# Redis cluster (docker/compose.yml, profile redis): kill a primary (its replica takes over), fail one over
# gracefully, reset every connection, then add latency longer than the lease (15 s) for 25 s.
# Usage: redis-faults.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-240}"
project="${CHAOS_COMPOSE_PROJECT:-pr601-chaos}"
cli() { docker exec "$project-redis-$1-1" redis-cli -p $((7700 + $1)) "${@:2}"; }
primary_of_slot() { for i in 0 1 2 3 4 5; do cli "$i" cluster nodes 2>/dev/null | awk -v s="$1" '$3 ~ /myself/ && $3 ~ /master/ { for (f = 9; f <= NF; f++) { split($f, r, "-"); if (s >= r[1] && s <= r[2]) { print 1; exit } } }' | grep -q 1 && { echo "$i"; return; }; done; }
tox_reset
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration" --slow-every 400 --slow-ms 20000
sleep 30
# The work queue's keys share the hash tag {prefix:messaging...}; kill the primary that owns them.
slot="$(cli 0 cluster keyslot "{$CHAOS_PREFIX:messaging:queue:chaos.work}" 2>/dev/null || echo 0)"
victim="$(primary_of_slot "$slot")"; victim="${victim:-0}"
note "fault: docker kill redis-$victim (primary of slot $slot)"
docker kill "$project-redis-$victim-1" > /dev/null
sleep 30
note "heal: start redis-$victim (rejoins as a replica)"
docker start "$project-redis-$victim-1" > /dev/null
sleep 20
replica="$(for i in 0 1 2 3 4 5; do cli "$i" role 2>/dev/null | head -1 | grep -q slave && cli "$i" cluster nodes | grep -q "myself,slave" && echo "$i"; done | head -1)"
note "fault: graceful CLUSTER FAILOVER on redis-$replica"
cli "$replica" cluster failover > /dev/null
sleep 20
note "fault: all redis proxies down for 3 s (every connection reset)"
for i in 0 1 2 3 4 5; do tox_down "redis-$i"; done
sleep 3
for i in 0 1 2 3 4 5; do tox_up "redis-$i"; done
note "heal: redis proxies up"
sleep 25
note "fault: 20 s latency on every redis connection for 25 s (beyond the 15 s lease)"
for i in 0 1 2 3 4 5; do tox_add "redis-$i" slow latency downstream '{"latency":20000,"jitter":0}'; done
sleep 25
tox_reset
note "heal: latency removed"
wait_exit p1
note "publisher finished"
verify --wait 600
stop c1; stop c2
