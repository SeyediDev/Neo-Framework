# Resolve these two official base images to digests when installing the runner.
# This build runs under the operator, never inside a source CI job.
ARG NODE_IMAGE=node:24-bookworm-slim
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${NODE_IMAGE} AS node
FROM ${DOTNET_SDK_IMAGE}
RUN apt-get update && apt-get install -y --no-install-recommends python3 git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=node /usr/local/bin/node /usr/local/bin/node
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_PROCESSOR_COUNT=1
RUN dotnet --version && node --version && python3 --version
