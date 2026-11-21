# Foundatio.Chaos

A workload for checking the v2 messaging and durable jobs guarantees while brokers misbehave. It is a console app
with three roles, run as separate processes so they can be killed and restarted:

- `publish` sends a steady mix for `--duration` seconds: ungrouped commands in batches (`chaos.work`), grouped
  commands in per-group batches, some of them delayed (`chaos.grouped`), delayed commands (`chaos.delayed`), events
  to a service subscription (`chaos.event`) and, with `--jobs true` (the default on Redis), durable jobs. Every
  message carries a unique id and a sequence number. Some fail their first attempt, some fail every attempt (and must
  end up dead-lettered), and `--slow-every N` makes every Nth command run for `--slow-ms` (longer than the lease).
- `consume` handles everything until SIGTERM (graceful) or SIGKILL.
- `verify` waits until what was sent is settled, then checks the guarantees and asks the broker for what is left.

Each process appends what it does to `<run-dir>/<name>.<pid>.tsv` as it happens (sends before and after they are
confirmed, every handler start and finish with the delivery id, and memory, handle, thread and processor counts every
5 s), so a killed process loses nothing it recorded. The verifier reports, per stream:

- sent, confirmed, **lost** (confirmed but never handled successfully, or a poison message missing from its dead-letter
  queue), duplicates (and how many of them the publisher sent twice after a failed send), redeliveries after a kill
  or lost lease, dead letters expected and found, and unexpected dead letters;
- delayed messages handled **early**, and how late the rest were;
- **concurrent runs of one delivery** (a lease lost while its handler kept running);
- **group order** on transports that declare `OrderedGroups`: a message must not start before its predecessor's
  settling run has finished;
- gaps with no handler starting, and how long after each fault handling resumed;
- each process's memory, file descriptors and threads; and the broker's backlog and job counts after the run.

It exits 1 when anything is lost, early, unexpectedly dead-lettered, concurrent, out of order or left on the broker.

## Running

```bash
dotnet build tests/Foundatio.Chaos/Foundatio.Chaos.csproj
# Copy the build somewhere else, so rebuilding doesn't change the binaries of running processes.
rsync -a tests/Foundatio.Chaos/bin/Debug/net10.0/ ~/tmp/chaos-bin/ && export CHAOS_BIN=~/tmp/chaos-bin/Foundatio.Chaos

# Brokers and toxiproxy (Redis cluster on 7700-7705 announced through proxies on 26700-26705, Kafka on
# 19092-19094 through 26092-26094, toxiproxy's API on 28474).
docker compose -f tests/Foundatio.Chaos/docker/compose.yml --profile redis --profile kafka up -d
docker build -t pr601-chaos-iptables -f tests/Foundatio.Chaos/docker/iptables.Dockerfile tests/Foundatio.Chaos/docker

export RUN=~/tmp/chaos/redis-faults CHAOS_PREFIX=rf1 CHAOS_TRANSPORT=redis \
  CHAOS_CONNECTION="127.0.0.1:26700,abortConnect=false,allowAdmin=true"
tests/Foundatio.Chaos/scenarios/redis-faults.sh 240
```

Every setting is an argument (`--work-rate 20`) or an environment variable (`CHAOS_WORK_RATE=20`). The common ones:

| Setting | Default | |
|---|---|---|
| `transport` | `memory` | `rabbitmq`, `redis`, `kafka`, `sqs` (`--fifo true`), `asb` (`--sessions true`) |
| `connection` | per transport | AMQP URI, Redis configuration, Kafka bootstrap servers, SQS or Service Bus connection string |
| `hosts`, `management` | | RabbitMQ failover hosts and management API |
| `kafka-protocol`, `partitions`, `replication-factor` | `classic`, 6, 1 | `consumer` for KIP-848 |
| `prefix` | `chaos` | use a new one per run |
| `concurrency`, `lease-seconds`, `max-attempts`, `retry-delay-ms` | 16, 15, 3, 2000 | handler options |
| `work-rate`, `group-batch-rate`, `delayed-rate`, `event-rate`, `job-rate` | 20, 20, 10, 20, 5 | sends per second; work and events go in batches of 10 and 5, groups in batches of 5 over 16 groups |
| `delay-min-ms`, `delay-max-ms`, `group-delay-every`, `group-delay-ms` | 1000, 30000, 15, 3000 | delays |
| `poison-every`, `transient-every`, `slow-every`, `slow-ms` | 250, 40, 0, 25000 | failures |
| `clock-file` | | a file holding a clock offset in ms, read every second, for clock jumps |
| `expected-loss-until-ms` | 0 | (verify) losses of messages sent before this time are expected, e.g. a restart without persistence |

## Scenarios

`scenarios/lib.sh` has the helpers (`start`, `stop NAME KILL`, `note "fault: ..."`, toxiproxy, `partition`, RabbitMQ
proxy re-pointing). Each scenario starts two consumers and a publisher, injects its faults, then verifies:

| Script | Faults |
|---|---|
| `baseline.sh` | none |
| `process-kill.sh` | SIGKILL consumers mid-handler (some handlers outlast the lease) and restart them; SIGKILL the publisher mid-batch |
| `clock-jump.sh` | one consumer's clock jumps +2 min, then -1 min; the publisher's clock runs 30 s ahead |
| `rabbitmq-node-loss.sh` | lose one node, then a second (no quorum) while delays wait, then bring both back |
| `rabbitmq-leader-loss.sh` | kill the work queue's leader; stop the grouped queue's leader gracefully |
| `rabbitmq-partition.sh` | cut the work queue's leader off from the other nodes for 90 s; freeze a node with `docker pause` |
| `rabbitmq-connection-drops.sh` | cut the connections clients use, mid-confirm and mid-ack; reset after 64 KB; 2 s latency |
| `redis-faults.sh` | kill the primary that holds the work queue; graceful failover; reset every connection; latency past the lease |
| `redis-restart.sh` | restart the whole cluster without persistence (start it with `CHAOS_REDIS_AOF=no CHAOS_REDIS_RDB_DISABLED=yes`) |
| `kafka-faults.sh` | broker restart, broker kill and preferred leader election, consumer SIGKILL mid-batch, scale out and in |

The RabbitMQ scenarios use the three-node cluster in `tests/Foundatio.RabbitMQ.AppHost` (`aspire start --isolated`,
then stop the other resources). Set `RABBIT_NODES` to the three containers in order and run `rabbit_proxies` (from
`lib.sh`) to put toxiproxy (26672-26674, management on 26675) in front of them; point `CHAOS_HOSTS` at the proxies.
