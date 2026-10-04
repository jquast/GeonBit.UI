#!/bin/bash
# Load machine-local settings (wine prefix for the shader compiler).
if [[ -f .env ]]; then
    set -a
    source .env
    set +a
fi
dotnet build "$@"
