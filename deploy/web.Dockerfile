# The `web` image: the download website, the browser client, and the routing between them and the API.
#
# Build from the repository root:  docker build -f deploy/web.Dockerfile .
#
# Two Node stages and no Node at runtime. Both applications compile to static files, which Caddy
# serves from one container — so the client reaches the API over the same origin it was loaded from,
# and the deployment gains no runtime to patch.

FROM node:24-alpine AS website
WORKDIR /website
COPY website/package.json website/package-lock.json ./
RUN npm ci
COPY website/ ./
RUN npm run build

FROM node:24-alpine AS client
WORKDIR /client
COPY client/package.json client/package-lock.json ./
RUN npm ci
COPY client/ ./
RUN npm run build

FROM caddy:2-alpine
COPY --from=website /website/dist /srv/site
COPY --from=client /client/build /srv/app
# Baked in rather than mounted: the routing rules then version and roll back with the image, and a
# deployment that pulls images needs no checkout of this repository on the server.
COPY deploy/Caddyfile /etc/caddy/Caddyfile
