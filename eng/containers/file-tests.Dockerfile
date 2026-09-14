ARG TOOLS_IMAGE=xregistry-native-tools:local
FROM ${TOOLS_IMAGE} AS build
WORKDIR /repo
COPY . .
RUN dotnet publish tests/XRegistry.File.Tests/XRegistry.File.Tests.csproj \
    -c Release -f net10.0 -r linux-x64 -p:PublishAot=true -o /native --nologo -v minimal

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:9c2883a67963933e3c4b8d0f732b72c172b48f8f606b6f5dcb95f09e84fcc797
COPY --from=build /native /native
WORKDIR /tmp
USER $APP_UID
ENTRYPOINT ["/native/XRegistry.File.Tests", "--zero-tests-policy", "strict", "--timeout", "3m", "--no-ansi", "--results-directory", "/tmp/xregistry-file-results"]
