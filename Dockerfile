# One Dockerfile, two images: `docker build --target api` and `docker build --target worker`, both from
# the repo root. They share the restore and source layers; BuildKit only builds the stages a target needs.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
# Project files first, so a code-only change reuses the cached restore layer.
COPY Directory.Build.props ./
COPY src/Marquee.Domain/Marquee.Domain.csproj src/Marquee.Domain/
COPY src/Marquee.Infrastructure/Marquee.Infrastructure.csproj src/Marquee.Infrastructure/
COPY src/Marquee.Api/Marquee.Api.csproj src/Marquee.Api/
COPY src/Marquee.Worker/Marquee.Worker.csproj src/Marquee.Worker/
RUN dotnet restore src/Marquee.Api/Marquee.Api.csproj \
 && dotnet restore src/Marquee.Worker/Marquee.Worker.csproj
COPY src/ src/

FROM build AS publish-api
RUN dotnet publish src/Marquee.Api/Marquee.Api.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM build AS publish-worker
RUN dotnet publish src/Marquee.Worker/Marquee.Worker.csproj -c Release -o /out --no-restore /p:UseAppHost=false

# Debian-based runtime images on purpose: they ship tzdata, and CLAUDE.md §4.4's day window is evaluated
# in the container's local time (TZ). Chiseled and Alpine images have no zoneinfo, so TZ would be
# silently ignored and every Premiere would be scheduled in UTC.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS api
WORKDIR /app
COPY --from=publish-api /out .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Marquee.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS worker
WORKDIR /app
COPY --from=publish-worker /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "Marquee.Worker.dll"]
