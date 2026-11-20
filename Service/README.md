# Service container publishing

The Service image runs on a Linux container engine: natively on Linux, or through Docker Desktop in Linux container mode on Windows or macOS. This does not change how you run the .NET project directly on Windows. The Keycloak and PostgreSQL images used by Aspire and `compose.auth.yml` already run on the same Linux engine, including on x64 and ARM64.

## Build and run with Docker

From the repository root:

```powershell
docker build -f Service\Dockerfile -t mulse-service .
docker run --rm -p 127.0.0.1:5210:8080 --mount type=volume,source=mulse-service-data,target=/app/Data -e ASPNETCORE_ENVIRONMENT=Development mulse-service
```

This loopback-only example lets you check `http://localhost:5210/health` and persists the default SQLite database in `mulse-service-data`. It does not configure sign-in. For authenticated calls, set `Authentication__Authority` to the issuer that issued the tokens, and ensure the Service container can reach that issuer's discovery and signing-key endpoints. `localhost` inside Service refers to that container, not Keycloak or the Docker host. Use HTTPS for the issuer outside Development.

The image runs as a non-root user (`1654`). Make bind-mounted data and output directories writable by that user. Flow paths must refer to files the container can see; mount those files and replace any host-only paths in existing flows. To use a plugin, mount its package directory under `/app/plugins` and ensure it and any native dependencies support the container platform.

## Publish an archive with the .NET SDK

Create a Linux x64 archive without a local Docker daemon (`linux-arm64` targets ARM64):

```powershell
dotnet publish Service\Service.csproj -c Release -r linux-x64 --self-contained false -t:PublishContainer -p:ContainerArchiveOutputPath=mulse-service.tar.gz
docker load --input mulse-service.tar.gz
```

The SDK image does not pre-create writable runtime directories. For a disposable smoke test, mount temporary filesystems for these paths:

```powershell
docker run --rm -p 127.0.0.1:5210:8080 -e ASPNETCORE_ENVIRONMENT=Development --tmpfs /app/Data:uid=1654,mode=0700 --tmpfs /app/Runtime:uid=1654,mode=0700 --tmpfs /app/SampleData/Outbox:uid=1654,mode=0700 mulse-service:latest
```

For persistence, replace the `/app/Data` tmpfs with a volume writable by UID `1654`, or use the Dockerfile build, which initializes the directory for a new named volume.
