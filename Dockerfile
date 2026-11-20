# Builds and runs the Paysys API only (the Blazor site is a separate static site).
# Build context is the repository root:  docker build -t paysys-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/ src/
RUN dotnet publish src/Paysys.Api/Paysys.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .

# Render sends traffic to port 10000 by default. Locally, publish it with: -p 8080:10000
ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000

# Not root. The official image ships an unprivileged user, exposed as $APP_UID.
USER $APP_UID

ENTRYPOINT ["dotnet", "Paysys.Api.dll"]
