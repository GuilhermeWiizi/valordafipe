# Histórico retroativo da FIPE em Python

Executa na máquina com acesso à FIPE direta, grava cada cotação no PostgreSQL local
e envia os preços à VPS por SSH. A pasta é independente do .NET e pode ser copiada
para outro local: mantenha `historico.py`, `merge_precos.sql`, `marcas-prioritarias.json`, configuração e dependências juntos.
Requer Python 3.11+ e o banco local já populado com o catálogo do site.

## Começar no Windows

No PowerShell, dentro desta pasta:

```powershell
.\preparar.bat
# Confira o banco e o destino em historico.local.toml.
.\executar.bat plan
.\executar.bat run
```

O ambiente Python desta máquina já foi preparado. `executar.bat` sem argumentos
mostra somente a prévia. `run` faz uma passagem completa, envia preços a cada
5 minutos entre consultas e envia novamente ao terminar. Não é um serviço nem
depende de manter o site local aberto; o terminal e a máquina precisam permanecer ativos.
Desative a suspensão automática enquanto estiver coletando.

Em qualquer sistema, também pode executar diretamente:

```powershell
python -m venv .venv
# Windows:
.venv\Scripts\python.exe -m pip install -r requirements.txt
Copy-Item config.example.toml historico.local.toml
.venv\Scripts\python.exe -u historico.py run
```

Linux/macOS usam `.venv/bin/python` em lugar de `.venv\Scripts\python.exe`.

## Modos e filtros

```powershell
# Prévia: lê o banco; não consulta a FIPE nem altera os bancos.
.\executar.bat plan

# Só coleta localmente, sem conectar à VPS.
.\executar.bat collect

# Só envia os preços locais ainda não confirmados na VPS.
.\executar.bat sync

# Coleta e envio periódico: carros primeiro, marcas populares primeiro; motos depois.
.\executar.bat run

# Primeiro teste pequeno, no Fiesta da base local atual.
.\executar.bat run --veiculo 8323 --max-consultas 3 --sem-atualizar-referencias

# Pode repetir --veiculo. Os IDs aqui são exclusivamente os do banco local.
.\executar.bat collect --veiculo 8323 --veiculo 5087 --meses 24

# Só carros; limitar aos primeiros 100 veículos da ordem de prioridade.
.\executar.bat run --tipo 1 --limite-veiculos 100

# Opcional: restaurar a ordem antiga por ID, sem prioridade de marcas/tipos.
.\executar.bat run --ordem cadastro

# Completar também linhas antigas que foram gravadas com preço nulo/zero.
.\executar.bat run --reconsultar-vazios

# Intervalo menor entre os lotes enviados (entre consultas, sem interromper pausas HTTP).
.\executar.bat run --sincronizar-cada 120 --lote-sync 500

# Logs sem barra dinâmica, para redirecionar a saída do terminal.
.\executar.bat run --sem-tela
```

`--meses` define as referências mais recentes presentes na base, até 240 meses.
Na coleta, o script atualiza essas referências primeiro; `--sem-atualizar-referencias`
usa somente as já existentes. Isso não cria modelos/anos que faltam no catálogo.
`--max-consultas` conta chamadas HTTP reais, incluindo referências, retentativas e
eventual consulta alternativa por marca/modelo. O limite não restringe quantos
preços já existentes podem ser enviados à VPS. Os filtros de veículos e meses valem
tanto para a coleta quanto para o envio.

## Ordem de prioridade e controle do total

`run` e `collect` usam por padrão esta sequência, sem excluir veículos:

1. Carros das marcas populares, na ordem do array do site (Fiat, Volkswagen,
   Chevrolet, Toyota, Hyundai e demais marcas do grupo).
2. Todos os demais carros.
3. Motos das marcas prioritárias, começando por Honda e Yamaha.
4. Todas as demais motos; outros tipos ficam ao final, se existirem na base.

No repositório, a lista vem de `src/FipeDados.Core/Models/MarcasRelevantes.cs`:
usa `CarrosPopulares`, quando disponível, ou `Carros`, e o array `Motos`.
Ao copiar a pasta para fora do repositório, usa a cópia em `marcas-prioritarias.json`.
`--marcas-prioritarias outro.json` permite indicar outra lista de slugs com os
arrays `Carros` e `Motos`. O limite de veículos é aplicado **depois** da ordenação.

A janela completa continua sendo veículos selecionados × meses de referência.
A prévia mostra totais por etapa e por marca, separados em com preço, sem preço
(linhas nulas/zero já existentes) e pendentes. Na retomada, os números são recalculados
pelo banco: mudar a ordem não zera o avanço nem reconsulta preços positivos.

As barras mostram combinações resolvidas no banco sobre o total, incluindo o que
já estava gravado antes desta execução. Uma tentativa recusada **não avança** essa
cobertura. O contador de combinações tentadas aparece separadamente no resumo.
O script segue para a etapa seguinte mesmo se algumas combinações da anterior
forem recusadas pela fonte; essas pendências continuam contabilizadas.

Os eventos `FASE`, `MARCA` e `PROGRESSO` identificam etapa, marca e cobertura global.
A cada minuto, nas mudanças de etapa e no encerramento da coleta, os totais são
recalculados no banco e registrados no log. Ao lado do JSONL, o arquivo
`historico-*.progresso.json` guarda a última fotografia completa, com os totais por
marca e etapa e a janela de referências. As atualizações usam substituição atômica.

## O que aparece durante a execução

- Prévia: veículos, janela de referências, combinações pendentes e tempo mínimo estimado.
- Cada veículo: posição na seleção, ID local, nome/ano/combustível e meses pendentes.
- Cada mês: referência, chamada HTTP, número da tentativa, status e duração.
- Cada gravação: preço em reais e confirmação de que foi salvo localmente.
- Barras: cobertura geral, marcas prioritárias, restante e marca atual, incluindo
  dados anteriores à execução; falhas permanecem pendentes sem avançar a cobertura.
- Pausas: motivo, espera, contagem regressiva e evento a cada 30 segundos.
- Cada lote: enviados, gravados/preenchidos, preservados, veículos ausentes e conflitos.
- Resumo final e caminho do log completo em `logs/historico-*.jsonl`.

O log é JSON Lines: uma linha por evento, com horário e campos como veículo,
referência, parâmetros da consulta e valor. Não contém senha SSH nem string de conexão.
Pode acompanhar outro terminal com:

```powershell
Get-Content .\logs\historico-ARQUIVO-DA-EXECUCAO.jsonl -Wait -Tail 30
```

A estimativa inicial é um mínimo com 1,8 segundo entre chamadas, sem concorrência.
Um catálogo grande pode levar semanas. A barra depende da velocidade observada e
as pausas podem aumentar bastante o tempo. Evite rodar outros coletores na mesma
conexão à internet durante essa coleta.

## Retomada e tratamento de falhas

Cada cotação é confirmada numa transação local antes de consultar o próximo mês.
Preços positivos existentes são preservados. Por padrão, linhas locais sem preço
também são consideradas já consultadas; use `--reconsultar-vazios` para revisitar essas lacunas.
Respostas inválidas, erro de parâmetros, bloqueios e timeouts ficam pendentes; não
são gravados como ausência de preço. Se a FIPE recusar uma consulta por código,
o script tenta também marca/modelo/ano.

Em HTTP 403/429 a pausa inicial é de 5 minutos, dobra por tentativa e respeita
`Retry-After` maior. Por padrão, após 4 tentativas interrompe a coleta inteira.
Timeout/5xx têm pausas menores. Dez falhas técnicas consecutivas também interrompem;
erros de combinação recusada não contam nesse bloqueio geral. Não contorna Cloudflare
nem usa a Parallelum, pois o objetivo é coletar pela conexão local que acessa a FIPE.

`Ctrl+C` encerra; pode esperar a resposta HTTP corrente até o timeout configurado.
Não há envio final forçado após Ctrl+C: os dados locais permanecem e `sync` envia depois.
Para retomar, execute o mesmo comando; os meses já gravados são pulados. Só um
coletor Python por banco local pode executar por vez.

Códigos de saída: `0` concluído/limite voluntário atingido, `2` falha ou pendências,
`130` interrupção pelo usuário. O limite voluntário não significa histórico completo.

## Como o merge protege os dados da VPS

O banco permanece na rede interna do Docker; não abre a porta PostgreSQL da VPS.
A conexão usa SSH e `docker exec ... psql`, com a senha digitada de forma oculta.
Pode usar chave SSH ou a variável `FIPE_VPS_SSH_PASSWORD` para uma execução sem interação.
`FIPE_VPS_SSH_KEY` permite indicar uma chave privada sem alterar o arquivo de configuração
nem invalidar o histórico de confirmações da sincronização.
A conexão local pode vir de `FIPE_LOCAL_DSN` em vez do TOML.

O host precisa constar em `~/.ssh/known_hosts`. Se for outro computador, conecte uma
vez com `ssh root@srv1684584.hstgr.cloud` e confira a identidade do servidor antes de
aceitar a chave. O script não aceita automaticamente chaves desconhecidas ou alteradas.

Cada lote é uma transação. A correspondência usa tipo de veículo, códigos FIPE
da marca/modelo e código ano-combustível; confere também código FIPE do veículo
quando conhecido e a data da referência. Os IDs locais não são enviados como vínculos.
Somente preços positivos entram no merge: insere meses ausentes e preenche lacunas
nulas/zero. Valores positivos existentes, usuários, consultas e pagamentos são preservados.
Completa apenas códigos FIPE desconhecidos de veículos correspondentes.

Veículos fora do catálogo da VPS ou referências/códigos incompatíveis ficam pendentes
para conciliação, sem criar catálogo automaticamente. O resumo mostra essas quantidades.
Somente após o COMMIT remoto o script registra a confirmação em `logs/sync.sqlite3`,
separada por destino. Queda de conexão ou dúvida no resultado permite reenviar o lote
com segurança. Guarde esse arquivo para evitar reenvios desnecessários; se o apagar,
o próximo `sync` compara tudo novamente e preserva as cotações já existentes.

O primeiro envio também verifica os preços positivos antigos da seleção, não apenas
os coletados pelo Python. Mesmo nessa primeira passagem, os existentes na VPS são
preservados. Se restaurar um backup antigo da VPS, remova o estado de sincronização
para conferir novamente todos os preços.

## Testes

Sem banco (testes de HTTP simulados, validação e confirmação de envio):

```powershell
.venv\Scripts\python.exe -m unittest discover -s . -p test_historico.py -v
```

Para incluir merge, retomada e cursor em PostgreSQL, defina `FIPE_PY_TEST_DSN` com
uma conexão de testes. Os testes criam e removem somente schemas temporários próprios.
