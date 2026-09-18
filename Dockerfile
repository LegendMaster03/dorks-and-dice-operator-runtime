FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore DorksAndDice.OperatorRuntime.slnx
RUN dotnet publish src/DorksAndDice.OperatorRuntime/DorksAndDice.OperatorRuntime.csproj \
    --configuration Release \
    --no-restore \
    --output /out

FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble AS runtime
WORKDIR /app
COPY --from=build --chown=pwuser:pwuser /out/ ./
USER pwuser
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DorksAndDice.OperatorRuntime.dll"]
