FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Hackathon.Assessment.Api/*.csproj src/Hackathon.Assessment.Api/
RUN dotnet restore src/Hackathon.Assessment.Api
COPY src/ src/
COPY prompts/ prompts/
RUN dotnet publish src/Hackathon.Assessment.Api -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ARG APP_VERSION=0.0.0-local
ARG GIT_COMMIT_SHA=local
ENV APP_VERSION=$APP_VERSION GIT_COMMIT_SHA=$GIT_COMMIT_SHA ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=6s --start-period=20s --retries=3 CMD ["dotnet", "Hackathon.Assessment.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Hackathon.Assessment.Api.dll"]
