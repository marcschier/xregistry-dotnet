ARG TOOLS_IMAGE=xregistry-native-tools:local
FROM ${TOOLS_IMAGE} AS build
WORKDIR /repo
COPY . .
RUN dotnet restore XRegistry.slnx --locked-mode \
    && pwsh -NoProfile -File eng/test-model-packages.ps1 -RuntimeIdentifier linux-x64 \
    && mkdir /result \
    && cp -a artifacts/package-smoke/*/net8.0-linux-x64 /result/net8 \
    && cp -a artifacts/package-smoke/*/net10.0-linux-x64 /result/net10

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:9c2883a67963933e3c4b8d0f732b72c172b48f8f606b6f5dcb95f09e84fcc797
WORKDIR /native
COPY --from=build /result .
USER $APP_UID
CMD ["/bin/sh", "-c", "test ! -e /usr/bin/dotnet && test ! -e /usr/bin/git && ./net8/ModelConsumer && ./net10/ModelConsumer"]
