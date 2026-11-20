# Run Mulse with Docker Compose

Use Docker with Linux containers. Compose builds separate Service and UI images; you don't need Visual Studio or Aspire. The UI is published on loopback only at `https://localhost:7443`. Service is reachable from the UI at `http://service:8080` on the Compose network, not published to the host. This setup is for local evaluation, not a production deployment.

## Prepare TLS and local settings

From the repository root in PowerShell:

```powershell
Copy-Item .env.mulse.example .env.mulse
New-Item -ItemType Directory -Force .mulse-compose\plugins
```

Edit `.env.mulse` and replace the placeholder values with distinct secrets. Use the same `MULSE_TLS_PASSWORD` when exporting the certificate. For example, with the .NET SDK installed:

```powershell
dotnet dev-certs https --trust
dotnet dev-certs https --export-path .mulse-compose\mulse.pfx --password '<your PFX password>'
```

If you don't have the SDK installed, supply a password-protected PFX for `localhost` and trust its issuer in your browser. The UI's non-root container user (UID `1654`) must be able to read the mounted PFX. On Linux, grant that UID read access without making the private key world-readable. `.env.mulse` and `.mulse-compose` are ignored by Git and excluded from image builds. For Docker Compose, local secrets injected as environment variables are visible to users who control Docker.

## Use bundled Keycloak

Add `127.0.0.1 mulse-auth.test` to your host's hosts file (`C:\Windows\System32\drivers\etc\hosts` on Windows, `/etc/hosts` on Linux/macOS). Compose gives Keycloak the same name on its internal network, so the browser, UI and Service use the same issuer. If you change `MULSE_KEYCLOAK_PORT`, update the port in `MULSE_OIDC_AUTHORITY` as well.

```powershell
docker compose --env-file .env.mulse -f compose.yml -f compose.keycloak.yml up --build -d --wait
```

Open `https://localhost:7443` and sign in as **mulse-admin** with `MULSE_KEYCLOAK_USER_PASSWORD`. For the Keycloak administration console, use `http://mulse-auth.test:8180/admin/` and **mulse-bootstrap** with `MULSE_KEYCLOAK_ADMIN_PASSWORD`.

On an empty database, Keycloak imports the `mulse` realm, both OIDC clients, and their roles. It skips importing a realm that already exists. If you change passwords, client settings or callback ports later, update the existing realm in Keycloak as well; changing `.env.mulse` alone won't update it. This stack has its own PostgreSQL volume and does not reuse or restart the Keycloak stack from `compose.auth.yml` or Aspire. Bundled Keycloak runs in development mode over HTTP; keep the loopback bindings.

## Use an existing OIDC provider

Set `MULSE_OIDC_AUTHORITY` to the exact **HTTPS** issuer URL. Its hostname and endpoints must be reachable and trusted from both containers and from the browser; `localhost` inside a container refers to that container. Configure the OIDC clients as described in [Mulse OIDC authentication](feature-reference.md#mulse-oidc-authentication), using `https://localhost:7443/signin-oidc` and `https://localhost:7443/signout-callback-oidc` for the UI callback and post-logout redirect (adjust the port if changed). Set the client secrets in `.env.mulse` and override the client IDs, audience and role names if needed. The provider must issue the claims Mulse expects, including the API access role, audience and `azp` client identifier. The bundled Keycloak password settings are unused in this mode.

```powershell
docker compose --env-file .env.mulse -f compose.yml up --build -d
```

## Data, plugins, and shutdown

Mulse uses SQLite by default. Named volumes retain Service data, UI data and UI Data Protection keys; bundled mode adds its own Keycloak database. Back up the data and keys before replacing volumes. Use container-visible paths for file-based flows. To make host files available, mount them into Service with a local Compose override and give UID `1654` access.

For SpaceTrax, copy `Mulse.SpaceTrax.dll`, `Mulse.SpaceTrax.deps.json` and the package's private dependency DLLs into `.mulse-compose\plugins\Mulse.SpaceTrax\net11.0`. Don't copy `Mulse.Modules.dll`; Service supplies it. That plugin directory is mounted read-only. After startup, use **Modules > Module Packages > Load package**. Any native dependencies must support the container platform; any Windows paths in a flow need to be updated separately.

Use the same Compose files you started with and replace `up ...` with `stop` to pause the stack or `down` to remove its containers and network. Don't add `--volumes` unless you intend to delete its persisted data.
