using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;

namespace LateJoin.Server;

[Injectable(InjectionType.Singleton, TypePriority = 0)]
public class LateJoinHost : IOnLoad
{
    private readonly ISptLogger<LateJoinHost> _logger;

    public LateJoinHost(ISptLogger<LateJoinHost> logger)
    {
        _logger = logger;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;
        var fikaServer = Path.GetFullPath(Path.Combine(modFolder, "..", "fika-server", "FikaServer.dll"));

        if (!File.Exists(fikaServer))
        {
            _logger.Warning($"[LateJoin] Fika server module was not found at {fikaServer}.");
        }
        else
        {
            _logger.Success($"[LateJoin] Server companion loaded; Fika server found at {fikaServer}.");
        }

        return Task.CompletedTask;
    }
}
