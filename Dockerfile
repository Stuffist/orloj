# Orloj — site complet : constructeur (/builder/), tournois (/pairings/ et /api/tournaments),
# servis par l'hôte ASP.NET Core (Olomoc.Server). Tournois enregistrés dans /data (volume).
# Étape 1 : publication avec le SDK .NET ; étape 2 : runtime ASP.NET seul, en HTTP sur 8080.
# Le HTTPS est assuré par le reverse proxy (deploy/proxy : Caddy).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restauration seule d'abord : couche mise en cache tant que les .csproj ne changent pas.
COPY src/Olomoc.Shared/Olomoc.Shared.csproj src/Olomoc.Shared/
COPY src/Olomoc.Builder/Olomoc.Builder.csproj src/Olomoc.Builder/
COPY src/Olomoc.Pairings/Olomoc.Pairings.csproj src/Olomoc.Pairings/
COPY src/Olomoc.Server/Olomoc.Server.csproj src/Olomoc.Server/
RUN dotnet restore src/Olomoc.Server/Olomoc.Server.csproj

COPY src/ src/
RUN dotnet publish src/Olomoc.Server/Olomoc.Server.csproj -c Release -o /app --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app .

# HTTP sur 8080 (ASPNETCORE_HTTP_PORTS de l'image), utilisateur « app » sans privilèges
# (uid 1654) : le dossier ./data monté sur /data doit lui appartenir.
ENV DATA_DIR=/data
RUN mkdir /data && chown app /data
VOLUME /data
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Olomoc.Server.dll"]
