FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY obsidian-graphrag-v2.Web/*.csproj obsidian-graphrag-v2.Web/
COPY obsidian-graphrag-v2.Shared/*.csproj obsidian-graphrag-v2.Shared/
RUN dotnet restore obsidian-graphrag-v2.Web/obsidian-graphrag-v2.Web.csproj
COPY obsidian-graphrag-v2.Web/ obsidian-graphrag-v2.Web/
COPY obsidian-graphrag-v2.Shared/ obsidian-graphrag-v2.Shared/
RUN dotnet publish obsidian-graphrag-v2.Web/obsidian-graphrag-v2.Web.csproj --no-restore -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish/ ./
RUN mkdir /knowledge && chown "$APP_UID" /knowledge
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "obsidian-graphrag-v2.Web.dll"]
