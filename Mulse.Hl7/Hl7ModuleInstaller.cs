using Mulse.Modules;

namespace Mulse.Hl7;

public sealed class Hl7ModuleInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddParse<Hl7ParseModule>();
        builder.AddParse<Hl7SchemaParseModule>();
        builder.AddOrchestrationAugment<Hl7MessageTypeAugmentModule>();
        builder.AddRender<Hl7RenderModule>();
        builder.AddRender<Hl7XmlRenderModule>();
        builder.AddRender<Hl7SchemaRenderModule>();
    }
}
