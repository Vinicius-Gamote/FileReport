FROM node:24.18.0-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d AS build
WORKDIR /web
COPY web/filereport-ui/package*.json ./
RUN npm ci
COPY web/filereport-ui .
RUN npm run build
FROM nginx:stable-alpine@sha256:97d490c12ba55b4946b01546d1c3ed324e8d41ab1c9fcb2a616aa470620e5b46
COPY config/nginx.conf /etc/nginx/nginx.conf
COPY --from=build /web/dist/filereport-ui/browser /usr/share/nginx/html
USER 101
EXPOSE 8080
ENTRYPOINT ["nginx"]
CMD ["-g", "daemon off;"]
