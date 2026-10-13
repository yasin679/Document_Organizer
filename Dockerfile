# Document Organizer — container image
# Build:  docker build -t document-organizer .
# Run:    docker run -p 8080:8080 -v docorg-data:/app/data -e DOCORG_ADMIN_PASSWORD=... document-organizer

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/DocumentOrganizer/DocumentOrganizer.csproj DocumentOrganizer/
RUN dotnet restore DocumentOrganizer/DocumentOrganizer.csproj
COPY src/DocumentOrganizer/ DocumentOrganizer/
RUN dotnet publish DocumentOrganizer/DocumentOrganizer.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# SQLite comes from the operating system library
RUN apt-get update && apt-get install -y --no-install-recommends libsqlite3-0 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
# "Urls" (not ASPNETCORE_URLS) so it overrides the value in appsettings.json
ENV Urls=http://+:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
VOLUME /app/data
ENTRYPOINT ["dotnet", "DocumentOrganizer.dll"]
