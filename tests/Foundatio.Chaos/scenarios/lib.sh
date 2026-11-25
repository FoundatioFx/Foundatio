#!/usr/bin/env bash
# Helpers for chaos scenarios. Source it after setting CHAOS_TRANSPORT, CHAOS_CONNECTION (and the other CHAOS_*
# settings the workload reads) and RUN (the run directory). Every process runs pinned to CHAOS_CPUS.
set -uo pipefail

CHAOS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CHAOS_BIN="${CHAOS_BIN:-$CHAOS_ROOT/bin/Debug/net10.0/Foundatio.Chaos}"
CHAOS_CPUS="${CHAOS_CPUS:-14-17}"
TOXIPROXY="${TOXIPROXY:-http://127.0.0.1:28474}"
: "${RUN:?set RUN to the run directory}"
export CHAOS_RUN_DIR="$RUN"
mkdir -p "$RUN"

now_ms() { date +%s%3N; }

# note "fault: kill node 2" writes a timestamped line the verifier reports recovery times against.
note() { printf '%s\tX\t%s\n' "$(now_ms)" "$*" >> "$RUN/scenario.0.tsv"; echo "[$(date -u +%T)] $*"; }

# start NAME ROLE [ARGS...] starts the workload in the background; its output goes to $RUN/NAME.log.
start() {
  local name="$1" role="$2"; shift 2
  taskset -c "$CHAOS_CPUS" "$CHAOS_BIN" "$role" --name "$name" "$@" >> "$RUN/$name.log" 2>&1 &
  echo $! > "$RUN/$name.pid"
  echo "[$(date -u +%T)] started $name ($role) pid $!"
}

pid_of() { cat "$RUN/$1.pid"; }

# stop NAME [SIGNAL] signals a process started by start (TERM by default) and waits for it to exit.
stop() {
  local name="$1" signal="${2:-TERM}" pid
  pid="$(pid_of "$name")"
  kill -s "$signal" "$pid" 2>/dev/null || return 0
  for _ in $(seq 1 120); do kill -0 "$pid" 2>/dev/null || return 0; sleep 0.5; done
  echo "$name ($pid) did not exit after SIG$signal; killing"; kill -9 "$pid" 2>/dev/null
}

wait_exit() {
  local pid; pid="$(pid_of "$1")"
  while kill -0 "$pid" 2>/dev/null; do sleep 1; done
}

verify() { taskset -c "$CHAOS_CPUS" "$CHAOS_BIN" verify --name verify --metrics false "$@" 2>&1 | grep -v ' info: ' | tee "$RUN/verify.out"; }

# Toxiproxy: tox_add PROXY NAME TYPE STREAM 'ATTRIBUTES-JSON', tox_del PROXY NAME, tox_down PROXY, tox_up PROXY.
tox_add() { curl -s -X POST "$TOXIPROXY/proxies/$1/toxics" -d "{\"name\":\"$2\",\"type\":\"$3\",\"stream\":\"$4\",\"attributes\":$5}" > /dev/null; }
tox_del() { curl -s -X DELETE "$TOXIPROXY/proxies/$1/toxics/$2" > /dev/null; }
tox_down() { curl -s -X POST "$TOXIPROXY/proxies/$1" -d '{"enabled":false}' > /dev/null; }
tox_up() { curl -s -X POST "$TOXIPROXY/proxies/$1" -d '{"enabled":true}' > /dev/null; }
tox_reset() { curl -s -X POST "$TOXIPROXY/reset" > /dev/null; }

# RabbitMQ: point toxiproxy's rabbit-1..3 (26672-26674) at the AMQP ports of the RABBIT_NODES containers, and
# rabbit-mgmt (26675) at the management API of the first running one. Docker gives a restarted container new host
# ports, so call it again after every docker start.
rabbit_proxies() {
  local i=0 node port mgmt=""
  read -r -a _nodes <<< "${RABBIT_NODES:?set RABBIT_NODES}"
  for node in "${_nodes[@]}"; do
    i=$((i + 1))
    port="$(docker port "$node" 5672 2>/dev/null | head -1 | cut -d: -f2)"
    [ -n "$port" ] || continue
    [ -n "$mgmt" ] || mgmt="$(docker port "$node" 15672 | head -1 | cut -d: -f2)"
    curl -s -X POST "$TOXIPROXY/proxies" -d "{\"name\":\"rabbit-$i\",\"listen\":\"127.0.0.1:$((26671 + i))\",\"upstream\":\"127.0.0.1:$port\",\"enabled\":true}" > /dev/null
    curl -s -X POST "$TOXIPROXY/proxies/rabbit-$i" -d "{\"upstream\":\"127.0.0.1:$port\"}" > /dev/null
  done
  curl -s -X POST "$TOXIPROXY/proxies" -d "{\"name\":\"rabbit-mgmt\",\"listen\":\"127.0.0.1:26675\",\"upstream\":\"127.0.0.1:$mgmt\",\"enabled\":true}" > /dev/null
  curl -s -X POST "$TOXIPROXY/proxies/rabbit-mgmt" -d "{\"upstream\":\"127.0.0.1:$mgmt\"}" > /dev/null
}

# rabbit_start CONTAINER starts a stopped node, waits until it runs, and re-points the proxies.
rabbit_start() {
  docker start "$1" > /dev/null
  for _ in $(seq 1 60); do docker exec "$1" rabbitmq-diagnostics -q check_running > /dev/null 2>&1 && break; sleep 1; done
  rabbit_proxies
}

# partition CONTAINER PEER... drops traffic between a container and its peers' IPs on their docker network, leaving
# its published ports reachable from the host. heal_partition CONTAINER removes the rules. Needs the image built from
# docker/iptables.Dockerfile (docker build -t pr601-chaos-iptables -f docker/iptables.Dockerfile docker).
partition() {
  local victim="$1" rules="" peer ip; shift
  for peer in "$@"; do
    ip="$(docker inspect "$peer" --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}')"
    rules="$rules iptables -A INPUT -s $ip -j DROP; iptables -A OUTPUT -d $ip -j DROP;"
  done
  docker run --rm --cap-add NET_ADMIN --net "container:$victim" pr601-chaos-iptables sh -c "$rules"
}
heal_partition() { docker run --rm --cap-add NET_ADMIN --net "container:$1" pr601-chaos-iptables iptables -F; }
