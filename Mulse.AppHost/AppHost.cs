using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var keycloakPort = builder.Configuration.GetValue("LocalAuth:Port", 8081);
var keycloakUrl = $"http://localhost:{keycloakPort}";
var authority = $"{keycloakUrl}/realms/mulse";

var service = builder.AddProject<Projects.Service>("service", "Service")
    .WithEnvironment("Authentication__Authority", authority);

if (builder.Configuration.GetValue("LocalAuth:UseExternalKeycloak", false))
{
    var keycloak = builder.AddExternalService("keycloak", keycloakUrl)
        .WithHttpHealthCheck("/realms/mulse/.well-known/openid-configuration");
    service.WaitFor(keycloak);
}
else
{
    var databasePassword = builder.AddParameter("keycloak-db-password", secret: true);
    var adminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
    var adminUsername = builder.Configuration["LocalAuth:AdminUsername"] ?? "mulse-admin";
    var databaseVolume = builder.Configuration["LocalAuth:DatabaseVolume"] ?? "mulse-aspire-keycloak-db-data";

    var databaseUsername = builder.AddParameter("keycloak-db-username", "keycloak");
    var keycloakDb = builder.AddPostgres("keycloak-db", userName: databaseUsername, password: databasePassword)
        .WithImageTag("18.3")
        .WithEnvironment("POSTGRES_DB", "keycloak")
        .WithVolume(databaseVolume, "/var/lib/postgresql");

    var keycloak = builder.AddContainer("keycloak", "keycloak/keycloak", "26.6")
        .WithImageRegistry("quay.io")
        .WithArgs("start-dev")
        .WithEnvironment("KC_HOSTNAME", keycloakUrl)
        .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", adminUsername)
        .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", adminPassword)
        .WithEnvironment("KC_DB", "postgres")
        .WithEnvironment("KC_DB_URL", "jdbc:postgresql://keycloak-db:5432/keycloak")
        .WithEnvironment("KC_DB_USERNAME", databaseUsername)
        .WithEnvironment("KC_DB_PASSWORD", databasePassword)
        .WithHttpEndpoint(port: keycloakPort, targetPort: 8080, name: "http")
        .WithHttpHealthCheck("/realms/master/.well-known/openid-configuration")
        .WaitFor(keycloakDb);

    service.WaitFor(keycloak);
}

builder.AddProject<Projects.Mulse_Ui>("ui", "https")
    .WithReference(service)
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("MulseApi__BaseUrl", service.GetEndpoint("http"))
    .WaitFor(service);

builder.Build().Run();
