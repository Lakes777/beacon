# Beacon

**Beacon · encurtador de links com estatísticas de cliques.**

[![CI](https://github.com/Lakes777/beacon/actions/workflows/ci.yml/badge.svg)](https://github.com/Lakes777/beacon/actions/workflows/ci.yml)

Você cria um link curto (por exemplo `.../r/curriculo-vaga-x`) que leva a qualquer endereço, e o Beacon
conta quantas pessoas clicaram, quando, de onde e por qual aparelho. A ideia nasceu de uma pergunta
real: colocando um link diferente no currículo de cada vaga, no LinkedIn e no GitHub, dá para saber se
alguém abriu o portfólio e por qual caminho chegou.

É o meu primeiro projeto em **C# com .NET**, com **RabbitMQ** (fila de mensagens) e **Redis** (cache).

![Painel do Beacon: cliques dos últimos 30 dias, lista de links e o detalhe de um link com origem, aparelho, navegador e sistema](docs/painel.png)

## Como funciona

```
 clique no link curto
        │
        ▼
 ┌──────────────┐  1. destino?   ┌───────┐
 │  API (.NET)  │ ─────────────► │ Redis │  cache: redireciona sem ir ao banco
 │              │                └───────┘
 │              │  2. "houve um clique" ──► RabbitMQ (fila) ──► serviço de estatísticas ──► PostgreSQL
 └──────────────┘     e redireciona na hora      (guarda os cliques    (origem, aparelho, navegador;
                                                  se o serviço cair)    sem contar em dobro)
```

O clique nunca espera pelas estatísticas: se o serviço que as calcula cair, os cliques ficam guardados
na fila e são contados quando ele voltar.

## Fases

- [x] **1. Esqueleto:** ASP.NET Core 10, PostgreSQL com Entity Framework Core e migrações, `/saude`, testes com Testcontainers, CI
- [x] **2. Links:** criar (código escolhido ou aleatório), listar, editar, apagar e redirecionar em `/r/{codigo}`; documentação em `/docs`
- [x] **3. Cache:** Redis na frente do redirecionamento, com invalidação quando o link muda e sem quebrar se o Redis cair
- [x] **4. Estatísticas:** cada clique vira uma mensagem no RabbitMQ; um segundo programa a consome, descobre navegador, sistema, aparelho e origem e grava sem contar em dobro
- [x] **5. Login:** conta única com senha (PBKDF2), sessão por cookie, limite de tentativas; toda rota de `/api` pede login, `/r/` continua pública
- [x] **6. Painel:** página servida pela própria API em `/`, com gráficos de cliques por dia, origem, aparelho, navegador e sistema; criar, editar e apagar links; identidade visual própria
- [ ] **7. Publicação:** imagens Docker para ARM, seis contêineres numa VM grátis da Oracle, HTTPS pelo Caddy, backup diário (pronta e testada; falta a VM ARM, que a Oracle ainda não liberou)

## Rotas

| Método | Rota | O que faz |
|---|---|---|
| `POST` | `/api/sessao` | Entra: `usuario` e `senha`; devolve o cookie da sessão (público, com limite de tentativas) |
| `GET` | `/api/sessao` | Quem está logado |
| `DELETE` | `/api/sessao` | Sai (público) |
| `POST` | `/api/links` | Cria um link: `destino` obrigatório, `codigo` opcional (sem ele, um aleatório de 7 caracteres) |
| `GET` | `/api/links` | Lista os links, do mais novo ao mais antigo |
| `GET` | `/api/links/{codigo}` | Busca um link |
| `PUT` | `/api/links/{codigo}` | Troca o `destino`; `ativo` opcional (omitido, mantém) |
| `DELETE` | `/api/links/{codigo}` | Apaga um link e os cliques dele |
| `GET` | `/api/estatisticas?dias=30` | Todos os links juntos: total, cliques por dia e cliques de cada link (o topo do painel) |
| `GET` | `/api/links/{codigo}/estatisticas?dias=30` | Cliques por dia (horário de Brasília), navegador, sistema, aparelho e origem; robôs contados à parte |
| `GET` | `/r/{codigo}` | Leva ao destino (302); 404 se não existe ou está desativado. O cabeçalho `X-Beacon-Cache` diz se veio do Redis (`HIT`) ou do banco (`MISS`) |
| `GET` | `/saude` | `Healthy` com tudo no ar; `Degraded` (200) sem o Redis ou o RabbitMQ; `Unhealthy` (503) sem o banco |
| `GET` | `/docs` | Página para testar a API no navegador (Scalar, a partir do OpenAPI em `/openapi/v1.json`) |

Toda rota de `/api` pede login (401 sem ele), menos entrar e sair. São públicas só `/r/{codigo}`,
`/saude`, `/docs`, `/openapi/v1.json` e os arquivos do painel (que não têm dados: os números vêm da API).

```bash
# entra e guarda o cookie em sessao.txt (a conta é criada com o comando definir-senha, abaixo)
curl -c sessao.txt -X POST localhost:8090/api/sessao -H 'Content-Type: application/json' \
  -d '{"usuario": "andre", "senha": "..."}'
curl -b sessao.txt -X POST localhost:8090/api/links -H 'Content-Type: application/json' \
  -d '{"destino": "https://lakes777.github.io", "codigo": "portfolio"}'
# {"codigo":"portfolio","destino":"https://lakes777.github.io","ativo":true,...,"urlCurta":"http://localhost:8090/r/portfolio"}
```

Erros vêm no formato padrão *problem details* (RFC 9457): 400 com os campos e o motivo de cada um,
404 e 409 (código em uso).

## Tecnologias

| Para quê | O que | No Vigil (Java), seria |
|---|---|---|
| Linguagem | C# 14 / .NET 10 | Java 21 |
| API | ASP.NET Core (Minimal APIs) | Spring Boot (`@RestController`) |
| Banco | PostgreSQL 17 + Entity Framework Core (Npgsql) | PostgreSQL + JPA/Hibernate |
| Cache | Redis 8 (StackExchange.Redis) | (o Vigil não tem cache) |
| Login | Autenticação por cookie + `PasswordHasher` (PBKDF2) + `RateLimiter` do ASP.NET | Spring Security |
| Fila de mensagens | RabbitMQ 4 (RabbitMQ.Client 7) | (o Vigil não tem fila) |
| Serviço em segundo plano | Worker Service (`BackgroundService`) | `@Scheduled` |
| User-Agent | MyCSharp.HttpUserAgentParser + lista própria de robôs | |
| Migrações | Migrações do EF Core, aplicadas ao subir | Flyway |
| Testes | xUnit v3 + `WebApplicationFactory` + Testcontainers | JUnit + `@SpringBootTest` + Testcontainers |
| Dependências | NuGet | Maven |

## Como rodar

Precisa do [.NET 10 SDK](https://dotnet.microsoft.com/download) e do Docker.

```bash
git clone https://github.com/Lakes777/beacon.git
cd beacon
docker compose up -d                            # Postgres (5434), Redis (6379) e RabbitMQ (5672)
dotnet run --project src/Beacon.Api             # API em http://localhost:8090
dotnet run --project src/Beacon.Estatisticas    # em outro terminal: o serviço que consome os cliques
curl localhost:8090/saude                  # "Healthy" quando a API e o banco estão no ar
```

Para entrar no painel, crie a conta (o mesmo comando troca a senha depois). A senha é pedida no
terminal, sem aparecer na tela:

```bash
dotnet run --project src/Beacon.Api -- definir-senha andre
```

As migrações rodam sozinhas quando a API sobe. Para criar uma nova depois de mudar as classes:

```bash
dotnet tool restore
dotnet ef migrations add NomeDaMudanca -p src/Beacon.Api -o Banco/Migracoes
```

## Testes

```bash
dotnet test
```

Precisa do Docker rodando: o Testcontainers sobe o Postgres sozinho.

Os testes sobem a API inteira em memória (`WebApplicationFactory`) ligada a um **Postgres de verdade**
num contêiner (Testcontainers), criado uma vez para todos os testes. O CI também confere a formatação
com `dotnet format`.

## Painel

Em `http://localhost:8090/` (o endereço da própria API). Feito com HTML, CSS e JavaScript puros, sem
biblioteca e sem etapa de build: os gráficos são SVG desenhados pelo script.

- **Mesma origem que a API:** o cookie da sessão (`SameSite=Strict`, `Path=/api`) vai sozinho, sem CORS.
- **Identidade própria:** o mar à noite (azul-marinho) e a luz do farol (âmbar). A logo é uma estrela de
  rumo das cartas náuticas, e os círculos no canto lembram as linhas de profundidade de uma carta. As fontes
  (Bricolage Grotesque e IBM Plex) são servidas pelo próprio Beacon, sem depender do Google Fonts.
- **Segurança:** os arquivos saem com `Content-Security-Policy` que só aceita o próprio Beacon (scripts,
  estilos, fontes e conexões), `X-Frame-Options: DENY` e `nosniff`. Tudo que vem da API entra na página
  como texto (`textContent`), nunca como HTML, então um destino com `<script>` aparece escrito, sem rodar.
- **No celular:** a lista e o detalhe ficam um embaixo do outro; tocar numa barra do gráfico mostra o dia.

## Publicação

Roda numa VM ARM grátis da Oracle Cloud (1 núcleo Ampere, 6 GB), com seis contêineres (`deploy/compose.yaml`):

| Contêiner | O que faz | Memória medida |
|---|---|---|
| `caddy` | recebe na porta 443, pega e renova sozinho o certificado HTTPS (Let's Encrypt) | ~25 MB |
| `api` | o Beacon (redirecionamento, API e painel) | ~240 MB |
| `estatisticas` | consome a fila e grava os cliques | ~40 MB |
| `banco` | Postgres 17 | ~40 MB |
| `rabbitmq` | a fila dos cliques, em disco (sobrevive a um reinício) | ~85 MB |
| `redis` | cache, só em memória (64 MB no máximo, apaga os menos usados) | ~6 MB |

- **Imagens para ARM montadas num PC x86, sem emulação** (`Dockerfile`): o SDK roda na arquitetura de quem
  monta (`--platform=$BUILDPLATFORM`) e o `dotnet publish -a arm64` gera o programa para a VM; as etapas ARM
  não têm nenhum `RUN`, só cópias, então nada precisa rodar como ARM durante a montagem (nem no CI). As duas
  imagens partem da mesma base (`aspnet:10.0-noble`, com o fuso de Brasília), rodam sem root e só têm o
  programa publicado, sem o SDK e sem o código-fonte.
- **Atrás do proxy:** o Caddy avisa o IP e o `https` verdadeiros (`X-Forwarded-For` e `-Proto`), e a API só
  acredita nesses cabeçalhos vindos da rede interna do Docker (`Proxy__Rede`). Assim o cookie sai com
  `Secure`, a `urlCurta` vem com `https://` e o limite de login conta o IP de quem acessou, e não o do Caddy.
  `AllowedHosts` recusa pedidos para outro domínio.
- **Chaves do cookie num volume:** sem isso, cada versão nova da imagem trocaria as chaves e deslogaria todo mundo.
- **Só o Caddy tem porta aberta.** Banco, fila e cache conversam só pela rede interna, com as senhas no
  `.env` da VM (fora do Git).
- Logs com rotação (10 MB × 3 por contêiner).
- **Backup:** todo dia às 3h o cron da VM grava o banco (7 dias na VM), e o PC traz uma cópia para fora dela
  (`deploy/trazer-backup.sh`, pelo Agendador de Tarefas do Windows, 30 dias em `D:\Backups\Beacon`): se a VM
  sumir, o backup não vai junto.
- **VM reiniciada:** o Docker religa tudo junto (`restart: unless-stopped`) e ignora a ordem do `depends_on`.
  A API pode chegar antes do banco e reiniciar algumas vezes até ele aceitar conexões; em menos de um minuto
  o site volta sozinho.

Publicar uma versão nova (monta as imagens aqui, envia pela conexão SSH e reinicia). Só publica com tudo
commitado, e cada imagem leva o número do commit:

```bash
deploy/publicar.sh
```

Voltar uma versão (a VM guarda as 3 últimas de cada imagem):

```bash
docker images beacon-api                       # na VM: as versões guardadas
docker tag beacon-api:<commit> beacon-api:latest
docker tag beacon-estatisticas:<commit> beacon-estatisticas:latest
docker compose up -d
```

Restaurar um backup (apaga o que está no banco e põe o do arquivo):

```bash
docker compose stop api estatisticas
docker compose exec -T banco psql -U beacon -d postgres -c "DROP DATABASE beacon WITH (FORCE)" -c "CREATE DATABASE beacon OWNER beacon"
gunzip -c backups/beacon-AAAA-MM-DD.sql.gz | docker compose exec -T banco psql -U beacon -d beacon
docker compose start api estatisticas
```

Primeira vez na VM:

1. Instalar o Docker: `sudo apt install docker.io docker-compose-v2` e `sudo usermod -aG docker ubuntu`.
2. Abrir as portas 80 e 443 na *Security List* da Oracle e no firewall da VM.
3. Criar `~/beacon/.env` (só o dono lê: `chmod 600`) com `DOMINIO`, `DB_SENHA` e `FILA_SENHA`. Gere as
   senhas com `openssl rand -hex 32` (só letras e números): elas vão no meio da conexão do banco e do
   endereço `amqp://` da fila, onde um `;`, `@` ou `/` quebraria tudo. O Postgres e o RabbitMQ só leem a
   senha na primeira subida; trocar no `.env` depois não muda a senha guardada no volume.
4. Rodar `deploy/publicar.sh` e criar a conta do painel:
   `docker compose exec api dotnet Beacon.Api.dll definir-senha <nome>`.
5. Backup: `0 3 * * * ~/beacon/backup.sh` no `crontab -e`.

## Login

O Beacon é de uma pessoa só, então não há cadastro: a conta é criada pelo comando `definir-senha`, por
quem tem acesso ao servidor. O resto fica com o que o ASP.NET já traz:

- **Senha:** guardada só como hash PBKDF2 com sal (`PasswordHasher`, o mesmo do ASP.NET Identity), com
  12 a 128 caracteres. O limite de cima existe porque o PBKDF2 calcula sobre a senha inteira.
- **Sessão:** cookie `beacon_sessao` cifrado pelo Data Protection, válido por 7 dias e renovado com o
  uso. `HttpOnly` (um script na página não o lê), `SameSite=Strict` (não vai em pedidos que partem de
  outro site, o que barra CSRF) e `Path=/api` (não viaja nos cliques de `/r/`). Segunda camada contra CSRF: um
  formulário de outro site só envia GET e POST, e não em JSON; um `fetch` com JSON, `PUT` ou `DELETE` precisaria
  da permissão do CORS, que a API não dá.
- **Trocar a senha derruba as sessões abertas:** a conta tem um carimbo que muda a cada troca e vai
  dentro do cookie. A cada pedido o carimbo é conferido no banco, e um cookie com o carimbo velho deixa de valer.
- **Seguro por padrão:** a política de autorização padrão pede login, e as rotas públicas dizem
  `AllowAnonymous`. Uma rota nova esquecida nasce protegida, e não aberta.
- **Adivinhação:** 10 tentativas de login por minuto por IP (`RateLimiter`, 429 com `Retry-After`); em IPv6,
  por bloco /64, que é o que um servidor alugado costuma ter.
  Usuário inexistente e senha errada dão a mesma resposta, no mesmo tempo (o hash é conferido
  mesmo sem conta), para não revelar quais nomes existem.

## Cache

O redirecionamento é o caminho mais usado (cada clique passa por ele), então o Redis fica na frente
do Postgres, no padrão *cache-aside*:

1. O clique em `/r/{codigo}` consulta o Redis (`beacon:link:{codigo}`).
2. Se o Redis não souber, busca no Postgres e guarda a resposta por **10 minutos**.
3. Criar, editar, desativar ou apagar um link **apaga a cópia** na hora (depois de gravar no banco).

Detalhes:

- **"Não existe" também fica guardado**, por 1 minuto: tentativas repetidas com um código errado (ou
  um robô testando códigos) não vão ao banco. Criar um link com esse código apaga a marca, então ele
  funciona na hora.
- **Redis fora do ar não derruba nada:** cada operação de cache engole o erro e o clique vai ao banco
  (mais lento, mas funciona). A API sobe mesmo sem o Redis e reconecta sozinha; o `/saude` fica
  `Degraded`, e não `Unhealthy`, para o Docker não reiniciar a API à toa. O limite de espera é de
  500 ms: um Redis lento não segura o clique.
- **Por que o prazo de 10 minutos, se a cópia é apagada a cada mudança?** Há um caso raro em que a
  cópia velha volta: um clique lê o destino antigo no banco, a edição grava o novo e apaga a cópia,
  e só então o clique guarda o destino antigo no Redis. O mesmo vale para os outros estados: um link
  recém-criado pode dar 404 por até 1 minuto (o "não existe" de um clique de antes), e um link
  desativado pode continuar redirecionando por até 10 minutos. O prazo limita quanto esse erro dura.
  (Para zerar de vez, daria para guardar uma versão do link junto com o destino.)
- **Redis caído falha na hora** (`BacklogPolicy.FailFast`): o padrão da biblioteca guarda os comandos
  numa fila esperando a conexão voltar, e cada clique esperava cerca de 2 s. A queda e a volta da
  conexão geram um aviso cada no log, e não um por clique.
- **Só códigos possíveis chegam ao cache:** `/r/` seguido de um texto que nunca seria um código
  (longo demais, com símbolos) responde 404 na hora, sem criar chave no Redis nem consultar o banco.
- **`X-Beacon-Cache` fora da produção:** ele diria a qualquer visitante se aquele link foi clicado
  nos últimos minutos.

## Estatísticas e fila

São dois programas que conversam só pela fila (o contrato fica em `src/Beacon.Contratos`):

- **API (`Beacon.Api`):** o clique em `/r/{codigo}` entrega uma mensagem `CliqueRegistrado` a uma fila
  em memória e redireciona na hora. Um serviço em segundo plano publica no RabbitMQ, com mensagem
  persistente e confirmação do broker; se o RabbitMQ estiver fora, segura a mensagem e tenta de novo.
  A fila em memória tem limite (10 mil): cheia, descarta e avisa, para nunca segurar o clique.
- **Serviço de estatísticas (`Beacon.Estatisticas`):** consome a fila (`ack` manual), classifica o
  User-Agent e o Referer e grava com `INSERT ... ON CONFLICT (id) DO NOTHING`. Só depois de gravar
  confirma a mensagem (`ack`).

Por que assim:

- **"Pelo menos uma vez":** o RabbitMQ pode entregar a mesma mensagem de novo (ex.: o serviço caiu
  depois de gravar e antes do `ack`). Cada clique tem um `Id` gerado na API e usado como chave
  primária, então a repetição não conta em dobro (consumidor *idempotente*).
- **Limite do que não se perde:** os cliques que ainda estão na memória da API (RabbitMQ fora do ar)
  se perdem se a API reiniciar nessa hora; com o RabbitMQ no ar, ela publica o que falta antes de
  desligar. A URL de origem vai sem o que vem depois de `?` ou `#` (tokens, e-mails, buscas).
- **Se o serviço de estatísticas cair, nada se perde:** os cliques ficam na fila (durável) e são
  gravados quando ele voltar. Se o banco cair, a mensagem volta para a fila com espera crescente.
- **Mensagem inválida vai para a "dead letter"** (`beacon.estatisticas.mortos`) em vez de travar a
  fila ou sumir: dá para olhar depois no painel do RabbitMQ (http://localhost:15672).
- **Robôs à parte:** prévias de link (LinkedIn, WhatsApp, Slack...) e robôs de busca "clicam" sozinhos.
  São gravados, mas ficam fora das contas (aparecem só no total `robos`). Detectados por uma lista
  própria antes da biblioteca, que não conhece todas as prévias.
- **Só conta o que veio depois da criação do link:** um código apagado e criado de novo não herda
  cliques antigos que ainda estavam na fila.

## Decisões

- **Minimal APIs** em vez de controllers: no .NET atual, as rotas podem ser declaradas direto
  (`app.MapGet("/saude", ...)`), sem uma classe por grupo de rotas. Menos cerimônia para uma API pequena.
- **`Ativo` sem valor padrão no banco:** com `DEFAULT true` no Postgres, o Entity Framework deixaria de
  enviar um `false` (é o valor padrão de um `bool` no C#) e o banco gravaria `true`. Um teste garante isso.
- **Código único no banco** (índice `UNIQUE`), e não só uma checagem no C#: dois pedidos ao mesmo tempo
  com o mesmo código não passam os dois.
- **Redirecionamento 302, não 301:** o navegador guarda um 301 para sempre e deixa de passar pelo
  Beacon, então trocar o destino e contar os cliques pararia de funcionar.
- **Códigos em minúsculas:** `Curriculo` e `curriculo` são o mesmo link, para ninguém errar ao digitar.
  Os gerados não usam `l`, `o`, `0` nem `1`, que se confundem num currículo impresso, e são sorteados
  com gerador criptográfico (não dá para adivinhar os links dos outros pela sequência).
- **Só destinos `http` e `https`:** um `javascript:...` viraria um link curto que roda código no
  navegador de quem clica.
- **Destino guardado só com ASCII:** `Programação` vira `Programa%C3%A7%C3%A3o` e um domínio com
  acento vira `xn--...`. O servidor recusa caracteres fora do ASCII no cabeçalho `Location`, e o
  link daria erro a cada clique. O limite de 2.048 caracteres vale depois dessa conversão.
- **Sem usuário e senha no destino:** `https://google.com@site-falso.com` parece Google e leva a
  outro lugar, e uma senha na URL ficaria guardada e aparecendo na lista.
- **Código em uso:** uma consulta antes do `INSERT` responde o caso comum sem deixar erro no log; o
  índice único continua decidindo quando dois pedidos chegam juntos (o perdedor recebe 409).
- **Avisos do compilador viram erro** (`TreatWarningsAsErrors`), principalmente os de possível `null`.
- **A API não sobe sem a conexão com o banco configurada**, com uma mensagem dizendo o que falta.
