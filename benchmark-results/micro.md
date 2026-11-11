# Signing micro-benchmarks (BenchmarkDotNet 0.15.8, ShortRun, Release)

    BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
    13th Gen Intel Core i7-13620H 2.40GHz, 1 CPU, 16 logical and 10 physical cores
    .NET SDK 10.0.401
      [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

| Method               | BodyBytes | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------- |---------- |----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| Sign                 | 1024      |  1.432 us |  0.4394 us | 0.0241 us |  1.00 |    0.02 | 0.0744 |     944 B |        1.00 |
| SignTwoSecrets       | 1024      |  2.778 us |  1.1902 us | 0.0652 us |  1.94 |    0.05 | 0.1678 |    2128 B |        2.25 |
| Verify               | 1024      |  1.579 us |  0.9114 us | 0.0500 us |  1.10 |    0.03 | 0.0343 |     432 B |        0.46 |
| VerifyRotationHeader | 1024      |  1.894 us |  0.1042 us | 0.0057 us |  1.32 |    0.02 | 0.0534 |     680 B |        0.72 |
|                      |           |           |            |           |       |         |        |           |             |
| Sign                 | 65536     | 30.645 us | 17.3014 us | 0.9483 us |  1.00 |    0.04 | 0.0610 |     944 B |        1.00 |
| SignTwoSecrets       | 65536     | 61.522 us | 42.2678 us | 2.3168 us |  2.01 |    0.08 | 0.1221 |    2128 B |        2.25 |
| Verify               | 65536     | 30.796 us |  6.2078 us | 0.3403 us |  1.01 |    0.03 | 0.0305 |     432 B |        0.46 |
| VerifyRotationHeader | 65536     | 32.350 us |  6.9714 us | 0.3821 us |  1.06 |    0.03 |      - |     680 B |        0.72 |
