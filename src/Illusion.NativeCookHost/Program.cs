using System.Numerics;
using System.Text.Json;
using Illusion.Formats.Navigation;

namespace Illusion.NativeCookHost;

public static class Program
{
    private sealed record Vertex(int Index, float X, float Y, float Z);
    private sealed record Request(string ExecutablePath, byte[] Resource, Vertex[]? Edits = null, string? Operation = null,
        int? Variant = null, bool StrictBounds = true, int? Edge = null, int? Face = null, Vertex? Position = null, byte[][]? Resources = null, bool MoveRepresentatives = false);
    private sealed record Response(bool Success, byte[]? Resource, string? Error, int Clusters = 0, int Edges = 0);
    private static int Main() => Run();
    public static int Run()
    {
        try
        {
            string input = Console.In.ReadToEnd();
            if (input.Length > 100_000_000) throw new InvalidDataException("Native cook request is too large.");
            var request = JsonSerializer.Deserialize<Request>(input) ?? throw new InvalidDataException("Missing native cook request.");
            if (request.Operation == "navigation_stream_split")
            {
                using var splitOracle = new NavigationOracle(request.ExecutablePath);
                var resources = splitOracle.CookStreamingEdgeSplit(request.Resources ?? throw new ArgumentException("Missing streaming resources."),
                    request.Edge ?? throw new ArgumentException("Missing streaming edge."), request.Position is { } p ? new Vector3(p.X, p.Y, p.Z) : null, request.MoveRepresentatives);
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Resources = resources }));
                return 0;
            }
            if (request.Operation == "navigation_crosscost_validate")
            {
                using var costOracle = new NavigationOracle(request.ExecutablePath);
                int connections = costOracle.ValidateStreamingCrossCosts(request.Resources ?? throw new ArgumentException("Missing streaming resources."));
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Connections = connections }));
                return 0;
            }
            if (request.Operation == "navigation_streaming_network")
            {
                using var network = new NavigationOracle(request.ExecutablePath);
                var networkEdits = (request.Edits ?? []).ToDictionary(vertex => vertex.Index, vertex => new Vector3(vertex.X, vertex.Y, vertex.Z));
                var resources = network.CookStreamingNetwork(request.Resources ?? throw new ArgumentException("Missing streaming resources."), networkEdits, request.MoveRepresentatives);
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Resources = resources }));
                return 0;
            }
            if (request.Operation == "navigation_streaming")
            {
                using var jointCook = new NavigationOracle(request.ExecutablePath);
                var jointEdits = (request.Edits ?? []).ToDictionary(v => v.Index, v => new Vector3(v.X, v.Y, v.Z));
                var resources = jointCook.CookStreamingPair(request.Resources ?? throw new ArgumentException("Missing streaming resources."), jointEdits, request.MoveRepresentatives);
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Resources = resources }));
                return 0;
            }
            if (request.Operation == "navigation_streaming_validate")
            {
                using var joint = new NavigationOracle(request.ExecutablePath);
                var report = joint.ValidateStreamingGraphs(request.Resources ?? throw new ArgumentException("Missing streaming resources."));
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Sections = report.Sections, Edges = report.Edges }));
                return 0;
            }
            if (request.Operation == "compound_validate")
            {
                using var compoundOracle = new NavigationOracle(request.ExecutablePath);
                int instances = compoundOracle.ValidateCompoundTransforms(request.Resource, request.Variant);
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Instances = instances }));
                return 0;
            }
            if (request.Operation == "physics_validate")
            {
                using var physicsOracle = new NavigationOracle(request.ExecutablePath);
                var report = physicsOracle.ValidatePhysics(request.Resource, request.Variant, request.StrictBounds);
                Console.Write(JsonSerializer.Serialize(new { Success = true, Error = (string?)null, Physics = report }));
                return 0;
            }
            if (request.Operation is not (null or "navigation" or "navigation_flip" or "navigation_subdivide" or "navigation_extrude")) throw new NotSupportedException("Unknown native cook operation.");
            var file = Mafia3NavMeshFile.Read(request.Resource);
            var edits = (request.Edits ?? []).ToDictionary(v => v.Index, v => new Vector3(v.X, v.Y, v.Z));
            var plan = request.Operation switch
            {
                "navigation_flip" => file.PrepareEdgeFlip(request.Edge ?? throw new ArgumentException("Missing navigation edge index.")),
                "navigation_subdivide" => file.PrepareFaceSubdivision(request.Face ?? throw new ArgumentException("Missing navigation face index."),
                    request.Position is { } point ? new Vector3(point.X, point.Y, point.Z) : null),
                "navigation_extrude" => file.PrepareBoundaryExtrusion(request.Edge ?? throw new ArgumentException("Missing navigation boundary edge index."),
                    request.Position is { } extension ? new Vector3(extension.X, extension.Y, extension.Z) : null),
                _ => file.PrepareInteriorDeformation(edits),
            };
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
