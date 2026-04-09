#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# run-linux-benchmark.sh
#
# Runs the full Anka load-test suite (framework + TechEmpower DB tests) inside
# Linux containers. PostgreSQL is started automatically.
# Results are written to:
#   docs/throughput-results-linux-{date}.md
#
# Each run creates a new timestamped file; existing results are never overwritten.
#
# Prerequisites:
#   - Podman or Docker must be installed and running
#
# Usage:
#   ./scripts/run-linux-benchmark.sh              # default: linux/amd64
#   ./scripts/run-linux-benchmark.sh linux/arm64  # native speed on Apple Silicon
# ─────────────────────────────────────────────────────────────────────────────

set -euo pipefail

PLATFORM="${1:-linux/amd64}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NETWORK="anka-bench-net"
DB_CONTAINER="anka-bench-db"
IMAGE="anka-benchmark"
SQL_FILE="$REPO_ROOT/Test/LoadTest/Anka.Wrk.LoadTest/scripts/setup_db.sql"

# ── Detect container runtime (Podman → Docker → error) ────────────────────────
if command -v podman &>/dev/null; then
    CTR="podman"
elif command -v docker &>/dev/null; then
    CTR="docker"
else
    echo "Error: neither 'podman' nor 'docker' was found on PATH." >&2
    echo "  Podman: https://podman-desktop.io/" >&2
    echo "  Docker: https://docs.docker.com/get-docker/" >&2
    exit 1
fi

# ── Cleanup helper (called on exit) ──────────────────────────────────────────
cleanup() {
    echo ""
    echo ">>> Cleaning up..."
    "$CTR" rm -f "$DB_CONTAINER" 2>/dev/null || true
    "$CTR" network rm "$NETWORK"  2>/dev/null || true
}
trap cleanup EXIT

echo "=== Anka Linux Benchmark ==="
echo "Runtime  : $CTR"
echo "Platform : $PLATFORM"
echo "Repo root: $REPO_ROOT"
echo ""

# ── Network ───────────────────────────────────────────────────────────────────
echo ">>> Creating network ($NETWORK)..."
"$CTR" network create "$NETWORK" 2>/dev/null || true

# ── PostgreSQL ────────────────────────────────────────────────────────────────
echo ">>> Starting PostgreSQL..."
"$CTR" run -d \
    --name "$DB_CONTAINER" \
    --network "$NETWORK" \
    --platform "$PLATFORM" \
    -e POSTGRES_USER=benchmarkdbuser \
    -e POSTGRES_PASSWORD=benchmarkdbpass \
    -e POSTGRES_DB=benchmark \
    postgres:16-alpine

echo -n ">>> Waiting for PostgreSQL to be ready"
for i in $(seq 1 30); do
    if "$CTR" exec "$DB_CONTAINER" pg_isready -U benchmarkdbuser -d benchmark &>/dev/null; then
        echo " OK"
        break
    fi
    echo -n "."
    sleep 2
    if [ "$i" -eq 30 ]; then
        echo " TIMEOUT"
        exit 1
    fi
done

# ── Schema ────────────────────────────────────────────────────────────────────
echo ">>> Initialising database schema..."
"$CTR" exec -i "$DB_CONTAINER" \
    psql -U benchmarkdbuser -d benchmark < "$SQL_FILE"

# ── Build benchmark image ─────────────────────────────────────────────────────
echo ""
echo ">>> Building benchmark image ($IMAGE)..."
"$CTR" build \
    --platform "$PLATFORM" \
    -f "$REPO_ROOT/Dockerfile.benchmark" \
    -t "$IMAGE" \
    "$REPO_ROOT"

# ── Run benchmark ─────────────────────────────────────────────────────────────
echo ""
echo ">>> Running load test (results → docs/throughput-results-linux-$(date +%Y-%m-%d).md)..."
"$CTR" run \
    --rm \
    --platform "$PLATFORM" \
    --network "$NETWORK" \
    -e DATABASE_URL="Host=${DB_CONTAINER};Database=benchmark;Username=benchmarkdbuser;Password=benchmarkdbpass" \
    -v "$REPO_ROOT/docs:/workspace/docs" \
    "$IMAGE"

echo ""
echo "=== Done. Results written to docs/throughput-results-linux-$(date +%Y-%m-%d).md ==="
