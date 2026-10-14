#!/usr/bin/env bash
# Runs once when your Codespace or dev container is created: installs the lab's Python
# dependencies and smoke-tests them.
#
# It lives at the REPO ROOT, not under lab/. Codespaces only looks for a dev container at the
# repository root, and the labs need Course/ as well as lab/, so every path below is root-relative.
set -uo pipefail        # deliberately NOT -e; an install failure is handled explicitly below

echo "== Installing Claims 365 lab dependencies (pinned) =="
python -m pip install --upgrade pip

# Install the LOCK rather than requirements.txt. agent-framework pulls in about thirty sub-packages
# that carry no version specifiers, so two fresh installs days apart can differ on dozens of them.
# The lock is the exact set these labs were tested against.
#
# The failure is handled here rather than by `set -e` because Codespaces opens the editor even when
# this script fails. A silent abort would leave you with a container that looks healthy until the
# first import fails much later. Instead: fall back to the readable requirements, and write a file
# into the explorer that is hard to miss.
if ! python -m pip install -r lab/requirements.lock.txt; then
  echo
  echo "############################################################################"
  echo "##  THE PINNED INSTALL FAILED. Falling back to lab/requirements.txt.       ##"
  echo "##  Versions may differ from the tested set. Expect small differences.   ##"
  echo "############################################################################"
  echo
  if ! python -m pip install -r lab/requirements.txt; then
    echo "SETUP FAILED - neither the lock nor the plain requirements installed." > SETUP-FAILED.txt
    echo "Run this and read the error it prints:" >> SETUP-FAILED.txt
    echo "  python -m pip install -r lab/requirements.txt" >> SETUP-FAILED.txt
    echo
    echo "SETUP FAILED. See SETUP-FAILED.txt in the file explorer."
    exit 1
  fi
  echo "FELL BACK to lab/requirements.txt - versions are not the tested set." > SETUP-USED-FALLBACK.txt
fi

# Pin VS Code to the interpreter we just installed into. The Run button otherwise defaults to
# Debian's /usr/bin/python3 in this image, which has none of these packages, and every lab script
# fails with ModuleNotFoundError. Detected rather than hard-coded so it survives an image change.
PYBIN="$(command -v python)"
mkdir -p .vscode
cat > .vscode/settings.json <<EOF
{
  "python.defaultInterpreterPath": "$PYBIN",
  "python.terminal.activateEnvInCurrentTerminal": true
}
EOF
echo "== VS Code pinned to $PYBIN =="

echo "== Smoke test: imports + versions =="
# The result is checked: a failed import here means the container is not ready, and saying
# "ready" anyway would only move the failure to the middle of Chapter 5.
if ! python - <<'PY'
import importlib.metadata as m
import sys
print("  python         :", sys.version.split()[0])
print("  agent-framework:", m.version("agent-framework"))
print("  azure-identity :", m.version("azure-identity"))
# What the Fraud Desk imports: the workflow, tools and middleware, the Responses client, the sign-in.
from agent_framework import (Executor, FileCheckpointStorage, FunctionMiddleware, WorkflowBuilder,
                             handler, response_handler, tool)
from agent_framework.openai import OpenAIChatClient
from azure.identity import DeviceCodeCredential, TokenCachePersistenceOptions
print("  Agent Framework and azure-identity symbols resolve")
# And what --telemetry imports.
from opentelemetry.sdk.trace import TracerProvider
from azure.monitor.opentelemetry.exporter import AzureMonitorTraceExporter
print("  opentelemetry-sdk:", m.version("opentelemetry-sdk"))
print("  All imports the labs need are present.")
PY
then
  echo "SETUP FAILED - the packages installed, but the smoke test could not import them." > SETUP-FAILED.txt
  echo "Run this and read the error it prints:" >> SETUP-FAILED.txt
  echo "  python lab/verify_env.py" >> SETUP-FAILED.txt
  echo
  echo "SETUP FAILED. See SETUP-FAILED.txt in the file explorer."
  exit 1
fi

# Create your .env from the sample. -n never overwrites, so a rebuild
# or re-run cannot clobber a file someone has already filled in.
cp -n lab/.env.sample lab/.env 2>/dev/null || true

echo
echo "== Next steps =="
echo "  1) lab/.env was just created from lab/.env.sample. Replace its four <placeholders>"
echo "     with your own tenant's values (Chapter 1 of the book). There is NO key to paste."
echo "  2) Model calls are KEYLESS: they reuse your sign-in (python m3_login.py, or"
echo "     dotnet run -- login for C#), the same session that reads the claims book. To"
echo "     override it, put your own resource key or an Entra token in FOUNDRY_KEY."
echo "  3) python lab/verify_env.py                            # offline, no credentials"
echo "  4) cd Course/Module3/code/python && python m3_test.py  # offline, no model calls"
echo
echo "Lab environment ready."
echo "  Work from the book, not from this repository:"
echo "    Chapters 2-3  browser only - Tenant-Setup/ holds what you import and paste"
echo "    Chapter 4     browser only - the schema to paste and the evidence to attach"
echo "    Chapter 5     Course/Module3/code/python/  (Python)  or  .../dotnet/FraudDesk/  (C#)"
