// Conversa com a API do Beacon. O cookie da sessão vai sozinho (mesma origem); sem ele, a API
// responde 401 e quem chamou volta para a tela de entrada.

export class SemSessao extends Error {}

/** Erro com a resposta da API (problem details): title, detail e, em 400, errors por campo. */
export class ErroDaApi extends Error {
  constructor(status, problema) {
    super(problema?.detail || problema?.title || `Erro ${status}`);
    this.status = status;
    this.erros = problema?.errors ?? {};
  }
}

export async function pedir(metodo, caminho, corpo) {
  const opcoes = { method: metodo, headers: { Accept: "application/json" } };
  if (corpo !== undefined) {
    opcoes.headers["Content-Type"] = "application/json";
    opcoes.body = JSON.stringify(corpo);
  }
  let resposta;
  try {
    resposta = await fetch(caminho, opcoes);
  } catch {
    throw new ErroDaApi(0, { title: "Sem conexão com o Beacon. Confira a internet e tente de novo." });
  }
  // 401 no login é "senha errada" (mostrada no formulário); em qualquer outro pedido, a sessão acabou
  if (resposta.status === 401 && !(metodo === "POST" && caminho === "/api/sessao")) {
    throw new SemSessao();
  }
  const texto = await resposta.text();
  let dados = null;
  try {
    dados = texto ? JSON.parse(texto) : null;
  } catch {
    // Não é JSON (ex.: a página de erro 502 de um proxy): mensagem em português, e não "Unexpected token <"
    throw new ErroDaApi(resposta.status, { title: `O Beacon respondeu com um erro (${resposta.status}). Tente de novo em instantes.` });
  }
  if (!resposta.ok) {
    if (resposta.status === 429) {
      const espera = resposta.headers.get("Retry-After");
      throw new ErroDaApi(429, { title: `Muitas tentativas. Espere ${espera ?? "alguns"} segundos e tente de novo.` });
    }
    throw new ErroDaApi(resposta.status, dados);
  }
  return dados;
}

export const api = {
  sessao: () => pedir("GET", "/api/sessao"),
  entrar: (usuario, senha) => pedir("POST", "/api/sessao", { usuario, senha }),
  sair: () => pedir("DELETE", "/api/sessao"),
  links: () => pedir("GET", "/api/links"),
  resumo: dias => pedir("GET", `/api/estatisticas?dias=${dias}`),
  estatisticas: (codigo, dias) => pedir("GET", `/api/links/${encodeURIComponent(codigo)}/estatisticas?dias=${dias}`),
  criar: (destino, codigo) => pedir("POST", "/api/links", { destino, codigo: codigo || null }),
  editar: (codigo, destino, ativo) => pedir("PUT", `/api/links/${encodeURIComponent(codigo)}`, { destino, ativo }),
  apagar: codigo => pedir("DELETE", `/api/links/${encodeURIComponent(codigo)}`),
};
