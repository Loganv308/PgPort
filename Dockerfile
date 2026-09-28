# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the package layer is cached between code changes.
COPY src/pgport/PgPort.csproj src/pgport/
RUN dotnet restore src/pgport/PgPort.csproj

COPY src/ src/
RUN dotnet publish src/pgport/PgPort.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

COPY --from=build /app/publish .

# The aspnet image ships a non-root "app" user.
USER app

ENTRYPOINT ["dotnet", "PgPort.dll"]
