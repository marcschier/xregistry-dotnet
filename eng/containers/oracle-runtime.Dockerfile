# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:9c2883a67963933e3c4b8d0f732b72c172b48f8f606b6f5dcb95f09e84fcc797
RUN apt-get update && apt-get install --yes --no-install-recommends python3 python3-venv \
    && python3 -m venv /opt/oracles \
    && apt-get clean
COPY eng/requirements-oracles.lock /tmp/requirements-oracles.lock
RUN /opt/oracles/bin/python -m pip install --require-hashes --only-binary=:all: --no-cache-dir \
    -r /tmp/requirements-oracles.lock
ENV PATH="/opt/oracles/bin:${PATH}"
