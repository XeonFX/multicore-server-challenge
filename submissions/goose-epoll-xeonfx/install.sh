#!/usr/bin/env bash
# Runs once as root on a clean Ubuntu 24.04: the C toolchain the Goose compiler is built with and compiles to.
set -euo pipefail
apt-get update -q && apt-get install -y -q build-essential cmake git ca-certificates
gcc --version | head -1
