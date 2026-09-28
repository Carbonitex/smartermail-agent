# Build context is the repo root: docker buildx build -f src/McpUser/Dockerfile .
# The build stage runs natively ($BUILDPLATFORM) even for multi-arch builds: the publish output is
# framework-dependent IL, identical for every architecture, so only the runtime stage is per-arch.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layer: cached until a project file, package version or feed changes. Only an optimisation —
# the test/publish steps below restore again (a no-op when this layer is complete), so a project
# missing from this list costs time, not correctness.
COPY nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Core/*.csproj src/Core/
COPY src/Tools.Mailbox/*.csproj src/Tools.Mailbox/
COPY src/Tools.DomainAdmin/*.csproj src/Tools.DomainAdmin/
COPY src/Tools.SysAdmin/*.csproj src/Tools.SysAdmin/
COPY src/McpUser/*.csproj src/McpUser/
COPY src/Mcp.Hosting/*.csproj src/Mcp.Hosting/
COPY src/McpAdmin/*.csproj src/McpAdmin/
COPY tests/SmarterMail.Tests/*.csproj tests/SmarterMail.Tests/
RUN dotnet restore tests/SmarterMail.Tests/SmarterMail.Tests.csproj \
 && dotnet restore src/McpUser/SmarterMailMcp.User.csproj

COPY . .
# Gate: a failing test fails the image build, so nothing is pushed or deployed.
RUN dotnet test tests/SmarterMail.Tests/SmarterMail.Tests.csproj -c Release
ARG VERSION=0.0.0-dev
RUN dotnet publish src/McpUser/SmarterMailMcp.User.csproj -c Release -o /app -p:Version=$VERSION -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The image's built-in non-root user; the token file goes to /tmp.
USER $APP_UID
EXPOSE 8080
# Exec form, so `docker run ... <image> --stdio` passes --stdio straight to the server.
ENTRYPOINT ["dotnet", "SmarterMailMcp.User.dll"]
