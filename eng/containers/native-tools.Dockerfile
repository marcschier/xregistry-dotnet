# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

FROM mcr.microsoft.com/dotnet/sdk@sha256:4ea6fe75dd36706bb6d8c3c293d4c4315840f5d76ea28ac97def77e3ec487fa5
RUN apt-get update && apt-get install --yes --no-install-recommends clang zlib1g-dev python3 python-is-python3
