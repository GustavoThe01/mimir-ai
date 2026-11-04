# ADR 0001 — Provedor de LLM configurável, com Ollama como padrão

- **Status:** Aceita
- **Data:** 2026-11-04

## Contexto
Não quero gastar nada neste projeto, mas quero que o código esteja pronto para APIs pagas.
Preço, qualidade e disponibilidade dos modelos mudam rápido, e o núcleo não deve depender dessas mudanças.

## Decisão
O núcleo depende apenas de `IChatClient`, do `Microsoft.Extensions.AI`. A `ChatClientFactory` é o único
ponto que conhece os provedores e os escolhe pela configuração. O padrão é o Ollama, com um modelo local.
As chaves só são lidas de variáveis de ambiente.

## Alternativas consideradas
- **Usar direto o SDK de um fornecedor** — mais simples, mas cada troca exigiria mudar o núcleo.
- **Semantic Kernel ou LangChain** — trazem mais recursos, mas escondem justamente o que quero demonstrar:
  RAG, ferramentas e o laço do agente escritos e testados por mim.
- **Criar a minha própria interface** — duplicaria o que `IChatClient` já resolve, e os SDKs já a suportam.

## Consequências
- ✅ Trocar de modelo é uma mudança de configuração, sem custo para quem não configurar uma chave.
- ✅ Testes e CI usam o provedor `Fake`: são determinísticos e nunca chamam uma API paga.
- ⚠️ Recursos exclusivos de um fornecedor ficam fora do caminho comum.
- ⚠️ Os provedores pagos têm testes de configuração, mas não são executados contra a API real.
