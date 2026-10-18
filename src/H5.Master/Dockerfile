# H5Reborn master server (online lobby list, join codes, NAT rendezvous, relay).
# Build from the repository root:
#   docker build -f src/H5.Master/Dockerfile -t h5reborn-master .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/H5.Net/H5.Net.csproj src/H5.Net/
COPY src/H5.Master/H5.Master.csproj src/H5.Master/
RUN dotnet restore src/H5.Master/H5.Master.csproj
COPY src/H5.Net/ src/H5.Net/
COPY src/H5.Master/ src/H5.Master/
RUN dotnet publish src/H5.Master/H5.Master.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# HTTP API + WebSockets (put a TLS reverse proxy in front for https/wss), UDP rendezvous/relay.
ENV H5_MASTER_URLS=http://0.0.0.0:27620 \
    H5_MASTER_UDP_PORT=27621 \
    H5_MASTER_UDP_BIND=0.0.0.0
EXPOSE 27620/tcp 27621/udp
USER app
ENTRYPOINT ["dotnet", "h5master.dll"]
