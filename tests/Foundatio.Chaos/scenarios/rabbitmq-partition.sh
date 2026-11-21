#!/usr/bin/env bash
# RabbitMQ: cut the leader of the work queue off from the other two nodes for 90 s (longer than net_ticktime, so the
# cluster notices) while clients stay connected to it, then freeze a node with docker pause for 40 s (a hung node
# that still accepts TCP connections). Usage: rabbitmq-partition.sh [DURATION]
source "$(dirname "$0")/lib.sh"
read -r -a nodes <<< "${RABBIT_NODES:?set RABBIT_NODES}"
duration="${1:-260}"
leader_of() {
  for n in "${nodes[@]}"; do
    docker exec "$n" rabbitmqctl list_queues name leader --quiet 2>/dev/null | awk -v q="$1" '$1 == q { sub("rabbit@chaos", "", $2); print $2 }' | grep . && return
  done
}
rabbit_proxies
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration" --delayed-rate 20 --delay-min-ms 2000 --delay-max-ms 30000
sleep 30
leader="$(leader_of "$CHAOS_PREFIX.queue.chaos.work")"; leader="${leader:-1}"
victim="${nodes[$((leader - 1))]}"
others=(); for n in "${nodes[@]}"; do [ "$n" != "$victim" ] && others+=("$n"); done
note "fault: partition $victim (leader of $CHAOS_PREFIX.queue.chaos.work) from ${others[*]}"
partition "$victim" "${others[@]}"
sleep 90
note "heal: partition removed"
heal_partition "$victim"
sleep 40
paused="${others[0]}"
note "fault: docker pause $paused (hung node, still accepts TCP)"
docker pause "$paused" > /dev/null
sleep 40
note "heal: docker unpause $paused"
docker unpause "$paused" > /dev/null
wait_exit p1
note "publisher finished"
verify --wait 600
stop c1; stop c2
