#!/usr/bin/env bash
# Builds the pinned Goose compiler from source, then the server with it (Goose compiles to C, built by gcc).
set -euo pipefail
cd "$(dirname "$0")"
GOOSE_COMMIT=f605b5a0
if [ ! -x .goose/build/goose ] || [ "$(git -C .goose rev-parse --short=8 HEAD)" != "$GOOSE_COMMIT" ]; then
    rm -rf .goose
    git clone -q https://github.com/aardappel/goose .goose
    git -C .goose checkout -q "$GOOSE_COMMIT"
    cmake -S .goose -B .goose/build -DCMAKE_BUILD_TYPE=Release >/dev/null
    cmake --build .goose/build -j"$(nproc)" >/dev/null
fi
mkdir -p bin
GOOSE_HOME="$PWD/.goose-home" GOOSE_CC=gcc .goose/build/goose build main.goose -o bin/conduit
