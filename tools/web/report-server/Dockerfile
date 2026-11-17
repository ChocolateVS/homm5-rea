# H5Reborn bug-report receiver (docs/WEB_BUILD.md, "Bug reports"). No npm dependencies.
#   docker build -t h5reborn-reports tools/web/report-server
FROM node:22-alpine
WORKDIR /app
COPY package.json server.mjs ./
ENV H5R_REPORTS_DIR=/data H5R_REPORTS_PORT=8170 NODE_ENV=production
RUN mkdir -p /data && chown node:node /data
USER node
VOLUME /data
EXPOSE 8170
HEALTHCHECK --interval=60s --timeout=5s CMD wget -qO- http://127.0.0.1:8170/api/health || exit 1
CMD ["node", "server.mjs"]
