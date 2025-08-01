// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Azure;
using Azure.AI.Projects;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using Azure.ResourceManager.Resources;
using AzureMcp.Areas.Foundry.Commands;
using AzureMcp.Areas.Foundry.Models;
using AzureMcp.Options;
using AzureMcp.Services.Azure;

namespace AzureMcp.Areas.Foundry.Services;

public class FoundryService : BaseAzureService, IFoundryService
{
    public async Task<List<ModelInformation>> ListModels(
        bool searchForFreePlayground = false,
        string publisherName = "",
        string licenseName = "",
        string modelName = "",
        int maxPages = 3,
        RetryPolicyOptions? retryPolicy = null)
    {
        string url = "https://api.catalog.azureml.ms/asset-gallery/v1.0/models";
        var request = new ModelCatalogRequest { Filters = [new ModelCatalogFilter("labels", ["latest"], "eq")] };

        if (searchForFreePlayground)
        {
            request.Filters.Add(new ModelCatalogFilter("freePlayground", ["true"], "eq"));
        }

        if (!string.IsNullOrEmpty(publisherName))
        {
            request.Filters.Add(new ModelCatalogFilter("publisher", [publisherName], "contains"));
        }

        if (!string.IsNullOrEmpty(licenseName))
        {
            request.Filters.Add(new ModelCatalogFilter("license", [licenseName], "contains"));
        }

        if (!string.IsNullOrEmpty(modelName))
        {
            request.Filters.Add(new ModelCatalogFilter("name", [modelName], "eq"));
        }

        var modelsList = new List<ModelInformation>();
        int pageCount = 0;

        try
        {
            while (pageCount < maxPages)
            {
                pageCount++;
                try
                {
                    var content = new StringContent(
                        JsonSerializer.Serialize(request, FoundryJsonContext.Default.ModelCatalogRequest),
                        Encoding.UTF8,
                        "application/json");

                    var httpResponse = await new HttpClient().PostAsync(url, content);
                    httpResponse.EnsureSuccessStatusCode();

                    var responseText = await httpResponse.Content.ReadAsStringAsync();
                    var response = JsonSerializer.Deserialize(responseText,
                        FoundryJsonContext.Default.ModelCatalogResponse);
                    if (response == null || response.Summaries.Count == 0)
                    {
                        break;
                    }

                    foreach (var summary in response.Summaries)
                    {
                        try
                        {
                            summary.DeploymentInformation.IsFreePlayground = summary.PlaygroundLimits != null;
                            if (!string.IsNullOrEmpty(summary.Publisher) &&
                                summary.Publisher.Equals("openai", StringComparison.OrdinalIgnoreCase))
                            {
                                summary.DeploymentInformation.IsOpenAI = true;
                            }
                            else
                            {
                                if (summary.AzureOffers != null)
                                {
                                    summary.DeploymentInformation.IsServerlessEndpoint =
                                        summary.AzureOffers.Contains("standard-paygo");

                                    summary.DeploymentInformation.IsManagedCompute =
                                        summary.AzureOffers.Contains("VM") ||
                                        summary.AzureOffers.Contains("VM-withSurcharge");
                                }
                            }

                            modelsList.Add(summary);
                        }
                        catch
                        {
                            // ignored
                        }
                    }

                    if (string.IsNullOrEmpty(response.ContinuationToken))
                    {
                        break;
                    }

                    request.ContinuationToken = response.ContinuationToken;
                }
                catch (HttpRequestException)
                {
                    break;
                }
                catch (JsonException)
                {
                    break;
                }
            }
        }
        catch (Exception e)
        {
            throw new Exception($"Error retrieving models from model catalog: {e.Message}");
        }

        return modelsList;
    }

    public async Task<List<Deployment>> ListDeployments(string endpoint, string? tenantId = null, RetryPolicyOptions? retryPolicy = null)
    {
        ValidateRequiredParameters(endpoint);

        try
        {
            var credential = await GetCredential(tenantId);
            var deploymentsClient = new AIProjectClient(new Uri(endpoint), credential).GetDeploymentsClient();

            var deployments = new List<Deployment>();
            await foreach (var deployment in deploymentsClient.GetDeploymentsAsync())
            {
                deployments.Add(deployment);
            }

            return deployments;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to list deployments: {ex.Message}", ex);
        }
    }

    public async Task<Dictionary<string, object>> DeployModel(string deploymentName, string modelName, string modelFormat,
        string azureAiServicesName, string resourceGroup, string subscriptionId, string? modelVersion = null, string? modelSource = null,
        string? skuName = null, int? skuCapacity = null, string? scaleType = null, int? scaleCapacity = null, RetryPolicyOptions? retryPolicy = null)
    {
        ValidateRequiredParameters(deploymentName, modelName, modelFormat, azureAiServicesName, resourceGroup, subscriptionId);

        try
        {
            ArmClient armClient = await CreateArmClientAsync(null, retryPolicy);

            var subscription =
                armClient.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));
            var resourceGroupResource = await subscription.GetResourceGroupAsync(resourceGroup);

            var cognitiveServicesAccounts = resourceGroupResource.Value.GetCognitiveServicesAccounts();
            var cognitiveServicesAccount = await cognitiveServicesAccounts.GetAsync(azureAiServicesName);

            var deploymentData = new CognitiveServicesAccountDeploymentData
            {
                Properties = new CognitiveServicesAccountDeploymentProperties
                {
                    Model = new CognitiveServicesAccountDeploymentModel
                    {
                        Format = modelFormat,
                        Name = modelName,
                        Version = modelVersion
                    }
                }
            };

            if (!string.IsNullOrEmpty(modelSource))
            {
                deploymentData.Properties.Model.Source = modelSource;
            }

            if (!string.IsNullOrEmpty(skuName))
            {
                deploymentData.Sku = new CognitiveServicesSku(skuName);
                if (skuCapacity.HasValue)
                {
                    deploymentData.Sku.Capacity = skuCapacity;
                }
            }

            if (!string.IsNullOrEmpty(scaleType))
            {
                deploymentData.Properties.ScaleSettings = new CognitiveServicesAccountDeploymentScaleSettings
                {
                    ScaleType = scaleType,
                    Capacity = scaleCapacity
                };
            }

            var deploymentOperation = await cognitiveServicesAccount.Value.GetCognitiveServicesAccountDeployments()
                .CreateOrUpdateAsync(waitUntil: WaitUntil.Completed, deploymentName, deploymentData);

            CognitiveServicesAccountDeploymentResource deployment = deploymentOperation.Value;

            if (!deployment.HasData)
            {
                return new Dictionary<string, object>
                {
                    { "has_data", false },
                };
            }

            // Manually converting system data to a dictionary due to lack of available JsonSerializer support
            return new Dictionary<string, object>
            {
                { "has_data", true },
                { "id", deployment.Data.Id.ToString() },
                { "name", deployment.Data.Name },
                { "type", deployment.Data.ResourceType.ToString() },
                { "sku", deployment.Data.Sku },
                { "tags", deployment.Data.Tags },
                { "properties", deployment.Data.Properties },
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to deploy model: {ex.Message}", ex);
        }
    }
}
