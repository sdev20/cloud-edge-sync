#!/usr/bin/env bash
# One-time Azure provisioning for SyncService.WebAPI's ACR + App Service pipeline.
# Review before running. Requires: az cli, logged in (`az login`) to the right subscription.
set -euo pipefail

# ---- Edit these before running ----
RESOURCE_GROUP="cloud-edge-sync-rg"
LOCATION="eastus"
ACR_NAME="cloudedgesyncacr"          # must be globally unique, alphanumeric only
APP_SERVICE_PLAN="cloud-edge-sync-plan"
WEBAPP_NAME="sync-service-webapi"    # must be globally unique (becomes <name>.azurewebsites.net)
GITHUB_ORG="<your-github-username>"
GITHUB_REPO="cloud-edge-sync"
# ------------------------------------

az group create --name "$RESOURCE_GROUP" --location "$LOCATION"

az acr create --resource-group "$RESOURCE_GROUP" --name "$ACR_NAME" --sku Basic

az appservice plan create --name "$APP_SERVICE_PLAN" --resource-group "$RESOURCE_GROUP" \
  --is-linux --sku B1

# Starts on a placeholder public image; the CI pipeline overwrites this on first deploy.
az webapp create --resource-group "$RESOURCE_GROUP" --plan "$APP_SERVICE_PLAN" \
  --name "$WEBAPP_NAME" --deployment-container-image-name mcr.microsoft.com/dotnet/aspnet:10.0

# Managed identity for the Web App, granted AcrPull on the registry (no admin credentials needed).
az webapp identity assign --resource-group "$RESOURCE_GROUP" --name "$WEBAPP_NAME"
PRINCIPAL_ID=$(az webapp identity show --resource-group "$RESOURCE_GROUP" --name "$WEBAPP_NAME" \
  --query principalId -o tsv)
ACR_ID=$(az acr show --name "$ACR_NAME" --query id -o tsv)
az role assignment create --assignee "$PRINCIPAL_ID" --role AcrPull --scope "$ACR_ID"
az webapp config set --resource-group "$RESOURCE_GROUP" --name "$WEBAPP_NAME" \
  --generic-configurations '{"acrUseManagedIdentityCreds": true}'

# App Registration for GitHub Actions OIDC (no stored client secret).
APP_ID=$(az ad app create --display-name "cloud-edge-sync-github-actions" --query appId -o tsv)
az ad sp create --id "$APP_ID"

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
az role assignment create --assignee "$APP_ID" --role AcrPush --scope "$ACR_ID"
az role assignment create --assignee "$APP_ID" --role "Website Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"

az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-main-branch",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GITHUB_ORG"'/'"$GITHUB_REPO"':ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'

echo ""
echo "Provisioning done. Add these as GitHub Actions repo secrets (Settings > Secrets and"
echo "variables > Actions) — do NOT paste them into chat:"
echo "  AZURE_CLIENT_ID       = $APP_ID"
echo "  AZURE_TENANT_ID       = $(az account show --query tenantId -o tsv)"
echo "  AZURE_SUBSCRIPTION_ID = $SUBSCRIPTION_ID"
