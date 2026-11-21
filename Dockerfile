FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, with only the project file copied, so a source-only change
# does not invalidate the restore layer.
COPY src/Tams.Api/Tams.Api.csproj src/Tams.Api/
RUN dotnet restore src/Tams.Api/Tams.Api.csproj

COPY src/ src/
RUN dotnet publish src/Tams.Api/Tams.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Mount points owned by the non-root user so the named volumes (media, dp-keys) are writable.
RUN mkdir -p /app/media /app/keys && chown -R $APP_UID /app/media /app/keys

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Non-root, per the server hardening in deployment-decision.md section 12.
USER $APP_UID

ENTRYPOINT ["dotnet", "Tams.Api.dll"]
