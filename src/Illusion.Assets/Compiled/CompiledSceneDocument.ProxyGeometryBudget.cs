using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // Include staged ownership registrations, schemas, class counts and variable buffers. Measuring
    // only Resource.ToArray would omit detached definitions inserted by the document rewrite plan.
    private long ProxyGeometrySerializedSize(CompiledResource resource)
    {
        var source = EffectivePlans().GetValueOrDefault(resource); var plan = new CompiledRewrite { AllowNativeAllocatorFallback = true };
        if (source is not null)
        {
            plan.AllocationBudget = source.AllocationBudget; plan.SchemaStreamIndex = source.SchemaStreamIndex;
            foreach (var pair in source.Children) plan.Children.Add(pair.Key, pair.Value);
            foreach (var pair in source.Values) plan.Values.Add(pair.Key, pair.Value);
            foreach (var pair in source.AdditionalSchemas) plan.AdditionalSchemas.Add(pair.Key, pair.Value);
            foreach (var pair in source.AllocationCountDeltas) plan.AllocationCountDeltas.Add(pair.Key, pair.Value);
        }
        return resource.Rewrite(plan).ToArray().LongLength;
    }
}
