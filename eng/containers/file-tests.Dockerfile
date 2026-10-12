# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

ARG TOOLS_IMAGE=xregistry-native-tools:local
FROM ${TOOLS_IMAGE} AS build
WORKDIR /repo
COPY . .
RUN dotnet publish tests/XRegistry.File.Tests/XRegistry.File.Tests.csproj \
    -c Release -f net10.0 -r linux-x64 -p:PublishAot=true -o /native --nologo -v minimal

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:12dd273c196e92aa91542749b8df0996f497f812811abba9e6613b484f681dd0
COPY --from=build /native /native
WORKDIR /tmp
USER $APP_UID
ENTRYPOINT ["/native/XRegistry.File.Tests", "--zero-tests-policy", "strict", "--timeout", "3m", "--no-ansi", "--results-directory", "/tmp/xregistry-file-results"]
