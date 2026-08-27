FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SyncService.Domain/SyncService.Domain.csproj SyncService.Domain/
COPY SyncService.DomainServices/SyncService.DomainServices.csproj SyncService.DomainServices/
COPY SyncService.Infrastructure.Client/SyncService.Infrastructure.Client.csproj SyncService.Infrastructure.Client/
COPY SyncService.WebAPI/SyncService.WebAPI.csproj SyncService.WebAPI/

RUN dotnet restore SyncService.WebAPI/SyncService.WebAPI.csproj

COPY . .
RUN dotnet publish SyncService.WebAPI/SyncService.WebAPI.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SyncService.WebAPI.dll"]
