namespace FipeDados.Infrastructure.Coleta;

public sealed class ColetaMensalConfiguracao
{
    public int Id { get; set; } = 1;
    public DateOnly PrimeiraCompetencia { get; set; }
}

public enum EstadoColetaMensal { Pendente, Executando, AguardandoReferencia, Incompleta, Concluida, Expirada }

public sealed class ColetaMensalExecucao
{
    public DateOnly Competencia { get; set; }
    public int? TabelaReferenciaCodigo { get; set; }
    public EstadoColetaMensal Estado { get; set; }
    public int Tentativas { get; set; }
    public DateTime? UltimaTentativaEm { get; set; }
    public DateTime? ConcluidaEm { get; set; }
    public int VeiculosPercorridos { get; set; }
    public int PrecosConsultados { get; set; }
    public int ItensComFalha { get; set; }
    public string? UltimoErro { get; set; }
    public string? JobId { get; set; }
}
