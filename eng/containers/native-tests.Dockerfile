# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

ARG TOOLS_IMAGE=xregistry-native-tools:local
ARG ORACLE_IMAGE=xregistry-oracle-runtime:local
FROM ${TOOLS_IMAGE} AS build
ARG TEST_PROJECT
ARG TEST_FRAMEWORK=net10.0
WORKDIR /repo
COPY . .
RUN test -n "$TEST_PROJECT" \
    && dotnet publish "tests/$TEST_PROJECT/$TEST_PROJECT.csproj" \
    -c Release -f "$TEST_FRAMEWORK" -r linux-x64 -p:PublishAot=true -p:EnableRequestDelegateGenerator=true \
    -o /native --nologo -v minimal \
    && mv "/native/$TEST_PROJECT" /native/test-runner

FROM ${ORACLE_IMAGE} AS runtime
COPY --from=build /native /native
RUN mkdir -p /native/ProducedLayouts && chown "$APP_UID:$APP_UID" /native/ProducedLayouts
ENV TMPDIR=/data
WORKDIR /tmp
USER $APP_UID
ENTRYPOINT ["/native/test-runner", "--zero-tests-policy", "strict", "--timeout", "5m", "--no-ansi", "--results-directory", "/data/results"]
