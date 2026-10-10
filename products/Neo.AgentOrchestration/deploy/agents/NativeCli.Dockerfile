FROM python@sha256:48b13b003dda20b16f9442b8475aa05fe21bf6579a8c881db92ffb4d8fd20f83 AS unpack
ARG ENGINE
COPY opencode.tar.gz codex.tar.gz /tmp/
RUN mkdir /opt/native && case "$ENGINE" in \
    opencode) tar -xzf /tmp/opencode.tar.gz -C /opt/native ;; \
    codex) tar -xzf /tmp/codex.tar.gz -C /opt/native ;; \
    *) exit 2 ;; esac && rm /tmp/opencode.tar.gz /tmp/codex.tar.gz

FROM python@sha256:48b13b003dda20b16f9442b8475aa05fe21bf6579a8c881db92ffb4d8fd20f83
ARG ENGINE
ARG VERSION
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git && rm -rf /var/lib/apt/lists/*
COPY --from=unpack /opt/native /opt/native
RUN case "$ENGINE" in \
    opencode) ln -s /opt/native/opencode /usr/local/bin/opencode ;; \
    codex) ln -s /opt/native/bin/codex /usr/local/bin/codex ;; \
    *) exit 2 ;; esac
RUN mkdir -p /workspace /state && chown 10000:10000 /workspace /state
LABEL fanasa.kind="native-runtime-v1" fanasa.engine="$ENGINE" fanasa.version="$VERSION"
ENV HOME=/state LANG=C.UTF-8
USER 10000:10000
WORKDIR /workspace
