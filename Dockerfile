FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/FipeDados.Core/FipeDados.Core.csproj src/FipeDados.Core/
COPY src/FipeDados.Infrastructure/FipeDados.Infrastructure.csproj src/FipeDados.Infrastructure/
COPY src/FipeDados.Web/FipeDados.Web.csproj src/FipeDados.Web/
COPY src/FipeDados.Scraper/FipeDados.Scraper.csproj src/FipeDados.Scraper/
RUN dotnet restore src/FipeDados.Web/FipeDados.Web.csproj \
    && dotnet restore src/FipeDados.Scraper/FipeDados.Scraper.csproj
COPY src/ src/

FROM build AS web-publish
RUN dotnet publish src/FipeDados.Web/FipeDados.Web.csproj \
    -c Release -o /app/web --no-restore /p:UseAppHost=false

FROM build AS scraper-publish
RUN dotnet publish src/FipeDados.Scraper/FipeDados.Scraper.csproj \
    -c Release -o /app/scraper --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
USER $APP_UID

FROM runtime AS scraper
COPY --from=scraper-publish /app/scraper .
ENTRYPOINT ["dotnet", "FipeDados.Scraper.dll"]

FROM runtime AS web
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=web-publish /app/web .
ENTRYPOINT ["dotnet", "FipeDados.Web.dll"]
