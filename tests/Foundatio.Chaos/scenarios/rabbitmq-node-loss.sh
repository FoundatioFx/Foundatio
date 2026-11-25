#!/usr/bin/env bash
# RabbitMQ 3-node cluster: lose the first host (RabbitMQ.Client shuffles Hosts, so some clients are on it), then a
# second, so the cluster loses its quorum while delayed messages wait in the delay levels, then bring both back.
# RABBIT_NODES names the three containers in Hosts order. Usage: rabbitmq-node-loss.sh [DURATION]
source "$(dirname "$0")/lib.sh"
read -r -a nodes <<< "${RABBIT_NODES:?set RABBIT_NODES to the three node containers}"
duration="${1:-240}"
rabbit_proxies 2>/dev/null || true
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration" --delayed-rate 20 --delay-min-ms 5000 --delay-max-ms 60000
sleep 40
note "fault: kill ${nodes[0]} (the first host in Hosts)"
docker kill "${nodes[0]}" > /dev/null
sleep 40
note "fault: kill ${nodes[1]} (quorum lost: one of three nodes left)"
docker kill "${nodes[1]}" > /dev/null
sleep 40
note "heal: start ${nodes[1]} (quorum back)"
rabbit_start "${nodes[1]}"
sleep 30
note "heal: start ${nodes[0]}"
rabbit_start "${nodes[0]}"
wait_exit p1
note "publisher finished"
verify --wait 600
stop c1; stop c2
