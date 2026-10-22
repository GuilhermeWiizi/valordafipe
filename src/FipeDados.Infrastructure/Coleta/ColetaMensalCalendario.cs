namespace FipeDados.Infrastructure.Coleta;

public static class ColetaMensalCalendario
{
    public static TimeZoneInfo Fuso { get; } = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    public static DateTime AgoraLocal(TimeProvider relogio) => TimeZoneInfo.ConvertTime(relogio.GetUtcNow(), Fuso).DateTime;
    public static DateOnly Competencia(DateTime local) => new(local.Year, local.Month, 1);
    public static bool PodeIniciar(DateTime local, DateOnly primeira, ColetaMensalExecucao? execucao)
    {
        if (Competencia(local) < primeira || local < Competencia(local).ToDateTime(new TimeOnly(3, 0))) return false;
        if (execucao?.Estado is EstadoColetaMensal.Concluida or EstadoColetaMensal.Expirada) return false;
        // Executando indica uma interrupção do processo: o lock do banco garante que não há outro coletor ativo.
        return execucao?.Estado == EstadoColetaMensal.Executando || execucao?.UltimaTentativaEm is not { } ultima
            || (TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ultima, DateTimeKind.Utc), Fuso).Date < local.Date
                && local.TimeOfDay >= TimeSpan.FromHours(3));
    }
}
