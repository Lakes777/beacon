# Beacon

**Beacon · encurtador de links com estatísticas de cliques.**

[![CI](https://github.com/Lakes777/beacon/actions/workflows/ci.yml/badge.svg)](https://github.com/Lakes777/beacon/actions/workflows/ci.yml)

Você cria um link curto (por exemplo `.../r/curriculo-vaga-x`) que leva a qualquer endereço, e o Beacon
conta quantas pessoas clicaram, quando, de onde e por qual aparelho. A ideia nasceu de uma pergunta
real: colocando um link diferente no currículo de cada vaga, no LinkedIn e no GitHub, dá para saber se
alguém abriu o portfólio e por qual caminho chegou.

É o meu primeiro projeto em **C# com .NET**, e também vai ser o primeiro com **RabbitMQ** (fila de mensagens)
e **Redis** (cache), nas fases 3 e 4.

## Como vai funcionar

```
 clique no link curto
        │
        ▼
 ┌──────────────┐  1. destino?   ┌───────┐
 │  API (.NET)  │ ─────────────► │ Redis │  cache: redireciona sem ir ao banco
 │              │                └───────┘
 │              │  2. "houve um clique" ──► RabbitMQ (fila) ──► serviço de estatísticas ──► PostgreSQL
 └──────────────┘     e redireciona na hora      (guarda os cliques    (país, aparelho, navegador;
                                                  se o serviço cair)    sem contar em dobro)
```

O clique nunca espera pelas estatísticas: se o serviço que as calcula cair, os cliques ficam guardados
na fila e são contados quando ele voltar.

## Fases

- [x] **1. Esqueleto:** ASP.NET Core 10, PostgreSQL com Entity Framework Core e migrações, `/saude`, testes com Testcontainers, CI
- [x] **2. Links:** criar (código escolhido ou aleatório), listar, editar, apagar e redirecionar em `/r/{codigo}`; documentação em `/docs`
- [ ] 3. Redis como cache do redirecionamento, com invalidação quando o link muda
- [ ] 4. RabbitMQ e o serviço de estatísticas (sem contar o mesmo clique duas vezes)
- [ ] 5. Login do painel e filtro de robôs (prévias do LinkedIn e do WhatsApp não contam)
- [ ] 6. Painel com gráficos, com identidade visual própria
- [ ] 7. Publicação com Docker (com `UseForwardedHeaders` e `AllowedHosts`: hoje a `urlCurta` usa o
  endereço e o esquema do pedido, que atrás do proxy seriam os internos)

## Rotas

| Método | Rota | O que faz |
|---|---|---|
| `POST` | `/api/links` | Cria um link: `destino` obrigatório, `codigo` opcional (sem ele, um aleatório de 7 caracteres) |
| `GET` | `/api/links` | Lista os links, do mais novo ao mais antigo |
| `GET` | `/api/links/{codigo}` | Busca um link |
| `PUT` | `/api/links/{codigo}` | Troca o `destino`; `ativo` opcional (omitido, mantém) |
| `DELETE` | `/api/links/{codigo}` | Apaga um link |
| `GET` | `/r/{codigo}` | Leva ao destino (302); 404 se não existe ou está desativado |
| `GET` | `/saude` | `Healthy` se a API e o banco estão no ar |
| `GET` | `/docs` | Página para testar a API no navegador (Scalar, a partir do OpenAPI em `/openapi/v1.json`) |

Por enquanto as rotas de `/api/links` não pedem login: o painel com login chega na fase 5, antes de publicar.

```bash
curl -X POST localhost:8090/api/links -H 'Content-Type: application/json' \
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
| Migrações | Migrações do EF Core, aplicadas ao subir | Flyway |
| Testes | xUnit v3 + `WebApplicationFactory` + Testcontainers | JUnit + `@SpringBootTest` + Testcontainers |
| Dependências | NuGet | Maven |

## Como rodar

Precisa do [.NET 10 SDK](https://dotnet.microsoft.com/download) e do Docker.

```bash
git clone https://github.com/Lakes777/beacon.git
cd beacon
docker compose up -d                       # Postgres na porta 5434
dotnet run --project src/Beacon.Api        # API em http://localhost:8090
curl localhost:8090/saude                  # "Healthy" quando a API e o banco estão no ar
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
