#!/usr/bin/env bash
# RabbitMQ: kill the node leading the work queue while it is busy and restart it, then stop the node leading the
# grouped queue gracefully (docker stop) for 40 s. RABBIT_NODES as in rabbitmq-node-loss.sh. For a partition see
# rabbitmq-partition.sh (docker network disconnect doesn't work under Aspire, which reattaches the container at once).
# Usage: rabbitmq-leader-loss.sh [DURATION]
source "$(dirname "$0")/lib.sh"
read -r -a nodes <<< "${RABBIT_NODES:?set RABBIT_NODES}"
duration="${1:-200}"
leader_of() {
  for n in "${nodes[@]}"; do
    docker exec "$n" rabbitmqctl list_queues name leader --quiet 2>/dev/null | awk -v q="$1" '$1 == q { sub("rabbit@chaos", "", $2); print $2 }' | grep . && return
  done
}
rabbit_proxies 2>/dev/null || true
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration" --delayed-rate 20 --delay-min-ms 2000 --delay-max-ms 30000
sleep 30
leader="$(leader_of "$CHAOS_PREFIX.queue.chaos.work")"
victim="${nodes[$((leader - 1))]}"
note "fault: kill $victim, leader of $CHAOS_PREFIX.queue.chaos.work"
docker kill "$victim" > /dev/null
sleep 30
note "heal: start $victim"
rabbit_start "$victim"
sleep 40
leader="$(leader_of "$CHAOS_PREFIX.queue.chaos.grouped")"
victim="${nodes[$((leader - 1))]}"
note "fault: docker stop $victim (graceful shutdown of the leader of $CHAOS_PREFIX.queue.chaos.grouped)"
docker stop "$victim" > /dev/null
sleep 40
note "heal: start $victim"
rabbit_start "$victim"
wait_exit p1
note "publisher finished"
verify --wait 600
stop c1; stop c2
