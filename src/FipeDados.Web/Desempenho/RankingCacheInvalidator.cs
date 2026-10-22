using FipeDados.Infrastructure.Services;
using Microsoft.AspNetCore.OutputCaching;

namespace FipeDados.Web.Desempenho;

public sealed class RankingCacheInvalidator(IOutputCacheStore cache) : IRankingPublicacaoObserver
{
    public Task PublicadoAsync(CancellationToken ct) => cache.EvictByTagAsync(PaginaCache.RankingTag, ct).AsTask();
}
