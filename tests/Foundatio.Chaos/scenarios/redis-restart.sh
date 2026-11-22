#!/usr/bin/env bash
# Redis cluster without persistence (start it with CHAOS_REDIS_AOF=no CHAOS_REDIS_RDB_DISABLED=yes): restart every
# node at once mid-run, so Redis loses everything it held. Messages sent before the restart may be lost; the run fails
# only if messages sent after it are lost, or if the processes don't recover by themselves. Usage: redis-restart.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-150}"
project="${CHAOS_COMPOSE_PROJECT:-pr601-chaos}"
port="${CHAOS_REDIS_BASE_PORT:-7700}"
tox_reset
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration" --delay-max-ms 60000
sleep 45
note "fault: docker restart of all six redis nodes (no persistence: every key is lost)"
docker kill $(for i in 0 1 2 3 4 5; do echo "$project-redis-$i-1"; done) > /dev/null
docker start $(for i in 0 1 2 3 4 5; do echo "$project-redis-$i-1"; done) > /dev/null
until docker exec "$project-redis-5-1" redis-cli -p $((port + 5)) cluster info 2>/dev/null | grep -q cluster_state:ok; do sleep 1; done
restored="$(now_ms)"
note "heal: cluster_state ok again; keys per node: $(for i in 0 1 2 3 4 5; do docker exec "$project-redis-$i-1" redis-cli -p $((port + i)) dbsize; done | paste -sd" ")"
wait_exit p1
note "publisher finished"
verify --wait 600 --expected-loss-until-ms "$restored"
stop c1; stop c2
