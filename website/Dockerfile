# Build from the repository root:  docker build -f website/Dockerfile .
FROM node:24-alpine AS website
WORKDIR /website
COPY website/package.json website/package-lock.json ./
RUN npm ci
COPY website/ ./
RUN npm run build

FROM caddy:2-alpine
COPY --from=website /website/dist /srv/site
# Baked in rather than mounted: the routing rules then version and roll back with the image, and a
# deployment that pulls images needs no checkout of this repository on the server.
COPY deploy/Caddyfile /etc/caddy/Caddyfile
