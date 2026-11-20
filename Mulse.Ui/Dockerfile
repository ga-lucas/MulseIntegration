FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Mulse.Ui/Mulse.Ui.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys /app/Data \
	&& chown -R "$APP_UID" /home/app/.aspnet /app/Data
USER $APP_UID
EXPOSE 8443
ENTRYPOINT ["dotnet", "Mulse.Ui.dll"]
