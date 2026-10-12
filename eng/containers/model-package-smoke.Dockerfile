# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

ARG TOOLS_IMAGE=xregistry-native-tools:local
FROM ${TOOLS_IMAGE} AS build
WORKDIR /repo
COPY . .
RUN dotnet restore XRegistry.slnx \
    && pwsh -NoProfile -File eng/test-model-packages.ps1 -RuntimeIdentifier linux-x64 \
    && mkdir /result \
    && cp -a artifacts/package-smoke/*/net8.0-linux-x64 /result/net8 \
    && cp -a artifacts/package-smoke/*/net10.0-linux-x64 /result/net10

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:12dd273c196e92aa91542749b8df0996f497f812811abba9e6613b484f681dd0
WORKDIR /native
COPY --from=build /result .
USER $APP_UID
CMD ["/bin/sh", "-c", "test ! -e /usr/bin/dotnet && test ! -e /usr/bin/git && ./net8/ModelConsumer && ./net10/ModelConsumer"]
