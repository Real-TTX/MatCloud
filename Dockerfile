# syntax=docker/dockerfile:1

# MatCloud - the cloud of the Thix apps (platform + app modules: sync API for the devices and the web app).

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable (no native host), so the arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "Directory.Build.targets", "./"]
COPY ["src/MatCloud.Contract/MatCloud.Contract.csproj", "src/MatCloud.Contract/"]
COPY ["src/MatCloud.Core/MatCloud.Core.csproj", "src/MatCloud.Core/"]
COPY ["src/Apps/MatCloud.Apps.Calendar/MatCloud.Apps.Calendar.csproj", "src/Apps/MatCloud.Apps.Calendar/"]
COPY ["src/Apps/MatCloud.Apps.Clip/MatCloud.Apps.Clip.csproj", "src/Apps/MatCloud.Apps.Clip/"]
COPY ["src/Apps/MatCloud.Apps.Note/MatCloud.Apps.Note.csproj", "src/Apps/MatCloud.Apps.Note/"]
COPY ["src/MatCloud/MatCloud.csproj", "src/MatCloud/"]
RUN dotnet restore "src/MatCloud/MatCloud.csproj"

COPY ["src", "src"]
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
# The secret of ThixCalendar's Google desktop client (build secret thixcalendar_google_secret, optional) lets MatCloud keep the Google sign-ins
# ThixCalendar hands over; as a build secret it never shows in the history of the image.
RUN --mount=type=secret,id=thixcalendar_google_secret,required=false \
    BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    GOOGLE_SECRET="$(cat /run/secrets/thixcalendar_google_secret 2>/dev/null || true)" && \
    dotnet publish "src/MatCloud/MatCloud.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE \
    -p:ThixCalendarGoogleClientSecret="$GOOGLE_SECRET"

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL org.opencontainers.image.title="MatCloud" \
      org.opencontainers.image.description="MatCloud - die Cloud der Thix-Apps: Sync-Server und Web-App (PWA)" \
      org.opencontainers.image.source="https://github.com/Real-TTX/MatCloud"

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:4950 \
    HTTP_PORTS="" \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    TZ=Europe/Berlin

EXPOSE 4950

COPY --from=build /app/publish .

# One volume for what the app keeps in files: the keys that protect the sessions, the bindings of the platform. The database is the other container.
RUN mkdir -p /data/keys && chown -R $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

# The app checks itself on /api/health (the platform contract: 200 for ok and degraded, 503 for down), so the image needs no curl.
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s --retries=3 \
    CMD ["dotnet", "MatCloud.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "MatCloud.dll"]
