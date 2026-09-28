#!/usr/bin/env bash
# Build (and optionally push) the Docker images. Releases are built by .github/workflows/release.yml;
# this is for local builds and private registries. Each image's build stage runs the .NET tests first,
# so a failing test aborts the build.
#
# Usage: scripts/build-images.sh [user|admin|agent|all]   (default: all)
#
#   REGISTRY   image prefix           default: smartermail (local only, e.g. smartermail/smartermail-mcp-user)
#   TAG        image tag              default: latest
#   PLATFORMS  buildx platforms       default: the host's platform
#   PUSH       1 = push to REGISTRY   default: 0 (load into the local Docker daemon)
#   VERSION    assembly version       default: 0.0.0-local
set -euo pipefail

cd "$(dirname "$0")/.."

REGISTRY="${REGISTRY:-smartermail}"
TAG="${TAG:-latest}"
PUSH="${PUSH:-0}"
VERSION="${VERSION:-0.0.0-local}"

build() {
    local dockerfile=$1 image=$2
    local args=(buildx build -f "$dockerfile" -t "$REGISTRY/$image:$TAG" --build-arg "VERSION=$VERSION")
    [[ -n "${PLATFORMS:-}" ]] && args+=(--platform "$PLATFORMS")
    if [[ "$PUSH" == "1" ]]; then args+=(--push); else args+=(--load); fi
    echo "Building $REGISTRY/$image:$TAG ..."
    docker "${args[@]}" .
}

case "${1:-all}" in
    user)  build src/McpUser/Dockerfile smartermail-mcp-user ;;
    admin) build src/McpAdmin/Dockerfile smartermail-mcp-admin ;;
    agent) build src/Agent/Dockerfile smartermail-agent ;;
    all)
        build src/McpUser/Dockerfile smartermail-mcp-user
        build src/McpAdmin/Dockerfile smartermail-mcp-admin
        build src/Agent/Dockerfile smartermail-agent
        ;;
    *) echo "Usage: $0 [user|admin|agent|all]" >&2; exit 1 ;;
esac
