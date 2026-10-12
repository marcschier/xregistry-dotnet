# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

ARG TOOLS_IMAGE=xregistry-native-tools:local
FROM ${TOOLS_IMAGE} AS build
WORKDIR /repo
COPY . .
RUN dotnet publish tests/XRegistry.Storage.CrashProbe/XRegistry.Storage.CrashProbe.csproj \
    -c Release -f net10.0 -r linux-x64 -p:PublishAot=true -o /native --nologo -v minimal

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:12dd273c196e92aa91542749b8df0996f497f812811abba9e6613b484f681dd0
RUN apt-get update && apt-get install --yes --no-install-recommends python3 && apt-get clean
COPY --from=build /native /native
COPY eng/verify_storage_crashes.py /harness/verify_storage_crashes.py
ENV TMPDIR=/data
WORKDIR /tmp
USER $APP_UID
ENTRYPOINT ["python3", "/harness/verify_storage_crashes.py", "--probe", "/native/XRegistry.Storage.CrashProbe", "--root", "/data", "--report", "/data/report.json"]
