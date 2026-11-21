#!/usr/bin/env bash
# RabbitMQ through toxiproxy: every client starts on rabbit-1's proxy. Cut it repeatedly (connections drop in the
# middle of publisher confirms and acks), reset connections after a few KB, and slow the next host down.
# Usage: rabbitmq-connection-drops.sh [DURATION]
source "$(dirname "$0")/lib.sh"
duration="${1:-200}"
tox_reset; rabbit_proxies
rabbit_proxies 2>/dev/null || true
start c1 consume
start c2 consume
sleep 8
start p1 publish --duration "$duration"
sleep 20
for i in 1 2 3 4; do
  note "fault: rabbit-1 proxy down (cycle $i)"
  tox_down rabbit-1
  sleep 4
  note "heal: rabbit-1 proxy up (cycle $i)"
  tox_up rabbit-1
  sleep 16
done
note "fault: reset rabbit-1 and rabbit-2 connections after 64 KB each way, 2 s of latency on rabbit-3"
for p in rabbit-1 rabbit-2; do
  tox_add "$p" cut-up limit_data upstream '{"bytes":65536}'
  tox_add "$p" cut-down limit_data downstream '{"bytes":65536}'
done
tox_add rabbit-3 slow latency downstream '{"latency":2000,"jitter":500}'
sleep 30
note "heal: remove toxics"
tox_reset; rabbit_proxies
wait_exit p1
note "publisher finished"
verify --wait 600
stop c1; stop c2
