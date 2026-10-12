# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

ARG TOOLS_IMAGE=xregistry-native-tools:local
FROM ${TOOLS_IMAGE} AS build
WORKDIR /repo
COPY . .
RUN dotnet publish interop/XRegistry.InteropProbe/XRegistry.InteropProbe.csproj \
    -c Release -r linux-x64 -o /native --nologo -v minimal

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:12dd273c196e92aa91542749b8df0996f497f812811abba9e6613b484f681dd0
WORKDIR /native
COPY --from=build /native .
USER $APP_UID
ENTRYPOINT ["/native/XRegistry.InteropProbe"]
