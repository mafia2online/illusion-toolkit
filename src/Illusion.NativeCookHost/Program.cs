using System.Numerics;
using System.Text.Json;
using Illusion.Formats.Navigation;

namespace Illusion.NativeCookHost;

public static class Program
{
    private sealed record Vertex(int Index, float X, float Y, float Z);
    private sealed record Request(string ExecutablePath, byte[] Resource, Vertex[]? Edits = null, string? Operation = null,
        int? Variant = null, bool StrictBounds = true);
    private sealed record Response(bool Success, byte[]? Resource, string? Error, int Clusters = 0, int Edges = 0);
    private static int Main() => Run();
    public static int Run()
    {
        try
        {
            string input = Console.In.ReadToEnd();
            if (input.Length > 100_000_000) throw new InvalidDataException("Native cook request is too large.");
            var request = JsonSerializer.Deserialize<Request>(input) ?? throw new InvalidDataException("Missing native cook request.");
            if (request.Operation == "physics_validate")
            {
                using var physicsOracle = new NavigationOracle(request.ExecutablePath);
                var report = physicsOracle.ValidatePhysics(request.Resource, request.Variant, request.StrictBounds);
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Physics = report }));
                return 0;
            }
            if (request.Operation is not (null or "navigation")) throw new NotSupportedException("Unknown native cook operation.");
            var file = Mafia3NavMeshFile.Read(request.Resource);
            var edits = (request.Edits ?? []).ToDictionary(v => v.Index, v => new Vector3(v.X, v.Y, v.Z));
            var plan = file.PrepareInteriorDeformation(edits);
            using var oracle = new NavigationOracle(request.ExecutablePath);
            byte[] result = oracle.Cook(plan);
            Console.Write(JsonSerializer.Serialize(new Response(true, result, null, plan.ClusterCount, plan.GraphEdgeCount)));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Write(JsonSerializer.Serialize(new Response(false, null, ex.Message)));
            return 1;
        }
    }
}
