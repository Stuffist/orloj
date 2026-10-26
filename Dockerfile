# Orloj, the complete site: browser app (builder /builder/, tournaments /pairings/, online games
# /online/), API, online games synchronization (/hubs/online), served by the ASP.NET Core host
# (Olomoc.Server). SQLite database in /data (volume).
# Stage 1: publish with the .NET SDK; stage 2: ASP.NET runtime only, HTTP on 8080.
# GENERIC (any instance). HTTPS is handled by a reverse proxy in front of the container (for
# orloj.top: deploy/proxy, Caddy).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore alone first: layer cached as long as the .csproj files do not change.
COPY src/Olomoc.Shared/Olomoc.Shared.csproj src/Olomoc.Shared/
COPY src/Olomoc.Builder/Olomoc.Builder.csproj src/Olomoc.Builder/
COPY src/Olomoc.Pairings/Olomoc.Pairings.csproj src/Olomoc.Pairings/
COPY src/Olomoc.Online/Olomoc.Online.csproj src/Olomoc.Online/
COPY src/Olomoc.Web/Olomoc.Web.csproj src/Olomoc.Web/
COPY src/Olomoc.Server/Olomoc.Server.csproj src/Olomoc.Server/
RUN dotnet restore src/Olomoc.Server/Olomoc.Server.csproj

COPY src/ src/
RUN dotnet publish src/Olomoc.Server/Olomoc.Server.csproj -c Release -o /app --no-restore
# SQLite: native library shipped for about twenty platforms; only the Alpine one (musl) is used
# in the final image.
RUN find /app/runtimes -name "*e_sqlite3*" ! -path "*/linux-musl-*" -delete \
 && find /app/runtimes -type d -empty -delete


FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app .

# HTTP on 8080 (the image's ASPNETCORE_HTTP_PORTS), unprivileged "app" user (uid 1654): the
# ./data folder mounted on /data must belong to it.
ENV DATA_DIR=/data
RUN mkdir /data && chown app /data
VOLUME /data
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Olomoc.Server.dll"]
