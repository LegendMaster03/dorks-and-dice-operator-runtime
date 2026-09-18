FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore DorksAndDice.OperatorRuntime.slnx
RUN dotnet publish src/DorksAndDice.OperatorRuntime/DorksAndDice.OperatorRuntime.csproj \
    --configuration Release \
    --no-restore \
    --output /out

FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble AS runtime
USER root
RUN curl -sSL https://dot.net/v1/dotnet-install.sh \
    | bash /dev/stdin --install-dir /usr/share/dotnet --channel 10.0 --runtime aspnetcore
WORKDIR /app
COPY --from=build /out/ ./
RUN chown -R pwuser:pwuser /app
USER pwuser
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DorksAndDice.OperatorRuntime.dll"]
