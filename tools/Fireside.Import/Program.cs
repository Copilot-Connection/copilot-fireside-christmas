using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Fireside;
using Microsoft.Azure.Cosmos;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;

namespace FiresideImport;

public static class ImportCommand
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--import" or "--verify"))
        {
            Console.Error.WriteLine("Usage: Fireside.Import (--import | --verify) <source-sqlite-file>");
            return 2;
        }
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Set {name} outside source control.");
        if (Required("FIRESIDE_SOURCE_WRITES_PAUSED") != "true")
            throw new InvalidOperationException("Stop source and target event writes, take a verified backup, then explicitly confirm FIRESIDE_SOURCE_WRITES_PAUSED=true.");
        var endpoint = new Uri(Required("Cosmos__Endpoint"));
        var blobEndpoint = new Uri(Required("Storage__BlobServiceUri"));
        if (endpoint.Scheme != "https" || blobEndpoint.Scheme != "https")
            throw new InvalidOperationException("Cloud data endpoints must use HTTPS.");
        var tenant = Guid.Parse(Required("AZURE_TENANT_ID"));
        var subscription = Guid.Parse(Required("AZURE_SUBSCRIPTION_ID"));
        var credential = new SubscriptionTenantCredential(
            new AzureCliCredential(new AzureCliCredentialOptions { Subscription = subscription.ToString() }), tenant);
        using var cosmos = new CosmosClient(endpoint.AbsoluteUri, credential, new CosmosClientOptions
        {
            Serializer = new EventCosmosSerializer(), ConsistencyLevel = ConsistencyLevel.Strong,
            ConnectionMode = ConnectionMode.Gateway
        });
        var container = cosmos.GetContainer(Required("Cosmos__Database"), Required("Cosmos__Container"));
        var images = new BlobEventImages(new BlobServiceClient(blobEndpoint, credential)
            .GetBlobContainerClient(Required("Storage__Container")));
        var archive = await EventArchive.Capture(args[1], CancellationToken.None);
        var transfer = new CosmosEventTransfer(container, Required("Cosmos__EventId"), images);
        if (args[0] == "--import") await transfer.Import(archive, CancellationToken.None);
        else await transfer.Verify(archive, CancellationToken.None);
        Console.WriteLine($"Verified: {archive.Data.Panellists.Count} panellists, {archive.Data.Questions.Count} questions, " +
            $"{archive.Votes.Count} votes, {archive.Adjustments.Count} adjustments, {archive.Images.Count} images. " +
            $"Event digest: {EventArchive.Digest(archive.Data)}");
        return 0;
    }

    internal sealed class SubscriptionTenantCredential(TokenCredential inner, Guid tenant) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            Validate(inner.GetToken(Prepare(requestContext), cancellationToken));

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            Validate(await inner.GetTokenAsync(Prepare(requestContext), cancellationToken).ConfigureAwait(false));

        private TokenRequestContext Prepare(TokenRequestContext request)
        {
            if (!string.IsNullOrEmpty(request.TenantId) &&
                (!Guid.TryParse(request.TenantId, out var requested) || requested != tenant))
                throw new AuthenticationFailedException("The service requested a tenant outside AZURE_TENANT_ID.");
            // CLI cannot combine tenant and subscription. Check the challenge and returned token
            // while keeping subscription-based account selection, including Blob's tenant challenge.
            return new TokenRequestContext(request.Scopes, parentRequestId: request.ParentRequestId,
                claims: request.Claims, isCaeEnabled: request.IsCaeEnabled);
        }

        private AccessToken Validate(AccessToken token)
        {
            const string message = "The operator credential did not return a valid token for AZURE_TENANT_ID.";
            try
            {
                var parts = token.Token.Split('.');
                if (parts.Length != 3) throw new AuthenticationFailedException(message);
                using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
                if (payload.RootElement.ValueKind != JsonValueKind.Object ||
                    !payload.RootElement.TryGetProperty("tid", out var tid) || tid.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(tid.GetString(), out var actual) || actual != tenant)
                    throw new AuthenticationFailedException(message);
                return token;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException)
            {
                throw new AuthenticationFailedException(message);
            }
        }
    }
}
