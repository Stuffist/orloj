# Olomòc — constructeur de cabale (Blazor WebAssembly = fichiers statiques).
# Étape 1 : publication avec le SDK .NET ; étape 2 : service HTTPS par nginx, sans runtime .NET.
#
# BASE_PATH : chemin public de l'app, avec « / » au début et à la fin (par défaut la racine).
# Le domaine (certificat) est fourni au démarrage : variable d'environnement DOMAIN.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restauration seule d'abord : couche mise en cache tant que le .csproj ne change pas.
COPY OlomocListBuilder.csproj .
RUN dotnet restore OlomocListBuilder.csproj

COPY . .
RUN dotnet publish OlomocListBuilder.csproj -c Release -o /app/publish --no-restore


FROM nginx:1.27-alpine AS final
ARG BASE_PATH=/

COPY --from=build /app/publish/wwwroot /usr/share/nginx/html${BASE_PATH}
# Modèle rendu au démarrage par l'image nginx (${DOMAIN}) vers /etc/nginx/conf.d/default.conf.
COPY docker/default.conf.template /etc/nginx/templates/default.conf.template
COPY docker/olomoc-app.conf /etc/nginx/olomoc-app.conf

# Chemin de base : <base href> de l'app (index.html n'est pas précompressé) et routes nginx.
RUN sed -i "s|<base href=\"/\" />|<base href=\"${BASE_PATH}\" />|" /usr/share/nginx/html${BASE_PATH}index.html \
 && sed -i "s|__BASE_PATH__|${BASE_PATH}|g" /etc/nginx/olomoc-app.conf \
 && grep -q "<base href=\"${BASE_PATH}\" />" /usr/share/nginx/html${BASE_PATH}index.html \
 && rm -f /etc/nginx/conf.d/default.conf

EXPOSE 443 80
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1/healthz || exit 1
