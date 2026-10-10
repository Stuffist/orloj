# Olomòc — constructeur de cabale (Blazor WebAssembly = fichiers statiques).
# Étape 1 : publication avec le SDK .NET ; étape 2 : service par nginx, sans runtime .NET.
#
# BASE_PATH : chemin public de l'app, avec « / » au début et à la fin. L'app est servie
# sous ce chemin aussi dans le conteneur : le reverse proxy le transmet tel quel.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restauration seule d'abord : couche mise en cache tant que le .csproj ne change pas.
COPY OlomocListBuilder.csproj .
RUN dotnet restore OlomocListBuilder.csproj

COPY . .
RUN dotnet publish OlomocListBuilder.csproj -c Release -o /app/publish --no-restore


FROM nginx:1.27-alpine AS final
ARG BASE_PATH=/olomoc/

COPY --from=build /app/publish/wwwroot /usr/share/nginx/html${BASE_PATH}
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf

# Chemin de base : <base href> de l'app (index.html n'est pas précompressé) et routes nginx.
RUN sed -i "s|<base href=\"/\" />|<base href=\"${BASE_PATH}\" />|" /usr/share/nginx/html${BASE_PATH}index.html \
 && sed -i "s|__BASE_PATH__|${BASE_PATH}|g" /etc/nginx/conf.d/default.conf \
 && grep -q "<base href=\"${BASE_PATH}\" />" /usr/share/nginx/html${BASE_PATH}index.html \
 && nginx -t

EXPOSE 80
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1/healthz || exit 1
