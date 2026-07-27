#!/usr/bin/env sh
# Run the coach LLM service locally (port 8002).
#
# Usage:
#   ./run.sh          # provider from .env (LLM_PROVIDER=jeonbuk|gemini|claude)
#   ./run.sh mock     # no API key needed — templated demo questions
#   ./run.sh gemini   # override provider for this run
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

# Optional provider override as the first arg (wins over .env).
[ -n "$1" ] && export LLM_PROVIDER="$1"

cd "$DIR"
echo "[coach] provider=${LLM_PROVIDER:-gemini}  port=8002"
exec env PYTHONPATH="$REPO_ROOT" ./.venv/bin/uvicorn app.main:app --host 127.0.0.1 --port 8002
