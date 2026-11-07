# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only project files first so `dotnet restore` is cached until dependencies change.
COPY ShaadiLens.sln ./
COPY src/ShaadiLens.Api/ShaadiLens.Api.csproj                       src/ShaadiLens.Api/
COPY src/ShaadiLens.Application/ShaadiLens.Application.csproj       src/ShaadiLens.Application/
COPY src/ShaadiLens.Domain/ShaadiLens.Domain.csproj                 src/ShaadiLens.Domain/
COPY src/ShaadiLens.Infrastructure/ShaadiLens.Infrastructure.csproj src/ShaadiLens.Infrastructure/
COPY tests/ShaadiLens.Tests/ShaadiLens.Tests.csproj                 tests/ShaadiLens.Tests/
RUN dotnet restore ShaadiLens.sln

COPY . .
RUN dotnet publish src/ShaadiLens.Api/ShaadiLens.Api.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is only for the Docker healthcheck.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Photo storage volume mount point, owned by the non-root `app` user that ships with the image.
RUN mkdir -p /data/photos && chown app:app /data/photos
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShaadiLens.Api.dll"]
