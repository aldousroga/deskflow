# Builds DeskFlow into a container image that Render (or any Docker-based host) can run directly.
# Two stages: the first ("build") has the full .NET SDK and compiles the app; the second ("final")
# only has the much smaller runtime and copies in just the compiled output - keeps the deployed
# image small and avoids shipping source code/build tools to production.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just the project file first and restore - Docker caches this layer, so re-running a build
# after only changing Program.cs/app.html doesn't re-download every NuGet package from scratch.
COPY DeskFlow.Api/DeskFlow.Api.csproj DeskFlow.Api/
RUN dotnet restore DeskFlow.Api/DeskFlow.Api.csproj

COPY DeskFlow.Api/ DeskFlow.Api/
WORKDIR /src/DeskFlow.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DeskFlow.Api.dll"]
