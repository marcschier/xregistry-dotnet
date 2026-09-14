ARG TOOLS_IMAGE=xregistry-native-tools:local
FROM ${TOOLS_IMAGE} AS build
WORKDIR /repo
COPY . .
RUN dotnet publish interop/XRegistry.InteropProbe/XRegistry.InteropProbe.csproj \
    -c Release -r linux-x64 -o /native --nologo -v minimal

FROM mcr.microsoft.com/dotnet/runtime-deps@sha256:9c2883a67963933e3c4b8d0f732b72c172b48f8f606b6f5dcb95f09e84fcc797
WORKDIR /native
COPY --from=build /native .
USER $APP_UID
ENTRYPOINT ["/native/XRegistry.InteropProbe"]
