# VALORDAFIPE

Site que mostra os veículos da **Tabela Fipe** (carros e motos) de forma clara e objetiva,
com **gráficos de variação de preço dos últimos 24 meses** — algo que o site oficial da Fipe
não oferece. Os dados são raspados da API interna da Fipe e consolidados em um **PostgreSQL**
próprio, com estratégia **sob demanda + cache**.

Stack: **.NET 10 · Blazor Web App (Interactive Server) · EF Core + Npgsql · PostgreSQL · ApexCharts**.

---

## ⚠️ Aviso legal

A Fipe **não oferece uma API pública oficial**. Este projeto consome a API interna do site
`veiculos.fipe.org.br` apenas com fins de estudo, com **coleta educada** (limite de taxa,
backoff, cache agressivo) para não sobrecarregar o serviço. Coleta massiva pode violar os
Termos de Uso e resultar em bloqueio de IP. Use com moderação e por sua conta e risco.

---

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- PostgreSQL de desenvolvimento, por **uma** das opções:
  - [Docker Desktop](https://www.docker.com/products/docker-desktop/) + `docker compose up -d postgres`; ou
  - **PostgreSQL portátil** (sem Docker e sem admin): `.\tools\pg-portable.ps1 start`. Na primeira
    execução baixa os binários oficiais da EnterpriseDB (~330 MB) para `.pgsql/` (fora do Git),
    cria o cluster com as mesmas credenciais do docker-compose e sobe na porta 5432.
    O servidor sobe desanexado (via WMI) e com console oculto, então não morre quando o
    terminal, a IDE ou o agente que rodou o script é fechado, nem por uma janela fechada. Não é serviço do Windows: após reiniciar o PC, rode `start`
    de novo. Para parar: `.\tools\pg-portable.ps1 stop`.

## Rodar tudo com Docker

Instale e inicie o [Docker Desktop](https://www.docker.com/products/docker-desktop/) com
**contêineres Linux** (no servidor Linux, use Docker Engine com o plugin Compose).
Não é necessário instalar o SDK .NET nem PostgreSQL no host.

Na raiz do projeto, execute no PowerShell:

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

Abra **http://localhost:8080**. O Compose compila o site com o SDK .NET 10 e executa-o
com a imagem ASP.NET Core, como usuário sem privilégios. O site aguarda o healthcheck
do PostgreSQL e aplica as migrations automaticamente. O coletor não inicia nesse comando.
A dependência usa `service_healthy`, conforme a
[documentação do Compose](https://docs.docker.com/compose/how-tos/startup-order/).

O arquivo `.env` configura portas, banco, domínio e SMTP; ele é ignorado pelo Git e não
entra na imagem. Se a porta 8080 estiver ocupada, altere `WEB_PORT`. Se o PostgreSQL
portátil estiver rodando na 5432, pare-o com `.\tools\pg-portable.ps1 stop` ou altere
`POSTGRES_PORT` (por exemplo, 5433). A comunicação entre os contêineres sempre usa
`Host=postgres;Port=5432`, independentemente da porta publicada no host.

Comandos de operação:

```bash
docker compose ps
docker compose logs -f web
docker compose logs -f postgres
# Atualizar depois de alterar o código
docker compose up -d --build
# Parar e remover os contêineres, mantendo os dados
docker compose down
```

O volume `fipedados-pgdata` guarda preços, catálogo, contas e chaves de Data Protection.
**`docker compose down -v` apaga esse volume e os dados.** As credenciais do PostgreSQL
só são aplicadas ao criar um banco vazio: mudar `.env` depois não troca a senha de um
banco existente. O banco do PostgreSQL portátil é separado; subir o Compose cria uma
base própria e não importa os dados locais automaticamente.

Para executar o coletor manualmente, usando o mesmo banco:

```bash
docker compose run --rm --build scraper --tipo Carro --marca Fiat --meses 1
```

O serviço `scraper` tem um perfil opcional e só roda quando solicitado. Evite coleta
em lote simultânea a consultas do site: os limitadores são independentes por processo.

### Publicar em um servidor

Copie o projeto para o servidor, crie o `.env` a partir do exemplo e escolha uma senha
forte para `POSTGRES_PASSWORD` antes da primeira execução. Defina `SEO_SITE_URL` com
a origem HTTPS pública e `SEO_ALLOW_INDEXING=true` quando o domínio oficial estiver pronto.
Configure as variáveis `EMAIL_*` para permitir recuperação de senha; sem SMTP não há envio.

Execute `docker compose up -d --build` e coloque um proxy reverso com certificado HTTPS
na frente da porta 8080. O proxy precisa suportar WebSockets para o Blazor Server.
O Compose fornece HTTP interno; o certificado fica no proxy. Configure o ASP.NET Core
para confiar no IP/rede desse proxy ao processar `X-Forwarded-Proto`, como explicado
na seção **SEO e publicação**. Restrinja a porta 8080 ao proxy no firewall e mantenha
backups do PostgreSQL. A porta publicada do banco fica limitada a `127.0.0.1`.

## Atualizar a VPS pelo Windows (`scripts/deploy.bat`)

O fluxo é o mesmo do script do RotinaPsi: SSH → `git pull origin main` → build →
atualização dos contêineres. O host padrão é `root@srv1684584.hstgr.cloud` e a pasta
remota é `/srv/FipeDados`. O FIPE usa um projeto Compose próprio, banco próprio sem
porta publicada e a porta **127.0.0.1:8082** para o site (RotinaPsi usa 8080 e Evolution usa 8081).

### Preparação inicial (uma vez)

O repositório do projeto é [GuilhermeWiizi/valordafipe](https://github.com/GuilhermeWiizi/valordafipe),
na branch `main`. O fluxo de atualização exige que o código esteja commitado e enviado
a essa branch, inclusive `Dockerfile`, scripts e arquivos Compose.

No Windows, conecte-se à VPS:

```powershell
ssh root@srv1684584.hstgr.cloud
```

Na VPS, faça a preparação inicial:

```bash
git clone https://github.com/GuilhermeWiizi/valordafipe.git /srv/FipeDados
cd /srv/FipeDados
cp .env.vps.example .env.vps
chmod 600 .env.vps
openssl rand -hex 32
nano .env.vps
```

Coloque a senha gerada em `POSTGRES_PASSWORD` e ajuste `SEO_SITE_URL` para o domínio
definitivo. Mantenha `SEO_ALLOW_INDEXING=false` até concluir domínio e HTTPS. O servidor
precisa de Git, Bash, curl e Docker com Compose que suporte `up --wait --wait-timeout`.
Confirme que a porta está livre com `ss -lntp 'sport = :8082'`; se necessário, escolha
outra em `WEB_PORT`.

### Publicação e próximas atualizações

Depois de enviar suas alterações ao Git, execute no seu computador:

```powershell
cd C:\Git\FipeDados
.\scripts\deploy.bat
```

O script busca a branch `main` com `--ff-only`, valida a configuração e compila antes
de substituir o site. Atualiza somente o projeto Compose `fipedados`, mantém o volume
do banco e confirma que `/health/version` retorna o commit esperado tanto no contêiner
quanto no domínio HTTPS de `SEO_SITE_URL`, depois das migrations. HTTP 200 de outra
versão não é considerado sucesso. A atualização pode causar
uma breve interrupção do FIPE. Se falhar, mostra o erro e retorna código diferente de zero.
O `.env.vps` permanece no servidor e é ignorado pelo Git.

O `.bat` confere o commit local e recusa alterações ainda não commitadas. Se a branch
da VPS estiver diferente da revisão local, o deploy é interrompido antes do build.
É possível consultar a revisão publicada em `https://valordafipe.com.br/health/version`.

Os anúncios de demonstração e a oferta de plano ficam habilitados no ambiente local
por `appsettings.Development.json`. Para exibi-los também na VPS, configure
`ADS_EXIBIR_MOCK=true` em `.env.vps` e execute o deploy. O padrão é `false`;
essa configuração não depende de `ADSENSE_CLIENT_ID` e exibe anúncios fictícios
identificados como demonstração. Diferenças nessa opção não significam versão antiga.

Para outra pasta, branch ou conexão, defina `DEPLOY_PATH`, `DEPLOY_BRANCH` ou `DEPLOY_HOST`
no ambiente antes de executar o `.bat`. Use `--no-pause` para execução automatizada.

Para conferir diretamente na VPS:

```bash
cd /srv/FipeDados
docker compose -p fipedados --env-file .env.vps -f docker-compose.vps.yml ps
docker compose -p fipedados --env-file .env.vps -f docker-compose.vps.yml logs -f web
curl -sS -D - -o /dev/null http://127.0.0.1:8082/robots.txt
```

### Domínio e HTTPS

Se o proxy existente roda diretamente na VPS, configure o domínio do FIPE para o destino
`http://127.0.0.1:8082`, com WebSockets habilitados. Se ele roda em outro contêiner, o
endereço `127.0.0.1` refere-se ao próprio proxy: nesse caso, conecte o serviço `web` à
rede externa do proxy, com um alias exclusivo (por exemplo, `fipedados-web`), e use
`http://fipedados-web:8080` como destino. Essa conexão depende da configuração do proxy
existente e não é alterada pelo script. Configure também a confiança no IP/rede do proxy
no ASP.NET Core, conforme a seção **SEO e publicação**, antes de habilitar o acesso público.

O banco criado na VPS começa vazio; os dados do PostgreSQL portátil precisam ser
exportados e importados separadamente, se você quiser preservar o histórico já coletado.

### Nginx no host para `valordafipe.com.br`

Na VPS atual, o FIPE usa a porta **8082** (8080 é do RotinaPsi e 8081 é do Evolution).
Mantenha `WEB_PORT=8082` no `.env.vps`. O modelo de configuração está em
`deploy/nginx/valordafipe.conf` e usa essa mesma porta, com suporte a WebSockets.

O Nginx chega ao contêiner pelo gateway da rede Docker. Descubra esse endereço:

```bash
docker inspect fipedados-web-1 --format '{{range .NetworkSettings.Networks}}{{.Gateway}}{{end}}'
```

Defina `TRUSTED_PROXY_IP` no `.env.vps` com o IP exibido e faça um novo deploy. A aplicação
aceita `X-Forwarded-For` e `X-Forwarded-Proto` desse IP, preservando a validação de origem.
Isso permite reconhecer o HTTPS terminado pelo Nginx, conforme a
[documentação do ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).
Se a rede Docker for recriada com outro gateway, atualize essa variável e recrie o serviço.

Crie o registro DNS `A` de `valordafipe.com.br` apontando para o IPv4 público da VPS.
Se houver registro `AAAA`, ele também deve apontar para a VPS com conectividade IPv6.
Depois de enviar as alterações do projeto ao Git e executar o deploy, instale o site:

```bash
cd /srv/FipeDados
chmod +x deploy/nginx/atualizar-bots-verificados.sh
deploy/nginx/atualizar-bots-verificados.sh
cat > /etc/cron.d/valordafipe-bots <<'EOF'
# O cron usa PATH=/usr/bin:/bin, sem o /usr/sbin do nginx.
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
17 4 * * * root /srv/FipeDados/deploy/nginx/atualizar-bots-verificados.sh >/dev/null
EOF
cp deploy/nginx/valordafipe.conf /etc/nginx/sites-available/valordafipe.com.br
ln -s /etc/nginx/sites-available/valordafipe.com.br /etc/nginx/sites-enabled/valordafipe.com.br
nginx -t && systemctl reload nginx
curl -sS -D - -o /dev/null -H 'Host: valordafipe.com.br' http://127.0.0.1/robots.txt
```

#### Limite por IP contra raspagem

O Nginx limita cada cliente (IPv4, ou prefixo /64 no IPv6) a 2 requisições/s com rajada de 40
e a 15 conexões simultâneas com o hub do Blazor; acima disso responde `429`. Arquivos estáticos,
`robots.txt` e sitemaps não contam. A aplicação aplica um segundo limite, um pouco maior
(seção `LimiteAcesso` do `appsettings.json`), que também vale para acesso direto ao contêiner.

- **Googlebot e Bingbot** ficam isentos pelas faixas de IP publicadas pelo Google e pela
  Microsoft, nunca pelo User-Agent. O script acima grava `/etc/nginx/valordafipe-bots-verificados.conf`
  e o cron o atualiza todo dia; a aplicação baixa as mesmas listas sozinha a cada 24 h.
- **Teste de carga ou outra rede confiável:** descomente a linha de exemplo no bloco `geo` do
  Nginx e defina `LIMITE_ACESSO_REDE_ISENTA` (CIDR) no `.env.vps`.
- Se o `TRUSTED_PROXY_IP` estiver errado, a aplicação desliga o próprio limite (senão todos
  os visitantes contariam como o IP do Nginx) e registra um aviso no log. O Nginx continua limitando.
- Se o arquivo do site já foi alterado pelo `certbot`, não sobrescreva: copie para ele os blocos
  `geo`, `map`, `limit_req_zone`/`limit_conn_zone` e as diretivas `limit_*` das `location`.
- Bloqueios aparecem como `429` no `access.log` do Nginx e na métrica
  `aspnetcore.rate_limiting.requests` da aplicação.

Instale o arquivo e crie o link somente na configuração inicial. Após o Certbot configurar
HTTPS, edite o arquivo já instalado para ajustes; copiar novamente o modelo HTTP por cima
remove os blocos de certificado adicionados por ele. Os deploys do aplicativo não alteram
os arquivos em `/etc/nginx`.

Com o DNS propagado e HTTP acessível, use o Certbot já instalado na VPS:

```bash
certbot --nginx -d valordafipe.com.br --redirect
curl -sS -D - -o /dev/null https://valordafipe.com.br/robots.txt
certbot renew --dry-run
```

O plugin Nginx emite o certificado e configura HTTPS, conforme o
[guia oficial do Certbot](https://certbot.eff.org/instructions?os=pip&tab=standard&ws=nginx).
Se `certbot` não estiver instalado, siga o instalador apropriado ao sistema da VPS.
Depois de validar HTTPS, defina `SEO_ALLOW_INDEXING=true` no `.env.vps` e execute o deploy.

## Rodar o site com o SDK local

```bash
# 1) Subir o PostgreSQL (ou: .\tools\pg-portable.ps1 start)
docker compose up -d postgres

# 2) Rodar o site (aplica as migrations automaticamente no startup)
dotnet run --project src/FipeDados.Web
```

Abra a URL exibida no console (algo como `https://localhost:7xxx`).
Vá em **Consultar Tabela Fipe**, escolha Tipo → Marca → Modelo → Ano e clique em
**Ver preços e variação**. A página abre com os preços já disponíveis no banco, inclusive
sem JavaScript. Ao abrir um veículo no navegador, a página completa automaticamente o
histórico na Fipe quando faltarem preços nos últimos 24 meses; os valores já salvos são
reutilizados. A busca pode levar alguns segundos e mostra um indicador de andamento.

> As migrations são aplicadas no startup. Para aplicá-las manualmente:
> ```bash
> dotnet ef database update --project src/FipeDados.Infrastructure --startup-project src/FipeDados.Web
> ```

## Como funciona

- **Histórico detalhado e alternativas:** a página de cada veículo oferece gráfico de
  preço ou variação percentual com filtros de 6/12 meses e todo o histórico, tabela com
  diferenças mensais em reais e percentuais e variação acumulada, além de download CSV.
  A variação mensal exige o mês imediatamente anterior; lacunas não são interpoladas.
  Mostra outros anos do modelo e até seis veículos do mesmo tipo com preço na faixa de
  ±10%, sempre na mesma referência da cotação exibida. Atalhos abrem `/comparar?a=ID&b=ID`
  com a seleção preenchida. As sugestões usam apenas a base. A página é SSR estático:
  gráfico, filtros e CSV rodam no navegador (`wwwroot/js/historico.js`), sem circuito no
  servidor. Só quando faltam meses entra a ilha interativa `ColetaHistorico`, que busca os
  preços ausentes na Fipe e atualiza a página ao terminar.

- **Busca em tempo de execução na comparação:** ao comparar ou trocar de período, os meses
  ausentes ou sem preço positivo são consultados na Fipe e salvos no banco. Preços válidos
  já armazenados são reutilizados. Lacunas antigas podem ser consultadas novamente; se a
  busca pelo código Fipe não encontrar preço, tenta-se a identificação por marca/modelo/ano.
  A tela mostra o andamento, permite cancelar e distingue indisponibilidade/limite da Fipe
  de ausência de preços para aquele veículo. As consultas seguem o limitador existente.

- **Desvalorização por marca:** a página inicial mostra as cinco marcas com menor média
  mensal, usando todos os veículos com preços positivos nos dois meses consecutivos,
  incluindo altas e estabilidade. A média é simples, calculada sobre os percentuais
  individuais, sem ponderação por preço. O ranking mostra a quantidade de veículos da
  amostra, respeita o filtro de tipo e calcula carros e motos separadamente. Marcas com
  média positiva ou zero ficam fora da lista; abrir a página não dispara coleta.

- **Comparação (`/comparar`):** selecione dois carros ou motos e compare a valorização ou
  desvalorização no mês, em 6 ou 12 meses, ou no histórico completo disponível. Os dois
  veículos usam as mesmas datas inicial e final. O gráfico alterna entre variação
  percentual (base 0%) e preços em reais, com linhas retas. A tela destaca diferenças
  em pontos percentuais e reais, mostra os indicadores lado a lado e avisa quando o
  histórico é parcial. Meses ausentes não são estimados; a coleta usa o cache existente.

- **Visão geral:** ranking dos cinco veículos que mais valorizaram e dos cinco que mais
  desvalorizaram, em percentual, com filtros para carros e motos. Compara a tabela de
  referência mais recente com o mês imediatamente anterior, apenas para combinações
  de modelo/ano/combustível com preços positivos nos dois meses. A página mostra a
  referência, a cobertura da base e os estados sem dados; abrir o ranking não dispara coleta.
- **Seletores com busca:** marca, modelo e ano têm filtro de texto (sem distinção de
  acentos), navegação por setas e Enter, fechamento com Esc e indicadores de carregamento.

- **Catálogo (lazy + cache):** marcas/modelos/anos são buscados na Fipe na primeira vez que
  são navegados e gravados no Postgres; depois vêm do banco.
- **Histórico (sob demanda):** ao abrir um veículo, a página busca automaticamente os preços
  ausentes dos últimos 24 meses na Fipe e grava em `PrecoHistorico`. Preços positivos já
  armazenados são reutilizados; meses já consultados sem cotação também são reaproveitados
  na consulta automática, evitando novas chamadas a cada visita. A comparação pode
  reconsultar lacunas. A primeira
  consulta descobre o **Código Fipe** (modo `tradicional`) e os demais meses usam o modo
  `codigo` (mais robusto entre meses). Lacunas (mês sem preço) são gravadas como valor nulo.
- **Coleta educada:** `FipeApiClient` aplica limite de taxa (token bucket) + retry com backoff
  + circuit breaker, com headers de navegador. O site usa 0,6 req/s, sem rajadas, e uma fila
  compartilhada entre consultas. Em HTTP 429, aguarda `Retry-After` ou 60 segundos (com
  espera crescente) e tenta a mesma requisição mais duas vezes. A pausa vale para todas
  as consultas daquele processo e pode ser cancelada ao sair da página. Se a fonte
  continuar bloqueando, o histórico tenta novamente silenciosamente após 30 s, 1, 2, 4 e
  até 5 minutos, respeitando esperas maiores informadas pela fonte. As tentativas param
  ao sair da página; preços já salvos são preservados e a página atualiza uma vez ao concluir.
  Respostas vazias ou inválidas são falhas, não meses sem cotação.
- **Fonte alternativa (opcional):** `Fipe:ParallelumHabilitada=true` (Docker:
  `FIPE_PARALLELUM_HABILITADA=true`) usa a [Parallelum v2](https://deividfortuna.github.io/fipe/v2/)
  quando a FIPE direta está bloqueada ou indisponível. Compartilha fila e limite de taxa,
  pausa o acesso direto por pelo menos 5 minutos e valida veículo e mês antes de gravar.
  A modalidade sem token permite 500 chamadas por dia para toda a VPS: ao receber 429,
  respeita `Retry-After` ou aguarda 24h sem novas chamadas à alternativa. Não há contratação
  automática de plano. Um HTTP 402 indica histórico restrito ao plano: encerra a tentativa
  silenciosamente, atualiza a página se salvou meses e evita consultar essa referência na
  alternativa por 24h (compartilhado entre veículos), sem bloquear referências recentes ou
  a FIPE direta. O plano gratuito não garante os 24 meses de histórico. Falhas e bloqueios
  da alternativa não são gravados como ausência de preço.
- **Job de fundo:** `ReferenceTableRefreshService` mantém as tabelas de referência (24 meses)
  atualizadas e pré-carrega as marcas no startup.

## Coleta retroativa externa em Python

O [coletor Python](tools/historico-fipe/README.md) roda independentemente do .NET,
consulta pela máquina local e sincroniza preços com a VPS via SSH. Inclui prévia,
logs por veículo/mês, pausas visíveis, retomada e merge incremental que preserva
preços positivos já existentes. Na pasta `tools/historico-fipe`, execute
`preparar.bat`, `executar.bat plan` e `executar.bat run`.

## Coleta em lote (`FipeDados.Scraper`)

Além da coleta sob demanda do site, o console `FipeDados.Scraper` popula o banco de uma vez:
catálogo completo (marcas → modelos → anos) e preços dos últimos N meses. Usa os mesmos
serviços do site, então é **retomável**: o que já está no banco não vai de novo à Fipe.

```bash
# Catálogo completo de carros e motos + preço do mês atual (padrão)
dotnet run --project src/FipeDados.Scraper

# Só uma marca, com 24 meses de histórico
dotnet run --project src/FipeDados.Scraper -- --tipo Carro --marca Fiat --meses 24

# Só o catálogo, sem preços
dotnet run --project src/FipeDados.Scraper -- --meses 0
```

Opções: `--tipo Carro|Moto`, `--marca <trecho do nome>`, `--meses <N>` (0 = só catálogo),
`--rps <req/s>` (aceita fração, ex.: `0.5`). Demais ajustes na seção `Scraper` de
`src/FipeDados.Scraper/appsettings.json`.

- **Limite da Fipe:** a Fipe fica atrás do Cloudflare e devolve **HTTP 429** quando passa de
  ~90 requisições em ~2 minutos (medido em out/2026: a 1 req/s o bloqueio vinha a cada ~90 s).
  O coletor usa **0,6 req/s** por padrão, que rodou sem nenhum 429.
- **429:** não é repetido em 1-2 s (isso só prolonga o bloqueio). O coletor espera o
  `Retry-After` ou 60 s, dobrando a cada 429 seguido (até 15 min), e repete o mesmo item sem
  perdê-lo. Com 6 bloqueios seguidos aborta; basta rodar de novo mais tarde.
- **Log:** só aparece o que exige atenção. Timeouts e quedas de circuito que o Polly recupera
  sozinho não são registrados; se a falha persistir, o coletor registra `Falha em ...`.
- **Banco fora do ar:** também não gasta tentativa. O coletor confere a cada 30 s e retoma
  quando o PostgreSQL volta; após 30 min sem banco aborta (`Scraper:MaxEsperaBancoMinutos`).
- **Outras falhas:** pausa (90 s, 180 s...) e tenta de novo; após 3 tentativas pula o item, que
  fica para a próxima execução. Com 6 falhas seguidas aborta.
- **Volume:** a coleta completa do mês atual é da ordem de dezenas de milhares de requisições,
  ou seja, de 10 a 15 horas a 0,6 req/s. Histórico de 24 meses para todo o catálogo multiplica
  isso por 24 (semanas de coleta e risco alto de bloqueio); prefira limitar por marca.
- **Limitação conhecida:** o catálogo é cacheado; modelos lançados depois da primeira coleta de
  uma marca não são descobertos automaticamente.

## Configuração (`src/FipeDados.Web/appsettings.json`)

```json
{
  "ConnectionStrings": { "FipeDb": "Host=localhost;Port=5432;Database=fipedados;Username=fipe;Password=fipe" },
  "Fipe": {
    "RequestsPerSecond": 0.6,
    "HistoryMonths": 24,
    "MaxRetries": 3,
    "RateLimitRetries": 2,
    "RateLimitDelay": "00:01:00",
    "PrewarmMarcasNoStartup": true
  }
}
```

Em produção, sobrescreva a connection string via variável de ambiente
(`ConnectionStrings__FipeDb`) ou `dotnet user-secrets`.

## SEO e publicação

O domínio canônico configurado em `Seo:SiteUrl` é **https://valordafipe.com.br**. Todas as
páginas públicas entregam conteúdo e metadados no HTML inicial, com prerender do Blazor.
Os títulos, descrições, Open Graph, Twitter Card e dados estruturados `WebSite`, `WebPage`
e `BreadcrumbList` são gerados por página. As URLs existentes de veículos foram preservadas.

- `/catalogo` → `/marca/{id}` → `/modelo/{id}` → `/veiculo/{id}` oferece navegação por
  links HTML, com paginação de 60 itens e canonical própria para cada página. O catálogo
  publica apenas veículos com preço positivo na janela de referências configurada.
- `/sobre` explica a fonte, os indicadores, a cobertura parcial e a independência da VALORDAFIPE.
- `/robots.txt` aponta para `/sitemap.xml`, índice dos sitemaps de páginas, marcas,
  modelos e veículos. Os arquivos têm até 10 mil URLs e cache de 15 minutos. O `lastmod`
  dos veículos usa a data real de coleta; datas desconhecidas são omitidas.
- Veículos sem preço disponível e páginas vazias recebem `noindex`; páginas inexistentes
  retornam HTTP 404, falhas retornam HTTP 500. Rastreamento e leitura do sitemap não coletam
  dados na Fipe. A consulta automática do histórico começa após a página do veículo ficar
  interativa no navegador; o HTML inicial continua usando somente o banco.
- Fontes usam `preconnect` e `display=swap`, sem `@import` no CSS. As respostas usam
  compressão Brotli/Gzip e os assets mantêm o fingerprint/compressão de `MapStaticAssets`.

Para permitir indexação, publique com `ASPNETCORE_ENVIRONMENT=Production` e
`Seo__AllowIndexing=true` (padrão). Outros ambientes enviam `noindex` também no cabeçalho
`X-Robots-Tag` e bloqueiam o rastreamento em `robots.txt`. Em homologação com ambiente
Production, defina `Seo__AllowIndexing=false`.

O HTTPS deve funcionar no domínio público. Atrás de proxy, configure os proxies/redes
confiáveis do ASP.NET Core para que `X-Forwarded-Proto` seja processado; não remova a
validação de origem dos cabeçalhos encaminhados. A aplicação já habilita o middleware
com as restrições padrão a proxies locais.

Após publicar, valide a propriedade de domínio no **Google Search Console**, envie
`https://valordafipe.com.br/sitemap.xml` e inspecione uma URL de veículo. Verifique o HTML
renderizado, os dados estruturados e os Core Web Vitals com as ferramentas do Google.
Essas etapas dependem do acesso à hospedagem e ao Search Console; otimização técnica
não garante indexação ou uma posição específica nos resultados.

Referências: [guia de SEO do Google](https://developers.google.com/search/docs/fundamentals/seo-starter-guide),
[SEO com JavaScript](https://developers.google.com/search/docs/crawling-indexing/javascript/javascript-seo-basics)
e [URLs canônicas](https://developers.google.com/search/docs/crawling-indexing/consolidate-duplicate-urls).

## Contas de usuário (opcional)

Login e cadastro existem para contratar e acompanhar o **plano sem anúncios**. Nenhuma página pública exige
conta; o topo mostra **Entrar / Criar conta** ou **Minha conta**. Usa ASP.NET Core Identity no
mesmo PostgreSQL (tabelas `AspNet*`, criadas pela migration `AddContas` no startup).

| Rota | Função |
|---|---|
| `/conta/cadastrar` | Cadastro com e-mail e senha (mínimo de 8 caracteres, sem regras de símbolo) |
| `/conta/entrar` | Login; volta para a página de origem (`ReturnUrl`, só caminhos locais) |
| `/conta` | E-mail, data de criação, plano e logout (exige login) |
| `/conta/senha` | Troca de senha |
| `/conta/esqueci-senha` e `/conta/redefinir-senha` | Recuperação por link de e-mail, válido por 2 horas |
| `/conta/excluir` | Exclusão definitiva, confirmada com a senha |

- **Por que SSR estático:** as páginas `/conta/*` usam `[ExcludeFromInteractiveRouting]`, porque
  gravar o cookie de login exige a resposta HTTP. O restante do site segue Interactive Server.
- **Segurança:** bloqueio de 15 minutos após 5 senhas erradas (o link de redefinição libera),
  antifalsificação em todos os formulários e no logout, `noindex` nas páginas de conta, resposta
  idêntica para e-mail existente ou não na recuperação, link de senha montado com `Seo:SiteUrl`
  (não com o `Host` da requisição) e senhas nunca reexibidas no HTML.
- **Chaves do Data Protection** ficam na tabela `DataProtectionKeys`: reinícios e deploys não
  derrubam logins nem invalidam links de senha. Elas são gravadas sem criptografia; quem lê o
  banco consegue forjar cookies, então proteja o acesso ao PostgreSQL de produção.
- **Plano sem anúncios:** `AspNetUsers.SemAnunciosAte` (UTC). Enquanto for futuro, a conta não vê
  anúncios. O valor vai no cookie e é renovado a cada 30 minutos. Ao abrir `/conta` ou acompanhar
  pagamentos, a sessão do próprio comprador é atualizada antes da renderização, incluindo o AdSense.
- **AdSense:** o Publisher ID `ca-pub-3765938973361380` já está configurado para carregar o
  script de verificação (Auto ads) no `<head>`. Os arquivos Compose usam esse mesmo ID quando
  `ADSENSE_CLIENT_ID` está vazio ou ausente; defina essa variável para usar outra conta.
  Para desativar, configure `Anuncios__AdSenseClientId` vazio diretamente na aplicação.
  Com `Anuncios:ExibirMock=true` (ou `ADS_EXIBIR_MOCK=true` na VPS), o script real não é carregado;
  use `ADS_EXIBIR_MOCK=false` para verificar o site no AdSense. Contas no plano sem anúncios
  nunca carregam o script. Depois de publicar, marque “Adicionei o código” e clique em
  “Verificar” no AdSense.
- **Blocos de anúncio:** os 8 espaços (`Anuncio`, identificados por `data-ad-placement`) usam
  5 blocos de display responsivos, mapeados em `Anuncios:Blocos` no `appsettings.json`
  (VF Home destaque, VF Cabecalho, VF Home rankings, VF Veiculo e VF Comparacao). Ficam
  desligados até a aprovação do site: depois dela, defina `ADS_BLOCOS_ATIVOS=true` no
  `.env.vps` e execute o deploy. Bloco sem anúncio (`data-ad-status="unfilled"`) é ocultado
  e o conteúdo original do espaço volta, como o gráfico da home.
- **Demonstração do plano:** com `Anuncios:ExibirMock=true`, pesquisas pelo formulário e novas
  comparações bem-sucedidas compartilham a oferta “Consulte sem anúncios”. Ela aparece depois
  do resultado, na terceira consulta; as próximas exigem três novas consultas e pelo menos
  10 minutos desde a última oferta. Pode ser fechada imediatamente, inclusive por Escape.
  A contagem fica em `sessionStorage` por aba e sobrevive à recarga. Se o armazenamento falhar,
  a consulta segue sem oferta. Erros, tentativas repetidas da mesma comparação, mudanças de
  período/medida, links diretos, catálogo e veículos relacionados não contam. Assinantes ativos
  não recebem a oferta. A origem da pesquisa usa estado interno de navegação, sem mudar a URL.
  `/sem-anuncios` apresenta os benefícios e os quatro planos e permite
  voltar ao resultado por `ReturnUrl` local; a última comparação fica em memória no circuito
  para esse retorno, preservando período e medida. A contratação por Pix está descrita abaixo.
  Testes da frequência no navegador: `node --test tests/browser/plan-offer.test.mjs`.

### Planos com Pix pelo Mercado Pago

Os quatro períodos removem anúncios e ofertas: mensal (1 mês, R$ 9,90), trimestral
(3 meses, R$ 27,90), semestral (6 meses, R$ 49,90) e anual (12 meses, R$ 89,90).
O pagamento é integral e antecipado, com confirmação automática e renovação por novo pagamento.
As consultas continuam gratuitas. Não há assinatura recorrente nem cobrança de cartão nesta versão.

- `/sem-anuncios`: catálogo público; a escolha é preservada no login/cadastro.
- `/conta/pagamentos`: histórico privado e acompanhamento, com QR Code e Copia e Cola.
- `/conta/admin/pagamentos`: acompanhamento, devoluções para revisão e conferência dos pedidos antigos.
- `POST /pagamentos/mercado-pago/webhook`: notificações assinadas do provedor.

#### Dados necessários da conta Mercado Pago

Em [Suas integrações](https://www.mercadopago.com.br/developers/panel/app), crie ou selecione
uma aplicação de Checkout Transparente (API Orders). Configure no servidor:

| Variável Docker | Configuração .NET | Onde obter / finalidade |
|---|---|---|
| `MP_ACCESS_TOKEN` | `MercadoPago__AccessToken` | Credenciais da aplicação, ambiente correspondente |
| `MP_WEBHOOK_SECRET` | `MercadoPago__WebhookSecret` | Webhooks → Configurar notificação → chave secreta |
| `MP_VENDEDOR_ID` | `MercadoPago__VendedorId` | User ID da conta vendedora; confira em Dados da integração/conta de teste ou `/users/me` |
| `MP_URL_PUBLICA` | `MercadoPago__UrlPublica` | Origem HTTPS do site ou do túnel de testes, sem caminho |
| `MP_CHAVE_PIX_CADASTRADA` | `MercadoPago__ChavePixCadastrada` | `true` após cadastrar uma chave Pix na conta Mercado Pago e habilitar recebimentos |
| `MP_PRODUCAO` | `MercadoPago__Producao` | `false` para vendedor de teste; `true` para vendedor real |
| `MP_HABILITADO` | `MercadoPago__Habilitado` | Libera novas compras somente após validar a configuração |
| `MP_NOME_PAGADOR_TESTE` | `MercadoPago__NomePagadorTeste` | Só com `MP_PRODUCAO=false`: enviado como `payer.first_name`; `APRO` aprova o Pix de teste |

Não envie senha, tokens ou chave secreta no chat. Grave os segredos no `.env`/`.env.vps`
ignorado pelo Git, ou em variáveis do servidor/user-secrets. O SDK local não carrega `.env`
automaticamente: use as variáveis .NET. Esta integração de servidor não usa Public Key,
Client Secret ou acesso à sua conta bancária. A chave Pix fica cadastrada no Mercado Pago;
o site não precisa receber seu valor, apenas a confirmação `MP_CHAVE_PIX_CADASTRADA=true`.

Exemplo sem segredos, mantendo novas compras desativadas:

```dotenv
MP_HABILITADO=false
MP_PRODUCAO=false
MP_ACCESS_TOKEN=
MP_WEBHOOK_SECRET=
MP_VENDEDOR_ID=
MP_URL_PUBLICA=https://valordafipe.com.br
MP_CHAVE_PIX_CADASTRADA=false
PIX_ADMINISTRADOR_ID=
PLANO_MENSAL=9.90
PLANO_TRIMESTRAL=27.90
PLANO_SEMESTRAL=49.90
PLANO_ANUAL=89.90
```

`PIX_ADMINISTRADOR_ID` é o identificador de uma conta deste site, diferente do vendedor
Mercado Pago. Cadastre-a pelo site e obtenha o ID com:

```sql
SELECT "Id" FROM "AspNetUsers" WHERE "NormalizedEmail" = UPPER('seu-email@example.com');
```

Essa configuração permite acompanhar devoluções e exceções no painel. Não concede permissão
por e-mail nem cria administradores automaticamente. Os preços usam ponto decimal, devem ser
positivos e ter no máximo duas casas. Pedidos preservam o valor e o período contratados.

#### Configurar notificações e validar

1. Cadastre no painel de Webhooks o endereço `{MP_URL_PUBLICA}/pagamentos/mercado-pago/webhook`
   e habilite os eventos de **Orders** (criação e atualizações, incluindo devoluções).
   Para localhost, use um túnel HTTPS ou um ambiente de homologação acessível ao provedor.
2. Use credenciais e vendedor próprios do ambiente de teste, com `MP_PRODUCAO=false`.
   A API Orders distingue usuários de teste dos reais; não deduza o ambiente pelo prefixo
   do token. A verificação `/users/me` compara o ID e a marca `test_user` da conta.
   Confira as [instruções oficiais de teste Pix](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/integration-test/pix):
   o provedor usa cenários e valores predefinidos para simulação, não um Pix real de teste.
3. Teste as notificações assinadas e acompanhe a fila. A simulação de webhook sozinha não
   comprova um pagamento: o servidor consulta a order pelo token antes de liberar acesso.
4. Antes de publicar, configure os contatos reais em `Components/Pages/Responsavel.cs`,
   confira as taxas aplicáveis à conta e faça backup do PostgreSQL. As migrations incluem
   `AddMercadoPago`; as colunas e os estados dos pedidos antigos são preservados.
5. Em ambiente restrito, use a conta real e suas credenciais, `MP_PRODUCAO=true`, e valide
   um recebimento real com aprovação automática e atualização do acesso sem novo login.
   Só então libere novas compras ao público. Nunca pague um QR Code do ambiente de teste.

Sem credenciais, os testes automatizados usam um provedor fictício e não comprovam a
homologação da conta Mercado Pago. Para pausar novas compras, altere `MP_HABILITADO=false`
e reinicie o serviço. Mantenha as credenciais para confirmar pagamentos já emitidos;
webhooks e reconciliação continuam funcionando com novas compras pausadas.

#### Operação e compatibilidade

A criação persiste a referência e a chave de idempotência antes de chamar a API. Cliques
repetidos e respostas perdidas recuperam a mesma cobrança. O Pix vence em 24 horas;
pedidos pendentes do mesmo cliente/período, vendedor e ambiente são reaproveitados.
O site aguarda o estado confirmado pelo provedor antes de permitir substituir o pedido.

Notificações autenticadas são persistidas antes do HTTP 200. Um `BackgroundService`
independente da coleta Fipe processa a fila a cada cinco segundos; cobranças pendentes
são reconciliadas a cada cinco minutos. Reservas temporárias permitem retomada após
reinícios e evitam processamento simultâneo. Retentativas usam backoff e `Retry-After`.
Assinaturas seguem o manifesto HMAC oficial; replays não concedem meses novamente.

A confirmação confere referência, conta, ambiente, moeda BRL, meio Pix e valor integral,
e grava aprovação e extensão do acesso na mesma transação. A página consulta apenas o
estado local a cada cinco segundos enquanto visível. Ao aprovar, recarrega e atualiza a sessão.
Os meses partem da confirmação ou do vencimento futuro e usam o calendário.

Devoluções totais/parciais ficam registradas com **Revisão necessária** no painel; preservam
o acesso até revisão administrativa. Não é possível emitir devoluções pelo site nesta versão.
Monitore os alertas de sincronização do painel, os códigos de erro dos logs e as notificações
no painel Mercado Pago. Nunca registre corpos externos, tokens ou dados do pagador em logs.

Pedidos anteriores à migration são identificados como **Pix direto (pedido antigo)**,
mantêm código e histórico e podem ser informados/conferidos manualmente. A configuração
`PIX_HABILITADO` não reativa novas vendas diretas. Novos pedidos usam exclusivamente Mercado Pago.
A exclusão da conta apaga seus pedidos; notificações tardias não recriam a conta ou seu acesso.
As notificações guardam somente identificadores e são apagadas sete dias após processamento.

### E-mail (recuperação de senha)

Sem SMTP nenhum e-mail sai: em desenvolvimento o link aparece no log (aviso de `EmailConta`);
em produção só um erro é registrado, para não gravar tokens em log. Configure antes de publicar,
com a senha fora do `appsettings.json`:

```bash
Email__Host=smtp.seuprovedor.com
Email__Port=587
Email__Usuario=usuario-smtp
Email__Senha=senha-smtp
Email__Remetente="VALORDAFIPE <nao-responda@valordafipe.com.br>"
```

## Estrutura

```
src/
  FipeDados.Core/            # Entidades, enums, DTOs, parsing, cálculo de variação (puro/testável)
  FipeDados.Infrastructure/  # EF Core + Npgsql, FipeApiClient (resiliência), serviços, job de fundo
  FipeDados.Web/             # Blazor Web App: páginas, gráficos (ApexCharts)
  FipeDados.Scraper/         # Console de coleta em lote (catálogo + preços), retomável
tests/
  FipeDados.Tests/           # Testes de parsing, variação e mapeamento de DTOs (xUnit)
tools/
  smoke.cs                   # Smoke test manual contra a API real da Fipe (dotnet run tools/smoke.cs)
  pg-portable.ps1            # PostgreSQL portátil (start/stop/status) sem Docker
```

## Coleta mensal automática (Hangfire)

O site executa a coleta completa de **carros e motos no dia 1 às 03h, no fuso
America/Sao_Paulo**, incluindo marcas, modelos e anos novos. A primeira competência
é o mês seguinte ao primeiro startup com `ColetaMensal:Habilitada=true` (padrão).
Esse marco fica gravado no PostgreSQL: reiniciar ou publicar novamente não o altera.
Não há carga automática retroativa de meses anteriores.

O Hangfire usa a conexão `FipeDb`, com suas tabelas no schema `hangfire`; as migrations
do aplicativo criam `ColetaMensalConfiguracoes` e `ColetasMensais` para elegibilidade,
competência, referência, tentativas, estado e progresso. O usuário do banco precisa
poder criar o schema do Hangfire, como já ocorre com as migrations da aplicação.

Se a tabela do mês ainda não estiver publicada ou a coleta falhar, o site tenta
novamente às 03h do próximo dia, até o fim daquela competência. Execuções incompletas
ficam registradas; preços já obtidos, inclusive ausências confirmadas pela fonte,
são preservados. Erros de rede ou bloqueios não são gravados como ausência de preço.
Cada tentativa redescobre o catálogo na referência original e consulta só os preços
faltantes. Uma coleta completa pode durar dias no limite compartilhado de 0,6 req/s.
O progresso registra veículos percorridos, preços consultados e etapas com falha
na tentativa atual; o total coletado permanece nas tabelas de preços.

Há um único worker na fila `coleta` e um advisory lock no PostgreSQL para impedir
coletas mensais sobrepostas entre instâncias. O job compartilha o limitador e as
pausas por HTTP 429 com as consultas do próprio site. O coletor de console continua
disponível, mas seus limitadores são independentes: evite executá-lo durante o job.

Ao iniciar depois do horário agendado, o site recupera apenas o mês corrente elegível.
Um job interrompido em execução é recuperado pelo Hangfire com sua competência
original, mesmo após a virada do mês. Uma execução que já começou pode terminar
depois dessa virada; novas tentativas automáticas do mês encerrado são suspensas.
Uma verificação antiga que estava na fila considera somente o mês corrente.

Para operar localmente:

```powershell
.\tools\pg-portable.ps1 start
dotnet run --project src/FipeDados.Web
```

Mantenha o PostgreSQL **e o site** em execução. O script só inicia o banco; não inicia
o agendador. Em `Development`, acesse `/hangfire` por localhost para acompanhar os
jobs, disparar a verificação ou repetir uma execução que falhou. O painel recusa
acessos remotos e não é registrado em produção; nesse ambiente acompanhe os logs
do serviço `web` e o estado em `ColetasMensais`. A verificação manual respeita a
elegibilidade e o limite diário; repetir explicitamente um job de coleta com falha
pelo painel permite tentar de novo no mesmo dia, enquanto a competência estiver aberta.

Para suspender, configure `ColetaMensal__Habilitada=false` e reinicie todas as
instâncias do site. No Docker, use `COLETA_MENSAL_HABILITADA=false` no `.env` ou
`.env.vps` e recrie o serviço `web`. Isso preserva os dados e os jobs; ao reabilitar,
o Hangfire recupera os trabalhos pendentes conforme as regras acima.
O serviço diário de atualização das referências continua funcionando.

Os testes do job usam relógio controlado, API simulada e schemas PostgreSQL
temporários, incluindo o storage do Hangfire. Não realizam coleta contra a Fipe.

## Testes

```bash
dotnet test
```

Os testes de integração do ranking e de SEO usam PostgreSQL e criam/removem somente
schemas temporários. Para incluí-los na execução, defina `FIPEDADOS_TEST_CONNECTION` com
a connection string do banco de desenvolvimento antes de rodar `dotnet test`. Sem essa
variável, os testes que dependem do PostgreSQL são ignorados. A integração de SEO verifica
HTML inicial, metadados, paginação, sitemaps, respostas 404 e ausência de coleta na Fipe.
A de contas percorre cadastro, login, `ReturnUrl` externo, plano sem anúncios, troca e
recuperação de senha, bloqueio por tentativas, exclusão e logout sem token.
`DesempenhoTests` garante que as páginas de conteúdo não abrem circuito, vêm do OutputCache
e só carregam a ilha interativa quando o histórico está incompleto.

`tools/smoke.cs` faz uma verificação rápida contra a API real da Fipe (não precisa de banco):

```bash
dotnet run tools/smoke.cs
```

Para testar veículos aleatórios cujo histórico ainda não está na base, use a partir da raiz:

```bash
dotnet run --project tools/FipeDados.LiveSmoke -- 3
```

Para incluir também uma combinação de modelo/ano ainda fora do catálogo local, buscando
em uma referência anterior da própria Fipe:

```bash
dotnet run --project tools/FipeDados.LiveSmoke -- 1 --new
```

Esse teste usa a configuração do site e a API real, sorteia três veículos de marcas
distintas que só possuem a cotação atual, busca 24 referências e repete a consulta para
verificar o cache sem novas chamadas. Prioriza alternar carros e motos quando houver
candidatos. Os preços obtidos permanecem na base. O relatório
fica em `logs/live-smoke-results.json`; use de 1 a 5 veículos e evite executar o coletor
em paralelo, pois processos distintos têm limitadores independentes.
O modo `--new` salva o relatório em `logs/live-smoke-new-results.json` e mantém no banco
a combinação real de modelo/ano encontrada na fonte.

## Desempenho e teste de carga

- **Render mode:** roteamento em SSR estático com navegação aprimorada. Só `Explorar`
  (`/consultar-tabela-fipe`), `Comparar` e as ilhas marcadas com `@rendermode` abrem
  circuito Blazor. Home, catálogo, marca, modelo, veículo e páginas institucionais vêm
  prontas do servidor.
- **OutputCache (`Desempenho/PaginaCache.cs`):** páginas de conteúdo ficam 10 minutos em
  cache para visitantes anônimos, variando só por `tipo` e `pagina`. Veículo com histórico
  incompleto (ilha interativa) não é armazenado.
- **Fila da Fipe:** no máximo `Fipe:MaxFilaEspera` (20) consultas esperando; acima disso a
  histórico aguarda e tenta novamente em segundo plano. Uma coleta por veículo de cada vez; a coleta mensal
  não entra no teto.
- **Banco:** o pool do Npgsql fica em 40 conexões quando a connection string não define
  `Maximum Pool Size`, abaixo do limite padrão de 100 do PostgreSQL.
- **Métricas:** `GET /health/carga` (só chamada direta ao contêiner; pelo Nginx responde 404)
  devolve circuitos ativos, fila da Fipe, coletas em andamento, memória e threads. Os mesmos
  valores estão no medidor `FipeDados` para `dotnet-counters`. Na VPS:

```bash
curl -s 127.0.0.1:8082/health/carga
```

O teste de carga (`tools/FipeDados.Carga`) simula usuários navegando pelas páginas do
sitemap com pausa de leitura e abre circuitos Blazor reais na página de consulta (SignalR
+ blazorpack), confirmando cada render. Não dispara consultas à Fipe. Rode contra uma cópia
publicada em Release, com a coleta mensal desligada:

```bash
dotnet run -c Release --project tools/FipeDados.Carga -- --alvo http://localhost:5090 --usuarios 1000 --circuitos 200 --rampa 60 --duracao 120 --pausa 2-8
```

Parâmetros: `--usuarios` (navegação HTTP), `--circuitos`, `--rampa` e `--duracao` em
segundos, `--pausa min-max` em segundos entre cliques e `--veiculos` (URLs lidas do sitemap).
`CARGA_DEBUG=1` mostra o passo a passo de cada circuito.
