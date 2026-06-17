#!/usr/bin/env bash
#
# Build the multi-arch (amd64 + arm64) image locally with docker buildx.
#
# Multi-arch images cannot be loaded into the local docker image store, so this
# script PUSHES to a registry. Set IMAGE to your target, e.g.:
#
#   IMAGE=ghcr.io/youruser/sonarqube-csharp-mcp:latest ./scripts/build-multiarch.sh
#
# For a quick local single-arch build instead, just use:  docker build -t sonarqube-csharp-mcp .
set -euo pipefail

IMAGE="${IMAGE:-ghcr.io/CHANGE-ME/sonarqube-csharp-mcp:latest}"
PLATFORMS="${PLATFORMS:-linux/amd64,linux/arm64}"
BUILDER="${BUILDER:-sonarcs-multiarch}"

if [[ "$IMAGE" == *CHANGE-ME* ]]; then
  echo "ERROR: set IMAGE to your registry target, e.g. IMAGE=ghcr.io/youruser/sonarqube-csharp-mcp:latest" >&2
  exit 1
fi

# Install QEMU binfmt handlers so the amd64 host can build arm64 (one-time, needs --privileged).
echo "==> Ensuring QEMU emulation is registered..."
docker run --privileged --rm tonistiigi/binfmt --install all >/dev/null

# Create a docker-container builder (the default 'docker' driver can't do multi-arch).
if ! docker buildx inspect "$BUILDER" >/dev/null 2>&1; then
  echo "==> Creating buildx builder '$BUILDER'..."
  docker buildx create --name "$BUILDER" --driver docker-container --use
else
  docker buildx use "$BUILDER"
fi
docker buildx inspect --bootstrap >/dev/null

echo "==> Building $IMAGE for $PLATFORMS and pushing..."
docker buildx build \
  --platform "$PLATFORMS" \
  --tag "$IMAGE" \
  --push \
  .

echo "==> Done: $IMAGE ($PLATFORMS)"
