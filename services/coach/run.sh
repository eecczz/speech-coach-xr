#!/usr/bin/env sh
# Run the coach LLM service locally (port 8002).
#
# Usage:
#   ./run.sh          # local Ollama only (no cloud quota)
#
# First-time setup:
#   python3 -m venv .venv && ./.venv/bin/pip install -r requirements.txt
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$DIR/../.." && pwd)"

# Load API keys / config from the repo-root .env.
if [ -f "$REPO_ROOT/.env" ]; then
  set -a
  . "$REPO_ROOT/.env"
  set +a
fi

export LLM_PROVIDER=ollama

cd "$DIR"
echo "[coach] provider=ollama  port=8002"
exec env PYTHONPATH="$REPO_ROOT" ./.venv/bin/uvicorn app.main:app --host 127.0.0.1 --port 8002
