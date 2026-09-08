FROM node:24-alpine AS website
WORKDIR /website
COPY website/package.json website/package-lock.json ./
RUN npm ci
COPY website/ ./
RUN npm run build

FROM caddy:2-alpine
COPY --from=website /website/dist /srv/site
