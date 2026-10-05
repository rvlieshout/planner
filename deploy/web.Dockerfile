# The `web` image: the website, the browser client, and the routing between them and the API.
#
# Build from the repository root:  docker build -f deploy/web.Dockerfile .
#
# One Node stage and no Node at runtime. The website is static files already and the client compiles
# to them, which Caddy serves from one container — so the client reaches the API over the same origin it was loaded from,
# and the deployment gains no runtime to patch.

FROM node:24-alpine AS client
WORKDIR /client
COPY client/package.json client/package-lock.json ./
RUN npm ci
COPY client/ ./
# The commit being built, baked into the client's status bar. There is no .git in this context for
# the build to ask, so the caller passes it; left empty, the bundle says 'unknown' rather than
# naming a commit it cannot know. Declared here, after `npm ci`, so a new commit does not invalidate
# the cached dependency install.
ARG PLANNER_BUILD_SHA=""
ENV PLANNER_BUILD_SHA=$PLANNER_BUILD_SHA
RUN npm run build

FROM caddy:2-alpine
COPY website/index.html website/changelog.json website/favicon.svg /srv/site/
COPY --from=client /client/build /srv/app
# Baked in rather than mounted: the routing rules then version and roll back with the image, and a
# deployment that pulls images needs no checkout of this repository on the server.
COPY deploy/Caddyfile /etc/caddy/Caddyfile
