using FipeDados.Infrastructure.Coleta;
using Hangfire;
using Hangfire.Server;

namespace FipeDados.Web.Jobs;

[Queue(Fila)]
[AutomaticRetry(Attempts = 0)]
public sealed class ColetaMensalJob(ColetaMensalService service, IBackgroundJobClient jobs, JobStorage storage)
{
    public const string Fila = "coleta";
    public Task VerificarAsync(CancellationToken ct) => service.VerificarAsync(
        competencia => jobs.Enqueue<ColetaMensalJob>(job => job.ExecutarAsync(competencia.Year, competencia.Month, null, CancellationToken.None)),
        id =>
        {
            using var connection = storage.GetConnection();
            return connection.GetJobData(id)?.State is "Enqueued" or "Processing" or "Scheduled" or "Awaiting";
        }, ct);

    public Task ExecutarAsync(int ano, int mes, PerformContext? context, CancellationToken ct)
        => service.ExecutarAsync(new(ano, mes, 1), ct, context?.BackgroundJob.Id);
}
