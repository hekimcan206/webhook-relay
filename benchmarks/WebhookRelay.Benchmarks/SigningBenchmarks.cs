using BenchmarkDotNet.Attributes;
using WebhookRelay.Signing;

namespace WebhookRelay.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class SigningBenchmarks
{
    private const string Id = "msg_0193f0a2b7c87f5e9d4c3b2a19087654";
    private readonly WebhookSecret _a = WebhookSecret.Generate();
    private readonly WebhookSecret _b = WebhookSecret.Generate();
    private byte[] _body = [];
    private string _header = "";
    private string _rotationHeader = "";
    private WebhookVerifier _verifier = null!;
    private string _timestamp = "";

    [Params(1024, 65536)]
    public int BodyBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _body = new byte[BodyBytes];
        Random.Shared.NextBytes(_body);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _timestamp = ts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _header = WebhookSigner.SignatureHeader([_a], Id, ts, _body);
        _rotationHeader = WebhookSigner.SignatureHeader([_b, _a], Id, ts, _body);
        _verifier = new WebhookVerifier(_a);
    }

    [Benchmark(Baseline = true)]
    public string Sign() => WebhookSigner.SignatureHeader([_a], Id, long.Parse(_timestamp, System.Globalization.CultureInfo.InvariantCulture), _body);

    [Benchmark]
    public string SignTwoSecrets() => WebhookSigner.SignatureHeader([_b, _a], Id, long.Parse(_timestamp, System.Globalization.CultureInfo.InvariantCulture), _body);

    [Benchmark]
    public bool Verify() => _verifier.Verify(Id, _timestamp, _header, _body).IsValid;

    [Benchmark]
    public bool VerifyRotationHeader() => _verifier.Verify(Id, _timestamp, _rotationHeader, _body).IsValid;
}
