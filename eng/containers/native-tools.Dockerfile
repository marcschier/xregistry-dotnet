# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

FROM mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
RUN apt-get update && apt-get install --yes --no-install-recommends clang zlib1g-dev python3 python-is-python3
