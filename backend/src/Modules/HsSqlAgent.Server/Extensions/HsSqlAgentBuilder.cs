using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Extensions;

public class HsSqlAgentBuilder(IApplicationBuilder app)
{
    public IApplicationBuilder App { get; } = app;
    public HsSqlAgentPipelineOptions Options { get; } = new();
}