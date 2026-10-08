# Imagens do Beacon (a API e o serviço de estatísticas) em duas etapas: a primeira compila com o SDK,
# a segunda só roda (runtime do ASP.NET, sem o SDK nem o código-fonte).
#   docker build --target api -t beacon-api .
#   docker build --target estatisticas -t beacon-estatisticas .

# A compilação roda na arquitetura de quem monta a imagem (o PC, x86) e gera o programa para a de
# destino (TARGETARCH: arm64 na VM da Oracle). Sem isso, o Docker emularia ARM e levaria muito mais.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS compilacao
ARG TARGETARCH
WORKDIR /src
# Primeiro só os .csproj: enquanto as dependências não mudarem, o Docker reaproveita o restore
COPY global.json Directory.Build.props ./
COPY src/Beacon.Contratos/Beacon.Contratos.csproj src/Beacon.Contratos/
COPY src/Beacon.Api/Beacon.Api.csproj src/Beacon.Api/
COPY src/Beacon.Estatisticas/Beacon.Estatisticas.csproj src/Beacon.Estatisticas/
RUN dotnet restore src/Beacon.Api -a $TARGETARCH && dotnet restore src/Beacon.Estatisticas -a $TARGETARCH
COPY src src
# Os testes rodam no CI (precisam do Docker para o Testcontainers); aqui só publica
RUN dotnet publish src/Beacon.Api -c Release -a $TARGETARCH --no-restore -o /saida/api \
 && dotnet publish src/Beacon.Estatisticas -c Release -a $TARGETARCH --no-restore -o /saida/estatisticas \
 && mkdir /saida/chaves

# As duas imagens partem da mesma base: na VM, as camadas dela são baixadas uma vez só.
# A imagem do Ubuntu já traz o fuso de Brasília (tzdata), usado para contar os cliques por dia.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
# A imagem já tem o usuário "app" (sem root): se alguém achar uma falha, não manda no contêiner
USER app
WORKDIR /app

FROM base AS api
COPY --from=compilacao /saida/api .
# Pasta das chaves do cookie, já com o dono certo: um volume montado aqui herda a permissão.
# Vem pronta da etapa de compilação porque nenhum RUN pode rodar nas etapas ARM: num PC (ou no CI)
# x86, um RUN ali precisaria emular ARM.
COPY --from=compilacao --chown=app:app /saida/chaves /chaves
ENV ASPNETCORE_HTTP_PORTS=8080 DataProtection__Pasta=/chaves
EXPOSE 8080
ENTRYPOINT ["dotnet", "Beacon.Api.dll"]

FROM base AS estatisticas
COPY --from=compilacao /saida/estatisticas .
ENTRYPOINT ["dotnet", "Beacon.Estatisticas.dll"]
