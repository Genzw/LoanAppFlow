FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/LoanApp.Core/LoanApp.Core.csproj src/LoanApp.Core/packages.lock.json src/LoanApp.Core/
COPY src/LoanApp.Api/LoanApp.Api.csproj src/LoanApp.Api/packages.lock.json src/LoanApp.Api/
RUN dotnet restore src/LoanApp.Api/LoanApp.Api.csproj --locked-mode
COPY src/LoanApp.Core/ src/LoanApp.Core/
COPY src/LoanApp.Api/ src/LoanApp.Api/
RUN dotnet publish src/LoanApp.Api/LoanApp.Api.csproj -c Release --no-restore -p:UseAppHost=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS runtime
WORKDIR /app
COPY --from=build /app/ ./
COPY --chmod=0555 deploy/entrypoint.sh /entrypoint.sh
ENV ASPNETCORE_ENVIRONMENT=Production
USER app
EXPOSE 8080
ENTRYPOINT ["/bin/sh", "/entrypoint.sh", "LoanApp.Api.dll"]
