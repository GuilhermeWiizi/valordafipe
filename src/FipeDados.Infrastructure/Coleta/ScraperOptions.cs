namespace FipeDados.Infrastructure.Coleta;

/// <summary>Configuração do coletor em lote (seção "Scraper" do appsettings ou linha de comando).</summary>
public class ScraperOptions
{
    public const string Section = "Scraper";

    /// <summary>"Carro" ou "Moto"; vazio coleta os dois.</summary>
    public string? Tipo { get; set; }

    /// <summary>Filtro pelo nome da marca (contém, sem diferenciar maiúsculas); vazio coleta todas.</summary>
    public string? Marca { get; set; }

    /// <summary>Meses de preço garantidos por veículo: 1 = só o mês atual; 0 = só o catálogo.</summary>
    public int MesesPreco { get; set; } = 1;

    /// <summary>Pausa base após uma falha; cresce linearmente a cada nova tentativa do mesmo item.</summary>
    public int PausaAposFalhaSegundos { get; set; } = 90;

    /// <summary>Tentativas por item antes de pulá-lo (fica para a próxima execução).</summary>
    public int TentativasPorItem { get; set; } = 3;

    /// <summary>Falhas seguidas (entre itens) que indicam bloqueio e abortam a execução.</summary>
    public int MaxFalhasConsecutivas { get; set; } = 6;

    /// <summary>Pausa base após um 429 da Fipe (sem Retry-After); dobra a cada 429 seguido.</summary>
    public int PausaLimiteSegundos { get; set; } = 60;

    /// <summary>429 seguidos (sem nenhum sucesso entre eles) que abortam a execução.</summary>
    public int MaxLimitesSeguidos { get; set; } = 6;

    /// <summary>Tempo máximo esperando o PostgreSQL voltar antes de abortar a execução.</summary>
    public int MaxEsperaBancoMinutos { get; set; } = 30;
}

/// <summary>Coleta abortada por falhas seguidas demais (provável bloqueio da Fipe).</summary>
public class ColetaAbortadaException(string message) : Exception(message);
