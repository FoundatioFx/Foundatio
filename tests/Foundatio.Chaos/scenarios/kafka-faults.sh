#!/usr/bin/env bash
# Kafka, three KRaft brokers (docker/compose.yml, profile kafka) reached through toxiproxy: restart a broker, kill
# another (partition leaders move) and bring it back with a preferred leader election, SIGKILL a consumer mid-batch,
# then scale out and in under load (rebalances). Set CHAOS_KAFKA_PROTOCOL=consumer for KIP-848.
# Usage: kafka-faults.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-240}"
project="${CHAOS_COMPOSE_PROJECT:-pr601-chaos}"
tox_reset
start c1 consume
start c2 consume
sleep 15
start p1 publish --duration "$duration"
sleep 30
note "fault: docker restart kafka-2 (graceful broker restart)"
docker restart "$project-kafka-2-1" > /dev/null
note "heal: kafka-2 restarted"
sleep 30
note "fault: docker kill kafka-1 (partition leaders move)"
docker kill "$project-kafka-1-1" > /dev/null
sleep 20
note "heal: start kafka-1"
docker start "$project-kafka-1-1" > /dev/null
sleep 15
docker exec "$project-kafka-3-1" /opt/kafka/bin/kafka-leader-election.sh --bootstrap-server kafka-3:29092 --election-type preferred --all-topic-partitions > /dev/null 2>&1
note "fault: preferred leader election (leaders move back to kafka-1)"
sleep 15
note "fault: SIGKILL c1 mid-batch"
stop c1 KILL
sleep 3
start c1 consume
note "heal: c1 restarted"
sleep 25
note "fault: scale out: start c3 under load"
start c3 consume
sleep 25
note "fault: scale in: stop c3 (SIGTERM)"
stop c3
wait_exit p1
note "publisher finished"
verify --wait 600
stop c1; stop c2
