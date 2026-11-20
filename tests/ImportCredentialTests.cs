using Azure.Core;
using Azure.Identity;
using static FiresideImport.ImportCommand;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using Xunit;

namespace Fireside.Tests;

public sealed class ImportCredentialTests
{
    private static readonly Guid Tenant = Guid.Parse("47f23e12-7b6c-432d-a818-028d31304ce8");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Keeps_subscription_account_selection_for_a_matching_service_tenant_challenge(bool asynchronous)
    {
        var inner = new RecordingCredential(Token(Tenant.ToString()));
        var credential = new SubscriptionTenantCredential(inner, Tenant);
        var request = new TokenRequestContext(["https://storage.azure.com/.default"],
            parentRequestId: "request", claims: "claims", isCaeEnabled: true, tenantId: Tenant.ToString());

        var result = asynchronous
            ? await credential.GetTokenAsync(request, default)
            : credential.GetToken(request, default);

        Assert.Equal(inner.Token, result);
        Assert.NotNull(inner.Request);
        Assert.Null(inner.Request.Value.TenantId);
        Assert.Equal(request.Scopes, inner.Request.Value.Scopes);
        Assert.Equal(request.Claims, inner.Request.Value.Claims);
        Assert.Equal(request.ParentRequestId, inner.Request.Value.ParentRequestId);
        Assert.True(inner.Request.Value.IsCaeEnabled);
    }

    [Fact]
    public void Rejects_a_different_service_tenant_before_requesting_a_token()
    {
        var inner = new RecordingCredential(Token(Tenant.ToString()));
        var credential = new SubscriptionTenantCredential(inner, Tenant);

        Assert.Throws<AuthenticationFailedException>(() => credential.GetToken(
            new TokenRequestContext(["scope"], tenantId: Guid.NewGuid().ToString()), default));

        Assert.Null(inner.Request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("b96df3c4-ce95-4756-b9f0-79a62118c8d1")]
    public async Task Rejects_an_issued_token_without_the_selected_tenant(string? tenant)
    {
        var credential = new SubscriptionTenantCredential(new RecordingCredential(Token(tenant)), Tenant);

        await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            credential.GetTokenAsync(new TokenRequestContext(["scope"]), default).AsTask());
    }

    [Fact]
    public void Rejects_a_malformed_token_without_exposing_its_contents()
    {
        const string material = "not-a-token";
        var credential = new SubscriptionTenantCredential(
            new RecordingCredential(new AccessToken(material, DateTimeOffset.UtcNow.AddMinutes(5))), Tenant);

        var error = Assert.Throws<AuthenticationFailedException>(() =>
            credential.GetToken(new TokenRequestContext(["scope"]), default));

        Assert.DoesNotContain(material, error.ToString());
    }

    private static AccessToken Token(string? tenant) => new(
        $"{Base64UrlEncoder.Encode("{\"alg\":\"none\"}")}." +
        $"{Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { tid = tenant }))}.test-signature",
        DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class RecordingCredential(AccessToken token) : TokenCredential
    {
        public AccessToken Token { get; } = token;
        public TokenRequestContext? Request { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Request = requestContext;
            return Token;
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
