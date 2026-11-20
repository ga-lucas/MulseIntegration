FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
RUN dotnet publish Service/Service.csproj -c "$BUILD_CONFIGURATION" -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p Data Runtime SampleData/Outbox \
    && chown "$APP_UID" Data Runtime SampleData/Outbox
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Service.dll"]
