using Azure.Identity;
using Azure.Storage.Blobs;
using Fireside;
using Microsoft.Azure.Cosmos;

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
        var credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = Required("AZURE_TENANT_ID") });
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
}
