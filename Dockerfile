FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["AiBox.DevPortal.csproj", "./"]
RUN dotnet restore "AiBox.DevPortal.csproj"

COPY . .
RUN dotnet publish "AiBox.DevPortal.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=14000
EXPOSE 14000

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AiBox.DevPortal.dll"]
