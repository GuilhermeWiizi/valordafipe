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
    O servidor sobe desanexado (via WMI), então não morre quando o terminal, a IDE ou o agente
    que rodou o script é fechado. Não é serviço do Windows: após reiniciar o PC, rode `start`
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
do banco e confirma a resposta HTTP depois das migrations. A atualização pode causar
uma breve interrupção do FIPE. Se falhar, mostra o erro e retorna código diferente de zero.
O `.env.vps` permanece no servidor e é ignorado pelo Git.

Para outra pasta, branch ou conexão, defina `DEPLOY_PATH`, `DEPLOY_BRANCH` ou `DEPLOY_HOST`
no ambiente antes de executar o `.bat`. Use `--no-pause` para execução automatizada.

Para conferir diretamente na VPS:

```bash
cd /srv/FipeDados
docker compose -p fipedados --env-file .env.vps -f docker-compose.vps.yml ps
docker compose -p fipedados --env-file .env.vps -f docker-compose.vps.yml logs -f web
curl -I http://127.0.0.1:8082/robots.txt
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
cp deploy/nginx/valordafipe.conf /etc/nginx/sites-available/valordafipe.com.br
ln -s /etc/nginx/sites-available/valordafipe.com.br /etc/nginx/sites-enabled/valordafipe.com.br
nginx -t && systemctl reload nginx
curl -I -H 'Host: valordafipe.com.br' http://127.0.0.1/robots.txt
```

Instale o arquivo e crie o link somente na configuração inicial. Após o Certbot configurar
HTTPS, edite o arquivo já instalado para ajustes; copiar novamente o modelo HTTP por cima
remove os blocos de certificado adicionados por ele. Os deploys do aplicativo não alteram
os arquivos em `/etc/nginx`.

Com o DNS propagado e HTTP acessível, use o Certbot já instalado na VPS:

```bash
certbot --nginx -d valordafipe.com.br --redirect
curl -I https://valordafipe.com.br/robots.txt
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
  com a seleção preenchida. As sugestões usam apenas a base; a consulta automática do
  histórico busca na Fipe os preços ausentes após a página ficar interativa.

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
  continuar bloqueando, a tela informa o limite e permite tentar novamente, preservando
  os preços já salvos. Respostas vazias ou inválidas são falhas, não meses sem cotação.
- **Job de fundo:** `ReferenceTableRefreshService` mantém as tabelas de referência (24 meses)
  atualizadas e pré-carrega as marcas no startup.

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

Login e cadastro existem para o futuro **plano sem anúncios**. Nenhuma página pública exige
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
  anúncios. O valor vai no cookie e é renovado a cada 30 minutos; ao estender o plano após um
  pagamento, chame `SignInManager.RefreshSignInAsync` para valer na hora.
- **AdSense:** defina `Anuncios__AdSenseClientId=ca-pub-...` para carregar o script (Auto ads) no
  `<head>`. Vazio = nenhum anúncio. Contas no plano sem anúncios nunca carregam o script.

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
