var bld = WebApplication.CreateBuilder(args);
bld.Services
   .OpenApiDocument()
   .AddFastEndpoints();

var app = bld.Build();
app.UseFastEndpoints();
app.MapOpenApi();
app.MapScalarApiReference();
app.Run();

internal sealed class ReproEndpoint : Endpoint<ReproRequest>
{
    public override void Configure()
    {
        Post("/testing");
        AllowAnonymous();
    }

    public override Task HandleAsync(ReproRequest req, CancellationToken ct)
        => Send.NoContentAsync(ct);
}

internal record ReproRequest
{
    public required int[] IntArray { get; init; }
    public required double[] DoubleArray { get; init; }
    public required string[] StringArray { get; init; }
    public required ComplexType[] ComplexTypeArray { get; init; }
    public required List<int> IntList { get; init; }
    public required List<double> DoubleList { get; init; }
    public required List<string> StringList { get; init; }
    public required List<ComplexType> ComplexTypeList { get; init; }
}

internal record ComplexType
{
    public required int Int { get; init; }
    public required double Double { get; init; }
    public required string String { get; init; }
}