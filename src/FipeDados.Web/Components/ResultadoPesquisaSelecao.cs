using FipeDados.Core.Models;

namespace FipeDados.Web.Components;

public sealed record ResultadoPesquisaSelecao(IReadOnlyList<ItemSelecao> Itens, bool TemMais = false);
