# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the dependency manifests first for better layer caching.
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/Inbix.Core/Inbix.Core.csproj src/Inbix.Core/
COPY src/Inbix.Data/Inbix.Data.csproj src/Inbix.Data/
COPY src/Inbix.Smtp/Inbix.Smtp.csproj src/Inbix.Smtp/
COPY src/Inbix.Worker/Inbix.Worker.csproj src/Inbix.Worker/
COPY src/Inbix.Web/Inbix.Web.csproj src/Inbix.Web/
RUN dotnet restore src/Inbix.Web/Inbix.Web.csproj

# Copy the rest of the sources and publish.
COPY src/ src/
RUN dotnet publish src/Inbix.Web/Inbix.Web.csproj -c Release -o /app --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Bind the web UI/API to 8080; SMTP port comes from configuration (defaults to 25).
ENV ASPNETCORE_URLS=http://+:8080
ENV Inbix__Database__ConnectionString="Data Source=/data/inbix.db"
ENV Inbix__Storage__RawPath="/data/raw"

# Persisted state (SQLite db + raw MIME/attachments).
VOLUME ["/data"]

# 8080 = web UI/API, 25 = inbound SMTP.
EXPOSE 8080
EXPOSE 25

ENTRYPOINT ["dotnet", "Inbix.Web.dll"]
