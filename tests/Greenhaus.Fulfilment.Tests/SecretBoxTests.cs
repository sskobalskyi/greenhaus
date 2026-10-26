using Greenhaus.Fulfilment.Infrastructure;

namespace Greenhaus.Fulfilment.Tests;

public class SecretBoxTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void A_sealed_value_opens_only_for_the_row_and_the_column_it_was_sealed_for()
    {
        var box = SecretBox.Create(Key, development: false);
        var id = Guid.NewGuid();
        var sealedValue = box.Seal("s3cret", SecretBox.ClaimTokenField, id, "ghx-1");

        Assert.True(box.TryOpen(sealedValue, SecretBox.ClaimTokenField, id, "ghx-1", out var plaintext));
        Assert.Equal("s3cret", plaintext);
        Assert.False(box.TryOpen(sealedValue, SecretBox.CredentialField, id, "ghx-1", out _));
        Assert.False(box.TryOpen(sealedValue, SecretBox.ClaimTokenField, Guid.NewGuid(), "ghx-1", out _));
        Assert.False(box.TryOpen(sealedValue, SecretBox.ClaimTokenField, id, "ghx-2", out _));
        Assert.False(box.TryOpen("s3cret", SecretBox.ClaimTokenField, id, "ghx-1", out _), "a value in the clear is not accepted as sealed");
        Assert.False(box.TryOpen(SecretBox.Prefix + "not base64!", SecretBox.ClaimTokenField, id, "ghx-1", out _));
        Assert.False(box.TryOpen(SecretBox.Prefix + "AAAA", SecretBox.ClaimTokenField, id, "ghx-1", out _));
    }

    [Fact]
    public void Another_key_does_not_open_a_sealed_value()
    {
        var id = Guid.NewGuid();
        var sealedValue = SecretBox.Create(Key, false).Seal("s3cret", SecretBox.CredentialField, id, "ghx-1");
        var other = SecretBox.Create(Convert.ToBase64String(new byte[32]), false);
        Assert.False(other.TryOpen(sealedValue, SecretBox.CredentialField, id, "ghx-1", out _));
    }

    [Fact]
    public void Outside_development_the_key_is_required_and_must_be_32_bytes_of_base64()
    {
        Assert.Throws<InvalidOperationException>(() => SecretBox.Create(null, development: false));
        Assert.Throws<InvalidOperationException>(() => SecretBox.Create("  ", development: false));
        Assert.Throws<InvalidOperationException>(() => SecretBox.Create("not base64!", development: false));
        Assert.Throws<InvalidOperationException>(() => SecretBox.Create(Convert.ToBase64String(new byte[16]), development: false));
        Assert.Throws<InvalidOperationException>(() => SecretBox.Create(Convert.ToBase64String(new byte[16]), development: true));

        Assert.False(SecretBox.Create(Key, development: false).Ephemeral);
        Assert.True(SecretBox.Create(null, development: true).Ephemeral, "in development a missing key is made up for the run");
    }

    [Fact]
    public void The_service_does_not_start_outside_development_without_a_key()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Program.Build(["--Auth:Mode=Keycloak", "--Auth:Authority=http://keycloak.invalid/realms/x"]));
        Assert.Contains("Fulfilment:SecretsKey", e.Message);
    }
}