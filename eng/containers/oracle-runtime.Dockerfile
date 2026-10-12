# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:12dd273c196e92aa91542749b8df0996f497f812811abba9e6613b484f681dd0
RUN apt-get update && apt-get install --yes --no-install-recommends python3 python3-venv \
    && python3 -m venv /opt/oracles \
    && apt-get clean
COPY eng/requirements-oracles.lock /tmp/requirements-oracles.lock
RUN /opt/oracles/bin/python -m pip install --require-hashes --only-binary=:all: --no-cache-dir \
    -r /tmp/requirements-oracles.lock
ENV PATH="/opt/oracles/bin:${PATH}"
