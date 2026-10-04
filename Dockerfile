FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Webionic.ICalMerger/Webionic.ICalMerger.csproj src/Webionic.ICalMerger/
RUN dotnet restore src/Webionic.ICalMerger
COPY src ./src
# publish führt das Build-Target aus der .csproj aus: Es lädt die gepinnte Tailwind-CLI (Netzwerk im Build-Stage nötig)
# und erzeugt wwwroot/css/app.css neu. Node wird nicht gebraucht, die CLI landet nicht im Runtime-Image.
RUN dotnet publish src/Webionic.ICalMerger -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__DefaultConnection="Data Source=/data/app.db;Default Timeout=30" \
    DataProtection__KeysPath=/data/keys

# Daten (SQLite und Data-Protection-Schlüssel) liegen auf dem Volume /data.
RUN mkdir -p /data/keys && chown -R $APP_UID /data
USER $APP_UID
VOLUME /data
EXPOSE 8080

ENTRYPOINT ["dotnet", "Webionic.ICalMerger.dll"]
