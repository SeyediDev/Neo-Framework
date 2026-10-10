FROM python@sha256:48b13b003dda20b16f9442b8475aa05fe21bf6579a8c881db92ffb4d8fd20f83 AS build
RUN apt-get update && apt-get install -y --no-install-recommends gcc g++ make git libffi-dev && rm -rf /var/lib/apt/lists/*
COPY uv.tar.gz sqlite.tar.gz hermes.tar.gz /tmp/
RUN mkdir /tmp/uv && tar -xzf /tmp/uv.tar.gz --strip-components=1 -C /tmp/uv
# Same fixed SQLite source/checksum as the reviewed upstream image. Core API
# runtime only: no Chromium, desktop, messaging extras, scheduler or dashboard.
RUN tar -xzf /tmp/sqlite.tar.gz -C /tmp && cd /tmp/sqlite-autoconf-3530400 && \
    ./configure --prefix=/opt/sqlite-fixed --disable-static && make -j2 && make install
RUN mkdir /opt/hermes && tar -xzf /tmp/hermes.tar.gz --strip-components=1 -C /opt/hermes
WORKDIR /opt/hermes
ENV UV_PYTHON_DOWNLOADS=never UV_LINK_MODE=copy
# sms extra is the upstream minimal locked aiohttp dependency group. It installs
# HTTP support only; no SMS account/platform is configured or enabled.
# Upstream explicitly disallows wheel/sdist installation of Hermes itself.
# Keep its pinned immutable source and invoke the supported module entrypoint;
# only dependencies are synchronized into the private environment.
RUN /tmp/uv/uv sync --frozen --no-default-groups --no-install-project --extra sms --python /usr/local/bin/python3

FROM python@sha256:48b13b003dda20b16f9442b8475aa05fe21bf6579a8c881db92ffb4d8fd20f83
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git libatomic1 && rm -rf /var/lib/apt/lists/*
COPY --from=build /opt/sqlite-fixed/lib/ /opt/sqlite-fixed/lib/
COPY --from=build /opt/hermes /opt/hermes
ENV LD_LIBRARY_PATH=/opt/sqlite-fixed/lib PATH=/opt/hermes/.venv/bin:/usr/local/bin:/usr/bin:/bin \
    HOME=/state HERMES_HOME=/state PYTHONPATH=/opt/hermes PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 LANG=C.UTF-8
RUN /opt/hermes/.venv/bin/python -c "import sqlite3; assert sqlite3.sqlite_version_info >= (3,51,3)" && \
    mkdir /workspace /state && chown 10000:10000 /workspace /state
LABEL fanasa.kind="native-runtime-v1" fanasa.engine="hermes" fanasa.version="rev-5f045f842a60" \
    org.opencontainers.image.revision="5f045f842a60184748dda30acb9fecbd961cc18b"
USER 10000:10000
WORKDIR /workspace
