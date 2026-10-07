// Gráficos em SVG feitos à mão (sem biblioteca): barras por dia e listas com barrinhas.
// Tudo que vem dos dados entra como texto (textContent), nunca como HTML.

const SVG = "http://www.w3.org/2000/svg";

/** Cria um elemento HTML: el("li", { class: "x" }, "texto", outroElemento). */
export function el(tag, atributos = {}, ...filhos) {
  const elemento = document.createElement(tag);
  for (const [nome, valor] of Object.entries(atributos)) {
    if (valor !== undefined && valor !== null && valor !== false) elemento.setAttribute(nome, valor === true ? "" : valor);
  }
  elemento.append(...filhos.filter(f => f !== null && f !== undefined));
  return elemento;
}

function svg(tag, atributos) {
  const elemento = document.createElementNS(SVG, tag);
  for (const [nome, valor] of Object.entries(atributos)) elemento.setAttribute(nome, valor);
  return elemento;
}

const data = dia => { const [, m, d] = dia.split("-"); return `${d}/${m}`; };
const plural = (n, um, varios) => `${n.toLocaleString("pt-BR")} ${n === 1 ? um : varios}`;

/**
 * Barras de cliques por dia (o último dia é hoje, em âmbar). Passar o mouse, tocar ou usar as setas
 * mostra o dia e o número. porDia: [{ dia: "2026-10-07", cliques: 3 }, ...]
 */
export function barrasPorDia(container, porDia) {
  container.replaceChildren();
  const largura = 600, altura = 120, n = porDia.length;
  const maximo = Math.max(1, ...porDia.map(d => d.cliques));
  const passo = largura / n;
  const folga = n > 45 ? 0.6 : n > 14 ? 1.5 : 4;
  const y = v => altura - (v / maximo) * (altura - 14);

  const desenho = svg("svg", { viewBox: `0 0 ${largura} ${altura}`, preserveAspectRatio: "none", role: "img",
    "aria-label": `${plural(porDia.reduce((s, d) => s + d.cliques, 0), "clique", "cliques")} em ${n} dias; o maior dia teve ${maximo}` });
  for (const f of [0.5]) desenho.append(svg("line", { class: "guia", x1: 0, x2: largura, y1: y(maximo * f), y2: y(maximo * f) }));
  const barras = porDia.map((d, i) => {
    // Dia sem clique ainda mostra um traço, para o eixo não parecer vazio
    const topo = d.cliques ? y(d.cliques) : altura - 1.5;
    const barra = svg("rect", { class: "barra" + (i === n - 1 ? " hoje" : ""), x: i * passo + folga / 2, y: topo,
      width: Math.max(passo - folga, 0.8), height: altura - topo, rx: n > 45 ? 0.5 : 2 });
    desenho.append(barra);
    return barra;
  });
  // Uma faixa invisível por dia, da altura toda: fácil de acertar com o mouse e com o dedo
  porDia.forEach((_, i) => desenho.append(svg("rect", { class: "alvo", "data-i": i, x: i * passo, y: 0, width: passo, height: altura })));

  const dica = el("div", { class: "dica", hidden: true });
  const maximoRotulo = el("span", { class: "maximo" }, `máx. ${maximo}`);
  const eixo = el("div", { class: "eixo" }, el("span", {}, data(porDia[0].dia)), el("span", {}, `hoje, ${data(porDia[n - 1].dia)}`));
  container.append(maximoRotulo, desenho, eixo, dica);

  let atual = -1;
  const mostrar = i => {
    barras[atual]?.classList.remove("ativa");
    atual = i;
    if (i < 0) { dica.hidden = true; return; }
    barras[i].classList.add("ativa");
    const d = porDia[i];
    dica.textContent = `${data(d.dia)} · ${plural(d.cliques, "clique", "cliques")}`;
    dica.style.left = `${((i + 0.5) / n) * 100}%`;
    dica.hidden = false;
  };
  // pointerdown: no celular, um toque já mostra o dia (não há "passar o mouse")
  for (const evento of ["pointermove", "pointerdown"]) {
    desenho.addEventListener(evento, e => { if (e.target.dataset.i) mostrar(Number(e.target.dataset.i)); });
  }
  desenho.addEventListener("pointerleave", () => mostrar(-1));
  // Teclado: Tab chega no gráfico, as setas andam pelos dias
  desenho.setAttribute("tabindex", "0");
  desenho.addEventListener("keydown", e => {
    if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
    e.preventDefault();
    const proximo = atual < 0 ? n - 1 : atual + (e.key === "ArrowRight" ? 1 : -1);
    mostrar(Math.min(n - 1, Math.max(0, proximo)));
  });
  desenho.addEventListener("blur", () => mostrar(-1));
}

/** Lista "nome ..... quantidade" com uma barrinha proporcional. contagens: [{ nome, cliques }] */
export function listaDeBarras(lista, contagens, total, limite = 6) {
  lista.replaceChildren();
  if (!contagens.length) {
    lista.append(el("li", { class: "nada" }, "Nenhum clique no período"));
    return;
  }
  const mostradas = contagens.slice(0, limite);
  const resto = contagens.slice(limite).reduce((s, c) => s + c.cliques, 0);
  if (resto) mostradas.push({ nome: "outros", cliques: resto });
  for (const c of mostradas) {
    const preenchido = el("i");
    preenchido.style.width = `${Math.max(2, (c.cliques / total) * 100)}%`;
    lista.append(el("li", { title: c.nome },
      el("span", { class: "nome" }, c.nome),
      el("span", { class: "qtd" }, `${c.cliques} · ${Math.round((c.cliques / total) * 100)}%`),
      el("span", { class: "trilho" }, preenchido)));
  }
}

export { plural };
