FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY LeCrocodile.Reviews.Contracts/LeCrocodile.Reviews.Contracts.csproj LeCrocodile.Reviews.Contracts/
COPY LeCrocodile.Reviews.Api/LeCrocodile.Reviews.Api.csproj LeCrocodile.Reviews.Api/
COPY LeCrocodile.Reviews.Tests/LeCrocodile.Reviews.Tests.csproj LeCrocodile.Reviews.Tests/
RUN dotnet restore LeCrocodile.Reviews.Tests/LeCrocodile.Reviews.Tests.csproj
COPY LeCrocodile.Reviews.Contracts/ LeCrocodile.Reviews.Contracts/
COPY LeCrocodile.Reviews.Api/ LeCrocodile.Reviews.Api/
COPY LeCrocodile.Reviews.Tests/ LeCrocodile.Reviews.Tests/
RUN dotnet run --project LeCrocodile.Reviews.Tests -c Release --no-restore
RUN dotnet publish LeCrocodile.Reviews.Api/LeCrocodile.Reviews.Api.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir -p /data /backups && chown app:app /data /backups
COPY --from=build /out/ ./
USER app
EXPOSE 7443
ENTRYPOINT ["dotnet", "LeCrocodile.Reviews.Api.dll"]
