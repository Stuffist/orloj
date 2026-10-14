# Olomòc — site complet : constructeur (/builder/), tournois (/pairings/ et /api/tournaments),
# servis par l'hôte ASP.NET Core (Olomoc.Server). Tournois enregistrés dans /data (volume).
# Étape 1 : publication avec le SDK .NET ; étape 2 : runtime ASP.NET seul, HTTPS par Kestrel.
#
# Le domaine (certificat) est fourni au démarrage : variable d'environnement DOMAIN.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restauration seule d'abord : couche mise en cache tant que les .csproj ne changent pas.
COPY src/Olomoc.Shared/Olomoc.Shared.csproj src/Olomoc.Shared/
COPY src/Olomoc.Builder/Olomoc.Builder.csproj src/Olomoc.Builder/
COPY src/Olomoc.Pairings/Olomoc.Pairings.csproj src/Olomoc.Pairings/
COPY src/Olomoc.Server/Olomoc.Server.csproj src/Olomoc.Server/
RUN dotnet restore src/Olomoc.Server/Olomoc.Server.csproj

COPY src/ src/
RUN dotnet publish src/Olomoc.Server/Olomoc.Server.csproj -c Release -o /app --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app
COPY --from=build /app .

# Ports fixés par l'app (Hosting/Tls.cs) : 443 HTTPS, 80 HTTP (healthcheck, diagnostic local).
# Utilisateur root : la clé privée de Let's Encrypt de l'hôte n'est lisible que par root. Ses
# privilèges sont retirés par docker-compose.yml (cap_drop, no-new-privileges, read_only).
ENV ASPNETCORE_HTTP_PORTS=
ENV DATA_DIR=/data
VOLUME /data
EXPOSE 443 80
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1/healthz || exit 1
ENTRYPOINT ["dotnet", "Olomoc.Server.dll"]
