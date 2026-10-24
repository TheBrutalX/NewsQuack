# T-045: multi-stage build for the Newsquack service image (Docker Hub: thebrutalx/newsquack).
# Build stage restores before copying sources so package restore stays layer-cached.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files + central package management first -> `dotnet restore` is a stable, cacheable layer.
COPY Directory.Build.props Directory.Packages.props NewsQuack.sln ./
COPY src/NewsQuack.Core/NewsQuack.Core.csproj src/NewsQuack.Core/
COPY src/NewsQuack.Infrastructure/NewsQuack.Infrastructure.csproj src/NewsQuack.Infrastructure/
COPY src/NewsQuack.App/NewsQuack.App.csproj src/NewsQuack.App/
COPY tests/NewsQuack.Core.Tests/NewsQuack.Core.Tests.csproj tests/NewsQuack.Core.Tests/
COPY tests/NewsQuack.App.Tests/NewsQuack.App.Tests.csproj tests/NewsQuack.App.Tests/
RUN dotnet restore NewsQuack.sln

# Sources, then publish framework-dependent (self-contained off: the runtime image provides the shared framework).
COPY . .
RUN dotnet publish src/NewsQuack.App -c Release -o /app/publish

# T-030: the App now carries a FrameworkReference to Microsoft.AspNetCore.App (health endpoints), so the runtime stage must be the
# aspnet image — dotnet/runtime does not contain the ASP.NET Core shared framework and the container would fail at startup.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Dedicated non-root user (uid/gid 1001).
RUN groupadd --gid 1001 app \
    && useradd --uid 1001 --gid app --create-home app

WORKDIR /app
COPY --from=build /app/publish ./

# /data is the SQLite database mount point (wired by later tasks); both /app and /data must be owned by the app user.
RUN mkdir -p /data \
    && chown -R app:app /app /data

USER app

ENTRYPOINT ["dotnet", "NewsQuack.App.dll"]
