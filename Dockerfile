FROM mcr.microsoft.com/dotnet/sdk:6.0 as build-env

WORKDIR /app
COPY . ./
RUN dotnet publish ./k8s-deploy.csproj -c Release -o out --no-self-contained

# Label the container
LABEL maintainer="Asplan Viak - Digitale tjenester <support@avinet.no>"
LABEL repository="https://github.com/avinet/action-k8s-deploy"
LABEL homepage="https://github.com/dotnet/action-k8s-deploy"

LABEL com.github.actions.name="Deploy to Kubernetes cluster"
LABEL com.github.actions.description="Applies Kubernetes manifests and TOML configuration with template substitution to a cluster."
# https://docs.github.com/actions/creating-actions/metadata-syntax-for-github-actions#branding
LABEL com.github.actions.icon="upload-cloud"
LABEL com.github.actions.color="purple"

FROM mcr.microsoft.com/dotnet/sdk:6.0
COPY --from=build-env /app/out .
ENTRYPOINT [ "dotnet", "/k8s-deploy.dll" ]