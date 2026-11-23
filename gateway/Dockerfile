# Builds and runs the payment gateway only.
# Build context is the gateway folder:  docker build -t paysys-gateway gateway/

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Gateway.Api/ Gateway.Api/
RUN dotnet publish Gateway.Api/Gateway.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .

ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000

USER $APP_UID

ENTRYPOINT ["dotnet", "Gateway.Api.dll"]
