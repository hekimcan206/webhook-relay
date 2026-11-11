# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY samples/receiver-csharp/ samples/receiver-csharp/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish samples/receiver-csharp/ReceiverSample.csproj --configuration Release --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build --link /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ReceiverSample.dll"]
