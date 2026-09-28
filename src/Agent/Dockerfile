# Build context is the repo root: docker buildx build -f src/Agent/Dockerfile .
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
COPY src/Mcp.Hosting/*.csproj src/Mcp.Hosting/
COPY src/McpAdmin/*.csproj src/McpAdmin/
COPY src/Agent/*.csproj src/Agent/
COPY tests/SmarterMail.Tests/*.csproj tests/SmarterMail.Tests/
COPY tests/Agent.Tests/*.csproj tests/Agent.Tests/
RUN dotnet restore tests/SmarterMail.Tests/SmarterMail.Tests.csproj \
 && dotnet restore tests/Agent.Tests/SmarterMailAgent.Tests.csproj

COPY . .
# Gate: a failing test fails the image build, so nothing is pushed or deployed. The frontend's node
# tests (src/Agent/wwwroot/dev/test) are not run here: this image has no node, and .dockerignore
# drops wwwroot/dev.
RUN dotnet test tests/SmarterMail.Tests/SmarterMail.Tests.csproj -c Release
RUN dotnet test tests/Agent.Tests/SmarterMailAgent.Tests.csproj -c Release
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Agent/SmarterMailAgent.csproj -c Release -o /app -p:Version=$VERSION -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# wwwroot is published by the SDK (Content items), minus wwwroot/dev which is excluded
ENV ASPNETCORE_URLS=http://+:8080
# Served at the site root by default; set PATH_BASE (e.g. /mail-agent) to host it under a prefix.
ENV PATH_BASE=/
# The image's built-in non-root user.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmarterMailAgent.dll"]
