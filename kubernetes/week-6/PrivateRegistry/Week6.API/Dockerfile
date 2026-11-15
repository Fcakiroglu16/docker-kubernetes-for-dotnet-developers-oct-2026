FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080


FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Week6.API.csproj", "Week6.API/"]
RUN dotnet restore "Week6.API/Week6.API.csproj"
COPY . ./Week6.API
WORKDIR "/src/Week6.API"
RUN dotnet build "./Week6.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Week6.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
# Image'ı GitHub reposuna bağlar. GHCR paketi repo sayfasında görünür.
LABEL org.opencontainers.image.source="https://github.com/Fcakiroglu16/docker-kubernetes-for-dotnet-developers-oct-2026"
ARG APP_VERSION=dev
ENV APP_VERSION=$APP_VERSION
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Week6.API.dll"]
