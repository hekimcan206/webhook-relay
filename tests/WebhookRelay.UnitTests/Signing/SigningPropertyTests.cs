using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using WebhookRelay.Signing;

namespace WebhookRelay.UnitTests.Signing;

public sealed class SigningPropertyTests
{
    private const long Now = 1_800_000_000;
    private static readonly FixedClock Clock = FixedClock.AtUnix(Now);

    private static Arbitrary<byte[]> Keys() =>
        Gen.Choose(WebhookSecret.MinBytes, WebhookSecret.MaxBytes)
            .SelectMany(n => Gen.ArrayOf(ArbMap.Default.GeneratorFor<byte>(), n))
            .ToArbitrary();

    [Property(MaxTest = 300)]
    public Property Signed_messages_always_verify() =>
        Prop.ForAll(Keys(), ArbMap.Default.ArbFor<byte[]>(), ArbMap.Default.ArbFor<NonEmptyString>(), (key, body, id) =>
        {
            var secret = WebhookSecret.FromBytes(key);
            var header = WebhookSigner.SignatureHeader([secret], id.Get, Now, body);
            return new WebhookVerifier(secret, timeProvider: Clock)
                .Verify(id.Get, Now.ToString(CultureInfo.InvariantCulture), header, body).IsValid;
        });

    [Property(MaxTest = 300)]
    public Property Flipping_any_body_bit_breaks_the_signature() =>
        Prop.ForAll(Keys(), ArbMap.Default.ArbFor<NonEmptyArray<byte>>(), ArbMap.Default.ArbFor<int>(), (key, body, position) =>
        {
            var secret = WebhookSecret.FromBytes(key);
            var original = body.Get;
            var header = WebhookSigner.SignatureHeader([secret], "msg_x", Now, original);
            var tampered = (byte[])original.Clone();
            var index = (int)((uint)position % (uint)(tampered.Length * 8));
            tampered[index / 8] ^= (byte)(1 << (index % 8));
            return !new WebhookVerifier(secret, timeProvider: Clock)
                .Verify("msg_x", Now.ToString(CultureInfo.InvariantCulture), header, tampered).IsValid;
        });

    [Property(MaxTest = 200)]
    public Property A_signature_never_verifies_under_a_different_id_or_timestamp() =>
        Prop.ForAll(Keys(), Gen.Choose(1, 299).ToArbitrary(), (key, delta) =>
        {
            var secret = WebhookSecret.FromBytes(key);
            var verifier = new WebhookVerifier(secret, timeProvider: Clock);
            var header = WebhookSigner.SignatureHeader([secret], "msg_a", Now, "{}");
            return !verifier.Verify("msg_b", Now.ToString(CultureInfo.InvariantCulture), header, "{}").IsValid
                && !verifier.Verify("msg_a", (Now - delta).ToString(CultureInfo.InvariantCulture), header, "{}").IsValid;
        });

    [Property(MaxTest = 200)]
    public Property A_signature_never_verifies_under_another_secret() =>
        Prop.ForAll(Keys(), Keys(), (a, b) =>
        {
            var signer = WebhookSecret.FromBytes(a);
            var other = WebhookSecret.FromBytes(b);
            var header = WebhookSigner.SignatureHeader([signer], "msg_c", Now, "{}");
            var valid = new WebhookVerifier(other, timeProvider: Clock).Verify("msg_c", Now.ToString(CultureInfo.InvariantCulture), header, "{}").IsValid;
            return valid == signer.Equals(other);
        });

    [Property(MaxTest = 200)]
    public Property Secrets_round_trip_through_their_text_form() =>
        Prop.ForAll(Keys(), key => WebhookSecret.Parse(WebhookSecret.FromBytes(key).Encoded).Key.SequenceEqual(key));
}
