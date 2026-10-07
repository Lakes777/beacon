// O painel do Beacon: entrar, ver os números, criar, editar e apagar links.
import { api, SemSessao } from "./api.js";
import { barrasPorDia, el, listaDeBarras } from "./graficos.js";

const $ = id => document.getElementById(id);
const PERIODOS = [7, 30, 90];
let ultimaCarga = 0;
// Cada carga ganha um número; uma resposta que chega depois de outra carga mais nova é descartada
// (clicar em "90 dias" e logo em "7 dias" não pode deixar os números de 90 na tela)
let cargaAtual = 0;
let detalheAtual = 0;

const estado = {
  dias: lerPreferencia(),
  links: [],
  cliquesPorLink: new Map(),
  // O link aberto fica no endereço (#codigo): recarregar a página volta para ele
  selecionado: lerEndereco(),
};

function lerEndereco() {
  // Um "#100%" digitado à mão não é um código válido: sem o try, o script pararia aqui
  try { return decodeURIComponent(location.hash.slice(1)) || null; } catch { return null; }
}

/** Desliga o botão enquanto a ação roda: um clique duplo não cria dois links nem apaga duas vezes. */
async function ocupado(botao, acao) {
  if (botao.disabled) return;
  botao.disabled = true;
  try { await acao(); } finally { botao.disabled = false; }
}

// ---------- preferências (só conveniência: sem localStorage, usa 30 dias) ----------
function lerPreferencia() {
  try {
    const dias = Number(localStorage.getItem("beacon.dias"));
    return PERIODOS.includes(dias) ? dias : 30;
  } catch { return 30; }
}
function guardarPreferencia(dias) {
  try { localStorage.setItem("beacon.dias", String(dias)); } catch { /* aba anônima: tudo bem */ }
}

// ---------- avisos ----------
let temporizadorDoAviso;
function avisar(texto, ruim = false) {
  const aviso = $("aviso");
  aviso.textContent = texto;
  aviso.classList.toggle("ruim", ruim);
  aviso.hidden = false;
  clearTimeout(temporizadorDoAviso);
  temporizadorDoAviso = setTimeout(() => { aviso.hidden = true; }, 3200);
}

function mostrarErro(elemento, erro) {
  // Erro 400 da API: mostra o motivo de cada campo ("destino: Use um endereço completo...")
  const porCampo = Object.values(erro.erros ?? {}).flat();
  elemento.textContent = porCampo.length ? porCampo.join(" ") : erro.message;
  elemento.hidden = false;
}

/** Roda uma ação da API; se a sessão acabou, volta para a entrada. */
async function comSessao(acao) {
  try {
    return await acao();
  } catch (erro) {
    if (erro instanceof SemSessao) {
      mostrarEntrada("Sua sessão terminou. Entre de novo.");
      return undefined;
    }
    throw erro;
  }
}

// ---------- entrada ----------
function mostrarEntrada(mensagem) {
  $("carregando").hidden = true;
  $("painel").hidden = true;
  $("entrada").hidden = false;
  const erro = $("erro-entrada");
  erro.textContent = mensagem ?? "";
  erro.hidden = !mensagem;
  $("senha").value = "";
  ($("usuario").value ? $("senha") : $("usuario")).focus();
}

$("form-entrada").addEventListener("submit", async e => {
  e.preventDefault();
  const botao = e.submitter ?? e.target.querySelector("button");
  botao.disabled = true;
  $("erro-entrada").hidden = true;
  try {
    const sessao = await api.entrar($("usuario").value, $("senha").value);
    await mostrarPainel(sessao.usuario);
  } catch (erro) {
    mostrarErro($("erro-entrada"), erro);
    $("senha").select();
  } finally {
    botao.disabled = false;
  }
});

$("sair").addEventListener("click", async () => {
  await api.sair().catch(() => {});
  estado.selecionado = null;
  history.replaceState(null, "", location.pathname);
  mostrarEntrada();
});

// ---------- painel ----------
async function mostrarPainel(usuario) {
  $("nome-usuario").textContent = usuario;
  $("carregando").hidden = true;
  $("entrada").hidden = true;
  $("painel").hidden = false;
  marcarPeriodo();
  await carregar();
}

function marcarPeriodo() {
  for (const botao of document.querySelectorAll(".periodo button")) {
    botao.setAttribute("aria-pressed", String(Number(botao.dataset.dias) === estado.dias));
  }
  $("rotulo-cliques").textContent = `cliques em ${estado.dias} dias`;
  $("d-rotulo-total").textContent = `cliques em ${estado.dias} dias`;
}

for (const botao of document.querySelectorAll(".periodo button")) {
  botao.addEventListener("click", async () => {
    estado.dias = Number(botao.dataset.dias);
    guardarPreferencia(estado.dias);
    marcarPeriodo();
    await carregar();
  });
}

/** Busca a lista e os números do período (e o detalhe do link aberto, se houver). */
async function carregar() {
  ultimaCarga = Date.now();
  const minha = ++cargaAtual;
  await comSessao(async () => {
    try {
      const [links, resumo] = await Promise.all([api.links(), api.resumo(estado.dias)]);
      if (minha !== cargaAtual) return;
      estado.links = links;
      estado.cliquesPorLink = new Map(resumo.porLink.map(l => [l.codigo, l.cliques]));
      desenharResumo(resumo);
      desenharLista();
      if (estado.selecionado && links.some(l => l.codigo === estado.selecionado)) {
        await abrirDetalhe(estado.selecionado);
      } else {
        fecharDetalhe();
      }
    } catch (erro) {
      if (erro instanceof SemSessao) throw erro;
      avisar(erro.message, true);
    }
  });
}

function desenharResumo(resumo) {
  $("total-cliques").textContent = resumo.total.toLocaleString("pt-BR");
  $("total-ativos").textContent = estado.links.filter(l => l.ativo).length;
  $("total-robos").textContent = resumo.robos.toLocaleString("pt-BR");
  barrasPorDia($("grafico-geral"), resumo.porDia);
}

function desenharLista() {
  const lista = $("links");
  lista.replaceChildren(...estado.links.map(link => {
    const cliques = estado.cliquesPorLink.get(link.codigo) ?? 0;
    const item = el("button", {
      type: "button",
      class: "item" + (link.ativo ? "" : " inativo"),
      "aria-current": link.codigo === estado.selecionado ? "true" : null,
    },
      el("span", { class: "codigo" }, `/r/${link.codigo}`,
        link.ativo ? null : el("span", { class: "estado inativo" }, "desativado")),
      el("span", { class: "cliques" }, el("b", {}, cliques.toLocaleString("pt-BR")), el("small", {}, cliques === 1 ? "clique" : "cliques")),
      el("span", { class: "para" }, link.destino));
    item.addEventListener("click", () => selecionar(link.codigo));
    return el("li", {}, item);
  }));
  $("sem-links").hidden = estado.links.length > 0;
}

async function selecionar(codigo) {
  estado.selecionado = codigo;
  history.replaceState(null, "", `#${encodeURIComponent(codigo)}`);
  desenharLista();
  await comSessao(() => abrirDetalhe(codigo).catch(erro => {
    if (erro instanceof SemSessao) throw erro;
    avisar(erro.message, true);
  }));
  // No celular o detalhe fica embaixo da lista: leva até ele
  if (matchMedia("(max-width: 860px)").matches) $("detalhe").scrollIntoView({ behavior: "smooth", block: "start" });
}

function fecharDetalhe() {
  estado.selecionado = null;
  $("conteudo-detalhe").hidden = true;
  $("sem-detalhe").hidden = false;
}

// ---------- detalhe de um link ----------
async function abrirDetalhe(codigo) {
  const link = estado.links.find(l => l.codigo === codigo);
  const minha = ++detalheAtual;
  const numeros = await api.estatisticas(codigo, estado.dias);
  if (minha !== detalheAtual) return;   // outro link ou outro período foi pedido enquanto este carregava

  $("d-codigo").textContent = link.codigo;
  const situacao = $("d-estado");
  situacao.textContent = link.ativo ? "ativo" : "desativado";
  situacao.className = `estado ${link.ativo ? "ativo" : "inativo"}`;
  $("d-url").textContent = link.urlCurta;
  $("d-destino").textContent = link.destino;
  $("d-destino").href = link.destino;
  $("d-total").textContent = numeros.total.toLocaleString("pt-BR");
  $("d-robos").textContent = numeros.robos.toLocaleString("pt-BR");
  barrasPorDia($("grafico-link"), numeros.porDia);
  listaDeBarras($("d-origens"), numeros.origens, numeros.total);
  listaDeBarras($("d-aparelhos"), numeros.aparelhos, numeros.total);
  listaDeBarras($("d-navegadores"), numeros.navegadores, numeros.total);
  listaDeBarras($("d-sistemas"), numeros.sistemas, numeros.total);

  // O formulário só é preenchido ao trocar de link: recarregar os números não apaga o que está sendo digitado
  const form = $("form-editar");
  if (form.dataset.codigo !== codigo) {
    form.dataset.codigo = codigo;
    $("editar-destino").value = link.destino;
    $("editar-ativo").checked = link.ativo;
    $("erro-editar").hidden = true;
    $("confirmar-apagar").hidden = true;
  }
  $("sem-detalhe").hidden = true;
  $("conteudo-detalhe").hidden = false;
}

$("d-copiar").addEventListener("click", async () => {
  const texto = $("d-url").textContent;
  try {
    await navigator.clipboard.writeText(texto);
    avisar("Link copiado");
  } catch {
    // Sem permissão para a área de transferência: seleciona o texto para copiar com Ctrl+C
    getSelection().selectAllChildren($("d-url"));
    avisar("Selecionado: aperte Ctrl+C para copiar");
  }
});

$("form-editar").addEventListener("submit", async e => {
  e.preventDefault();
  const codigo = estado.selecionado;
  $("erro-editar").hidden = true;
  await ocupado(e.submitter ?? e.target.querySelector("[type=submit]"), () => comSessao(async () => {
    try {
      await api.editar(codigo, $("editar-destino").value, $("editar-ativo").checked);
      avisar("Link salvo");
    } catch (erro) {
      if (erro instanceof SemSessao) throw erro;
      mostrarErro($("erro-editar"), erro);
      return;
    }
    e.target.dataset.codigo = "";   // preenche o formulário de novo com o que foi salvo
    await carregar();
  }));
});

$("apagar").addEventListener("click", () => {
  $("confirmar-codigo").textContent = `/r/${estado.selecionado}`;
  $("confirmar-apagar").hidden = false;
  $("confirmar-nao").focus();
});
$("confirmar-nao").addEventListener("click", () => {
  $("confirmar-apagar").hidden = true;
  $("apagar").focus();
});
$("confirmar-sim").addEventListener("click", e => ocupado(e.currentTarget, () => comSessao(async () => {
  const codigo = estado.selecionado;
  try {
    await api.apagar(codigo);
  } catch (erro) {
    if (erro instanceof SemSessao) throw erro;
    avisar(erro.message, true);
    return;
  }
  $("confirmar-apagar").hidden = true;
  $("form-editar").dataset.codigo = "";
  history.replaceState(null, "", location.pathname);
  fecharDetalhe();
  avisar(`/r/${codigo} apagado`);
  // O botão sumiu junto com o detalhe: o foco volta para a lista, e não para o começo da página
  $("titulo-links").focus();
  await carregar();
})));

// ---------- novo link ----------
function abrirNovo(abrir) {
  $("form-novo").hidden = !abrir;
  $("abrir-novo").setAttribute("aria-expanded", String(abrir));
  $("erro-novo").hidden = true;
  if (abrir) $("novo-destino").focus();
}
$("abrir-novo").addEventListener("click", () => abrirNovo($("form-novo").hidden));
$("cancelar-novo").addEventListener("click", () => abrirNovo(false));

$("form-novo").addEventListener("submit", async e => {
  e.preventDefault();
  $("erro-novo").hidden = true;
  await ocupado(e.submitter ?? e.target.querySelector("[type=submit]"), () => comSessao(async () => {
    try {
      const link = await api.criar($("novo-destino").value, $("novo-codigo").value.trim());
      e.target.reset();
      abrirNovo(false);
      avisar(`/r/${link.codigo} criado`);
      estado.selecionado = link.codigo;
      history.replaceState(null, "", `#${encodeURIComponent(link.codigo)}`);
      await carregar();
    } catch (erro) {
      if (erro instanceof SemSessao) throw erro;
      mostrarErro($("erro-novo"), erro);
    }
  }));
});

// Voltando para a aba depois de um tempo, os números podem ter mudado
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && !$("painel").hidden && Date.now() - ultimaCarga > 60_000) {
    carregar();
  }
});

// ---------- início: já tem sessão? ----------
try {
  const sessao = await api.sessao();
  await mostrarPainel(sessao.usuario);
} catch (erro) {
  if (erro instanceof SemSessao) mostrarEntrada();
  else { $("carregando").textContent = erro.message; }
}
